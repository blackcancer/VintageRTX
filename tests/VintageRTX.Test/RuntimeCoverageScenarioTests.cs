using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Exercises deterministic scenario parsing and the world-search algorithms on
/// a synthetic block accessor. These tests do not require a running game or a
/// graphics context and therefore remain discoverable in Visual Studio.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuntimeCoverageScenarioTests
{
    private static readonly string[] ScenarioEnvironmentVariables =
    [
        "VINTAGERTX_TEST_SCENARIO",
        "VINTAGERTX_TEST_TIME_HOUR",
        "VINTAGERTX_TEST_CLEAR_WEATHER",
        "VINTAGERTX_TEST_PRECIPITATION",
        "VINTAGERTX_RENDER_LAB_READY"
    ];

    /// <summary>
    /// Verifies the scenario Settings Disable Empty Or Malformed Input regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ScenarioSettingsDisableEmptyOrMalformedInput()
    {
        RuntimeScenarioSettings empty = RuntimeScenarioProbe.ReadSettings(static _ => null);
        Assert.IsFalse(empty.Enabled);
        Assert.AreEqual(string.Empty, empty.Scenario);
        Assert.IsNull(empty.RequestedWorldHour);
        Assert.IsNull(empty.RequestedPrecipitation);
        Assert.IsFalse(empty.ClearWeather);

        Dictionary<string, string?> malformedValues = new()
        {
            ["VINTAGERTX_TEST_SCENARIO"] = "   ",
            ["VINTAGERTX_TEST_TIME_HOUR"] = "NaN",
            ["VINTAGERTX_TEST_CLEAR_WEATHER"] = "true",
            ["VINTAGERTX_TEST_PRECIPITATION"] = "Infinity"
        };
        RuntimeScenarioSettings malformed = RuntimeScenarioProbe.ReadSettings(
            key => malformedValues.GetValueOrDefault(key));

        Assert.IsFalse(malformed.Enabled);
        Assert.IsNull(malformed.RequestedWorldHour);
        Assert.IsNull(malformed.RequestedPrecipitation);
        Assert.IsFalse(malformed.ClearWeather);
    }

    /// <summary>
    /// Verifies the scenario Settings Normalize Scenario And Clamp Precipitation regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ScenarioSettingsNormalizeScenarioAndClampPrecipitation()
    {
        Dictionary<string, string?> values = new()
        {
            ["VINTAGERTX_TEST_SCENARIO"] = "  Render-Lab  ",
            ["VINTAGERTX_TEST_TIME_HOUR"] = "18.25",
            ["VINTAGERTX_TEST_CLEAR_WEATHER"] = "1",
            ["VINTAGERTX_TEST_PRECIPITATION"] = "12.5"
        };

        RuntimeScenarioSettings settings = RuntimeScenarioProbe.ReadSettings(
            key => values.GetValueOrDefault(key));

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual("render-lab", settings.Scenario);
        Assert.AreEqual(18.25f, settings.RequestedWorldHour);
        Assert.IsTrue(settings.ClearWeather);
        Assert.AreEqual(1.0f, settings.RequestedPrecipitation);

        values["VINTAGERTX_TEST_PRECIPITATION"] = "-4";
        Assert.AreEqual(
            0.0f,
            RuntimeScenarioProbe.ReadSettings(key => values.GetValueOrDefault(key)).RequestedPrecipitation);
    }

    /// <summary>
    /// Verifies the scenario Settings Enable Environment Only Probe regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ScenarioSettingsEnableEnvironmentOnlyProbe()
    {
        RuntimeScenarioSettings settings = RuntimeScenarioProbe.ReadSettings(
            key => key == "VINTAGERTX_TEST_TIME_HOUR" ? "7.5" : null);

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(string.Empty, settings.Scenario);
        Assert.AreEqual(7.5f, settings.RequestedWorldHour);
    }

    /// <summary>
    /// Verifies the try Start Does Not Register Callbacks When No Behavior Is Requested regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TryStartDoesNotRegisterCallbacksWhenNoBehaviorIsRequested()
    {
        ICoreClientAPI api = CreateClientApi([], out _);

        RuntimeScenarioProbe? probe = RuntimeScenarioProbe.TryStart(
            api,
            static () => true,
            static () => true,
            static _ => null);

        Assert.IsNull(probe);
    }

    /// <summary>
    /// Verifies the try Start Registers And Disposes Runtime Callbacks regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void TryStartRegistersAndDisposesRuntimeCallbacks()
    {
        List<string> calls = [];
        ICoreClientAPI api = CreateClientApi(calls, out _);
        string? previousReady = Environment.GetEnvironmentVariable("VINTAGERTX_RENDER_LAB_READY");
        try
        {
            RuntimeScenarioProbe? probe = RuntimeScenarioProbe.TryStart(
                api,
                static () => true,
                static () => true,
                key => key == "VINTAGERTX_TEST_SCENARIO" ? "render-lab" : null);

            Assert.IsNotNull(probe);
            Assert.AreEqual(-1.0, probe.RenderOrder);
            Assert.AreEqual(0, probe.RenderRange);
            Assert.AreEqual("0", Environment.GetEnvironmentVariable("VINTAGERTX_RENDER_LAB_READY"));
            CollectionAssert.Contains(calls, "RegisterGameTickListener");
            CollectionAssert.Contains(calls, "RegisterRenderer");

            probe.Dispose();

            CollectionAssert.Contains(calls, "UnregisterRenderer");
            CollectionAssert.Contains(calls, "UnregisterGameTickListener");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_RENDER_LAB_READY", previousReady);
        }
    }

    /// <summary>
    /// Verifies the public Try Start Reads The Process Environment regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void PublicTryStartReadsTheProcessEnvironment()
    {
        Dictionary<string, string?> previous = ScenarioEnvironmentVariables.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable);
        List<string> calls = [];
        try
        {
            foreach (string key in ScenarioEnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(key, null);
            }

            ICoreClientAPI api = CreateClientApi(calls, out _);
            Assert.IsNull(RuntimeScenarioProbe.TryStart(api, static () => true, static () => true));

            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_SCENARIO", "held-light");
            RuntimeScenarioProbe? probe = RuntimeScenarioProbe.TryStart(
                api,
                static () => true,
                static () => true);
            Assert.IsNotNull(probe);
            probe.Dispose();
        }
        finally
        {
            foreach ((string key, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>
    /// Verifies the environment Verification Covers Time Weather Precipitation And Night Rules regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void EnvironmentVerificationCoversTimeWeatherPrecipitationAndNightRules()
    {
        Assert.IsTrue(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, false, float.NaN, float.NaN, float.NaN, float.NaN));
        Assert.IsTrue(RuntimeScenarioProbe.IsEnvironmentVerified(
            23.95f, false, 0.02f, 0.0f, 0.5f, 0.5f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            6.0f, false, 7.0f, 0.0f, 0.0f, 0.0f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            6.0f, false, 6.0f, 0.01f, 0.0f, 0.0f));

        Assert.IsTrue(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, true, 0.0f, 1.0f, 0.0f, 0.0f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, true, 0.0f, 1.0f, 0.02f, 0.0f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, true, 0.0f, 1.0f, float.NaN, 0.0f));

        Assert.IsTrue(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, false, 0.0f, 1.0f, 0.48f, 1.0f, requestedPrecipitation: 0.5f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, false, 0.0f, 1.0f, 0.2f, 1.0f, requestedPrecipitation: 0.5f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, false, 0.0f, 1.0f, float.NaN, 1.0f, requestedPrecipitation: 0.5f));

        Assert.IsTrue(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, false, 0.0f, 1.0f, 0.0f, 0.0f, requireNight: true, daylightStrength: 0.12f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, false, 0.0f, 1.0f, 0.0f, 0.0f, requireNight: true, daylightStrength: 0.13f));
        Assert.IsFalse(RuntimeScenarioProbe.IsEnvironmentVerified(
            null, false, 0.0f, 1.0f, 0.0f, 0.0f, requireNight: true, daylightStrength: float.NaN));
    }

    /// <summary>
    /// Verifies the angle Helpers Wrap Both Directions And Shortest Hour Arc regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AngleHelpersWrapBothDirectionsAndShortestHourArc()
    {
        Assert.AreEqual(0.25f, RuntimeScenarioProbe.NormalizeSignedAngle(0.25f), 0.0001f);
        Assert.AreEqual(
            -GameMath.PI + 0.25f,
            RuntimeScenarioProbe.NormalizeSignedAngle(GameMath.PI + 0.25f),
            0.0001f);
        Assert.AreEqual(
            GameMath.PI - 0.25f,
            RuntimeScenarioProbe.NormalizeSignedAngle(-GameMath.PI - 0.25f),
            0.0001f);
        Assert.AreEqual(2.0f, RuntimeScenarioProbe.CircularHourDistance(4.0f, 6.0f));
        Assert.AreEqual(1.0f, RuntimeScenarioProbe.CircularHourDistance(23.5f, 0.5f));
    }

    /// <summary>
    /// Verifies the block Predicates Distinguish Air Non Colliding And Solid Geometry regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void BlockPredicatesDistinguishAirNonCollidingAndSolidGeometry()
    {
        Block air = CreateAir();
        Block decorativePlane = new()
        {
            BlockId = 3,
            BlockMaterial = EnumBlockMaterial.Plant,
            CollisionBoxes = null!
        };
        Block solid = CreateSolid();

        Assert.IsFalse(RuntimeScenarioProbe.IsCaveSolid(null!));
        Assert.IsFalse(RuntimeScenarioProbe.IsCaveSolid(air));
        Assert.IsFalse(RuntimeScenarioProbe.IsCaveSolid(decorativePlane));
        Assert.IsTrue(RuntimeScenarioProbe.IsCaveSolid(solid));

        Assert.IsTrue(RuntimeScenarioProbe.IsOpenForCamera(null!));
        Assert.IsTrue(RuntimeScenarioProbe.IsOpenForCamera(air));
        Assert.IsTrue(RuntimeScenarioProbe.IsOpenForCamera(decorativePlane));
        Assert.IsFalse(RuntimeScenarioProbe.IsOpenForCamera(solid));

        Assert.IsTrue(RuntimeScenarioProbe.IsVisuallyOpen(null!));
        Assert.IsTrue(RuntimeScenarioProbe.IsVisuallyOpen(air));
        Assert.IsFalse(RuntimeScenarioProbe.IsVisuallyOpen(decorativePlane));
        Assert.IsFalse(RuntimeScenarioProbe.IsVisuallyOpen(solid));
    }

    /// <summary>
    /// Verifies the cave Search Finds Roof Counts Volume And Targets Wall regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CaveSearchFindsRoofCountsVolumeAndTargetsWall()
    {
        Block air = CreateAir();
        Block solid = CreateSolid();
        IBlockAccessor roofAccessor = CreateBlockAccessor(
            (x, y, z, _) => y == 4 ? solid : air,
            static (_, _) => 0);
        BlockPos sample = new(0);

        Assert.AreEqual(4, RuntimeScenarioProbe.FindCaveRoofDistance(roofAccessor, sample, 0, 0, 0));
        Assert.AreEqual(-1, RuntimeScenarioProbe.FindCaveRoofDistance(
            CreateBlockAccessor((_, _, _, _) => air, static (_, _) => 0),
            sample,
            0,
            0,
            0));

        IBlockAccessor volumeAccessor = CreateBlockAccessor(
            (x, y, z, _) => x == 0 && y == 1 && z == 0 ? solid : air,
            static (_, _) => 0);
        Assert.AreEqual(74, RuntimeScenarioProbe.CountCaveOpenCells(volumeAccessor, sample, 0, 0, 0));

        IBlockAccessor wallAccessor = CreateBlockAccessor(
            (x, _, _, _) => x >= 8 ? solid : air,
            static (_, _) => 0);
        Assert.IsTrue(RuntimeScenarioProbe.TryFindCaveWallTarget(
            wallAccessor,
            sample,
            0,
            0,
            0,
            out double targetX,
            out _,
            out _,
            out int wallDistance));
        Assert.AreEqual(8.5, targetX, 0.01);
        Assert.AreEqual(8, wallDistance);

        Assert.IsFalse(RuntimeScenarioProbe.TryFindCaveWallTarget(
            CreateBlockAccessor((_, _, _, _) => air, static (_, _) => 0),
            sample,
            0,
            0,
            0,
            out _,
            out _,
            out _,
            out _));
    }

    /// <summary>
    /// Verifies the water Surface And Patch Search Reject Covered Or Sparse Liquids regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WaterSurfaceAndPatchSearchRejectCoveredOrSparseLiquids()
    {
        Block air = CreateAir();
        Block solid = CreateSolid();
        Block water = CreateWater();
        BlockPos sample = new(0);
        IBlockAccessor fullWater = CreateBlockAccessor(
            (_, y, _, layer) => layer == BlockLayersAccess.Fluid && y == 10 ? water : air,
            static (_, _) => 10);

        Assert.IsTrue(RuntimeScenarioProbe.TryGetWaterSurface(
            fullWater, sample, 0, 0, out int waterY, out Block found));
        Assert.AreEqual(10, waterY);
        Assert.AreSame(water, found);
        Assert.IsTrue(RuntimeScenarioProbe.HasWaterPatch(fullWater, sample, 0, 10, 0));

        IBlockAccessor covered = CreateBlockAccessor(
            (_, y, _, layer) => layer == BlockLayersAccess.Fluid && y == 10
                ? water
                : layer != BlockLayersAccess.Fluid && y == 10
                    ? solid
                    : air,
            static (_, _) => 10);
        Assert.IsFalse(RuntimeScenarioProbe.TryGetWaterSurface(
            covered, sample, 0, 0, out int coveredY, out Block coveredWater));
        Assert.AreEqual(0, coveredY);
        Assert.IsNull(coveredWater);

        IBlockAccessor sparse = CreateBlockAccessor(
            (x, y, z, layer) => layer == BlockLayersAccess.Fluid
                && y == 10
                && !(x == 1 || (x == 0 && z == 1) || (x == -1 && z == 1))
                    ? water
                    : air,
            static (_, _) => 10);
        Assert.IsFalse(RuntimeScenarioProbe.HasWaterPatch(sparse, sample, 0, 10, 0));
    }

    /// <summary>
    /// Verifies the water View Search Finds Bank And Requires Continuous Visible Run regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WaterViewSearchFindsBankAndRequiresContinuousVisibleRun()
    {
        Block air = CreateAir();
        Block solid = CreateSolid();
        Block water = CreateWater();
        BlockPos sample = new(0);
        IBlockAccessor shoreline = CreateBlockAccessor(
            (x, y, _, layer) => layer == BlockLayersAccess.Fluid
                ? x >= 0 && y == 10 ? water : air
                : x < 0 && y == 10 ? solid : air,
            static (_, _) => 10);

        Assert.IsTrue(RuntimeScenarioProbe.TryFindWaterView(
            shoreline,
            sample,
            0,
            10,
            0,
            out int bankX,
            out int bankY,
            out _,
            out double targetX,
            out _,
            out _,
            out double distance));
        Assert.IsTrue(bankX < 0);
        Assert.AreEqual(10, bankY);
        Assert.IsTrue(targetX > 0.0);
        Assert.IsTrue(distance >= 8.0);

        Assert.IsTrue(RuntimeScenarioProbe.HasContinuousWaterRun(
            shoreline, sample, -2, 0, 1.0, 0.0, 2.0, 12, 10));

        IBlockAccessor waterWithHole = CreateBlockAccessor(
            (x, y, _, layer) => layer == BlockLayersAccess.Fluid
                && x >= 0
                && x != 5
                && y == 10
                    ? water
                    : air,
            static (_, _) => 10);
        Assert.IsFalse(RuntimeScenarioProbe.HasContinuousWaterRun(
            waterWithHole, sample, -2, 0, 1.0, 0.0, 2.0, 12, 10));

        Assert.IsFalse(RuntimeScenarioProbe.TryFindWaterView(
            CreateBlockAccessor((_, _, _, _) => air, static (_, _) => 10),
            sample,
            0,
            10,
            0,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _));
    }

    /// <summary>
    /// Verifies the ground And Roof Visibility Detect Terrain Discontinuities regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GroundAndRoofVisibilityDetectTerrainDiscontinuities()
    {
        Block air = CreateAir();
        Block solid = CreateSolid();
        BlockPos sample = new(0);
        IBlockAccessor open = CreateBlockAccessor(
            (_, _, _, _) => air,
            static (_, _) => 10);
        Assert.IsTrue(RuntimeScenarioProbe.HasOpenGroundPatch(open, sample, 0, 10, 0));
        Assert.IsTrue(RuntimeScenarioProbe.HasRoofLineOfSight(open, sample, 0, 10, 0, 10, 18, 0));

        IBlockAccessor uneven = CreateBlockAccessor(
            (_, _, _, _) => air,
            static (x, z) => x == 1 && z == 1 ? 13 : 10);
        Assert.IsFalse(RuntimeScenarioProbe.HasOpenGroundPatch(uneven, sample, 0, 10, 0));

        IBlockAccessor blockedGround = CreateBlockAccessor(
            (x, y, z, _) => x == 0 && y == 11 && z == 0 ? solid : air,
            static (_, _) => 10);
        Assert.IsFalse(RuntimeScenarioProbe.HasOpenGroundPatch(blockedGround, sample, 0, 10, 0));

        IBlockAccessor blockedSight = CreateBlockAccessor(
            (_, _, _, _) => air,
            static (x, _) => x == 1 ? 30 : 10);
        Assert.IsFalse(RuntimeScenarioProbe.HasRoofLineOfSight(
            blockedSight, sample, 0, 10, 0, 10, 18, 0));
    }

    /// <summary>
    /// Creates client Api with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="calls">The calls input used to configure this deterministic test path.</param>
    /// <param name="eventApi">Vintage Story API facade or test double supplied to the scenario.</param>
    /// <returns>The create Client Api result consumed by the caller&apos;s assertion.</returns>
    private static ICoreClientAPI CreateClientApi(
        List<string> calls,
        out IClientEventAPI eventApi)
    {
        eventApi = RuntimeCoverageDispatchProxy.Create<IClientEventAPI>((method, _) =>
        {
            calls.Add(method.Name);
            return method.Name == "RegisterGameTickListener"
                ? 73L
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        IClientEventAPI capturedEventApi = eventApi;
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_Event"
                ? capturedEventApi
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
    }

    /// <summary>
    /// Creates block Accessor with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="blockAt">The block At input used to configure this deterministic test path.</param>
    /// <param name="rainHeightAt">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <returns>The create Block Accessor result consumed by the caller&apos;s assertion.</returns>
    private static IBlockAccessor CreateBlockAccessor(
        System.Func<int, int, int, int, Block> blockAt,
        System.Func<int, int, int> rainHeightAt)
    {
        return RuntimeCoverageDispatchProxy.Create<IBlockAccessor>((method, arguments) =>
        {
            if (method.Name == "GetRainMapHeightAt")
            {
                if (arguments![0] is BlockPos rainPosition)
                {
                    return rainHeightAt(rainPosition.X, rainPosition.Z);
                }

                return rainHeightAt((int)arguments[0]!, (int)arguments[1]!);
            }

            if (method.Name == "GetBlock" && arguments![0] is BlockPos position)
            {
                int layer = arguments.Length >= 2
                    ? (int)arguments[1]!
                    : BlockLayersAccess.MostSolid;
                return blockAt(position.X, position.Y, position.Z, layer);
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
    }

    /// <summary>
    /// Creates air with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Air result consumed by the caller&apos;s assertion.</returns>
    private static Block CreateAir()
    {
        return new Block
        {
            BlockId = 0,
            BlockMaterial = EnumBlockMaterial.Air,
            CollisionBoxes = null!
        };
    }

    /// <summary>
    /// Creates solid with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Solid result consumed by the caller&apos;s assertion.</returns>
    private static Block CreateSolid()
    {
        return new Block
        {
            BlockId = 1,
            BlockMaterial = EnumBlockMaterial.Stone,
            CollisionBoxes = [new Cuboidf(0, 0, 0, 1, 1, 1)]
        };
    }

    /// <summary>
    /// Creates water with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Water result consumed by the caller&apos;s assertion.</returns>
    private static Block CreateWater()
    {
        return new Block
        {
            BlockId = 2,
            BlockMaterial = EnumBlockMaterial.Water,
            MatterState = EnumMatterState.Liquid,
            LiquidCode = "water",
            CollisionBoxes = null!
        };
    }
}
