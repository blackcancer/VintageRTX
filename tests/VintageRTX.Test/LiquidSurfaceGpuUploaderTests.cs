using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>
/// Verifies dynamic-liquid RGBA32F packing, world-grid metadata, allocation
/// reuse, rollback, and texture ownership without requiring an OpenGL context.
/// </summary>
[TestClass]
public sealed class LiquidSurfaceGpuUploaderTests
{
    /// <summary>Verifies row-major channel packing and complete shader metadata.</summary>
    [TestMethod]
    public void UploadPacksSimulationAndPublishesWorldGridMetadata()
    {
        LiquidSurfaceSimulation simulation = CreateFilledSimulation(
            originX: -12,
            originZ: 34,
            width: 2,
            depth: 2,
            cellSize: 0.5f);
        Assert.IsTrue(simulation.QueueImpulse(-11.25, 34.25, 3.0f));
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        RecordingLiquidTextureApi textureApi = new() { NextTextureId = 41 };
        using LiquidSurfaceGpuUploader uploader = new(textureApi);

        LiquidSurfaceGpuBinding binding = uploader.Upload(simulation);

        Assert.AreEqual(41, binding.TextureId);
        Assert.AreEqual(-12, binding.OriginWorldX);
        Assert.AreEqual(34, binding.OriginWorldZ);
        Assert.AreEqual(2, binding.Width);
        Assert.AreEqual(2, binding.Depth);
        Assert.AreEqual(0.5f, binding.CellSize);
        Assert.AreEqual(1L, binding.Revision);
        Assert.AreEqual(binding, uploader.Current);
        Assert.AreEqual(1, textureApi.CreateCount);
        Assert.AreEqual(1, textureApi.AllocateCount);
        Assert.AreEqual(0, textureApi.UpdateCount);

        float[] expected = new float[2 * 2 * LiquidSurfaceSimulation.GpuChannels];
        for (int z = 0; z < simulation.Depth; z++)
        {
            for (int x = 0; x < simulation.Width; x++)
            {
                LiquidSurfaceCellSample sample = simulation.GetCellSample(x, z);
                int offset = (z * simulation.Width + x) * LiquidSurfaceSimulation.GpuChannels;
                expected[offset] = sample.Height;
                expected[offset + 1] = sample.NormalX;
                expected[offset + 2] = sample.NormalZ;
                expected[offset + 3] = sample.TransientEmission;
            }
        }
        CollectionAssert.AreEqual(expected, textureApi.LastPixels!);
        Assert.AreEqual("dynamicLiquidSurface", LiquidSurfaceGpuContract.SamplerUniformName);
        Assert.AreEqual("dynamicLiquidOriginCell", LiquidSurfaceGpuContract.OriginCellUniformName);
        Assert.AreEqual("dynamicLiquidGridSize", LiquidSurfaceGpuContract.GridSizeUniformName);
    }

    /// <summary>Verifies sub-image reuse and same-handle reallocation after a grid resize.</summary>
    [TestMethod]
    public void RepeatedUploadUpdatesThenReallocatesSameTextureForNewGridSize()
    {
        RecordingLiquidTextureApi textureApi = new() { NextTextureId = 7 };
        using LiquidSurfaceGpuUploader uploader = new(textureApi);
        LiquidSurfaceSimulation first = CreateFilledSimulation(0, 0, 2, 2, 1.0f);

        LiquidSurfaceGpuBinding initial = uploader.Upload(first);
        LiquidSurfaceGpuBinding updated = uploader.Upload(first);
        LiquidSurfaceSimulation resized = CreateFilledSimulation(10, 20, 3, 1, 0.25f);
        LiquidSurfaceGpuBinding reallocated = uploader.Upload(resized);

        Assert.AreEqual(initial.TextureId, updated.TextureId);
        Assert.AreEqual(updated.TextureId, reallocated.TextureId);
        Assert.AreEqual(1L, initial.Revision);
        Assert.AreEqual(2L, updated.Revision);
        Assert.AreEqual(3L, reallocated.Revision);
        Assert.AreEqual(1, textureApi.CreateCount);
        Assert.AreEqual(2, textureApi.AllocateCount);
        Assert.AreEqual(1, textureApi.UpdateCount);
        Assert.AreEqual(3 * LiquidSurfaceSimulation.GpuChannels, textureApi.LastPixels!.Length);
        Assert.AreEqual(10, reallocated.OriginWorldX);
        Assert.AreEqual(20, reallocated.OriginWorldZ);
        Assert.AreEqual(0.25f, reallocated.CellSize);
    }

