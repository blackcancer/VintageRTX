using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>
/// Verifies the eight independent point-light visibility channels, separate solar channel, and their
/// deterministic bilateral and temporal rejection rules without requiring a live OpenGL context.
/// </summary>
[TestClass]
public sealed class ShadowFilterPipelineTests
{
    /// <summary>Verifies that coplanar, similarly oriented samples reduce stochastic mask variance.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void SpatialResolveSmoothsCompatibleReceiverSamples()
    {
        double filtered = ResolveSpatialVisibility(
            0.20,
            [0.80, 0.70, 0.60, 0.50],
            [1.0, 0.999, 0.998, 0.997],
            [0.0, 0.002, 0.003, 0.004],
            centerDepth: 8.0,
            out double minimum,
            out double maximum);

        Assert.IsTrue(filtered > 0.45 && filtered < 0.65);
        Assert.AreEqual(0.20, minimum, 0.000001);
        Assert.AreEqual(0.80, maximum, 0.000001);
    }

    /// <summary>Verifies that depth and normal discontinuities cannot leak a shadow across a silhouette.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void SpatialResolveRejectsDepthAndNormalDiscontinuities()
    {
        double filtered = ResolveSpatialVisibility(
            0.15,
            [0.95, 0.95, 0.95, 0.95],
            [0.40, 0.60, 0.80, 0.87],
            [0.0, 0.0, 2.0, 2.0],
            centerDepth: 4.0,
            out double minimum,
            out double maximum);

        Assert.AreEqual(0.15, filtered, 0.000001);
        Assert.AreEqual(0.15, minimum, 0.000001);
        Assert.AreEqual(0.15, maximum, 0.000001);
    }

    /// <summary>Verifies history clamping, stable accumulation, and rejection after a visibility change.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void TemporalResolveClampsAndRejectsDisoccludedHistory()
    {
        double stable = ResolveTemporalVisibility(
            spatialVisibility: 0.52,
            neighborhoodMinimum: 0.45,
            neighborhoodMaximum: 0.60,
            previousVisibility: 0.56,
            requestedHistoryWeight: 0.90);
        double disoccluded = ResolveTemporalVisibility(
            spatialVisibility: 0.92,
            neighborhoodMinimum: 0.88,
            neighborhoodMaximum: 1.00,
            previousVisibility: 0.10,
            requestedHistoryWeight: 0.90);

        Assert.IsTrue(stable > 0.54 && stable < 0.56);
        Assert.AreEqual(0.92, disoccluded, 0.000001);
    }

    /// <summary>
    /// Verifies that temporal resolve rejects a disoccluded sun channel without discarding stable
    /// point-light history carried by an independent MRT channel.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void DualChannelTemporalResolveRejectsChannelsIndependently()
    {
        double pointVisibility = ResolveTemporalVisibility(
            spatialVisibility: 0.52,
            neighborhoodMinimum: 0.45,
            neighborhoodMaximum: 0.60,
            previousVisibility: 0.56,
            requestedHistoryWeight: 0.90);
        double sunVisibility = ResolveTemporalVisibility(
            spatialVisibility: 0.92,
            neighborhoodMinimum: 0.88,
            neighborhoodMaximum: 1.00,
            previousVisibility: 0.10,
            requestedHistoryWeight: 0.90);

        Assert.IsTrue(pointVisibility > 0.54 && pointVisibility < 0.56);
        Assert.AreEqual(0.92, sunVisibility, 0.000001);
    }

    /// <summary>
    /// Verifies that a synthetic one-pixel receiver inherits both filtered channels only after at
    /// least two coherent neighbours agree, while a lone silhouette neighbour remains invalid.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void RepairedReceiverInheritsAtLeastTwoCoherentShadowNeighbours()
    {
        bool loneNeighbourAccepted = TryResolveRepairedVisibility(
            [0.20],
            [0.90],
            out double lonePointVisibility,
            out double loneSunVisibility);
        bool coherentCrossAccepted = TryResolveRepairedVisibility(
            [0.20, 0.40, 0.60],
            [0.90, 0.80, 0.70],
            out double pointVisibility,
            out double sunVisibility);

        Assert.IsFalse(loneNeighbourAccepted);
        Assert.AreEqual(1.0, lonePointVisibility, 0.000001);
        Assert.AreEqual(1.0, loneSunVisibility, 0.000001);
        Assert.IsTrue(coherentCrossAccepted);
        Assert.AreEqual(0.40, pointVisibility, 0.000001);
        Assert.AreEqual(0.80, sunVisibility, 0.000001);
    }

