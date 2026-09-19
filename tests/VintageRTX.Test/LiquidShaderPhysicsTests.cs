using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>
/// Verifies that the display shader keeps liquid transport physically ordered and visibly distinct
/// without requiring a live OpenGL context.
/// </summary>
[TestClass]
public sealed class LiquidShaderPhysicsTests
{
    /// <summary>
    /// Verifies that exact dielectric Fresnel uses the profile IOR and strengthens reflection toward
    /// grazing incidence.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void FresnelUsesProfileIorAndAngularIncidence()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "float exactDielectricFresnel(");
        StringAssert.Contains(shader, "dot(-cameraRayWorld, liquidSurfaceNormal)");
        StringAssert.Contains(shader, "airIor / liquidProfile.ior");
        StringAssert.Contains(shader, "profileDrivenLiquidNormal(");

        double waterNormal = ExactDielectricFresnel(1.0, 1.000293, 1.333);
        double waterGrazing = ExactDielectricFresnel(0.10, 1.000293, 1.333);
        double honeyNormal = ExactDielectricFresnel(1.0, 1.000293, 1.49);

        Assert.AreEqual(0.02033, waterNormal, 0.0002);
        Assert.IsTrue(waterGrazing > waterNormal * 20.0);
        Assert.IsTrue(honeyNormal > waterNormal);
    }

    /// <summary>
    /// Verifies depth-dependent Beer-Lambert attenuation, channel-selective water/honey colour, and
    /// opaque lava extinction.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void BeerLambertUsesDepthAndSeparatesLiquidProfiles()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "vec3 liquidExtinctionCoefficient(");
        StringAssert.Contains(shader, "Absorption and out-scattering are already Napierian coefficients in m^-1");
        StringAssert.Contains(shader, "* reflection.liquidMetresPerWorldBlock");
        StringAssert.Contains(shader, "integratedLiquidEmission");
        Assert.IsFalse(shader.Contains("float bulkExtinction = -log(bulkTransmission)", StringComparison.Ordinal));

        double[] shallowWater = BeerLambert(
            [0.3594, 0.0654, 0.00922],
            [0.0007, 0.0015, 0.0035],
            0.25);
        double[] deepWater = BeerLambert(
            [0.3594, 0.0654, 0.00922],
            [0.0007, 0.0015, 0.0035],
            8.0);
        double[] honey = BeerLambert(
            [0.15, 0.55, 2.20],
            [0.040, 0.035, 0.025],
            2.0);
        double[] lava = BeerLambert(
            [8.0, 12.0, 18.0],
            [3.0, 2.0, 1.0],
            1.5);

        Assert.IsTrue(deepWater.Zip(shallowWater).All(pair => pair.First < pair.Second));
        Assert.IsTrue(deepWater[0] < deepWater[1] && deepWater[1] < deepWater[2]);
        Assert.IsTrue(honey[2] < honey[1] && honey[1] < honey[0]);
        Assert.IsTrue(lava.All(static channel => channel < 0.000003));
    }

    /// <summary>
    /// Verifies all liquid-plane tests decode the column Y sentinel and exact Vintage Story
    /// LiquidLevel channel through one shared engine-aligned function.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void FluidColumnHeightMatchesVintageStoryLiquidLevelConvention()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "float decodeFluidSurfaceWorldY(vec4 columnData)");
        StringAssert.Contains(shader, "float topLocalY = encodedTopCell - 1.0;");
        StringAssert.Contains(shader, "floor(columnData.b * 255.0 + 0.5)");
        StringAssert.Contains(shader, "return voxelOrigin.y + topLocalY + liquidLevel / 8.0;");
        Assert.AreEqual(
            6,
            shader.Split("decodeFluidSurfaceWorldY(", StringSplitOptions.None).Length - 1,
            "The helper definition plus source-mask, initial-plane, liquid-face, adjacent-shore, and reprojected-plane calls are required.");
        Assert.IsFalse(shader.Contains(
            "voxelOrigin.y + floor(encodedFluidSurface * 255.0 + 0.5)",
            StringComparison.Ordinal));

        Assert.AreEqual(42.0, DecodeSurfaceHeight(42, 0), 0.0);
        Assert.AreEqual(42.375, DecodeSurfaceHeight(42, 3), 0.0);
        Assert.AreEqual(42.875, DecodeSurfaceHeight(42, 7), 0.0);
    }

    /// <summary>
    /// Verifies all water-evidence and planar lookups use the independent 128-block surface ABI,
    /// while procedural waves retain the 0.5 m dynamic-grid Nyquist limit beyond its 64-block area.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void StaticLiquidReachIsIndependentFromVoxelAndDynamicFootprints()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "uniform vec2 fluidSurfaceOrigin;");
        StringAssert.Contains(shader, "uniform vec2 fluidSurfaceSize;");
        StringAssert.Contains(shader, "worldPosition.xz - fluidSurfaceOrigin");
        StringAssert.Contains(shader, "/ fluidSurfaceSize");
        StringAssert.Contains(shader, "planarWorldPosition.xz\n        - fluidSurfaceOrigin");
        StringAssert.Contains(shader, "refractedWorldTarget.xz\n            - fluidSurfaceOrigin");
        StringAssert.Contains(shader, "max(dynamicLiquidOriginCell.z, 0.5)");
        Assert.IsFalse(shader.Contains(
            "/ voxelSize.xz)",
            StringComparison.Ordinal),
            "No static liquid column may retain the old 64-block material footprint.");
    }

    /// <summary>
    /// Verifies that a fluid column cannot paint water over crossed plants, fences, or another
    /// partial mesh that is closer to the camera than the reconstructed liquid interface.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void TransparentWaterFallbackRespectsPartialGeometryDepth()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "float liquidInterfaceDepthVisibility = step(");
        StringAssert.Contains(shader, "surfaceDistance - 0.018");
        StringAssert.Contains(shader, "opaqueDistance);");
        StringAssert.Contains(shader, "uniform sampler2D gOpaquePosition;");
        StringAssert.Contains(shader, "opaqueInterfaceDepthVisibility");
        StringAssert.Contains(shader, "float(opaquePositionEnabled) * opaquePositionValid");
        StringAssert.Contains(shader, "uniform sampler2D gOpaqueDepth;");
        StringAssert.Contains(shader, "vec3 interfaceNearWorldPosition");
        StringAssert.Contains(shader, "float opaqueDepthVisibility = step(interfaceNearDepth, opaqueSceneDepth);");
        StringAssert.Contains(shader, "float(opaqueDepthEnabled)");
        StringAssert.Contains(shader, "vec3 interfaceGeometryProbeLocal = planarWorldPosition");
        StringAssert.Contains(shader, "+ vec3(0.0, 0.06, 0.0)");
        StringAssert.Contains(shader, "fineOccupancyCell / fineOccupancySize");
        StringAssert.Contains(shader, "interfaceFineOccupancyVisibility = 1.0");
        StringAssert.Contains(shader, "liquidInterfaceDepthVisibility *= interfaceFineOccupancyVisibility;");
        StringAssert.Contains(shader, "float exactLiquidInterfaceDepthVisibility = liquidInterfaceDepthVisibility;");
        StringAssert.Contains(shader, "float exactOpaquePartialSilhouette = opaquePositionValid");
        StringAssert.Contains(shader, "* partialGeometryMetadataAtWorldPosition(exactOpaqueWorldPosition);");
        StringAssert.Contains(shader, "float layeredPartialGeometry = max(");
        StringAssert.Contains(shader, "planarWorldPosition - vec3(0.0, 0.06, 0.0)");
        StringAssert.Contains(shader, "float layeredPartialWaterEvidence = fluidColumnSupport");
        StringAssert.Contains(shader, "* exactLiquidInterfaceDepthVisibility;");
        StringAssert.Contains(shader, "horizontalReflector = max(\n        horizontalReflector,\n        max(layeredPartialWaterEvidence, liquidFaceLayeredEvidence));");
        StringAssert.Contains(shader, "float visibleFluidColumnEvidence = fluidColumnEvidence");
        Assert.AreEqual(
            2,
            shader.Split("max(visibleFluidColumnEvidence, supportedTransparentWater)", StringSplitOptions.None).Length - 1,
            "Both water-candidate and final water-evidence paths must respect interface visibility.");
        Assert.IsFalse(shader.Contains(
            "max(fluidColumnEvidence, supportedTransparentWater)",
            StringComparison.Ordinal));
        StringAssert.Contains(shader, "uniform sampler2D gLiquidDepth;");
        StringAssert.Contains(shader, "uniform mat4 inverseProjection;");
        StringAssert.Contains(shader, "vec3 liquidViewGeometricNormalUnnormalized = cross(");
        StringAssert.Contains(shader, "mat3(inverseViewMatrix) * liquidViewGeometricNormal");
        StringAssert.Contains(shader, "float liquidDepthSurfaceUpness = abs(liquidWorldGeometricNormal.y);");
        StringAssert.Contains(shader, "liquidDepthHorizontalSupport = smoothstep(");
        StringAssert.Contains(shader, "if (liquidDepthEnabled != 0)");
        StringAssert.Contains(shader, "liquidDepthGeometryValid)");
        StringAssert.Contains(shader, "float partialGeometryMetadataAtWorldPosition(");
        StringAssert.Contains(shader, "(encodedMaterialClass & 4) != 0");
        StringAssert.Contains(shader, "float solidGeometryMetadataAtWorldPosition(");
        StringAssert.Contains(shader, "return step(0.45, material.a);");
        StringAssert.Contains(shader, "liquidDepthPartialGeometrySupport = max(");
        StringAssert.Contains(shader, "float liquidDepthShoreGeometrySupport = 0.0;");
        StringAssert.Contains(shader, "float positiveFluidShoreContact = positiveFaceHasFluid");
        StringAssert.Contains(shader, "liquidDepthShoreGeometrySupport = max(");
        StringAssert.Contains(shader, "result.liquidVerticalFaceEvidence = liquidDepthGeometryValid");
        StringAssert.Contains(shader, "result.liquidShoreFaceEvidence = liquidDepthShoreGeometrySupport;");
        StringAssert.Contains(shader, "float sharedPartialLayerOccluderEvidence = fluidColumnSupport");
        StringAssert.Contains(shader, "* (1.0 - exactLiquidInterfaceDepthVisibility)");
        StringAssert.Contains(shader, "* step(0.025, lateLiquidCarrierDifference)");
        StringAssert.Contains(shader, "result.liquidShorePartialGeometryEvidence =\n        exactOpaquePartialSilhouette;");
        StringAssert.Contains(shader, "float screenSpaceShoreDecalEvidence = 0.0;");
        StringAssert.Contains(shader, "float visiblePartialReceiver = partialGeometryMetadataAtWorldPosition(");
        StringAssert.Contains(shader, "vec3 shoreReceiverWorldPosition = opaquePositionValid > 0.5");
        StringAssert.Contains(shader, "- shoreReceiverWorldPosition.y;");
        StringAssert.Contains(shader, "float exactOpaqueReceiverEvidence = max(");
        StringAssert.Contains(shader, "float opaqueReceiverEvidence = visiblePartialReceiver");
        StringAssert.Contains(shader, "const float liquidDepthSearchPixels[5]");
        StringAssert.Contains(shader, "liquidDepthGeometryValid < 0.001");
        StringAssert.Contains(shader, "result.liquidShoreSurfaceUv = clamp(");
        StringAssert.Contains(shader, "vec4 nearbyLiquidViewHomogeneous = inverseProjection");
        StringAssert.Contains(shader, "float nearbyLiquidWorldContact = nearbyLiquidDepthSupport");
        StringAssert.Contains(shader, "length(\n                    nearbyLiquidWorldPosition\n                        - shoreReceiverWorldPosition)");
        StringAssert.Contains(shader, "result.liquidShoreSurfaceConfidence = 1.0;");
        StringAssert.Contains(shader, "float shoreFluidSupport = max(");
        StringAssert.Contains(shader, "surfaceColor - opaqueCarrierColor");
        StringAssert.Contains(shader, "const vec2 adjacentColumnOffsets[4]");
        StringAssert.Contains(shader, "step(-0.12, receiverToSurface)");
        StringAssert.Contains(shader, "* step(0.025, lateLiquidCarrierDifference)");
        StringAssert.Contains(shader, "vec3 verticalFaceDiagnostic = vec3(");
        StringAssert.Contains(shader, "reflection.liquidShoreFaceEvidence");
        StringAssert.Contains(shader, "int positiveFaceLiquidFlags = int(floor(");
        StringAssert.Contains(shader, "(positiveFaceLiquidFlags & 1) != 0");
        StringAssert.Contains(shader, "vec4 liquidFaceFluidColumn = liquidFaceInsideFluidSurface");
        StringAssert.Contains(shader, "float liquidFaceLayeredEvidence = 0.0;");
        StringAssert.Contains(shader, "planarFluidColumnData = liquidFaceFluidColumn;");
        StringAssert.Contains(shader, "planarFluidSurfaceWorldY = liquidFaceSurfaceWorldY;");
        StringAssert.Contains(shader, "max(layeredPartialWaterEvidence, liquidFaceLayeredEvidence)");
        StringAssert.Contains(shader, "result.liquidPartialGeometryFace = max(\n            liquidDepthPartialGeometrySupport,\n            liquidDepthShoreGeometrySupport)");
        // Only the actual per-pixel liquid interface may receive the resolve.
        // The removed shoreline colour reconstruction copied neighbours over
        // partial silhouettes; its old implementation is not an acceptance oracle.
        StringAssert.Contains(shader, "float strictSupport = aboveWaterVisibility(");
        StringAssert.Contains(shader, "readGBufferTexel(gOpaqueDepth, uv).r");
        StringAssert.Contains(shader, "readGBufferTexel(gLiquidDepth, uv).r < 0.99999");
        StringAssert.Contains(shader, "waterEvidence *= strictSupport;");
        StringAssert.Contains(shader, "horizontalReflector *= strictSupport;");
        Assert.IsFalse(shader.Contains("float partialLiquidRecovery = clamp(", StringComparison.Ordinal));
        // A legacy body is still present but constant-folded away. Require its
        // gate to stay zero and forbid a later assignment that could revive it.
        StringAssert.Contains(shader, "float partialLiquidRecovery = 0.0;");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(
            shader, @"\bpartialLiquidRecovery\s*=").Count);
        StringAssert.Contains(shader, "* liquidInterfaceDepthVisibility;");

        Assert.AreEqual(0.0, InterfaceVisibility(8.0, 7.90), 0.0, "A nearer crossed mesh must occlude water.");
        Assert.AreEqual(1.0, InterfaceVisibility(8.0, 7.99), 0.0, "Equal-depth water writes need tolerance.");
        Assert.AreEqual(1.0, InterfaceVisibility(8.0, 11.0), 0.0, "Submerged receivers remain behind water.");
    }

    /// <summary>
    /// Verifies that every surface profile controls animated gravity/capillary waves and the
    /// reflection filter rather than inheriting the roughness of the submerged block.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SurfaceDynamicsAndRoughnessComeFromTheResolvedProfile()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "0.00512\n        * windSpeedMetresPerSecond");
        StringAssert.Contains(shader, "const int liquidWaveModeCount = 16");
        StringAssert.Contains(shader, "const vec4 liquidWaveModes[liquidWaveModeCount]");
        StringAssert.Contains(shader, "if (modeIndex >= liquidWaveModeLimit)");
        StringAssert.Contains(shader, "float packetPhaseModulation = 0.92 * sin(packetCarrier)");
        StringAssert.Contains(shader, "float packetPhaseDerivative = 0.92 * cos(packetCarrier)");
        StringAssert.Contains(shader, "/ max(packetScaleMetres * modeWaveNumber, 0.001)");
        StringAssert.Contains(shader, "profile.microNormal * profile.microNormal");
        StringAssert.Contains(shader, "1.0 - profile.resolvedWaveEnergyFraction");
        StringAssert.Contains(shader, "sqrt(2.0 * mode.z)");
        StringAssert.Contains(shader, "profile.surfaceTensionNm");
        StringAssert.Contains(shader, "modeWaveNumber * modeWaveNumber * modeWaveNumber");
        StringAssert.Contains(shader, "profile.dynamicViscosityPaS");
        StringAssert.Contains(shader, "profile.densityKgM3");
        StringAssert.Contains(shader, "rendererTimeSeconds");
        StringAssert.Contains(shader, "liquidWindVector");
        StringAssert.Contains(shader, "liquidWindMetresPerSecondPerEngineUnit");
        StringAssert.Contains(shader, "simulationCellMetres * 1.84");
        StringAssert.Contains(shader, "authoredWavelengthMetres * 0.24");
        StringAssert.Contains(shader, "float modePeriodSeconds");
        StringAssert.Contains(shader, "animationTime * angularFrequency");
        StringAssert.Contains(shader, "float phaseFootprint = fwidth(");
        StringAssert.Contains(shader, "phasePositionMetres + phaseWarpMetres");
        StringAssert.Contains(shader, "phaseWarpDerivativeX");
        StringAssert.Contains(shader, "phaseWarpDerivativeZ");
        StringAssert.Contains(shader, "dot(phaseWarpDerivativeX, modeDirection)");
        StringAssert.Contains(shader, "profile.bubbleRate * 24.0");
        StringAssert.Contains(shader, "profile.bubbleBurstStrength * 0.035");
        StringAssert.Contains(shader, "bubbleRing * profile.bubbleEmissionBoost");
        StringAssert.Contains(shader, "hasVisibleContainedSurface");
        StringAssert.Contains(shader, "containedSurfaceEvidence");
        StringAssert.Contains(shader, "containedMetadata.b");
        StringAssert.Contains(shader, "float liquidInterfaceRoughness");
        StringAssert.Contains(shader, "float maximumRipplePixels = mix(");
        StringAssert.Contains(shader, "10.0,\n            1.0 - liquidInterfaceRoughness");
        StringAssert.Contains(shader, "mix(0.96, 0.72, liquidInterfaceRoughness)");
        StringAssert.Contains(shader, "vec3 waveReflectedFarViewPosition");
        StringAssert.Contains(shader, "vec3 structuralReflectionDirection = normalize(reflect(");
        StringAssert.Contains(shader, "waveDirectionalUv - directionalUv");
        StringAssert.Contains(shader, "resolvedSurfaceSlope");
        StringAssert.Contains(shader, "out vec2 structuralSurfaceSlope");
        StringAssert.Contains(shader, "structuralSurfaceSlope = resolvedSurfaceSlope;");
        StringAssert.Contains(shader, "normalizedResolvedHeight");
        StringAssert.Contains(shader, "normalizedPhaseGradient += resolvedSurfaceSlope");
        StringAssert.Contains(shader, "profile.waveAmplitude * profile.metresPerWorldBlock");
        StringAssert.Contains(shader, "float phaseFootprint = fwidth(phase)");
        StringAssert.Contains(shader, "vec2 directionalUv = uv;");
        StringAssert.Contains(shader, "vec2 waveDirectionalUv = uv;");
        Assert.IsFalse(shader.Contains("planarWorldPosition.x * 1.37", StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains(
            "liquidSurfaceNormal.xz\n            * inverseFrameSize",
            StringComparison.Ordinal));

        Assert.IsFalse(shader.Contains(
            "float(temporalFrameIndex) * (1.0 / 60.0)",
            StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains("waveNumber * 0.73", StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains("capillaryWaveNumber * 1.91", StringComparison.Ordinal));

        double waterMobility = ViscousPersistence(0.1, 998.2, 0.001002);
        double honeyMobility = ViscousPersistence(0.1, 1496.0, 7.85);
        Assert.IsTrue(waterMobility > honeyMobility * 20.0);

        double[] directionalWeights =
        [
            0.105, 0.095, 0.095, 0.085, 0.085, 0.070, 0.070, 0.065,
            0.060, 0.055, 0.050, 0.045, 0.040, 0.035, 0.025, 0.020
        ];
        double normalizedMeanSquareSlope = 0.5 * directionalWeights
            .Sum(weight => Math.Pow(Math.Sqrt(2.0 * weight), 2.0));
        Assert.AreEqual(1.0, normalizedMeanSquareSlope, 0.000001);
    }

    /// <summary>
    /// Verifies the stable water-debug channel assignment used to compare temporal surface motion
    /// without sacrificing depth and transmittance diagnostics.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void WaterDebugViewEncodesDynamicsDepthAndTransmittance()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "Liquid diagnostic ABI: R=surface presence modulated by animated");
        StringAssert.Contains(shader, "mix(0.55, 1.0, reflection.liquidSurfaceDynamics)");
        StringAssert.Contains(shader, "1.0 - exp(-reflection.liquidPathLength * 0.22)");
        StringAssert.Contains(shader, "reflection.waterEvidence * diagnosticMeanTransmittance");
        StringAssert.Contains(shader, "reflection.liquidBubbleEmission");
    }

    /// <summary>Verifies raw reflection-carrier and CPU-to-GPU surface diagnostics remain observable.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void ReflectionSourceAndSurfaceFieldHaveLosslessDebugViews()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "if (debugView == 14)");
        StringAssert.Contains(shader, "sampleReflectionSource(uv)");
        StringAssert.Contains(shader, "dedicated asset-backed isolation pass");
        StringAssert.Contains(shader, "if (debugView == 15)");
        StringAssert.Contains(shader, "sampleDynamicLiquidSurface(");
        StringAssert.Contains(
            shader,
            "coherent world reflection behind a detected first-person overlay");
        Assert.IsFalse(shader.Contains(
            "if (primaryFirstPersonOverlay > 0.001)\n        {\n            outColor = vec4(0.0",
            StringComparison.Ordinal));
        StringAssert.Contains(shader, "reflection.planarWorldPosition");
        StringAssert.Contains(shader, "diagnosticFieldHeightRangeMetres = 0.05");
        StringAssert.Contains(shader, "length(dynamicState.yz)");
    }

    /// <summary>Guards the raw entity carrier used to diagnose finite reflected geometry.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void EntityMirrorHasLosslessDebugView()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "if (debugView == 16)");
        StringAssert.Contains(shader, "texture(entityMirrorColor, uv)");
        StringAssert.Contains(shader, "entityMirror.rgb * entityMirror.a");
    }

    /// <summary>
    /// Verifies refraction and sky radiance use the resolved wave normal while structural planar
    /// reflection placement uses the stable liquid plane and a bounded, validated scene lookup.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void RefractionAndStructuredReflectionSeparateShadingAndPlanarNormals()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "refract(\n            cameraRayWorld,\n            liquidSurfaceNormal");
        StringAssert.Contains(shader, "normalize(reflect(\n            cameraRayWorld,\n            liquidSurfaceNormal))");
        StringAssert.Contains(shader, "result.liquidTransmissionUv");
        StringAssert.Contains(shader, "sampleReflectionSource(\n            reflection.liquidTransmissionUv)");
        StringAssert.Contains(shader, "float refractedSourceAlignment");
        StringAssert.Contains(shader, "refractedSourceLateralError");
        StringAssert.Contains(shader, "* refractedSourceBelowInterface");
        StringAssert.Contains(shader, "vec4 maskedPlanarFallbackSample(");
        StringAssert.Contains(shader, "vec4 filteredMaskedPlanarFallbackSample(");
        StringAssert.Contains(shader, "filteredReflection = filteredMaskedPlanarFallbackSample(");
        StringAssert.Contains(shader, "float filterRadiusPixels = mix(");
        StringAssert.Contains(shader, "uniform sampler2D reflectionSourceColor;");
        StringAssert.Contains(shader, "uniform sampler2D entityMirrorColor;");
        StringAssert.Contains(shader, "uniform int entityMirrorEnabled;");
        StringAssert.Contains(shader, "texture(entityMirrorColor, entityMirrorUv)");
        StringAssert.Contains(shader, "if (entityMirrorSupport <= 0.001)");
        StringAssert.Contains(shader, "sampleReflectionSource(fallbackUv)");
        StringAssert.Contains(shader, "readGBufferTexel(gOpaquePosition, fallbackUv).xyz");
        StringAssert.Contains(shader, "alpha-tested\n    // foliage and OIT silhouettes");
        StringAssert.Contains(shader, "float fallbackSupport = 1.0 - fallbackRejectedGeometry;");
        StringAssert.Contains(shader, "float filteredSceneSupport = clamp(filteredReflection.a");
        StringAssert.Contains(shader, "vec3 planarReflectionDirection = normalize(reflect(\n            cameraRayWorld,\n            vec3(0.0, 1.0, 0.0)));");
        StringAssert.Contains(shader, "reflectedFarWorldPosition = planarWorldPosition\n            + planarReflectionDirection * 128.0;");
        StringAssert.Contains(shader, "waveReflectedFarWorldPosition = planarWorldPosition\n            + structuralReflectionDirection * 128.0;");
        StringAssert.Contains(shader, "waveReflectedFarWorldPosition - floatingWorldOrigin");
        StringAssert.Contains(shader, "uniform mat4 viewMatrix;");
        StringAssert.Contains(shader, "uniform vec3 floatingWorldOrigin;");
        StringAssert.Contains(shader, "skyEnvironmentRadiance(environmentDirection)");
        StringAssert.Contains(shader, "* directionalInsideFrame;");
        StringAssert.Contains(shader, "screenSceneConfidence");
        StringAssert.Contains(shader, "refineDynamicLiquidSurfaceIntersection(");
        StringAssert.Contains(shader, "gridPosition / dynamicLiquidGridSize");
        StringAssert.Contains(shader, "firstPersonOverlayEvidence(");
        StringAssert.Contains(shader, "length(viewPosition - vec3(1.0))");
        StringAssert.Contains(shader, "deferredFirstPersonOverlayEvidence(uv, position)");
        StringAssert.Contains(shader, "uniform sampler2D gDirectPosition;");
        StringAssert.Contains(shader, "screenBelowReflectingSurfaceEvidence(");
        Assert.IsFalse(shader.Contains("tracePlanarLiquidScreenSample", StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains("candidateWorldPosition.y = 2.0 * reflectingSurfaceWorldY", StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains("reprojectionErrorPixels", StringComparison.Ordinal));
        StringAssert.Contains(shader, "float surfaceTemporalBlend = temporalBlend");
        StringAssert.Contains(shader, "primaryFirstPersonOverlay");
        Assert.IsFalse(shader.Contains("float blurPhase", StringComparison.Ordinal));

        double incidentSine = Math.Sin(50.0 * Math.PI / 180.0);
        double waterTransmittedSine = incidentSine * 1.000293 / 1.333;
        double honeyTransmittedSine = incidentSine * 1.000293 / 1.49;
        Assert.IsTrue(waterTransmittedSine < incidentSine);
        Assert.IsTrue(honeyTransmittedSine < waterTransmittedSine);
    }

    /// <summary>
    /// Verifies that generic dropped-item/projectile energy reaches a dispersive sub-grid height
    /// packet rather than a screen-only ripple and that SI material properties control propagation.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SubgridImpactPacketUsesGravityCapillaryDispersionAndGeometricHeight()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "const int MAX_SUBGRID_IMPACT_PACKETS = 4;");
        StringAssert.Contains(
            shader,
            "uniform vec4 liquidImpactEnergyLedger[MAX_SUBGRID_IMPACT_PACKETS];");
        StringAssert.Contains(shader, "uniform vec4 liquidImpactSplash[MAX_SUBGRID_IMPACT_PACKETS];");
        StringAssert.Contains(shader, "void accumulateSubgridImpactPacket(");
        StringAssert.Contains(shader, "void applySubgridImpactPackets(");
        StringAssert.Contains(shader, "surfaceEnergyJoules");
        StringAssert.Contains(shader, "resolvedEnergyJoules");
        StringAssert.Contains(shader, "subgridEnergyJoules");
        StringAssert.Contains(shader, "renderedPacketEnergyJoules");
        StringAssert.Contains(
            shader,
            "renderedPacketEnergyJoules + localSplashEnergyJoules");
        StringAssert.Contains(shader, "ledgerToleranceJoules");
        StringAssert.Contains(shader, "vec2 windMetresPerSecond = liquidWindVector");
        StringAssert.Contains(shader, "vec2 exactImpactOrigin = originAgeAmplitude.xy");
        StringAssert.Contains(shader, "splashRadialWorldBlocks = localWorldPosition - exactImpactOrigin");
        Assert.IsFalse(shader.Contains(
            "impactVelocityMetresPerSecond *",
            StringComparison.Ordinal));
        StringAssert.Contains(shader, "float coupledPhaseWarp = baseHeightWorldBlocks");
        StringAssert.Contains(shader, "vec2 baseSlope = -baseHorizontalNormal");
        StringAssert.Contains(shader, "gravityMetresPerSecondSquared = 9.80665");
        StringAssert.Contains(shader, "surfaceTensionNewtonsPerMetre");
        StringAssert.Contains(shader, "groupVelocityMetresPerSecond");
        StringAssert.Contains(shader, "angularFrequencySecondDerivative");
        StringAssert.Contains(shader, "dynamicViscosityPascalSeconds");
        StringAssert.Contains(shader, "2.0 * renderedPacketEnergyJoules");
        StringAssert.Contains(shader, "accumulatedHeightWorldBlocks += waveHeightWorldBlocks");
        StringAssert.Contains(shader, "accumulatedSlope += heightGradientWorldBlocksPerMetre");
        StringAssert.Contains(shader, "dynamicState.x += accumulatedHeightWorldBlocks");
        StringAssert.Contains(shader, "dynamicState.yz = combinedNormal.xz");
        StringAssert.Contains(shader, "float splashHeight = -localAmplitude * splashShape");
        StringAssert.Contains(shader, "sqrt(max(1.0 - splashReleaseProgress, 0.0))");
        StringAssert.Contains(shader, "vec2 coupledPhaseWarpGradientPerMetre = baseSlope");
        StringAssert.Contains(
            shader,
            "vec2 phaseGradientPerMetre = radialDirection * reconstructionWaveNumberPerMetre");
        StringAssert.Contains(shader, "vec2 heightGradientWorldBlocksPerMetre = -amplitude");
        Assert.IsFalse(shader.Contains(
            "+ dot(baseSlope, radialDirection) * 0.32",
            StringComparison.Ordinal));
        StringAssert.Contains(shader, "packetIndex < MAX_SUBGRID_IMPACT_PACKETS");
        StringAssert.Contains(shader, "applySubgridImpactPackets(worldPosition, dynamicState)");
    }

    /// <summary>
    /// Verifies a subpixel projectile carrier is reconstructed above Nyquist from the radial pixel
    /// footprint while its amplitude is derived from the unchanged joule ledger and projected stiffness.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void SubpixelProjectilePacketUsesEnergyConservingFootprintReconstruction()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "vec2 worldPixelDxMetres = dFdx(localWorldPosition)");
        StringAssert.Contains(shader, "vec2 worldPixelDyMetres = dFdy(localWorldPosition)");
        StringAssert.Contains(shader, "float radialPixelFootprintMetres");
        StringAssert.Contains(shader, "float reconstructionWavelengthMetres = max(");
        StringAssert.Contains(shader, "3.0 * radialPixelFootprintMetres");
        StringAssert.Contains(shader, "float reconstructionPacketStiffnessNewtonsPerMetre");
        StringAssert.Contains(shader, "float physicalPacketStiffnessNewtonsPerMetre");
        StringAssert.Contains(shader, "float reconstructionSteepnessBoundWorldBlocks");
        Assert.IsFalse(shader.Contains("texture(liquidImpact", StringComparison.Ordinal));

        const double physicalWavelengthMetres = 0.0184;
        const double measuredPixelsPerWavelength = 0.31;
        const double renderedPacketEnergyJoules = 0.000442;
        const double densityKilogramsPerCubicMetre = 998.2;
        const double surfaceTensionNewtonsPerMetre = 0.07275;
        const double initialEnvelopeSigmaMetres = 0.05;
        double radialPixelFootprintMetres = physicalWavelengthMetres
            / measuredPixelsPerWavelength;
        double reconstructionWavelengthMetres = Math.Max(
            physicalWavelengthMetres,
            3.0 * radialPixelFootprintMetres);
        double reconstructionWaveNumberPerMetre = 2.0 * Math.PI
            / reconstructionWavelengthMetres;
        double packetEnergyAreaSquareMetres = Math.Pow(Math.PI, 1.5)
            * initialEnvelopeSigmaMetres * initialEnvelopeSigmaMetres;
        double reconstructionPacketStiffnessNewtonsPerMetre = (
                densityKilogramsPerCubicMetre
                    * LiquidPhysicalModel.StandardGravityMetresPerSecondSquared
                + surfaceTensionNewtonsPerMetre
                    * reconstructionWaveNumberPerMetre
                    * reconstructionWaveNumberPerMetre)
            * packetEnergyAreaSquareMetres;
        double reconstructionAmplitudeMetres = Math.Sqrt(
            2.0 * renderedPacketEnergyJoules
            / reconstructionPacketStiffnessNewtonsPerMetre);
        double reconstructedEnergyJoules = 0.5
            * reconstructionPacketStiffnessNewtonsPerMetre
            * reconstructionAmplitudeMetres * reconstructionAmplitudeMetres;

        Assert.AreEqual(3.0, reconstructionWavelengthMetres / radialPixelFootprintMetres, 1.0e-12);
        Assert.IsTrue(reconstructionAmplitudeMetres > 0.002);
        Assert.AreEqual(renderedPacketEnergyJoules, reconstructedEnergyJoules, 1.0e-12);
    }

    /// <summary>
    /// Verifies analytically and by finite difference that the dropped-item packet normal is the
    /// derivative of its phase-warped geometric height for a non-unit block-to-metre scale.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Liquid")]
    public void DroppedItemPacketChainRuleMatchesFiniteDifference()
    {
        const double metresPerWorldBlock = 0.73;
        const double waveNumberPerMetre = 8.4;
        const double phaseCoupling = 0.45;
        const double baseSlopeX = 0.18;
        const double baseSlopeZ = -0.11;
        const double amplitudeWorldBlocks = 0.07;
        const double packetFrontMetres = 1.3;
        const double envelopeSigmaMetres = 0.42;
        const double phaseOffset = -0.67;
        const double worldX = 2.05;
        const double worldZ = 0.81;
        const double differenceStepWorldBlocks = 0.00001;

        Func<double, double, double> packetHeight = (sampleWorldX, sampleWorldZ) =>
        {
            double sampleXMetres = sampleWorldX * metresPerWorldBlock;
            double sampleZMetres = sampleWorldZ * metresPerWorldBlock;
            double radiusMetres = Math.Sqrt(
                sampleXMetres * sampleXMetres + sampleZMetres * sampleZMetres);
            double distanceFromFrontMetres = radiusMetres - packetFrontMetres;
            double envelope = Math.Exp(
                -0.5 * Math.Pow(distanceFromFrontMetres / envelopeSigmaMetres, 2.0));
            double resolvedHeightWorldBlocks = 0.09
                + baseSlopeX * sampleWorldX
                + baseSlopeZ * sampleWorldZ;
            double phase = waveNumberPerMetre * radiusMetres
                + resolvedHeightWorldBlocks * metresPerWorldBlock
                    * waveNumberPerMetre * phaseCoupling
                + phaseOffset;
            return -amplitudeWorldBlocks * envelope * Math.Cos(phase);
        };

        double xMetres = worldX * metresPerWorldBlock;
        double zMetres = worldZ * metresPerWorldBlock;
        double radius = Math.Sqrt(xMetres * xMetres + zMetres * zMetres);
        double radialX = xMetres / radius;
        double radialZ = zMetres / radius;
        double distanceFromFront = radius - packetFrontMetres;
        double envelopeAtPoint = Math.Exp(
            -0.5 * Math.Pow(distanceFromFront / envelopeSigmaMetres, 2.0));
        double envelopeDerivative = -distanceFromFront
            / (envelopeSigmaMetres * envelopeSigmaMetres) * envelopeAtPoint;
        double resolvedHeight = 0.09 + baseSlopeX * worldX + baseSlopeZ * worldZ;
        double phaseAtPoint = waveNumberPerMetre * radius
            + resolvedHeight * metresPerWorldBlock * waveNumberPerMetre * phaseCoupling
            + phaseOffset;
        double phaseGradientX = radialX * waveNumberPerMetre
            + baseSlopeX * waveNumberPerMetre * phaseCoupling;
        double phaseGradientZ = radialZ * waveNumberPerMetre
            + baseSlopeZ * waveNumberPerMetre * phaseCoupling;
        double analyticSlopeX = -amplitudeWorldBlocks
            * (radialX * envelopeDerivative * Math.Cos(phaseAtPoint)
                - envelopeAtPoint * Math.Sin(phaseAtPoint) * phaseGradientX)
            * metresPerWorldBlock;
        double analyticSlopeZ = -amplitudeWorldBlocks
            * (radialZ * envelopeDerivative * Math.Cos(phaseAtPoint)
                - envelopeAtPoint * Math.Sin(phaseAtPoint) * phaseGradientZ)
            * metresPerWorldBlock;
        double finiteDifferenceSlopeX = (
                packetHeight(worldX + differenceStepWorldBlocks, worldZ)
                - packetHeight(worldX - differenceStepWorldBlocks, worldZ))
            / (2.0 * differenceStepWorldBlocks);
        double finiteDifferenceSlopeZ = (
                packetHeight(worldX, worldZ + differenceStepWorldBlocks)
                - packetHeight(worldX, worldZ - differenceStepWorldBlocks))
            / (2.0 * differenceStepWorldBlocks);

        Assert.AreEqual(analyticSlopeX, finiteDifferenceSlopeX, 0.00000001);
        Assert.AreEqual(analyticSlopeZ, finiteDifferenceSlopeZ, 0.00000001);
    }

    /// <summary>Loads the copied canonical fragment shader used by the test assembly.</summary>
    /// <returns>The complete GLSL fragment source.</returns>
    private static string ReadFragmentShader() =>
        DisplayShaderSource.LoadFromFileSystem(AppContext.BaseDirectory).Fragment.ReplaceLineEndings("\n");

    /// <summary>Evaluates unpolarized dielectric Fresnel using the same equations as the shader.</summary>
    /// <param name="cosIncident">Cosine of the incident angle.</param>
    /// <param name="incidentIor">Index of refraction on the incident side.</param>
    /// <param name="transmittedIor">Index of refraction inside the liquid.</param>
    /// <returns>Fraction of interface energy reflected.</returns>
    private static double ExactDielectricFresnel(
        double cosIncident,
        double incidentIor,
        double transmittedIor)
    {
        cosIncident = Math.Clamp(cosIncident, 0.0, 1.0);
        double eta = incidentIor / transmittedIor;
        double sinTransmittedSquared = eta * eta * Math.Max(1.0 - cosIncident * cosIncident, 0.0);
        if (sinTransmittedSquared >= 1.0)
        {
            return 1.0;
        }

        double cosTransmitted = Math.Sqrt(Math.Max(1.0 - sinTransmittedSquared, 0.0));
        double reflectanceS = (incidentIor * cosIncident - transmittedIor * cosTransmitted)
            / (incidentIor * cosIncident + transmittedIor * cosTransmitted);
        double reflectanceP = (transmittedIor * cosIncident - incidentIor * cosTransmitted)
            / (transmittedIor * cosIncident + incidentIor * cosTransmitted);
        return 0.5 * (reflectanceS * reflectanceS + reflectanceP * reflectanceP);
    }

    /// <summary>Evaluates per-channel Beer-Lambert transmittance for one authored liquid profile.</summary>
    /// <param name="absorption">RGB absorption coefficients in inverse metres.</param>
    /// <param name="scattering">RGB out-scattering coefficients in inverse metres.</param>
    /// <param name="pathLengthMetres">Refracted path length in metres.</param>
    /// <returns>RGB surviving radiance fractions.</returns>
    private static double[] BeerLambert(
        double[] absorption,
        double[] scattering,
        double pathLengthMetres)
    {
        return absorption.Zip(scattering)
            .Select(pair => Math.Exp(-(pair.First + pair.Second) * pathLengthMetres))
            .ToArray();
    }

    /// <summary>Evaluates the public-engine liquid surface convention used by the shader contract.</summary>
    /// <param name="blockY">Integer world Y containing the top fluid block.</param>
    /// <param name="liquidLevel">Vintage Story level in the interval zero through seven.</param>
    /// <returns>World-space free-surface height.</returns>
    private static double DecodeSurfaceHeight(int blockY, int liquidLevel) =>
        blockY + Math.Clamp(liquidLevel, 0, 7) / 8.0;

    /// <summary>Evaluates the shader's conservative liquid-interface depth predicate.</summary>
    private static double InterfaceVisibility(double surfaceDistance, double opaqueDistance) =>
        opaqueDistance >= surfaceDistance - 0.018 ? 1.0 : 0.0;

    /// <summary>Evaluates one-second small-amplitude viscous persistence.</summary>
    /// <param name="wavelengthMetres">Wave length in metres.</param>
    /// <param name="densityKgM3">Liquid density in kilograms per cubic metre.</param>
    /// <param name="dynamicViscosityPaS">Dynamic viscosity in pascal seconds.</param>
    /// <returns>One-second amplitude multiplier from exp(-2*nu*k^2).</returns>
    private static double ViscousPersistence(
        double wavelengthMetres,
        double densityKgM3,
        double dynamicViscosityPaS)
    {
        double waveNumber = 2.0 * Math.PI / wavelengthMetres;
        double dampingPerSecond = 2.0 * dynamicViscosityPaS / densityKgM3
            * waveNumber * waveNumber;
        return Math.Exp(-dampingPerSecond);
    }
}
