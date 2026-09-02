using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Test;

/// <summary>
/// Verifies the dedicated two-channel point/sun shadow pipeline and its deterministic bilateral and
/// temporal rejection rules without requiring a live OpenGL context.
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
    /// Verifies that the RG temporal resolve rejects a disoccluded sun channel without discarding
    /// stable point-light history carried by the other channel.
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

    /// <summary>Verifies the GLSL raw, bilateral, temporal, and consumption passes remain separate.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void ShaderContractUsesDedicatedShadowPasses()
    {
        string shader = ReadFragmentShader();
        StringAssert.Contains(shader, "uniform sampler2D shadowCurrent;");
        StringAssert.Contains(shader, "uniform sampler2D shadowHistory;");
        StringAssert.Contains(shader, "uniform vec2 shadowInverseFrameSize;");
        StringAssert.Contains(shader, "float traceRawPointShadowVisibility(");
        StringAssert.Contains(shader, "float traceRawSunShadowVisibility(");
        StringAssert.Contains(shader, "vec2 filterShadowVisibility(");
        StringAssert.Contains(shader, "texture(shadowCurrent, uv).rg");
        StringAssert.Contains(shader, "shadowFilterOffset(index) * shadowInverseFrameSize");
        StringAssert.Contains(shader, "float normalWeight = smoothstep(0.88, 0.995, normalAgreement);");
        StringAssert.Contains(shader, "float depthWeight = exp(");
        StringAssert.Contains(shader, "vec2 historyRejection = smoothstep(");
        StringAssert.Contains(shader, "vec2(0.04),\n        vec2(0.15),\n        historyDisagreement);");
        StringAssert.Contains(shader, "if (shadowTemporalBlend <= 0.001)");
        StringAssert.Contains(shader, "if (shadowPass == 2)");
        StringAssert.Contains(shader, "if (shadowPass == 1)");
        StringAssert.Contains(shader, "if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)");
        StringAssert.Contains(shader, "vec2 repairedShadowVisibility = vec2(0.0);");
        StringAssert.Contains(shader, "texture(shadowHistory, neighbourUv).rg");
        StringAssert.Contains(shader, "repairedShadowVisibilityCount++;");
        StringAssert.Contains(shader, "geometryWasRepaired = true;");
        StringAssert.Contains(shader, "geometryWasRepaired\n                        && repairedShadowVisibilityCount >= 2");
        StringAssert.Contains(shader, "? filterShadowVisibility(position, normal)\n            : vec2(1.0);");
        StringAssert.Contains(shader, "if (prefilteredShadowVisibility == 0 && !cameraAlignedLight)");
        StringAssert.Contains(shader, ": clamp(texture(shadowHistory, uv).rg");
        StringAssert.Contains(shader, "result.sunVisibility = prefilteredShadowVisibility != 0");
        StringAssert.Contains(shader, "vec2 rawVisibility = vec2(1.0);");
        StringAssert.Contains(shader, "rawVisibility.r = traceRawPointShadowVisibility(");
        StringAssert.Contains(shader, "rawVisibility.g = traceRawSunShadowVisibility(");
        StringAssert.Contains(shader, "float cameraAlignedShadow;");
        StringAssert.Contains(shader, "if (cameraAlignedLight)\n        {\n            continue;");
        StringAssert.Contains(shader, "float visibility = cameraAlignedLight\n            ? 1.0");
        StringAssert.Contains(shader, "clamp(voxelLighting.cameraAlignedShadow, 0.0, 1.0)");
        Assert.IsFalse(shader.Contains(
            "if (!hasGeometry && deferredGeometryRequired)\n    {",
            StringComparison.Ordinal));

        int rawPass = shader.IndexOf("if (shadowPass == 1)", StringComparison.Ordinal);
        int filteredConsumption = shader.IndexOf(
            "resolvedShadowVisibility = geometryWasRepaired",
            StringComparison.Ordinal);
        int finalColor = shader.IndexOf("vec3 finalColor;", StringComparison.Ordinal);
        Assert.IsTrue(rawPass >= 0 && filteredConsumption > rawPass && finalColor > filteredConsumption);
        Assert.IsFalse(shader[(finalColor)..].Contains("shadowCurrent", StringComparison.Ordinal));
        Assert.IsFalse(shader[(finalColor)..].Contains("shadowHistory", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies the renderer owns half-resolution RG16F current/history targets, keeps tier two
    /// enabled, restores the full-resolution viewport, and invalidates stale masks.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void RendererContractOwnsHalfResolutionPingPongRg16fAndThreeOrderedDraws()
    {
        string renderer = ReadRendererSource();
        StringAssert.Contains(renderer, "private int shadowCurrentTexture;");
        StringAssert.Contains(renderer, "private readonly int[] shadowHistoryTextures = new int[2];");
        StringAssert.Contains(renderer, "private readonly int[] shadowHistoryFramebuffers = new int[2];");
        StringAssert.Contains(renderer, "int shadowWidth = Math.Max(1, (width + 1) / 2);");
        StringAssert.Contains(renderer, "int shadowHeight = Math.Max(1, (height + 1) / 2);");
        StringAssert.Contains(renderer, "PixelInternalFormat.Rg16f");
        StringAssert.Contains(renderer, "PixelFormat.Rg");
        StringAssert.Contains(renderer, "PixelType.HalfFloat");
        StringAssert.Contains(renderer, "\"shadowInverseFrameSize\",");
        StringAssert.Contains(renderer, "shader.Uniform(\"shadowPass\", 1);");
        StringAssert.Contains(renderer, "shader.Uniform(\"shadowPass\", 2);");
        StringAssert.Contains(renderer, "shader.Uniform(\"prefilteredShadowVisibility\", 1);");
        StringAssert.Contains(renderer, "bool selectedSunShadowSource = config.SunShadowsEnabled");
        StringAssert.Contains(renderer, "GL.Viewport(0, 0, shadowWidth, shadowHeight);");
        StringAssert.Contains(renderer, "GL.Viewport(0, 0, width, height);");
        StringAssert.Contains(renderer, "shadowHistoryIndex = shadowWriteIndex;");
        StringAssert.Contains(renderer, "GL.DeleteTexture(shadowCurrentTexture);");
        Assert.IsTrue(CountOccurrences(renderer, "shadowHistoryValid = false;") >= 6);

        int filterGateStart = renderer.IndexOf(
            "bool shadowFilteringActive =",
            StringComparison.Ordinal);
        int filterGateEnd = renderer.IndexOf(
            "shader.Uniform(\"shadowPass\", 0);",
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
            "FilmicDisplayRenderer.cs"));

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
