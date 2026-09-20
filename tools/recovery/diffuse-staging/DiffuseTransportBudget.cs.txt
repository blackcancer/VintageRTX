namespace VintageRTX.Rendering;

/// <summary>Budgets current-frame diffuse transport without dropping dynamic lights at lower tiers.</summary>
internal static class DiffuseTransportBudget
{
    /// <summary>Reduces angular sample count while preserving every enabled tier's diffuse path.</summary>
    /// <param name="tier">Zero for full quality, one or two for reduced quality.</param>
    /// <param name="requested">Configured hemisphere sample count.</param>
    /// <param name="strength">Configured contribution weight; zero explicitly disables transport.</param>
    /// <returns>Zero when disabled, otherwise one to four hemisphere samples.</returns>
    internal static int RayCount(int tier, int requested, float strength)
    {
        if (!float.IsFinite(strength) || strength <= 0f) return 0;
        int count = Math.Clamp(requested, 0, 4);
        return tier == 0 ? count : Math.Min(count, 1);
    }

    /// <summary>Bounds unit-cell crossings over a normalized ray, including its starting cell.</summary>
    /// <param name="distance">Requested distance in world blocks.</param>
    /// <returns>A bounded traversal budget, not a ray-length approximation.</returns>
    internal static int TraversalSteps(float distance)
    {
        if (!float.IsFinite(distance) || distance <= 0f) return 1;
        // |dx|+|dy|+|dz| <= sqrt(3) * distance; three accounts for the starting cell
        // and independent fractional boundary phases. Exact ties can only reduce the count.
        return (int)Math.Min(128d, Math.Ceiling(Math.Sqrt(3d) * distance) + 3d);
    }

    /// <summary>Requires a published, settled geometry generation before an A/B transport capture.</summary>
    /// <param name="voxelEnabled">Whether the requested effect depends on voxel geometry.</param>
    /// <param name="gpuReady">Whether an immutable scene is published to the GPU.</param>
    /// <param name="generation">Identity of the published scene.</param>
    /// <param name="settled">Whether rebuilds and queued edits have completed.</param>
    /// <returns>Whether a frame can start or continue an honest comparison.</returns>
    internal static bool CaptureReady(bool voxelEnabled, bool gpuReady, int generation, bool settled) =>
        !voxelEnabled || (gpuReady && generation > 0 && settled);
}
