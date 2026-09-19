using System.Reflection;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>World-facing coverage for geometry, liquid, light, dirty-update, and lifecycle paths.</summary>
[TestClass]
[DoNotParallelize]
public sealed class VoxelSceneCoverageDeepTests
{
    /// <summary>
    /// Verifies the geometry Classification Distinguishes Cube Crossed Planes Dynamic And Missing Meshes regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GeometryClassificationDistinguishesCubeCrossedPlanesDynamicAndMissingMeshes()
    {
        FixtureBlock cube = Block(1, "game:cube", EnumBlockMaterial.Stone);
        FixtureBlock grass = Block(2, "game:grass", EnumBlockMaterial.Plant);
        grass.RenderPass = EnumChunkRenderPass.OpaqueNoCull;
        grass.Collision = null;
        FixtureBlock anvil = Block(3, "game:anvil", EnumBlockMaterial.Metal);
        anvil.EntityClass = "BlockEntityAnvil";
        FixtureBlock missing = Block(4, "game:missing", EnumBlockMaterial.Plant);
        missing.RenderPass = EnumChunkRenderPass.OpaqueNoCull;
        missing.Collision = null;
        using SceneFixture fixture = new(
            meshes: new Dictionary<int, MeshData?>
            {
                [1] = CubeMesh(),
                [2] = CrossedPlanes(),
                [3] = QuadMesh(0.2f),
                [4] = null
            },
            throwMeshForBlockId: 4);

        CachedBlockOccupancy cubeOccupancy = fixture.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", cube);
        Assert.AreEqual(BlockGeometryKind.FullCubeStatic, cubeOccupancy.GeometryKind);
        Assert.AreEqual(ulong.MaxValue, cubeOccupancy.Mask);
        CachedBlockOccupancy plantOccupancy = fixture.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", grass);
        Assert.AreEqual(BlockGeometryKind.StaticComplex, plantOccupancy.GeometryKind);
        Assert.AreNotEqual(0UL, plantOccupancy.Mask);
        Assert.AreNotEqual(ulong.MaxValue, plantOccupancy.Mask);
        CachedBlockOccupancy dynamicOccupancy = fixture.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", anvil);
        Assert.AreEqual(BlockGeometryKind.DynamicInstance, dynamicOccupancy.GeometryKind);
        CachedBlockOccupancy failed = fixture.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", missing);
        Assert.AreEqual(BlockGeometryKind.Unknown, failed.GeometryKind);
        Assert.AreEqual(failed, fixture.Invoke<CachedBlockOccupancy>("GetCachedBlockOccupancy", missing));

        Assert.AreEqual(ulong.MaxValue, fixture.Invoke<ulong>(
            "ResolveSunShadowMask", cube, new BlockPos(0, 0, 0)));
        ulong plantMask = fixture.Invoke<ulong>(
            "ResolveSunShadowMask", grass, new BlockPos(0, 0, 0));
        Assert.IsTrue(plantMask is > 0 and < ulong.MaxValue);
        Assert.AreEqual(0UL, fixture.Invoke<ulong>(
            "ResolveSunShadowMask", missing, new BlockPos(0, 0, 0)));
    }

    /// <summary>
    /// Verifies that opaque-pass crossed vegetation follows its texture alpha while canonical cubes
    /// and entity-backed/chiseled meshes retain their existing geometry paths.
    /// </summary>
    [TestMethod]
    public void OpaqueCrossedPlanesUseFourAlphaTestedTrianglesWithoutCuttingCubesOrChisels()
    {
        TextureAtlasPosition atlasPosition = new()
        {
            atlasTextureId = 92,
            atlasNumber = 0,
            x1 = 0,
            y1 = 0,
            x2 = 1,
            y2 = 1
        };
        AssetLocation alphaSource = new("game", "textures/block/cross-alpha.png");
        CompositeTexture crossTexture = new(new AssetLocation("game:block/cross-alpha"))
        {
            Baked = new BakedCompositeTexture { TextureSubId = 0 }
        };
        Dictionary<string, CompositeTexture> textures = new()
        {
            ["all"] = crossTexture
        };

        MeshData crossedMesh = CrossedPlanes();
        crossedMesh.RenderPassesAndExtraBits =
        [
            (short)EnumChunkRenderPass.Opaque,
            (short)EnumChunkRenderPass.Opaque
        ];
        crossedMesh.Uv =
        [
            0, 0, 1, 0, 1, 1, 0, 1,
            0, 0, 1, 0, 1, 1, 0, 1
        ];
        crossedMesh.TextureIndices = [0, 0];
        crossedMesh.TextureIds = [92];

        FixtureBlock crossed = Block(5, "game:cross-alpha", EnumBlockMaterial.Plant);
        crossed.DrawType = EnumDrawType.Cross;
        crossed.Collision = null;
        crossed.Textures = textures;

        // Deliberately attach the same broad draw flag and alpha asset to a
        // canonical cube: topology must keep the full-cube fast path authoritative.
        FixtureBlock cube = Block(6, "game:cross-flagged-cube", EnumBlockMaterial.Stone);
        cube.DrawType = EnumDrawType.Cross;
        cube.Textures = textures;

        // Entity-backed shapes are resolved from their per-position tessellation.
        // A coincidental Cross flag on their default mesh must not pre-cut a chisel.
        FixtureBlock chisel = Block(7, "game:cross-flagged-chisel", EnumBlockMaterial.Stone);
        chisel.DrawType = EnumDrawType.Cross;
        chisel.EntityClass = "BlockEntityChisel";
        chisel.Textures = textures;

        using SceneFixture fixture = new(
            meshes: new Dictionary<int, MeshData?>
            {
                [crossed.Id] = crossedMesh,
                [cube.Id] = CubeMesh(),
                [chisel.Id] = crossedMesh
            },
            atlasPositions: [atlasPosition],
            assets: new Dictionary<AssetLocation, IAsset>
            {
                [alphaSource] = BitmapAsset(
                    new SKColor(20, 30, 40, 255),
                    new SKColor(20, 30, 40, 0))
            });

        CachedBlockOccupancy crossedOccupancy = fixture.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", crossed);
        CachedBlockOccupancy cubeOccupancy = fixture.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", cube);
        CachedBlockOccupancy chiselOccupancy = fixture.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", chisel);

        Assert.AreEqual(4, crossedOccupancy.AlphaTestedTriangles);
        Assert.AreEqual(BlockGeometryKind.StaticComplex, crossedOccupancy.GeometryKind);
        Assert.IsTrue(System.Numerics.BitOperations.PopCount(crossedOccupancy.Mask) > 0);
        Assert.IsTrue(
            System.Numerics.BitOperations.PopCount(crossedOccupancy.Mask)
                < System.Numerics.BitOperations.PopCount(chiselOccupancy.Mask),
            "Texture alpha must remove covered subvoxels from the two broad planes.");
        Assert.IsTrue(VoxelScene.MeasureCasterGeometry(
            crossedOccupancy,
            isCrossedPlane: true,
            fineMask: null).CrossedPlanePartial);

        Assert.AreEqual(BlockGeometryKind.FullCubeStatic, cubeOccupancy.GeometryKind);
        Assert.AreEqual(ulong.MaxValue, cubeOccupancy.Mask);
        Assert.AreEqual(0, cubeOccupancy.AlphaTestedTriangles);
        Assert.AreEqual(BlockGeometryKind.DynamicInstance, chiselOccupancy.GeometryKind);
        Assert.AreEqual(0, chiselOccupancy.AlphaTestedTriangles);

        byte[] cutoutCaster = fixture.Invoke<byte[]>(
            "BuildLightCasterMask", crossed, crossedMesh);
        byte[] uncutInstanceCaster = fixture.Invoke<byte[]>(
            "BuildLightCasterMask", chisel, crossedMesh);
        Assert.IsTrue(
            cutoutCaster.Count(value => value != 0)
                < uncutInstanceCaster.Count(value => value != 0),
            "Fine caster masks must apply the same Cross texture-alpha verdict.");
    }

    /// <summary>
    /// Verifies the world Sampling Writes Cube Fluid Material Rain And Coarse Sun Coverage regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WorldSamplingWritesCubeFluidMaterialRainAndCoarseSunCoverage()
    {
        FixtureBlock cube = Block(10, "game:stone", EnumBlockMaterial.Stone);
        FixtureBlock water = Block(11, "game:water-still-7", EnumBlockMaterial.Water);
        water.LiquidCode = "water";
        water.LiquidLevel = 7;
        water.Collision = null;
        using SceneFixture fixture = new(meshes: new Dictionary<int, MeshData?> { [10] = CubeMesh() });
        fixture.SetSolid(0, 0, 0, cube);
        fixture.SetFluid(1, 0, 0, water);
        fixture.RainHeight = 37;
        fixture.SetOrigins(0, 0, 0, 0, 0, 0);

        fixture.Invoke<object>("SampleVoxel", 0);
        fixture.Invoke<object>("SampleVoxel", 1);
        fixture.Invoke<object>("SampleVoxel", 2);
        byte[] materials = fixture.Field<byte[]>("buildVoxels");
        byte[] occupancy = fixture.Field<byte[]>("buildOccupancy");
        byte[] liquid = fixture.Field<byte[]>("buildLiquidMetadata");
        byte[] surface = fixture.Field<byte[]>("buildFluidSurface");
        int fluidSurfaceOffset = (32 * VoxelScene.FluidSurfaceWidth + 33)
            * VoxelScene.FluidSurfaceChannels;
        Assert.AreEqual(128, materials[3]);
        Assert.AreEqual(64, materials[7]);
        Assert.IsTrue(occupancy.Take(4).Any(value => value == 255));
        Assert.AreNotEqual(0, liquid[4 + 1]);
        Assert.AreEqual(7, liquid[4 + 2]);
        Assert.AreEqual(1, surface[fluidSurfaceOffset]);

        fixture.Invoke<object>("SampleRainSurfaceCell", 0);
        Assert.AreEqual(37.0f, fixture.Field<float[]>("buildRainSurface")[0]);
        fixture.SetSolid(0, 0, 0, cube);
        ulong cubeSunMask = fixture.Invoke<ulong>("SampleSunOccupancyMask", 0, 0, 0);
        Assert.AreEqual(8, System.Numerics.BitOperations.PopCount(cubeSunMask));
        fixture.Invoke<object>("SampleSunOccupancyCell", 0);
        Assert.AreEqual(cubeSunMask, fixture.Field<ulong[]>("buildSunOccupancy")[0]);
    }

    /// <summary>
    /// Verifies the incremental Updates Coalesce Material Occupancy Liquid Sun And Rain Payloads regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void IncrementalUpdatesCoalesceMaterialOccupancyLiquidSunAndRainPayloads()
    {
        FixtureBlock cube = Block(20, "game:metal", EnumBlockMaterial.Metal);
        using SceneFixture fixture = new(meshes: new Dictionary<int, MeshData?> { [20] = CubeMesh() });
        fixture.SetSolid(2, 3, 4, cube);
        fixture.RainHeight = 52;
        fixture.SetOrigins(0, 0, 0, 0, 0, 0);
        fixture.SetField("generation", 1);

        fixture.Invoke<object>("UpdateReadyVoxel", -1, 0, 0);
        fixture.Invoke<object>("UpdateReadyVoxel", 2, 3, 4);
        fixture.Invoke<object>("UpdateReadyVoxel", 2, 3, 4);
        List<VoxelSceneBlockUpdate> blockUpdates = fixture.Field<List<VoxelSceneBlockUpdate>>(
            "pendingBlockUpdates");
        Assert.AreEqual(1, blockUpdates.Count);
        Assert.AreEqual(192, blockUpdates[0].Material[3]);
        Assert.IsTrue(blockUpdates[0].Occupancy.Any(value => value == 255));

        fixture.Invoke<object>("UpdateReadyRainSurface", -1, -1);
        fixture.Invoke<object>("UpdateReadyRainSurface", 2, 4);
        fixture.Invoke<object>("UpdateReadyRainSurface", 2, 4);
        Assert.AreEqual(1, fixture.Field<List<VoxelRainSurfaceUpdate>>("pendingRainSurfaceUpdates").Count);
        fixture.Invoke<object>("UpdateReadySunOccupancy", -1, 0, 0);
        fixture.Invoke<object>("UpdateReadySunOccupancy", 2, 3, 4);
        fixture.Invoke<object>("UpdateReadySunOccupancy", 2, 3, 4);
        Assert.AreEqual(1, fixture.Field<List<VoxelSunOccupancyUpdate>>("pendingSunOccupancyUpdates").Count);

        ConcurrentDictionary<(int X, int Y, int Z), byte> dirty = fixture.Field<ConcurrentDictionary<(int X, int Y, int Z), byte>>("dirtyBlocks");
        fixture.SetField("generation", 0);
        dirty.TryAdd((63, 0, 0), 0);
        fixture.Invoke<object>("ProcessDirtyBlocks");
        Assert.AreEqual(1, dirty.Count);
        dirty.Clear();
        fixture.SetField("generation", 1);
        for (int index = 0; index < 20; index++)
        {
            dirty.TryAdd((index, 0, 0), 0);
        }
        fixture.Invoke<object>("ProcessDirtyBlocks");
        Assert.AreEqual(4, dirty.Count);
        fixture.Invoke<object>("ProcessDirtyBlocks");
        Assert.AreEqual(0, dirty.Count);
        fixture.Invoke<object>("ProcessDirtyBlocks");
    }

    /// <summary>
    /// Verifies the block Change Lifecycle Separates Outside Dirty And Emissive Rebuild Paths regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void BlockChangeLifecycleCoalescesSameDimensionEditsAndRefreshesEmitters()
    {
        FixtureBlock old = Block(30, "game:old", EnumBlockMaterial.Stone);
        FixtureBlock current = Block(31, "game:current", EnumBlockMaterial.Stone);
        FixtureBlock torch = Block(32, "game:torch", EnumBlockMaterial.Stone);
        torch.Light = [5, 8, 16];
        using SceneFixture fixture = new(withPlayer: true, meshes: new Dictionary<int, MeshData?>
        {
            [30] = CubeMesh(),
            [31] = CubeMesh(),
            [32] = QuadMesh(0.5f)
        });
        fixture.SetOrigins(0, 0, 0, 0, 0, 0);
        fixture.SetField("generation", 1);
        fixture.SetField("rebuildRequested", false);
        int dimension = fixture.Field<ICoreClientAPI>("api").World.Player.Entity.Pos.Dimension;
        fixture.Invoke<object>("OnBlockChanged", new BlockPos(2, 2, 2, dimension + 1), old);
        Assert.AreEqual(0, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);
        fixture.SetSolid(2, 2, 2, current);
        fixture.Invoke<object>("OnBlockChanged", new BlockPos(999, 999, 999), old);
        Assert.AreEqual(0, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);

        fixture.Invoke<object>("OnBlockChanged", new BlockPos(2, 2, 2, dimension), old);
        Assert.AreEqual(1, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);
        fixture.SetFluid(2, 2, 2, current);
        fixture.Invoke<object>("OnBlockChanged", new BlockPos(2, 2, 2, dimension), old);
        fixture.SetSolid(2, 2, 2, torch);
        fixture.Invoke<object>("OnBlockChanged", new BlockPos(2, 2, 2, dimension), current);
        Assert.IsFalse(fixture.Field<bool>("rebuildRequested"),
            "Changing an emitter must not force a full terrain-volume rebuild.");
        Assert.AreEqual(1, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);
        fixture.Invoke<object>("ProcessDirtyBlocks");
        Assert.AreEqual(0, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);
        Assert.AreEqual(1, fixture.Field<VoxelLight[]>("readyLights").Length);
        Assert.AreEqual("game:torch", fixture.Field<VoxelLight[]>("readyLights")[0].Code);
        Assert.IsTrue(fixture.Field<bool>("liveLightsUploadPending"));
        Assert.IsTrue(fixture.Field<bool>("radianceRefreshRequested"));
        using SceneFixture noPlayer = new();
        noPlayer.SetOrigins(0, 0, 0, 0, 0, 0);
        noPlayer.Invoke<object>("OnBlockChanged", new BlockPos(2, 2, 2), old);
        Assert.AreEqual(0, noPlayer.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);

        Assert.IsFalse(fixture.Invoke<bool>("BlockEmitsLight", null, new BlockPos(0)));
        Assert.IsFalse(fixture.Invoke<bool>("BlockEmitsLight", Block(0, "game:air", EnumBlockMaterial.Air), new BlockPos(0)));
        Assert.IsTrue(fixture.Invoke<bool>("BlockEmitsLight", new ThrowingLightBlock { BlockId = 33 }, new BlockPos(0)));
    }

