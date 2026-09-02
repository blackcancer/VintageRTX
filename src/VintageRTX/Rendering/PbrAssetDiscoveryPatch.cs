using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Rendering;

/// <summary>
/// Indexes _n/_r/_m/_e immediately after Vintage Story discovers external assets, while atlas
/// boundary guards prevent those still-visible mod assets from entering albedo wildcard variants.
/// </summary>
internal static class PbrAssetDiscoveryPatch
{
    /// <summary>Stable owner identifier used to remove only this mod's Harmony patches.</summary>
    private const string HarmonyId = "vintagertx.pbr-sidecar-discovery";
    private static readonly object Sync = new();
    private static readonly Dictionary<IAssetManager, PbrSidecarAssetStore> Stores =
        new(ReferenceEqualityComparer.Instance);
    private static Harmony? harmony;
    private static ILogger? logger;
    private static int blockVariantsRemoved;
    private static int itemVariantsRemoved;
    private static int entityVariantsRemoved;
    [ThreadStatic]
    private static AtlasTarget activeAtlas;

    /// <summary>
    /// Installs the asset-index, atlas-boundary, and wildcard-bake guards exactly once. Reflection
    /// failures are fatal because allowing sidecars into albedo atlases corrupts unrelated textures.
    /// </summary>
    /// <param name="api">Core API supplying assets, logger, and the loaded Vintage Story assemblies.</param>
    public static void Install(ICoreAPI api)
    {
        lock (Sync)
        {
            logger = api.Logger;
            if (harmony is not null)
            {
                return;
            }

            Type assetManagerType = AccessTools.TypeByName("Vintagestory.Common.AssetManager")
                ?? throw new InvalidOperationException("Vintage Story AssetManager type was not found.");
            MethodInfo target = AccessTools.Method(assetManagerType, "AddExternalAssets")
                ?? throw new MissingMethodException(assetManagerType.FullName, "AddExternalAssets");
            MethodInfo postfix = AccessTools.Method(typeof(PbrAssetDiscoveryPatch), nameof(AfterExternalAssetsDiscovered))
                ?? throw new MissingMethodException(typeof(PbrAssetDiscoveryPatch).FullName, nameof(AfterExternalAssetsDiscovered));

            harmony = new Harmony(HarmonyId);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            int atlasFilterCount = 0;
            foreach ((string typeName, string prefixName, string postfixName) in new[]
            {
                ("Vintagestory.Client.NoObf.BlockTextureAtlasManager", nameof(BeforeBlockAtlasCollection), nameof(AfterBlockAtlasCollection)),
                ("Vintagestory.Client.NoObf.ItemTextureAtlasManager", nameof(BeforeItemAtlasCollection), nameof(AfterItemAtlasCollection)),
                ("Vintagestory.Client.NoObf.EntityTextureAtlasManager", nameof(BeforeEntityAtlasCollection), nameof(AfterEntityAtlasCollection))
            })
            {
                Type atlasType = AccessTools.TypeByName(typeName)
                    ?? throw new InvalidOperationException($"Vintage Story atlas type '{typeName}' was not found.");
                MethodInfo collectTextures = AccessTools.Method(atlasType, "CollectTextures")
                    ?? throw new MissingMethodException(atlasType.FullName, "CollectTextures");
                MethodInfo prefix = AccessTools.Method(typeof(PbrAssetDiscoveryPatch), prefixName)
                    ?? throw new MissingMethodException(typeof(PbrAssetDiscoveryPatch).FullName, prefixName);
                MethodInfo atlasPostfix = AccessTools.Method(typeof(PbrAssetDiscoveryPatch), postfixName)
                    ?? throw new MissingMethodException(typeof(PbrAssetDiscoveryPatch).FullName, postfixName);
                harmony.Patch(
                    collectTextures,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(atlasPostfix));
                atlasFilterCount++;
            }

            MethodInfo bakeTexture = AccessTools.Method(
                typeof(CompositeTexture),
                nameof(CompositeTexture.Bake),
                [typeof(IAssetManager)])
                ?? throw new MissingMethodException(
                    typeof(CompositeTexture).FullName,
                    nameof(CompositeTexture.Bake));
            MethodInfo bakePostfix = AccessTools.Method(
                typeof(PbrAssetDiscoveryPatch),
                nameof(AfterCompositeTextureBaked))
                ?? throw new MissingMethodException(
                    typeof(PbrAssetDiscoveryPatch).FullName,
                    nameof(AfterCompositeTextureBaked));
            harmony.Patch(bakeTexture, postfix: new HarmonyMethod(bakePostfix));
            PatchBakeCallsite(
                "Vintagestory.Client.NoObf.EntityTextureAtlasManager",
                "CollectTextures");
            PatchBakeCallsite(
                "Vintagestory.Client.NoObf.EntityTextureAtlasManager",
                "LoadShapeTextures");
            PatchBakeCallsite(
                "Vintagestory.Client.NoObf.ItemTextureAtlasManager",
                "CollectTextures");
            api.Logger.Notification(
                "[VintageRTX] PBR asset-discovery suffix filter installed: non-destructive index=1, atlas collectors={0}, wildcard bake boundary=1, protected callsites=3.",
                atlasFilterCount);
        }
    }

