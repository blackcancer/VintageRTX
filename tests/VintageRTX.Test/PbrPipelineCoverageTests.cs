using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Test;

/// <summary>Behavioral coverage for the complete file-backed PBR asset pipeline.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PbrPipelineCoverageTests
{
    private static string? deniedTypeName;
    private static Type? deniedMethodType;
    private static string? deniedMethodName;
    private static int deniedScaleCall;
    private static int scaleCallCount;
    private static SKCodecResult? forcedCodecResult;

    /// <summary>
    /// Executes the reset Patch State step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    [TestCleanup]
    public void ResetPatchState()
    {
        deniedTypeName = null;
        deniedMethodType = null;
        deniedMethodName = null;
        deniedScaleCall = 0;
        scaleCallCount = 0;
        forcedCodecResult = null;
        PbrAssetDiscoveryPatch.Uninstall();
    }

    /// <summary>
    /// Verifies the sidecar Suffix Contract Recognizes Only Reserved Texture Pngs regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void SidecarSuffixContractRecognizesOnlyReservedTexturePngs()
    {
        foreach (string suffix in new[] { "n", "r", "m", "e" })
        {
            Assert.IsTrue(PbrSidecarAssetStore.IsPbrSidecar(
                new AssetLocation("game", $"textures/block/stone_{suffix}.png")));
            Assert.IsTrue(PbrSidecarAssetStore.HasPbrSuffix(
                new AssetLocation("game", $"textures/block/stone_{suffix}")));
        }

        Assert.IsFalse(PbrSidecarAssetStore.HasPbrSuffix(null));
        Assert.IsFalse(PbrSidecarAssetStore.HasPbrSuffix(new AssetLocation("game", string.Empty)));
        Assert.IsFalse(PbrSidecarAssetStore.HasPbrSuffix(new AssetLocation("game", "textures/block/stone.png")));
        Assert.IsFalse(PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "shapes/block/stone_n.png")));
        Assert.IsFalse(PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone_n.jpg")));
        Assert.IsFalse(PbrSidecarAssetStore.IsPbrSidecar(new AssetLocation("game", "textures/block/stone_N.png")));
    }

    /// <summary>
    /// Verifies sidecar indexing preserves the shared mod catalog and sorts normals deterministically.
    /// </summary>
    [TestMethod]
    public void SidecarCapturePreservesCatalogAndSortsNormals()
    {
        List<string> log = [];
        ILogger logger = Proxy<ILogger>((method, args) =>
        {
            if (method.Name == nameof(ILogger.Notification)) log.Add((string)args![0]!);
            return Default(method);
        });
        IAsset normalZ = Asset(new AssetLocation("zmod", "textures/b_n.png"), true);
        IAsset normalA = Asset(new AssetLocation("amod", "textures/z_n.png"), true);
        IAsset normalB = Asset(new AssetLocation("amod", "textures/a_n.png"), true);
        Dictionary<AssetLocation, IAsset> catalog = new()
        {
            [normalZ.Location] = normalZ,
            [normalA.Location] = normalA,
            [normalB.Location] = normalB,
            [new AssetLocation("game", "textures/a_r.png")] = Asset(new("game", "textures/a_r.png"), true),
            [new AssetLocation("game", "textures/a_m.png")] = Asset(new("game", "textures/a_m.png"), true),
            [new AssetLocation("game", "textures/a_e.png")] = Asset(new("game", "textures/a_e.png"), true),
            [new AssetLocation("game", "textures/a.png")] = Asset(new("game", "textures/a.png"), true)
        };
        IAssetManager manager = AssetManager(catalog);

        PbrSidecarAssetStore store = PbrSidecarAssetStore.Capture(manager, logger);

        Assert.AreEqual(6, store.Count);
        Assert.AreEqual(7, catalog.Count);
        Assert.IsTrue(catalog.ContainsKey(normalZ.Location));
        Assert.IsTrue(catalog.ContainsKey(new AssetLocation("game", "textures/a.png")));
        CollectionAssert.AreEqual(
            new[] { normalB.Location, normalA.Location, normalZ.Location },
            store.NormalLocations.ToArray());
        Assert.AreEqual(1, log.Count);

        ICoreAPI api = Proxy<ICoreAPI>((method, _) => method.Name switch
        {
            "get_Assets" => manager,
            "get_Logger" => logger,
            _ => Default(method)
        });
        PbrSidecarAssetStore capturedAgain = PbrSidecarAssetStore.Capture(api);
        Assert.AreEqual(6, capturedAgain.Count);
    }

    /// <summary>
    /// Verifies the sidecar Try Get Covers Missing Loaded Lazy Success And Lazy Failure regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void SidecarTryGetCoversMissingLoadedLazySuccessAndLazyFailure()
    {
        AssetLocation loadedLocation = new("game", "textures/loaded_n.png");
        AssetLocation lazyLocation = new("game", "textures/lazy_n.png");
        AssetLocation failedLocation = new("game", "textures/failed_n.png");
        IAsset loaded = Asset(loadedLocation, true);
        IAsset lazy = Asset(lazyLocation, false, true);
        IAsset failed = Asset(failedLocation, false, false);
        PbrSidecarAssetStore store = Capture(new Dictionary<AssetLocation, IAsset>
        {
            [loadedLocation] = loaded,
            [lazyLocation] = lazy,
            [failedLocation] = failed
        });

        Assert.IsFalse(store.TryGet(new AssetLocation("game", "textures/missing_n.png"), out IAsset? missing));
        Assert.IsNull(missing);
        Assert.IsTrue(store.TryGet(loadedLocation, out IAsset? loadedResult));
        Assert.AreSame(loaded, loadedResult);
        Assert.IsTrue(store.TryGet(lazyLocation, out IAsset? lazyResult));
        Assert.AreSame(lazy, lazyResult);
        Assert.IsFalse(store.TryGet(failedLocation, out IAsset? failedResult));
        Assert.IsNull(failedResult);
    }

    /// <summary>
    /// Verifies that two mods retain independently addressable sidecars in the shared catalog while
    /// the renderer receives the same domain-qualified assets through its private index.
    /// </summary>
    [TestMethod]
    public void SidecarIndexCoexistsAcrossModDomainsWithoutHidingAssets()
    {
        AssetLocation firstLocation = new("firstmod", "textures/block/shared_n.png");
        AssetLocation secondLocation = new("secondmod", "textures/block/shared_n.png");
        IAsset first = Asset(firstLocation, true, data: [1]);
        IAsset second = Asset(secondLocation, true, data: [2]);
        Dictionary<AssetLocation, IAsset> catalog = new()
        {
            [firstLocation] = first,
            [secondLocation] = second
        };
        IAssetManager manager = AssetManager(catalog);
        ILogger logger = Proxy<ILogger>((method, _) => Default(method));

        PbrSidecarAssetStore store = PbrSidecarAssetStore.Capture(manager, logger);

        Assert.AreEqual(2, store.Count);
        Assert.AreEqual(2, catalog.Count);
        Assert.AreSame(first, manager.TryGet(firstLocation));
        Assert.AreSame(second, manager.TryGet(secondLocation));
        Assert.IsTrue(store.TryGet(firstLocation, out IAsset? indexedFirst));
        Assert.IsTrue(store.TryGet(secondLocation, out IAsset? indexedSecond));
        Assert.AreSame(first, indexedFirst);
        Assert.AreSame(second, indexedSecond);
    }

    /// <summary>
    /// Verifies the sanitizer Removes Pbr Roots And Traverses All Composite Branches Once regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void SanitizerRemovesPbrRootsAndTraversesAllCompositeBranchesOnce()
    {
        CompositeTexture retained = Composite("game:textures/base.png");
        CompositeTexture sidecarByBase = Composite("game:textures/base_n.png");
        CompositeTexture sidecarByBaked = Composite("game:textures/base.png");
        sidecarByBaked.Baked = Baked("game:textures/base_r.png");
        CompositeTexture alternate = Composite("game:textures/alternate.png");
        CompositeTexture tile = Composite("game:textures/tile.png");
        retained.Alternates = [sidecarByBase, alternate, alternate];
        retained.Tiles = [sidecarByBaked, tile, null!];
        alternate.Alternates = [retained]; // reference cycle
        retained.BlendedOverlays =
        [
            new BlendedOverlayTexture { Base = new AssetLocation("game:textures/overlay.png") },
            new BlendedOverlayTexture { Base = new AssetLocation("game:textures/overlay_e.png") },
            null!
        ];
        BakedCompositeTexture bakedVariant = Baked("game:textures/variant.png");
        BakedCompositeTexture bakedTile = Baked("game:textures/tile.png");
        retained.Baked = Baked("game:textures/base.png");
        retained.Baked.BakedVariants = [Baked("game:textures/variant_m.png"), bakedVariant, bakedVariant];
        retained.Baked.BakedTiles = [Baked("game:textures/tile_e.png"), bakedTile, null!];
        bakedVariant.BakedVariants = [retained.Baked];

        Dictionary<string, CompositeTexture> textures = new()
        {
            ["root"] = retained,
            ["sidecar-base"] = sidecarByBase,
            ["sidecar-baked"] = sidecarByBaked,
            ["null"] = null!
        };
        Block block = new() { Textures = textures, TexturesInventory = null };
        int removed = PbrTextureVariantSanitizer.SanitizeBlocks([null!, block]);

        Assert.AreEqual(7, removed);
        Assert.AreEqual(2, textures.Count);
        Assert.AreEqual(2, retained.Alternates.Length);
        Assert.AreEqual(2, retained.Tiles.Length);
        Assert.AreEqual(2, retained.BlendedOverlays.Length);
        Assert.AreEqual(2, retained.Baked.BakedVariants.Length);
        Assert.AreEqual(2, retained.Baked.BakedTiles.Length);
    }

    /// <summary>
    /// Verifies the sanitizer Handles Items Entities Nulls And Shared Graphs regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void SanitizerHandlesItemsEntitiesNullsAndSharedGraphs()
    {
        CompositeTexture shared = Composite("game:textures/ordinary.png");
        shared.Alternates = [Composite("game:textures/remove_n.png")];
        Item item = new() { Textures = new Dictionary<string, CompositeTexture> { ["a"] = shared, ["b"] = shared } };
        Assert.AreEqual(1, PbrTextureVariantSanitizer.SanitizeItems([null!, item]));
        Assert.AreEqual(0, PbrTextureVariantSanitizer.SanitizeItems([new Item { Textures = null }]));

        EntityProperties serverOnly = new();
        EntityProperties client = new()
        {
            Client = new EntityClientProperties([], [])
            {
                Textures = new Dictionary<string, CompositeTexture>
                {
                    ["pbr"] = Composite("game:textures/entity_e.png"),
                    ["albedo"] = Composite("game:textures/entity.png")
                }
            }
        };
        Assert.AreEqual(1, PbrTextureVariantSanitizer.SanitizeEntities([null!, serverOnly, client]));
        Assert.AreEqual(0, PbrTextureVariantSanitizer.SanitizeCompositeTexture(null));
        Assert.AreEqual(0, PbrTextureVariantSanitizer.SanitizeCompositeTexture(Composite("game:textures/plain.png")));
        CompositeTexture withoutBakedIdentity = Composite("game:textures/without-baked.png");
        withoutBakedIdentity.Baked = null!;
        Assert.AreEqual(0, PbrTextureVariantSanitizer.SanitizeCompositeTexture(withoutBakedIdentity));
        CompositeTexture withoutResolvedFilenames = Composite("game:textures/no-filenames.png");
        withoutResolvedFilenames.Baked = new BakedCompositeTexture
        {
            BakedName = new AssetLocation("game:textures/no-filenames.png"),
            TextureFilenames = null
        };
        Item itemWithoutResolvedFilenames = new()
        {
            Textures = new Dictionary<string, CompositeTexture>
            {
                ["plain"] = withoutResolvedFilenames
            }
        };
        Assert.AreEqual(0, PbrTextureVariantSanitizer.SanitizeItems([itemWithoutResolvedFilenames]));
    }

    /// <summary>
    /// Verifies the sanitizer Promotes First Albedo When Wildcard Root Is Pbr regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void SanitizerPromotesFirstAlbedoWhenWildcardRootIsPbr()
    {
        BakedCompositeTexture albedo = Baked("game:textures/stone.png");
        BakedCompositeTexture pbr = Baked("game:textures/stone_n.png");
        BakedCompositeTexture pbrByFilename = new()
        {
            BakedName = new AssetLocation("game:textures/not-pbr.png"),
            TextureFilenames = [new AssetLocation("game:textures/stone_r.png")]
        };
        BakedCompositeTexture root = Baked("game:textures/root_e.png");
        root.BakedVariants = [pbr, albedo, pbrByFilename];
        CompositeTexture composite = Composite("game:textures/root.png");
        composite.Baked = root;

        Assert.AreEqual(2, PbrTextureVariantSanitizer.SanitizeCompositeTexture(composite));
        Assert.AreSame(albedo, composite.Baked);
        CollectionAssert.AreEqual(new[] { albedo }, albedo.BakedVariants);

        CompositeTexture noPromotion = Composite("game:textures/plain.png");
        noPromotion.Baked = Baked("game:textures/plain.png");
        noPromotion.Baked.BakedVariants = [Baked("game:textures/all_n.png")];
        Assert.AreEqual(1, PbrTextureVariantSanitizer.SanitizeCompositeTexture(noPromotion));
        Assert.AreSame(noPromotion.Baked, noPromotion.Baked);

        CompositeTexture noResolvedNames = Composite("game:textures/plain.png");
        noResolvedNames.Baked = new BakedCompositeTexture
        {
            BakedName = new AssetLocation("game:textures/plain.png"),
            TextureFilenames = null
        };
        Assert.AreEqual(0, PbrTextureVariantSanitizer.SanitizeCompositeTexture(noResolvedNames));
        CompositeTexture nullBakedName = Composite("game:textures/plain.png");
        nullBakedName.Baked = new BakedCompositeTexture
        {
            BakedName = null,
            TextureFilenames = [new AssetLocation("game:textures/plain.png")]
        };
        Assert.AreEqual(0, PbrTextureVariantSanitizer.SanitizeCompositeTexture(nullBakedName));
    }

    /// <summary>
    /// Verifies the asset Discovery Atlas Hooks Attribute Sanitization And Reset State regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AssetDiscoveryAtlasHooksAttributeSanitizationAndResetState()
    {
        List<object?[]> notices = [];
        ILogger logger = Proxy<ILogger>((method, args) =>
        {
            if (method.Name == nameof(ILogger.Notification)) notices.Add(args!);
            return Default(method);
        });
        SetStatic("logger", logger);

        InvokePatch("BeforeBlockAtlasCollection", new List<Block>
        {
            new() { Textures = new Dictionary<string, CompositeTexture> { ["n"] = Composite("game:textures/a_n.png") } }
        });
        InvokePatch("AfterCompositeTextureBaked", CompositeWithBakedPbrVariant());
        InvokePatch("AfterBlockAtlasCollection");

        InvokePatch("BeforeItemAtlasCollection", new List<Item>
        {
            new() { Textures = new Dictionary<string, CompositeTexture> { ["r"] = Composite("game:textures/a_r.png") } }
        });
        InvokePatch("AfterCompositeTextureBaked", CompositeWithBakedPbrVariant());
        InvokePatch("AfterItemAtlasCollection");

        InvokePatch("BeforeEntityAtlasCollection", new List<EntityProperties>
        {
            new()
            {
                Client = new EntityClientProperties([], [])
                {
                    Textures = new Dictionary<string, CompositeTexture> { ["e"] = Composite("game:textures/a_e.png") }
                }
            }
        });
        InvokePatch("AfterCompositeTextureBaked", CompositeWithBakedPbrVariant());
        InvokePatch("AfterEntityAtlasCollection");
        InvokePatch("CountRemovedVariants", 9); // AtlasTarget.None

        Assert.AreEqual(3, notices.Count);
        Assert.IsTrue(notices.All(call => ((string)call[0]!).Contains("wildcard filter", StringComparison.Ordinal)));
        PbrAssetDiscoveryPatch.Uninstall();
        InvokePatch("LogAtlasFilter", "none", 0); // null logger branch
    }

    /// <summary>
    /// Verifies the asset Discovery Capture Stores And Transfers By Manager Identity regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AssetDiscoveryCaptureStoresAndTransfersByManagerIdentity()
    {
        ILogger logger = Proxy<ILogger>((method, _) => Default(method));
        AssetLocation location = new("game", "textures/a_n.png");
        Dictionary<AssetLocation, IAsset> catalog = new() { [location] = Asset(location, true) };
        IAssetManager manager = AssetManager(catalog);
        ICoreAPI api = Core(manager, logger);
        SetStatic("logger", logger);

        InvokePatch("AfterExternalAssetsDiscovered", new object());
        InvokePatch("AfterExternalAssetsDiscovered", manager);
        Assert.AreEqual(1, catalog.Count);
        PbrSidecarAssetStore captured = PbrAssetDiscoveryPatch.TakeOrCapture(api);
        Assert.AreEqual(1, captured.Count);
        PbrSidecarAssetStore fallback = PbrAssetDiscoveryPatch.TakeOrCapture(api);
        Assert.AreEqual(1, fallback.Count);

        IAssetManager emptyManager = AssetManager([]);
        InvokePatch("AfterExternalAssetsDiscovered", emptyManager);
        Assert.AreEqual(0, PbrAssetDiscoveryPatch.TakeOrCapture(Core(emptyManager, logger)).Count);

        PbrAssetDiscoveryPatch.Uninstall();
        InvokePatch("AfterExternalAssetsDiscovered", emptyManager); // valid manager, null logger
    }

    /// <summary>
    /// Verifies the asset Discovery Transpiler Rewrites Only Composite Bake Calls regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AssetDiscoveryTranspilerRewritesOnlyCompositeBakeCalls()
    {
        MethodInfo bake = typeof(CompositeTexture).GetMethod(nameof(CompositeTexture.Bake), [typeof(IAssetManager)])!;
        CodeInstruction untouched = new(OpCodes.Nop);
        CodeInstruction call = new(OpCodes.Callvirt, bake);
        object result = InvokePatch("WrapAtlasBakeCalls", (object)new[] { untouched, call });
        CodeInstruction[] rewritten = ((IEnumerable<CodeInstruction>)result).ToArray();

        Assert.AreSame(untouched, rewritten[0]);
        Assert.AreEqual(OpCodes.Call, rewritten[1].opcode);
        Assert.AreEqual("BakeAlbedoTexture", ((MethodInfo)rewritten[1].operand).Name);
    }

    /// <summary>
    /// Verifies the asset Discovery Guarded Bake Runs Original Bake And Sanitizes Result regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AssetDiscoveryGuardedBakeRunsOriginalBakeAndSanitizesResult()
    {
        CompositeTexture composite = CompositeWithBakedPbrVariant();
        IAssetManager manager = AssetManager([]);
        InvokePatch("BakeAlbedoTexture", composite, manager);
        Assert.AreEqual(0, composite.Baked.BakedVariants.Length);
    }

    /// <summary>
    /// Verifies the asset Discovery Install Is Idempotent And Reflection Failures Are Fatal regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AssetDiscoveryInstallIsIdempotentAndReflectionFailuresAreFatal()
    {
        List<object?[]> notices = [];
        ILogger logger = Proxy<ILogger>((method, args) =>
        {
            if (method.Name == nameof(ILogger.Notification)) notices.Add(args!);
            return Default(method);
        });
        ICoreAPI api = Core(AssetManager([]), logger);
        Harmony faultHarness = InstallAccessToolsFaultHarness();
        try
        {
            AssertInstallThrows<InvalidOperationException>(
                api,
                deniedType: "Vintagestory.Common.AssetManager");
            AssertInstallThrows<MissingMethodException>(
                api,
                deniedMethodTypeValue: typeof(global::Vintagestory.Common.AssetManager),
                deniedMethod: "AddExternalAssets");
            AssertInstallThrows<MissingMethodException>(
                api,
                deniedMethodTypeValue: typeof(PbrAssetDiscoveryPatch),
                deniedMethod: "AfterExternalAssetsDiscovered");
            AssertInstallThrows<InvalidOperationException>(
                api,
                deniedType: "Vintagestory.Client.NoObf.BlockTextureAtlasManager");
            AssertInstallThrows<MissingMethodException>(
                api,
                deniedMethodTypeValue: typeof(global::Vintagestory.Client.NoObf.BlockTextureAtlasManager),
                deniedMethod: "CollectTextures");
            AssertInstallThrows<MissingMethodException>(
                api,
                deniedMethodTypeValue: typeof(PbrAssetDiscoveryPatch),
                deniedMethod: "BeforeBlockAtlasCollection");
            AssertInstallThrows<MissingMethodException>(
                api,
                deniedMethodTypeValue: typeof(PbrAssetDiscoveryPatch),
                deniedMethod: "AfterBlockAtlasCollection");
            AssertInstallThrows<MissingMethodException>(
                api,
                deniedMethodTypeValue: typeof(CompositeTexture),
                deniedMethod: nameof(CompositeTexture.Bake));
            AssertInstallThrows<MissingMethodException>(
                api,
                deniedMethodTypeValue: typeof(PbrAssetDiscoveryPatch),
                deniedMethod: "AfterCompositeTextureBaked");

            AssertPatchBakeCallsiteThrows<InvalidOperationException>(
                deniedType: "Vintagestory.Client.NoObf.EntityTextureAtlasManager");
            AssertPatchBakeCallsiteThrows<MissingMethodException>(
                deniedMethodTypeValue: typeof(global::Vintagestory.Client.NoObf.EntityTextureAtlasManager),
                deniedMethod: "LoadShapeTextures");
            AssertPatchBakeCallsiteThrows<MissingMethodException>(
                deniedMethodTypeValue: typeof(PbrAssetDiscoveryPatch),
                deniedMethod: "WrapAtlasBakeCalls");
            AssertAtlasTranspilerThrows<MissingMethodException>(
                typeof(CompositeTexture),
                nameof(CompositeTexture.Bake));
            AssertAtlasTranspilerThrows<MissingMethodException>(
                typeof(PbrAssetDiscoveryPatch),
                "BakeAlbedoTexture");

            PbrAssetDiscoveryPatch.Install(api);
            object installed = GetStatic("harmony")!;
            PbrAssetDiscoveryPatch.Install(api);
            Assert.AreSame(installed, GetStatic("harmony"));
            Assert.AreEqual(1, notices.Count);
            PbrAssetDiscoveryPatch.Uninstall();
            PbrAssetDiscoveryPatch.Uninstall();
        }
        finally
        {
            deniedTypeName = null;
            deniedMethodType = null;
            deniedMethodName = null;
            PbrAssetDiscoveryPatch.Uninstall();
            faultHarness.UnpatchAll(faultHarness.Id);
        }
    }

    /// <summary>
    /// Verifies the terrain Renderer Properties Reload And Non Opaque Frames Avoid Gpu Work regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainRendererPropertiesReloadAndNonOpaqueFramesAvoidGpuWork()
    {
        ILogger logger = Proxy<ILogger>((method, _) => Default(method));
        IBlockTextureAtlasAPI atlas = Proxy<IBlockTextureAtlasAPI>((method, _) => method.Name switch
        {
            "get_AtlasTextures" => new List<LoadedTexture>(),
            _ => Default(method)
        });
        ICoreClientAPI api = ClientCore(atlas, logger);
        PbrTerrainRenderer renderer = new(api, Capture([]));

        Assert.AreEqual(0.36, renderer.RenderOrder);
        Assert.AreEqual(0, renderer.RenderRange);
        Assert.AreEqual("waiting for terrain atlas", renderer.Status);
        renderer.OnRenderFrame(0.1f, EnumRenderStage.AfterFinalComposition);
        renderer.OnRenderFrame(0.1f, EnumRenderStage.Opaque);
        Assert.IsTrue(renderer.ReloadShader());
        Assert.AreEqual("waiting for terrain atlas", renderer.Status);
        SetField(renderer, "loaded", true);
        Assert.IsTrue(renderer.ReloadShader());
        StringAssert.Contains(renderer.Status, "shader reload pending");
        renderer.Dispose();
    }

    /// <summary>
    /// Verifies the terrain Renderer Frame Failure Is Sticky And Reload Clears It regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainRendererFrameFailureIsStickyAndReloadClearsIt()
    {
        List<object?[]> errors = [];
        ILogger logger = Proxy<ILogger>((method, args) =>
        {
            if (method.Name == nameof(ILogger.Error)) errors.Add(args!);
            return Default(method);
        });
        ICoreClientAPI api = Proxy<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_BlockTextureAtlas" => throw new InvalidOperationException("atlas failure"),
            "get_Logger" => logger,
            _ => Default(method)
        });
        PbrTerrainRenderer renderer = new(api, Capture([]));

        renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
        Assert.AreEqual("faulted: atlas failure", renderer.Status);
        Assert.AreEqual(1, errors.Count);
        renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
        Assert.AreEqual(1, errors.Count);
        Assert.IsTrue(renderer.ReloadShader());
        Assert.AreEqual("waiting for terrain atlas", renderer.Status);
    }

    /// <summary>
    /// Verifies the terrain Sampler Selection Skips Reserved Units And Rejects Insufficient Hardware regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainSamplerSelectionSkipsReservedUnitsAndRejectsInsufficientHardware()
    {
        Assert.AreEqual(31, PbrTerrainRenderer.SelectPbrTextureUnit(32));
        Assert.AreEqual(14, PbrTerrainRenderer.SelectPbrTextureUnit(17));
        Assert.AreEqual(14, PbrTerrainRenderer.SelectPbrTextureUnit(16));
        Assert.AreEqual(7, PbrTerrainRenderer.SelectPbrTextureUnit(8));
        StringAssert.Contains(
            Assert.ThrowsException<InvalidOperationException>(() => PbrTerrainRenderer.SelectPbrTextureUnit(7)).Message,
            "At least eight");
    }

    /// <summary>
    /// Verifies the terrain Asset Reference Parser Covers Every Supported And Rejected Shape regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainAssetReferenceParserCoversEverySupportedAndRejectedShape()
    {
        Assert.IsTrue(PbrTerrainRenderer.TryParseAssetReference(
            " OtherMod:Textures\\Block\\Stone_N.PNG ", "pack", out AssetLocation colon));
        Assert.AreEqual("othermod", colon.Domain);
        Assert.AreEqual("textures/block/stone_n.png", colon.Path);
        Assert.IsTrue(PbrTerrainRenderer.TryParseAssetReference(
            "textures/block/stone_n.png", "pack", out AssetLocation local));
        Assert.AreEqual("pack", local.Domain);
        Assert.IsTrue(PbrTerrainRenderer.TryParseAssetReference(
            "othermod/textures/block/stone_n.png", "pack", out AssetLocation slash));
        Assert.AreEqual("othermod", slash.Domain);
        Assert.IsFalse(PbrTerrainRenderer.TryParseAssetReference("stone.png", "pack", out _));
        Assert.IsFalse(PbrTerrainRenderer.TryParseAssetReference(":textures/a.png", "pack", out _));
        Assert.IsFalse(PbrTerrainRenderer.TryParseAssetReference("game:", "pack", out _));
        Assert.IsFalse(PbrTerrainRenderer.TryParseAssetReference("/textures/a.png", "pack", out _));
    }

    /// <summary>
    /// Verifies the terrain Checksum And Canonicalization Contracts Cover Valid Invalid And Duplicate Inputs regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainChecksumAndCanonicalizationContractsCoverValidInvalidAndDuplicateInputs()
    {
        byte[] bytes = [1, 2, 3];
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.AreEqual(true, InvokeRendererStatic("MatchesOptionalSha256", bytes, string.Empty));
        Assert.AreEqual(true, InvokeRendererStatic("MatchesOptionalSha256", bytes, hash.ToLowerInvariant()));
        Assert.AreEqual(false, InvokeRendererStatic("MatchesOptionalSha256", bytes, new string('0', 64)));
        Assert.AreEqual(true, InvokeRendererStatic("MatchesAnySha256", bytes, new HashSet<string> { hash }));
        Assert.AreEqual(false, InvokeRendererStatic("MatchesAnySha256", bytes, new HashSet<string>()));

        AssetLocation canonical = (AssetLocation)InvokeRendererStatic(
            "CanonicalTextureAsset", new AssetLocation("mod", "Block\\STONE"));
        Assert.AreEqual("mod", canonical.Domain);
        Assert.AreEqual("textures/block/stone.png", canonical.Path);
        AssetLocation unchanged = (AssetLocation)InvokeRendererStatic(
            "CanonicalTextureAsset", new AssetLocation("mod", "textures/block/stone.png"));
        Assert.AreEqual("textures/block/stone.png", unchanged.Path);

        Dictionary<AssetLocation, HashSet<string>> hashes = [];
        InvokeRendererStatic("RegisterFallbackChecksum", "mod", "bad", hash, hashes);
        InvokeRendererStatic("RegisterFallbackChecksum", "mod", "textures/a_n.png", "", hashes);
        Assert.AreEqual(0, hashes.Count);
        InvokeRendererStatic("RegisterFallbackChecksum", "mod", "textures/a_n.png", hash, hashes);
        InvokeRendererStatic("RegisterFallbackChecksum", "mod", "textures/a_n.png", hash.ToLowerInvariant(), hashes);
        Assert.AreEqual(1, hashes.Count);
        Assert.AreEqual(1, hashes.Single().Value.Count);
    }

    /// <summary>
    /// Proves that v3 manifests cannot redirect one albedo to another texture&apos;s maps and that
    /// manifest enumeration is normalized before duplicate claims are evaluated.
    /// </summary>
    [TestMethod]
    public void TerrainManifestIdentityRequiresExactAdjacentSidecarsAndStableOrder()
    {
        PbrManifestTexture exact = ManifestEntry(
            "textures/block/stone.png",
            "mod:textures/block/stone_n.png",
            "normal-hash",
            roughnessAsset: "mod:textures/block/stone_r.png",
            roughnessSha256: "roughness-hash",
            metallicAsset: "mod:textures/block/stone_m.png",
            metallicSha256: "metallic-hash",
            emissiveAsset: "mod:textures/block/stone_e.png",
            emissiveSha256: "emissive-hash");
        Assert.IsTrue(PbrTerrainRenderer.HasExactManifestSidecarReferences(
            3, "pack", exact, out AssetLocation source));
        Assert.AreEqual(new AssetLocation("mod", "textures/block/stone.png"), source);

        PbrManifestTexture redirected = ManifestEntry(
            "textures/block/stone.png",
            "mod:textures/block/anvil_n.png",
            "normal-hash",
            roughnessAsset: "mod:textures/block/stone_r.png",
            roughnessSha256: "roughness-hash",
            metallicAsset: "mod:textures/block/stone_m.png",
            metallicSha256: "metallic-hash",
            emissiveAsset: "mod:textures/block/stone_e.png",
            emissiveSha256: "emissive-hash");
        Assert.IsFalse(PbrTerrainRenderer.HasExactManifestSidecarReferences(3, "pack", redirected, out _));

        PbrManifestTexture wrongDomain = ManifestEntry(
            "textures/block/stone.png",
            "other:textures/block/stone_n.png",
            "normal-hash",
            roughnessAsset: "mod:textures/block/stone_r.png",
            roughnessSha256: "roughness-hash",
            metallicAsset: "mod:textures/block/stone_m.png",
            metallicSha256: "metallic-hash",
            emissiveAsset: "mod:textures/block/stone_e.png",
            emissiveSha256: "emissive-hash");
        Assert.IsFalse(PbrTerrainRenderer.HasExactManifestSidecarReferences(3, "pack", wrongDomain, out _));
        Assert.IsTrue(PbrTerrainRenderer.HasExactManifestSidecarReferences(2, "pack", redirected, out _));

        PbrManifestTexture unsigned = ManifestEntry(
            "textures/block/stone.png",
            "mod:textures/block/stone_n.png",
            roughnessAsset: "mod:textures/block/stone_r.png",
            metallicAsset: "mod:textures/block/stone_m.png",
            emissiveAsset: "mod:textures/block/stone_e.png");
        Assert.IsFalse(PbrTerrainRenderer.HasExactManifestSidecarReferences(3, "pack", unsigned, out _));

        PbrManifestTexture missingSource = ManifestEntry(string.Empty, "mod:textures/block/stone_n.png");
        Assert.IsFalse(PbrTerrainRenderer.HasExactManifestSidecarReferences(3, "pack", missingSource, out _));

        IAsset late = Asset(new AssetLocation("zpack", "config/vintagertx/pbr-manifest.json"), true);
        IAsset earlyPath = Asset(new AssetLocation("apack", "config/z.json"), true);
        IAsset early = Asset(new AssetLocation("apack", "config/a.json"), true);
        IAsset[] ordered = PbrTerrainRenderer.OrderManifestAssets([late, earlyPath, early]);
        CollectionAssert.AreEqual(new[] { early, earlyPath, late }, ordered);

        byte[] albedo = [9, 8, 7, 6];
        string albedoHash = Convert.ToHexString(SHA256.HashData(albedo));
        PbrTerrainRenderer renderer = Renderer(
            Atlas([], Position(0, 0), []),
            AssetManager(new Dictionary<AssetLocation, IAsset>
            {
                [source] = Asset(source, true, data: albedo)
            }));
        Assert.AreEqual(true, InvokeRenderer(renderer, "MatchesManifestSource", 3, source, albedoHash));
        Assert.AreEqual(false, InvokeRenderer(renderer, "MatchesManifestSource", 3, source, string.Empty));
        Assert.AreEqual(false, InvokeRenderer(renderer, "MatchesManifestSource", 3, source, new string('0', 64)));
        Assert.AreEqual(true, InvokeRenderer(renderer, "MatchesManifestSource", 2, source, string.Empty));
        Assert.AreEqual(
            false,
            InvokeRenderer(renderer, "MatchesManifestSource", 3, new AssetLocation("mod", "textures/block/missing.png"), albedoHash));
    }

    /// <summary>
    /// Verifies the terrain Material Packing Covers Optional And Authored Maps regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainMaterialPackingCoversOptionalAndAuthoredMaps()
    {
        byte[] normal = Png(new SKColor(10, 20, 30, 255), 2, 2);
        byte[] roughness = Png(new SKColor(40, 0, 0, 255));
        byte[] metallic = Png(new SKColor(255, 0, 0, 255));
        byte[] emissive = Png(new SKColor(128, 0, 0, 255));

        byte[] neutral = PbrTerrainRenderer.BuildConsolidatedPbrPixels(normal, null, null, null, 1, 1);
        CollectionAssert.AreEqual(new byte[] { 10, 20, 184, 0 }, neutral);
        byte[] authored = PbrTerrainRenderer.BuildConsolidatedPbrPixels(
            normal, roughness, metallic, emissive, 2, 1);
        Assert.AreEqual(8, authored.Length);
        Assert.AreEqual(40, authored[2]);
        Assert.AreEqual(PbrTerrainRenderer.PackMaterialBits(255, 128, true), authored[3]);
        Assert.AreEqual(0, PbrTerrainRenderer.PackMaterialBits(0, 0, false));
        Assert.AreEqual(127, PbrTerrainRenderer.PackMaterialBits(255, 255, true));

        byte[] steepNormal = Png(new SKColor(197, 128, 235, 255));
        byte[] untouchedAuthored = PbrTerrainRenderer.BuildConsolidatedPbrPixels(
            steepNormal, null, null, null, 1, 1);
        CollectionAssert.AreEqual(new byte[] { 197, 128, 184, 0 }, untouchedAuthored);

        byte[] calibratedFallback = PbrTerrainRenderer.BuildConsolidatedPbrPixels(
            steepNormal,
            null,
            null,
            null,
            1,
            1,
            PbrTerrainRenderer.GeneratedFallbackMaximumSlope);
        Assert.AreNotEqual(197, calibratedFallback[0]);
        float calibratedX = ((calibratedFallback[0] / 255f) * 2f) - 1f;
        float calibratedY = ((calibratedFallback[1] / 255f) * 2f) - 1f;
        float calibratedZ = MathF.Sqrt(MathF.Max(
            1f - (calibratedX * calibratedX) - (calibratedY * calibratedY),
            0.000001f));
        float calibratedSlope = MathF.Sqrt(
            (calibratedX * calibratedX) + (calibratedY * calibratedY)) / calibratedZ;
        Assert.IsTrue(
            calibratedSlope <= PbrTerrainRenderer.GeneratedFallbackMaximumSlope + 0.01f,
            $"generated fallback slope remained overdriven ({calibratedSlope:F4})");
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            PbrTerrainRenderer.CalibrateGeneratedNormal(
                new SKColor(197, 128, 235, 255),
                0f));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            PbrTerrainRenderer.CalibrateGeneratedNormal(
                new SKColor(197, 128, 235, 255),
                float.NaN));

        Assert.IsTrue(PbrTerrainRenderer.IsOpaqueGeneratedFallbackSource(
            Png(new SKColor(42, 84, 126, 255), 2, 2)));
        Assert.IsFalse(PbrTerrainRenderer.IsOpaqueGeneratedFallbackSource(
            Png(new SKColor(42, 84, 126, 0), 2, 2)));
        Assert.IsFalse(PbrTerrainRenderer.IsOpaqueGeneratedFallbackSource([1, 2, 3]));

        StringAssert.Contains(
            Assert.ThrowsException<InvalidDataException>(() =>
                PbrTerrainRenderer.BuildConsolidatedPbrPixels([1, 2, 3], null, null, null, 1, 1)).Message,
            "normal");
    }

    /// <summary>
    /// Verifies the terrain Skia Failure Guards Cover Every Resize And Codec Outcome regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainSkiaFailureGuardsCoverEveryResizeAndCodecOutcome()
    {
        MethodInfo scalePixels = typeof(SKBitmap).GetMethod(
            nameof(SKBitmap.ScalePixels),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(SKBitmap), typeof(SKSamplingOptions)])
            ?? throw new MissingMethodException(typeof(SKBitmap).FullName, nameof(SKBitmap.ScalePixels));
        MethodInfo getPixels = typeof(SKCodec).GetMethod(
            nameof(SKCodec.GetPixels),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(SKImageInfo), typeof(IntPtr)])
            ?? throw new MissingMethodException(typeof(SKCodec).FullName, nameof(SKCodec.GetPixels));
        Harmony harmony = new("vintagertx.tests.skia-faults");
        harmony.Patch(
            scalePixels,
            prefix: new HarmonyMethod(typeof(PbrPipelineCoverageTests), nameof(FilterScalePixels)));
        harmony.Patch(
            getPixels,
            prefix: new HarmonyMethod(typeof(PbrPipelineCoverageTests), nameof(FilterCodecPixels)));
        byte[] map = Png(new SKColor(128, 128, 255, 255));
        try
        {
            for (int failedCall = 1; failedCall <= 4; failedCall++)
            {
                deniedScaleCall = failedCall;
                scaleCallCount = 0;
                StringAssert.Contains(
                    Assert.ThrowsException<InvalidDataException>(() =>
                        PbrTerrainRenderer.BuildConsolidatedPbrPixels(map, map, map, map, 1, 1)).Message,
                    "resized");
            }

            deniedScaleCall = 0;
            forcedCodecResult = SKCodecResult.IncompleteInput;
            using SKBitmap incomplete = (SKBitmap)InvokeRendererStatic("DecodeUnpremultiplied", map, "normal");
            Assert.AreEqual(1, incomplete.Width);

            forcedCodecResult = SKCodecResult.ErrorInInput;
            TargetInvocationException wrapper = Assert.ThrowsException<TargetInvocationException>(() =>
                InvokeRendererStatic("DecodeUnpremultiplied", map, "roughness"));
            Assert.IsInstanceOfType<InvalidDataException>(wrapper.InnerException);
            StringAssert.Contains(wrapper.InnerException!.Message, "ErrorInInput");
        }
        finally
        {
            deniedScaleCall = 0;
            scaleCallCount = 0;
            forcedCodecResult = null;
            harmony.UnpatchAll(harmony.Id);
        }
    }

    /// <summary>
    /// Verifies the terrain Lookup Traverses Fallback Variants Tiles Cycles Duplicates And Ambiguity regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainLookupTraversesFallbackVariantsTilesCyclesDuplicatesAndAmbiguity()
    {
        TextureAtlasPosition[] positions = Enumerable.Range(0, 8)
            .Select(index => new TextureAtlasPosition
            {
                atlasTextureId = 77,
                atlasNumber = 0,
                x1 = index / 8f,
                y1 = 0,
                x2 = (index + 1) / 8f,
                y2 = 1
            })
            .ToArray();
        AssetLocation fallback = new("game", "Block\\Fallback");
        AssetLocation variant = new("mod", "textures/block/variant.png");
        BakedCompositeTexture rootBaked = new() { TextureSubId = 0, TextureFilenames = null };
        BakedCompositeTexture variantBaked = new() { TextureSubId = 1, TextureFilenames = [variant] };
        BakedCompositeTexture tileBaked = new() { TextureSubId = 2, TextureFilenames = [] };
        rootBaked.BakedVariants = [variantBaked, null!];
        rootBaked.BakedTiles = [tileBaked, variantBaked, null!];
        variantBaked.BakedVariants = [rootBaked];
        CompositeTexture root = new(fallback) { Baked = rootBaked };
        CompositeTexture alternate = new(new AssetLocation("game:textures/block/alternate.png"))
        {
            Baked = new BakedCompositeTexture { TextureSubId = 3, TextureFilenames = [new("game:textures/block/alternate.png")] }
        };
        CompositeTexture tile = new(new AssetLocation("game:textures/block/tile.png"))
        {
            Baked = new BakedCompositeTexture { TextureSubId = 4, TextureFilenames = [new("game:textures/block/tile.png")] }
        };
        root.Alternates = [alternate, root, null!];
        root.Tiles = [tile, alternate, null!];
        CompositeTexture overlay = new(new AssetLocation("game:textures/block/overlay-base.png"))
        {
            Baked = new BakedCompositeTexture
            {
                TextureSubId = 5,
                TextureFilenames = [new("game:textures/block/a.png"), new("game:textures/block/b.png")]
            }
        };
        CompositeTexture collision = new(new AssetLocation("other:textures/block/collision.png"))
        {
            Baked = new BakedCompositeTexture { TextureSubId = 3, TextureFilenames = [new("other:textures/block/collision.png")] }
        };

        PbrAtlasLookup lookup = PbrTerrainRenderer.BuildSourceAtlasPositionLookupForTextures(
            [null!, Composite("game:textures/block/unbaked.png"), root, overlay, collision],
            positions,
            position => position.atlasTextureId != 77 || position.x1 != 0.5f);

        Assert.IsTrue(lookup.Positions.ContainsKey(new AssetLocation("game", "textures/block/fallback.png")));
        Assert.IsTrue(lookup.Positions.ContainsKey(variant));
        Assert.IsTrue(lookup.AmbiguousSources.Count >= 2);
        Assert.AreEqual(2, lookup.SkippedCompositeRectangleCount);
        Assert.IsTrue(lookup.SkippedSourceLinkCount >= 2);
        Assert.AreEqual(AtlasPositionKey.From(positions[0]), AtlasPositionKey.From(positions[0]));
        AtlasPositionKey explicitKey = new(77, 0, 0, 0, 0.125f, 1);
        Assert.AreEqual(77, explicitKey.AtlasTextureId);
        Assert.AreEqual((byte)0, explicitKey.AtlasNumber);
        Assert.AreEqual(0, explicitKey.X1);
        Assert.AreEqual(0, explicitKey.Y1);
        Assert.AreEqual(0.125f, explicitKey.X2);
        Assert.AreEqual(1, explicitKey.Y2);
    }

    /// <summary>
    /// Verifies the terrain Source Deduplication Checks Every Rectangle Coordinate regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainSourceDeduplicationChecksEveryRectangleCoordinate()
    {
        AssetLocation source = new("mod", "textures/block/source.png");
        TextureAtlasPosition baseline = new()
        {
            atlasTextureId = 77,
            atlasNumber = 0,
            x1 = 0.1f,
            y1 = 0.2f,
            x2 = 0.3f,
            y2 = 0.4f
        };
        Dictionary<AssetLocation, List<TextureAtlasPosition>> nullLookup = [];
        InvokeRendererStatic("AddSource", null, baseline, nullLookup);
        Assert.AreEqual(0, nullLookup.Count);

        TextureAtlasPosition[] candidates =
        [
            PositionLike(baseline, atlasNumber: 1),
            PositionLike(baseline, x1: 0.11f),
            PositionLike(baseline, y1: 0.21f),
            PositionLike(baseline, x2: 0.31f),
            PositionLike(baseline, y2: 0.41f),
            PositionLike(baseline)
        ];
        for (int index = 0; index < candidates.Length; index++)
        {
            Dictionary<AssetLocation, List<TextureAtlasPosition>> lookup = new()
            {
                [source] = [baseline]
            };
            InvokeRendererStatic("AddSource", source, candidates[index], lookup);
            Assert.AreEqual(index == candidates.Length - 1 ? 1 : 2, lookup[source].Count);
        }
    }

    /// <summary>
    /// Verifies the terrain Production Lookup Traverses Null And Textured World Blocks regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainProductionLookupTraversesNullAndTexturedWorldBlocks()
    {
        TextureAtlasPosition position = Position(77, 0);
        AssetLocation source = new("game", "textures/block/stone.png");
        CompositeTexture composite = new(source)
        {
            Baked = new BakedCompositeTexture
            {
                TextureSubId = 0,
                TextureFilenames = [source]
            }
        };
        List<Block> blocks =
        [
            null!,
            new Block { Textures = null },
            new Block
            {
                Textures = new Dictionary<string, CompositeTexture> { ["all"] = composite }
            }
        ];
        PbrTerrainRenderer renderer = Renderer(
            Atlas([], Position(999, 0), [position]),
            AssetManager([]),
            blocks: blocks);
        SetField(renderer, "sourceAtlasTexture", 77);

        PbrAtlasLookup lookup = (PbrAtlasLookup)InvokeRenderer(renderer, "BuildSourceAtlasPositionLookup");
        Assert.AreEqual(1, lookup.ExactPlacementCount);
        CollectionAssert.AreEqual(new[] { position }, lookup.Positions[source]);
    }

    /// <summary>
    /// Verifies the terrain Atlas Resolution Uses Exact Mapping Rejects Ambiguity And Falls Back To Direct Index regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainAtlasResolutionUsesExactMappingRejectsAmbiguityAndFallsBackToDirectIndex()
    {
        TextureAtlasPosition unknown = Position(999, 0);
        TextureAtlasPosition direct = Position(77, 0);
        AssetLocation requested = new("game", "textures/block/stone.png");
        IBlockTextureAtlasAPI atlas = Atlas([], unknown, [], location =>
            location.Path == "block/stone" ? direct : unknown);
        PbrTerrainRenderer renderer = Renderer(atlas, AssetManager([]));
        SetField(renderer, "sourceAtlasTexture", 77);

        object?[] invalidArgs = [new AssetLocation("game", "block/stone.png"), null];
        Assert.AreEqual(false, InvokeRenderer(renderer, "TryResolveAtlasPositions", invalidArgs));
        object?[] directArgs = [requested, null];
        Assert.AreEqual(true, InvokeRenderer(renderer, "TryResolveAtlasPositions", directArgs));
        CollectionAssert.AreEqual(new[] { direct }, (TextureAtlasPosition[])directArgs[1]!);

        Dictionary<AssetLocation, TextureAtlasPosition[]> mapped = new() { [requested] = [Position(77, 0)] };
        SetField(renderer, "sourceAtlasPositions", mapped);
        object?[] mappedArgs = [requested, null];
        Assert.AreEqual(true, InvokeRenderer(renderer, "TryResolveAtlasPositions", mappedArgs));
        SetField(renderer, "sourceAtlasPositions", new Dictionary<AssetLocation, TextureAtlasPosition[]>
        {
            [requested] = []
        });
        SetField(renderer, "ambiguousAtlasSources", new HashSet<AssetLocation> { requested });
        object?[] ambiguousArgs = [requested, null];
        Assert.AreEqual(false, InvokeRenderer(renderer, "TryResolveAtlasPositions", ambiguousArgs));

        SetField(renderer, "ambiguousAtlasSources", new HashSet<AssetLocation>());
        SetField(renderer, "sourceAtlasTexture", 88);
        object?[] wrongTextureArgs = [requested, null];
        Assert.AreEqual(false, InvokeRenderer(renderer, "TryResolveAtlasPositions", wrongTextureArgs));
        SetField(renderer, "sourceAtlasTexture", 77);
        direct.atlasNumber = 1;
        object?[] wrongAtlasArgs = [requested, null];
        Assert.AreEqual(false, InvokeRenderer(renderer, "TryResolveAtlasPositions", wrongAtlasArgs));
        direct.atlasNumber = 0;
        Assert.AreEqual(false, InvokeRenderer(renderer, "IsSourceAtlasPosition", (object?)null));
        Assert.AreEqual(false, InvokeRenderer(renderer, "IsSourceAtlasPosition", unknown));
        Assert.AreEqual(true, InvokeRenderer(renderer, "IsSourceAtlasPosition", direct));
    }

    /// <summary>
    /// Verifies the conservative page contract: a proven page zero is accepted, page one is never
    /// allowed to sample its material data, and reload iterations change the deterministic revision.
    /// </summary>
    [TestMethod]
    public void TerrainAtlasSafetyRejectsForeignPagesAndTracksReloadIteration()
    {
        ICoreClientAPI textureApi = Proxy<ICoreClientAPI>((method, _) => Default(method));
        LoadedTexture pageZero = new(textureApi)
        {
            TextureId = 77,
            Width = 1024,
            Height = 1024,
            IgnoreUndisposed = true
        };
        TextureAtlasPosition unknown = Position(999, 0);
        TextureAtlasPosition position = Position(77, 0);
        position.reloadIteration = 4;

        PbrAtlasSafetyResult proven = PbrTerrainRenderer.InspectAtlasSafety(
            [pageZero],
            [position],
            unknown);
        Assert.AreEqual(PbrAtlasSafetyKind.ProvenSinglePage, proven.Kind);
        Assert.IsTrue(proven.IsSinglePageProven);
        Assert.IsFalse(proven.IsUnsafe);

        position.reloadIteration = 5;
        PbrAtlasSafetyResult reloaded = PbrTerrainRenderer.InspectAtlasSafety(
            [pageZero],
            [position],
            unknown);
        Assert.AreEqual(PbrAtlasSafetyKind.ProvenSinglePage, reloaded.Kind);
        Assert.AreNotEqual(proven.Revision, reloaded.Revision);

        LoadedTexture pageOne = new(textureApi)
        {
            TextureId = 88,
            Width = 1024,
            Height = 1024,
            IgnoreUndisposed = true
        };
        PbrAtlasSafetyResult multiple = PbrTerrainRenderer.InspectAtlasSafety(
            [pageZero, pageOne],
            [position, Position(88, 1)],
            unknown);
        Assert.AreEqual(PbrAtlasSafetyKind.UnsafeMultiplePages, multiple.Kind);
        Assert.IsFalse(multiple.IsSinglePageProven);
        Assert.IsTrue(multiple.IsUnsafe);
        StringAssert.Contains(multiple.Reason, "2 pages");
        PbrTerrainRenderer unsafeRenderer = Renderer(
            Atlas([pageZero, pageOne], unknown, [position, Position(88, 1)]),
            AssetManager([]));
        unsafeRenderer.OnRenderFrame(0, EnumRenderStage.Opaque);
        StringAssert.Contains(unsafeRenderer.Status, "PBR disabled");

        TextureAtlasPosition foreign = Position(88, 1);
        PbrAtlasSafetyResult foreignPosition = PbrTerrainRenderer.InspectAtlasSafety(
            [pageZero],
            [position, foreign],
            unknown);
        Assert.AreEqual(PbrAtlasSafetyKind.UnsafeForeignPosition, foreignPosition.Kind);
        Assert.IsTrue(foreignPosition.IsUnsafe);

        PbrAtlasSafetyResult waiting = PbrTerrainRenderer.InspectAtlasSafety(
            [],
            [null, unknown],
            unknown);
        Assert.AreEqual(PbrAtlasSafetyKind.Waiting, waiting.Kind);
        Assert.IsFalse(waiting.IsUnsafe);
    }

    /// <summary>
    /// Verifies the terrain Asset Resolution Prefers Isolated Then Catalog And Rejects Malformed References regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainAssetResolutionPrefersIsolatedThenCatalogAndRejectsMalformedReferences()
    {
        AssetLocation isolatedLocation = new("mod", "textures/i_n.png");
        AssetLocation catalogLocation = new("mod", "textures/c_n.png");
        IAsset isolated = Asset(isolatedLocation, true, data: [1]);
        IAsset catalogAsset = Asset(catalogLocation, true, data: [2]);
        PbrSidecarAssetStore store = Capture(new Dictionary<AssetLocation, IAsset> { [isolatedLocation] = isolated });
        Dictionary<AssetLocation, IAsset> catalog = new() { [catalogLocation] = catalogAsset };
        PbrTerrainRenderer renderer = Renderer(Atlas([], Position(0, 0), []), AssetManager(catalog), store);

        object?[] malformed = ["mod", "bad", null];
        Assert.AreEqual(false, InvokeRenderer(renderer, "TryResolvePbrAsset", malformed));
        Assert.IsNull(malformed[2]);
        object?[] isolatedArgs = ["mod", "textures/i_n.png", null];
        Assert.AreEqual(true, InvokeRenderer(renderer, "TryResolvePbrAsset", isolatedArgs));
        Assert.AreSame(isolated, isolatedArgs[2]);
        object?[] catalogArgs = ["mod", "textures/c_n.png", null];
        Assert.AreEqual(true, InvokeRenderer(renderer, "TryResolvePbrAsset", catalogArgs));
        Assert.AreSame(catalogAsset, catalogArgs[2]);
        object?[] absent = ["mod", "textures/x_n.png", null];
        Assert.AreEqual(false, InvokeRenderer(renderer, "TryResolvePbrAsset", absent));
    }

    /// <summary>
    /// Verifies the terrain Preferred Sidecars Distinguish Generated And Newest Authored Origins regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainPreferredSidecarsDistinguishGeneratedAndNewestAuthoredOrigins()
    {
        AssetLocation location = new("mod", "textures/block/stone_n.png");
        byte[] generatedBytes = [1, 2, 3];
        byte[] authoredBytes = [4, 5, 6];
        string generatedHash = Convert.ToHexString(SHA256.HashData(generatedBytes));
        IAsset generated = Asset(location, true, data: generatedBytes);
        IAsset authored = Asset(location, true, data: authoredBytes);
        IAsset wrongLocation = Asset(new AssetLocation("mod", "textures/block/other_n.png"), true, data: authoredBytes);
        IAssetOrigin newestOrigin = Proxy<IAssetOrigin>((method, _) => method.Name switch
        {
            nameof(IAssetOrigin.GetAssets) => new List<IAsset> { authored, generated, wrongLocation },
            _ => Default(method)
        });
        PbrSidecarAssetStore generatedStore = Capture(new Dictionary<AssetLocation, IAsset>
        {
            [location] = generated
        });
        PbrTerrainRenderer renderer = Renderer(
            Atlas([], Position(0, 0), []),
            AssetManager([], origins: [newestOrigin]),
            generatedStore);
        Dictionary<AssetLocation, HashSet<string>> hashes = new()
        {
            [location] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { generatedHash }
        };

        object?[] authoredArgs = [location, hashes, null];
        Assert.AreSame(authored, InvokeRendererNullable(renderer, "ResolvePreferredSidecar", authoredArgs));
        Assert.AreEqual(true, authoredArgs[2]);

        IAssetOrigin generatedOrigin = Proxy<IAssetOrigin>((method, _) => method.Name switch
        {
            nameof(IAssetOrigin.GetAssets) => new List<IAsset> { generated, wrongLocation },
            _ => Default(method)
        });
        PbrTerrainRenderer generatedOnly = Renderer(
            Atlas([], Position(0, 0), []),
            AssetManager([], origins: [generatedOrigin]),
            generatedStore);
        object?[] generatedArgs = [location, hashes, null];
        Assert.AreSame(generated, InvokeRendererNullable(generatedOnly, "ResolvePreferredSidecar", generatedArgs));
        Assert.AreEqual(false, generatedArgs[2]);

        AssetLocation authoredLocation = new("mod", "textures/block/authored_n.png");
        IAsset isolatedAuthored = Asset(authoredLocation, true, data: authoredBytes);
        PbrTerrainRenderer isolated = Renderer(
            Atlas([], Position(0, 0), []),
            AssetManager([]),
            Capture(new Dictionary<AssetLocation, IAsset> { [authoredLocation] = isolatedAuthored }));
        object?[] isolatedArgs = [authoredLocation, new Dictionary<AssetLocation, HashSet<string>>
        {
            [authoredLocation] = new(StringComparer.OrdinalIgnoreCase) { generatedHash }
        }, null];
        Assert.AreSame(isolatedAuthored, InvokeRendererNullable(isolated, "ResolvePreferredSidecar", isolatedArgs));
        Assert.AreEqual(true, isolatedArgs[2]);

        AssetLocation missing = new("mod", "textures/block/missing_n.png");
        object?[] missingArgs = [missing, new Dictionary<AssetLocation, HashSet<string>>(), null];
        Assert.IsNull(InvokeRendererNullable(renderer, "ResolvePreferredSidecar", missingArgs));
        Assert.AreEqual(false, missingArgs[2]);
    }

    /// <summary>
    /// Verifies the terrain Manifest Validation Rejects Every Cpu Only Schema And Checksum Failure regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainManifestValidationRejectsEveryCpuOnlySchemaAndChecksumFailure()
    {
        List<object?[]> warnings = [];
        ILogger logger = Proxy<ILogger>((method, args) =>
        {
            if (method.Name == nameof(ILogger.Warning)) warnings.Add(args!);
            return Default(method);
        });
        byte[] normalBytes = Png(new SKColor(128, 128, 255, 255));
        byte[] scalarBytes = Png(new SKColor(90, 0, 0, 255));
        string normalHash = Convert.ToHexString(SHA256.HashData(normalBytes));
        string scalarHash = Convert.ToHexString(SHA256.HashData(scalarBytes));
        string mismatch = new('0', 64);
        Dictionary<AssetLocation, IAsset> maps = new()
        {
            [new("mod", "textures/maps/normal.png")] = Asset(new("mod", "textures/maps/normal.png"), true, data: normalBytes),
            [new("mod", "textures/maps/rough.png")] = Asset(new("mod", "textures/maps/rough.png"), true, data: scalarBytes),
            [new("mod", "textures/maps/metal.png")] = Asset(new("mod", "textures/maps/metal.png"), true, data: scalarBytes),
            [new("mod", "textures/maps/emit.png")] = Asset(new("mod", "textures/maps/emit.png"), true, data: scalarBytes),
            [new("mod", "textures/block/missing-normal.png")] = Asset(new("mod", "textures/block/missing-normal.png"), true),
            [new("mod", "textures/block/normal-mismatch.png")] = Asset(new("mod", "textures/block/normal-mismatch.png"), true),
            [new("mod", "textures/block/rough-mismatch.png")] = Asset(new("mod", "textures/block/rough-mismatch.png"), true),
            [new("mod", "textures/block/metal-mismatch.png")] = Asset(new("mod", "textures/block/metal-mismatch.png"), true),
            [new("mod", "textures/block/emit-mismatch.png")] = Asset(new("mod", "textures/block/emit-mismatch.png"), true)
        };
        PbrManifestTexture schemaEntry = ManifestEntry("textures/block/schema.png", "textures/maps/missing.png");
        List<IAsset> manifests =
        [
            ManifestAsset("null"u8.ToArray()),
            ManifestAsset(new PbrManifest { Schema = "vintagertx.pbr-manifest", SchemaVersion = 3 }),
            ManifestAsset(new PbrManifest { Schema = "wrong", SchemaVersion = 3, Textures = [schemaEntry] }),
            ManifestAsset(new PbrManifest { Schema = "vintagertx.pbr-manifest", SchemaVersion = 0, Textures = [schemaEntry] }),
            ManifestAsset(new PbrManifest { Schema = "vintagertx.pbr-manifest", SchemaVersion = 4, Textures = [schemaEntry] })
        ];
        PbrManifest valid = new()
        {
            Schema = "vintagertx.pbr-manifest",
            SchemaVersion = 2,
            Textures =
            [
                ManifestEntry("textures/block/missing-normal.png", "textures/maps/absent.png"),
                ManifestEntry(
                    "textures/block/normal-mismatch.png",
                    "textures/maps/normal.png",
                    mismatch,
                    roughnessAsset: "textures/maps/absent-rough.png"),
                ManifestEntry(
                    "textures/block/rough-mismatch.png",
                    "textures/maps/normal.png",
                    normalHash,
                    "textures/maps/rough.png",
                    mismatch),
                ManifestEntry(
                    "textures/block/metal-mismatch.png",
                    "textures/maps/normal.png",
                    normalHash,
                    "textures/maps/rough.png",
                    scalarHash,
                    "textures/maps/metal.png",
                    mismatch),
                ManifestEntry(
                    "textures/block/emit-mismatch.png",
                    "textures/maps/normal.png",
                    normalHash,
                    "textures/maps/rough.png",
                    scalarHash,
                    "textures/maps/metal.png",
                    scalarHash,
                    "textures/maps/emit.png",
                    mismatch)
            ]
        };
        manifests.Add(ManifestAsset(valid));
        PbrTerrainRenderer renderer = Renderer(Atlas([], Position(0, 0), []), AssetManager(maps, manifests), logger: logger);
        Dictionary<AssetLocation, TextureAtlasPosition[]> positions = valid.Textures.ToDictionary(
            entry => new AssetLocation(entry.Source.Domain, entry.Source.Path),
            _ => new[] { Position(77, 0) });
        SetField(renderer, "sourceAtlasPositions", positions);
        SetField(renderer, "ambiguousAtlasSources", new HashSet<AssetLocation>());

        HashSet<AssetLocation> applied = [];
        Dictionary<AssetLocation, HashSet<string>> fallbackHashes = [];
        HashSet<AssetLocation> rejected = [];
        Assert.AreEqual(0, InvokeRenderer(renderer, "ApplyManifestOverrides", applied, fallbackHashes, rejected));
        Assert.AreEqual(0, applied.Count);
        Assert.AreEqual(5, rejected.Count);
        Assert.AreEqual(4, warnings.Count);
        Assert.IsTrue(fallbackHashes.Count > 0);
    }

    /// <summary>
    /// Verifies the terrain Sidecar Scan Rejects Lazy Failure And Skips Applied Generated Fallback regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainSidecarScanRejectsLazyFailureAndSkipsAppliedGeneratedFallback()
    {
        AssetLocation failedLocation = new("mod", "textures/block/failed_n.png");
        PbrTerrainRenderer failed = Renderer(
            Atlas([], Position(0, 0), []),
            AssetManager([]),
            Capture(new Dictionary<AssetLocation, IAsset>
            {
                [failedLocation] = Asset(failedLocation, loaded: false, loadResult: false)
            }));
        Assert.AreEqual(0, InvokeRenderer(
            failed,
            "ApplySidecarOverrides",
            new HashSet<AssetLocation>(),
            new Dictionary<AssetLocation, HashSet<string>>(),
            new HashSet<AssetLocation>()));

        AssetLocation generatedNormal = new("mod", "textures/block/generated_n.png");
        AssetLocation generatedSource = new("mod", "textures/block/generated.png");
        byte[] generatedBytes = Png(new SKColor(128, 128, 255, 255));
        string generatedHash = Convert.ToHexString(SHA256.HashData(generatedBytes));
        PbrTerrainRenderer generated = Renderer(
            Atlas([], Position(0, 0), []),
            AssetManager([]),
            Capture(new Dictionary<AssetLocation, IAsset>
            {
                [generatedNormal] = Asset(generatedNormal, true, data: generatedBytes)
            }));
        SetField(generated, "sourceAtlasPositions", new Dictionary<AssetLocation, TextureAtlasPosition[]>
        {
            [generatedSource] = [Position(77, 0)]
        });
        SetField(generated, "ambiguousAtlasSources", new HashSet<AssetLocation>());
        Assert.AreEqual(0, InvokeRenderer(
            generated,
            "ApplySidecarOverrides",
            new HashSet<AssetLocation> { generatedSource },
            new Dictionary<AssetLocation, HashSet<string>>
            {
                [generatedNormal] = new(StringComparer.OrdinalIgnoreCase) { generatedHash }
            },
            new HashSet<AssetLocation>()));
        Assert.AreEqual(0, InvokeRenderer(
            generated,
            "ApplySidecarOverrides",
            new HashSet<AssetLocation>(),
            new Dictionary<AssetLocation, HashSet<string>>
            {
                [generatedNormal] = new(StringComparer.OrdinalIgnoreCase) { generatedHash }
            },
            new HashSet<AssetLocation> { generatedSource }));
    }

    /// <summary>
    /// Verifies the terrain Sidecar And Manifest Scans Cover No Upload Validation Paths regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainSidecarAndManifestScansCoverNoUploadValidationPaths()
    {
        List<object?[]> notices = [];
        List<object?[]> warnings = [];
        ILogger logger = Proxy<ILogger>((method, args) =>
        {
            if (method.Name == nameof(ILogger.Notification)) notices.Add(args!);
            if (method.Name == nameof(ILogger.Warning)) warnings.Add(args!);
            return Default(method);
        });
        AssetLocation wrongNormal = new("mod", "textures/a_r.png");
        AssetLocation validNormal = new("mod", "textures/b_n.png");
        PbrSidecarAssetStore store = Capture(new Dictionary<AssetLocation, IAsset>
        {
            [wrongNormal] = Asset(wrongNormal, true),
            [validNormal] = Asset(validNormal, true)
        });
        AssetLocation malformedManifestLocation = new("mod", "config/vintagertx/pbr-manifest.json");
        IAsset malformed = Asset(malformedManifestLocation, true, data: "{"u8.ToArray());
        IAsset invalid = Asset(malformedManifestLocation, true, data: "{}"u8.ToArray());
        byte[] noAtlasJson = """
            {"schema":"vintagertx.pbr-manifest","schemaVersion":3,"textures":[{"source":{"domain":"mod","path":"textures/missing.png"},"normal":{"asset":"textures/missing_n.png"},"roughness":{},"metallic":{},"emissive":{}}]}
            """u8.ToArray();
        IAsset noAtlas = Asset(malformedManifestLocation, true, data: noAtlasJson);
        IAssetManager manager = AssetManager([], manifests: [malformed, invalid, noAtlas]);
        PbrTerrainRenderer renderer = Renderer(Atlas([], Position(0, 0), []), manager, store, logger);
        SetField(renderer, "sourceAtlasPositions", new Dictionary<AssetLocation, TextureAtlasPosition[]>());
        SetField(renderer, "ambiguousAtlasSources", new HashSet<AssetLocation>());

        Assert.AreEqual(0, InvokeRenderer(
            renderer,
            "ApplySidecarOverrides",
            new HashSet<AssetLocation>(),
            new Dictionary<AssetLocation, HashSet<string>>(),
            new HashSet<AssetLocation>()));
        Assert.AreEqual(0, InvokeRenderer(
            renderer,
            "ApplyManifestOverrides",
            new HashSet<AssetLocation>(),
            new Dictionary<AssetLocation, HashSet<string>>(),
            new HashSet<AssetLocation>()));
        Assert.AreEqual(2, warnings.Count);
        Assert.AreEqual(1, notices.Count);
    }

    /// <summary>
    /// Verifies the terrain Invalid Source Atlases And Missing Shaders Return Without Gpu Mutation regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainInvalidSourceAtlasesAndMissingShadersReturnWithoutGpuMutation()
    {
        ILogger logger = Proxy<ILogger>((method, _) => Default(method));
        LoadedTexture disposedTexture = new(Proxy<ICoreClientAPI>((m, _) => Default(m)))
        {
            TextureId = 1,
            Width = 1,
            Height = 1
        };
        SetLoadedTextureDisposed(disposedTexture);
        LoadedTexture[] invalidTextures =
        {
            disposedTexture,
            new LoadedTexture(Proxy<ICoreClientAPI>((m, _) => Default(m))) { TextureId = 0, Width = 1, Height = 1 },
            new LoadedTexture(Proxy<ICoreClientAPI>((m, _) => Default(m))) { TextureId = 1, Width = 0, Height = 1 },
            new LoadedTexture(Proxy<ICoreClientAPI>((m, _) => Default(m))) { TextureId = 1, Width = 1, Height = 0 }
        };
        foreach (LoadedTexture invalid in invalidTextures)
        {
            PbrTerrainRenderer renderer = Renderer(Atlas([invalid], Position(0, 0), []), AssetManager([]), logger: logger);
            renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
            Assert.AreEqual("waiting for a valid terrain atlas page", renderer.Status);
            SetLoadedTextureDisposed(invalid);
        }

        IShaderAPI shaderApi = Proxy<IShaderAPI>((method, _) =>
            method.Name == nameof(IShaderAPI.GetProgram) ? null : Default(method));
        PbrTerrainRenderer noShader = Renderer(Atlas([], Position(0, 0), []), AssetManager([]), logger: logger, shader: shaderApi);
        InvokeRenderer(noShader, "BindTerrainShader");
        IShaderProgram disposed = Proxy<IShaderProgram>((method, _) => method.Name switch
        {
            "get_Disposed" => true,
            "get_ProgramId" => 1,
            _ => Default(method)
        });
        IShaderAPI disposedApi = Proxy<IShaderAPI>((method, _) =>
            method.Name == nameof(IShaderAPI.GetProgram) ? disposed : Default(method));
        PbrTerrainRenderer disposedRenderer = Renderer(Atlas([], Position(0, 0), []), AssetManager([]), logger: logger, shader: disposedApi);
        InvokeRenderer(disposedRenderer, "BindTerrainShader");
        IShaderProgram zeroProgram = Proxy<IShaderProgram>((method, _) => method.Name switch
        {
            "get_Disposed" => false,
            "get_ProgramId" => 0,
            _ => Default(method)
        });
        IShaderAPI zeroProgramApi = Proxy<IShaderAPI>((method, _) =>
            method.Name == nameof(IShaderAPI.GetProgram) ? zeroProgram : Default(method));
        PbrTerrainRenderer zeroProgramRenderer = Renderer(
            Atlas([], Position(0, 0), []),
            AssetManager([]),
            logger: logger,
            shader: zeroProgramApi);
        InvokeRenderer(zeroProgramRenderer, "BindTerrainShader");
    }

    /// <summary>
    /// Verifies the terrain Manifest Contracts Retain All Assigned Metadata regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TerrainManifestContractsRetainAllAssignedMetadata()
    {
        PbrMapReference map = new() { Asset = "a", Sha256 = "b", Encoding = "c" };
        PbrSourceReference source = new()
        {
            ModId = "m",
            ModVersion = "1",
            Domain = "d",
            Path = "p",
            Sha256 = "s"
        };
        PbrManifestTexture texture = new()
        {
            Source = source,
            Normal = map,
            Roughness = map,
            Metallic = map,
            Emissive = map
        };
        PbrManifest manifest = new() { Schema = "schema", SchemaVersion = 3, Textures = [texture] };
        Assert.AreEqual("schema", manifest.Schema);
        Assert.AreEqual(3, manifest.SchemaVersion);
        Assert.AreSame(texture, manifest.Textures[0]);
        Assert.AreEqual("m", texture.Source.ModId);
        Assert.AreEqual("1", source.ModVersion);
        Assert.AreEqual("d", source.Domain);
        Assert.AreEqual("p", source.Path);
        Assert.AreEqual("s", source.Sha256);
        Assert.AreEqual("a", texture.Normal.Asset);
        Assert.AreEqual("b", texture.Roughness.Sha256);
        Assert.AreEqual("c", texture.Metallic.Encoding);
        Assert.AreSame(map, texture.Emissive);

        PbrAtlasLookup lookup = new([], [], 1, 2, 3, 4);
        Assert.AreEqual(1, lookup.ExactPlacementCount);
        Assert.AreEqual(2, lookup.AmbiguousRectangleCount);
        Assert.AreEqual(3, lookup.SkippedSourceLinkCount);
        Assert.AreEqual(4, lookup.SkippedCompositeRectangleCount);
        Assert.AreEqual(0, lookup.Positions.Count);
        Assert.AreEqual(0, lookup.AmbiguousSources.Count);
    }

    /// <summary>
    /// Executes the composite step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="code">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The composite result consumed by the caller&apos;s assertion.</returns>
    private static CompositeTexture Composite(string code) => new(new AssetLocation(code));

    /// <summary>
    /// Executes the baked step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="code">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The baked result consumed by the caller&apos;s assertion.</returns>
    private static BakedCompositeTexture Baked(string code) => new()
    {
        BakedName = new AssetLocation(code),
        TextureFilenames = [new AssetLocation(code)]
    };

    /// <summary>
    /// Executes the composite With Baked Pbr Variant step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <returns>The composite With Baked Pbr Variant result consumed by the caller&apos;s assertion.</returns>
    private static CompositeTexture CompositeWithBakedPbrVariant()
    {
        CompositeTexture composite = Composite("game:textures/albedo.png");
        composite.Baked = Baked("game:textures/albedo.png");
        composite.Baked.BakedVariants = [Baked("game:textures/albedo_n.png")];
        return composite;
    }

    /// <summary>
    /// Executes the capture step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="assets">The assets input used to configure this deterministic test path.</param>
    /// <returns>The capture result consumed by the caller&apos;s assertion.</returns>
    private static PbrSidecarAssetStore Capture(Dictionary<AssetLocation, IAsset> assets)
    {
        ILogger logger = Proxy<ILogger>((method, _) => Default(method));
        return PbrSidecarAssetStore.Capture(AssetManager(assets), logger);
    }

    /// <summary>
    /// Executes the core step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="manager">The manager input used to configure this deterministic test path.</param>
    /// <param name="logger">The logger input used to configure this deterministic test path.</param>
    /// <returns>The core result consumed by the caller&apos;s assertion.</returns>
    private static ICoreAPI Core(IAssetManager manager, ILogger logger) =>
        Proxy<ICoreAPI>((method, _) => method.Name switch
        {
            "get_Assets" => manager,
            "get_Logger" => logger,
            _ => Default(method)
        });

    /// <summary>
    /// Executes the client Core step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="atlas">The atlas input used to configure this deterministic test path.</param>
    /// <param name="logger">The logger input used to configure this deterministic test path.</param>
    /// <returns>The client Core result consumed by the caller&apos;s assertion.</returns>
    private static ICoreClientAPI ClientCore(IBlockTextureAtlasAPI atlas, ILogger logger) =>
        Proxy<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_BlockTextureAtlas" => atlas,
            "get_Logger" => logger,
            _ => Default(method)
        });

    /// <summary>
    /// Executes the asset Manager step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="assets">The assets input used to configure this deterministic test path.</param>
    /// <param name="manifests">The manifests input used to configure this deterministic test path.</param>
    /// <param name="origins">The origins input used to configure this deterministic test path.</param>
    /// <returns>The asset Manager result consumed by the caller&apos;s assertion.</returns>
    private static IAssetManager AssetManager(
        Dictionary<AssetLocation, IAsset> assets,
        List<IAsset>? manifests = null,
        List<IAssetOrigin>? origins = null) =>
        Proxy<IAssetManager>((method, args) => method.Name switch
        {
            "get_AllAssets" => assets,
            "get_Origins" => origins ?? [],
            "TryGet" => assets.TryGetValue((AssetLocation)args![0]!, out IAsset? asset) ? asset : null,
            "GetManyInCategory" => manifests ?? [],
            _ => Default(method)
        });

    /// <summary>
    /// Executes the atlas step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="textures">The textures input used to configure this deterministic test path.</param>
    /// <param name="unknown">The unknown input used to configure this deterministic test path.</param>
    /// <param name="positions">The positions input used to configure this deterministic test path.</param>
    /// <param name="lookup">The lookup input used to configure this deterministic test path.</param>
    /// <returns>The atlas result consumed by the caller&apos;s assertion.</returns>
    private static IBlockTextureAtlasAPI Atlas(
        List<LoadedTexture> textures,
        TextureAtlasPosition unknown,
        TextureAtlasPosition[] positions,
        System.Func<AssetLocation, TextureAtlasPosition>? lookup = null) =>
        Proxy<IBlockTextureAtlasAPI>((method, args) => method.Name switch
        {
            "get_AtlasTextures" => textures,
            "get_UnknownTexturePosition" => unknown,
            "get_Positions" => positions,
            "get_Item" => lookup?.Invoke((AssetLocation)args![0]!) ?? unknown,
            _ => Default(method)
        });

    /// <summary>
    /// Executes the renderer step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="atlas">The atlas input used to configure this deterministic test path.</param>
    /// <param name="assets">The assets input used to configure this deterministic test path.</param>
    /// <param name="store">The store input used to configure this deterministic test path.</param>
    /// <param name="logger">The logger input used to configure this deterministic test path.</param>
    /// <param name="shader">The shader input used to configure this deterministic test path.</param>
    /// <param name="blocks">The blocks input used to configure this deterministic test path.</param>
    /// <returns>The renderer result consumed by the caller&apos;s assertion.</returns>
    private static PbrTerrainRenderer Renderer(
        IBlockTextureAtlasAPI atlas,
        IAssetManager assets,
        PbrSidecarAssetStore? store = null,
        ILogger? logger = null,
        IShaderAPI? shader = null,
        List<Block>? blocks = null)
    {
        logger ??= Proxy<ILogger>((method, _) => Default(method));
        shader ??= Proxy<IShaderAPI>((method, _) => Default(method));
        IClientWorldAccessor world = Proxy<IClientWorldAccessor>((method, _) => method.Name switch
        {
            "get_Blocks" => blocks ?? [],
            _ => Default(method)
        });
        ICoreClientAPI api = Proxy<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_BlockTextureAtlas" => atlas,
            "get_Assets" => assets,
            "get_Logger" => logger,
            "get_Shader" => shader,
            "get_World" => world,
            _ => Default(method)
        });
        return new PbrTerrainRenderer(api, store ?? Capture([]));
    }

    /// <summary>
    /// Executes the position step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="textureId">The texture Id input used to configure this deterministic test path.</param>
    /// <param name="atlasNumber">The atlas Number input used to configure this deterministic test path.</param>
    /// <returns>The position result consumed by the caller&apos;s assertion.</returns>
    private static TextureAtlasPosition Position(int textureId, byte atlasNumber) => new()
    {
        atlasTextureId = textureId,
        atlasNumber = atlasNumber,
        x1 = 0,
        y1 = 0,
        x2 = 1,
        y2 = 1
    };

    /// <summary>
    /// Executes the position Like step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="source">Caller-owned input consumed only for the duration of this test step.</param>
    /// <param name="atlasNumber">The atlas Number input used to configure this deterministic test path.</param>
    /// <param name="x1">The x1 input used to configure this deterministic test path.</param>
    /// <param name="y1">The y1 input used to configure this deterministic test path.</param>
    /// <param name="x2">The x2 input used to configure this deterministic test path.</param>
    /// <param name="y2">The y2 input used to configure this deterministic test path.</param>
    /// <returns>The position Like result consumed by the caller&apos;s assertion.</returns>
    private static TextureAtlasPosition PositionLike(
        TextureAtlasPosition source,
        byte? atlasNumber = null,
        float? x1 = null,
        float? y1 = null,
        float? x2 = null,
        float? y2 = null) =>
        new()
        {
            atlasTextureId = source.atlasTextureId,
            atlasNumber = atlasNumber ?? source.atlasNumber,
            x1 = x1 ?? source.x1,
            y1 = y1 ?? source.y1,
            x2 = x2 ?? source.x2,
            y2 = y2 ?? source.y2
        };

    /// <summary>
    /// Executes the asset step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="location">The location input used to configure this deterministic test path.</param>
    /// <param name="loaded">The loaded input used to configure this deterministic test path.</param>
    /// <param name="loadResult">The load Result input used to configure this deterministic test path.</param>
    /// <param name="data">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The asset result consumed by the caller&apos;s assertion.</returns>
    private static IAsset Asset(
        AssetLocation location,
        bool loaded,
        bool loadResult = true,
        byte[]? data = null)
    {
        IAsset? asset = null;
        IAssetOrigin origin = Proxy<IAssetOrigin>((method, args) =>
            method.Name == nameof(IAssetOrigin.TryLoadAsset) ? loadResult : Default(method));
        asset = Proxy<IAsset>((method, _) => method.Name switch
        {
            "get_Location" => location,
            "get_Data" => data ?? [1],
            "get_Origin" => origin,
            "IsLoaded" => loaded,
            _ => Default(method)
        });
        return asset;
    }

    /// <summary>
    /// Executes the manifest Asset step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="manifest">The manifest input used to configure this deterministic test path.</param>
    /// <returns>The manifest Asset result consumed by the caller&apos;s assertion.</returns>
    private static IAsset ManifestAsset(PbrManifest manifest) =>
        ManifestAsset(JsonSerializer.SerializeToUtf8Bytes(manifest));

    /// <summary>
    /// Executes the manifest Asset step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="data">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The manifest Asset result consumed by the caller&apos;s assertion.</returns>
    private static IAsset ManifestAsset(byte[] data) =>
        Asset(
            new AssetLocation("mod", "config/vintagertx/pbr-manifest.json"),
            loaded: true,
            data: data);

    /// <summary>
    /// Executes the manifest Entry step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="sourcePath">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="normalAsset">The normal Asset input used to configure this deterministic test path.</param>
    /// <param name="normalSha256">The normal Sha256 input used to configure this deterministic test path.</param>
    /// <param name="roughnessAsset">The roughness Asset input used to configure this deterministic test path.</param>
    /// <param name="roughnessSha256">The roughness Sha256 input used to configure this deterministic test path.</param>
    /// <param name="metallicAsset">The metallic Asset input used to configure this deterministic test path.</param>
    /// <param name="metallicSha256">The metallic Sha256 input used to configure this deterministic test path.</param>
    /// <param name="emissiveAsset">The emissive Asset input used to configure this deterministic test path.</param>
    /// <param name="emissiveSha256">The emissive Sha256 input used to configure this deterministic test path.</param>
    /// <returns>The manifest Entry result consumed by the caller&apos;s assertion.</returns>
    private static PbrManifestTexture ManifestEntry(
        string sourcePath,
        string normalAsset,
        string normalSha256 = "",
        string roughnessAsset = "",
        string roughnessSha256 = "",
        string metallicAsset = "",
        string metallicSha256 = "",
        string emissiveAsset = "",
        string emissiveSha256 = "") =>
        new()
        {
            Source = new PbrSourceReference { Domain = "mod", Path = sourcePath },
            Normal = new PbrMapReference { Asset = normalAsset, Sha256 = normalSha256 },
            Roughness = new PbrMapReference { Asset = roughnessAsset, Sha256 = roughnessSha256 },
            Metallic = new PbrMapReference { Asset = metallicAsset, Sha256 = metallicSha256 },
            Emissive = new PbrMapReference { Asset = emissiveAsset, Sha256 = emissiveSha256 }
        };

    /// <summary>
    /// Executes the proxy step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="handler">The handler input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The proxy result consumed by the caller&apos;s assertion.</returns>
    private static T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class =>
        RuntimeCoverageDispatchProxy.Create<T>(handler);

    /// <summary>
    /// Executes the default step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="method">The method input used to configure this deterministic test path.</param>
    /// <returns>The default result consumed by the caller&apos;s assertion.</returns>
    private static object? Default(MethodInfo method) =>
        RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);

    /// <summary>
    /// Invokes patch through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>The invoke Patch result consumed by the caller&apos;s assertion.</returns>
    private static object InvokePatch(string name, params object?[]? args)
    {
        MethodInfo method = typeof(PbrAssetDiscoveryPatch).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PbrAssetDiscoveryPatch).FullName, name);
        return method.Invoke(null, args) ?? new object();
    }

    /// <summary>
    /// Invokes renderer through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="renderer">The renderer input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>The invoke Renderer result consumed by the caller&apos;s assertion.</returns>
    private static object InvokeRenderer(PbrTerrainRenderer renderer, string name, params object?[]? args)
    {
        MethodInfo method = typeof(PbrTerrainRenderer).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PbrTerrainRenderer).FullName, name);
        return method.Invoke(renderer, args) ?? new object();
    }

    /// <summary>
    /// Invokes renderer Nullable through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="renderer">The renderer input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>The invoke Renderer Nullable result consumed by the caller&apos;s assertion.</returns>
    private static object? InvokeRendererNullable(PbrTerrainRenderer renderer, string name, params object?[]? args)
    {
        MethodInfo method = typeof(PbrTerrainRenderer).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PbrTerrainRenderer).FullName, name);
        return method.Invoke(renderer, args);
    }

    /// <summary>
    /// Invokes renderer Static through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>The invoke Renderer Static result consumed by the caller&apos;s assertion.</returns>
    private static object InvokeRendererStatic(string name, params object?[]? args)
    {
        MethodInfo method = typeof(PbrTerrainRenderer).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PbrTerrainRenderer).FullName, name);
        return method.Invoke(null, args) ?? new object();
    }

    /// <summary>
    /// Sets field on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private static void SetField(object instance, string name, object? value)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        field.SetValue(instance, value);
    }

    /// <summary>
    /// Sets loaded Texture Disposed on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="texture">The texture input used to configure this deterministic test path.</param>
    private static void SetLoadedTextureDisposed(LoadedTexture texture)
    {
        FieldInfo field = typeof(LoadedTexture).GetField(
            "disposed",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(LoadedTexture).FullName, "disposed");
        field.SetValue(texture, true);
    }

    /// <summary>
    /// Returns field from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The get Field result consumed by the caller&apos;s assertion.</returns>
    private static object? GetField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        return field.GetValue(instance);
    }

    /// <summary>
    /// Returns static from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The get Static result consumed by the caller&apos;s assertion.</returns>
    private static object? GetStatic(string name)
    {
        FieldInfo field = typeof(PbrAssetDiscoveryPatch).GetField(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PbrAssetDiscoveryPatch).FullName, name);
        return field.GetValue(null);
    }

    /// <summary>
    /// Sets static on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private static void SetStatic(string name, object? value)
    {
        FieldInfo field = typeof(PbrAssetDiscoveryPatch).GetField(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PbrAssetDiscoveryPatch).FullName, name);
        field.SetValue(null, value);
    }

    /// <summary>
    /// Executes the install Access Tools Fault Harness step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <returns>The install Access Tools Fault Harness result consumed by the caller&apos;s assertion.</returns>
    private static Harmony InstallAccessToolsFaultHarness()
    {
        Harmony harmony = new("vintagertx.tests.access-tools-faults");
        MethodInfo typeByName = typeof(AccessTools).GetMethod(
            nameof(AccessTools.TypeByName),
            BindingFlags.Static | BindingFlags.Public,
            [typeof(string)])
            ?? throw new MissingMethodException(typeof(AccessTools).FullName, nameof(AccessTools.TypeByName));
        MethodInfo method = typeof(AccessTools).GetMethod(
            nameof(AccessTools.Method),
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Type), typeof(string), typeof(Type[]), typeof(Type[])])
            ?? throw new MissingMethodException(typeof(AccessTools).FullName, nameof(AccessTools.Method));
        harmony.Patch(
            typeByName,
            prefix: new HarmonyMethod(typeof(PbrPipelineCoverageTests), nameof(FilterTypeByName)));
        harmony.Patch(
            method,
            prefix: new HarmonyMethod(typeof(PbrPipelineCoverageTests), nameof(FilterMethod)));
        return harmony;
    }

    /// <summary>
    /// Executes the filter Type By Name step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="__result">The result input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool FilterTypeByName(string name, ref Type? __result)
    {
        if (!string.Equals(name, deniedTypeName, StringComparison.Ordinal))
        {
            return true;
        }

        __result = null;
        return false;
    }

    /// <summary>
    /// Executes the filter Method step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="type">The type input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="__result">The result input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool FilterMethod(Type type, string name, ref MethodInfo? __result)
    {
        if (!ReferenceEquals(type, deniedMethodType)
            || !string.Equals(name, deniedMethodName, StringComparison.Ordinal))
        {
            return true;
        }

        __result = null;
        return false;
    }

    /// <summary>
    /// Executes the filter Scale Pixels step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="__result">The result input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool FilterScalePixels(ref bool __result)
    {
        scaleCallCount++;
        if (deniedScaleCall <= 0 || scaleCallCount != deniedScaleCall)
        {
            return true;
        }

        __result = false;
        return false;
    }

    /// <summary>
    /// Executes the filter Codec Pixels step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="__result">The result input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    private static bool FilterCodecPixels(ref SKCodecResult __result)
    {
        if (forcedCodecResult is not SKCodecResult result)
        {
            return true;
        }

        __result = result;
        return false;
    }

    /// <summary>
    /// Asserts install Throws and throws when the regression contract is violated.
    /// </summary>
    /// <param name="api">Vintage Story API facade or test double supplied to the scenario.</param>
    /// <param name="deniedType">The denied Type input used to configure this deterministic test path.</param>
    /// <param name="deniedMethodTypeValue">The denied Method Type Value input used to configure this deterministic test path.</param>
    /// <param name="deniedMethod">The denied Method input used to configure this deterministic test path.</param>
    /// <typeparam name="TException">Type participating in the generic test contract.</typeparam>
    private static void AssertInstallThrows<TException>(
        ICoreAPI api,
        string? deniedType = null,
        Type? deniedMethodTypeValue = null,
        string? deniedMethod = null)
        where TException : Exception
    {
        deniedTypeName = deniedType;
        deniedMethodType = deniedMethodTypeValue;
        deniedMethodName = deniedMethod;
        try
        {
            Assert.ThrowsException<TException>(() => PbrAssetDiscoveryPatch.Install(api));
        }
        finally
        {
            deniedTypeName = null;
            deniedMethodType = null;
            deniedMethodName = null;
            PbrAssetDiscoveryPatch.Uninstall();
        }
    }

    /// <summary>
    /// Asserts patch Bake Callsite Throws and throws when the regression contract is violated.
    /// </summary>
    /// <param name="deniedType">The denied Type input used to configure this deterministic test path.</param>
    /// <param name="deniedMethodTypeValue">The denied Method Type Value input used to configure this deterministic test path.</param>
    /// <param name="deniedMethod">The denied Method input used to configure this deterministic test path.</param>
    /// <typeparam name="TException">Type participating in the generic test contract.</typeparam>
    private static void AssertPatchBakeCallsiteThrows<TException>(
        string? deniedType = null,
        Type? deniedMethodTypeValue = null,
        string? deniedMethod = null)
        where TException : Exception
    {
        deniedTypeName = deniedType;
        deniedMethodType = deniedMethodTypeValue;
        deniedMethodName = deniedMethod;
        SetStatic("harmony", new Harmony("vintagertx.tests.patch-callsite"));
        try
        {
            TargetInvocationException wrapper = Assert.ThrowsException<TargetInvocationException>(() =>
                InvokePatch(
                    "PatchBakeCallsite",
                    "Vintagestory.Client.NoObf.EntityTextureAtlasManager",
                    "LoadShapeTextures"));
            Assert.IsInstanceOfType<TException>(wrapper.InnerException);
        }
        finally
        {
            deniedTypeName = null;
            deniedMethodType = null;
            deniedMethodName = null;
            PbrAssetDiscoveryPatch.Uninstall();
        }
    }

    /// <summary>
    /// Asserts atlas Transpiler Throws and throws when the regression contract is violated.
    /// </summary>
    /// <param name="type">The type input used to configure this deterministic test path.</param>
    /// <param name="methodName">Stable identifier selecting the deterministic fixture case.</param>
    /// <typeparam name="TException">Type participating in the generic test contract.</typeparam>
    private static void AssertAtlasTranspilerThrows<TException>(Type type, string methodName)
        where TException : Exception
    {
        deniedMethodType = type;
        deniedMethodName = methodName;
        try
        {
            Assert.ThrowsException<TException>(() =>
                ((IEnumerable<CodeInstruction>)InvokePatch(
                    "WrapAtlasBakeCalls",
                    (object)new[] { new CodeInstruction(OpCodes.Nop) }))
                .ToArray());
        }
        finally
        {
            deniedMethodType = null;
            deniedMethodName = null;
        }
    }

    /// <summary>
    /// Executes the png step used by the deterministic pbr Pipeline Coverage Tests fixture.
    /// </summary>
    /// <param name="color">The color input used to configure this deterministic test path.</param>
    /// <param name="width">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="height">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <returns>The png result consumed by the caller&apos;s assertion.</returns>
    private static byte[] Png(SKColor color, int width = 1, int height = 1)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
