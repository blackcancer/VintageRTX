using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Direct, independently discoverable MSTest coverage for the deterministic
/// voxel-scene and liquid-optics contracts. Runtime API/event behaviour remains
/// the responsibility of the in-game scenarios because it cannot be represented
/// faithfully by a unit-test mock.
/// </summary>
[TestClass]
public sealed class VoxelSceneCoverageTests
{
    /// <summary>
    /// Verifies the liquid Container Metadata Accepts Open And State Controlled Surfaces regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidContainerMetadata_AcceptsOpenAndStateControlledSurfaces()
    {
        Block openBlock = CreateContainerBlock(CreateContainerContract());
        Assert.IsTrue(LiquidContainerOptics.TryRead(openBlock, out LiquidContainerOptics open));
        Assert.AreEqual(0, open.ContentSlot);
        Assert.AreEqual(10.0f, open.CapacityLitres);
        Assert.AreEqual(0.2f, open.SurfaceMinimumY);
        Assert.AreEqual(0.8f, open.SurfaceMaximumY);
        Assert.IsTrue(open.AlwaysOpen);
        Assert.AreEqual(string.Empty, open.VisibilityTreeBool);
        Assert.IsFalse(open.VisibleWhen);
        Assert.IsTrue(open.IsSurfaceVisible(new StateBlockEntity(false)));
        Assert.AreEqual((byte)51, open.EncodeSurfaceHeight(-1.0f));
        Assert.AreEqual((byte)128, open.EncodeSurfaceHeight(0.5f));
        Assert.AreEqual((byte)204, open.EncodeSurfaceHeight(2.0f));

        JObject closedContract = CreateContainerContract();
        JObject metadata = (JObject)closedContract["vintageRtxLiquidContainer"]!;
        metadata["alwaysOpen"] = false;
        metadata["visibilityTreeBool"] = "  sealed  ";
        metadata["visibleWhen"] = false;
        Assert.IsTrue(LiquidContainerOptics.TryRead(
            CreateContainerBlock(closedContract),
            out LiquidContainerOptics closed));
        Assert.AreEqual("sealed", closed.VisibilityTreeBool);
        Assert.IsTrue(closed.IsSurfaceVisible(new StateBlockEntity(false)));
        Assert.IsFalse(closed.IsSurfaceVisible(new StateBlockEntity(true)));

        metadata["visibleWhen"] = true;
        Assert.IsTrue(LiquidContainerOptics.TryRead(
            CreateContainerBlock(closedContract),
            out LiquidContainerOptics visibleWhenSealed));
        Assert.IsFalse(visibleWhenSealed.IsSurfaceVisible(new StateBlockEntity(false)));
        Assert.IsTrue(visibleWhenSealed.IsSurfaceVisible(new StateBlockEntity(true)));
    }

    /// <summary>
    /// Verifies the liquid Container Metadata Rejects Every Invalid Shape And Range regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidContainerMetadata_RejectsEveryInvalidShapeAndRange()
    {
        Assert.IsFalse(LiquidContainerOptics.TryRead(new Block(), out _));
        Assert.IsFalse(LiquidContainerOptics.TryRead(
            new Block { Attributes = Wrap(new JObject()) }, out _));

        string[] required =
        [
            "contentSlot", "capacityLitres", "surfaceMinimumY", "surfaceMaximumY"
        ];
        foreach (string property in required)
        {
            JObject contract = CreateContainerContract();
            ((JObject)contract["vintageRtxLiquidContainer"]!).Remove(property);
            Assert.IsFalse(LiquidContainerOptics.TryRead(CreateContainerBlock(contract), out _), property);
        }

        (string Property, JToken Value)[] invalidValues =
        [
            ("contentSlot", -1),
            ("capacityLitres", 0.0),
            ("capacityLitres", "not-a-number"),
            ("surfaceMinimumY", -0.01),
            ("surfaceMinimumY", 1.01),
            ("surfaceMinimumY", "not-a-number"),
            ("surfaceMaximumY", 0.1),
            ("surfaceMaximumY", 1.01),
            ("surfaceMaximumY", "not-a-number")
        ];
        foreach ((string property, JToken value) in invalidValues)
        {
            JObject contract = CreateContainerContract();
            ((JObject)contract["vintageRtxLiquidContainer"]!)[property] = value;
            Assert.IsFalse(LiquidContainerOptics.TryRead(CreateContainerBlock(contract), out _), property);
        }

        JObject hiddenWithoutState = CreateContainerContract();
        JObject hiddenMetadata = (JObject)hiddenWithoutState["vintageRtxLiquidContainer"]!;
        hiddenMetadata["alwaysOpen"] = false;
        hiddenMetadata["visibilityTreeBool"] = "   ";
        Assert.IsFalse(LiquidContainerOptics.TryRead(
            CreateContainerBlock(hiddenWithoutState), out _));
    }

    /// <summary>
    /// Verifies the liquid Optical Profile Parses All Authored Fields And Canonical Identity regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidOpticalProfile_ParsesAllAuthoredFieldsAndCanonicalIdentity()
    {
        Item liquid = CreateLiquid("fixture", "game:fixture-liquid");
        Assert.IsTrue(LiquidOpticalRegistry.TryReadProfile(liquid, out LiquidOpticalProfile profile));
        Assert.AreEqual("fixture", profile.ProfileKey);
        Assert.AreEqual(1.333f, profile.IndexOfRefraction);
        Assert.AreEqual(new LiquidRgb(0.01f, 0.02f, 0.03f), profile.Absorption);
        Assert.AreEqual(new LiquidRgb(0.04f, 0.05f, 0.06f), profile.Scattering);
        Assert.AreEqual(new LiquidRgb(0.07f, 0.08f, 0.09f), profile.Emission);
        Assert.AreEqual(0.92f, profile.Transmission);
        Assert.AreEqual(0.1f, profile.EmissionIntensity);
        Assert.AreEqual(0.12f, profile.Roughness);
        Assert.AreEqual(0.13f, profile.MicroNormalStrength);
        Assert.AreEqual(0.14f, profile.OpticalViscosity);
        Assert.AreEqual(0.15f, profile.ScatteringAnisotropy);
        Assert.AreEqual(0.16f, profile.SurfaceDynamics.WindCoupling);
        Assert.AreEqual(0.28f, profile.SurfaceDynamics.BubbleEmissionBoost);
        Assert.IsFalse(profile.Opaque);
        Assert.AreEqual("fixture-basis", profile.PhysicalProperties.BasisKey);
        Assert.AreEqual(998.2f, profile.PhysicalProperties.DensityKilogramsPerCubicMetre);
        Assert.AreEqual(0.001002f, profile.PhysicalProperties.DynamicViscosityPascalSeconds);
        Assert.AreEqual(0.07275f, profile.PhysicalProperties.SurfaceTensionNewtonsPerMetre);

        string canonical = profile.CanonicalIdentity;
        Assert.IsTrue(canonical.StartsWith("fixture|1.333|0.01,0.02,0.03|", StringComparison.Ordinal));
        Assert.IsTrue(canonical.Contains("|0|fixture-basis,", StringComparison.Ordinal));
        Assert.AreEqual(
            "0.16,0.17,1.8,1.9,0.2,0.21,1.22,0.23,0.24,0.25,2.6,0.27,0.28",
            profile.SurfaceDynamics.CanonicalIdentity);
        Assert.AreEqual("0.01,0.02,0.03", profile.Absorption.CanonicalIdentity);
        Assert.IsTrue(profile.Absorption.IsInRange(0.0f, 64.0f));
    }

    /// <summary>
    /// Verifies the liquid Optical Profile Rejects Missing Top Level And Dynamics Fields regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidOpticalProfile_RejectsMissingTopLevelAndDynamicsFields()
    {
        Assert.IsFalse(LiquidOpticalRegistry.TryReadProfile(
            new Item { Code = new AssetLocation("game:missing-attributes") }, out _));
        Assert.IsFalse(LiquidOpticalRegistry.TryReadProfile(
            new Item
            {
                Code = new AssetLocation("game:missing-optics"),
                Attributes = Wrap(new JObject())
            },
            out _));

        string[] topLevel =
        [
            "profile", "ior", "absorptionRgb", "scatteringRgb", "transmission",
            "emissionRgb", "emissionIntensity", "roughness", "microNormalStrength",
            "opticalViscosity", "scatteringAnisotropy", "surfaceDynamics", "opaque"
        ];
        foreach (string property in topLevel)
        {
            JObject root = CreateOpticsContract("fixture");
            ((JObject)root[LiquidOpticalRegistry.OpticalAttributeName]!).Remove(property);
            AssertRejected(root, $"missing {property}");
        }

        string[] dynamics =
        [
            "windCoupling", "waveAmplitude", "waveLength", "waveSpeed", "damping",
            "impactResponse", "surfaceTension", "bubbleRate", "bubbleRadiusMin",
            "bubbleRadiusMax", "bubbleRiseDuration", "bubbleBurstStrength",
            "bubbleEmissionBoost"
        ];
        foreach (string property in dynamics)
        {
            JObject root = CreateOpticsContract("fixture");
            Dynamics(root).Remove(property);
            AssertRejected(root, $"missing surfaceDynamics.{property}");
        }

        string[] physicalFields =
        [
            "basis", "referenceTemperatureK", "densityKgM3", "dynamicViscosityPaS",
            "surfaceTensionNm", "resolvedWaveEnergyFraction", "thermalTemperatureK",
            "thermalEmissivity"
        ];
        foreach (string property in physicalFields)
        {
            JObject root = CreateOpticsContract("fixture");
            ((JObject)root[LiquidOpticalRegistry.PhysicalAttributeName]!).Remove(property);
            AssertRejected(root, $"missing physics.{property}");
        }
    }

