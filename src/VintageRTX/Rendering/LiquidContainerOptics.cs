using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Rendering;

/// <summary>Bit contract stored in the voxel liquid metadata texture's green channel.</summary>
[Flags]
internal enum VoxelLiquidFlags : byte
{
    /// <summary>No liquid semantics are present for the voxel.</summary>
    None = 0,
    /// <summary>The cell is populated through Vintage Story's dedicated fluid block layer.</summary>
    FluidLayer = 1,
    /// <summary>The liquid belongs to an inventory-backed block such as a barrel or bucket.</summary>
    Contained = 2,
    /// <summary>The surface is open and can contribute reflection, refraction, and disturbances.</summary>
    VisibleSurface = 4
}

/// <summary>
/// Describes how an inventory-backed block exposes a planar liquid surface. Heights are normalized
/// block-local Y coordinates; capacity is expressed in litres and the content slot is zero-based.
/// </summary>
internal readonly record struct LiquidContainerOptics(
    int ContentSlot,
    float CapacityLitres,
    float SurfaceMinimumY,
    float SurfaceMaximumY,
    bool AlwaysOpen,
    string VisibilityTreeBool,
    bool VisibleWhen)
{
    /// <summary>Parses and validates the <c>vintageRtxLiquidContainer</c> JSON-patch contract.</summary>
    /// <param name="block">Block type whose attributes may declare a visible liquid inventory.</param>
    /// <param name="metadata">Validated metadata when every required unit/range is coherent.</param>
    /// <returns><see langword="false"/> for missing, closed-without-state, or out-of-range data.</returns>
    public static bool TryRead(Block block, out LiquidContainerOptics metadata)
    {
        ArgumentNullException.ThrowIfNull(block);
        return TryRead(block.Attributes, out metadata);
    }

    /// <summary>Parses container metadata from a raw attribute object reconstructed from JSON patches.</summary>
    /// <param name="attributes">Attributes containing <c>vintageRtxLiquidContainer</c>.</param>
    /// <param name="metadata">Validated container metadata.</param>
    /// <returns><see langword="true"/> when all required volume and visibility fields are coherent.</returns>
    internal static bool TryRead(JsonObject? attributes, out LiquidContainerOptics metadata)
    {
        metadata = default;
        if (attributes is null)
        {
            return false;
        }

        JsonObject value = attributes["vintageRtxLiquidContainer"];
        if (!value.Exists
            || !value.KeyExists("contentSlot")
            || !value.KeyExists("capacityLitres")
            || !value.KeyExists("surfaceMinimumY")
            || !value.KeyExists("surfaceMaximumY"))
        {
            return false;
        }

        int contentSlot = value["contentSlot"].AsInt(-1);
        float capacity = value["capacityLitres"].AsFloat(float.NaN);
        float minimumY = value["surfaceMinimumY"].AsFloat(float.NaN);
        float maximumY = value["surfaceMaximumY"].AsFloat(float.NaN);
        bool alwaysOpen = value["alwaysOpen"].AsBool(false);
        string visibilityTreeBool = value["visibilityTreeBool"]
            .AsString(string.Empty)
            .Trim();
        bool visibleWhen = value["visibleWhen"].AsBool(false);
        if (contentSlot < 0
            || !float.IsFinite(capacity) || capacity <= 0.0f
            || !float.IsFinite(minimumY) || minimumY < 0.0f || minimumY > 1.0f
            || !float.IsFinite(maximumY) || maximumY < minimumY || maximumY > 1.0f
            || (!alwaysOpen && visibilityTreeBool.Length == 0))
        {
            return false;
        }

        metadata = new LiquidContainerOptics(
            contentSlot,
            capacity,
            minimumY,
            maximumY,
            alwaysOpen,
            visibilityTreeBool,
            visibleWhen);
        return true;
    }

    /// <summary>Evaluates whether the container opening exposes its contents in the current block state.</summary>
    /// <param name="blockEntity">Live entity serialized to inspect the configured tree boolean.</param>
    /// <returns>Whether reflection/refraction may be emitted for the contained surface.</returns>
    public bool IsSurfaceVisible(BlockEntity blockEntity)
    {
        if (AlwaysOpen)
        {
            return true;
        }

        TreeAttribute state = new();
        blockEntity.ToTreeAttributes(state);
        return state.GetBool(VisibilityTreeBool, !VisibleWhen) == VisibleWhen;
    }

    /// <summary>Maps a clamped volume ratio onto the authored Y interval and UNorm8 encoding.</summary>
    /// <param name="fillRatio">Container fill fraction; values outside 0..1 are saturated.</param>
    /// <returns>Block-local surface height encoded over the inclusive byte range 0..255.</returns>
    public byte EncodeSurfaceHeight(float fillRatio)
    {
        float height = SurfaceMinimumY
            + Math.Clamp(fillRatio, 0.0f, 1.0f) * (SurfaceMaximumY - SurfaceMinimumY);
        return (byte)Math.Clamp((int)MathF.Round(height * 255.0f), 0, 255);
    }
}
