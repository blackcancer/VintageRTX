using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>
/// Guards the unchecked array reads in Vintage Story 1.22.7's
/// <c>BlockFenceStackAware.OnJsonTesselation</c>. A boundary fence can otherwise abort the complete
/// chunk polygon build, indirectly removing geometry and its projected shadows.
/// </summary>
internal static class FenceStackAwareTessellationGuardPatch
{
    /// <summary>Runtime-only type name supplied by VSSurvivalMod.</summary>
    private const string TargetTypeName = "Vintagestory.GameContent.BlockFenceStackAware";
    /// <summary>Runtime-only upper-neighbour type tested by the stock method before mesh work.</summary>
    private const string FenceTypeName = "Vintagestory.GameContent.BlockFence";
    /// <summary>Harmony owner used to remove only this compatibility guard.</summary>
    private const string HarmonyId = "vintagertx.fence-stack-aware-bounds";
    /// <summary>Maximum detailed boundary warnings emitted during one installation.</summary>
    private const int DetailedWarningLimit = 8;
    /// <summary>One additional saturated count represents the suppression notice.</summary>
    private const int SaturatedReportCount = DetailedWarningLimit + 1;

    /// <summary>Owned Harmony instance while the renderer is active.</summary>
    private static Harmony? harmony;
    /// <summary>Client logger detached during teardown.</summary>
    private static ILogger? logger;
    /// <summary>Detailed reports plus at most one suppression report.</summary>
    private static int boundedReportCount;
    /// <summary>Runtime fence base type used to preserve the stock early-return branch.</summary>
    private static Type? fenceType;
    /// <summary>Per-block mesh cache read by the stock method before its baked-variant array.</summary>
    private static FieldInfo? continuousFenceMeshesField;
    /// <summary>Cache-key prefix paired with <see cref="continuousFenceMeshesField"/>.</summary>
    private static FieldInfo? continuousFenceCodeField;

    /// <summary>
    /// Resolves the survival-mod type by name so VintageRTX does not acquire a compile-time
    /// reference to VSSurvivalMod.
    /// </summary>
    /// <param name="clientApi">Active client API providing compatibility diagnostics.</param>
    /// <returns>Whether the exact 1.22.7 method contract was patched.</returns>
    internal static bool Install(ICoreClientAPI clientApi) =>
        Install(clientApi, AccessTools.TypeByName);

    /// <summary>Installs the exact guard through a testable runtime type resolver.</summary>
    /// <param name="clientApi">Active client API providing compatibility diagnostics.</param>
    /// <param name="typeResolver">Resolver receiving the runtime-qualified survival type name.</param>
    /// <returns>Whether the exact method contract was patched.</returns>
    internal static bool Install(
        ICoreClientAPI clientApi,
        System.Func<string, Type?> typeResolver)
    {
        ArgumentNullException.ThrowIfNull(clientApi);
        ArgumentNullException.ThrowIfNull(typeResolver);
        Uninstall();

        Type? targetType = typeResolver(TargetTypeName);
        MethodInfo? target = ResolveTarget(targetType);
        Type? resolvedFenceType = typeResolver(FenceTypeName);
        FieldInfo? resolvedMeshesField = targetType is null
            ? null
            : AccessTools.Field(targetType, "continousFenceMeches");
        FieldInfo? resolvedCodeField = targetType is null
            ? null
            : AccessTools.Field(targetType, "cntCode");
        bool exactFields = resolvedMeshesField is not null
            && typeof(Dictionary<string, MeshData>).IsAssignableFrom(resolvedMeshesField.FieldType)
            && resolvedCodeField?.FieldType == typeof(string);
        if (target is null || resolvedFenceType is null || !exactFields)
        {
            clientApi.Logger.Warning(
                "[VintageRTX] Fence tessellation boundary guard unavailable: the exact 1.22.7 method contract was not found.");
            return false;
        }

        Harmony candidate = new(HarmonyId);
        try
        {
            candidate.Patch(
                target,
                prefix: new HarmonyMethod(
                    typeof(FenceStackAwareTessellationGuardPatch),
                    nameof(BeforeOnJsonTesselation)));
        }
        catch (Exception exception)
        {
            // This catch covers installation only. The original tessellator is never wrapped or
            // caught: every valid invocation retains its native exception semantics.
            candidate.UnpatchAll(HarmonyId);
            clientApi.Logger.Warning(
                "[VintageRTX] Fence tessellation boundary guard could not be installed: {0}",
                exception.Message);
            return false;
        }

        harmony = candidate;
        logger = clientApi.Logger;
        fenceType = resolvedFenceType;
        continuousFenceMeshesField = resolvedMeshesField;
        continuousFenceCodeField = resolvedCodeField;
        Volatile.Write(ref boundedReportCount, 0);
        clientApi.Logger.Notification(
            "[VintageRTX] Fence tessellation array guard installed for Vintage Story 1.22.7: MoveIndex[4], extended-neighbor and baked-variant reads audited.");
        return true;
    }

