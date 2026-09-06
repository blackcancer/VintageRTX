using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using VintageRTX.Rendering;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Test;

/// <summary>
/// Verifies that local emitters use documented SI photometry consistently in CPU data, assets,
/// and the shipped display shader.
/// </summary>
[TestClass]
public sealed class EmitterPhotometryTests
{
    /// <summary>Verifies inverse-square attenuation, receiver cosine, and finite-source clamping.</summary>
    [TestMethod]
    public void PointIlluminanceUsesInverseSquareAndCosineLaw()
    {
        float atOneMetre = EmitterPhotometry.PointIlluminanceLux(8.0f, 1.0f, 1.0f, 0.01f);
        float atTwoMetres = EmitterPhotometry.PointIlluminanceLux(8.0f, 2.0f, 1.0f, 0.01f);
        float oblique = EmitterPhotometry.PointIlluminanceLux(8.0f, 1.0f, 0.5f, 0.01f);
        float atSource = EmitterPhotometry.PointIlluminanceLux(8.0f, 0.0f, 1.0f, 0.02f);

        Assert.AreEqual(8.0f, atOneMetre, 0.0001f);
        Assert.AreEqual(4.0f, atOneMetre / atTwoMetres, 0.0001f);
        Assert.AreEqual(atOneMetre * 0.5f, oblique, 0.0001f);
        Assert.AreEqual(20_000.0f, atSource, 0.5f);
        Assert.AreEqual(0.0f, EmitterPhotometry.PointIlluminanceLux(-1.0f, 1.0f, -1.0f, 0.0f));

        float lanternAtFourMetres = EmitterPhotometry.PointIlluminanceLux(
            6.5f,
            4.0f,
            1.0f,
            0.04f);
        Assert.AreEqual(0.40625f, lanternAtFourMetres, 0.0001f);
        Assert.AreEqual(
            0.14215f,
            lanternAtFourMetres * EmitterPhotometry.LuxToRendererRadiance,
            0.0001f);
    }

    /// <summary>Verifies that the numerical lux cutoff and candela conversions are reciprocal.</summary>
    [TestMethod]
    public void TraceRadiusIsDerivedFromCandelaAndLuxCutoff()
    {
        float candela = EmitterPhotometry.CandelaAtCutoffRange(
            18.0f,
            EmitterPhotometry.DefaultCutoffIlluminanceLux);
        EmitterPhotometry profile = new(
            "test",
            candela,
            0.015f,
            0.040f,
            EmitterPhotometry.DefaultCutoffIlluminanceLux,
            0.55f,
            0.41f);

        Assert.AreEqual(6.48f, candela, 0.0001f);
        Assert.AreEqual(18.0f, profile.TraceRadiusMetres(), 0.0001f);
        Assert.IsTrue(profile.HasChromaticity);
        Assert.IsTrue(profile.TryGetSrgb(out float red, out float green, out float blue));
        Assert.AreEqual(1.0f, red, 0.0001f);
        Assert.IsTrue(green is > 0.0f and < 1.0f);
        Assert.IsTrue(blue >= 0.0f && blue < green);
    }

    /// <summary>Verifies physical ellipsoid foreshortening for horizontal, oblique and axial rays.</summary>
    [TestMethod]
    public void FlameProjectionForeshortensTowardItsRadialWidth()
    {
        EmitterPhotometry lantern = new(
            "projection-fixture",
            6.5f,
            0.015f,
            0.040f,
            0.020f,
            0.527f,
            0.413f);

        Assert.AreEqual(0.040f, lantern.ProjectedVerticalHalfExtentMetres(0.0f), 0.000001f);
        Assert.AreEqual(0.015f, lantern.ProjectedVerticalHalfExtentMetres(1.0f), 0.000001f);
        Assert.AreEqual(0.015f, lantern.ProjectedVerticalHalfExtentMetres(-2.0f), 0.000001f);
        Assert.AreEqual(
            MathF.Sqrt((0.040f * 0.040f + 0.015f * 0.015f) * 0.5f),
            lantern.ProjectedVerticalHalfExtentMetres(MathF.Sqrt(0.5f)),
            0.000001f);
        Assert.AreEqual(0.040f, lantern.ProjectedVerticalHalfExtentMetres(float.NaN), 0.000001f);
    }

