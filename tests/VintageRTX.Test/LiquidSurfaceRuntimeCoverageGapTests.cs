using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Drives the live liquid runtime against in-memory Vintage Story and texture interfaces so its
/// configuration, bounded projectile queue, entity observations, telemetry, and disposal are real.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiquidSurfaceRuntimeCoverageGapTests
{
    /// <summary>
    /// Exercises the complete renderer update lifecycle, including topology reuse, a physically
    /// accepted arrow entry, fixed queue overflow, no-op frames, and the 300-frame timing report.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void RuntimeUpdateConfiguresConsumesArrowAndReportsBoundedTelemetry()
    {
        string? previousRunId = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "liquid-runtime-coverage");
        RuntimeFixture fixture = new();
        RecordingTextureApi textureApi = new();
        LiquidSurfaceGpuUploader uploader = new(textureApi);
        LiquidSurfaceRuntime runtime = new(fixture.Api, uploader);
        try
        {
            Assert.AreEqual(default, runtime.Current);
            Assert.IsNull(runtime.Simulation);
            Assert.AreEqual(default, runtime.CurrentForcing);
            Assert.AreEqual(0, runtime.TotalDroppedItemImpactCount);
            Assert.AreEqual(0.0f, runtime.LastAppliedImpactPeakDisplacement);
            Assert.AreEqual(default, runtime.LastDroppedItemImpactDiagnostic);
            Assert.AreEqual(default, runtime.LastSubgridImpactDiagnostic);
            Assert.AreEqual(0, runtime.TotalSubgridImpactCount);
            Assert.AreEqual(0, runtime.TotalProjectileImpactCount);
            Assert.AreEqual(default, runtime.LastProjectileImpactDiagnostic);
            Assert.AreEqual(0, runtime.WriteSubgridImpactsAfter(0, []));
            Assert.IsFalse(runtime.TryGetNearestSurfaceWorldY(0.5, 0.5, out float unavailableY));
            Assert.AreEqual(0.0f, unavailableY);

            VoxelSceneSnapshot unavailable = CreateSnapshot(generation: 0, activeSurface: true);
            Assert.AreEqual(default, runtime.Update(0.1f, in unavailable, voxelTextureReady: true));
            VoxelSceneSnapshot active = CreateSnapshot(generation: 1, activeSurface: true);
            Assert.AreEqual(default, runtime.Update(0.1f, in active, voxelTextureReady: false));

            fixture.Entities = Enumerable.Range(0, 40)
                .Select(index => CreateGenericEntity(9000 + index))
                .ToArray();
            LiquidSurfaceGpuBinding initial = runtime.Update(
                LiquidSurfaceSimulation.FixedStepSeconds,
                in active,
                voxelTextureReady: true);
            Assert.AreEqual(71, initial.TextureId);
            Assert.AreEqual(1L, initial.Revision);
            Assert.IsNotNull(runtime.Simulation);
            Assert.IsTrue(runtime.TryGetNearestSurfaceWorldY(0.5, 0.5, out float surfaceY));
            Assert.AreEqual(0.875f, surfaceY, 1.0e-6f);

            fixture.Entities = [];
            LiquidSurfaceGpuBinding unchanged = runtime.Update(0.0f, in active, true);
            Assert.AreEqual(initial, unchanged);
            fixture.ReturnNullEntities = true;
            runtime.Update(1.0f / 30.0f, in active, true);
            fixture.ReturnNullEntities = false;

            EntityItem droppedItem = new()
            {
                Alive = true,
                EntityId = 9051,
                Itemstack = new ItemStack(new Item(), 1)
            };
            droppedItem.Pos.SetPos(0.50, 1.30, 0.50);
            droppedItem.Pos.Motion.Set(0.02, -0.04, 0.01);
            fixture.Entities = [droppedItem];
            runtime.Update(1.0f / 30.0f, in active, true);
            droppedItem.Pos.SetPos(0.52, 0.80, 0.51);
            droppedItem.Pos.Motion.Set(0.01, -0.01, 0.01);
            droppedItem.FeetInLiquid = true;
            runtime.Update(1.0f / 30.0f, in active, true);
            Assert.AreEqual(1, runtime.TotalDroppedItemImpactCount);
            LiquidSurfaceImpactDiagnostic dropped = runtime.LastDroppedItemImpactDiagnostic;
            Assert.IsTrue(dropped.PeakDisplacement > 0.0f);
            Assert.IsTrue(dropped.AdditionalDampingPerSecond >= 0.0f);
            fixture.Entities = [];

            LiquidProjectileCollisionSample arrow = new(
                EntityId: 9101,
                SurfaceClass: LiquidEntitySurfaceClass.Projectile,
                WorldX: 0.55,
                WorldY: 0.80,
                WorldZ: 0.55,
                PreviousWorldX: 0.45,
                PreviousWorldY: 1.05,
                PreviousWorldZ: 0.50,
                IncidentMotionX: 0.20f,
                IncidentMotionY: -0.30f,
                IncidentMotionZ: 0.10f,
                OutgoingMotionX: 0.18f,
                OutgoingMotionY: -0.05f,
                OutgoingMotionZ: 0.08f,
                MassKilograms: LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
                MotionSamplePeriodSeconds: LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds,
                IsServerAuthoritative: true);
            for (int index = 0; index < 65; index++)
            {
                runtime.ObserveProjectileLiquidCollision(arrow);
            }
            Assert.AreEqual(1, runtime.DroppedProjectileCollisionCount);
            runtime.Update(LiquidSurfaceSimulation.FixedStepSeconds * 2.0f, in active, true);
            Assert.AreEqual(1, runtime.TotalProjectileImpactCount);
            LiquidProjectileImpactDiagnostic projectile = runtime.LastProjectileImpactDiagnostic;
            Assert.AreEqual(LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms, projectile.MassKilograms);
            Assert.AreEqual(12.0f, projectile.IncidentVelocityXMetresPerSecond, 1.0e-4f);
            Assert.AreEqual(6.0f, projectile.IncidentVelocityZMetresPerSecond, 1.0e-4f);
            Assert.AreEqual(10.8f, projectile.OutgoingVelocityXMetresPerSecond, 1.0e-4f);
            Assert.AreEqual(4.8f, projectile.OutgoingVelocityZMetresPerSecond, 1.0e-4f);
            Assert.IsTrue(projectile.NearInterfaceEnergyJoules >= 0.0f);
            Assert.IsTrue(projectile.EnergyJoules > 0.0f);

            Span<LiquidSurfaceSubgridImpactDiagnostic> packets =
                stackalloc LiquidSurfaceSubgridImpactDiagnostic[1];
            Assert.AreEqual(1, runtime.WriteSubgridImpactsAfter(0, packets));
            LiquidSurfaceSubgridImpactDiagnostic packet = packets[0];
            Assert.AreEqual(LiquidSurfaceImpulseKind.GenericEntry, packet.ImpulseKind);
            Assert.IsTrue(packet.CellX >= 0);
            Assert.IsTrue(packet.CellZ >= 0);
            Assert.IsTrue(float.IsFinite(packet.TextureU));
            Assert.IsTrue(float.IsFinite(packet.TextureV));
            Assert.IsTrue(float.IsFinite(packet.UploadedHeight));
            Assert.IsTrue(float.IsFinite(packet.UploadedNormalX));
            Assert.IsTrue(float.IsFinite(packet.UploadedNormalZ));
            Assert.IsTrue(float.IsFinite(packet.ImpactVelocityYMetresPerSecond));

            VoxelSceneSnapshot cleared = CreateSnapshot(generation: 2, activeSurface: false);
            runtime.Update(float.NaN, in cleared, true);
            Assert.IsFalse(runtime.TryGetNearestSurfaceWorldY(0.5, 0.5, out _));

            VoxelSceneSnapshot restored = CreateSnapshot(generation: 3, activeSurface: true);
            runtime.Update(0.0f, in restored, true);
            for (int frame = 0; frame < 305; frame++)
            {
                runtime.Update(0.0f, in restored, true);
            }

            Assert.IsTrue(fixture.Logs.Any(log => log.Contains("Projectile surface impact applied", StringComparison.Ordinal)));
            Assert.IsTrue(fixture.Logs.Any(log => log.Contains("Dropped-item surface impact applied", StringComparison.Ordinal)));
            Assert.IsTrue(fixture.Logs.Any(log => log.Contains("Liquid CPU stages", StringComparison.Ordinal)));
            Assert.IsTrue(textureApi.UpdateCount > 0);
        }
        finally
        {
            runtime.Dispose();
            runtime.Dispose();
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previousRunId);
        }

        VoxelSceneSnapshot disposedSnapshot = CreateSnapshot(generation: 4, activeSurface: true);
        Assert.ThrowsException<ObjectDisposedException>(
            () => runtime.Update(0.0f, in disposedSnapshot, true));
        Assert.AreEqual(1, textureApi.DeleteCount);
    }

    /// <summary>Verifies constructor guards and the production uploader's no-texture disposal path.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void RuntimeConstructorsRejectNullAndProductionUploaderCanDisposeUnused()
    {
        RuntimeFixture fixture = new();
        RecordingTextureApi textureApi = new();
        using LiquidSurfaceGpuUploader uploader = new(textureApi);
        Assert.ThrowsException<ArgumentNullException>(() => new LiquidSurfaceRuntime(null!, uploader));
        Assert.ThrowsException<ArgumentNullException>(() => new LiquidSurfaceRuntime(fixture.Api, null!));

        LiquidSurfaceRuntime production = new(fixture.Api);
        Assert.AreEqual(default, production.Current);
        production.Dispose();
        production.Dispose();
    }

    /// <summary>Creates one finite generic entity for bounded observation-array growth.</summary>
    /// <param name="entityId">Entity identity.</param>
    /// <returns>Alive detached entity.</returns>
    private static Entity CreateGenericEntity(long entityId)
    {
        EntityPlayer entity = new() { EntityId = entityId, Alive = true };
        entity.Pos.SetPos(0.5, 1.5, 0.5);
        return entity;
    }

    /// <summary>Creates a one-cell snapshot with a valid physical optical row.</summary>
    /// <param name="generation">Immutable scene generation.</param>
    /// <param name="activeSurface">Whether the cell contains an authored liquid surface.</param>
    /// <returns>Complete deterministic snapshot.</returns>
    private static VoxelSceneSnapshot CreateSnapshot(int generation, bool activeSurface)
    {
        byte[] fluidSurface = new byte[VoxelScene.FluidSurfaceChannels];
        byte[] liquidMetadata = new byte[VoxelScene.LiquidMetadataChannels];
        if (activeSurface)
        {
            fluidSurface[0] = 1;
            fluidSurface[1] = 1;
            fluidSurface[2] = 7;
            liquidMetadata[0] = 1;
            liquidMetadata[1] = (byte)(VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface);
            liquidMetadata[2] = 7;
        }

        float[] lookup = new float[
            LiquidOpticalRegistry.LookupWidth
            * LiquidOpticalRegistry.LookupHeight
            * LiquidOpticalRegistry.LookupChannels];
        int offset = LiquidOpticalRegistry.LookupWidth * LiquidOpticalRegistry.LookupChannels;
        lookup[offset + 16] = 0.4f;
        lookup[offset + 17] = 0.03f;
        lookup[offset + 18] = 2.0f;
        lookup[offset + 19] = 1.5f;
        lookup[offset + 20] = 0.07f;
        lookup[offset + 21] = 1.0f;
        lookup[offset + 22] = 0.072f;
        lookup[offset + 24] = 0.0f;
        lookup[offset + 25] = 0.0f;
        lookup[offset + 26] = 0.0f;
        lookup[offset + 27] = 0.0f;
        lookup[offset + 28] = 0.0f;
        lookup[offset + 29] = 0.0f;
        lookup[offset + 32] = 998.0f;
        lookup[offset + 33] = 0.001f;
        lookup[offset + 34] = 0.072f;
        lookup[offset + 35] = 0.50f;

        return new VoxelSceneSnapshot(
            Voxels: [],
            Occupancy: [],
            Width: 1,
            Height: 1,
            Depth: 1,
            OriginX: 0,
            OriginY: 0,
            OriginZ: 0,
            Lights: [],
            Irradiance: [],
            IrradianceDirection: [],
            FluidSurface: fluidSurface,
            LiquidMetadata: liquidMetadata,
            LiquidOpticalProfileLookup: lookup,
            LiquidOpticalProfileCount: 1,
            SunOccupancy: [],
            RainSurface: [0.0f],
            SunOccupancyWidth: 0,
            SunOccupancyHeight: 0,
            SunOccupancyDepth: 0,
            SunOccupancyScale: 1,
            SunTraceDistance: 1,
            SunOriginX: 0,
            SunOriginY: 0,
            SunOriginZ: 0,
            RainSurfaceWidth: 1,
            RainSurfaceDepth: 1,
            RainSurfaceOriginX: 0,
            RainSurfaceOriginZ: 0,
            FluidVoxelCount: activeSurface ? 1 : 0,
            VisibleLiquidContainerCount: 0,
            Generation: generation,
            FluidSurfaceWidth: 1,
            FluidSurfaceDepth: 1,
            FluidSurfaceOriginX: 0,
            FluidSurfaceOriginZ: 0);
    }

    /// <summary>In-memory Vintage Story client surface consumed by the runtime.</summary>
    private sealed class RuntimeFixture
    {
        /// <summary>Creates all interface proxies and the camera entity.</summary>
        internal RuntimeFixture()
        {
            CameraEntity = new EntityPlayer
            {
                Alive = true,
                CameraPos = new Vec3d(0.5, 1.5, 0.5)
            };
            CameraEntity.Pos.SetPos(0.5, 1.0, 0.5);
            ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
            {
                if (method.Name == nameof(ILogger.Notification))
                {
                    Logs.Add(arguments?[0]?.ToString() ?? string.Empty);
                }
                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            });
            IBlockAccessor blockAccessor = RuntimeCoverageDispatchProxy.Create<IBlockAccessor>(
                (method, _) => method.Name switch
                {
                    "GetWindSpeedAt" => Wind,
                    "GetClimateAt" => Climate,
                    "GetRainMapHeightAt" => 0,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
            IClientPlayer player = RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) =>
                method.Name == "get_Entity"
                    ? CameraEntity
                    : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>(
                (method, _) => method.Name switch
                {
                    "get_Player" => player,
                    "get_BlockAccessor" => blockAccessor,
                    "GetEntitiesAround" => ReturnNullEntities ? null : Entities,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
            Api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_World" => world,
                "get_Logger" => logger,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        }

        /// <summary>Gets the detached client API.</summary>
        internal ICoreClientAPI Api { get; }
        /// <summary>Gets the player entity used as a camera source.</summary>
        internal EntityPlayer CameraEntity { get; }
        /// <summary>Gets mutable climate returned by the block accessor.</summary>
        internal ClimateCondition Climate { get; } = new() { Rainfall = 0.25f };
        /// <summary>Gets mutable wind returned by the block accessor.</summary>
        internal Vec3d Wind { get; } = new(0.1, 0.0, -0.2);
        /// <summary>Gets recorded notification format strings.</summary>
        internal List<string> Logs { get; } = [];
        /// <summary>Gets or sets nearby entities.</summary>
        internal Entity[] Entities { get; set; } = [];
        /// <summary>Gets or sets whether the world query returns null.</summary>
        internal bool ReturnNullEntities { get; set; }
    }

    /// <summary>Records allocation/update ownership without an OpenGL context.</summary>
    private sealed class RecordingTextureApi : ILiquidSurfaceTextureApi
    {
        /// <summary>Gets update submission count.</summary>
        internal int UpdateCount { get; private set; }
        /// <summary>Gets deletion count.</summary>
        internal int DeleteCount { get; private set; }

        /// <inheritdoc />
        public int CreateTexture() => 71;

        /// <inheritdoc />
        public void AllocateRgba32Float(int textureId, int width, int depth, float[] pixels)
        {
        }

        /// <inheritdoc />
        public void UpdateRgba32Float(int textureId, int width, int depth, float[] pixels)
        {
            UpdateCount++;
        }

        /// <inheritdoc />
        public void DeleteTexture(int textureId)
        {
            DeleteCount++;
        }
    }
}