    /// <summary>Verifies packing and texture submission expose separate non-allocating timings.</summary>
    [TestMethod]
    public void UploadSeparatesPackingFromTextureDriverTiming()
    {
        RecordingLiquidTextureApi textureApi = new()
        {
            NextTextureId = 17,
            DriverSpinWaitIterations = 100_000
        };
        using LiquidSurfaceGpuUploader uploader = new(textureApi);

        uploader.Upload(CreateFilledSimulation(0, 0, 8, 8, 0.5f));

        Assert.IsTrue(uploader.LastPackingElapsedTicks > 0);
        Assert.IsTrue(uploader.LastDriverUploadElapsedTicks > 0);
    }

    /// <summary>Verifies failed allocation cleanup, retry, and failed update state preservation.</summary>
    [TestMethod]
    public void UploadFailureRollsBackAllocationAndPreservesLastGoodUpdateBinding()
    {
        RecordingLiquidTextureApi textureApi = new()
        {
            NextTextureId = 50,
            ThrowOnAllocate = true,
            ThrowOnDelete = true
        };
        using LiquidSurfaceGpuUploader uploader = new(textureApi);
        LiquidSurfaceSimulation simulation = CreateFilledSimulation(1, 2, 2, 1, 1.0f);

        InvalidOperationException allocationError = Assert.ThrowsException<InvalidOperationException>(
            () => uploader.Upload(simulation));
        StringAssert.Contains(allocationError.Message, "allocate");
        Assert.AreEqual(default, uploader.Current);
        Assert.AreEqual(1, textureApi.DeleteCount);

        textureApi.ThrowOnAllocate = false;
        textureApi.ThrowOnDelete = false;
        LiquidSurfaceGpuBinding recovered = uploader.Upload(simulation);
        Assert.AreEqual(51, recovered.TextureId);
        Assert.AreEqual(1L, recovered.Revision);

        textureApi.ThrowOnUpdate = true;
        InvalidOperationException updateError = Assert.ThrowsException<InvalidOperationException>(
            () => uploader.Upload(simulation));
        StringAssert.Contains(updateError.Message, "update");
        Assert.AreEqual(recovered, uploader.Current);
        Assert.AreEqual(1, textureApi.DeleteCount);
    }

    /// <summary>Verifies invalid handles, arguments, disposal, and checked size guards.</summary>
    [TestMethod]
    public void LifecycleRejectsInvalidInputsAndDeletesOwnedTextureExactlyOnce()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => new LiquidSurfaceGpuUploader(null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidSurfaceGpuUploader.RequiredFloatCount(0, 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidSurfaceGpuUploader.RequiredFloatCount(1, 0));
        Assert.ThrowsException<OverflowException>(
            () => LiquidSurfaceGpuUploader.RequiredFloatCount(int.MaxValue, 2));

        using (LiquidSurfaceGpuUploader unusedProductionUploader = new())
        {
            Assert.AreEqual(default, unusedProductionUploader.Current);
        }

        RecordingLiquidTextureApi invalidApi = new() { NextTextureId = 0 };
        using LiquidSurfaceGpuUploader invalidUploader = new(invalidApi);
        Assert.ThrowsException<InvalidOperationException>(
            () => invalidUploader.Upload(CreateFilledSimulation(0, 0, 1, 1, 1.0f)));
        Assert.AreEqual(0, invalidApi.DeleteCount);

        RecordingLiquidTextureApi textureApi = new() { NextTextureId = 9 };
        LiquidSurfaceGpuUploader uploader = new(textureApi);
        Assert.ThrowsException<ArgumentNullException>(() => uploader.Upload(null!));
        uploader.Upload(CreateFilledSimulation(0, 0, 1, 1, 1.0f));
        uploader.Dispose();
        uploader.Dispose();
        Assert.AreEqual(1, textureApi.DeleteCount);
        Assert.AreEqual(9, textureApi.LastDeletedTexture);
        Assert.AreEqual(default, uploader.Current);
        Assert.ThrowsException<ObjectDisposedException>(
            () => uploader.Upload(CreateFilledSimulation(0, 0, 1, 1, 1.0f)));
    }