    /// <summary>Verifies authored metadata validation, HSV scaling, and optional chromaticity.</summary>
    [TestMethod]
    public void AuthoredEmitterMetadataScalesReferenceCandela()
    {
        JsonObject attributes = Json(
            """
            {
              "vintageRtxEmitter": {
                "basis": "measured-fixture",
                "luminousIntensityCd": 6.5,
                "referenceLightHsvValue": 20,
                "sourceHalfWidthM": 0.015,
                "sourceHalfHeightM": 0.040,
                "cutoffIlluminanceLux": 0.02,
                "chromaticityX": 0.55,
                "chromaticityY": 0.41
              }
            }
            """);

        Assert.IsTrue(EmitterPhotometry.TryRead(attributes, 10, out EmitterPhotometry profile));
        Assert.AreEqual("measured-fixture", profile.Basis);
        Assert.AreEqual(3.25f, profile.LuminousIntensityCandela, 0.0001f);
        Assert.AreEqual(0.015f, profile.SourceHalfWidthMetres, 0.0001f);
        Assert.AreEqual(0.040f, profile.SourceHalfHeightMetres, 0.0001f);
        Assert.AreEqual(0.02f, profile.CutoffIlluminanceLux, 0.0001f);

        JsonObject noChromaticity = Json(
            """
            {
              "vintageRtxEmitter": {
                "luminousIntensityCd": 1.0,
                "referenceLightHsvValue": 5,
                "sourceHalfWidthM": 0.01,
                "sourceHalfHeightM": 0.02
              }
            }
            """);
        Assert.IsTrue(EmitterPhotometry.TryRead(noChromaticity, 5, out EmitterPhotometry neutral));
        Assert.IsFalse(neutral.HasChromaticity);
        Assert.IsFalse(neutral.TryGetSrgb(out _, out _, out _));
    }

    /// <summary>Rejects missing, incomplete, non-positive, and incoherent emitter metadata.</summary>
    [TestMethod]
    public void AuthoredEmitterMetadataRejectsInvalidPhysicalValues()
    {
        Assert.IsFalse(EmitterPhotometry.TryRead(null, 5, out _));
        Assert.IsFalse(EmitterPhotometry.TryRead(Json("{}"), 5, out _));
        Assert.IsFalse(EmitterPhotometry.TryRead(
            Json("{\"vintageRtxEmitter\":{\"luminousIntensityCd\":1}}"),
            5,
            out _));
        Assert.IsFalse(EmitterPhotometry.TryRead(
            Json(
                """
                {"vintageRtxEmitter":{
                  "basis":" ","luminousIntensityCd":-1,"referenceLightHsvValue":0,
                  "sourceHalfWidthM":0,"sourceHalfHeightM":0.6,"cutoffIlluminanceLux":0,
                  "chromaticityX":0.8,"chromaticityY":0.4}}
                """),
            5,
            out _));
        Assert.IsFalse(EmitterPhotometry.TryRead(
            Json(
                """
                {"vintageRtxEmitter":{
                  "luminousIntensityCd":1,"referenceLightHsvValue":5,
                  "sourceHalfWidthM":0.01,"sourceHalfHeightM":0.02,
                  "chromaticityX":0.55,"chromaticityY":0}}
                """),
            5,
            out _));
    }

    /// <summary>Verifies deterministic finite-source estimates for unprofiled game emitters.</summary>
    [TestMethod]
    public void GameRangeFallbackRetainsPhysicalAttenuationAndEmitterScale()
    {
        EmitterPhotometry firepit = EmitterPhotometry.FromGameLight("game:firepit-lit", 20);
        EmitterPhotometry torch = EmitterPhotometry.FromGameLight("game:torch-up", 20);
        EmitterPhotometry candle = EmitterPhotometry.FromGameLight("game:candle", 20);
        EmitterPhotometry generic = EmitterPhotometry.FromGameLight("other:glow", 0);

        Assert.AreEqual("game-range-calibrated-inverse-square", firepit.Basis);
        Assert.AreEqual(0.18f, firepit.SourceHalfWidthMetres);
        Assert.AreEqual(0.035f, torch.SourceHalfWidthMetres);
        Assert.AreEqual(0.09f, torch.SourceHalfHeightMetres);
        Assert.AreEqual(0.006f, candle.SourceHalfWidthMetres);
        Assert.AreEqual(0.025f, candle.SourceHalfHeightMetres);
        Assert.AreEqual(0.025f, generic.SourceHalfWidthMetres);
        Assert.AreEqual(4.0f, generic.TraceRadiusMetres(), 0.0001f);
    }

    /// <summary>Verifies the bundled block patches expose cited photometry without replacing textures.</summary>
    [TestMethod]
    public void EmitterPatchContainsLanternCandleAndOilLampProfiles()
    {
        string path = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "assets", "vintagertx", "patches",
            "emitter-photometry.json");
        JArray patches = JArray.Parse(File.ReadAllText(path));

