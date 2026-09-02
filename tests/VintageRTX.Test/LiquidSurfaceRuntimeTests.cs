using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Test;

/// <summary>
/// Verifies the immutable snapshot-to-solver contract used by the live liquid runtime without
/// requiring a Vintage Story world or an OpenGL context.
/// </summary>
[TestClass]
public sealed class LiquidSurfaceRuntimeTests
{
    /// <summary>Checks the exact public-engine level convention at both three-bit bounds.</summary>
    [TestMethod]
    public void FluidLevelDecoderMatchesVintageStoryEntityPhysics()
    {
        Assert.AreEqual(0.0f, LiquidSurfaceRuntime.DecodeFluidFillHeightMetres(0), 0.0f);
        Assert.AreEqual(0.375f, LiquidSurfaceRuntime.DecodeFluidFillHeightMetres(3), 0.0f);
        Assert.AreEqual(0.875f, LiquidSurfaceRuntime.DecodeFluidFillHeightMetres(7), 0.0f);
        Assert.AreEqual(0.875f, LiquidSurfaceRuntime.DecodeFluidFillHeightMetres(byte.MaxValue), 0.0f);
    }

    /// <summary>Ensures the bounded live query cannot evict airborne dropped items behind land entities.</summary>
    [TestMethod]
    public void RelevantSurfaceEntityFilterReservesBudgetForDroppedItemsAndLiquidContact()
    {
        EntityItem droppedItem = new() { Alive = true };
        Assert.IsTrue(LiquidSurfaceRuntime.IsRelevantSurfaceEntity(droppedItem));

        EntityPlayer generic = new() { Alive = true };
        Assert.IsFalse(LiquidSurfaceRuntime.IsRelevantSurfaceEntity(generic));

        generic.FeetInLiquid = true;
        Assert.IsTrue(LiquidSurfaceRuntime.IsRelevantSurfaceEntity(generic));

        generic.Alive = false;
        Assert.IsFalse(LiquidSurfaceRuntime.IsRelevantSurfaceEntity(generic));
    }

    /// <summary>Verifies that the highest visible authored surface wins within a world column.</summary>
    [TestMethod]
    public void TryResolveColumnSurfaceSelectsHigherVisibleContainerOverFluidLayer()
    {
        byte[] fluidSurface = new byte[2 * 2 * VoxelScene.FluidSurfaceChannels];
        int fluidOffset = (0 * 2 + 1) * VoxelScene.FluidSurfaceChannels;
        fluidSurface[fluidOffset] = 2;
        fluidSurface[fluidOffset + 1] = 1;

        byte[] metadata = new byte[2 * 3 * 2 * VoxelScene.LiquidMetadataChannels];
        int containedVoxel = (0 * 3 + 2) * 2 + 1;
        int metadataOffset = containedVoxel * VoxelScene.LiquidMetadataChannels;
        metadata[metadataOffset] = 2;
        metadata[metadataOffset + 1] = (byte)(
            VoxelLiquidFlags.Contained | VoxelLiquidFlags.VisibleSurface);
        metadata[metadataOffset + 2] = 191;
        VoxelSceneSnapshot snapshot = CreateSnapshot(fluidSurface, metadata);

        bool resolved = LiquidSurfaceRuntime.TryResolveColumnSurface(
            in snapshot,
            1,
            0,
            out float surfaceWorldY,
            out byte profileId);

        Assert.IsTrue(resolved);
        Assert.AreEqual(2, profileId);
        Assert.AreEqual(12.0f + 191.0f / 255.0f, surfaceWorldY, 1.0e-6f);
    }

    /// <summary>Verifies engine-aligned level height and contiguous same-profile geometric depth.</summary>
    [TestMethod]
    public void TryResolveColumnSurfaceReturnsPhysicalFluidHeightAndDepth()
    {
        byte[] fluidSurface = new byte[2 * 2 * VoxelScene.FluidSurfaceChannels];
        int fluidOffset = (0 * 2 + 1) * VoxelScene.FluidSurfaceChannels;
        fluidSurface[fluidOffset] = 3;
        fluidSurface[fluidOffset + 1] = 4;
        fluidSurface[fluidOffset + 2] = 3;

        byte[] metadata = new byte[2 * 3 * 2 * VoxelScene.LiquidMetadataChannels];
        WriteFluidVoxel(metadata, localX: 1, localY: 2, localZ: 0, profileId: 4, level: 3);
        WriteFluidVoxel(metadata, localX: 1, localY: 1, localZ: 0, profileId: 4, level: 7);
        WriteFluidVoxel(metadata, localX: 1, localY: 0, localZ: 0, profileId: 5, level: 7);
        VoxelSceneSnapshot snapshot = CreateSnapshot(fluidSurface, metadata);

        bool resolved = LiquidSurfaceRuntime.TryResolveColumnSurface(
            in snapshot,
            1,
            0,
            out float surfaceWorldY,
            out byte profileId,
            out float depthMetres);

        Assert.IsTrue(resolved);
        Assert.AreEqual(4, profileId);
        Assert.AreEqual(12.375f, surfaceWorldY, 1.0e-6f);
        Assert.AreEqual(1.375f, depthMetres, 1.0e-6f);
    }

