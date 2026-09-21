namespace VintageRTX.Rendering;

/// <summary>Caches only liquid topology; the fixed-step solver still advances every active cell.</summary>
internal sealed partial class LiquidSurfaceSimulation
{
    /// <summary>Row-major active cells with no-flux edges represented by the center index.</summary>
    private CellStencil[] activeStencils = [];
    /// <summary>Active prefix of <see cref="activeStencils"/>.</summary>
    private int activeStencilCount;
    /// <summary>Topology invalidation is independent of water height, velocity, wind, or time.</summary>
    private bool topologyDirty = true;
    /// <summary>Cyclic successor reproducing the original forward search for a rain-exposed cell.</summary>
    private int[] rainSuccessors = [];
    /// <summary>Number of exposed cells, preserving the original rainfall energy calculation.</summary>
    private int rainExposedCellCount;
    /// <summary>Row-major active cell IDs grouped by optical profile, including non-bubbling cells.</summary>
    private int[] profileCellIndices = [];
    /// <summary>Prefix offsets into <see cref="profileCellIndices"/>.</summary>
    private readonly int[] profileCellOffsets = new int[LiquidOpticalRegistry.LookupHeight + 1];
    /// <summary>Temporary counts reused when topology changes.</summary>
    private readonly int[] profileCellCounts = new int[LiquidOpticalRegistry.LookupHeight];
    /// <summary>Number of cached topology builds, exposed to deterministic regression tests.</summary>
    internal int TopologyBuildCount { get; private set; }

    /// <summary>
    /// Rebuilds row-major stencils and exact selection tables once after a topology edit batch.
    /// Boundary conditions use the same profile/base-height predicate as the reference solver.
    /// No radiance, wave state, random numbers, or time-dependent quantities are cached here.
    /// </summary>
    private void EnsureActiveTopology()
    {
        if (!topologyDirty) return;
        int count = heights.Length;
        if (activeStencils.Length != count)
        {
            activeStencils = new CellStencil[count];
            rainSuccessors = new int[count];
            profileCellIndices = new int[count];
        }
        activeStencilCount = 0;
        rainExposedCellCount = 0;
        Array.Clear(profileCellCounts);
        Array.Clear(bubbleAreaByProfile);
        Array.Clear(bubbleCellCountByProfile);
        int firstRainCell = -1;
        float cellArea = CellSize * CellSize;
        for (int index = 0; index < count; index++)
        {
            if (!IsActive(index)) continue;
            int x = index % Width;
            activeStencils[activeStencilCount++] = new CellStencil(index,
                x > 0 && IsConnected(index, index - 1) ? index - 1 : index,
                x + 1 < Width && IsConnected(index, index + 1) ? index + 1 : index,
                index >= Width && IsConnected(index, index - Width) ? index - Width : index,
                index + Width < count && IsConnected(index, index + Width) ? index + Width : index);
            byte profile = profileIds[index];
            profileCellCounts[profile]++;
            if (IsRainExposed(index))
            {
                rainExposedCellCount++;
                if (firstRainCell < 0) firstRainCell = index;
            }
            LiquidSurfaceDynamics coefficients = dynamics[index];
            if (!(coefficients.BubbleRate <= 0.0f || coefficients.BubbleRadiusMaximum <= 0.0f
                || coefficients.BubbleRiseDuration <= 0.0f))
            {
                // Keep the same summation order/rounding as the old per-step full-grid scan.
                bubbleAreaByProfile[profile] += cellArea;
                bubbleCellCountByProfile[profile]++;
            }
        }
        int successor = firstRainCell;
        for (int index = count - 1; index >= 0; index--)
        {
            if (IsRainExposed(index)) successor = index;
            rainSuccessors[index] = successor;
        }
        profileCellOffsets[0] = 0;
        for (int profile = 0; profile < profileCellCounts.Length; profile++)
            profileCellOffsets[profile + 1] = profileCellOffsets[profile] + profileCellCounts[profile];
        Array.Clear(profileCellCounts);
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
            int index = activeStencils[ordinal].Center;
            byte profile = profileIds[index];
            profileCellIndices[profileCellOffsets[profile] + profileCellCounts[profile]++] = index;
        }
        topologyDirty = false;
        TopologyBuildCount++;
    }

    /// <summary>Five flat indices for the unchanged nearest-neighbor finite-difference stencil.</summary>
    private readonly record struct CellStencil(int Center, int Left, int Right, int Up, int Down);
}