    /// <summary>
    /// Verifies the rebuild Lifecycle Covers Recentering Settle Queues And Player Tick regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void RebuildLifecycleCoversRecenteringSettleQueuesAndPlayerTick()
    {
        using SceneFixture fixture = new(withPlayer: true, daylight: 1.5f);
        Assert.IsTrue(fixture.Invoke<bool>("NeedsRecentering", 0, 0, 0));
        Assert.IsTrue(fixture.Invoke<bool>("NeedsSunRecentering", 0, 0, 0, new Vec3f(0, 1, 0)));
        fixture.Invoke<object>("BeginRebuild", 10, 20, 30, true);
        Assert.IsTrue(fixture.Field<bool>("building"));
        Assert.IsTrue(fixture.Field<bool>("settleRebuildPending"));
        Assert.AreEqual(10 - VoxelScene.Width / 2, fixture.Field<int>("originX"));

        fixture.Invoke<object>("BeginRebuild", 100, 100, 100, true);
        fixture.Invoke<object>("OnGameTick", 0.02f);
        Assert.AreEqual(
            -VoxelScene.Width / 2,
            fixture.Field<int>("originX"),
            "A teleport must restart an in-flight scan before stale geometry can be published.");
        Assert.AreEqual(-VoxelScene.Height / 2, fixture.Field<int>("originY"));
        Assert.AreEqual(-VoxelScene.Depth / 2, fixture.Field<int>("originZ"));

        fixture.SetField("building", false);
        fixture.SetField("generation", 1);
        fixture.SetField("rebuildRequested", false);
        fixture.SetField("settleRebuildDelaySeconds", 0.01f);
        fixture.Invoke<object>("OnGameTick", 1.0f);
        Assert.IsTrue(fixture.Field<bool>("building"));
        fixture.SetField("building", false);
        fixture.SetField("generation", 1);
        fixture.SetField("originX", -32);
        fixture.SetField("originY", -24);
        fixture.SetField("originZ", -32);
        Assert.IsFalse(fixture.Invoke<bool>("NeedsRecentering", 0, 0, 0));
        Assert.IsTrue(fixture.Invoke<bool>("NeedsRecentering", -100, 0, 0));

        fixture.Scene.Dispose();
        fixture.Invoke<object>("OnGameTick", 0.02f);
    }

    /// <summary>Verifies runtime view distance selects a bounded LOD and distant surfaces remain editable.</summary>
    [TestMethod]
    public void ViewDistanceBuildUsesReducedSunSurfaceLodAndIndependentRainOrigin()
    {
        using SceneFixture runtime = new(
            withPlayer: true,
            desiredViewDistance: 384,
            approvedViewDistance: 256,
            configuredSunDistance: 80.0f);
        runtime.Invoke<object>("OnGameTick", 0.02f);
        Assert.AreEqual(256, runtime.Field<int>("sunTraceDistance"));
        Assert.AreEqual(8, runtime.Field<int>("sunOccupancyScale"));
        Assert.AreEqual(-VoxelScene.RainSurfaceWidth / 2, runtime.Field<int>("rainSurfaceOriginX"));
        Assert.AreEqual(-VoxelScene.RainSurfaceDepth / 2, runtime.Field<int>("rainSurfaceOriginZ"));
        Assert.IsTrue(runtime.Property<bool>("SunBuildComplete") is false);
        Assert.IsTrue(runtime.Property<int>("SunBuildCompletedCellEquivalent") >= 0);

        using SceneFixture surface = new(withPlayer: true, configuredSunDistance: 128.0f);
        surface.SetField("sunOccupancyScale", 4);
        surface.SetField("sunTraceDistance", 128);
        surface.SetOrigins(0, 0, 0, 0, 0, 0);
        surface.RainHeight = 2;
        surface.SetSolid(0, 2, 0, Block(78, "game:distantroof", EnumBlockMaterial.Stone));
        surface.Invoke<object>("SampleDistantSunSurface", 0);
        ulong[] build = surface.Field<ulong[]>("buildSunOccupancy");
        Assert.AreEqual(1UL << 8, build[0]);

        surface.Invoke<object>("UpdateReadyDistantSunColumn", 0, 0);
        ulong[] ready = surface.Field<ulong[]>("readySunOccupancy");
        Assert.AreEqual(1UL << 8, ready[0]);
        Assert.AreEqual(
            1,
            surface.Field<List<VoxelSunOccupancyUpdate>>("pendingSunOccupancyUpdates").Count);

        surface.SetSolid(0, 2, 0, null);
        surface.SetSolid(0, 1, 0, Block(79, "game:distantlowerroof", EnumBlockMaterial.Stone));
        Assert.AreEqual(1, surface.Invoke<int>("ResolveDistantSurfaceCasterY", 0, 2, 0));
        surface.SetSolid(0, 1, 0, null);
        Assert.AreEqual(int.MinValue, surface.Invoke<int>("ResolveDistantSurfaceCasterY", 0, 2, 0));
    }

    /// <summary>
    /// Verifies the tick Idle And Terminal Build Paths Process Dirty State And Publish regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TickIdleAndTerminalBuildPathsProcessDirtyStateAndPublish()
    {
        using SceneFixture noPlayer = new();
        noPlayer.Invoke<object>("OnGameTick", 0.016f);

        using SceneFixture idle = new(withPlayer: true);
        (int sunX, int sunY, int sunZ) = VoxelScene.CalculateSunClipmapOrigin(
            0, 0, 0, new Vec3f(0, 1, 0));
        idle.SetOrigins(
            -VoxelScene.Width / 2,
            -VoxelScene.Height / 2,
            -VoxelScene.Depth / 2,
            sunX,
            sunY,
            sunZ);
        idle.SetField("generation", 1);
        idle.SetField("building", false);
        idle.SetField("rebuildRequested", false);
        idle.Invoke<object>("OnGameTick", 0.016f);
        Assert.IsFalse(idle.Field<bool>("building"));
        idle.SetField("settleRebuildPending", true);
        idle.SetField("settleRebuildDelaySeconds", 1.0f);
        idle.Invoke<object>("OnGameTick", -1.0f);
        Assert.AreEqual(1.0f, idle.Field<float>("settleRebuildDelaySeconds"));

        using SceneFixture terminal = new(withPlayer: true);
        terminal.SetOrigins(
            -VoxelScene.Width / 2,
            -VoxelScene.Height / 2,
            -VoxelScene.Depth / 2,
            sunX,
            sunY,
            sunZ);
        terminal.SetField("generation", 1);
        terminal.SetField("building", true);
        terminal.SetField("buildIndex", VoxelScene.Width * VoxelScene.Height * VoxelScene.Depth);
        terminal.SetField(
            "fluidSurfaceBuildIndex",
            VoxelScene.FluidSurfaceWidth * VoxelScene.FluidSurfaceDepth);
        terminal.SetField(
            "sunBuildIndex",
            VoxelScene.SunOccupancyWidth * VoxelScene.SunOccupancyHeight * VoxelScene.SunOccupancyDepth);
        terminal.SetField(
            "rainSurfaceBuildIndex",
            VoxelScene.RainSurfaceWidth * VoxelScene.RainSurfaceDepth);
        terminal.Invoke<object>("OnGameTick", 0.016f);
        Assert.AreEqual(2, terminal.Field<int>("generation"));
        Assert.IsFalse(terminal.Field<bool>("building"));
    }

    /// <summary>
    /// Verifies the full And Incremental Sampling Encode Contained And Fluid Liquids regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void FullAndIncrementalSamplingEncodeContainedAndFluidLiquids()
    {
        FixtureBlock container = Block(55, "game:coveragebarrel", EnumBlockMaterial.Wood);
        container.Attributes = Json(
            """
            {"vintageRtxLiquidContainer":{"contentSlot":0,"capacityLitres":10,"surfaceMinimumY":0.2,"surfaceMaximumY":0.8,"alwaysOpen":true,"visibilityTreeBool":"","visibleWhen":false}}
            """);
        Item portion = LiquidItem("game:coveragehoney", 10.0f);
        using SceneFixture contained = new(
            meshes: new Dictionary<int, MeshData?> { [container.Id] = CubeMesh() },
            blockEntity: new FixtureContainerBlockEntity(
                Inventory(1, new DummySlot(new ItemStack(portion, 25)))));
        contained.SetOrigins(0, 0, 0, 0, 0, 0);
        contained.SetSolid(0, 0, 0, container);
        contained.Invoke<object>("SampleVoxel", 0);
        Assert.AreEqual(1, contained.Field<int>("buildVisibleLiquidContainers"));
        Assert.AreEqual(
            (byte)(VoxelLiquidFlags.Contained | VoxelLiquidFlags.VisibleSurface),
            contained.Field<byte[]>("buildLiquidMetadata")[1]);
        contained.Invoke<object>("UpdateReadyVoxel", 0, 0, 0);
        Assert.AreEqual(
            (byte)(VoxelLiquidFlags.Contained | VoxelLiquidFlags.VisibleSurface),
            contained.Field<byte[]>("readyLiquidMetadata")[1]);

        FixtureBlock fluid = Block(56, "game:coveragewater", EnumBlockMaterial.Water);
        fluid.MatterState = EnumMatterState.Liquid;
        fluid.LiquidLevel = 6;
        fluid.Collision = null;
        fluid.Selection = null;
        using SceneFixture liquid = new();
        liquid.SetOrigins(0, 0, 0, 0, 0, 0);
        liquid.SetFluid(0, 0, 0, fluid);
        liquid.SetFluid(-32, 0, -32, fluid);
        liquid.Invoke<object>("SampleFluidSurfaceColumn", 0);
        liquid.Invoke<object>("UpdateReadyVoxel", 0, 0, 0);
        liquid.Invoke<object>("UpdateReadyFluidSurface", 0, 0);
        liquid.Invoke<object>("UpdateReadyFluidSurface", 0, 0);
        liquid.Invoke<object>("UpdateReadyFluidSurface", 999, 999);
        int centeredFluidSurfaceOffset = (32 * VoxelScene.FluidSurfaceWidth + 32)
            * VoxelScene.FluidSurfaceChannels;
        Assert.AreEqual((byte)64, liquid.Field<byte[]>("readyVoxels")[3]);
        Assert.AreEqual(
            (byte)(VoxelLiquidFlags.FluidLayer | VoxelLiquidFlags.VisibleSurface),
            liquid.Field<byte[]>("readyLiquidMetadata")[1]);
        Assert.AreEqual(
            (byte)1,
            liquid.Field<byte[]>("readyFluidSurface")[centeredFluidSurfaceOffset]);
        Assert.AreEqual(
            (byte)6,
            liquid.Field<byte[]>("readyFluidSurface")[centeredFluidSurfaceOffset + 2]);

        FixtureBlock nonOpticalFluid = Block(57, "game:coveragefluidshape", EnumBlockMaterial.Stone);
        nonOpticalFluid.Collision = null;
        nonOpticalFluid.Selection = null;
        nonOpticalFluid.LiquidResult = " ";
        using SceneFixture nonOptical = new();
        nonOptical.SetOrigins(0, 0, 0, 0, 0, 0);
        nonOptical.SetFluid(0, 0, 0, nonOpticalFluid);
        nonOptical.SetFluid(0, 1, 0, Block(0, "game:emptyfluid", EnumBlockMaterial.Water));
        nonOptical.SetFluid(0, 2, 0, Block(58, "game:airfluid", EnumBlockMaterial.Air));
        nonOptical.Invoke<object>("UpdateReadyVoxel", 0, 0, 0);
        nonOptical.Invoke<object>("UpdateReadyVoxel", 0, 1, 0);
        nonOptical.Invoke<object>("UpdateReadyVoxel", 0, 2, 0);
        Assert.AreEqual((byte)64, nonOptical.Field<byte[]>("readyVoxels")[3]);
        Assert.AreEqual((byte)0, nonOptical.Field<byte[]>("readyLiquidMetadata")[1]);
    }