    /// <summary>
    /// Verifies the liquid Optical Profile Rejects Malformed And Out Of Range Rgb regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidOpticalProfile_RejectsMalformedAndOutOfRangeRgb()
    {
        string[] rgbProperties = ["absorptionRgb", "scatteringRgb", "emissionRgb"];
        foreach (string property in rgbProperties)
        {
            JObject missing = CreateOpticsContract("fixture");
            Optics(missing)[property] = new JArray(0.1, 0.2);
            AssertRejected(missing, $"short {property}");

            JObject nonNumeric = CreateOpticsContract("fixture");
            Optics(nonNumeric)[property] = new JArray("bad", 0.2, 0.3);
            AssertRejected(nonNumeric, $"non-finite {property}");

            JObject nonArray = CreateOpticsContract("fixture");
            Optics(nonArray)[property] = new JObject { ["red"] = 0.1 };
            AssertRejected(nonArray, $"non-array {property}");

            JObject below = CreateOpticsContract("fixture");
            Optics(below)[property] = new JArray(-0.01, 0.2, 0.3);
            AssertRejected(below, $"negative {property}");

            JObject above = CreateOpticsContract("fixture");
            Optics(above)[property] = new JArray(64.01, 0.2, 0.3);
            AssertRejected(above, $"oversized {property}");
        }

        Assert.IsFalse(new LiquidRgb(float.NaN, 0.0f, 0.0f).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(0.0f, float.PositiveInfinity, 0.0f).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(0.0f, 0.0f, float.NegativeInfinity).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(-0.1f, 0.0f, 0.0f).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(0.0f, -0.1f, 0.0f).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(0.0f, 0.0f, -0.1f).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(1.1f, 0.0f, 0.0f).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(0.0f, 1.1f, 0.0f).IsInRange(0.0f, 1.0f));
        Assert.IsFalse(new LiquidRgb(0.0f, 0.0f, 1.1f).IsInRange(0.0f, 1.0f));
    }

    /// <summary>
    /// Verifies the liquid Optical Profile Rejects Every Scalar Outside Its Physical Range regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidOpticalProfile_RejectsEveryScalarOutsideItsPhysicalRange()
    {
        (string Path, JToken Value)[] invalid =
        [
            ("profile", "   "),
            ("ior", 0.999), ("ior", 3.001), ("ior", "bad"),
            ("transmission", -0.001), ("transmission", 1.001),
            ("emissionIntensity", -0.001), ("emissionIntensity", 64.001),
            ("roughness", -0.001), ("roughness", 1.001),
            ("microNormalStrength", -0.001), ("microNormalStrength", 4.001),
            ("opticalViscosity", -0.001), ("opticalViscosity", 1.001),
            ("scatteringAnisotropy", -0.951), ("scatteringAnisotropy", 0.951),
            ("surfaceDynamics.windCoupling", -0.001), ("surfaceDynamics.windCoupling", 4.001),
            ("surfaceDynamics.waveAmplitude", -0.001), ("surfaceDynamics.waveAmplitude", 4.001),
            ("surfaceDynamics.waveLength", 0.009), ("surfaceDynamics.waveLength", 256.001),
            ("surfaceDynamics.waveSpeed", -0.001), ("surfaceDynamics.waveSpeed", 32.001),
            ("surfaceDynamics.damping", -0.001), ("surfaceDynamics.damping", 1.001),
            ("surfaceDynamics.impactResponse", -0.001), ("surfaceDynamics.impactResponse", 4.001),
            ("surfaceDynamics.surfaceTension", -0.001), ("surfaceDynamics.surfaceTension", 4.001),
            ("surfaceDynamics.bubbleRate", -0.001), ("surfaceDynamics.bubbleRate", 64.001),
            ("surfaceDynamics.bubbleRadiusMin", -0.001), ("surfaceDynamics.bubbleRadiusMin", 4.001),
            ("surfaceDynamics.bubbleRadiusMax", 0.20), ("surfaceDynamics.bubbleRadiusMax", 4.001),
            ("surfaceDynamics.bubbleRiseDuration", -0.001), ("surfaceDynamics.bubbleRiseDuration", 64.001),
            ("surfaceDynamics.bubbleBurstStrength", -0.001), ("surfaceDynamics.bubbleBurstStrength", 16.001),
            ("surfaceDynamics.bubbleEmissionBoost", -0.001), ("surfaceDynamics.bubbleEmissionBoost", 64.001),
            ("physics.basis", "   "),
            ("physics.referenceTemperatureK", 199.9), ("physics.referenceTemperatureK", 3000.1),
            ("physics.densityKgM3", 99.9), ("physics.densityKgM3", 10000.1),
            ("physics.dynamicViscosityPaS", 0.0), ("physics.dynamicViscosityPaS", 100000.1),
            ("physics.surfaceTensionNm", 0.0), ("physics.surfaceTensionNm", 10.1),
            ("physics.resolvedWaveEnergyFraction", -0.001), ("physics.resolvedWaveEnergyFraction", 1.001),
            ("physics.thermalTemperatureK", -0.001), ("physics.thermalTemperatureK", 3000.1),
            ("physics.thermalEmissivity", -0.001), ("physics.thermalEmissivity", 1.001)
        ];
        foreach ((string path, JToken value) in invalid)
        {
            JObject root = CreateOpticsContract("fixture");
            if (path.StartsWith("surfaceDynamics.", StringComparison.Ordinal))
            {
                Dynamics(root)[path["surfaceDynamics.".Length..]] = value;
            }
            else if (path.StartsWith("physics.", StringComparison.Ordinal))
            {
                ((JObject)root[LiquidOpticalRegistry.PhysicalAttributeName]!)[
                    path["physics.".Length..]] = value;
            }
            else
            {
                Optics(root)[path] = value;
            }
            AssertRejected(root, path);
        }
    }

    /// <summary>
    /// Verifies the liquid Optical Registry Deduplicates Profiles And Assigns Stable Ids regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidOpticalRegistry_DeduplicatesProfilesAndAssignsStableIds()
    {
        Item sharedB = CreateLiquid("shared", "game:z-shared");
        Item distinct = CreateLiquid("distinct", "game:m-distinct");
        Item sharedA = CreateLiquid("shared", "game:a-shared");
        Item invalid = new() { Code = new AssetLocation("game:invalid") };
        Item noCode = new() { Code = null!, Attributes = sharedA.Attributes };
        CollectibleObject[] inputs = [sharedB, invalid, null!, distinct, noCode, sharedA];

        LiquidOpticalRegistry registry = new(inputs);
        LiquidOpticalRegistry reversed = new(inputs.Reverse());
        Assert.AreEqual(2, registry.ProfileCount);
        Assert.AreEqual(2, reversed.ProfileCount);
        Assert.AreEqual(registry.GetProfileId(sharedA), registry.GetProfileId(sharedB));
        Assert.AreNotEqual(registry.GetProfileId(sharedA), registry.GetProfileId(distinct));
        Assert.AreEqual(registry.GetProfileId(sharedA), reversed.GetProfileId(sharedA));
        Assert.AreEqual(registry.GetProfileId(distinct), reversed.GetProfileId(distinct));
        Assert.AreEqual(LiquidOpticalRegistry.UnknownLiquidProfileId, registry.GetProfileId(invalid));
        Assert.IsNull(registry.GetProfile(LiquidOpticalRegistry.NoLiquidProfileId));
        Assert.IsNull(registry.GetProfile(LiquidOpticalRegistry.UnknownLiquidProfileId));
        Assert.AreEqual("shared", registry.GetProfile(registry.GetProfileId(sharedA))!.Value.ProfileKey);
        Assert.AreEqual(LiquidOpticalRegistry.LookupWidth * LiquidOpticalRegistry.LookupHeight
            * LiquidOpticalRegistry.LookupChannels, registry.GpuLookup.Length);
    }