    /// <summary>Verifies that deletion failure still invalidates local ownership.</summary>
    [TestMethod]
    public void DisposeInvalidatesBindingBeforeDeleteFailureEscapes()
    {
        RecordingLiquidTextureApi textureApi = new() { NextTextureId = 19 };
        LiquidSurfaceGpuUploader uploader = new(textureApi);
        uploader.Upload(CreateFilledSimulation(0, 0, 1, 1, 1.0f));
        textureApi.ThrowOnDelete = true;

        Assert.ThrowsException<InvalidOperationException>(() => uploader.Dispose());
        Assert.AreEqual(default, uploader.Current);
        Assert.AreEqual(1, textureApi.DeleteCount);
        uploader.Dispose();
        Assert.AreEqual(1, textureApi.DeleteCount);
    }

    /// <summary>Creates a fully active deterministic grid with non-zero wave response.</summary>
    /// <param name="originX">Grid minimum world X.</param>
    /// <param name="originZ">Grid minimum world Z.</param>
    /// <param name="width">Cell count along X.</param>
    /// <param name="depth">Cell count along Z.</param>
    /// <param name="cellSize">Cell edge length in blocks.</param>
    /// <returns>Configured simulation.</returns>
    private static LiquidSurfaceSimulation CreateFilledSimulation(
        int originX,
        int originZ,
        int width,
        int depth,
        float cellSize)
    {
        LiquidSurfaceSimulation simulation = new(
            originX,
            originZ,
            width,
            depth,
            cellSize);
        LiquidSurfaceDynamics dynamics = new(
            WindCoupling: 0.4f,
            WaveAmplitude: 0.12f,
            WaveLength: 2.5f,
            WaveSpeed: 2.0f,
            Damping: 0.08f,
            ImpactResponse: 1.0f,
            SurfaceTension: 0.1f,
            BubbleRate: 0.0f,
            BubbleRadiusMinimum: 0.0f,
            BubbleRadiusMaximum: 0.0f,
            BubbleRiseDuration: 0.0f,
            BubbleBurstStrength: 0.0f,
            BubbleEmissionBoost: 0.0f);
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                simulation.SetSurfaceCell(
                    x,
                    z,
                    worldSurfaceY: 100.0f,
                    profileId: 1,
                    in dynamics,
                    rainExposed: true);
            }
        }
        return simulation;
    }

    /// <summary>In-memory texture API recording payloads and deterministic failures.</summary>
    private sealed class RecordingLiquidTextureApi : ILiquidSurfaceTextureApi
    {
        /// <summary>Gets or sets the next handle returned by creation.</summary>
        internal int NextTextureId { get; set; } = 1;
        /// <summary>Gets or sets whether allocation throws.</summary>
        internal bool ThrowOnAllocate { get; set; }
        /// <summary>Gets or sets whether update throws.</summary>
        internal bool ThrowOnUpdate { get; set; }
        /// <summary>Gets or sets whether deletion throws after being recorded.</summary>
        internal bool ThrowOnDelete { get; set; }
        /// <summary>Gets creation call count.</summary>
        internal int CreateCount { get; private set; }
        /// <summary>Gets allocation call count.</summary>
        internal int AllocateCount { get; private set; }
        /// <summary>Gets sub-image update call count.</summary>
        internal int UpdateCount { get; private set; }
        /// <summary>Gets deletion call count.</summary>
        internal int DeleteCount { get; private set; }
        /// <summary>Gets the last deleted texture handle.</summary>
        internal int LastDeletedTexture { get; private set; }
        /// <summary>Gets a detached copy of the last uploaded pixels.</summary>
        internal float[]? LastPixels { get; private set; }
        /// <summary>Gets or sets deterministic driver work used by timing tests.</summary>
        internal int DriverSpinWaitIterations { get; set; }

        /// <inheritdoc />
        public int CreateTexture()
        {
            CreateCount++;
            return NextTextureId++;
        }

        /// <inheritdoc />
        public void AllocateRgba32Float(int textureId, int width, int depth, float[] pixels)
        {
            AllocateCount++;
            Thread.SpinWait(DriverSpinWaitIterations);
            if (ThrowOnAllocate)
            {
                throw new InvalidOperationException("allocate failure");
            }
            LastPixels = pixels.ToArray();
        }

        /// <inheritdoc />
        public void UpdateRgba32Float(int textureId, int width, int depth, float[] pixels)
        {
            UpdateCount++;
            Thread.SpinWait(DriverSpinWaitIterations);
            if (ThrowOnUpdate)
            {
                throw new InvalidOperationException("update failure");
            }
            LastPixels = pixels.ToArray();
        }

        /// <inheritdoc />
        public void DeleteTexture(int textureId)
        {
            DeleteCount++;
            LastDeletedTexture = textureId;
            if (ThrowOnDelete)
            {
                throw new InvalidOperationException("delete failure");
            }
        }
    }
}
