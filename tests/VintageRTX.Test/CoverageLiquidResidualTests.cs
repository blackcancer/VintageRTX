using System.Text;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Covers the remaining fail-closed branches of the offline liquid asset bridge.</summary>
[TestClass]
public sealed class CoverageLiquidResidualTests
{
    /// <summary>Covers solver sanitizers, saturation, zero-support deposition, and fixed-step catch-up.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SurfaceSimulationContainsNonFiniteForcingAndCorruptedTransientState()
    {
        LiquidSurfaceDynamics dynamics = Dynamics();
        LiquidSurfacePhysicalProperties physics = Physics();
        _ = new LiquidSurfaceSimulation(0, 0, 1, 1, deterministicSeed: 0);

        LiquidSurfaceSimulation depthGuard = new(0, 0, 1, 1, 0.5f);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => depthGuard.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: false,
            depthMetres: float.NegativeInfinity));

        LiquidSurfaceDynamics invalidAmplitude = dynamics with { WaveAmplitude = float.NaN };
        LiquidSurfaceDynamics invalidWavelength = dynamics with { WaveLength = float.NaN };
        depthGuard.SetSurfaceCell(0, 0, 0.875f, 1, in invalidAmplitude, in physics, false);
        depthGuard.SetSurfaceCell(0, 0, 0.875f, 1, in invalidWavelength, in physics, false);
        _ = LiquidSurfaceSimulation.ComputePhaseSpeedMetresPerSecond(
            in invalidWavelength,
            in physics,
            0.5f);

        LiquidSurfaceSimulation saturated = ConfiguredSimulation(dynamics, physics);
        PrivateArray<float>(saturated, "heights")[0] = 1.0e9f;
        PrivateArray<float>(saturated, "velocities")[0] = 1.0e9f;
        Assert.AreEqual(1, saturated.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default));

        LiquidSurfaceSimulation nonFinite = ConfiguredSimulation(dynamics, physics);
        PrivateArray<float>(nonFinite, "heights")[0] = float.NaN;
        PrivateArray<float>(nonFinite, "velocities")[0] = float.NaN;
        Assert.AreEqual(1, nonFinite.Advance(
            LiquidSurfaceSimulation.FixedStepSeconds,
            new LiquidSurfaceForcing(float.NaN, float.NaN, float.NaN)));
        Assert.IsTrue(float.IsFinite(nonFinite.GetCellSample(0, 0).Height));

        LiquidSurfaceSimulation catchUp = ConfiguredSimulation(dynamics, physics);
        SetPrivateField(catchUp, "accumulator", 1.0f);
        Assert.AreEqual(12, catchUp.Advance(float.NaN, default));

        LiquidSurfaceSimulation zeroArea = ConfiguredSimulation(dynamics, physics);
        Assert.IsTrue(zeroArea.QueueImpulse(
            0.49,
            0.25,
            1.0f,
            1.0f,
            0.0f,
            LiquidSurfaceImpulseKind.SwimmingWake));
        zeroArea.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        Assert.AreEqual(0.0f, zeroArea.LastAppliedImpactPeakDisplacement);
    }

    /// <summary>Covers exact-contact rejection boundaries and non-exposed stochastic rain probes.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SurfaceSimulationRejectsEachInvalidProjectileContactAndSkipsDryCells()
    {
        LiquidSurfaceDynamics dynamics = Dynamics();
        LiquidSurfacePhysicalProperties physics = Physics();
        LiquidSurfaceSimulation simulation = ConfiguredSimulation(dynamics, physics, eventBudget: 32);
        LiquidProjectileCollisionSample sample = ProjectileSample(12001);
        simulation.ObserveProjectileLiquidCollisions([
            sample with { EntityId = 12002, IncidentMotionX = float.MaxValue },
            sample with { EntityId = 12003, IncidentMotionY = float.NaN },
            sample with { EntityId = 12004, PreviousWorldY = double.NaN },
            sample with { EntityId = 12005, PreviousWorldY = 0.50, WorldY = 0.40 },
            sample with { EntityId = 12006, PreviousWorldY = 2.0, WorldY = 1.50 }
        ]);
        Assert.AreEqual(
            3,
            simulation.TotalProjectileImpactCount,
            "Invalid velocity vectors are rejected; unresolved contact interpolation falls back to the finite endpoint.");

        LiquidProjectileWaveEnergyPartition zeroEnergy =
            LiquidSurfaceSimulation.ResolveProjectileWaveEnergyPartition(
            LiquidEntitySurfaceClass.Projectile,
            1.0f,
            0.0f,
            in physics,
            0.5f);
        Assert.AreEqual(0.0f, zeroEnergy.SurfaceCoupledEnergyJoules);
        Assert.IsTrue(zeroEnergy.CavityRadiusMetres > 0.0f);

        LiquidSurfaceSimulation rain = new(
            0,
            0,
            2,
            1,
            0.5f,
            eventBudget: 32,
            deterministicSeed: 1);
        rain.SetSurfaceCell(0, 0, 0.875f, 1, in dynamics, in physics, rainExposed: true);
        rain.SetSurfaceCell(1, 0, 0.875f, 1, in dynamics, in physics, rainExposed: false);
        rain.Advance(
            LiquidSurfaceSimulation.FixedStepSeconds,
            new LiquidSurfaceForcing(
                float.NaN,
                float.NaN,
                LiquidSurfaceWorldInputs.MaximumRainfallRateMetresPerSecond));
        Assert.IsTrue(rain.TotalImpulseCount > 0);
    }

    /// <summary>Covers bobber entry semantics, non-finite crossings, and oldest-probe replacement.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SurfaceSimulationTracksBobberEntriesAndReplacesTheOldestCollidingSlot()
    {
        LiquidSurfaceDynamics dynamics = Dynamics();
        LiquidSurfacePhysicalProperties physics = Physics();
        LiquidSurfaceSimulation bobber = ConfiguredSimulation(dynamics, physics, eventBudget: 16);
        LiquidEntitySurfaceSample above = EntitySample(13001, LiquidEntitySurfaceClass.Bobber) with
        {
            WorldY = 1.10,
            FeetInLiquid = false,
        };
        bobber.ObserveEntities([above]);
        bobber.ObserveEntities([above with { WorldY = 0.80, FeetInLiquid = true }]);
        Assert.AreEqual(1, bobber.TotalImpulseCount);

        LiquidEntitySurfaceSample infinite = EntitySample(13002, LiquidEntitySurfaceClass.Generic) with
        {
            WorldY = double.PositiveInfinity,
            FeetInLiquid = false,
        };
        bobber.ObserveEntities([infinite]);
        bobber.ObserveEntities([infinite with { WorldY = 0.80, FeetInLiquid = true }]);

        LiquidSurfaceSimulation trackers = new(
            0,
            0,
            1,
            1,
            0.5f,
            trackedEntityBudget: 8);
        for (int ordinal = 0; ordinal < 8; ordinal++)
        {
            trackers.ObserveEntities([EntitySample(ordinal * 8, LiquidEntitySurfaceClass.Generic)]);
            trackers.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        }
        trackers.ObserveEntities([EntitySample(0, LiquidEntitySurfaceClass.Generic)]);
        trackers.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);
        trackers.ObserveEntities([EntitySample(64, LiquidEntitySurfaceClass.Generic)]);
    }

    /// <summary>Distinguishes missing, null, blank, malformed, and valid liquid binding values.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void OpticalCatalogRejectsEveryNullAndBlankLiquidBindingShape()
    {
        CatalogDocuments documents = ReadDocuments();
        FirstLiquidBinding(documents).Remove("target");
        AssertInvalid(documents, "complete optics/physics");

        documents = ReadDocuments();
        FirstLiquidBinding(documents).Remove("codeRoots");
        AssertInvalid(documents, "no collectible-code roots");

        documents = ReadDocuments();
        FirstLiquidBinding(documents)["codeRoots"] = "game:water";
        AssertInvalid(documents, "no collectible-code roots");

        documents = ReadDocuments();
        FirstLiquidBinding(documents)["codeRoots"] = new JArray(JValue.CreateNull());
        AssertInvalid(documents, "invalid code root");

        documents = ReadDocuments();
        FirstLiquidBinding(documents)["codeRoots"] = new JArray("   ");
        AssertInvalid(documents, "invalid code root");
    }

    /// <summary>Distinguishes missing, null, blank, malformed, and duplicate container roots.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void OpticalCatalogRejectsEveryNullAndBlankContainerBindingShape()
    {
        CatalogDocuments documents = ReadDocuments();
        FirstContainerBinding(documents).Remove("target");
        AssertInvalid(documents, "no server patch contract");

        documents = ReadDocuments();
        FirstContainerBinding(documents).Remove("codeRoots");
        AssertInvalid(documents, "no collectible-code roots");

        documents = ReadDocuments();
        FirstContainerBinding(documents)["codeRoots"] = "game:woodbucket";
        AssertInvalid(documents, "no collectible-code roots");

        documents = ReadDocuments();
        FirstContainerBinding(documents)["codeRoots"] = new JArray(JValue.CreateNull());
        AssertInvalid(documents, "invalid code root");

        documents = ReadDocuments();
        FirstContainerBinding(documents)["codeRoots"] = new JArray("   ");
        AssertInvalid(documents, "invalid code root");
    }

    /// <summary>Proves absent patch fields are ignored independently from empty string fields.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void OpticalCatalogIgnoresPatchOperationsWithAbsentFields()
    {
        CatalogDocuments documents = ReadDocuments();
        documents.Optics.Insert(0, new JObject());
        documents.Optics.Insert(1, new JObject
        {
            ["file"] = "game:noise.json",
            ["value"] = new JObject(),
        });
        documents.Optics.Insert(2, new JObject
        {
            ["file"] = "game:missing-value.json",
        });
        documents.Measured.Insert(0, new JObject
        {
            ["path"] = "/attributes/vintageRtxOptics",
            ["value"] = new JObject { ["ior"] = 2.0f },
        });

        LiquidClientFallbackData parsed = Parse(documents);

        Assert.AreEqual(36, parsed.ProfilesByCodeRoot.Count);
        Assert.AreEqual(2, parsed.ContainersByCodeRoot.Count);
    }

    /// <summary>Covers empty history destinations and the source-sequence fallback for dropped items.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SurfaceSimulationRetainsDroppedItemHistoryWhenCallerCapacityIsZero()
    {
        LiquidSurfaceDynamics dynamics = Dynamics();
        LiquidSurfacePhysicalProperties physics = Physics();
        LiquidSurfaceSimulation simulation = ConfiguredSimulation(dynamics, physics);

        Assert.IsTrue(simulation.QueueImpact(
            0.25,
            0.25,
            1.0f,
            1.0f,
            kind: LiquidSurfaceImpulseKind.DroppedItemEntry));
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, default);

        Assert.AreEqual(1, simulation.TotalSubgridImpactCount);
        Assert.AreEqual(0, simulation.WriteSubgridImpactsAfter(0, []));
    }

    /// <summary>Covers exact, variant, null, empty, prefix, and delimiter lookup outcomes.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void OpticalCatalogResolutionRequiresAnExactRootOrHyphenDelimitedVariant()
    {
        LiquidClientFallbackData parsed = Parse(ReadDocuments());
        LiquidOpticalProfile water = parsed.ProfilesByCodeRoot["game:water"];

        Assert.IsFalse(LiquidOpticalFallbackCatalog.TryResolve(
            null,
            new Dictionary<string, LiquidOpticalProfile>(),
            out _));
        Assert.IsTrue(LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:water"),
            new Dictionary<string, LiquidOpticalProfile> { ["game:water"] = water },
            out _));
        Assert.IsTrue(LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:water-still-7"),
            new Dictionary<string, LiquidOpticalProfile> { ["game:water"] = water },
            out _));
        Assert.IsFalse(LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:water"),
            new Dictionary<string, LiquidOpticalProfile> { ["game:water-long"] = water },
            out _));
        Assert.IsFalse(LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:water"),
            new Dictionary<string, LiquidOpticalProfile> { ["game:honey"] = water },
            out _));
        Assert.IsFalse(LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:water"),
            new Dictionary<string, LiquidOpticalProfile> { ["game:wat"] = water },
            out _));
        Assert.IsFalse(LiquidOpticalFallbackCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game:water"),
            new Dictionary<string, LiquidOpticalProfile>(),
            out _));
    }

    /// <summary>Returns the first liquid binding from a mutable document set.</summary>
    /// <param name="documents">Mutable catalog documents.</param>
    /// <returns>First liquid binding object.</returns>
    private static JObject FirstLiquidBinding(CatalogDocuments documents) =>
        (JObject)((JArray)documents.Bindings["bindings"]!)[0]!;

    /// <summary>Returns the first container binding from a mutable document set.</summary>
    /// <param name="documents">Mutable catalog documents.</param>
    /// <returns>First container binding object.</returns>
    private static JObject FirstContainerBinding(CatalogDocuments documents) =>
        (JObject)((JArray)documents.Bindings["containerBindings"]!)[0]!;

    /// <summary>Asserts that a malformed document set fails with the requested diagnostic.</summary>
    /// <param name="documents">Malformed mutable documents.</param>
    /// <param name="message">Required exception-message fragment.</param>
    private static void AssertInvalid(CatalogDocuments documents, string message)
    {
        InvalidDataException exception = Assert.ThrowsException<InvalidDataException>(() =>
            Parse(documents));
        StringAssert.Contains(exception.Message, message);
    }

    /// <summary>Invokes the deterministic production parser for one mutable document set.</summary>
    /// <param name="documents">Documents to encode as UTF-8 JSON.</param>
    /// <returns>Validated client catalog.</returns>
    private static LiquidClientFallbackData Parse(CatalogDocuments documents) =>
        LiquidOpticalFallbackCatalog.Parse(
            Bytes(documents.Optics),
            Bytes(documents.Physics),
            Bytes(documents.Measured),
            Bytes(documents.Bindings));

    /// <summary>Loads fresh mutable clones of the four checked-in liquid client assets.</summary>
    /// <returns>Independent mutable JSON documents.</returns>
    private static CatalogDocuments ReadDocuments()
    {
        string root = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "assets",
            "vintagertx");
        return new CatalogDocuments(
            JArray.Parse(File.ReadAllText(Path.Combine(root, "patches", "liquid-optical-profiles.json"))),
            JArray.Parse(File.ReadAllText(Path.Combine(root, "patches", "liquid-physical-properties.json"))),
            JArray.Parse(File.ReadAllText(Path.Combine(root, "patches", "liquid-zz-measured-water-optics.json"))),
            JObject.Parse(File.ReadAllText(Path.Combine(root, "config", "liquid-client-bindings.json"))));
    }

    /// <summary>Encodes one JSON document as UTF-8 bytes.</summary>
    /// <param name="token">JSON document.</param>
    /// <returns>Compact UTF-8 representation.</returns>
    private static byte[] Bytes(JToken token) => Encoding.UTF8.GetBytes(token.ToString());

    /// <summary>Creates one active water-like simulation cell.</summary>
    /// <param name="dynamics">Surface coefficients.</param>
    /// <param name="physics">SI physical properties.</param>
    /// <param name="eventBudget">Bounded event capacity.</param>
    /// <returns>Configured one-cell solver.</returns>
    private static LiquidSurfaceSimulation ConfiguredSimulation(
        LiquidSurfaceDynamics dynamics,
        LiquidSurfacePhysicalProperties physics,
        int eventBudget = 8)
    {
        LiquidSurfaceSimulation simulation = new(0, 0, 1, 1, 0.5f, eventBudget: eventBudget);
        simulation.SetSurfaceCell(
            0,
            0,
            0.875f,
            1,
            in dynamics,
            in physics,
            rainExposed: true,
            depthMetres: 1.0f);
        return simulation;
    }

    /// <summary>Creates stable water-like authored dynamics.</summary>
    /// <returns>Finite deterministic coefficients.</returns>
    private static LiquidSurfaceDynamics Dynamics() => new(
        WindCoupling: 0.4f,
        WaveAmplitude: 0.03f,
        WaveLength: 2.0f,
        WaveSpeed: 1.5f,
        Damping: 0.07f,
        ImpactResponse: 1.0f,
        SurfaceTension: 0.072f,
        BubbleRate: 0.0f,
        BubbleRadiusMinimum: 0.0f,
        BubbleRadiusMaximum: 0.0f,
        BubbleRiseDuration: 0.0f,
        BubbleBurstStrength: 0.0f,
        BubbleEmissionBoost: 0.0f);

    /// <summary>Creates stable water-like SI properties.</summary>
    /// <returns>Validated physical coefficients.</returns>
    private static LiquidSurfacePhysicalProperties Physics() => new(
        DensityKilogramsPerCubicMetre: 998.0f,
        DynamicViscosityPascalSeconds: 0.001f,
        SurfaceTensionNewtonsPerMetre: 0.072f,
        AdditionalDampingPerSecond: 0.07f,
        ResolvedWaveEnergyFraction: 0.5f);

    /// <summary>Creates one finite descending exact projectile contact.</summary>
    /// <param name="entityId">Stable detached entity identity.</param>
    /// <returns>Centered arrow collision sample.</returns>
    private static LiquidProjectileCollisionSample ProjectileSample(long entityId) => new(
        EntityId: entityId,
        SurfaceClass: LiquidEntitySurfaceClass.Projectile,
        WorldX: 0.25,
        WorldY: 0.80,
        WorldZ: 0.25,
        PreviousWorldX: 0.25,
        PreviousWorldY: 1.05,
        PreviousWorldZ: 0.25,
        IncidentMotionX: 0.10f,
        IncidentMotionY: -0.25f,
        IncidentMotionZ: 0.05f,
        OutgoingMotionX: 0.08f,
        OutgoingMotionY: -0.04f,
        OutgoingMotionZ: 0.02f,
        MassKilograms: LiquidSurfaceWorldInputs.ReferenceArrowMassKilograms,
        MotionSamplePeriodSeconds: LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds,
        IsServerAuthoritative: true);

    /// <summary>Creates one finite entity observation above the configured surface.</summary>
    /// <param name="entityId">Stable detached identity.</param>
    /// <param name="surfaceClass">Surface behavior taxonomy.</param>
    /// <returns>Finite initial observation.</returns>
    private static LiquidEntitySurfaceSample EntitySample(
        long entityId,
        LiquidEntitySurfaceClass surfaceClass) => new(
            entityId,
            surfaceClass,
            0.25,
            1.0,
            0.25,
            0.10f,
            -0.10f,
            0.0f,
            false,
            false,
            1.0f,
            LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds);

    /// <summary>Returns one private preallocated solver array by stable field name.</summary>
    /// <typeparam name="T">Array element type.</typeparam>
    /// <param name="simulation">Solver fixture.</param>
    /// <param name="name">Private field name.</param>
    /// <returns>Mutable bounded storage used only to exercise fail-closed numerical guards.</returns>
    private static T[] PrivateArray<T>(LiquidSurfaceSimulation simulation, string name) =>
        (T[])GetPrivateField(name).GetValue(simulation)!;

    /// <summary>Sets one private transient solver field to exercise its recovery contract.</summary>
    /// <param name="simulation">Solver fixture.</param>
    /// <param name="name">Private field name.</param>
    /// <param name="value">Transient fixture value.</param>
    private static void SetPrivateField(
        LiquidSurfaceSimulation simulation,
        string name,
        object value) => GetPrivateField(name).SetValue(simulation, value);

    /// <summary>Resolves one private solver field or fails with an actionable fixture error.</summary>
    /// <param name="name">Stable field name.</param>
    /// <returns>Reflected instance field.</returns>
    private static FieldInfo GetPrivateField(string name) =>
        typeof(LiquidSurfaceSimulation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(LiquidSurfaceSimulation).FullName, name);

    /// <summary>Mutable offline inputs consumed by the client fallback parser.</summary>
    /// <param name="Optics">Optical and container patch operations.</param>
    /// <param name="Physics">Physical-property patch operations.</param>
    /// <param name="Measured">Late measured-water corrections.</param>
    /// <param name="Bindings">Client code-root bindings.</param>
    private readonly record struct CatalogDocuments(
        JArray Optics,
        JArray Physics,
        JArray Measured,
        JObject Bindings);
}