    /// <summary>
    /// Verifies the liquid Optical Registry Uses Neutral Fallback After Profile Capacity regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidOpticalRegistry_UsesNeutralFallbackAfterProfileCapacity()
    {
        List<Item> liquids = [];
        for (int index = 0; index < LiquidOpticalRegistry.UnknownLiquidProfileId; index++)
        {
            string suffix = index.ToString("D3", CultureInfo.InvariantCulture);
            liquids.Add(CreateLiquid($"profile-{suffix}", $"game:liquid-{suffix}"));
        }

        ILogger logger = DispatchProxy.Create<ILogger, RecordingLoggerProxy>();
        RecordingLoggerProxy recorder = (RecordingLoggerProxy)(object)logger;
        LiquidOpticalRegistry registry = new(liquids, logger);
        Assert.AreEqual(254, registry.ProfileCount);
        Assert.AreNotEqual(LiquidOpticalRegistry.UnknownLiquidProfileId, registry.GetProfileId(liquids[0]));
        Assert.AreEqual(LiquidOpticalRegistry.UnknownLiquidProfileId, registry.GetProfileId(liquids[^1]));
        Assert.IsNull(registry.GetProfile(LiquidOpticalRegistry.UnknownLiquidProfileId));
        CollectionAssert.Contains(recorder.Calls, nameof(ILogger.Warning));
        CollectionAssert.Contains(recorder.Calls, nameof(ILogger.Notification));
    }

    /// <summary>
    /// Verifies the liquid Optical Registry Gpu Lookup Preserves Neutral And Authored Rows regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void LiquidOpticalRegistry_GpuLookupPreservesNeutralAndAuthoredRows()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            LiquidOpticalRegistry.BuildGpuLookupForProfiles(null!));

        LiquidOpticalProfile transparent = CreateProfile("transparent", opaque: false);
        LiquidOpticalProfile opaque = CreateProfile("opaque", opaque: true);
        Assert.IsTrue(opaque.CanonicalIdentity.Contains("|1|fixture-basis,", StringComparison.Ordinal));
        float[] lookup = LiquidOpticalRegistry.BuildGpuLookupForProfiles(
            new Dictionary<byte, LiquidOpticalProfile>
            {
                [2] = transparent,
                [7] = opaque
            });
        Assert.AreEqual(
            LiquidOpticalRegistry.LookupWidth
                * LiquidOpticalRegistry.LookupHeight
                * LiquidOpticalRegistry.LookupChannels,
            lookup.Length);
        AssertTexel(lookup, 0, 0, 1.0f, 1.0f, 1.0f, 0.0f);
        AssertTexel(lookup, 255, 0, 1.0f, 1.0f, 1.0f, 0.0f);
        AssertTexel(lookup, 2, 1, 0.01f, 0.02f, 0.03f, 0.0f);
        AssertTexel(lookup, 7, 1, 0.01f, 0.02f, 0.03f, 1.0f);
        AssertTexel(lookup, 7, 7, 0.27f, 0.28f, 0.0f, 0.0f);
        AssertTexel(lookup, 7, 8, 998.2f, 0.001002f, 0.07275f, 0.02f);
        AssertTexel(lookup, 7, 9, 0.0f, 0.0f, 1.0f, 0.0f);