    /// <summary>
    /// Measures a two-lamp composition where equal-energy red and blue emitters exchange occlusion:
    /// total energy stays constant while the visible chroma follows the unblocked source.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void TwoLampCompositionPreservesOcclusionIdentityAndChroma()
    {
        FilmicDisplayRenderer.IndependentLightEnergy redWhenRedBlocked =
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy(
                [1.0f, 0.0f],
                [0.0f, 1.0f]);
        FilmicDisplayRenderer.IndependentLightEnergy blueWhenRedBlocked =
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy(
                [0.0f, 1.0f],
                [0.0f, 1.0f]);
        FilmicDisplayRenderer.IndependentLightEnergy redWhenBlueBlocked =
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy(
                [1.0f, 0.0f],
                [1.0f, 0.0f]);
        FilmicDisplayRenderer.IndependentLightEnergy blueWhenBlueBlocked =
            FilmicDisplayRenderer.AccumulateIndependentLightEnergy(
                [0.0f, 1.0f],
                [1.0f, 0.0f]);

        Assert.AreEqual(0.0f, redWhenRedBlocked.Direct, 0.000001f);
        Assert.AreEqual(1.0f, blueWhenRedBlocked.Direct, 0.000001f);
        Assert.AreEqual(1.0f, redWhenBlueBlocked.Direct, 0.000001f);
        Assert.AreEqual(0.0f, blueWhenBlueBlocked.Direct, 0.000001f);
        Assert.AreEqual(
            redWhenRedBlocked.Direct + blueWhenRedBlocked.Direct,
            redWhenBlueBlocked.Direct + blueWhenBlueBlocked.Direct,
            0.000001f);
    }

    /// <summary>Verifies temporal visibility is reusable only while every ordered slot keeps its source.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void TemporalHistoryInvalidatesWhenASelectedSourceChangesSlot()
    {
        long firstLamp = FilmicDisplayRenderer.DynamicShadowLightSlotKey(2);
        long secondLamp = FilmicDisplayRenderer.DynamicShadowLightSlotKey(7);

        Assert.AreNotEqual(0L, firstLamp);
        Assert.AreNotEqual(firstLamp, secondLamp);
        Assert.IsTrue(FilmicDisplayRenderer.ShadowLightSlotsAreStable(
            [firstLamp, secondLamp],
            2,
            [firstLamp, secondLamp],
            2));
        Assert.IsFalse(FilmicDisplayRenderer.ShadowLightSlotsAreStable(
            [firstLamp, secondLamp],
            2,
            [secondLamp, firstLamp],
            2));
        Assert.IsFalse(FilmicDisplayRenderer.ShadowLightSlotsAreStable(
            [firstLamp, secondLamp],
            2,
            [firstLamp],
            1));
    }

    /// <summary>Verifies numerical source jitter is snapped while real emitter motion rejects history.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void TemporalHistorySnapsSubCentimetreLightJitterAndRejectsMotion()
    {
        float[] previous = [10.0f, 20.0f, 30.0f, -2.0f, 4.0f, 8.0f];
        float[] jittered = [10.004f, 20.0f, 30.0f, 1.0f, -2.0f, 4.006f, 8.0f, 1.0f];
        Assert.IsTrue(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            previous,
            jittered,
            2,
            0.01f));
        CollectionAssert.AreEqual(
            new[] { 10.0f, 20.0f, 30.0f, 1.0f, -2.0f, 4.0f, 8.0f, 1.0f },
            jittered);