    /// <summary>Finds only the affected by-reference tessellation signature.</summary>
    /// <param name="targetType">Runtime survival type, or null when the module is absent.</param>
    /// <returns>The exact instance method, otherwise null.</returns>
    internal static MethodInfo? ResolveTarget(Type? targetType)
    {
        if (targetType is null)
        {
            return null;
        }

        MethodInfo? method = targetType.GetMethod(
            "OnJsonTesselation",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(MeshData).MakeByRefType(),
                typeof(int[]).MakeByRefType(),
                typeof(BlockPos),
                typeof(Block[]),
                typeof(int)
            ],
            modifiers: null);
        return method?.DeclaringType == targetType && method.ReturnType == typeof(void)
            ? method
            : null;
    }

    /// <summary>
    /// Lets the original method run unless one of its three audited array reads would be outside
    /// the supplying array. Non-array failures retain native exception semantics.
    /// </summary>
    /// <param name="__instance">Runtime fence-stack block supplied by Harmony.</param>
    /// <param name="pos">World position used by the stock cache and texture-variant hashes.</param>
    /// <param name="chunkExtBlocks">Extended chunk array passed by the stock tessellator.</param>
    /// <param name="extIndex3d">Current block index inside that array.</param>
    /// <returns>True for the native path; false only when an audited array read is unsafe.</returns>
    private static bool BeforeOnJsonTesselation(
        object __instance,
        BlockPos? pos,
        Block[]? chunkExtBlocks,
        int extIndex3d)
    {
        int[]? moveIndices = TileSideEnum.MoveIndex;
        FenceTessellationUnsafeArray unsafeArray = ResolveUpperNeighbor(
            chunkExtBlocks,
            extIndex3d,
            moveIndices,
            out Block? upperNeighbor);
        if (unsafeArray != FenceTessellationUnsafeArray.None)
        {
            ReportSuppressedAccess(
                unsafeArray,
                extIndex3d,
                chunkExtBlocks?.Length ?? 0,
                moveIndices,
                selectedVariant: -1,
                bakedVariantCount: -1);
            return false;
        }

        Type? activeFenceType = fenceType;
        if (upperNeighbor is null
            || activeFenceType is null
            || !activeFenceType.IsInstanceOfType(upperNeighbor))
        {
            return true;
        }

        if (pos is null
            || !TryResolveCachedMesh(__instance, pos, out bool meshCached)
            || meshCached
            || __instance is not Block stackBlock
            || stackBlock.Textures is null
            || !stackBlock.Textures.TryGetValue("wall", out CompositeTexture? wallTexture)
            || wallTexture is null)
        {
            return true;
        }

        bool validVariant = HasValidBakedVariantAccess(
            pos,
            wallTexture,
            out int selectedVariant,
            out int bakedVariantCount);
        if (validVariant)
        {
            return true;
        }

        ReportSuppressedAccess(
            FenceTessellationUnsafeArray.BakedTextureVariant,
            extIndex3d,
            chunkExtBlocks?.Length ?? 0,
            moveIndices,
            selectedVariant,
            bakedVariantCount);
        return false;
    }

    /// <summary>Resolves the two exact upper-neighbour array reads at IL offsets 000a and 000c.</summary>
    /// <param name="chunkExtBlocks">Extended chunk array to be indexed.</param>
    /// <param name="extIndex3d">Current extended-chunk index.</param>
    /// <param name="moveIndices">Engine side offsets whose fourth entry denotes upward motion.</param>
    /// <param name="upperNeighbor">Safely resolved upper neighbor for the stock type test.</param>
    /// <returns>The unsafe read, or <see cref="FenceTessellationUnsafeArray.None"/>.</returns>
    internal static FenceTessellationUnsafeArray ResolveUpperNeighbor(
        Block[]? chunkExtBlocks,
        int extIndex3d,
        int[]? moveIndices,
        out Block? upperNeighbor)
    {
        upperNeighbor = null;
        if (moveIndices is null || moveIndices.Length <= TileSideEnum.Up)
        {
            return FenceTessellationUnsafeArray.MoveIndexTable;
        }

        if (chunkExtBlocks is null)
        {
            return FenceTessellationUnsafeArray.ExtendedChunkNeighbor;
        }

        long neighborIndex = (long)extIndex3d + moveIndices[TileSideEnum.Up];
        if (neighborIndex < 0 || neighborIndex >= chunkExtBlocks.LongLength)
        {
            return FenceTessellationUnsafeArray.ExtendedChunkNeighbor;
        }

        upperNeighbor = chunkExtBlocks[(int)neighborIndex];
        return FenceTessellationUnsafeArray.None;
    }

    /// <summary>Validates the exact <c>extIndex3d + MoveIndex[4]</c> access used by stock IL.</summary>
    /// <param name="chunkExtBlocks">Extended chunk array to be indexed.</param>
    /// <param name="extIndex3d">Current extended-chunk index.</param>
    /// <param name="moveIndices">Engine side offsets whose fourth entry denotes upward motion.</param>
    /// <returns>Whether both stock upper-neighbour reads are within their supplying arrays.</returns>
    internal static bool HasValidUpperNeighbor(
        Block[]? chunkExtBlocks,
        int extIndex3d,
        int[]? moveIndices) => ResolveUpperNeighbor(
            chunkExtBlocks,
            extIndex3d,
            moveIndices,
            out _) == FenceTessellationUnsafeArray.None;

    /// <summary>Reads the stock mesh cache without changing its contents.</summary>
    /// <param name="instance">Fence-stack instance owning the cache.</param>
    /// <param name="pos">World position used to select one of eight authored top meshes.</param>
    /// <param name="meshCached">Whether the exact top mesh already exists.</param>
    /// <returns>Whether the runtime field values matched the audited 1.22.7 contract.</returns>
    private static bool TryResolveCachedMesh(object instance, BlockPos pos, out bool meshCached)
    {
        meshCached = false;
        if (continuousFenceMeshesField?.GetValue(instance) is not Dictionary<string, MeshData> meshes
            || continuousFenceCodeField is null)
        {
            return false;
        }

        string? code = continuousFenceCodeField.GetValue(instance) as string;
        int randomVariant = GameMath.MurmurHash3Mod(pos.X, pos.Y, pos.Z, 8) + 1;
        meshCached = meshes.ContainsKey((code ?? string.Empty) + randomVariant.ToString());
        return true;
    }

    /// <summary>Validates the baked-variant array read at stock IL offset 00fa.</summary>
    /// <param name="pos">World position used by the stock texture hash.</param>
    /// <param name="wallTexture">Wall texture selected by the stock dictionary lookup.</param>
    /// <param name="selectedVariant">Hashed index the original method will read.</param>
    /// <param name="bakedVariantCount">Available entries in the supplying array.</param>
    /// <returns>
    /// False only when the original method would index beyond a non-null baked-variant array. Null
    /// or empty precursor state remains native so unrelated exception types are not hidden.
    /// </returns>
    internal static bool HasValidBakedVariantAccess(
        BlockPos? pos,
        CompositeTexture? wallTexture,
        out int selectedVariant,
        out int bakedVariantCount)
    {
        selectedVariant = -1;
        bakedVariantCount = -1;
        CompositeTexture[]? alternates = wallTexture?.Alternates;
        BakedCompositeTexture[]? bakedVariants = wallTexture?.Baked?.BakedVariants;
        if (pos is null
            || alternates is null
            || alternates.Length == 0
            || bakedVariants is null)
        {
            return true;
        }

        selectedVariant = GameMath.MurmurHash3Mod(
            pos.X,
            pos.Y,
            pos.Z,
            alternates.Length);
        bakedVariantCount = bakedVariants.Length;
        return selectedVariant >= 0 && selectedVariant < bakedVariants.Length;
    }

    /// <summary>Emits bounded diagnostics without changing the native valid-index path.</summary>
    /// <param name="unsafeArray">Exact stock array read rejected by the guard.</param>
    /// <param name="extIndex3d">Rejected current block index.</param>
    /// <param name="arrayLength">Supplied extended chunk length, or zero for null.</param>
    /// <param name="moveIndices">Current engine side-offset table.</param>
    /// <param name="selectedVariant">Hashed baked-variant index, or minus one when not applicable.</param>
    /// <param name="bakedVariantCount">Supplying baked array length, or minus one when unavailable.</param>
    private static void ReportSuppressedAccess(
        FenceTessellationUnsafeArray unsafeArray,
        int extIndex3d,
        int arrayLength,
        int[]? moveIndices,
        int selectedVariant,
        int bakedVariantCount)
    {
        int reportNumber = IncrementSaturatedReportCount();
        ILogger? activeLogger = logger;
        if (activeLogger is null || reportNumber == 0)
        {
            return;
        }

        if (reportNumber <= DetailedWarningLimit)
        {
            int? upperOffset = moveIndices is not null && moveIndices.Length > TileSideEnum.Up
                ? moveIndices[TileSideEnum.Up]
                : null;
            activeLogger.Warning(
                "[VintageRTX] Skipped unsafe fence tessellation array read ({0}/{1}): access={2}, extIndex={3}, upOffset={4}, chunkLength={5}, selectedVariant={6}, bakedVariants={7}.",
                reportNumber,
                DetailedWarningLimit,
                unsafeArray,
                extIndex3d,
                upperOffset?.ToString() ?? "unavailable",
                arrayLength,
                selectedVariant,
                bakedVariantCount);
        }
        else if (reportNumber == SaturatedReportCount)
        {
            activeLogger.Warning(
                "[VintageRTX] Further unsafe fence tessellation array read warnings are suppressed.");
        }
    }

    /// <summary>Atomically increments the report counter and bounds published report numbers.</summary>
    /// <returns>
    /// A value from one through <see cref="SaturatedReportCount"/>, or zero after saturation.
    /// </returns>
    private static int IncrementSaturatedReportCount()
    {
        int reportNumber = Interlocked.Increment(ref boundedReportCount);
        return reportNumber is > 0 and <= SaturatedReportCount
            ? reportNumber
            : 0;
    }

    /// <summary>Removes only this compatibility prefix and clears all world-owned state.</summary>
    internal static void Uninstall()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        logger = null;
        fenceType = null;
        continuousFenceMeshesField = null;
        continuousFenceCodeField = null;
        Volatile.Write(ref boundedReportCount, 0);
    }
}

/// <summary>Identifies each unsafe array operation present in the audited 1.22.7 method IL.</summary>
internal enum FenceTessellationUnsafeArray
{
    /// <summary>All audited array reads are safe.</summary>
    None,
    /// <summary>The stock <c>MoveIndex[4]</c> read has no fourth entry.</summary>
    MoveIndexTable,
    /// <summary>The computed upper-neighbour index is outside <c>chunkExtBlocks</c>.</summary>
    ExtendedChunkNeighbor,
    /// <summary>The hash selected an entry absent from <c>Baked.BakedVariants</c>.</summary>
    BakedTextureVariant
}