    /// <summary>Verifies that reserved, hidden, truncated, and out-of-range columns stay inactive.</summary>
    [TestMethod]
    public void TryResolveColumnSurfaceRejectsInvisibleOrReservedPayloads()
    {
        byte[] fluidSurface = new byte[2 * 2 * VoxelScene.FluidSurfaceChannels];
        fluidSurface[0] = 3;
        fluidSurface[1] = LiquidOpticalRegistry.UnknownLiquidProfileId;
        byte[] hiddenMetadata = new byte[2 * 3 * 2 * VoxelScene.LiquidMetadataChannels];
        hiddenMetadata[0] = 1;
        hiddenMetadata[1] = (byte)VoxelLiquidFlags.Contained;
        VoxelSceneSnapshot snapshot = CreateSnapshot(fluidSurface, hiddenMetadata);

        Assert.IsFalse(LiquidSurfaceRuntime.TryResolveColumnSurface(
            in snapshot,
            0,
            0,
            out float missingHeight,
            out byte missingProfile));
        Assert.IsTrue(float.IsNegativeInfinity(missingHeight));
        Assert.AreEqual(LiquidOpticalRegistry.NoLiquidProfileId, missingProfile);
    }

    /// <summary>Verifies exact ten-texel decoding of authored dynamics and SI material properties.</summary>
    [TestMethod]
    public void TryDecodeProfileReadsDynamicsAndPhysicalProperties()
    {
        float[] lookup = new float[
            LiquidOpticalRegistry.LookupWidth
            * LiquidOpticalRegistry.LookupHeight
            * LiquidOpticalRegistry.LookupChannels];
        int offset = 7 * LiquidOpticalRegistry.LookupWidth * LiquidOpticalRegistry.LookupChannels;
        lookup[offset + 16] = 0.44f;
        lookup[offset + 17] = 0.03f;
        lookup[offset + 18] = 1.8f;
        lookup[offset + 19] = 2.4f;
        lookup[offset + 20] = 0.07f;
        lookup[offset + 21] = 0.62f;
        lookup[offset + 22] = 0.072f;
        lookup[offset + 24] = 0.5f;
        lookup[offset + 25] = 0.01f;
        lookup[offset + 26] = 0.03f;
        lookup[offset + 27] = 1.2f;
        lookup[offset + 28] = 0.8f;
        lookup[offset + 29] = 0.15f;
        lookup[offset + 32] = 998.2f;
        lookup[offset + 33] = 0.001002f;
        lookup[offset + 34] = 0.0728f;
        lookup[offset + 35] = 0.18f;

        bool decoded = LiquidSurfaceRuntime.TryDecodeProfile(
            lookup,
            7,
            out LiquidSurfaceDynamics dynamics,
            out LiquidSurfacePhysicalProperties physics);

        Assert.IsTrue(decoded);
        Assert.AreEqual(0.44f, dynamics.WindCoupling);
        Assert.AreEqual(1.8f, dynamics.WaveLength);
        Assert.AreEqual(0.8f, dynamics.BubbleBurstStrength);
        Assert.AreEqual(998.2f, physics.DensityKilogramsPerCubicMetre);
        Assert.AreEqual(0.001002f, physics.DynamicViscosityPascalSeconds);
        Assert.AreEqual(0.0728f, physics.SurfaceTensionNewtonsPerMetre);
        Assert.AreEqual(0.07f, physics.AdditionalDampingPerSecond);
        Assert.AreEqual(0.18f, physics.ResolvedWaveEnergyFraction);
    }