    /// <summary>
    /// Verifies that the runtime multiblock path reads the proxy owner, tessellates the distinct
    /// principal, and preserves the principal replacement contract used for projected occupancy.
    /// </summary>
    [TestMethod]
    public void MultiblockProxyCapturesDistinctPrincipalMeshAndRejectsInvalidOwners()
    {
        BlockPos proxyPosition = new(10, 20, 30, 2);
        BlockPos principalPosition = new(10, 19, 30, 2);
        FixtureMultiblockProxyEntity proxy = new(principalPosition)
        {
            Pos = proxyPosition
        };
        MeshData principalMesh = CubeMesh();
        for (int index = 1; index < principalMesh.xyz.Length; index += 3)
        {
            principalMesh.xyz[index] *= 2.0f;
        }
        FixtureBlockEntity principal = new(principalMesh, skipsDefault: true)
        {
            Pos = principalPosition
        };
        using SceneFixture fixture = new(blockEntity: proxy);
        fixture.SetBlockEntityAt(principalPosition, principal);
        Assert.IsTrue(fixture.Invoke<bool>("NeedsFluidSurfaceRecentering", 10, 30));

        object?[] args = [proxy, proxyPosition, null, null, null];
        Assert.IsTrue((bool)fixture.InvokeRaw("TryGetMultiblockPrincipalMeshes", args)!);
        Assert.AreEqual(principalPosition, (BlockPos)args[2]!);
        Assert.AreEqual(1, ((InstanceTerrainMeshCollector)args[3]!).Meshes.Count);
        Assert.AreEqual(true, args[4]);

        FixtureBlock proxyBlock = Block(196, "game:coverage-multiblock-proxy", EnumBlockMaterial.Metal);
        proxyBlock.EntityClass = "BEMPMultiblock";
        proxy.Block = proxyBlock;
        principal.Block = proxyBlock;
        object?[] occupancyArgs = [proxyBlock, proxyPosition, null];
        string? previousRunId = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "voxel-multiblock");
            Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", occupancyArgs)!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previousRunId);
        }
        InstanceMeshOccupancy projected = (InstanceMeshOccupancy)occupancyArgs[2]!;
        Assert.AreEqual(1, projected.CapturedMeshCount);
        Assert.IsTrue(projected.SkipsDefaultMesh);
        Assert.AreNotEqual(0UL, projected.Mask);
        fixture.Field<Dictionary<(int X, int Y, int Z, int Dimension), InstanceMeshOccupancy>>(
            "instanceMeshOccupancyByPosition").Clear();
        proxyBlock.Code = null;
        object?[] anonymousOccupancyArgs = [proxyBlock, proxyPosition, null];
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "voxel-multiblock-anonymous");
            Assert.IsTrue((bool)fixture.InvokeRaw(
                "TryGetInstanceMeshOccupancy", anonymousOccupancyArgs)!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previousRunId);
        }

        FixtureMultiblockProxyEntity self = new(proxyPosition) { Pos = proxyPosition };
        object?[] selfArgs = [self, proxyPosition, null, null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetMultiblockPrincipalMeshes", selfArgs)!);

        BlockPos absentPosition = new(11, 19, 30, 2);
        FixtureMultiblockProxyEntity absent = new(absentPosition) { Pos = proxyPosition };
        object?[] absentArgs = [absent, proxyPosition, null, null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetMultiblockPrincipalMeshes", absentArgs)!);

        using SceneFixture missingPrincipal = new();
        object?[] missingArgs = [proxy, proxyPosition, null, null, null];
        Assert.IsFalse((bool)missingPrincipal.InvokeRaw("TryGetMultiblockPrincipalMeshes", missingArgs)!);
        using SceneFixture selfPrincipal = new(blockEntity: proxy);
        object?[] principalSelfArgs = [proxy, proxyPosition, null, null, null];
        Assert.IsFalse((bool)selfPrincipal.InvokeRaw(
            "TryGetMultiblockPrincipalMeshes", principalSelfArgs)!);
        FixtureBlockEntity ordinaryEntity = new(QuadMesh(0.5f), false);
        object?[] ordinaryArgs = [ordinaryEntity, proxyPosition, null, null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw(
            "TryGetMultiblockPrincipalMeshes", ordinaryArgs)!);

        Assert.IsFalse(VoxelScene.TryResolveMultiblockPrincipal(
            null, new TreeAttribute(), 0, out _));
        Assert.IsFalse(VoxelScene.TryResolveMultiblockPrincipal(
            "FixtureMultiblock", new TreeAttribute(), 0, out _));
        TreeAttribute missingY = new();
        missingY.SetInt("cx", 1);
        Assert.IsFalse(VoxelScene.TryResolveMultiblockPrincipal(
            "FixtureMultiblock", missingY, 0, out _));
        TreeAttribute missingZ = new();
        missingZ.SetInt("cx", 1);
        missingZ.SetInt("cy", 2);
        Assert.IsFalse(VoxelScene.TryResolveMultiblockPrincipal(
            "FixtureMultiblock", missingZ, 0, out _));
    }

    /// <summary>
    /// Verifies the dynamic Block Entity Tessellation Caches Captured Meshes And Replacement Semantics regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void DynamicBlockEntityTessellationCachesCapturedMeshesAndReplacementSemantics()
    {
        FixtureBlock chiseled = Block(40, "game:chiseled-rock", EnumBlockMaterial.Stone);
        chiseled.EntityClass = "BlockEntityChisel";
        FixtureBlockEntity blockEntity = new(QuadMesh(0.3f), skipsDefault: true)
        {
            Block = chiseled,
            Pos = new BlockPos(5, 6, 7)
        };
        using SceneFixture fixture = new(
            meshes: new Dictionary<int, MeshData?> { [40] = CubeMesh() },
            blockEntity: blockEntity);
        object?[] args = [chiseled, new BlockPos(5, 6, 7), null];
        Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", args)!);
        InstanceMeshOccupancy captured = (InstanceMeshOccupancy)args[2]!;
        Assert.AreEqual(1, captured.CapturedMeshCount);
        Assert.IsTrue(captured.SkipsDefaultMesh);
        Assert.AreNotEqual(0UL, captured.Mask);
        object?[] cachedArgs = [chiseled, new BlockPos(5, 6, 7), null];
        Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", cachedArgs)!);
        Assert.AreEqual(captured.Mask, ((InstanceMeshOccupancy)cachedArgs[2]!).Mask);

        FixtureBlock other = Block(41, "game:other", EnumBlockMaterial.Stone);
        object?[] changedArgs = [other, new BlockPos(5, 6, 7), null];
        Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", changedArgs)!);
        Assert.AreEqual(41, ((InstanceMeshOccupancy)changedArgs[2]!).BlockId);
    }

    /// <summary>
    /// Verifies the geometry Caches Diagnostics Alpha Casters And Failures Are Observable regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GeometryCachesDiagnosticsAlphaCastersAndFailuresAreObservable()
    {
        MeshData transparent = QuadMesh(0.5f);
        transparent.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        transparent.RenderPassCount = 1;
        FixtureBlock lantern = Block(42, "game:coverage-lantern", EnumBlockMaterial.Metal);
        FixtureBlock lowerLantern = Block(201, "game:coverage-lantern-lower", EnumBlockMaterial.Metal);
        FixtureBlock middleLantern = Block(204, "game:coverage-lantern-middle", EnumBlockMaterial.Metal);
        FixtureBlock opaqueLantern = Block(202, "game:coverage-lantern-opaque", EnumBlockMaterial.Metal);
        FixtureBlock completeLantern = Block(203, "game:coverage-lantern-complete", EnumBlockMaterial.Metal);
        FixtureBlock anvil = Block(48, "game:coverage-anvil", EnumBlockMaterial.Metal);
        FixtureBlock ordinary = Block(49, "game:coverage-ordinary", EnumBlockMaterial.Stone);
        MeshData middleLanternMesh = CubeMesh();
        for (int index = 1; index < middleLanternMesh.xyz.Length; index += 3)
        {
            middleLanternMesh.xyz[index] *= 0.55f;
        }
        using SceneFixture fixture = new(meshes: new Dictionary<int, MeshData?>
        {
            [42] = transparent,
            [201] = QuadMesh(0.1f),
            [204] = middleLanternMesh,
            [202] = CubeMesh(),
            [203] = CubeMesh(),
            [48] = transparent,
            [49] = transparent
        });
        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "voxel-remainder");
            CachedBlockOccupancy occupancy = fixture.Invoke<CachedBlockOccupancy>(
                "GetCachedBlockOccupancy", lantern);
            Assert.AreEqual(2, occupancy.TransparentTriangles);
            fixture.Invoke<CachedBlockOccupancy>("GetCachedBlockOccupancy", anvil);
            fixture.Invoke<CachedBlockOccupancy>("GetCachedBlockOccupancy", ordinary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previous);
        }

        CachedBlockOccupancy casterOccupancy = new(1, true, 1, 0, 0, BlockGeometryKind.StaticComplex);
        CachedLightCaster firstCaster;
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "voxel-lantern-caster");
            firstCaster = fixture.Invoke<CachedLightCaster>(
                "GetLightCaster", lantern, casterOccupancy);
            fixture.Invoke<CachedLightCaster>(
                "GetLightCaster",
                lowerLantern,
                new CachedBlockOccupancy(1, true, 2, 1, 0, BlockGeometryKind.StaticComplex));
            fixture.Invoke<CachedLightCaster>(
                "GetLightCaster",
                middleLantern,
                new CachedBlockOccupancy(1, true, 12, 1, 0, BlockGeometryKind.StaticComplex));
            fixture.Invoke<CachedLightCaster>(
                "GetLightCaster",
                opaqueLantern,
                new CachedBlockOccupancy(1, true, 12, 0, 0, BlockGeometryKind.StaticComplex));
            fixture.Invoke<CachedLightCaster>(
                "GetLightCaster",
                completeLantern,
                new CachedBlockOccupancy(1, true, 12, 1, 0, BlockGeometryKind.StaticComplex));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previous);
        }
        Assert.AreEqual(firstCaster, fixture.Invoke<CachedLightCaster>(
            "GetLightCaster", lantern, casterOccupancy));
        Assert.AreEqual(0, fixture.Invoke<CachedLightCaster>(
            "GetLightCaster", ordinary, default(CachedBlockOccupancy)).Mask.Length);
        Assert.AreEqual(0, fixture.Invoke<byte[]>("BuildLightCasterMask", lantern, new MeshData(false)).Length);
        Assert.AreEqual(0, fixture.Invoke<byte[]>(
            "BuildLightCasterMask", lantern, (object?)null).Length);
        MeshData missingIndices = new(false)
        {
            xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            VerticesCount = 3,
            IndicesCount = 3
        };
        MeshData tooFewVertices = new(false)
        {
            xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Indices = [0, 1, 2],
            VerticesCount = 2,
            IndicesCount = 3
        };
        MeshData tooFewIndices = new(false)
        {
            xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Indices = [0, 1, 2],
            VerticesCount = 3,
            IndicesCount = 2
        };
        foreach (MeshData invalid in new[] { missingIndices, tooFewVertices, tooFewIndices })
        {
            Assert.AreEqual(0, fixture.Invoke<byte[]>(
                "BuildLightCasterMask", lantern, invalid).Length);
            Assert.AreEqual(default(CachedBlockOccupancy), fixture.Invoke<CachedBlockOccupancy>(
                "BuildMeshMask", lantern, invalid));
        }
        foreach (int[] invalidIndices in new[]
        {
            new[] { 9, 1, 2 },
            new[] { 0, 9, 2 },
            new[] { 0, 1, 9 }
        })
        {
            MeshData invalidTriangle = new(false)
            {
                xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0],
                Indices = invalidIndices,
                VerticesCount = 3,
                IndicesCount = 3,
                IndicesPerFace = 3
            };
            Assert.AreEqual(0UL, fixture.Invoke<CachedBlockOccupancy>(
                "BuildMeshMask", lantern, invalidTriangle).Mask);
            Assert.IsFalse(fixture.Invoke<byte[]>(
                "BuildLightCasterMask", lantern, invalidTriangle).Any(value => value != 0));
            Assert.AreEqual(0UL, VoxelScene.RasterizeOpaqueMeshMask(invalidTriangle));
            Assert.AreEqual(1, VoxelScene.InspectInstanceMeshes(
                [invalidTriangle], 0, 0, 0, 0).InvalidTriangles);
        }

        FixtureBlock broken = Block(43, "game:broken-caster", EnumBlockMaterial.Metal);
        broken.Code = null;
        using SceneFixture throwing = new(throwMeshForBlockId: 43);
        Assert.AreEqual(default(CachedBlockOccupancy), throwing.Invoke<CachedBlockOccupancy>(
            "GetCachedBlockOccupancy", broken));
        CachedLightCaster fallback = throwing.Invoke<CachedLightCaster>(
            "GetLightCaster", broken, casterOccupancy);
        Assert.AreEqual(0, fallback.Mask.Length);
        Assert.AreEqual(fallback, throwing.Invoke<CachedLightCaster>(
            "GetLightCaster", broken, casterOccupancy));

        FixtureBlock codedBroken = Block(194, "game:coded-broken-caster", EnumBlockMaterial.Metal);
        using SceneFixture codedThrowing = new(throwMeshForBlockId: codedBroken.Id);
        Assert.AreEqual(0, codedThrowing.Invoke<CachedLightCaster>(
            "GetLightCaster", codedBroken, casterOccupancy).Mask.Length);

        FixtureBlock codeLess = Block(44, "game:temporary", EnumBlockMaterial.Stone);
        codeLess.Code = null;
        fixture.Invoke<object>("CountFallbackOccupancy", codeLess, "coverage");
        fixture.Invoke<object>("CountFallbackOccupancy", codeLess, "coverage");
        Assert.AreEqual(2, fixture.Field<Dictionary<(string Code, string Reason), int>>(
            "fallbackOccupancyHistogram")[("id:44", "coverage")]);

        TextureAtlasPosition position = new()
        {
            atlasTextureId = 92,
            atlasNumber = 0,
            x1 = 0,
            y1 = 0,
            x2 = 1,
            y2 = 1
        };
        AssetLocation alphaSource = new("game", "textures/block/cutout.png");
        FixtureBlock cutout = Block(45, "game:cutout", EnumBlockMaterial.Plant);
        cutout.Textures = new Dictionary<string, CompositeTexture>
        {
            ["all"] = new CompositeTexture(new AssetLocation("game:block/cutout"))
            {
                Baked = new BakedCompositeTexture { TextureSubId = 0 }
            }
        };
        MeshData alphaMesh = QuadMesh(0.5f);
        alphaMesh.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.OpaqueNoCull];
        alphaMesh.RenderPassCount = 1;
        alphaMesh.IndicesPerFace = 0;
        alphaMesh.Uv = [0, 0, 1, 0, 1, 1, 0, 1];
        alphaMesh.TextureIndices = [0];
        alphaMesh.TextureIds = [92];
        using SceneFixture alpha = new(
            meshes: new Dictionary<int, MeshData?> { [45] = alphaMesh },
            atlasPositions: [position],
            assets: new Dictionary<AssetLocation, IAsset>
            {
                [alphaSource] = BitmapAsset(
                    new SKColor(20, 30, 40, 255),
                    new SKColor(20, 30, 40, 0))
            });
        CachedBlockOccupancy alphaOccupancy = alpha.Invoke<CachedBlockOccupancy>(
            "BuildMeshMask", cutout, alphaMesh);
        Assert.IsTrue(alphaOccupancy.AlphaTestedTriangles > 0);
        Assert.AreEqual(16 * 16 * 16, alpha.Invoke<byte[]>(
            "BuildLightCasterMask", cutout, alphaMesh).Length);
        Assert.IsNull(alpha.Invoke<object?>(
            "ResolveTextureAlphaSampler", alphaMesh, 0, 0, 1, 2, new List<TextureAlphaCandidate>()));
    }

    /// <summary>
    /// Verifies the instance Tessellation Logs Zero Meshes Clears Capacity And Recovers From Exceptions regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void InstanceTessellationLogsZeroMeshesClearsCapacityAndRecoversFromExceptions()
    {
        MeshData transparent = QuadMesh(0.5f);
        transparent.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        transparent.RenderPassCount = 1;
        FixtureBlock chisel = Block(46, "game:coverage-chisel", EnumBlockMaterial.Stone);
        chisel.EntityClass = "BlockEntityChisel";
        FixtureBlockEntity entity = new(transparent, true)
        {
            Block = chisel,
            Pos = new BlockPos(0)
        };
        using SceneFixture fixture = new(blockEntity: entity);
        Dictionary<(int X, int Y, int Z, int Dimension), InstanceMeshOccupancy> cache =
            fixture.Field<Dictionary<(int X, int Y, int Z, int Dimension), InstanceMeshOccupancy>>(
                "instanceMeshOccupancyByPosition");
        for (int index = 0; index < 2048; index++)
        {
            cache[(index, 0, 0, 0)] = new InstanceMeshOccupancy(entity, chisel.Id, 0, true, 1);
        }

        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "voxel-remainder");
            object?[] args = [chisel, new BlockPos(5, 6, 7), null];
            Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", args)!);
            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(0UL, ((InstanceMeshOccupancy)args[2]!).Mask);
            FixtureBlock appended = Block(191, "game:coverage-appended", EnumBlockMaterial.Stone);
            appended.EntityClass = "BlockEntityFixture";
            object?[] appendedArgs = [appended, new BlockPos(8, 6, 7), null];
            Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", appendedArgs)!);

            chisel.Code = null;
            object?[] anonymousArgs = [chisel, new BlockPos(9, 6, 7), null];
            Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", anonymousArgs)!);
            Assert.AreEqual(0UL, ((InstanceMeshOccupancy)anonymousArgs[2]!).Mask);

            FixtureBlock opaqueBlock = Block(205, "game:coverage-opaque-instance", EnumBlockMaterial.Stone);
            opaqueBlock.EntityClass = "BlockEntityFixture";
            FixtureBlockEntity opaqueEntity = new(QuadMesh(0.5f), false)
            {
                Block = opaqueBlock,
                Pos = new BlockPos(10, 6, 7)
            };
            fixture.SetBlockEntity(opaqueEntity);
            object?[] opaqueArgs = [opaqueBlock, opaqueEntity.Pos, null];
            Assert.IsTrue((bool)fixture.InvokeRaw("TryGetInstanceMeshOccupancy", opaqueArgs)!);
            Assert.AreNotEqual(0UL, ((InstanceMeshOccupancy)opaqueArgs[2]!).Mask);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previous);
        }

        FixtureBlock failingBlock = Block(47, "game:failing-instance", EnumBlockMaterial.Stone);
        failingBlock.Code = null;
        ThrowingTessellationBlockEntity failingEntity = new()
        {
            Block = failingBlock,
            Pos = new BlockPos(0)
        };
        using SceneFixture failing = new(blockEntity: failingEntity);
        object?[] failedArgs = [failingBlock, new BlockPos(0), null];
        Assert.IsFalse((bool)failing.InvokeRaw("TryGetInstanceMeshOccupancy", failedArgs)!);
        Assert.AreEqual(default(InstanceMeshOccupancy), (InstanceMeshOccupancy)failedArgs[2]!);
        failingBlock.Code = new AssetLocation("game:coded-failing-instance");
        object?[] codedFailedArgs = [failingBlock, new BlockPos(1, 0, 0), null];
        Assert.IsFalse((bool)failing.InvokeRaw("TryGetInstanceMeshOccupancy", codedFailedArgs)!);
    }

    /// <summary>
    /// Verifies the private Scalar Helpers Cover Physical Materials Lights And Triangle Regions regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void PrivateScalarHelpersCoverPhysicalMaterialsLightsAndTriangleRegions()
    {
        using SceneFixture fixture = new(meshes: new Dictionary<int, MeshData?>
        {
            [50] = QuadMesh(0.5f),
            [51] = QuadMesh(0.5f),
            [52] = QuadMesh(0.5f),
            [53] = QuadMesh(0.5f)
        });
        byte[] destination = new byte[16];
        FixtureBlock glass = Block(50, "game:glass", EnumBlockMaterial.Glass);
        FixtureBlock metal = Block(51, "game:metal", EnumBlockMaterial.Metal);
        FixtureBlock soil = Block(52, "game:soil", EnumBlockMaterial.Soil);
        FixtureBlock plant = Block(53, "game:plant", EnumBlockMaterial.Plant);
        fixture.Invoke<object>("WriteBlockMaterial", destination, glass, 0, 0, 0, 0, (byte)0, false);
        fixture.Invoke<object>("WriteBlockMaterial", destination, metal, 4, 0, 0, 0, (byte)0, false);
        fixture.Invoke<object>("WriteBlockMaterial", destination, soil, 8, 0, 0, 0, (byte)222, false);
        fixture.Invoke<object>("WriteBlockMaterial", destination, plant, 12, 0, 0, 0, (byte)0, true);
        Assert.AreEqual(64, destination[3]);
        Assert.AreEqual(192, destination[7]);
        Assert.AreEqual(222, destination[11]);
        Assert.AreEqual(
            (byte)(128 | VoxelScene.VegetationMaterialFlag | VoxelScene.PartialGeometryMaterialFlag),
            destination[15]);
        Assert.AreEqual((byte)128, VoxelScene.EncodeMaterialGeometryClass(128, false));
        Assert.AreEqual(
            (byte)(128 | VoxelScene.PartialGeometryMaterialFlag),
            VoxelScene.EncodeMaterialGeometryClass(128, true));

        FixtureBlock dynamicGeometry = Block(197, "game:dynamic-geometry", EnumBlockMaterial.Stone);
        FixtureBlock partialGeometry = Block(198, "game:partial-geometry", EnumBlockMaterial.Stone);
        FixtureBlock emptyGeometry = Block(199, "game:empty-geometry", EnumBlockMaterial.Stone);
        Dictionary<int, CachedBlockOccupancy> geometryCache =
            fixture.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy");
        geometryCache[dynamicGeometry.Id] = new CachedBlockOccupancy(
            0, false, 0, 0, 0, BlockGeometryKind.DynamicInstance);
        geometryCache[partialGeometry.Id] = new CachedBlockOccupancy(
            1, true, 1, 0, 0, BlockGeometryKind.StaticComplex);
        geometryCache[emptyGeometry.Id] = new CachedBlockOccupancy(
            0, false, 0, 0, 0, BlockGeometryKind.Unknown);
        Assert.IsTrue(fixture.Invoke<bool>("HasPotentialPartialGeometry", dynamicGeometry));
        Assert.IsTrue(fixture.Invoke<bool>("HasPotentialPartialGeometry", partialGeometry));
        Assert.IsFalse(fixture.Invoke<bool>("HasPotentialPartialGeometry", emptyGeometry));

        fixture.Invoke<object>("CollectLight", (object?)null, 0, 0, 0);
        fixture.Invoke<object>("CollectLight", Block(0, "game:air", EnumBlockMaterial.Air), 0, 0, 0);
        FixtureBlock shortEmitter = Block(200, "game:short-emitter", EnumBlockMaterial.Stone);
        shortEmitter.Light = [1, 2];
        fixture.Invoke<object>("CollectLight", shortEmitter, 0, 0, 0);
        shortEmitter.Light = null!;
        fixture.Invoke<object>("CollectLight", shortEmitter, 0, 0, 0);
        soil.Light = [0, 0, 0];
        fixture.Invoke<object>("CollectLight", soil, 0, 0, 0);
        foreach (string family in new[] { "lantern", "torch", "candle", "fire", "flame", "ember", "forge", "bloomery" })
        {
            Assert.IsTrue(InvokeStatic<bool>("IsWarmEmitter", family));
        }
        Assert.IsFalse(InvokeStatic<bool>("IsWarmEmitter", "glowworm"));
        FixtureBlock lantern = Block(50, "game:lantern", EnumBlockMaterial.Metal);
        lantern.Light = [5, 7, 32];
        fixture.Invoke<object>("CollectLight", lantern, 1, 2, 3);
        Assert.AreEqual(1, fixture.Field<List<VoxelLight>>("buildLights").Count);
        FixtureBlock unknownEmitter = Block(53, "game:temporary", EnumBlockMaterial.Stone);
        unknownEmitter.Code = null;
        unknownEmitter.Light = [1, 1, 8];
        fixture.Invoke<object>("CollectLight", unknownEmitter, 0, 0, 0);
        Assert.AreEqual(1, fixture.Field<List<VoxelLight>>("buildLights").Count,
            "An emitter without a canonical code is rejected, not published as a fictitious second source.");
        Assert.AreEqual("game:lantern", fixture.Field<List<VoxelLight>>("buildLights")[0].Code);

        FixtureBlock shortLight = Block(54, "game:shortlight", EnumBlockMaterial.Stone);
        shortLight.Light = [1, 2];
        Assert.IsFalse(fixture.Invoke<bool>("BlockEmitsLight", shortLight, new BlockPos(0)));
        shortLight.Light = [1, 2, 3];
        Assert.IsTrue(fixture.Invoke<bool>("BlockEmitsLight", shortLight, new BlockPos(0)));

        MeshData optionalPassCount = QuadMesh(0.5f);
        optionalPassCount.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        optionalPassCount.RenderPassCount = 0;
        Assert.AreEqual(1, VoxelScene.CloneInstanceMesh(optionalPassCount).RenderPassCount);
        Block anonymousEntityBlock = new() { EntityClass = "BlockEntityFixture", Code = null };
        Assert.IsFalse(VoxelScene.InstanceGeometryReplacesDefaultMesh(anonymousEntityBlock));

        InstanceMeshDiagnostics missedGrid = VoxelScene.InspectInstanceMeshes(
            [QuadMesh(0.5f)], 0, 0, 0, 0);
        Assert.AreEqual("opaque-triangles-missed-grid", missedGrid.Reason);
        MeshData nonFinite = new(false)
        {
            xyz = [float.NaN, 0, 0, float.NaN, 1, 0, float.NaN, 0, 1],
            Indices = [0, 1, 2],
            VerticesCount = 3,
            IndicesCount = 3,
            IndicesPerFace = 3
        };
        Assert.AreEqual(
            "outside-local-block",
            VoxelScene.InspectInstanceMeshes([nonFinite], 0, 0, 0, 0).Reason);
        MeshData missingBuffers = new(false)
        {
            xyz = null!,
            Indices = null!,
            VerticesCount = 0,
            IndicesCount = 0,
            IndicesPerFace = 0
        };
        Assert.AreEqual(
            "empty-buffers",
            VoxelScene.InspectInstanceMeshes([missingBuffers], 0, 0, 0, 0).Reason);

        using SceneFixture directedSun = new(sunDirection: new Vec3f(0, 2, 0));
        Assert.AreEqual(1.0f, directedSun.Invoke<Vec3f>("GetNormalizedSunDirection").Y);
        using SceneFixture zeroSun = new(sunDirection: new Vec3f());
        Assert.AreEqual(1.0f, zeroSun.Invoke<Vec3f>("GetNormalizedSunDirection").Y);

        Assert.AreEqual(0UL, VoxelScene.PackSunBlockMask(0, 0, 0, 0, 0));
        ulong lowCorner = VoxelScene.PackSunBlockMask(0, 1UL, 0, 0, 0);
        Assert.AreEqual(1UL, lowCorner);
        ulong highCorner = VoxelScene.PackSunBlockMask(0, 1UL << 63, 1, 1, 1);
        Assert.AreEqual(1UL << 63, highCorner);
        ulong oneFullBlock = VoxelScene.PackSunBlockMask(0, ulong.MaxValue, 0, 0, 0);
        Assert.AreEqual(8, System.Numerics.BitOperations.PopCount(oneFullBlock));
        Assert.AreEqual(lowCorner | highCorner, VoxelScene.PackSunBlockMask(
            lowCorner, 1UL << 63, 1, 1, 1));
        Assert.AreEqual(-4, InvokeStatic<int>("AlignDown", -3, 2));
        Assert.AreEqual(2, InvokeStatic<int>("AlignDown", 3, 2));

        Vec3f a = new(0, 0, 0);
        Vec3f b = new(1, 0, 0);
        Vec3f c = new(0, 1, 0);
        foreach (Vec3f point in new[]
        {
            new Vec3f(-1, -1, 0), new Vec3f(2, 0, 0), new Vec3f(0.5f, -0.2f, 0),
            new Vec3f(0, 2, 0), new Vec3f(-0.2f, 0.5f, 0), new Vec3f(0.8f, 0.8f, 0),
            new Vec3f(0.25f, 0.25f, 1)
        })
        {
            Assert.IsTrue(InvokeStatic<float>("PointTriangleDistanceSquared", point, a, b, c) >= 0);
        }
    }

    /// <summary>
    /// Verifies the canonical Cube Validation Rejects Every Unsafe Topology And Geometry Case regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CanonicalCubeValidationRejectsEveryUnsafeTopologyAndGeometryCase()
    {
        MeshData noIndices = CubeMesh();
        noIndices.Indices = null!;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(noIndices));
        MeshData wrongVertexCount = CubeMesh();
        wrongVertexCount.VerticesCount = 23;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(wrongVertexCount));
        MeshData wrongIndexCount = CubeMesh();
        wrongIndexCount.IndicesCount = 35;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(wrongIndexCount));

        MeshData alphaPass = CubeMesh();
        alphaPass.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.OpaqueNoCull];
        alphaPass.RenderPassCount = 1;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(alphaPass));
        MeshData transparentPass = CubeMesh();
        transparentPass.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        transparentPass.RenderPassCount = 1;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(transparentPass));

        MeshData shortRenderPassArray = CubeMesh();
        shortRenderPassArray.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Opaque];
        shortRenderPassArray.RenderPassCount = 1;
        shortRenderPassArray.IndicesPerFace = 3;
        Assert.IsTrue(VoxelScene.IsCanonicalUnitCubeMesh(shortRenderPassArray));
        MeshData defaultFaceWidth = CubeMesh();
        defaultFaceWidth.IndicesPerFace = 0;
        Assert.IsTrue(VoxelScene.IsCanonicalUnitCubeMesh(defaultFaceWidth));
        MeshData rasterizedDefaultFaceWidth = QuadMesh(0.5f);
        rasterizedDefaultFaceWidth.IndicesPerFace = 0;
        Assert.AreNotEqual(0UL, VoxelScene.RasterizeOpaqueMeshMask(rasterizedDefaultFaceWidth));

        MeshData invalidIndex = CubeMesh();
        invalidIndex.Indices[0] = 24;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(invalidIndex));

        MeshData interiorTriangle = CubeMesh();
        interiorTriangle.xyz[0] = 0.25f;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(interiorTriangle));

        MeshData degenerate = CubeMesh();
        degenerate.Indices[1] = degenerate.Indices[0];
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(degenerate));

        MeshData nonCorner = CubeMesh();
        nonCorner.xyz[1] = 0.4f;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(nonCorner));

        MeshData duplicateTriangle = CubeMesh();
        duplicateTriangle.Indices[3] = duplicateTriangle.Indices[0];
        duplicateTriangle.Indices[4] = duplicateTriangle.Indices[1];
        duplicateTriangle.Indices[5] = duplicateTriangle.Indices[2];
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(duplicateTriangle));

        MeshData boundaryEdge = CubeMesh();
        boundaryEdge.Indices[3] = 0;
        boundaryEdge.Indices[4] = 1;
        boundaryEdge.Indices[5] = 2;
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(boundaryEdge));

        MeshData missingFace = CubeMesh();
        Array.Copy(missingFace.Indices, 0, missingFace.Indices, 30, 6);
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(missingFace));

        Assert.AreEqual(-1, InvokeStatic<int>(
            "UnitCubeBoundaryFace",
            new Vec3f(0, 0, 0),
            new Vec3f(1, 0, 0),
            new Vec3f(0, 1, 1)));
        Assert.AreEqual(3, InvokeStatic<int>("UnitCubeFaceCorner", 0, new Vec3f(0, 1, 1)));
        Assert.AreEqual(3, InvokeStatic<int>("UnitCubeFaceCorner", 1, new Vec3f(1, 1, 1)));
        Assert.AreEqual(3, InvokeStatic<int>("UnitCubeFaceCorner", 2, new Vec3f(1, 0, 1)));
        Assert.AreEqual(3, InvokeStatic<int>("UnitCubeFaceCorner", 3, new Vec3f(1, 1, 1)));
        Assert.AreEqual(3, InvokeStatic<int>("UnitCubeFaceCorner", 4, new Vec3f(1, 1, 0)));
        Assert.AreEqual(-1, InvokeStatic<int>("UnitCubeFaceCorner", 0, new Vec3f(0, 0.5f, 0)));
        Assert.AreEqual(-1, InvokeStatic<int>("UnitCubeFaceCorner", 0, new Vec3f(0, 0, 0.5f)));
        Assert.AreEqual(105.0f, InvokeStatic<float>("InstanceMeshAxisOffset", 100.0f, 110.0f, 0));
        Assert.AreEqual(0.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", float.NaN, 1.0f, 0));
        Assert.AreEqual(0.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", -0.5f, 1.0f, 100));
        Assert.AreEqual(100.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", 100.5f, 101.5f, 100));
        Assert.AreEqual(3.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", 3.5f, 4.5f, 35));
        Assert.AreEqual(0.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", 0.5f, 1.5f, 100));
        Assert.AreEqual(105.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", 100.0f, 110.0f, 0));
        Assert.AreEqual(-1.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", -3.0f, 2.0f, 100));
        Assert.AreEqual(2.0f, InvokeStatic<float>(
            "InstanceMeshOwnerAxisOffset", 2.0f, 2.0f, 100));
        Assert.AreEqual(0UL, VoxelScene.RasterizeInstanceMeshesAtRelativeBlock(
            [], 0, 0, 0, 0, 0, 0));
        Assert.AreNotEqual(0UL, VoxelScene.RasterizeInstanceMeshesAtRelativeBlock(
            [QuadMesh(0.5f), null!, new MeshData(false) { xyz = null!, VerticesCount = 1 }],
            0,
            0,
            0,
            0,
            0,
            0));
        Assert.IsTrue(VoxelScene.MeasureCasterGeometry(
            new CachedBlockOccupancy(1, true, 1, 0, 0, BlockGeometryKind.DynamicInstance),
            false,
            null).DetailedNonCube);
        Assert.IsFalse(VoxelScene.MeasureCasterGeometry(
            new CachedBlockOccupancy(1, true, 1, 0, 0, BlockGeometryKind.FullCubeStatic),
            false,
            null).DetailedNonCube);
        Assert.AreEqual(0UL, VoxelScene.RasterizeOpaqueMeshMask(new MeshData(false)
        {
            xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Indices = null!,
            VerticesCount = 3,
            IndicesCount = 3
        }));

        object?[] outsideUv =
        [
            new Vec3f(3, 3, 0),
            new Vec3f(0, 0, 0),
            new Vec3f(1, 0, 0),
            new Vec3f(0, 1, 0),
            new TextureUv(0, 0),
            new TextureUv(1, 0),
            new TextureUv(0, 1),
            null
        ];
        Assert.IsFalse((bool)InvokeStaticRaw("TryInterpolateUv", outsideUv)!);
        outsideUv[0] = new Vec3f(-1, 0, 0);
        Assert.IsFalse((bool)InvokeStaticRaw("TryInterpolateUv", outsideUv)!);
        outsideUv[0] = new Vec3f(0, -1, 0);
        Assert.IsFalse((bool)InvokeStaticRaw("TryInterpolateUv", outsideUv)!);
    }

    /// <summary>
    /// Verifies the traceable Solid And Sun Coverage Handle Plants Glass And Dynamic Instances regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TraceableSolidAndSunCoverageHandlePlantsGlassAndDynamicInstances()
    {
        Assert.IsFalse(InvokeStatic<bool>("IsTraceableSolid", (object?)null));
        Assert.IsFalse(InvokeStatic<bool>(
            "IsTraceableSolid", Block(0, "game:void", EnumBlockMaterial.Stone)));
        Assert.IsFalse(InvokeStatic<bool>(
            "IsTraceableSolid", Block(181, "game:air", EnumBlockMaterial.Air)));
        Assert.IsFalse(InvokeStatic<bool>(
            "IsTraceableSolid", Block(182, "game:water", EnumBlockMaterial.Water)));
        FixtureBlock transparentPlant = Block(183, "game:transparentplant", EnumBlockMaterial.Plant);
        transparentPlant.RenderPass = EnumChunkRenderPass.Transparent;
        Assert.IsFalse(InvokeStatic<bool>("IsTraceableSolid", transparentPlant));
        FixtureBlock solidPlant = Block(184, "game:solidplant", EnumBlockMaterial.Plant);
        solidPlant.RenderPass = EnumChunkRenderPass.OpaqueNoCull;
        Assert.IsTrue(InvokeStatic<bool>("IsTraceableSolid", solidPlant));
        FixtureBlock emptyStone = Block(185, "game:emptystone", EnumBlockMaterial.Stone);
        emptyStone.CollisionBoxes = null;
        emptyStone.LightAbsorption = 0;
        Assert.IsFalse(InvokeStatic<bool>("IsTraceableSolid", emptyStone));
        emptyStone.LightAbsorption = 1;
        Assert.IsTrue(InvokeStatic<bool>("IsTraceableSolid", emptyStone));

        FixtureBlock partialPlant = Block(186, "game:partialplant", EnumBlockMaterial.Plant);
        partialPlant.RenderPass = EnumChunkRenderPass.OpaqueNoCull;
        using SceneFixture sun = new();
        sun.SetOrigins(0, 0, 0, 0, 0, 0);
        sun.SetSolid(0, 0, 0, partialPlant);
        sun.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy")[partialPlant.Id] =
            new CachedBlockOccupancy(1, true, 1, 0, 1, BlockGeometryKind.StaticComplex);
        ulong partialMask = sun.Invoke<ulong>("SampleSunOccupancyMask", 0, 0, 0);
        Assert.IsTrue(partialMask is > 0 and < ulong.MaxValue);
        Assert.IsFalse(InvokeStatic<bool>(
            "IsSunShadowCaster", Block(187, "game:glass", EnumBlockMaterial.Glass)));
        Assert.IsFalse(InvokeStatic<bool>(
            "IsSunShadowCaster", Block(188, "game:ice", EnumBlockMaterial.Ice)));

        FixtureBlock chisel = Block(189, "game:sun-chisel", EnumBlockMaterial.Stone);
        chisel.EntityClass = "BlockEntityChisel";
        FixtureBlockEntity entity = new(QuadMesh(0.5f), true)
        {
            Block = chisel,
            Pos = new BlockPos(0)
        };
        using SceneFixture tessellated = new(blockEntity: entity);
        tessellated.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy")[chisel.Id] =
            new CachedBlockOccupancy(ulong.MaxValue, true, 1, 0, 0, BlockGeometryKind.DynamicInstance);
        Assert.IsTrue(tessellated.Invoke<ulong>(
            "ResolveSunShadowMask", chisel, new BlockPos(0)) is > 0 and < ulong.MaxValue);

        FixtureBlock dynamic = Block(190, "game:sun-dynamic", EnumBlockMaterial.Stone);
        dynamic.EntityClass = "BlockEntityFixture";
        dynamic.Collision = [new Cuboidf(0, 0, 0, 0.5f, 1, 1)];
        dynamic.Selection = null;
        using SceneFixture fallback = new();
        fallback.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy")[dynamic.Id] =
            new CachedBlockOccupancy(0, false, 0, 0, 0, BlockGeometryKind.DynamicInstance);
        Assert.IsTrue(fallback.Invoke<ulong>(
            "ResolveSunShadowMask", dynamic, new BlockPos(0)) is > 0 and < ulong.MaxValue);

        FixtureBlock staticFallback = Block(195, "game:sun-static-fallback", EnumBlockMaterial.Stone);
        staticFallback.Collision = [new Cuboidf(0, 0, 0, 0.5f, 1, 1)];
        staticFallback.Selection = null;
        fallback.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy")[staticFallback.Id] =
            new CachedBlockOccupancy(0, false, 0, 0, 0, BlockGeometryKind.Unknown);
        Assert.IsTrue(fallback.Invoke<ulong>(
            "ResolveSunShadowMask", staticFallback, new BlockPos(0)) is > 0 and < ulong.MaxValue);
        staticFallback.Collision = null;
        staticFallback.LightAbsorption = 1;
        Assert.AreEqual(ulong.MaxValue, fallback.Invoke<ulong>(
            "ResolveSunShadowMask", staticFallback, new BlockPos(0)));
        staticFallback.LightAbsorption = 0;
        Assert.AreEqual(0UL, fallback.Invoke<ulong>(
            "ResolveSunShadowMask", staticFallback, new BlockPos(0)));

        FixtureBlock appendedReplacement = Block(192, "game:sun-appended-replacement", EnumBlockMaterial.Stone);
        appendedReplacement.EntityClass = "BlockEntityFixture";
        FixtureBlockEntity replacementEntity = new(QuadMesh(0.5f), true)
        {
            Block = appendedReplacement,
            Pos = new BlockPos(0)
        };
        using SceneFixture replacement = new(blockEntity: replacementEntity);
        replacement.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy")[appendedReplacement.Id] =
            new CachedBlockOccupancy(ulong.MaxValue, true, 1, 0, 0, BlockGeometryKind.DynamicInstance);
        Assert.IsTrue(replacement.Invoke<ulong>(
            "ResolveSunShadowMask", appendedReplacement, new BlockPos(0)) is > 0 and < ulong.MaxValue);
    }

    /// <summary>
    /// Verifies the contained Liquid Contract Rejects Invalid Inventory States And Encodes Valid Surface regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ContainedLiquidContractRejectsInvalidInventoryStatesAndEncodesValidSurface()
    {
        FixtureBlock noMetadata = Block(59, "game:plainbarrel", EnumBlockMaterial.Wood);
        FixtureBlock containerBlock = Block(60, "game:barrel", EnumBlockMaterial.Wood);
        containerBlock.Attributes = Json(
            """
            {"vintageRtxLiquidContainer":{"contentSlot":0,"capacityLitres":10,"surfaceMinimumY":0.2,"surfaceMaximumY":0.8,"alwaysOpen":true,"visibilityTreeBool":"","visibleWhen":false}}
            """);
        using SceneFixture fixture = new();
        object?[] noMetadataArgs = [noMetadata, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", noMetadataArgs)!);
        FixtureBlock anonymousBlock = Block(591, "game:anonymous", EnumBlockMaterial.Wood);
        anonymousBlock.Code = null!;
        object?[] anonymousArgs = [anonymousBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", anonymousArgs)!);
        object?[] absentArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", absentArgs)!);

        fixture.SetBlockEntity(new FixtureBlockEntity(new MeshData(false), false));
        object?[] notContainer = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", notContainer)!);

        fixture.SetBlockEntity(new FixtureContainerBlockEntity(Inventory(0, null)));
        object?[] emptyInventory = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", emptyInventory)!);

        fixture.SetBlockEntity(new FixtureContainerBlockEntity(Inventory(1, null)));
        object?[] emptySlot = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", emptySlot)!);

        Item solid = new() { Code = new AssetLocation("game:solid") };
        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(solid, 1)))));
        object?[] solidArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", solidArgs)!);

        Item invalidRatio = LiquidItem("game:invalid", 0.0f);
        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(invalidRatio, 10)))));
        object?[] invalidRatioArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", invalidRatioArgs)!);

        Item attributeLessLiquid = new()
        {
            Code = new AssetLocation("game:attribute-less-liquid"),
            MatterState = EnumMatterState.Liquid,
            Attributes = null!
        };
        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(attributeLessLiquid, 10)))));
        object?[] attributeLessArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", attributeLessArgs)!);

        Item nonFiniteRatio = LiquidItem("game:nonfinite", float.PositiveInfinity);
        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(nonFiniteRatio, 10)))));
        object?[] nonFiniteRatioArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", nonFiniteRatioArgs)!);

        Item overflowingFill = LiquidItem("game:overflowingfill", float.Epsilon);
        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(overflowingFill, int.MaxValue)))));
        object?[] overflowingFillArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", overflowingFillArgs)!);

        Item liquid = LiquidItem("game:honeyportion", 10.0f);
        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(liquid, 0)))));
        object?[] emptyStackArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", emptyStackArgs)!);

        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(liquid, 50)))));
        object?[] validArgs = [containerBlock, new BlockPos(0), null, null];
        Assert.IsTrue((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", validArgs)!);
        Assert.IsTrue((byte)validArgs[2]! > 0);
        Assert.IsTrue((byte)validArgs[3]! is > 0 and < 255);
        Assert.IsTrue(InvokeStatic<bool>("IsContainerLiquid", liquid));
        Item stateLiquid = new() { MatterState = EnumMatterState.Liquid };
        Assert.IsTrue(InvokeStatic<bool>("IsContainerLiquid", stateLiquid));
        Assert.IsFalse(InvokeStatic<bool>("IsContainerLiquid", solid));

        FixtureBlock lidded = Block(61, "game:liddedbarrel", EnumBlockMaterial.Wood);
        lidded.Attributes = Json(
            """
            {"vintageRtxLiquidContainer":{"contentSlot":0,"capacityLitres":10,"surfaceMinimumY":0.2,"surfaceMaximumY":0.8,"alwaysOpen":false,"visibilityTreeBool":"open","visibleWhen":true}}
            """);
        fixture.SetBlockEntity(new FixtureContainerBlockEntity(
            Inventory(1, new DummySlot(new ItemStack(liquid, 10))), "open", false));
        object?[] hiddenArgs = [lidded, new BlockPos(0), null, null];
        Assert.IsFalse((bool)fixture.InvokeRaw("TryGetVisibleContainedLiquid", hiddenArgs)!);
    }

    /// <summary>
    /// Verifies the optical Liquid Recognition And Fluid Column Ordering Are Defensive regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void OpticalLiquidRecognitionAndFluidColumnOrderingAreDefensive()
    {
        IBlockAccessor accessor = RuntimeCoverageDispatchProxy.Create<IBlockAccessor>(
            (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        BlockPos position = new(0);

        FixtureBlock state = Block(62, "game:state-liquid", EnumBlockMaterial.Water);
        state.MatterState = EnumMatterState.Liquid;
        Assert.IsTrue(InvokeStatic<bool>("IsOpticalLiquid", state, accessor, position));

        FixtureBlock declared = Block(63, "game:declared-liquid", EnumBlockMaterial.Water);
        declared.LiquidCode = "honey";
        Assert.IsTrue(InvokeStatic<bool>("IsOpticalLiquid", declared, accessor, position));

        FixtureBlock dynamic = Block(64, "game:dynamic-liquid", EnumBlockMaterial.Stone);
        dynamic.LiquidResult = "juice";
        Assert.IsTrue(InvokeStatic<bool>("IsOpticalLiquid", dynamic, accessor, position));
        dynamic.LiquidResult = " ";
        Assert.IsFalse(InvokeStatic<bool>("IsOpticalLiquid", dynamic, accessor, position));
        dynamic.ThrowLiquidCode = true;
        Assert.IsFalse(InvokeStatic<bool>("IsOpticalLiquid", dynamic, accessor, position));

        byte[] column = new byte[VoxelScene.Width * VoxelScene.Depth * 4];
        InvokeStatic<object>(
            "WriteFluidColumn", column, 0, 3, 0, (byte)4, (byte)5, VoxelLiquidFlags.FluidLayer);
        InvokeStatic<object>(
            "WriteFluidColumn", column, 0, 1, 0, (byte)9, (byte)1, VoxelLiquidFlags.Contained);
        CollectionAssert.AreEqual(
            new byte[] { 4, 4, 5, (byte)VoxelLiquidFlags.FluidLayer },
            column.Take(4).ToArray());
    }

    /// <summary>
    /// Verifies the dynamic Write Occupancy Uses Authoritative Meshes Boxes And Fallback Statistics regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void DynamicWriteOccupancyUsesAuthoritativeMeshesBoxesAndFallbackStatistics()
    {
        FixtureBlock chisel = Block(70, "game:chisel", EnumBlockMaterial.Stone);
        chisel.EntityClass = "BlockEntityChisel";
        FixtureBlockEntity entity = new(QuadMesh(0.4f), true)
        {
            Block = chisel,
            Pos = new BlockPos(0)
        };
        using SceneFixture captured = new(
            meshes: new Dictionary<int, MeshData?> { [70] = CubeMesh() },
            blockEntity: entity);
        byte[] destination = captured.Field<byte[]>("buildOccupancy");
        captured.Invoke<object>("WriteOccupancy", destination, chisel, 0, 0, 0, 0, 0, 0, true);
        Assert.IsTrue(destination.Any(value => value == 255));

        FixtureBlock collision = Block(71, "game:dynamic-collision", EnumBlockMaterial.Stone);
        collision.EntityClass = "BlockEntityFixture";
        collision.Collision = [new Cuboidf(0, 0, 0, 0.25f, 1, 1)];
        collision.Selection = [new Cuboidf(0, 0, 0, 1, 1, 1)];
        using SceneFixture boxes = new(meshes: new Dictionary<int, MeshData?> { [71] = new MeshData(false) });
        byte[] boxesDestination = boxes.Field<byte[]>("buildOccupancy");
        boxes.Invoke<object>("WriteOccupancy", boxesDestination, collision, 0, 0, 0, 0, 0, 0, true);
        Assert.IsTrue(boxesDestination.Any(value => value == 255));
        Assert.AreEqual(1, boxes.Field<int>("collisionOccupancyBlocks"));

        collision.Collision = null;
        collision.Selection = [new Cuboidf(0, 0, 0, 0.5f, 1, 1)];
        boxes.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy").Clear();
        Array.Clear(boxesDestination);
        boxes.Invoke<object>("WriteOccupancy", boxesDestination, collision, 0, 0, 0, 0, 0, 0, true);
        Assert.AreEqual(1, boxes.Field<int>("selectionOccupancyBlocks"));

        collision.Collision = null;
        collision.Selection = null;
        collision.LightAbsorption = 1;
        boxes.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy").Clear();
        boxes.Invoke<object>("WriteOccupancy", boxesDestination, collision, 0, 0, 0, 0, 0, 0, true);
        Assert.IsTrue(boxes.Field<int>("fallbackOccupancyBlocks") > 0);
        Assert.IsTrue(boxes.Field<Dictionary<(string Code, string Reason), int>>(
            "fallbackOccupancyHistogram").Count > 0);
    }

    /// <summary>
    /// Verifies the write Occupancy Branch Matrix Covers Every Geometry Fallback Source regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WriteOccupancyBranchMatrixCoversEveryGeometryFallbackSource()
    {
        using SceneFixture fixture = new();
        byte[] destination = fixture.Field<byte[]>("buildOccupancy");
        Dictionary<int, CachedBlockOccupancy> cache =
            fixture.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy");
        int nextId = 90;

        FixtureBlock Candidate(
            string code,
            BlockGeometryKind kind,
            ulong mask,
            bool detailed,
            Cuboidf[]? collision = null,
            Cuboidf[]? selection = null,
            int absorption = 0)
        {
            FixtureBlock block = Block(nextId++, $"game:{code}", EnumBlockMaterial.Stone);
            block.EntityClass = kind == BlockGeometryKind.DynamicInstance ? "BlockEntityFixture" : null;
            block.Collision = collision;
            block.Selection = selection;
            block.LightAbsorption = absorption;
            cache[block.Id] = new CachedBlockOccupancy(mask, detailed, 0, 0, 0, kind);
            return block;
        }

        void Write(FixtureBlock block, bool statistics = true)
        {
            Array.Clear(destination);
            fixture.Invoke<object>("WriteOccupancy", destination, block, 0, 0, 0, 0, 0, 0, statistics);
        }

        Cuboidf[] partial = [new Cuboidf(0, 0, 0, 0.5f, 1, 1)];
        Cuboidf[] full = [new Cuboidf(0, 0, 0, 1, 1, 1)];

        Write(Candidate("dynamic-default", BlockGeometryKind.DynamicInstance, 1, true));
        Write(Candidate("dynamic-collision", BlockGeometryKind.DynamicInstance, 0, false, partial, full));
        Write(Candidate("dynamic-selection", BlockGeometryKind.DynamicInstance, 0, false, null, partial));
        Write(Candidate("dynamic-empty", BlockGeometryKind.DynamicInstance, 0, true));
        Assert.IsFalse(destination.Any(value => value != 0));
        Write(Candidate("dynamic-full-collision", BlockGeometryKind.DynamicInstance, 0, false, full));
        Write(Candidate("dynamic-full-selection", BlockGeometryKind.DynamicInstance, 0, false, null, full));
        Write(Candidate("dynamic-absorbing", BlockGeometryKind.DynamicInstance, 0, false, null, null, 1));
        Write(Candidate("dynamic-no-statistics", BlockGeometryKind.DynamicInstance, 0, false, partial), false);
        FixtureBlock replacingFallback = Candidate(
            "dynamic-replacing-collision", BlockGeometryKind.DynamicInstance, 0, false, partial);
        replacingFallback.EntityClass = "BlockEntityChisel";
        Write(replacingFallback);

        Write(Candidate("static-cube", BlockGeometryKind.FullCubeStatic, 0, false));
        Assert.IsTrue(destination.Any(value => value == 255));
        Write(Candidate("static-cube-no-statistics", BlockGeometryKind.FullCubeStatic, 0, false), false);
        Write(Candidate("static-detailed-empty", BlockGeometryKind.StaticComplex, 0, true));
        Assert.IsFalse(destination.Any(value => value != 0));
        Write(Candidate("static-detailed", BlockGeometryKind.StaticComplex, 1, true));
        Write(Candidate("static-detailed-no-statistics", BlockGeometryKind.StaticComplex, 1, true), false);
        Write(Candidate("static-collision", BlockGeometryKind.Unknown, 0, false, partial, full));
        Write(Candidate("static-collision-no-statistics", BlockGeometryKind.Unknown, 0, false, partial), false);
        Write(Candidate("static-selection", BlockGeometryKind.Unknown, 0, false, null, partial));
        Write(Candidate("static-selection-no-statistics", BlockGeometryKind.Unknown, 0, false, null, partial), false);
        Write(Candidate("static-cached", BlockGeometryKind.Unknown, 1, false));
        Write(Candidate("static-cached-no-statistics", BlockGeometryKind.Unknown, 1, false), false);
        Write(Candidate("static-full-collision", BlockGeometryKind.Unknown, 0, false, full));
        Write(Candidate("static-full-selection", BlockGeometryKind.Unknown, 0, false, null, full));
        Write(Candidate("static-absorbing", BlockGeometryKind.Unknown, 0, false, null, null, 1));
        Write(Candidate("static-empty", BlockGeometryKind.Unknown, 0, false));
        Assert.IsFalse(destination.Any(value => value != 0));

        Dictionary<(string Code, string Reason), int> reasons =
            fixture.Field<Dictionary<(string Code, string Reason), int>>("fallbackOccupancyHistogram");
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "dynamic-full-collision", "dynamic-full-selection", "dynamic-absorbing-no-shape",
                "static-full-collision", "static-full-selection", "static-absorbing-no-shape"
            },
            reasons.Keys.Select(key => key.Reason).ToArray());

        FixtureBlock replacing = Candidate(
            "chisel-authoritative-empty", BlockGeometryKind.DynamicInstance, ulong.MaxValue, true);
        replacing.EntityClass = "BlockEntityChisel";
        FixtureBlockEntity emptyEntity = new(new MeshData(false), true) { Block = replacing, Pos = new BlockPos(0) };
        using SceneFixture authoritativeEmpty = new(blockEntity: emptyEntity);
        authoritativeEmpty.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy")[replacing.Id] =
            new CachedBlockOccupancy(ulong.MaxValue, true, 0, 0, 0, BlockGeometryKind.DynamicInstance);
        byte[] authoritativeDestination = authoritativeEmpty.Field<byte[]>("buildOccupancy");
        authoritativeEmpty.Invoke<object>(
            "WriteOccupancy", authoritativeDestination, replacing, 0, 0, 0, 0, 0, 0, true);
        Assert.IsFalse(authoritativeDestination.Any(value => value != 0));

        MeshData transparentMesh = QuadMesh(0.5f);
        transparentMesh.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        transparentMesh.RenderPassCount = 1;
        FixtureBlock meshEmpty = Candidate(
            "dynamic-instance-empty", BlockGeometryKind.DynamicInstance, 0, false, null, null, 1);
        FixtureBlockEntity meshEmptyEntity = new(transparentMesh, false) { Block = meshEmpty, Pos = new BlockPos(0) };
        using SceneFixture emptyMeshFixture = new(blockEntity: meshEmptyEntity);
        emptyMeshFixture.Field<Dictionary<int, CachedBlockOccupancy>>("cachedBlockOccupancy")[meshEmpty.Id] =
            new CachedBlockOccupancy(0, false, 0, 0, 0, BlockGeometryKind.DynamicInstance);
        emptyMeshFixture.Invoke<object>(
            "WriteOccupancy", emptyMeshFixture.Field<byte[]>("buildOccupancy"), meshEmpty,
            0, 0, 0, 0, 0, 0, true);
        Assert.IsTrue(emptyMeshFixture.Field<Dictionary<(string Code, string Reason), int>>(
            "fallbackOccupancyHistogram").Keys.Any(key => key.Reason == "dynamic-instance-mesh-empty"));
    }

    /// <summary>
    /// Verifies the alpha Candidate Graph Canonicalizes Deduplicates And Rejects Unavailable Textures regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AlphaCandidateGraphCanonicalizesDeduplicatesAndRejectsUnavailableTextures()
    {
        TextureAtlasPosition position = new()
        {
            atlasTextureId = 91,
            atlasNumber = 0,
            x1 = 0.1f,
            y1 = 0.2f,
            x2 = 0.6f,
            y2 = 0.8f
        };
        BakedCompositeTexture baked = new()
        {
            TextureSubId = 0,
            TextureFilenames = [new AssetLocation("game:Block\\PLANT")]
        };
        baked.BakedVariants = [baked, null!];
        baked.BakedTiles = [new BakedCompositeTexture { TextureSubId = -1 }, null!];
        baked.TextureFilenames = [new AssetLocation("game:Block\\PLANT"), null!];
        CompositeTexture root = new(new AssetLocation("game:block/plant")) { Baked = baked };
        root.Alternates = [root, null!];
        root.Tiles = [new CompositeTexture(new AssetLocation("game:block/tile")), null!];
        FixtureBlock plant = Block(80, "game:plant", EnumBlockMaterial.Plant);
        plant.Textures = new Dictionary<string, CompositeTexture> { ["all"] = root, ["null"] = null! };
        AssetLocation transparentSource = new("game", "textures/block/plant.png");
        AssetLocation opaqueSource = new("game", "textures/block/opaque.png");
        AssetLocation brokenSource = new("game", "textures/block/broken.png");
        IAsset brokenAsset = RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "ToBitmap"
                ? throw new InvalidDataException("fixture bitmap")
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        using SceneFixture fixture = new(
            atlasPositions: [position],
            assets: new Dictionary<AssetLocation, IAsset>
            {
                [transparentSource] = BitmapAsset(
                    new SKColor(20, 30, 40, 255),
                    new SKColor(20, 30, 40, 0)),
                [opaqueSource] = BitmapAsset(
                    new SKColor(20, 30, 40, 255),
                    new SKColor(20, 30, 40, 255)),
                [brokenSource] = brokenAsset
            });

        List<TextureAlphaCandidate> candidates = fixture.Invoke<List<TextureAlphaCandidate>>(
            "BuildTextureAlphaCandidates", plant);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("textures/block/plant.png", candidates[0].Source.Path);
        Assert.IsTrue(InvokeStatic<bool>("ContainsUv", position, 0.2f, 0.3f));
        Assert.IsFalse(InvokeStatic<bool>("ContainsUv", position, 0.0f, 0.0f));
        Assert.IsFalse(InvokeStatic<bool>("ContainsUv", position, 0.9f, 0.3f));
        Assert.IsFalse(InvokeStatic<bool>("ContainsUv", position, 0.2f, 0.0f));
        Assert.IsFalse(InvokeStatic<bool>("ContainsUv", position, 0.2f, 0.9f));
        AssetLocation canonical = InvokeStatic<AssetLocation>(
            "CanonicalTextureAsset", new AssetLocation("MOD", "Block\\STONE"));
        Assert.AreEqual("textures/Block/STONE.png", canonical.Path);
        Assert.AreEqual(
            "textures/block/stone.png",
            InvokeStatic<AssetLocation>(
                "CanonicalTextureAsset", new AssetLocation("mod", "textures/block/stone.png")).Path);

        FixtureBlock textureless = Block(81, "game:textureless", EnumBlockMaterial.Stone);
        textureless.Textures = null;
        Assert.AreEqual(0, fixture.Invoke<List<TextureAlphaCandidate>>(
            "BuildTextureAlphaCandidates", textureless).Count);
        List<TextureAlphaCandidate> filenameOnly = [];
        InvokeStatic<object>(
            "AddBakedAlphaCandidates",
            null,
            new BakedCompositeTexture
            {
                TextureSubId = 0,
                TextureFilenames = [new AssetLocation("game:block/plant")]
            },
            new[] { position },
            filenameOnly);
        Assert.AreEqual(1, filenameOnly.Count);

        List<TextureAlphaCandidate> duplicates = [];
        InvokeStatic<object>("AddAlphaCandidate", position, new AssetLocation("game:block/plant"), duplicates);
        InvokeStatic<object>("AddAlphaCandidate", position, new AssetLocation("game:textures/block/plant.png"), duplicates);
        InvokeStatic<object>(
            "AddAlphaCandidate",
            new TextureAtlasPosition
            {
                atlasTextureId = 92,
                x1 = position.x1,
                y1 = position.y1,
                x2 = position.x2,
                y2 = position.y2
            },
            new AssetLocation("game:block/plant"),
            duplicates);
        InvokeStatic<object>(
            "AddAlphaCandidate",
            new TextureAtlasPosition
            {
                atlasTextureId = position.atlasTextureId,
                x1 = 0.15f,
                y1 = position.y1,
                x2 = position.x2,
                y2 = position.y2
            },
            new AssetLocation("game:block/plant"),
            duplicates);
        InvokeStatic<object>(
            "AddAlphaCandidate",
            new TextureAtlasPosition
            {
                atlasTextureId = position.atlasTextureId,
                x1 = position.x1,
                y1 = 0.25f,
                x2 = position.x2,
                y2 = position.y2
            },
            new AssetLocation("game:block/plant"),
            duplicates);
        InvokeStatic<object>(
            "AddAlphaCandidate", position, new AssetLocation("game:block/other"), duplicates);
        Assert.AreEqual(5, duplicates.Count);

        MeshData noUv = QuadMesh(0.5f);
        Assert.IsNull(fixture.Invoke<object?>("ResolveTextureAlphaSampler", noUv, 0, 0, 1, 2, candidates));
        MeshData noIndices = QuadMesh(0.5f);
        noIndices.Uv = [0.2f, 0.3f, 0.3f, 0.3f, 0.3f, 0.4f, 0.2f, 0.4f];
        Assert.IsNull(fixture.Invoke<object?>("ResolveTextureAlphaSampler", noIndices, 0, 0, 1, 2, candidates));
        noIndices.TextureIndices = [0];
        Assert.IsNull(fixture.Invoke<object?>("ResolveTextureAlphaSampler", noIndices, 0, 0, 1, 2, candidates));
        MeshData mapped = QuadMesh(0.5f);
        mapped.Uv = [0.2f, 0.3f, 0.3f, 0.3f, 0.3f, 0.4f, 0.2f, 0.4f];
        mapped.TextureIndices = [0];
        mapped.TextureIds = [91];
        Assert.IsNotNull(fixture.Invoke<object?>("ResolveTextureAlphaSampler", mapped, 0, 0, 1, 2, candidates));
        List<TextureAlphaCandidate> missingCandidates =
        [
            new TextureAlphaCandidate(
                position,
                new AssetLocation("game", "textures/block/not-present.png"))
        ];
        Assert.IsNull(fixture.Invoke<object?>(
            "ResolveTextureAlphaSampler", mapped, 0, 0, 1, 2, missingCandidates));
        TextureAlphaData? decoded = fixture.Invoke<TextureAlphaData?>("GetTextureAlpha", candidates[0].Source);
        Assert.IsNotNull(decoded);
        CollectionAssert.AreEqual(new byte[] { 255, 0 }, decoded.Alpha);
        Assert.AreSame(decoded, fixture.Invoke<TextureAlphaData?>("GetTextureAlpha", candidates[0].Source));

        mapped.TextureIndices = [1];
        Assert.IsNull(fixture.Invoke<object?>("ResolveTextureAlphaSampler", mapped, 0, 0, 1, 2, candidates));
        mapped.TextureIndices = [0];
        Assert.IsNull(fixture.Invoke<object?>("ResolveTextureAlphaSampler", mapped, 1, 0, 1, 2, candidates));
        Assert.IsNull(fixture.Invoke<object?>(
            "ResolveTextureAlphaSampler",
            mapped,
            0,
            0,
            1,
            2,
            new List<TextureAlphaCandidate>
            {
                new(
                    new TextureAtlasPosition
                    {
                        atlasTextureId = 999,
                        x1 = 0,
                        y1 = 0,
                        x2 = 1,
                        y2 = 1
                    },
                    transparentSource),
                new(
                    new TextureAtlasPosition
                    {
                        atlasTextureId = 91,
                        x1 = 0.8f,
                        y1 = 0.8f,
                        x2 = 1,
                        y2 = 1
                    },
                    transparentSource)
            }));
        Assert.IsNull(fixture.Invoke<TextureAlphaData?>("GetTextureAlpha", opaqueSource));
        Assert.IsNull(fixture.Invoke<TextureAlphaData?>("GetTextureAlpha", brokenSource));
        Assert.IsNull(fixture.Invoke<TextureAlphaData?>(
            "GetTextureAlpha", new AssetLocation("game", "textures/block/missing.png")));
    }

    /// <summary>
    /// Verifies the radiance Helpers Cover Seeds Visibility Smoothing Gamma And Non Empty Transport regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void RadianceHelpersCoverSeedsVisibilitySmoothingGammaAndNonEmptyTransport()
    {
        int cellCount = VoxelScene.Width * VoxelScene.Height * VoxelScene.Depth;
        float[] seeds = new float[cellCount * 3];
        float[] directions = new float[cellCount * 3];
        float[] weights = new float[cellCount];
        InvokeStatic<object>("AddIrradianceSeed", seeds, directions, weights, 0,
            -1.0f, 1.0f, 3.0f, 1.0f, 0.0f, 0.0f);
        Assert.AreEqual(0.0f, seeds[0]);
        Assert.AreEqual(1.0f, seeds[1]);
        Assert.AreEqual(2.0f, seeds[2]);
        Assert.IsTrue(weights[0] > 0);
        InvokeStatic<object>("AddIrradianceSeed", seeds, directions, weights, 1,
            -1.0f, -1.0f, -1.0f, 0.0f, 1.0f, 0.0f);
        Assert.AreEqual(0.0f, weights[1]);

        byte[] voxels = new byte[cellCount * 4];
        Assert.IsTrue(InvokeStatic<bool>("TraceIrradianceVisibility", voxels,
            1.5f, 1.5f, 1.5f, 4.5f, 1.5f, 1.5f));
        int obstacle = (1 * VoxelScene.Height + 1) * VoxelScene.Width + 2;
        voxels[obstacle * 4 + 3] = 128;
        Assert.IsFalse(InvokeStatic<bool>("TraceIrradianceVisibility", voxels,
            1.5f, 1.5f, 1.5f, 4.5f, 1.5f, 1.5f));
        Assert.IsFalse(InvokeStatic<bool>("TraceIrradianceVisibility", voxels,
            1.5f, 1.5f, 1.5f, -4.5f, 1.5f, 1.5f));
        Array.Clear(voxels);
        int endpoint = (1 * VoxelScene.Height + 1) * VoxelScene.Width + 2;
        voxels[endpoint * 4 + 3] = 128;
        Assert.IsTrue(InvokeStatic<bool>("TraceIrradianceVisibility", voxels,
            1.5f, 1.5f, 1.5f, 2.5f, 1.5f, 1.5f));
        Assert.IsTrue(InvokeStatic<bool>("TraceIrradianceVisibility", voxels,
            1.5f, 1.5f, 1.5f, 1.5f, 1.5f, 1.5f));
        Assert.AreEqual(0.0f, InvokeStatic<float>("SmoothStep", 1.0f, 2.0f, 0.0f));
        Assert.AreEqual(1.0f, InvokeStatic<float>("SmoothStep", 1.0f, 2.0f, 3.0f));
        Assert.IsTrue(InvokeStatic<float>("SmoothStep", 1.0f, 2.0f, 1.5f) is > 0 and < 1);
        Assert.AreEqual(1.0f, InvokeStatic<float>("SmoothStep", 2.0f, 1.0f, 3.0f));
        Assert.AreEqual(0.0f, InvokeStatic<float>("SrgbToLinear", -1.0f));
        Assert.AreEqual(1.0f, InvokeStatic<float>("SrgbToLinear", 2.0f));
        Assert.IsTrue(InvokeStatic<float>("SrgbToLinear", 0.5f) is > 0 and < 0.5f);
        Assert.AreEqual(0.0f, InvokeStatic<float>("IrradianceLuminance", seeds, voxels, -1, 0, 0));
        Assert.AreEqual(0.0f, InvokeStatic<float>("IrradianceLuminance", seeds, voxels, 2, 1, 1));
        object?[] neighborArgs = [seeds, voxels, 2, 1, 1, 0, 0.0f, 0.0f, 0];
        InvokeStatic<object>("AccumulateIrradianceNeighbor", neighborArgs);
        Assert.AreEqual(0, neighborArgs[8]);

        byte[] boundaryVoxels = new byte[cellCount * 4];
        boundaryVoxels[3] = 128;
        boundaryVoxels[7] = 128;
        VoxelRadianceField boundaryField = VoxelScene.BuildRadianceField(
            boundaryVoxels,
            [new VoxelLight(2.5f, 0.5f, 0.5f, 1, 1, 1, 2, "boundary", [])],
            0,
            0,
            0,
            new Vec3f());
        Assert.AreEqual(cellCount * 3, boundaryField.Irradiance.Length);
    }

    /// <summary>
    /// Verifies the complete Rebuild Publishes Arrays Lights Radiance And Diagnostics regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CompleteRebuildPublishesArraysLightsRadianceAndDiagnostics()
    {
        using SceneFixture fixture = new(withPlayer: true, daylight: 1.5f);
        fixture.SetOrigins(0, 0, 0, 0, 0, 0);
        byte[] voxels = fixture.Field<byte[]>("buildVoxels");
        int center = (24 * VoxelScene.Height + 20) * VoxelScene.Width + 20;
        voxels[center * 4] = 180;
        voxels[center * 4 + 1] = 140;
        voxels[center * 4 + 2] = 100;
        voxels[center * 4 + 3] = 128;
        int boundary = 0;
        voxels[boundary * 4] = 80;
        voxels[boundary * 4 + 1] = 100;
        voxels[boundary * 4 + 2] = 120;
        voxels[boundary * 4 + 3] = 128;
        int blockedColumn = VoxelScene.Width;
        voxels[blockedColumn * 4] = 100;
        voxels[blockedColumn * 4 + 1] = 80;
        voxels[blockedColumn * 4 + 2] = 60;
        voxels[blockedColumn * 4 + 3] = 192;
        foreach ((int x, int y, int z) in new[]
        {
            (9, 10, 10), (11, 10, 10), (10, 9, 10),
            (10, 11, 10), (10, 10, 9), (10, 10, 11)
        })
        {
            int shell = (z * VoxelScene.Height + y) * VoxelScene.Width + x;
            voxels[shell * 4] = 90;
            voxels[shell * 4 + 1] = 90;
            voxels[shell * 4 + 2] = 90;
            voxels[shell * 4 + 3] = 128;
        }
        byte[] diagnosticCaster = new byte[16 * 16 * 16];
        diagnosticCaster[0] = 255;
        diagnosticCaster[1] = 128;
        diagnosticCaster[2] = 1;
        fixture.Field<List<VoxelLight>>("buildLights").Add(
            new VoxelLight(
                20.5f, 22.5f, 24.5f, 1.0f, 0.7f, 0.4f, 12.0f, "fixture", diagnosticCaster));
        fixture.Field<List<VoxelLight>>("buildLights").Add(
            new VoxelLight(21.001f, 20.5f, 24.5f, 0.2f, 0.4f, 1.0f, 4.0f, "surface", []));
        fixture.Field<Dictionary<(string Code, string Reason), int>>(
            "fallbackOccupancyHistogram")[("fixture", "reason")] = 1;
        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", null);
            fixture.Invoke<object>("CompleteRebuild");
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", "voxel-coverage");
            fixture.Invoke<object>("CompleteRebuild");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_RUN_ID", previous);
        }
        Assert.AreEqual(2, fixture.Field<int>("generation"));
        Assert.IsTrue(fixture.Field<bool>("uploadPending"));
        Assert.IsFalse(fixture.Field<bool>("building"));
        Assert.AreEqual(2, fixture.Field<VoxelLight[]>("readyLights").Length);
        Assert.IsTrue(fixture.Field<byte[]>("readyIrradiance").Any(value => value > 0));
    }

    /// <summary>
    /// Executes the block step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <param name="id">The id input used to configure this deterministic test path.</param>
    /// <param name="code">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="material">The material input used to configure this deterministic test path.</param>
    /// <returns>The block result consumed by the caller&apos;s assertion.</returns>
    private static FixtureBlock Block(int id, string code, EnumBlockMaterial material) => new()
    {
        BlockId = id,
        Code = new AssetLocation(code),
        BlockMaterial = material,
        LightAbsorption = material == EnumBlockMaterial.Air ? 0 : 1,
        RenderPass = EnumChunkRenderPass.Opaque,
        Collision = [new Cuboidf(0, 0, 0, 1, 1, 1)],
        Selection = [new Cuboidf(0, 0, 0, 1, 1, 1)]
    };

    /// <summary>
    /// Executes the json step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <param name="json">The json input used to configure this deterministic test path.</param>
    /// <returns>The json result consumed by the caller&apos;s assertion.</returns>
    private static JsonObject Json(string json) => new(JObject.Parse(json));

    /// <summary>
    /// Executes the liquid Item step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <param name="code">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="itemsPerLitre">The items Per Litre input used to configure this deterministic test path.</param>
    /// <returns>The liquid Item result consumed by the caller&apos;s assertion.</returns>
    private static Item LiquidItem(string code, float itemsPerLitre) => new()
    {
        Code = new AssetLocation(code),
        Attributes = new JsonObject(new JObject
        {
            ["waterTightContainerProps"] = new JObject
            {
                ["itemsPerLitre"] = itemsPerLitre
            }
        })
    };

    /// <summary>
    /// Executes the inventory step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <param name="count">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <param name="slot">The slot input used to configure this deterministic test path.</param>
    /// <returns>The inventory result consumed by the caller&apos;s assertion.</returns>
    private static IInventory Inventory(int count, ItemSlot? slot) =>
        RuntimeCoverageDispatchProxy.Create<IInventory>((method, args) => method.Name switch
        {
            "get_Count" => count,
            "get_Item" => slot,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>
    /// Executes the bitmap Asset step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <param name="pixels">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The bitmap Asset result consumed by the caller&apos;s assertion.</returns>
    private static IAsset BitmapAsset(params SKColor[] pixels) =>
        RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
        {
            if (method.Name == "ToBitmap")
            {
                SKBitmap bitmap = new(pixels.Length, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                for (int x = 0; x < pixels.Length; x++)
                {
                    bitmap.SetPixel(x, 0, pixels[x]);
                }
                return new BitmapExternal(bitmap);
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

    /// <summary>
    /// Invokes static through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The invoke Static result consumed by the caller&apos;s assertion.</returns>
    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        MethodInfo method = typeof(VoxelScene).GetMethod(
            name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException(typeof(VoxelScene).FullName, name);
        return (T)method.Invoke(null, args)!;
    }

    /// <summary>
    /// Invokes static Raw through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>The invoke Static Raw result consumed by the caller&apos;s assertion.</returns>
    private static object? InvokeStaticRaw(string name, object?[] args)
    {
        MethodInfo method = typeof(VoxelScene).GetMethod(
            name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException(typeof(VoxelScene).FullName, name);
        return method.Invoke(null, args);
    }

    /// <summary>
    /// Executes the quad Mesh step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The quad Mesh result consumed by the caller&apos;s assertion.</returns>
    private static MeshData QuadMesh(float z)
    {
        return new MeshData(false)
        {
            xyz = [0, 0, z, 1, 0, z, 1, 1, z, 0, 1, z],
            Indices = [0, 1, 2, 0, 2, 3],
            VerticesCount = 4,
            IndicesCount = 6,
            IndicesPerFace = 6
        };
    }

    /// <summary>
    /// Executes the crossed Planes step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <returns>The crossed Planes result consumed by the caller&apos;s assertion.</returns>
    private static MeshData CrossedPlanes()
    {
        MeshData first = new(false)
        {
            xyz =
            [
                0, 0, 0, 1, 0, 1, 1, 1, 1, 0, 1, 0,
                1, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 0
            ],
            Indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7],
            VerticesCount = 8,
            IndicesCount = 12,
            IndicesPerFace = 6,
            RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.OpaqueNoCull, (short)EnumChunkRenderPass.OpaqueNoCull],
            RenderPassCount = 2
        };
        return first;
    }

    /// <summary>
    /// Executes the cube Mesh step used by the deterministic voxel Scene Coverage Deep Tests fixture.
    /// </summary>
    /// <returns>The cube Mesh result consumed by the caller&apos;s assertion.</returns>
    private static MeshData CubeMesh()
    {
        Vec3f[][] faces =
        [
            [new(0,0,0), new(0,1,0), new(0,0,1), new(0,1,1)],
            [new(1,0,0), new(1,1,0), new(1,0,1), new(1,1,1)],
            [new(0,0,0), new(1,0,0), new(0,0,1), new(1,0,1)],
            [new(0,1,0), new(1,1,0), new(0,1,1), new(1,1,1)],
            [new(0,0,0), new(1,0,0), new(0,1,0), new(1,1,0)],
            [new(0,0,1), new(1,0,1), new(0,1,1), new(1,1,1)]
        ];
        List<float> vertices = [];
        List<int> indices = [];
        foreach (Vec3f[] face in faces)
        {
            int start = vertices.Count / 3;
            foreach (Vec3f vertex in face)
            {
                vertices.Add(vertex.X);
                vertices.Add(vertex.Y);
                vertices.Add(vertex.Z);
            }
            indices.AddRange([start, start + 1, start + 3, start, start + 3, start + 2]);
        }
        return new MeshData(false)
        {
            xyz = vertices.ToArray(),
            Indices = indices.ToArray(),
            VerticesCount = 24,
            IndicesCount = 36,
            IndicesPerFace = 6
        };
    }

    /// <summary>
    /// Supports fixture Block within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class FixtureBlock : Block
    {
        /// <summary>
        /// Gets or sets the collision value exposed to the deterministic fixture.
        /// </summary>
        internal Cuboidf[]? Collision { get; set; }
        /// <summary>
        /// Gets or sets the selection value exposed to the deterministic fixture.
        /// </summary>
        internal Cuboidf[]? Selection { get; set; }
        /// <summary>
        /// Gets or sets the light value exposed to the deterministic fixture.
        /// </summary>
        internal byte[] Light { get; set; } = [0, 0, 0];
        /// <summary>
        /// Gets or sets the color value exposed to the deterministic fixture.
        /// </summary>
        internal int Color { get; set; } = unchecked((int)0xff336699);
        /// <summary>
        /// Gets or sets the liquid Result value exposed to the deterministic fixture.
        /// </summary>
        internal string? LiquidResult { get; set; }
        /// <summary>
        /// Gets or sets the throw Liquid Code value exposed to the deterministic fixture.
        /// </summary>
        internal bool ThrowLiquidCode { get; set; }

        /// <summary>
        /// Returns collision Boxes from deterministic fixture state for use by the caller&apos;s assertion.
        /// </summary>
        /// <param name="blockAccessor">The block Accessor input used to configure this deterministic test path.</param>
        /// <param name="pos">The pos input used to configure this deterministic test path.</param>
        /// <returns>The get Collision Boxes result consumed by the caller&apos;s assertion.</returns>
        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => Collision!;
        /// <summary>
        /// Returns selection Boxes from deterministic fixture state for use by the caller&apos;s assertion.
        /// </summary>
        /// <param name="blockAccessor">The block Accessor input used to configure this deterministic test path.</param>
        /// <param name="pos">The pos input used to configure this deterministic test path.</param>
        /// <returns>The get Selection Boxes result consumed by the caller&apos;s assertion.</returns>
        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => Selection!;
        /// <summary>
        /// Returns light Hsv from deterministic fixture state for use by the caller&apos;s assertion.
        /// </summary>
        /// <param name="blockAccessor">The block Accessor input used to configure this deterministic test path.</param>
        /// <param name="pos">The pos input used to configure this deterministic test path.</param>
        /// <param name="stack">The stack input used to configure this deterministic test path.</param>
        /// <returns>The get Light Hsv result consumed by the caller&apos;s assertion.</returns>
        public override byte[] GetLightHsv(IBlockAccessor blockAccessor, BlockPos pos, ItemStack? stack = null) => Light;
        /// <summary>
        /// Returns color from deterministic fixture state for use by the caller&apos;s assertion.
        /// </summary>
        /// <param name="capi">Vintage Story API facade or test double supplied to the scenario.</param>
        /// <param name="pos">The pos input used to configure this deterministic test path.</param>
        /// <returns>The get Color result consumed by the caller&apos;s assertion.</returns>
        public override int GetColor(ICoreClientAPI capi, BlockPos pos) => Color;
        /// <summary>
        /// Returns liquid Code from deterministic fixture state for use by the caller&apos;s assertion.
        /// </summary>
        /// <param name="blockAccessor">The block Accessor input used to configure this deterministic test path.</param>
        /// <param name="pos">The pos input used to configure this deterministic test path.</param>
        /// <returns>The get Liquid Code result consumed by the caller&apos;s assertion.</returns>
        public override string GetLiquidCode(IBlockAccessor blockAccessor, BlockPos pos) =>
            ThrowLiquidCode ? throw new InvalidOperationException("fixture liquid") : LiquidResult!;
    }

    /// <summary>
    /// Supports throwing Light Block within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class ThrowingLightBlock : Block
    {
        /// <summary>
        /// Returns light Hsv from deterministic fixture state for use by the caller&apos;s assertion.
        /// </summary>
        /// <param name="blockAccessor">The block Accessor input used to configure this deterministic test path.</param>
        /// <param name="pos">The pos input used to configure this deterministic test path.</param>
        /// <param name="stack">The stack input used to configure this deterministic test path.</param>
        /// <returns>The get Light Hsv result consumed by the caller&apos;s assertion.</returns>
        public override byte[] GetLightHsv(IBlockAccessor blockAccessor, BlockPos pos, ItemStack? stack = null) =>
            throw new InvalidOperationException("fixture");
    }

    /// <summary>
    /// Supports fixture Block Entity within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class FixtureBlockEntity(MeshData mesh, bool skipsDefault) : BlockEntity
    {
        /// <summary>Writes no persistent state for the geometry-only fixture entity.</summary>
        /// <param name="tree">Destination state ignored by this fixture.</param>
        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            _ = tree;
        }

        /// <summary>
        /// Handles tesselation for the test double and records only the state required by later assertions.
        /// </summary>
        /// <param name="mesher">The mesher input used to configure this deterministic test path.</param>
        /// <param name="tessellator">The tessellator input used to configure this deterministic test path.</param>
        /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessellator)
        {
            mesher.AddMeshData(mesh);
            return skipsDefault;
        }
    }

    /// <summary>Multiblock proxy that serializes the public principal coordinates used by the game.</summary>
    private sealed class FixtureMultiblockProxyEntity(BlockPos principalPosition) : BlockEntity
    {
        /// <summary>Writes the principal coordinates consumed by the production proxy resolver.</summary>
        /// <param name="tree">Destination public block-entity state.</param>
        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            tree.SetInt("cx", principalPosition.X);
            tree.SetInt("cy", principalPosition.Y);
            tree.SetInt("cz", principalPosition.Z);
        }
    }

    /// <summary>
    /// Supports throwing Tessellation Block Entity within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class ThrowingTessellationBlockEntity : BlockEntity
    {
        /// <summary>
        /// Handles tesselation for the test double and records only the state required by later assertions.
        /// </summary>
        /// <param name="mesher">The mesher input used to configure this deterministic test path.</param>
        /// <param name="tessellator">The tessellator input used to configure this deterministic test path.</param>
        /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessellator) =>
            throw new InvalidOperationException("fixture tessellation");
    }

    /// <summary>
    /// Supports fixture Container Block Entity within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class FixtureContainerBlockEntity(
        IInventory inventory,
        string? stateKey = null,
        bool stateValue = false) : BlockEntity, IBlockEntityContainer
    {
        /// <summary>
        /// Gets the inventory value exposed to the deterministic fixture.
        /// </summary>
        public IInventory Inventory => inventory;
        /// <summary>
        /// Gets the inventory Class Name value exposed to the deterministic fixture.
        /// </summary>
        public string InventoryClassName => "fixture";
        /// <summary>
        /// Executes the check Inventory Cleared Mid Tick step used by the deterministic fixture Container Block Entity fixture.
        /// </summary>
        public void CheckInventoryClearedMidTick()
        {
        }

        /// <summary>
        /// Executes the drop Contents step used by the deterministic fixture Container Block Entity fixture.
        /// </summary>
        /// <param name="atPos">The at Pos input used to configure this deterministic test path.</param>
        public void DropContents(Vec3d atPos)
        {
            _ = atPos;
        }

        /// <summary>
        /// Executes the to Tree Attributes step used by the deterministic fixture Container Block Entity fixture.
        /// </summary>
        /// <param name="tree">The tree input used to configure this deterministic test path.</param>
        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            if (stateKey is not null)
            {
                tree.SetBool(stateKey, stateValue);
            }
        }
    }

    /// <summary>
    /// Provides the scene Fixture state required by tests without depending on a live game world.
    /// </summary>
    private sealed class SceneFixture : IDisposable
    {
        private readonly Dictionary<(int X, int Y, int Z), Block> solids = [];
        private readonly Dictionary<(int X, int Y, int Z), Block> fluids = [];
        private readonly Dictionary<(int X, int Y, int Z, int Dimension), BlockEntity> blockEntities = [];
        private readonly Dictionary<int, MeshData?> meshes;
        private readonly int throwMeshForBlockId;
        private BlockEntity? blockEntity;

        /// <summary>
        /// Initializes a new scene Fixture fixture with the dependencies required for isolated execution.
        /// </summary>
        /// <param name="meshes">The meshes input used to configure this deterministic test path.</param>
        /// <param name="throwMeshForBlockId">The throw Mesh For Block Id input used to configure this deterministic test path.</param>
        /// <param name="blockEntity">Coordinate component in the space defined by the tested API.</param>
        /// <param name="withPlayer">The with Player input used to configure this deterministic test path.</param>
        /// <param name="atlasPositions">The atlas Positions input used to configure this deterministic test path.</param>
        /// <param name="assets">The assets input used to configure this deterministic test path.</param>
        /// <param name="daylight">The daylight input used to configure this deterministic test path.</param>
        /// <param name="sunDirection">The sun Direction input used to configure this deterministic test path.</param>
        /// <param name="desiredViewDistance">Client-requested visible block distance.</param>
        /// <param name="approvedViewDistance">Server-approved visible block distance.</param>
        /// <param name="configuredSunDistance">Optional VintageRTX minimum trace distance.</param>
        internal SceneFixture(
            Dictionary<int, MeshData?>? meshes = null,
            int throwMeshForBlockId = -1,
            BlockEntity? blockEntity = null,
            bool withPlayer = false,
            TextureAtlasPosition[]? atlasPositions = null,
            Dictionary<AssetLocation, IAsset>? assets = null,
            float? daylight = null,
            Vec3f? sunDirection = null,
            int desiredViewDistance = 0,
            int approvedViewDistance = 0,
            float? configuredSunDistance = null)
        {
            this.meshes = meshes ?? [];
            this.throwMeshForBlockId = throwMeshForBlockId;
            this.blockEntity = blockEntity;
            ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
                RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            IBlockAccessor accessor = RuntimeCoverageDispatchProxy.Create<IBlockAccessor>(AccessBlock);
            EntityPlayer? playerEntity = withPlayer
                ? new EntityPlayer { CameraPos = new Vec3d(0, 0, 0) }
                : null;
            IWorldPlayerData? worldData = withPlayer
                ? RuntimeCoverageDispatchProxy.Create<IWorldPlayerData>((method, _) => method.Name switch
                {
                    "get_DesiredViewDistance" => desiredViewDistance,
                    "get_LastApprovedViewDistance" => approvedViewDistance,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                })
                : null;
            IClientPlayer? player = withPlayer
                ? RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) => method.Name switch
                {
                    "get_Entity" => playerEntity,
                    "get_WorldData" => worldData,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                })
                : null;
            IClientGameCalendar? calendar = daylight.HasValue || sunDirection is not null
                ? RuntimeCoverageDispatchProxy.Create<IClientGameCalendar>((method, _) => method.Name switch
                {
                    "GetDayLightStrength" => daylight ?? 0.0f,
                    "get_SunPositionNormalized" => sunDirection,
                    "GetSunPosition" => sunDirection,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                })
                : null;
            IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>(
                (method, _) => method.Name switch
                {
                    "get_Collectibles" => new List<CollectibleObject>(),
                    "get_BlockAccessor" => accessor,
                    "get_Player" => player,
                    "get_Calendar" => calendar,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
            IClientEventAPI eventApi = RuntimeCoverageDispatchProxy.Create<IClientEventAPI>(
                (method, _) => method.Name == "RegisterGameTickListener"
                    ? 77L
                    : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            ITesselatorManager tesselatorManager = RuntimeCoverageDispatchProxy.Create<ITesselatorManager>(
                (method, args) =>
                {
                    if (method.Name == "GetDefaultBlockMesh")
                    {
                        Block block = (Block)args![0]!;
                        if (block.Id == this.throwMeshForBlockId)
                        {
                            throw new InvalidOperationException("missing mesh");
                        }
                        return this.meshes.TryGetValue(block.Id, out MeshData? mesh) ? mesh : null;
                    }
                    return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
                });
            ITesselatorAPI tesselator = RuntimeCoverageDispatchProxy.Create<ITesselatorAPI>(
                (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            IBlockTextureAtlasAPI atlas = RuntimeCoverageDispatchProxy.Create<IBlockTextureAtlasAPI>(
                (method, _) => method.Name switch
                {
                    "get_Positions" => atlasPositions ?? [],
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
            Dictionary<AssetLocation, IAsset> assetCatalog = assets ?? [];
            IAssetManager assetManager = RuntimeCoverageDispatchProxy.Create<IAssetManager>(
                (method, args) => method.Name switch
                {
                    "get_AllAssets" => assetCatalog,
                    "get_Origins" => new List<IAssetOrigin>(),
                    "TryGet" => assetCatalog.TryGetValue((AssetLocation)args![0]!, out IAsset? asset)
                        ? asset
                        : null,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
            ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>(
                (method, _) => method.Name switch
                {
                    "get_World" => world,
                    "get_Event" => eventApi,
                    "get_Logger" => logger,
                    "get_TesselatorManager" => tesselatorManager,
                    "get_Tesselator" => tesselator,
                    "get_BlockTextureAtlas" => atlas,
                    "get_Assets" => assetManager,
                    _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                });
            Scene = new VoxelScene(api, configuredSunDistance.HasValue
                ? () => configuredSunDistance.Value
                : null);

            object? AccessBlock(MethodInfo method, object?[]? args)
            {
                if (method.Name == "GetRainMapHeightAt")
                {
                    return RainHeight;
                }
                if (method.Name == "GetBlockEntity")
                {
                    if (args is { Length: > 0 } && args[0] is BlockPos entityPosition
                        && blockEntities.TryGetValue(
                            (entityPosition.X, entityPosition.Y, entityPosition.Z, entityPosition.dimension),
                            out BlockEntity? positionedEntity))
                    {
                        return positionedEntity;
                    }
                    return this.blockEntity;
                }
                if (method.Name == "GetBlock" && args is { Length: > 0 } && args[0] is BlockPos pos)
                {
                    bool fluidLayer = args.Length > 1
                        && Convert.ToInt32(args[1], System.Globalization.CultureInfo.InvariantCulture)
                            == BlockLayersAccess.Fluid;
                    Dictionary<(int, int, int), Block> source = fluidLayer ? fluids : solids;
                    return source.TryGetValue((pos.X, pos.Y, pos.Z), out Block? block) ? block : null;
                }
                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            }
        }

        /// <summary>
        /// Gets the scene value exposed to the deterministic fixture.
        /// </summary>
        internal VoxelScene Scene { get; }
        /// <summary>
        /// Gets or sets the rain Height value exposed to the deterministic fixture.
        /// </summary>
        internal int RainHeight { get; set; }

        /// <summary>
        /// Sets solid on the test double without invoking unrelated production side effects.
        /// </summary>
        /// <param name="x">Coordinate component in the space defined by the tested API.</param>
        /// <param name="y">Coordinate component in the space defined by the tested API.</param>
        /// <param name="z">Coordinate component in the space defined by the tested API.</param>
        /// <param name="block">Block to store, or null to remove the fixture cell.</param>
        internal void SetSolid(int x, int y, int z, Block? block)
        {
            if (block is null)
            {
                solids.Remove((x, y, z));
            }
            else
            {
                solids[(x, y, z)] = block;
            }
        }
        /// <summary>
        /// Sets fluid on the test double without invoking unrelated production side effects.
        /// </summary>
        /// <param name="x">Coordinate component in the space defined by the tested API.</param>
        /// <param name="y">Coordinate component in the space defined by the tested API.</param>
        /// <param name="z">Coordinate component in the space defined by the tested API.</param>
        /// <param name="block">The block input used to configure this deterministic test path.</param>
        internal void SetFluid(int x, int y, int z, Block block) => fluids[(x, y, z)] = block;
        /// <summary>
        /// Sets block Entity on the test double without invoking unrelated production side effects.
        /// </summary>
        /// <param name="value">The value input used to configure this deterministic test path.</param>
        internal void SetBlockEntity(BlockEntity? value) => blockEntity = value;
        /// <summary>Associates a block entity with one exact world position.</summary>
        /// <param name="position">Exact block-entity position.</param>
        /// <param name="value">Entity returned for that position.</param>
        internal void SetBlockEntityAt(BlockPos position, BlockEntity value) =>
            blockEntities[(position.X, position.Y, position.Z, position.dimension)] = value;

        /// <summary>
        /// Sets origins on the test double without invoking unrelated production side effects.
        /// </summary>
        /// <param name="x">Coordinate component in the space defined by the tested API.</param>
        /// <param name="y">Coordinate component in the space defined by the tested API.</param>
        /// <param name="z">Coordinate component in the space defined by the tested API.</param>
        /// <param name="sunX">Coordinate component in the space defined by the tested API.</param>
        /// <param name="sunY">Coordinate component in the space defined by the tested API.</param>
        /// <param name="sunZ">Coordinate component in the space defined by the tested API.</param>
        internal void SetOrigins(int x, int y, int z, int sunX, int sunY, int sunZ)
        {
            SetField("originX", x);
            SetField("originY", y);
            SetField("originZ", z);
            SetField(
                "fluidSurfaceOriginX",
                x - (VoxelScene.FluidSurfaceWidth - VoxelScene.Width) / 2);
            SetField(
                "fluidSurfaceOriginZ",
                z - (VoxelScene.FluidSurfaceDepth - VoxelScene.Depth) / 2);
            SetField("sunOriginX", sunX);
            SetField("sunOriginY", sunY);
            SetField("sunOriginZ", sunZ);
        }

        /// <summary>
        /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
        /// </summary>
        /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
        /// <param name="args">The args input used to configure this deterministic test path.</param>
        /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
        /// <returns>The invoke result consumed by the caller&apos;s assertion.</returns>
        internal T Invoke<T>(string name, params object?[] args) => (T)InvokeRaw(name, args)!;

        /// <summary>
        /// Invokes raw through the fixture reflection boundary and propagates failures to the calling assertion.
        /// </summary>
        /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
        /// <param name="args">The args input used to configure this deterministic test path.</param>
        /// <returns>The invoke Raw result consumed by the caller&apos;s assertion.</returns>
        internal object? InvokeRaw(string name, object?[] args)
        {
            MethodInfo method = typeof(VoxelScene).GetMethod(
                name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingMethodException(typeof(VoxelScene).FullName, name);
            return method.Invoke(Scene, args);
        }

        /// <summary>
        /// Executes the field step used by the deterministic scene Fixture fixture.
        /// </summary>
        /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
        /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
        /// <returns>The field result consumed by the caller&apos;s assertion.</returns>
        internal T Field<T>(string name) => (T)(typeof(VoxelScene).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Scene)
            ?? throw new MissingFieldException(typeof(VoxelScene).FullName, name));

        /// <summary>Reads a private or public scene property for lifecycle coverage assertions.</summary>
        /// <param name="name">Property name.</param>
        /// <typeparam name="T">Expected property value type.</typeparam>
        /// <returns>Current property value.</returns>
        internal T Property<T>(string name) => (T)(typeof(VoxelScene).GetProperty(
            name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(Scene)
            ?? throw new MissingMemberException(typeof(VoxelScene).FullName, name));

        /// <summary>
        /// Sets field on the test double without invoking unrelated production side effects.
        /// </summary>
        /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
        /// <param name="value">The value input used to configure this deterministic test path.</param>
        internal void SetField(string name, object? value)
        {
            FieldInfo field = typeof(VoxelScene).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(VoxelScene).FullName, name);
            field.SetValue(Scene, value);
        }

        /// <summary>
        /// Releases resources owned by scene Fixture; cleanup remains safe after partial fixture initialization.
        /// </summary>
        public void Dispose() => Scene.Dispose();
    }
}