        foreach (byte reserved in new byte[] { 0, byte.MaxValue })
        {
            Dictionary<byte, LiquidOpticalProfile> invalid = new() { [reserved] = transparent };
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                LiquidOpticalRegistry.BuildGpuLookupForProfiles(invalid));
        }
    }

    /// <summary>
    /// Verifies the voxel Scene Sun Clipmap And Dirty Volume Boundaries Are Conservative regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_SunClipmapAndDirtyVolumeBoundariesAreConservative()
    {
        Assert.AreEqual(152, VoxelScene.SunWorldWidth);
        Assert.AreEqual(116, VoxelScene.SunWorldHeight);
        Assert.AreEqual(152, VoxelScene.SunWorldDepth);
        Assert.AreEqual(96, VoxelScene.MaximumSunTraceDistance);
        Vec3f[] directions =
        [
            new(-1.0f, -1.0f, -1.0f), new(1.0f, 1.0f, 1.0f),
            new(-4.0f, 4.0f, 0.0f), new(0.0f, 0.0f, 0.0f)
        ];
        foreach (Vec3f direction in directions)
        {
            (int x, int y, int z) = VoxelScene.CalculateSunClipmapOrigin(-101, 79, -99, direction);
            Assert.AreEqual(0, x % VoxelScene.SunOccupancyScale);
            Assert.AreEqual(0, y % VoxelScene.SunOccupancyScale);
            Assert.AreEqual(0, z % VoxelScene.SunOccupancyScale);
            Assert.IsTrue(VoxelScene.SunClipmapContainsFullTrace(
                x, y, z, -101, 79, -99, direction));
            Assert.IsFalse(VoxelScene.SunClipmapContainsFullTrace(
                x + VoxelScene.SunWorldWidth, y, z, -101, 79, -99, direction));
            Assert.IsFalse(VoxelScene.SunClipmapContainsFullTrace(
                x, y + VoxelScene.SunWorldHeight, z, -101, 79, -99, direction));
            Assert.IsFalse(VoxelScene.SunClipmapContainsFullTrace(
                x, y, z + VoxelScene.SunWorldDepth, -101, 79, -99, direction));
        }

        Assert.AreEqual((true, true), VoxelScene.ClassifyDirtyBlockVolumes(
            0, 0, 0, 0, 0, 0, -32, -32, -32));
        Assert.AreEqual((false, true), VoxelScene.ClassifyDirtyBlockVolumes(
            VoxelScene.Width, 0, 0, 0, 0, 0, -32, -32, -32));
        Assert.AreEqual((false, false), VoxelScene.ClassifyDirtyBlockVolumes(
            999, 999, 999, 0, 0, 0, -32, -32, -32));
        Assert.AreEqual((false, false), VoxelScene.ClassifyDirtyBlockVolumes(
            -1, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    /// <summary>
    /// Verifies the dedicated liquid map covers Cinematic's 48-block radius at the recenter
    /// threshold and retains the old positive 32-block boundary as an ordinary interior column.
    /// </summary>
    [TestMethod]
    public void VoxelScene_LiquidSurfaceFootprintGuaranteesCinematicReachAndPositiveBoundary()
    {
        Assert.AreEqual(64, VoxelScene.Width);
        Assert.AreEqual(64, VoxelScene.Depth);
        Assert.AreEqual(128, VoxelScene.FluidSurfaceWidth);
        Assert.AreEqual(128, VoxelScene.FluidSurfaceDepth);

        const int centeredOrigin = -64;
        Assert.AreEqual(
            48,
            VoxelScene.CalculateSurfaceCoverageReach(centeredOrigin, 128, 16),
            "At the exact recenter threshold, Cinematic still owns 48 complete blocks.");
        Assert.IsTrue(VoxelScene.TryMapFluidSurfaceColumn(
            32,
            32,
            centeredOrigin,
            centeredOrigin,
            out int positiveBoundaryX,
            out int positiveBoundaryZ));
        Assert.AreEqual(96, positiveBoundaryX);
        Assert.AreEqual(96, positiveBoundaryZ);
        Assert.IsTrue(VoxelScene.TryMapFluidSurfaceColumn(
            63, 63, centeredOrigin, centeredOrigin, out _, out _));
        Assert.IsFalse(VoxelScene.TryMapFluidSurfaceColumn(
            64, 64, centeredOrigin, centeredOrigin, out _, out _));
    }

    /// <summary>
    /// Verifies the voxel Scene Lifecycle Status And Upload Queues Use The Public Runtime Contract regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_LifecycleStatusAndUploadQueuesUseThePublicRuntimeContract()
    {
        List<string> eventCalls = [];
        Action<float>? tick = null;
        IClientEventAPI eventApi = RuntimeCoverageDispatchProxy.Create<IClientEventAPI>(
            (method, arguments) =>
            {
                eventCalls.Add(method.Name);
                if (method.Name == "RegisterGameTickListener")
                {
                    tick = (Action<float>)arguments![0]!;
                    return 42L;
                }

                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            });
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>(
            (method, _) => method.Name switch
            {
                "get_Collectibles" => new List<CollectibleObject>(),
                "get_Player" => null,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>(
            (method, _) => method.Name switch
            {
                "get_Event" => eventApi,
                "get_Logger" => logger,
                "get_World" => world,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });

        VoxelScene scene = new(api);
        Assert.AreEqual("waiting for player", scene.Status);
        Assert.IsFalse(scene.IsReady);
        Assert.IsFalse(scene.IsSettled);
        Assert.IsFalse(scene.TryConsumeUpload(out _));
        Assert.IsFalse(scene.TryConsumeBlockUpdates(out VoxelSceneBlockUpdate[] noBlocks));
        Assert.AreEqual(0, noBlocks.Length);
        Assert.IsFalse(scene.TryConsumeFluidSurfaceUpdates(out VoxelFluidSurfaceUpdate[] noFluid));
        Assert.AreEqual(0, noFluid.Length);
        Assert.IsFalse(scene.TryConsumeSunOccupancyUpdates(out VoxelSunOccupancyUpdate[] noSun));
        Assert.AreEqual(0, noSun.Length);
        Assert.IsFalse(scene.TryConsumeRainSurfaceUpdates(out VoxelRainSurfaceUpdate[] noRain));
        Assert.AreEqual(0, noRain.Length);
        Assert.IsNotNull(tick);
        tick(0.02f);

        SetField(scene, "building", true);
        SetField(scene, "buildIndex", 1024);
        SetField(scene, "sunBuildIndex", 512);
        SetField(scene, "rainSurfaceBuildIndex", 128);
        SetField(scene, "originX", -4);
        SetField(scene, "originY", 8);
        SetField(scene, "originZ", 12);
        StringAssert.StartsWith(scene.Status, "building ");
        Assert.IsFalse(scene.IsReady);
        Assert.IsFalse(scene.IsSettled);

        SetField(scene, "building", false);
        SetField(scene, "generation", 7);
        SetField(scene, "uploadPending", true);
        Assert.AreEqual("ready gen=7, origin=(-4,8,12), lights=0", scene.Status);
        Assert.IsTrue(scene.IsReady);
        Assert.IsFalse(scene.IsSettled, "a complete CPU generation is not settled before its full upload is consumed");
        Assert.IsTrue(scene.TryConsumeUpload(out VoxelSceneSnapshot snapshot));
        Assert.AreEqual(7, snapshot.Generation);
        Assert.AreEqual(LiquidOpticalRegistry.LookupWidth * LiquidOpticalRegistry.LookupHeight
            * LiquidOpticalRegistry.LookupChannels, snapshot.LiquidOpticalProfileLookup.Length);
        Assert.IsFalse(scene.TryConsumeUpload(out _));

        Assert.IsTrue(scene.IsSettled);
        SetField(scene, "rebuildRequested", true);
        Assert.IsFalse(scene.IsSettled, "a requested replacement makes the current generation obsolete");
        SetField(scene, "rebuildRequested", false);
        SetField(scene, "settleRebuildPending", true);
        Assert.IsFalse(scene.IsSettled, "the delayed confirmation scan must complete before capture");
        SetField(scene, "settleRebuildPending", false);
        HashSet<(int X, int Y, int Z)> dirtyBlocks = GetField<HashSet<(int X, int Y, int Z)>>(
            scene,
            "dirtyBlocks");
        dirtyBlocks.Add((1, 2, 3));
        Assert.IsFalse(scene.IsSettled, "incremental world edits must reach the upload queue before capture");
        dirtyBlocks.Clear();
        Assert.IsTrue(scene.IsSettled);

        List<VoxelSceneBlockUpdate> blockQueue = GetField<List<VoxelSceneBlockUpdate>>(
            scene, "pendingBlockUpdates");
        List<VoxelSunOccupancyUpdate> sunQueue = GetField<List<VoxelSunOccupancyUpdate>>(
            scene, "pendingSunOccupancyUpdates");
        List<VoxelFluidSurfaceUpdate> fluidQueue = GetField<List<VoxelFluidSurfaceUpdate>>(
            scene, "pendingFluidSurfaceUpdates");
        List<VoxelRainSurfaceUpdate> rainQueue = GetField<List<VoxelRainSurfaceUpdate>>(
            scene, "pendingRainSurfaceUpdates");
        blockQueue.Add(new VoxelSceneBlockUpdate(1, 2, 3, [4], [5], [6], [7]));
        fluidQueue.Add(new VoxelFluidSurfaceUpdate(2, 3, [8]));
        sunQueue.Add(new VoxelSunOccupancyUpdate(8, 9, 10, 11));
        rainQueue.Add(new VoxelRainSurfaceUpdate(12, 13, 14.0f));
        Assert.IsFalse(scene.IsSettled, "partial GPU updates are part of the stability contract");
        Assert.IsTrue(scene.TryConsumeBlockUpdates(out VoxelSceneBlockUpdate[] blocks));
        Assert.AreEqual(1, blocks.Length);
        Assert.IsTrue(scene.TryConsumeFluidSurfaceUpdates(out VoxelFluidSurfaceUpdate[] fluid));
        Assert.AreEqual(1, fluid.Length);
        Assert.IsTrue(scene.TryConsumeSunOccupancyUpdates(out VoxelSunOccupancyUpdate[] sun));
        Assert.AreEqual(1, sun.Length);
        Assert.IsTrue(scene.TryConsumeRainSurfaceUpdates(out VoxelRainSurfaceUpdate[] rain));
        Assert.AreEqual(1, rain.Length);
        Assert.AreEqual(0, blockQueue.Count);
        Assert.AreEqual(0, fluidQueue.Count);
        Assert.AreEqual(0, sunQueue.Count);
        Assert.AreEqual(0, rainQueue.Count);
        Assert.IsTrue(scene.IsSettled);

        SetField(scene, "uploadPending", true);
        blockQueue.Add(default);
        fluidQueue.Add(default);
        sunQueue.Add(default);
        rainQueue.Add(default);
        Assert.IsFalse(scene.TryConsumeBlockUpdates(out _));
        Assert.IsFalse(scene.TryConsumeFluidSurfaceUpdates(out _));
        Assert.IsFalse(scene.TryConsumeSunOccupancyUpdates(out _));
        Assert.IsFalse(scene.TryConsumeRainSurfaceUpdates(out _));
        Assert.IsFalse(scene.IsSettled);

        scene.Dispose();
        scene.Dispose();
        CollectionAssert.Contains(eventCalls, "RegisterGameTickListener");
        CollectionAssert.Contains(eventCalls, "add_BlockChanged");
        CollectionAssert.Contains(eventCalls, "remove_BlockChanged");
        CollectionAssert.Contains(eventCalls, "UnregisterGameTickListener");
    }

    /// <summary>
    /// Verifies the voxel Scene Emission Pocket And Caster Bands Handle Empty And Detailed Masks regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_EmissionPocketAndCasterBandsHandleEmptyAndDetailedMasks()
    {
        Vec3f centered = VoxelScene.FindEmissionOffset(null);
        Assert.AreEqual(0.5f, centered.X);
        Assert.AreEqual(0.5f, VoxelScene.FindEmissionOffset(new MeshData(false)).Y);
        MeshData nonEmissive = new(false)
        {
            xyz = [0.1f, 0.2f, 0.3f],
            Flags = [0],
            VerticesCount = 1
        };
        Assert.AreEqual(0.5f, VoxelScene.FindEmissionOffset(nonEmissive).Z);

        MeshData emissive = new(false)
        {
            xyz =
            [
                0.10f, 0.20f, 0.30f,
                0.12f, 0.22f, 0.32f,
                0.80f, 0.80f, 0.80f
            ],
            Flags = [196, 196, 196],
            VerticesCount = 3
        };
        Vec3f offset = VoxelScene.FindEmissionOffset(emissive);
        Assert.IsTrue(offset.X is > 0.10f and < 0.13f);

        LightCasterBandCoverage empty = VoxelScene.MeasureLightCasterBands(null);
        Assert.AreEqual(VoxelScene.LightCasterVoxelCount, empty.Empty);
        byte[] mask = Enumerable.Repeat((byte)255, VoxelScene.LightCasterVoxelCount).ToArray();
        LightCasterBandCoverage full = VoxelScene.MeasureLightCasterBands(mask);
        Assert.IsTrue(full.LowerOccupied > 0);
        Assert.IsTrue(full.MiddleOccupied > 0);
        Assert.IsTrue(full.UpperOccupied > 0);
        Assert.AreEqual(0, full.Empty);
        VoxelScene.ClearEmissionPocket(mask, new Vec3f(0.5f, 0.5f, 0.5f));
        LightCasterBandCoverage cleared = VoxelScene.MeasureLightCasterBands(mask);
        Assert.IsTrue(cleared.Empty > 0);
        Assert.IsTrue(cleared.LowerOccupied > 0 && cleared.UpperOccupied > 0);
    }

    /// <summary>
    /// Verifies the voxel Scene Alpha Coverage And Shadow Passes Respect Transparency regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_AlphaCoverageAndShadowPassesRespectTransparency()
    {
        foreach (EnumChunkRenderPass pass in new[]
        {
            EnumChunkRenderPass.Opaque,
            EnumChunkRenderPass.OpaqueNoCull
        })
        {
            Assert.IsTrue(VoxelScene.ShouldCastShadow(pass));
        }
        foreach (EnumChunkRenderPass pass in new[]
        {
            EnumChunkRenderPass.BlendNoCull,
            EnumChunkRenderPass.Transparent,
            EnumChunkRenderPass.Liquid
        })
        {
            Assert.IsFalse(VoxelScene.ShouldCastShadow(pass));
        }

        TextureAlphaSampler sampler = new(
            new TextureAtlasPosition { x1 = 0, y1 = 0, x2 = 1, y2 = 1 },
            new TextureAlphaData(2, 2, [0, 255, 255, 0]));
        Assert.IsFalse(sampler.IsOpaque(-1.0f, -1.0f));
        Assert.IsFalse(sampler.IsOpaque(2.0f, 2.0f));
        Assert.IsTrue(sampler.IsOpaque(0.75f, 0.25f));
        TextureAlphaSampler invalidAtlas = new(
            new TextureAtlasPosition { x1 = 1, y1 = 1, x2 = 0, y2 = 0 },
            new TextureAlphaData(1, 1, [0]));
        Assert.IsTrue(invalidAtlas.IsOpaque(0.5f, 0.5f));

        byte degenerate = VoxelScene.TriangleCellOpaqueAlphaCoverage(
            new Vec3f(0.5f, 0.5f, 0.5f),
            VoxelScene.OccupancyScale,
            new Vec3f(), new Vec3f(), new Vec3f(),
            new TextureUv(), new TextureUv(), new TextureUv(), sampler);
        Assert.AreEqual((byte)0, degenerate);

        TextureAlphaSampler opaque = new(
            new TextureAtlasPosition { x1 = 0, y1 = 0, x2 = 1, y2 = 1 },
            new TextureAlphaData(1, 1, [255]));
        byte coverage = VoxelScene.TriangleCellOpaqueAlphaCoverage(
            new Vec3f(0.125f, 0.125f, 0.5f),
            VoxelScene.OccupancyScale,
            new Vec3f(0, 0, 0.5f), new Vec3f(1, 0, 0.5f), new Vec3f(0, 1, 0.5f),
            new TextureUv(0, 0), new TextureUv(1, 0), new TextureUv(0, 1), opaque);
        Assert.AreEqual((byte)255, coverage);
    }

    /// <summary>
    /// Verifies the voxel Scene Mesh Rasterization Rejects Invalid And Transparent Geometry regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_MeshRasterizationRejectsInvalidAndTransparentGeometry()
    {
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(null));
        Assert.IsFalse(VoxelScene.IsCanonicalUnitCubeMesh(new MeshData(false)));
        Assert.AreEqual(0UL, VoxelScene.RasterizeOpaqueMeshMask(new MeshData(false)));

        MeshData invalidIndices = new(false)
        {
            xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Indices = [0, 1, 7],
            VerticesCount = 3,
            IndicesCount = 3,
            IndicesPerFace = 3
        };
        Assert.AreEqual(0UL, VoxelScene.RasterizeOpaqueMeshMask(invalidIndices));

        MeshData opaque = CreateQuadMesh(
            [
                new Vec3f(0, 0, 0.5f), new Vec3f(1, 0, 0.5f),
                new Vec3f(1, 1, 0.5f), new Vec3f(0, 1, 0.5f)
            ]);
        Assert.AreNotEqual(0UL, VoxelScene.RasterizeOpaqueMeshMask(opaque));
        MeshData transparent = VoxelScene.CloneInstanceMesh(opaque);
        transparent.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        transparent.RenderPassCount = 1;
        Assert.AreEqual(0UL, VoxelScene.RasterizeOpaqueMeshMask(transparent));

        Assert.IsTrue(VoxelScene.TriangleIntersectsVoxel(
            new Vec3f(0.125f, 0.125f, 0.125f),
            new Vec3f(0, 0, 0.2f), new Vec3f(0.25f, 0, 0.2f), new Vec3f(0, 0.25f, 0.2f)));
        Assert.IsFalse(VoxelScene.TriangleIntersectsVoxel(
            new Vec3f(0.125f, 0.125f, 0.125f),
            new Vec3f(0, 0, 0.9f), new Vec3f(0.25f, 0, 0.9f), new Vec3f(0, 0.25f, 0.9f)));
        Assert.IsTrue(VoxelScene.TriangleIntersectsVoxel(
            new Vec3f(0, 0, 0), new Vec3f(), new Vec3f(), new Vec3f(), 4));
    }

    /// <summary>
    /// Verifies the voxel Scene Dynamic Occupancy Covers Collision Selection And Fallback Rules regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_DynamicOccupancyCoversCollisionSelectionAndFallbackRules()
    {
        Cuboidf[] narrow = [new(0, 0, 0, 0.25f, 1, 1)];
        Cuboidf[] wide = [new(0, 0, 0, 0.75f, 1, 1)];
        ulong narrowMask = VoxelScene.BuildInstanceOccupancyMask(narrow, null, false);
        ulong wideMask = VoxelScene.BuildInstanceOccupancyMask(null, wide, false);
        Assert.AreNotEqual(0UL, narrowMask);
        Assert.AreNotEqual(narrowMask, wideMask);
        Assert.AreEqual(0UL, VoxelScene.BuildInstanceOccupancyMask(null, null, false));
        Assert.AreEqual(ulong.MaxValue, VoxelScene.BuildInstanceOccupancyMask(null, null, true));

        const ulong baseMask = 0x0066UL;
        Assert.AreEqual(narrowMask, VoxelScene.ResolveDynamicInstanceOccupancy(
            baseMask, true, true, narrowMask, 0, true));
        Assert.AreEqual(ulong.MaxValue, VoxelScene.ResolveDynamicInstanceOccupancy(
            baseMask, true, true, 0, 0, true));
        Assert.AreEqual(0UL, VoxelScene.ResolveDynamicInstanceOccupancy(
            baseMask, true, true, 0, 0, false));
        Assert.AreEqual(baseMask | narrowMask, VoxelScene.ResolveDynamicInstanceOccupancy(
            baseMask, true, false, narrowMask, 0, true));
        Assert.AreEqual(baseMask, VoxelScene.ResolveDynamicInstanceOccupancy(
            baseMask, true, false, ulong.MaxValue, ulong.MaxValue, true));
        Assert.AreEqual(narrowMask, VoxelScene.ResolveDynamicInstanceOccupancy(
            0, false, false, narrowMask, 0, false));
        Assert.AreEqual(ulong.MaxValue, VoxelScene.ResolveDynamicInstanceOccupancy(
            0, false, false, 0, 0, true));
        Assert.AreEqual(0UL, VoxelScene.ResolveDynamicInstanceOccupancy(
            0, true, false, 0, 0, true));

        Assert.IsTrue(VoxelScene.TryResolveTessellatedInstanceOccupancy(
            true, 0, true, true, baseMask, 0, out ulong authoritative));
        Assert.AreEqual(0UL, authoritative);
        Assert.IsTrue(VoxelScene.TryResolveTessellatedInstanceOccupancy(
            true, 1, false, false, baseMask, narrowMask, out ulong combined));
        Assert.AreEqual(baseMask | narrowMask, combined);
        Assert.IsFalse(VoxelScene.TryResolveTessellatedInstanceOccupancy(
            false, 1, true, true, baseMask, narrowMask, out ulong unresolved));
        Assert.AreEqual(0UL, unresolved);

        Assert.IsTrue(VoxelScene.InstanceGeometryReplacesDefaultMesh("BlockEntityChisel", null));
        Assert.IsTrue(VoxelScene.InstanceGeometryReplacesDefaultMesh(null, "game:micro-block"));
        Assert.IsFalse(VoxelScene.InstanceGeometryReplacesDefaultMesh("BlockEntityAnvil", "game:anvil"));
        Assert.IsTrue(VoxelScene.InstanceGeometryReplacesDefaultMesh(new Block
        {
            EntityClass = "MicroBlock",
            Code = new AssetLocation("game:fixture")
        }));
    }

    /// <summary>
    /// Verifies that an invisible multiblock proxy projects its principal's authored
    /// upper-cell mesh instead of promoting the proxy's full collision box to a cube.
    /// </summary>
    [TestMethod]
    public void VoxelScene_MultiblockProxyUsesPrincipalAuthoredGeometryInsteadOfFullCollision()
    {
        TreeAttribute proxyState = new();
        proxyState.SetInt("cx", 100);
        proxyState.SetInt("cy", 50);
        proxyState.SetInt("cz", -75);
        Assert.IsTrue(VoxelScene.TryResolveMultiblockPrincipal(
            "Vintagestory.GameContent.Mechanics.BEMPMultiblock",
            proxyState,
            3,
            out BlockPos principal));
        Assert.AreEqual(new BlockPos(100, 50, -75, 3), principal);
        Assert.IsFalse(VoxelScene.TryResolveMultiblockPrincipal(
            "BlockEntityOrdinary",
            proxyState,
            3,
            out _));

        TreeAttribute missingPrincipal = new();
        missingPrincipal.SetInt("cx", -1);
        missingPrincipal.SetInt("cy", -1);
        missingPrincipal.SetInt("cz", -1);
        Assert.IsFalse(VoxelScene.TryResolveMultiblockPrincipal(
            "BEMPMultiblock",
            missingPrincipal,
            3,
            out _));

        MeshData lowerCell = CreateQuadMesh(
            [
                new Vec3f(0.1f, 0.5f, 0.1f), new Vec3f(0.9f, 0.5f, 0.1f),
                new Vec3f(0.9f, 0.5f, 0.9f), new Vec3f(0.1f, 0.5f, 0.9f)
            ]);
        MeshData authoredUpperCell = CreateQuadMesh(
            [
                new Vec3f(0.1f, 1.5f, 0.1f), new Vec3f(0.9f, 1.5f, 0.1f),
                new Vec3f(0.9f, 1.5f, 0.9f), new Vec3f(0.1f, 1.5f, 0.9f)
            ]);
        MeshData expectedLocalUpperCell = VoxelScene.CloneInstanceMesh(authoredUpperCell);
        expectedLocalUpperCell.Translate(0, -1, 0);

        ulong projected = VoxelScene.RasterizeInstanceMeshesAtRelativeBlock(
            [lowerCell, authoredUpperCell],
            principal.X,
            principal.Y,
            principal.Z,
            principal.X,
            principal.Y + 1,
            principal.Z);
        ulong expected = VoxelScene.RasterizeOpaqueMeshMask(expectedLocalUpperCell);
        ulong fullCollision = VoxelScene.BuildInstanceOccupancyMask(
            [new Cuboidf(0, 0, 0, 1, 1, 1)],
            null,
            false);

        Assert.AreNotEqual(0UL, projected);
        Assert.AreEqual(expected, projected);
        Assert.AreEqual(ulong.MaxValue, fullCollision);
        Assert.AreNotEqual(fullCollision, projected);
        Assert.IsTrue(VoxelScene.TryResolveTessellatedInstanceOccupancy(
            true,
            2,
            true,
            true,
            0,
            projected,
            out ulong resolved));
        Assert.AreEqual(projected, resolved);
    }

    /// <summary>
    /// Verifies the voxel Scene Instance Mesh Normalization And Diagnostics Cover Every Valid Reason regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_InstanceMeshNormalizationAndDiagnosticsCoverEveryValidReason()
    {
        MeshData local = CreateQuadMesh(
            [
                new Vec3f(0.1f, 0, 0.5f), new Vec3f(0.4f, 0, 0.5f),
                new Vec3f(0.4f, 1, 0.5f), new Vec3f(0.1f, 1, 0.5f)
            ]);
        MeshData world = local.Clone().Translate(100, 50, -75);
        MeshData chunk = local.Clone().Translate(100 % GlobalConstants.ChunkSize,
            50 % GlobalConstants.ChunkSize,
            ((-75 % GlobalConstants.ChunkSize) + GlobalConstants.ChunkSize) % GlobalConstants.ChunkSize);
        ulong localMask = VoxelScene.RasterizeOpaqueMeshMask(local);
        Assert.AreEqual(localMask, VoxelScene.RasterizeInstanceMeshes([world], 100, 50, -75));
        Assert.AreEqual(localMask, VoxelScene.RasterizeInstanceMeshes([chunk], 100, 50, -75));

        MeshData cloned = VoxelScene.CloneInstanceMesh(local);
        Assert.AreNotSame(local.xyz, cloned.xyz);
        Assert.AreEqual(local.IndicesPerFace, cloned.IndicesPerFace);
        MeshData empty = new(false);
        MeshData normalizedEmpty = VoxelScene.NormalizeInstanceMeshToBlock(empty, 0, 0, 0);
        Assert.AreEqual(0, normalizedEmpty.VerticesCount);
        Assert.AreEqual(0, normalizedEmpty.xyz?.Length ?? 0);

        InstanceMeshDiagnostics emptyDiagnostic = VoxelScene.InspectInstanceMeshes([], 0, 0, 0, 0);
        Assert.AreEqual("empty-buffers", emptyDiagnostic.Reason);
        MeshData transparent = VoxelScene.CloneInstanceMesh(local);
        transparent.RenderPassesAndExtraBits = [(short)EnumChunkRenderPass.Transparent];
        transparent.RenderPassCount = 1;
        InstanceMeshDiagnostics transparentDiagnostic = VoxelScene.InspectInstanceMeshes(
            [transparent], 0, 0, 0, 0);
        Assert.AreEqual("transparent-only", transparentDiagnostic.Reason);
        Assert.AreEqual(2, transparentDiagnostic.TransparentTriangles);

        MeshData invalid = new(false)
        {
            xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Indices = [0, 1, 99],
            VerticesCount = 3,
            IndicesCount = 3,
            IndicesPerFace = 3
        };
        InstanceMeshDiagnostics invalidDiagnostic = VoxelScene.InspectInstanceMeshes(
            [invalid], 0, 0, 0, 0);
        Assert.AreEqual("invalid-indices", invalidDiagnostic.Reason);
        Assert.AreEqual(1, invalidDiagnostic.InvalidTriangles);

        InstanceMeshDiagnostics rasterized = VoxelScene.InspectInstanceMeshes(
            [local], 0, 0, 0, localMask);
        Assert.AreEqual("rasterized", rasterized.Reason);
        Assert.AreEqual(2, rasterized.OpaqueTriangles);
    }

    /// <summary>
    /// Verifies the voxel Scene Mesh Bounds Collector And Gpu Records Remain Lossless regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_MeshBoundsCollectorAndGpuRecordsRemainLossless()
    {
        MeshBounds empty = MeshBounds.Empty;
        Assert.IsTrue(empty.IsEmpty);
        Assert.IsFalse(empty.OverlapsUnitBlock);
        Assert.AreEqual("empty", empty.ToString());
        Assert.AreEqual(empty, empty.Include(new MeshData(false)));

        MeshData mesh = CreateQuadMesh(
            [
                new Vec3f(-0.2f, 0.1f, 0.3f), new Vec3f(0.8f, 0.1f, 0.3f),
                new Vec3f(0.8f, 0.9f, 0.3f), new Vec3f(-0.2f, 0.9f, 0.3f)
            ]);
        MeshBounds bounds = empty.Include(mesh);
        Assert.IsFalse(bounds.IsEmpty);
        Assert.IsTrue(bounds.OverlapsUnitBlock);
        Assert.AreEqual("[-0.2,0.1,0.3]-[0.8,0.9,0.3]", bounds.ToString());
        Assert.IsFalse(new MeshBounds(2, 2, 2, 3, 3, 3).OverlapsUnitBlock);

        InstanceTerrainMeshCollector collector = new();
        collector.AddMeshData(null!);
        collector.AddMeshData(mesh);
        float original = collector.Meshes[0].xyz[0];
        mesh.xyz[0] = 99;
        Assert.AreEqual(original, collector.Meshes[0].xyz[0]);
        collector.AddMeshData(CreateQuadMesh(
            [
                new Vec3f(0, 0, 0), new Vec3f(1, 0, 0),
                new Vec3f(1, 1, 0), new Vec3f(0, 1, 0)
            ]),
            IdentityMatrix());
        collector.AddMeshData(CreateQuadMesh(
            [
                new Vec3f(0, 0, 1), new Vec3f(1, 0, 1),
                new Vec3f(1, 1, 1), new Vec3f(0, 1, 1)
            ]),
            new ColorMapData());
        Assert.AreEqual(3, collector.Meshes.Count);

        byte[] caster = [1, 2, 3];
        CachedBlockOccupancy cached = new(3, true, 1, 2, 3, BlockGeometryKind.StaticComplex);
        CachedLightCaster cachedCaster = new(caster, new Vec3f(0.1f, 0.2f, 0.3f));
        StateBlockEntity blockEntity = new(false);
        InstanceMeshOccupancy instance = new(blockEntity, 7, 9, true, 2);
        InstanceMeshDiagnostics diagnostics = new(1, 2, 3, 4, 5, "raw", "local", "reason");
        Assert.AreEqual(3UL, cached.Mask);
        Assert.IsTrue(cached.HasDetailedMesh);
        Assert.AreEqual(1, cached.OpaqueTriangles);
        Assert.AreEqual(2, cached.TransparentTriangles);
        Assert.AreEqual(3, cached.AlphaTestedTriangles);
        Assert.AreEqual(BlockGeometryKind.StaticComplex, cached.GeometryKind);
        Assert.AreSame(caster, cachedCaster.Mask);
        Assert.AreEqual(0.1f, cachedCaster.EmissionOffset.X);
        Assert.AreSame(blockEntity, instance.BlockEntity);
        Assert.AreEqual(7, instance.BlockId);
        Assert.AreEqual(9UL, instance.Mask);
        Assert.IsTrue(instance.SkipsDefaultMesh);
        Assert.AreEqual(2, instance.CapturedMeshCount);
        Assert.AreEqual(1, diagnostics.VertexCount);
        Assert.AreEqual(2, diagnostics.IndexCount);
        Assert.AreEqual(3, diagnostics.OpaqueTriangles);
        Assert.AreEqual(4, diagnostics.TransparentTriangles);
        Assert.AreEqual(5, diagnostics.InvalidTriangles);
        Assert.AreEqual("raw", diagnostics.RawBounds);
        Assert.AreEqual("local", diagnostics.LocalBounds);
        Assert.AreEqual("reason", diagnostics.Reason);

        VoxelLight light = new(1, 2, 3, 0.4f, 0.5f, 0.6f, 10, "fixture", caster);
        Assert.AreEqual(1.0f, light.X);
        Assert.AreEqual(2.0f, light.Y);
        Assert.AreEqual(3.0f, light.Z);
        Assert.AreEqual(0.4f, light.Red);
        Assert.AreEqual(0.5f, light.Green);
        Assert.AreEqual(0.6f, light.Blue);
        Assert.AreEqual(10.0f, light.Intensity);
        Assert.AreEqual("fixture", light.Code);
        Assert.AreSame(caster, light.CasterMask);
        Assert.AreEqual(10.0, light.Score(new Vec3d(1, 2, 3)), 0.00001);
        Assert.IsTrue(light.Score(new Vec3d(100, 100, 100)) < 1.0);

        VoxelSceneSnapshot snapshot = new(
            [1], [2], 3, 4, 5, 6, 7, 8, [light], [9], [10], [11], [12], [13.0f], 14,
            [15], [16.0f], 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
            29, 30, 31, 32);
        VoxelSceneBlockUpdate blockUpdate = new(1, 2, 3, [4], [5], [6], [7]);
        VoxelFluidSurfaceUpdate fluidUpdate = new(8, 9, [10]);
        VoxelSunOccupancyUpdate sunUpdate = new(1, 2, 3, 4);
        VoxelRainSurfaceUpdate rainUpdate = new(1, 2, 3.0f);
        VoxelRadianceField radiance = new([1], [2]);
        TextureAlphaCandidate alphaCandidate = new(
            new TextureAtlasPosition(), new AssetLocation("game:fixture"));
        TextureAlphaData alphaData = new(1, 1, [255]);
        Assert.AreEqual(28, snapshot.Generation);
        Assert.AreEqual(29, snapshot.FluidSurfaceWidth);
        Assert.AreEqual(30, snapshot.FluidSurfaceDepth);
        Assert.AreEqual(31, snapshot.FluidSurfaceOriginX);
        Assert.AreEqual(32, snapshot.FluidSurfaceOriginZ);
        Assert.AreEqual((byte)1, snapshot.Voxels[0]);
        Assert.AreEqual((byte)2, snapshot.Occupancy[0]);
        Assert.AreEqual(3, snapshot.Width);
        Assert.AreEqual(4, snapshot.Height);
        Assert.AreEqual(5, snapshot.Depth);
        Assert.AreEqual(6, snapshot.OriginX);
        Assert.AreEqual(7, snapshot.OriginY);
        Assert.AreEqual(8, snapshot.OriginZ);
        Assert.AreSame(light.CasterMask, snapshot.Lights[0].CasterMask);
        Assert.AreEqual((byte)9, snapshot.Irradiance[0]);
        Assert.AreEqual((byte)10, snapshot.IrradianceDirection[0]);
        Assert.AreEqual((byte)11, snapshot.FluidSurface[0]);
        Assert.AreEqual((byte)12, snapshot.LiquidMetadata[0]);
        Assert.AreEqual(13.0f, snapshot.LiquidOpticalProfileLookup[0]);
        Assert.AreEqual(14, snapshot.LiquidOpticalProfileCount);
        Assert.AreEqual(15UL, snapshot.SunOccupancy[0]);
        Assert.AreEqual(16.0f, snapshot.RainSurface[0]);
        Assert.AreEqual(17, snapshot.SunOccupancyWidth);
        Assert.AreEqual(18, snapshot.SunOccupancyHeight);
        Assert.AreEqual(19, snapshot.SunOccupancyDepth);
        Assert.AreEqual(20, snapshot.SunOccupancyScale);
        Assert.AreEqual(21, snapshot.SunOriginX);
        Assert.AreEqual(22, snapshot.SunOriginY);
        Assert.AreEqual(23, snapshot.SunOriginZ);
        Assert.AreEqual(24, snapshot.RainSurfaceWidth);
        Assert.AreEqual(25, snapshot.RainSurfaceDepth);
        Assert.AreEqual(26, snapshot.FluidVoxelCount);
        Assert.AreEqual(27, snapshot.VisibleLiquidContainerCount);
        Assert.AreEqual(1, blockUpdate.LocalX);
        Assert.AreEqual(2, blockUpdate.LocalY);
        Assert.AreEqual(3, blockUpdate.LocalZ);
        Assert.AreEqual((byte)4, blockUpdate.Material[0]);
        Assert.AreEqual((byte)5, blockUpdate.Occupancy[0]);
        Assert.AreEqual((byte)6, blockUpdate.LiquidMetadata[0]);
        Assert.AreEqual(7, blockUpdate.FluidSurface[0]);
        Assert.AreEqual(8, fluidUpdate.LocalX);
        Assert.AreEqual(9, fluidUpdate.LocalZ);
        Assert.AreEqual((byte)10, fluidUpdate.FluidSurface[0]);
        Assert.AreEqual(1, sunUpdate.LocalX);
        Assert.AreEqual(2, sunUpdate.LocalY);
        Assert.AreEqual(3, sunUpdate.LocalZ);
        Assert.AreEqual(4UL, sunUpdate.Occupancy);
        Assert.AreEqual(1, rainUpdate.LocalX);
        Assert.AreEqual(2, rainUpdate.LocalZ);
        Assert.AreEqual(3.0f, rainUpdate.Height);
        Assert.AreEqual((byte)1, radiance.Irradiance[0]);
        Assert.AreEqual((byte)2, radiance.Direction[0]);
        Assert.AreEqual(0.0f, alphaCandidate.Position.x1);
        Assert.AreEqual("fixture", alphaCandidate.Source.Path);
        Assert.AreEqual((byte)255, alphaData.Alpha[0]);
    }

    /// <summary>
    /// Verifies the voxel Scene Radiance Field Rejects Wrong Volume And Handles Empty Scene regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void VoxelScene_RadianceFieldRejectsWrongVolumeAndHandlesEmptyScene()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            VoxelScene.BuildRadianceField([], [], 0, 0, 0, new Vec3f()));

        byte[] empty = new byte[VoxelScene.Width * VoxelScene.Height * VoxelScene.Depth * 4];
        VoxelRadianceField field = VoxelScene.BuildRadianceField(
            empty, [], 0, 0, 0, new Vec3f());
        Assert.AreEqual(VoxelScene.Width * VoxelScene.Height * VoxelScene.Depth * 3,
            field.Irradiance.Length);
        Assert.IsTrue(field.Irradiance.All(static value => value == 0));
        Assert.IsTrue(field.Direction.Where((_, index) => index % 3 == 0)
            .All(static value => value == 128));
        Assert.IsTrue(field.Direction.Where((_, index) => index % 3 == 1)
            .All(static value => value == 255));
        Assert.IsTrue(field.Direction.Where((_, index) => index % 3 == 2)
            .All(static value => value == 128));
        CollectionAssert.AreEqual(
            field.Irradiance,
            VoxelScene.BuildIrradianceVolume(empty, [], 0, 0, 0));
    }

    /// <summary>
    /// Creates liquid with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="profileKey">Coordinate component in the space defined by the tested API.</param>
    /// <param name="code">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The create Liquid result consumed by the caller&apos;s assertion.</returns>
    private static Item CreateLiquid(string profileKey, string code)
    {
        return new Item
        {
            Code = new AssetLocation(code),
            Attributes = Wrap(CreateOpticsContract(profileKey))
        };
    }

    /// <summary>
    /// Creates profile with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="key">Coordinate component in the space defined by the tested API.</param>
    /// <param name="opaque">The opaque input used to configure this deterministic test path.</param>
    /// <returns>The create Profile result consumed by the caller&apos;s assertion.</returns>
    private static LiquidOpticalProfile CreateProfile(string key, bool opaque)
    {
        return new LiquidOpticalProfile(
            key,
            1.333f,
            new LiquidRgb(0.01f, 0.02f, 0.03f),
            new LiquidRgb(0.04f, 0.05f, 0.06f),
            0.92f,
            new LiquidRgb(0.07f, 0.08f, 0.09f),
            0.1f,
            0.12f,
            0.13f,
            0.14f,
            0.15f,
            new LiquidSurfaceDynamics(
                0.16f, 0.17f, 1.8f, 1.9f, 0.20f, 0.21f, 1.22f,
                0.23f, 0.24f, 0.25f, 2.6f, 0.27f, 0.28f),
            opaque,
            new LiquidPhysicalProperties(
                "fixture-basis",
                293.15f,
                998.2f,
                0.001002f,
                0.07275f,
                0.02f,
                0.0f,
                0.0f));
    }

    /// <summary>
    /// Creates optics Contract with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="profileKey">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The create Optics Contract result consumed by the caller&apos;s assertion.</returns>
    private static JObject CreateOpticsContract(string profileKey)
    {
        JObject root = JObject.Parse(
            """
            {
              "vintageRtxOptics": {
                "profile": "fixture",
                "ior": 1.333,
                "absorptionRgb": [0.01, 0.02, 0.03],
                "scatteringRgb": [0.04, 0.05, 0.06],
                "transmission": 0.92,
                "emissionRgb": [0.07, 0.08, 0.09],
                "emissionIntensity": 0.10,
                "roughness": 0.12,
                "microNormalStrength": 0.13,
                "opticalViscosity": 0.14,
                "scatteringAnisotropy": 0.15,
                "surfaceDynamics": {
                  "windCoupling": 0.16,
                  "waveAmplitude": 0.17,
                  "waveLength": 1.8,
                  "waveSpeed": 1.9,
                  "damping": 0.20,
                  "impactResponse": 0.21,
                  "surfaceTension": 1.22,
                  "bubbleRate": 0.23,
                  "bubbleRadiusMin": 0.24,
                  "bubbleRadiusMax": 0.25,
                  "bubbleRiseDuration": 2.6,
                  "bubbleBurstStrength": 0.27,
                  "bubbleEmissionBoost": 0.28
                },
                "opaque": false
              },
              "vintageRtxPhysics": {
                "basis": "fixture-basis",
                "referenceTemperatureK": 293.15,
                "densityKgM3": 998.2,
                "dynamicViscosityPaS": 0.001002,
                "surfaceTensionNm": 0.07275,
                "resolvedWaveEnergyFraction": 0.02,
                "thermalTemperatureK": 0,
                "thermalEmissivity": 0
              }
            }
            """);
        Optics(root)["profile"] = profileKey;
        return root;
    }

    /// <summary>
    /// Creates container Contract with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Container Contract result consumed by the caller&apos;s assertion.</returns>
    private static JObject CreateContainerContract()
    {
        return JObject.Parse(
            """
            {
              "vintageRtxLiquidContainer": {
                "contentSlot": 0,
                "capacityLitres": 10.0,
                "surfaceMinimumY": 0.2,
                "surfaceMaximumY": 0.8,
                "alwaysOpen": true,
                "visibilityTreeBool": "",
                "visibleWhen": false
              }
            }
            """);
    }

    /// <summary>
    /// Executes the optics step used by the deterministic voxel Scene Coverage Tests fixture.
    /// </summary>
    /// <param name="root">Filesystem location constrained to the isolated test sandbox.</param>
    /// <returns>The optics result consumed by the caller&apos;s assertion.</returns>
    private static JObject Optics(JObject root)
    {
        return (JObject)root[LiquidOpticalRegistry.OpticalAttributeName]!;
    }

    /// <summary>
    /// Executes the dynamics step used by the deterministic voxel Scene Coverage Tests fixture.
    /// </summary>
    /// <param name="root">Filesystem location constrained to the isolated test sandbox.</param>
    /// <returns>The dynamics result consumed by the caller&apos;s assertion.</returns>
    private static JObject Dynamics(JObject root)
    {
        return (JObject)Optics(root)["surfaceDynamics"]!;
    }

    /// <summary>
    /// Asserts rejected and throws when the regression contract is violated.
    /// </summary>
    /// <param name="root">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="message">The message input used to configure this deterministic test path.</param>
    private static void AssertRejected(JObject root, string message)
    {
        Item item = new()
        {
            Code = new AssetLocation("game:invalid-fixture"),
            Attributes = Wrap(root)
        };
        Assert.IsFalse(LiquidOpticalRegistry.TryReadProfile(item, out _), message);
    }

    /// <summary>
    /// Creates container Block with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="contract">The contract input used to configure this deterministic test path.</param>
    /// <returns>The create Container Block result consumed by the caller&apos;s assertion.</returns>
    private static Block CreateContainerBlock(JObject contract)
    {
        return new Block
        {
            Code = new AssetLocation("game:fixture-container"),
            Attributes = Wrap(contract)
        };
    }

    /// <summary>
    /// Executes the wrap step used by the deterministic voxel Scene Coverage Tests fixture.
    /// </summary>
    /// <param name="token">The token input used to configure this deterministic test path.</param>
    /// <returns>The wrap result consumed by the caller&apos;s assertion.</returns>
    private static JsonObject Wrap(JToken token)
    {
        return new JsonObject(token);
    }

    /// <summary>
    /// Asserts texel and throws when the regression contract is violated.
    /// </summary>
    /// <param name="lookup">The lookup input used to configure this deterministic test path.</param>
    /// <param name="profileId">The profile Id input used to configure this deterministic test path.</param>
    /// <param name="texel">The texel input used to configure this deterministic test path.</param>
    /// <param name="red">The red input used to configure this deterministic test path.</param>
    /// <param name="green">The green input used to configure this deterministic test path.</param>
    /// <param name="blue">The blue input used to configure this deterministic test path.</param>
    /// <param name="alpha">The alpha input used to configure this deterministic test path.</param>
    private static void AssertTexel(
        float[] lookup,
        int profileId,
        int texel,
        float red,
        float green,
        float blue,
        float alpha)
    {
        int offset = (profileId * LiquidOpticalRegistry.LookupWidth + texel)
            * LiquidOpticalRegistry.LookupChannels;
        CollectionAssert.AreEqual(
            new[] { red, green, blue, alpha },
            lookup[offset..(offset + 4)]);
    }

    /// <summary>
    /// Creates quad Mesh with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="quads">The quads input used to configure this deterministic test path.</param>
    /// <returns>The create Quad Mesh result consumed by the caller&apos;s assertion.</returns>
    private static MeshData CreateQuadMesh(params Vec3f[][] quads)
    {
        float[] xyz = new float[quads.Length * 12];
        int[] indices = new int[quads.Length * 6];
        for (int face = 0; face < quads.Length; face++)
        {
            for (int vertex = 0; vertex < 4; vertex++)
            {
                Vec3f value = quads[face][vertex];
                int xyzOffset = (face * 4 + vertex) * 3;
                xyz[xyzOffset] = value.X;
                xyz[xyzOffset + 1] = value.Y;
                xyz[xyzOffset + 2] = value.Z;
            }

            int vertexOffset = face * 4;
            int indexOffset = face * 6;
            indices[indexOffset] = vertexOffset;
            indices[indexOffset + 1] = vertexOffset + 1;
            indices[indexOffset + 2] = vertexOffset + 2;
            indices[indexOffset + 3] = vertexOffset;
            indices[indexOffset + 4] = vertexOffset + 2;
            indices[indexOffset + 5] = vertexOffset + 3;
        }

        return new MeshData(false)
        {
            xyz = xyz,
            Indices = indices,
            VerticesCount = quads.Length * 4,
            IndicesCount = quads.Length * 6,
            IndicesPerFace = 6
        };
    }

    /// <summary>
    /// Executes the identity Matrix step used by the deterministic voxel Scene Coverage Tests fixture.
    /// </summary>
    /// <returns>The identity Matrix result consumed by the caller&apos;s assertion.</returns>
    private static float[] IdentityMatrix()
    {
        return
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];
    }

    /// <summary>
    /// Returns field from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The get Field result consumed by the caller&apos;s assertion.</returns>
    private static T GetField<T>(object instance, string fieldName)
    {
        FieldInfo? field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, fieldName);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>
    /// Sets field on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    private static void SetField<T>(object instance, string fieldName, T value)
    {
        FieldInfo? field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, fieldName);
        field.SetValue(instance, value);
    }

    /// <summary>
    /// Supports state Block Entity within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class StateBlockEntity(bool state) : BlockEntity
    {
        /// <summary>
        /// Executes the to Tree Attributes step used by the deterministic state Block Entity fixture.
        /// </summary>
        /// <param name="tree">The tree input used to configure this deterministic test path.</param>
        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            tree.SetBool("sealed", state);
        }
    }

    /// <summary>
    /// Provides a deterministic test double for recording Logger Proxy and records calls without invoking external services.
    /// </summary>
    public class RecordingLoggerProxy : DispatchProxy
    {
        /// <summary>
        /// Gets the calls value exposed to the deterministic fixture.
        /// </summary>
        public List<string> Calls { get; } = [];

        /// <summary>
        /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
        /// </summary>
        /// <param name="targetMethod">The target Method input used to configure this deterministic test path.</param>
        /// <param name="args">The args input used to configure this deterministic test path.</param>
        /// <returns>The invoke result consumed by the caller&apos;s assertion.</returns>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            Assert.IsNotNull(targetMethod);
            Calls.Add(targetMethod.Name);
            if (targetMethod.ReturnType == typeof(void))
            {
                return null;
            }

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