    /// <summary>Verifies all invalid profile classes fail closed before entering the solver.</summary>
    [TestMethod]
    public void TryDecodeProfileRejectsReservedTruncatedAndNonPhysicalRows()
    {
        Assert.IsFalse(LiquidSurfaceRuntime.TryDecodeProfile(
            [],
            LiquidOpticalRegistry.NoLiquidProfileId,
            out _,
            out _));
        Assert.IsFalse(LiquidSurfaceRuntime.TryDecodeProfile(
            new float[LiquidOpticalRegistry.LookupWidth * LiquidOpticalRegistry.LookupChannels],
            1,
            out _,
            out _));

        float[] invalid = new float[
            LiquidOpticalRegistry.LookupWidth
            * LiquidOpticalRegistry.LookupHeight
            * LiquidOpticalRegistry.LookupChannels];
        int offset = 1 * LiquidOpticalRegistry.LookupWidth * LiquidOpticalRegistry.LookupChannels;
        invalid[offset + 17] = 0.1f;
        invalid[offset + 18] = 1.0f;
        invalid[offset + 32] = 0.0f;
        invalid[offset + 33] = 0.001f;
        invalid[offset + 34] = 0.07f;
        invalid[offset + 35] = 0.5f;

        Assert.IsFalse(LiquidSurfaceRuntime.TryDecodeProfile(invalid, 1, out _, out _));
    }

    /// <summary>Verifies roof rejection and inclusive contact with the rain-height surface.</summary>
    [TestMethod]
    public void IsRainExposedUsesSnapshotOriginBoundsAndSurfaceHeight()
    {
        VoxelSceneSnapshot snapshot = CreateSnapshot(
            new byte[2 * 2 * VoxelScene.FluidSurfaceChannels],
            new byte[2 * 3 * 2 * VoxelScene.LiquidMetadataChannels],
            rainSurface: [9.0f, 10.0f, 11.0f, 12.0f]);

        Assert.IsTrue(LiquidSurfaceRuntime.IsRainExposed(in snapshot, 101, -49, 12.0f));
        Assert.IsFalse(LiquidSurfaceRuntime.IsRainExposed(in snapshot, 101, -49, 11.99f));
        Assert.IsFalse(LiquidSurfaceRuntime.IsRainExposed(in snapshot, 99, -49, 100.0f));
        Assert.IsFalse(LiquidSurfaceRuntime.IsRainExposed(in snapshot, 101, -47, 100.0f));
    }

    /// <summary>Creates the smallest complete immutable snapshot needed by runtime helper tests.</summary>
    /// <param name="fluidSurface">Four-channel column surface payload.</param>
    /// <param name="liquidMetadata">Four-channel per-voxel liquid metadata.</param>
    /// <param name="rainSurface">Optional two-by-two rain-height payload.</param>
    /// <returns>A deterministic two-by-three-by-two scene rooted at (100,10,-50).</returns>
    private static VoxelSceneSnapshot CreateSnapshot(
        byte[] fluidSurface,
        byte[] liquidMetadata,
        float[]? rainSurface = null)
    {
        return new VoxelSceneSnapshot(
            Voxels: [],
            Occupancy: [],
            Width: 2,
            Height: 3,
            Depth: 2,
            OriginX: 100,
            OriginY: 10,
            OriginZ: -50,
            Lights: [],
            Irradiance: [],
            IrradianceDirection: [],
            FluidSurface: fluidSurface,
            LiquidMetadata: liquidMetadata,
            LiquidOpticalProfileLookup: [],
            LiquidOpticalProfileCount: 0,
            SunOccupancy: [],
            RainSurface: rainSurface ?? [9.0f, 10.0f, 11.0f, 12.0f],
            SunOccupancyWidth: 0,
            SunOccupancyHeight: 0,
            SunOccupancyDepth: 0,
            SunOccupancyScale: 1,
            SunOriginX: 100,
            SunOriginY: 0,
            SunOriginZ: -50,
            RainSurfaceWidth: 2,
            RainSurfaceDepth: 2,
            FluidVoxelCount: 0,
            VisibleLiquidContainerCount: 0,
            Generation: 1,
            FluidSurfaceWidth: 2,
            FluidSurfaceDepth: 2,
            FluidSurfaceOriginX: 100,
            FluidSurfaceOriginZ: -50);
    }

    /// <summary>Writes one fluid-layer voxel into the two-by-three-by-two test ABI.</summary>
    /// <param name="metadata">Complete liquid metadata buffer.</param>
    /// <param name="localX">Voxel X.</param>
    /// <param name="localY">Voxel Y.</param>
    /// <param name="localZ">Voxel Z.</param>
    /// <param name="profileId">Liquid profile.</param>
    /// <param name="level">Vintage Story level 0..7.</param>
    private static void WriteFluidVoxel(
        byte[] metadata,
        int localX,
        int localY,
        int localZ,
        byte profileId,
        byte level)
    {
        int voxelIndex = (localZ * 3 + localY) * 2 + localX;
        int offset = voxelIndex * VoxelScene.LiquidMetadataChannels;
        metadata[offset] = profileId;
        metadata[offset + 1] = (byte)(
            VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface);
        metadata[offset + 2] = level;
    }
}