        Assert.AreEqual(3, patches.Count);
        JObject lantern = (JObject)patches[0]!["value"]![EmitterPhotometry.AttributeName]!;
        Assert.AreEqual(6.5f, lantern["luminousIntensityCd"]!.Value<float>(), 0.0001f);
        Assert.AreEqual(0.015f, lantern["sourceHalfWidthM"]!.Value<float>(), 0.0001f);
        Assert.AreEqual(0.040f, lantern["sourceHalfHeightM"]!.Value<float>(), 0.0001f);
        Assert.AreEqual(0.020f, lantern["cutoffIlluminanceLux"]!.Value<float>(), 0.0001f);
        Assert.AreEqual(
            "https://light.lbl.gov/pubs/mills_science_fbl_som.pdf",
            lantern["reference"]!.Value<string>());
        Assert.AreEqual("game:lantern", lantern["clientCodeRoots"]![0]!.Value<string>());
        StringAssert.Contains(patches[1]!["value"]![EmitterPhotometry.AttributeName]!["reference"]!
            .Value<string>()!, "10.1016/j.buildenv.2019.106565");
        StringAssert.Contains(patches[2]!["file"]!.Value<string>()!, "oillamp");
    }

    /// <summary>Verifies client reconstruction and variant matching against the server patch bytes.</summary>
    [TestMethod]
    public void ClientEmitterBridgeResolvesLanternVariantsFromPatchSourceOfTruth()
    {
        string path = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "assets", "vintagertx", "patches",
            "emitter-photometry.json");
        IReadOnlyDictionary<string, JObject> profiles = EmitterPhotometryCatalog.Parse(
            File.ReadAllBytes(path));

        Assert.AreEqual(3, profiles.Count);
        Assert.IsTrue(EmitterPhotometryCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game", "lantern-large-up"),
            20,
            profiles,
            out EmitterPhotometry lantern));
        Assert.AreEqual(6.5f, lantern.LuminousIntensityCandela, 0.0001f);
        Assert.AreEqual(0.015f, lantern.SourceHalfWidthMetres, 0.0001f);
        Assert.AreEqual(0.040f, lantern.SourceHalfHeightMetres, 0.0001f);
        Assert.IsFalse(EmitterPhotometryCatalog.TryResolve(null, 20, profiles, out _));
        Assert.IsFalse(EmitterPhotometryCatalog.TryResolve(
            new Vintagestory.API.Common.AssetLocation("game", "unrelated"),
            20,
            profiles,
            out _));
    }

    /// <summary>Rejects malformed or duplicate client bindings before they reach the renderer.</summary>
    [TestMethod]
    public void ClientEmitterBridgeRejectsIncompleteAndDuplicateBindings()
    {
        Assert.ThrowsException<ArgumentNullException>(() => EmitterPhotometryCatalog.Parse(null!));
        Assert.ThrowsException<InvalidDataException>(() => EmitterPhotometryCatalog.Parse(
            System.Text.Encoding.UTF8.GetBytes(
                "[{\"file\":\"game:test\",\"value\":{\"vintageRtxEmitter\":{}}}]")));

        byte[] duplicate = System.Text.Encoding.UTF8.GetBytes(
            """
            [
              {"file":"game:a","value":{"vintageRtxEmitter":{
                "luminousIntensityCd":1,"referenceLightHsvValue":5,
                "sourceHalfWidthM":0.01,"sourceHalfHeightM":0.02,
                "clientCodeRoots":["game:same"]}}},
              {"file":"game:b","value":{"vintageRtxEmitter":{
                "luminousIntensityCd":1,"referenceLightHsvValue":5,
                "sourceHalfWidthM":0.01,"sourceHalfHeightM":0.02,
                "clientCodeRoots":["game:same"]}}}
            ]
            """);
        Assert.ThrowsException<InvalidDataException>(() => EmitterPhotometryCatalog.Parse(duplicate));
    }

    /// <summary>Verifies every local-light shader path shares one SI inverse-square implementation.</summary>
    [TestMethod]
    public void DisplayShaderUsesSharedPhotometricTransport()
    {
        string shaderPath = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "assets", "vintagertx", "shaders", "display.frag");
        string shader = File.ReadAllText(shaderPath);

        StringAssert.Contains(shader, "uniform vec4 voxelLightPhotometry[8]");
        StringAssert.Contains(shader, "PHOTOMETRIC_LUX_TO_RENDERER_RADIANCE");
        StringAssert.Contains(shader, "float photometricIncidentRadiance(");
        StringAssert.Contains(shader, "float physicalLightRange(");
        StringAssert.Contains(shader, "float projectedHalfHeight = sqrt(");
        Assert.IsTrue(Count(shader, "photometricIncidentRadiance(") >= 5);
        Assert.IsFalse(shader.Contains(
            "pointLightShadowStrength * 0.35",
            StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains(
            "1.0 + 0.075 * lightDistance * lightDistance",
            StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains(
            "1.0 + 0.075 * distanceToLight * distanceToLight",
            StringComparison.Ordinal));
    }

    /// <summary>Counts exact ordinal occurrences in one source document.</summary>
    /// <param name="text">Source document.</param>
    /// <param name="value">Non-empty token.</param>
    /// <returns>Number of non-overlapping occurrences.</returns>
    private static int Count(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    /// <summary>Creates a Vintage Story JSON wrapper from a strict JSON fixture.</summary>
    /// <param name="json">Strict JSON object.</param>
    /// <returns>Wrapped object.</returns>
    private static JsonObject Json(string json) => new(JObject.Parse(json));
}