    /// <summary>Rewrites one known atlas method so every composite bake is sanitized immediately.</summary>
    /// <param name="typeName">Runtime-qualified Vintage Story manager type.</param>
    /// <param name="methodName">Method whose calls to <c>CompositeTexture.Bake</c> are guarded.</param>
    private static void PatchBakeCallsite(string typeName, string methodName)
    {
        Type targetType = AccessTools.TypeByName(typeName)
            ?? throw new InvalidOperationException($"Vintage Story type '{typeName}' was not found.");
        MethodInfo target = AccessTools.Method(targetType, methodName)
            ?? throw new MissingMethodException(targetType.FullName, methodName);
        MethodInfo transpiler = AccessTools.Method(
            typeof(PbrAssetDiscoveryPatch),
            nameof(WrapAtlasBakeCalls))
            ?? throw new MissingMethodException(
                typeof(PbrAssetDiscoveryPatch).FullName,
                nameof(WrapAtlasBakeCalls));
        harmony!.Patch(target, transpiler: new HarmonyMethod(transpiler));
    }

    /// <summary>Transfers the startup capture to the renderer, falling back for unusual reload order.</summary>
    /// <param name="api">Core API whose asset-manager identity selects the capture.</param>
    /// <returns>Exactly one sidecar store for the active asset manager.</returns>
    public static PbrSidecarAssetStore TakeOrCapture(ICoreAPI api)
    {
        lock (Sync)
        {
            if (Stores.Remove(api.Assets, out PbrSidecarAssetStore? store))
            {
                return store;
            }
        }

        // Defensive fallback for unusual reload orders. Normal startup is
        // captured by the AddExternalAssets postfix before AssetsLoaded.
        return PbrSidecarAssetStore.Capture(api);
    }