        float[] moved = [10.2f, 20.0f, 30.0f, 1.0f];
        Assert.IsFalse(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            previous,
            moved,
            1,
            0.01f));
        Assert.AreEqual(10.2f, moved[0]);
        Assert.IsFalse(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            previous,
            moved,
            -1,
            0.01f));
        Assert.IsFalse(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            [],
            moved,
            1,
            0.01f));
        Assert.IsFalse(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            previous,
            moved,
            1,
            float.NaN));
    }

    /// <summary>Verifies camera-aligned emitters cannot invalidate masks that deliberately exclude them.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void TemporalHistoryIgnoresOnlyShadowlessCameraAlignedLightMotion()
    {
        float[] previous = [10.0f, 20.0f, 30.0f, 40.0f, 20.0f, 30.0f];
        float[] cameraAndWorld = [10.25f, 20.0f, 30.0f, 1.0f, 40.0f, 20.0f, 30.0f, 1.0f];
        Assert.IsTrue(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            previous,
            cameraAndWorld,
            [-1.0f, 2.0f],
            2,
            0.01f,
            10.0f,
            20.0f,
            30.0f,
            0.75f));
        Assert.AreEqual(10.25f, cameraAndWorld[0]);

        cameraAndWorld[4] = 40.2f;
        Assert.IsFalse(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            previous,
            cameraAndWorld,
            [-1.0f, 2.0f],
            2,
            0.01f,
            10.0f,
            20.0f,
            30.0f,
            0.75f));
        Assert.IsFalse(FilmicDisplayRenderer.StabilizeShadowLightPositions(
            previous,
            cameraAndWorld,
            [-1.0f],
            2,
            0.01f,
            10.0f,
            20.0f,
            30.0f,
            0.75f));
    }

    /// <summary>Verifies the GLSL raw, bilateral, temporal, and consumption passes remain separate.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void ShaderContractUsesDedicatedShadowPasses()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "uniform sampler2D shadowPointCurrentA;");
        StringAssert.Contains(shader, "uniform sampler2D shadowPointCurrentB;");
        StringAssert.Contains(shader, "uniform sampler2D shadowSunCurrent;");
        StringAssert.Contains(shader, "uniform sampler2D shadowPointHistoryA;");
        StringAssert.Contains(shader, "uniform sampler2D shadowPointHistoryB;");
        StringAssert.Contains(shader, "uniform sampler2D shadowSunHistory;");
        StringAssert.Contains(shader, "uniform vec2 shadowInverseFrameSize;");
        StringAssert.Contains(shader, "uniform int shadowFilterTapCount;");
        StringAssert.Contains(shader, "uniform int secondaryBounceCadence;");
        StringAssert.Contains(shader, "layout(location = 1) out vec4 outShadowPointB;");
        StringAssert.Contains(shader, "layout(location = 2) out vec4 outShadowSun;");
        StringAssert.Contains(shader, "void traceRawPointShadowVisibilities(");
        StringAssert.Contains(shader, "float traceRawSunShadowVisibility(");
        StringAssert.Contains(shader, "void filterShadowVisibilities(");
        StringAssert.Contains(shader, "texture(shadowPointCurrentA, uv)");
        StringAssert.Contains(shader, "texture(shadowPointCurrentB, uv)");
        StringAssert.Contains(shader, "texture(shadowSunCurrent, uv).r");
        StringAssert.Contains(shader, "shadowFilterOffset(index) * shadowInverseFrameSize");
        StringAssert.Contains(shader, "if (index >= shadowFilterTapCount)");
        StringAssert.Contains(shader, "shadowFilterTapCount == 2");
        StringAssert.Contains(shader, "vec2(0.70710678, 0.70710678)");
        Assert.IsFalse(shader.Contains(
            "shadowFilterTapCount == 2 && (temporalFrameIndex & 1) != 0",
            StringComparison.Ordinal));
        StringAssert.Contains(shader, "float normalWeight = smoothstep(0.88, 0.995, normalAgreement);");
        StringAssert.Contains(shader, "float depthWeight = exp(");
        StringAssert.Contains(shader, "vec4 pointRejectionA = smoothstep(");
        StringAssert.Contains(shader, "vec4 pointRejectionB = smoothstep(");
        StringAssert.Contains(shader, "float sunRejection = smoothstep(");
        StringAssert.Contains(shader, "if (shadowTemporalBlend <= 0.001)");
        StringAssert.Contains(shader, "if (shadowPass == 2)");
        StringAssert.Contains(shader, "if (shadowPass == 1)");
        StringAssert.Contains(shader, "if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)");
        StringAssert.Contains(shader, "vec4 repairedPointVisibilityA = vec4(0.0);");
        StringAssert.Contains(shader, "vec4 repairedPointVisibilityB = vec4(0.0);");
        StringAssert.Contains(shader, "texture(shadowSunHistory, neighbourUv).r");
        StringAssert.Contains(shader, "repairedShadowVisibilityCount++;");
        StringAssert.Contains(shader, "geometryWasRepaired = true;");
        StringAssert.Contains(shader, "if (geometryWasRepaired");
        StringAssert.Contains(shader, "&& repairedShadowVisibilityCount >= 2");
        StringAssert.Contains(shader, "if (prefilteredShadowVisibility == 0 && !cameraAlignedLight)");
        StringAssert.Contains(shader, "texture(shadowPointHistoryA, uv)");
        StringAssert.Contains(shader, "texture(shadowPointHistoryB, uv)");
        StringAssert.Contains(shader, "vec4 rawPointA = vec4(1.0);");
        StringAssert.Contains(shader, "vec4 rawPointB = vec4(1.0);");
        StringAssert.Contains(shader, "rawSun = traceRawSunShadowVisibility(");
        StringAssert.Contains(shader, "float cameraAlignedShadow;");
        StringAssert.Contains(shader, "if (cameraAlignedLight)\n        {\n            continue;");
        StringAssert.Contains(shader, "float visibility = cameraAlignedLight\n            ? 1.0");
        StringAssert.Contains(shader, "pointShadowVisibilityAt(\n                filteredPointVisibilityA,\n                filteredPointVisibilityB,\n                lightIndex)");
        StringAssert.Contains(shader, "result.direct += unoccludedDirect * visibility;");
        StringAssert.Contains(shader, "clamp(voxelLighting.cameraAlignedShadow, 0.0, 1.0)");
        int rawFunctionStart = shader.IndexOf(
            "void traceRawPointShadowVisibilities(",
            StringComparison.Ordinal);
        int rawFunctionEnd = shader.IndexOf(
            "float traceRawSunShadowVisibility(",
            rawFunctionStart,
            StringComparison.Ordinal);
        Assert.IsTrue(rawFunctionStart >= 0 && rawFunctionEnd > rawFunctionStart);
        Assert.IsFalse(shader[rawFunctionStart..rawFunctionEnd].Contains(
            "potentialWeight += shadowPotential",
            StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains(
            "if (!hasGeometry && deferredGeometryRequired)\n    {",
            StringComparison.Ordinal));

        int rawPass = shader.IndexOf("if (shadowPass == 1)", StringComparison.Ordinal);
        int filteredConsumption = shader.IndexOf(
            "vec4 resolvedPointVisibilityA = vec4(1.0);",
            StringComparison.Ordinal);
        int finalColor = shader.IndexOf("vec3 finalColor;", StringComparison.Ordinal);
        Assert.IsTrue(rawPass >= 0 && filteredConsumption > rawPass && finalColor > filteredConsumption);
        Assert.IsFalse(shader[(finalColor)..].Contains("shadowPointCurrent", StringComparison.Ordinal));
        Assert.IsFalse(shader[(finalColor)..].Contains("shadowSunCurrent", StringComparison.Ordinal));
    }

    /// <summary>
    /// Guards exact edge/corner traversal for the cage, fine block, main local-light, and solar
    /// grids so zero-width neighbours cannot widen an authored shadow silhouette.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void VoxelShadowTraversalAdvancesEveryTiedAxis()
    {
        string shader = ReadFragmentShader();
        const string MinimumBoundary =
            "float traveled = min(sideDistance.x, min(sideDistance.y, sideDistance.z));";
        const string AdvanceX = "if (sideDistance.x <= traveled + 0.00001)";
        const string AdvanceY = "if (sideDistance.y <= traveled + 0.00001)";
        const string AdvanceZ = "if (sideDistance.z <= traveled + 0.00001)";

        Assert.AreEqual(5, CountOccurrences(shader, MinimumBoundary));
        Assert.AreEqual(5, CountOccurrences(shader, AdvanceX));
        Assert.AreEqual(5, CountOccurrences(shader, AdvanceY));
        Assert.AreEqual(5, CountOccurrences(shader, AdvanceZ));
        Assert.IsFalse(shader.Contains(
            "if (sideDistance.x <= sideDistance.y && sideDistance.x <= sideDistance.z)",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies the renderer owns two RGBA16F point banks plus an R16F solar bank, keeps tier two
    /// enabled, restores the full-resolution viewport, and invalidates stale source histories.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void RendererContractOwnsTierScaledMrtHistoryAndThreeOrderedDraws()
    {
        string renderer = ReadRendererSource();
        string shader = ReadFragmentShader();
        StringAssert.Contains(renderer, "private int shadowCurrentPointATexture;");
        StringAssert.Contains(renderer, "private int shadowCurrentPointBTexture;");
        StringAssert.Contains(renderer, "private int shadowCurrentSunTexture;");
        StringAssert.Contains(renderer, "private readonly int[] shadowHistoryPointATextures = new int[2];");
        StringAssert.Contains(renderer, "private readonly int[] shadowHistoryPointBTextures = new int[2];");
        StringAssert.Contains(renderer, "private readonly int[] shadowHistorySunTextures = new int[2];");
        StringAssert.Contains(renderer, "private readonly int[] shadowHistoryFramebuffers = new int[2];");
        StringAssert.Contains(renderer, "int shadowWidth = shadowTextureWidth;");
        StringAssert.Contains(renderer, "int shadowHeight = shadowTextureHeight;");
        Assert.AreEqual(2, FilmicDisplayRenderer.ShadowResolutionDivisor(0));
        Assert.AreEqual(2, FilmicDisplayRenderer.ShadowResolutionDivisor(1));
        Assert.AreEqual(3, FilmicDisplayRenderer.ShadowResolutionDivisor(2));
        StringAssert.Contains(renderer, "PixelInternalFormat.Rgba16f");
        StringAssert.Contains(renderer, "PixelFormat.Rgba");
        StringAssert.Contains(renderer, "PixelInternalFormat.R16f");
        StringAssert.Contains(renderer, "PixelFormat.Red");
        StringAssert.Contains(renderer, "PixelType.HalfFloat");
        StringAssert.Contains(renderer, "DrawBuffersEnum.ColorAttachment2");
        StringAssert.Contains(renderer, "GL.DrawBuffers(ShadowDrawBuffers.Length, ShadowDrawBuffers);");
        StringAssert.Contains(renderer, "\"shadowInverseFrameSize\",");
        StringAssert.Contains(renderer, "shader.Uniform(\"shadowFilterTapCount\", adaptiveQualityLevel == 2 ? 2 : 4);");
        StringAssert.Contains(renderer, "shader.Uniform(\"shadowPass\", 1);");
        StringAssert.Contains(renderer, "shader.Uniform(\"shadowPass\", 2);");
        StringAssert.Contains(renderer, "shader.Uniform(\"shadowTemporalBlend\", 0.0f);");
        StringAssert.Contains(renderer, "never blend different receiver identities here");
        StringAssert.Contains(renderer, "shader.Uniform(\"prefilteredShadowVisibility\", 1);");
        StringAssert.Contains(renderer, "bool shadowRefreshActive = shadowFilteringActive");
        StringAssert.Contains(renderer, "ShouldRefreshShadowVisibility(");
        StringAssert.Contains(renderer, "else if (shadowFilteringActive)");
        StringAssert.Contains(renderer, "\"shadowPointHistoryA\",");
        StringAssert.Contains(renderer, "\"shadowPointHistoryB\",");
        StringAssert.Contains(renderer, "\"shadowSunHistory\",");
        StringAssert.Contains(renderer, "shadowCurrentPointBTexture,\n                    5);");
        StringAssert.Contains(renderer, "shadowCurrentSunTexture,\n                    8);");
        StringAssert.Contains(renderer, "\"historyColor\",\n                    temporalHistoryTextures[temporalHistoryIndex],\n                    5);");
        StringAssert.Contains(renderer, "bool selectedSunShadowSource = config.SunShadowsEnabled");
        StringAssert.Contains(renderer, "bool selectedPointShadowSources = currentVoxelLightCount > 0;");
        StringAssert.Contains(renderer, "GL.Viewport(0, 0, shadowWidth, shadowHeight);");
        StringAssert.Contains(renderer, "GL.Viewport(0, 0, width, height);");
        StringAssert.Contains(renderer, "shadowHistoryIndex = shadowWriteIndex;");
        StringAssert.Contains(renderer, "GL.DeleteTexture(shadowCurrentPointATexture);");
        StringAssert.Contains(renderer, "if (!UpdateShadowLightSlotHistory(");
        StringAssert.Contains(renderer, "ResolveRenderFloatingOrigin(");
        StringAssert.Contains(renderer, "EnumRenderStage.Before,\n            \"vintagertx-camera-origin\"");
        StringAssert.Contains(renderer, "api.Event.UnregisterRenderer(this, EnumRenderStage.Before);");
        Assert.IsFalse(renderer.Contains("var floatingOrigin = api.World.Player.Entity.Pos;", StringComparison.Ordinal));
        StringAssert.Contains(shader, "distance(lightPositionIntensity.xyz, floatingWorldOrigin) < 0.75");
        Assert.IsTrue(CountOccurrences(renderer, "shadowHistoryValid = false;") >= 6);

        int filterGateStart = renderer.IndexOf(
            "bool shadowFilteringActive =",
            StringComparison.Ordinal);
        int filterGateEnd = renderer.IndexOf(
            "bool shadowRefreshActive =",
            filterGateStart,
            StringComparison.Ordinal);
        Assert.IsTrue(filterGateStart >= 0 && filterGateEnd > filterGateStart);
        Assert.IsFalse(renderer[filterGateStart..filterGateEnd].Contains(
            "adaptiveQualityLevel",
            StringComparison.Ordinal));

        int rawPass = renderer.IndexOf("shader.Uniform(\"shadowPass\", 1);", StringComparison.Ordinal);
        int filterPass = renderer.IndexOf("shader.Uniform(\"shadowPass\", 2);", StringComparison.Ordinal);
        int consumePass = renderer.IndexOf(
            "shader.Uniform(\"prefilteredShadowVisibility\", 1);",
            StringComparison.Ordinal);
        int shadowViewport = renderer.IndexOf(
            "GL.Viewport(0, 0, shadowWidth, shadowHeight);",
            StringComparison.Ordinal);
        int finalViewport = renderer.IndexOf(
            "GL.Viewport(0, 0, width, height);",
            shadowViewport,
            StringComparison.Ordinal);
        Assert.IsTrue(
            shadowViewport >= 0
            && rawPass > shadowViewport
            && filterPass > rawPass
            && consumePass > filterPass
            && finalViewport > consumePass);
    }

    /// <summary>Evaluates the shader's four-neighbour depth/normal bilateral visibility resolve.</summary>
    /// <param name="centerVisibility">Raw visibility of the receiver pixel.</param>
    /// <param name="neighborVisibilities">Four raw cross-neighbour visibilities.</param>
    /// <param name="normalAgreements">Dot products between center and neighbour normals.</param>
    /// <param name="depthDeltas">Absolute view-depth differences in blocks.</param>
    /// <param name="centerDepth">Absolute center view depth in blocks.</param>
    /// <param name="minimum">Accepted neighbourhood minimum.</param>
    /// <param name="maximum">Accepted neighbourhood maximum.</param>
    /// <returns>Spatially resolved visibility.</returns>
    private static double ResolveSpatialVisibility(
        double centerVisibility,
        double[] neighborVisibilities,
        double[] normalAgreements,
        double[] depthDeltas,
        double centerDepth,
        out double minimum,
        out double maximum)
    {
        Assert.AreEqual(4, neighborVisibilities.Length);
        Assert.AreEqual(4, normalAgreements.Length);
        Assert.AreEqual(4, depthDeltas.Length);
        double accumulated = centerVisibility;
        double accumulatedWeight = 1.0;
        minimum = centerVisibility;
        maximum = centerVisibility;
        double depthScale = Math.Max(0.025, Math.Abs(centerDepth) * 0.0025);
        for (int index = 0; index < 4; index++)
        {
            double normalWeight = SmoothStep(0.88, 0.995, Math.Max(normalAgreements[index], 0.0));
            double depthWeight = Math.Exp(-Math.Abs(depthDeltas[index]) / depthScale);
            double weight = normalWeight * depthWeight;
            if (weight <= 0.02)
            {
                continue;
            }

            accumulated += neighborVisibilities[index] * weight;
            accumulatedWeight += weight;
            minimum = Math.Min(minimum, neighborVisibilities[index]);
            maximum = Math.Max(maximum, neighborVisibilities[index]);
        }

        return accumulated / Math.Max(accumulatedWeight, 0.001);
    }

    /// <summary>Evaluates clamped temporal visibility with disagreement rejection.</summary>
    /// <param name="spatialVisibility">Current bilateral result.</param>
    /// <param name="neighborhoodMinimum">Minimum accepted current visibility.</param>
    /// <param name="neighborhoodMaximum">Maximum accepted current visibility.</param>
    /// <param name="previousVisibility">Previous filtered visibility at the same stable pixel.</param>
    /// <param name="requestedHistoryWeight">Configured history weight.</param>
    /// <returns>Temporally resolved visibility.</returns>
    private static double ResolveTemporalVisibility(
        double spatialVisibility,
        double neighborhoodMinimum,
        double neighborhoodMaximum,
        double previousVisibility,
        double requestedHistoryWeight)
    {
        double unclampedHistory = previousVisibility;
        previousVisibility = Math.Clamp(
            previousVisibility,
            neighborhoodMinimum,
            neighborhoodMaximum);
        double disagreement = Math.Abs(unclampedHistory - spatialVisibility);
        double rejection = SmoothStep(0.04, 0.15, disagreement);
        double acceptedWeight = Math.Clamp(requestedHistoryWeight, 0.0, 0.94)
            * (1.0 - rejection);
        return spatialVisibility * (1.0 - acceptedWeight)
            + previousVisibility * acceptedWeight;
    }

    /// <summary>Averages independent point/sun visibility carried by coherent repair neighbours.</summary>
    /// <param name="pointVisibilities">Filtered point-light visibility of coherent neighbours.</param>
    /// <param name="sunVisibilities">Filtered sun visibility of the same coherent neighbours.</param>
    /// <param name="pointVisibility">Inherited point-light visibility, or one when unsupported.</param>
    /// <param name="sunVisibility">Inherited sun visibility, or one when unsupported.</param>
    /// <returns>Whether at least two coherent neighbours support the repaired receiver.</returns>
    private static bool TryResolveRepairedVisibility(
        double[] pointVisibilities,
        double[] sunVisibilities,
        out double pointVisibility,
        out double sunVisibility)
    {
        Assert.AreEqual(pointVisibilities.Length, sunVisibilities.Length);
        pointVisibility = 1.0;
        sunVisibility = 1.0;
        if (pointVisibilities.Length < 2)
        {
            return false;
        }

        pointVisibility = pointVisibilities.Average();
        sunVisibility = sunVisibilities.Average();
        return true;
    }

    /// <summary>Evaluates GLSL-compatible smoothstep.</summary>
    /// <param name="minimum">Lower edge.</param>
    /// <param name="maximum">Upper edge.</param>
    /// <param name="value">Input value.</param>
    /// <returns>Hermite interpolation in [0, 1].</returns>
    private static double SmoothStep(double minimum, double maximum, double value)
    {
        double normalized = Math.Clamp((value - minimum) / (maximum - minimum), 0.0, 1.0);
        return normalized * normalized * (3.0 - 2.0 * normalized);
    }

    /// <summary>Loads the canonical display fragment copied to the test output.</summary>
    /// <returns>Normalized fragment source.</returns>
    private static string ReadFragmentShader() =>
        VintageRTX.Rendering.DisplayShaderSource
            .LoadFromFileSystem(AppContext.BaseDirectory)
            .Fragment
            .ReplaceLineEndings("\n");

    /// <summary>Loads the renderer source to validate resource and pass-order contracts.</summary>
    /// <returns>Complete renderer C# source.</returns>
    private static string ReadRendererSource() => File.ReadAllText(
            Path.Combine(
                TestPaths.FindRepositoryRoot(),
                "src",
                "VintageRTX",
                "Rendering",
                "FilmicDisplayRenderer.cs"))
        .ReplaceLineEndings("\n");

    /// <summary>Counts exact non-overlapping occurrences of a source contract.</summary>
    /// <param name="source">Text to inspect.</param>
    /// <param name="value">Exact value to count.</param>
    /// <returns>Number of non-overlapping matches.</returns>
    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