    /// <summary>Unpatches only VintageRTX hooks and clears reload-sensitive stores/counters.</summary>
    public static void Uninstall()
    {
        lock (Sync)
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            logger = null;
            Stores.Clear();
            blockVariantsRemoved = 0;
            itemVariantsRemoved = 0;
            entityVariantsRemoved = 0;
            activeAtlas = AtlasTarget.None;
        }
    }

    /// <summary>Harmony postfix that indexes sidecars after external asset discovery completes.</summary>
    /// <param name="__instance">Patched AssetManager instance supplied by Harmony.</param>
    private static void AfterExternalAssetsDiscovered(object __instance)
    {
        if (__instance is not IAssetManager assetManager || logger is null)
        {
            return;
        }

        PbrSidecarAssetStore captured = PbrSidecarAssetStore.Capture(assetManager, logger);
        if (captured.Count <= 0)
        {
            return;
        }

        lock (Sync)
        {
            Stores[assetManager] = captured;
        }
    }

    /// <summary>Marks the thread-local atlas target and sanitizes block texture graphs.</summary>
    /// <param name="blocks">Definitions about to enter the block atlas.</param>
    private static void BeforeBlockAtlasCollection(IList<Block> blocks)
    {
        activeAtlas = AtlasTarget.Block;
        Volatile.Write(
            ref blockVariantsRemoved,
            PbrTextureVariantSanitizer.SanitizeBlocks(blocks));
    }

    /// <summary>Marks the thread-local atlas target and sanitizes item texture graphs.</summary>
    /// <param name="items">Definitions about to enter the item atlas.</param>
    private static void BeforeItemAtlasCollection(IList<Item> items)
    {
        activeAtlas = AtlasTarget.Item;
        Volatile.Write(
            ref itemVariantsRemoved,
            PbrTextureVariantSanitizer.SanitizeItems(items));
    }

    /// <summary>Marks the thread-local atlas target and sanitizes entity texture graphs.</summary>
    /// <param name="entityClasses">Definitions about to enter the entity atlas.</param>
    private static void BeforeEntityAtlasCollection(List<EntityProperties> entityClasses)
    {
        activeAtlas = AtlasTarget.Entity;
        Volatile.Write(
            ref entityVariantsRemoved,
            PbrTextureVariantSanitizer.SanitizeEntities(entityClasses));
    }

    /// <summary>Post-bake safety net that removes sidecars produced by wildcard expansion.</summary>
    /// <param name="__instance">Composite that has just resolved its baked variants.</param>
    private static void AfterCompositeTextureBaked(CompositeTexture __instance)
    {
        CountRemovedVariants(PbrTextureVariantSanitizer.SanitizeCompositeTexture(__instance));
    }

    /// <summary>Replaces atlas-local Bake calls while preserving labels and exception blocks.</summary>
    /// <param name="instructions">Original Harmony instruction stream.</param>
    /// <returns>Instruction stream whose Bake operands target <see cref="BakeAlbedoTexture"/>.</returns>
    private static IEnumerable<CodeInstruction> WrapAtlasBakeCalls(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo bake = AccessTools.Method(
            typeof(CompositeTexture),
            nameof(CompositeTexture.Bake),
            [typeof(IAssetManager)])
            ?? throw new MissingMethodException(
                typeof(CompositeTexture).FullName,
                nameof(CompositeTexture.Bake));
        MethodInfo guardedBake = AccessTools.Method(
            typeof(PbrAssetDiscoveryPatch),
            nameof(BakeAlbedoTexture))
            ?? throw new MissingMethodException(
                typeof(PbrAssetDiscoveryPatch).FullName,
                nameof(BakeAlbedoTexture));

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(bake))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = guardedBake;
            }

            yield return instruction;
        }
    }

    /// <summary>Bakes a texture through the original API, then removes newly materialized sidecars.</summary>
    /// <param name="texture">Composite being expanded.</param>
    /// <param name="assetManager">Asset manager required by the original bake contract.</param>
    private static void BakeAlbedoTexture(
        CompositeTexture texture,
        IAssetManager assetManager)
    {
        texture.Bake(assetManager);
        CountRemovedVariants(PbrTextureVariantSanitizer.SanitizeCompositeTexture(texture));
    }

    /// <summary>Attributes removals to the active per-thread atlas collection.</summary>
    /// <param name="removed">Non-negative number removed by one sanitation call.</param>
    private static void CountRemovedVariants(int removed)
    {
        switch (activeAtlas)
        {
            case AtlasTarget.Block:
                Interlocked.Add(ref blockVariantsRemoved, removed);
                break;
            case AtlasTarget.Item:
                Interlocked.Add(ref itemVariantsRemoved, removed);
                break;
            case AtlasTarget.Entity:
                Interlocked.Add(ref entityVariantsRemoved, removed);
                break;
        }
    }

    /// <summary>Logs and resets block-atlas sanitation state after collection.</summary>
    private static void AfterBlockAtlasCollection()
    {
        LogAtlasFilter("blocks", Interlocked.Exchange(ref blockVariantsRemoved, 0));
        activeAtlas = AtlasTarget.None;
    }

    /// <summary>Logs and resets item-atlas sanitation state after collection.</summary>
    private static void AfterItemAtlasCollection()
    {
        LogAtlasFilter("items", Interlocked.Exchange(ref itemVariantsRemoved, 0));
        activeAtlas = AtlasTarget.None;
    }

    /// <summary>Logs and resets entity-atlas sanitation state after collection.</summary>
    private static void AfterEntityAtlasCollection()
    {
        LogAtlasFilter("entities", Interlocked.Exchange(ref entityVariantsRemoved, 0));
        activeAtlas = AtlasTarget.None;
    }

    /// <summary>Emits exact sanitation cardinality for runtime regression diagnostics.</summary>
    /// <param name="target">Human-readable atlas category.</param>
    /// <param name="removed">Number of sidecar variants excluded.</param>
    private static void LogAtlasFilter(string target, int removed)
    {
        logger?.Notification(
            "[VintageRTX] PBR atlas wildcard filter applied: target={0}, removed variants={1}.",
            target,
            removed);
    }

    /// <summary>Thread-local destination used to attribute nested wildcard-bake removals.</summary>
    private enum AtlasTarget
    {
        /// <summary>No atlas collection is active on this thread.</summary>
        None,
        /// <summary>Block world/inventory atlas collection.</summary>
        Block,
        /// <summary>Item atlas collection.</summary>
        Item,
        /// <summary>Entity atlas collection.</summary>
        Entity
    }
}
