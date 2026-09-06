using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>Locks the targeted outdoor tallgrass runtime evidence without launching the game.</summary>
[TestClass]
public sealed class RuntimeVegetationShadowValidationTests
{
    /// <summary>Every render-lab profile must wait for both real vegetation and native cascades.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void RenderLabProfilesRequireNativeSolarDetailAndTallgrassEvidence()
    {
        ScenarioDefinition[] renderLabScenarios = ScenarioCatalog.AutomatedScenarios
            .Where(scenario => string.Equals(
                scenario.RuntimeProbe,
                "render-lab",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.IsTrue(renderLabScenarios.Length >= 7, "All authored render-lab profiles must be covered.");
        foreach (ScenarioDefinition scenario in renderLabScenarios)
        {
            CollectionAssert.Contains(
                scenario.RequiredLogTokens,
                "[VintageRTX] Native solar shadow detail active:",
                scenario.Name);
            CollectionAssert.Contains(
                scenario.RequiredLogTokens,
                "Geometry evidence game:tallgrass-tall-free: kind=StaticComplex, detailed non-cube=True, crossed planes=True",
                scenario.Name);
        }
    }

    /// <summary>An irregular, alpha-cut blade shadow passes the normalized render-lab ROI gate.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void AlphaCutLocalizedTallgrassShadowPassesMorphologyGate()
    {
        using SKBitmap shadow = CreateShadowBitmap();
        for (int step = 0; step < 20; step++)
        {
            int x = 102 + step;
            int y = 101 + step / 5;
            PaintSolarShadow(shadow, x, y);
            if (step is >= 4 and <= 18 && step % 3 != 0)
            {
                PaintSolarShadow(shadow, x + 3, y + 2);
            }
            if (step is >= 7 and <= 18 && step % 4 != 1)
            {
                PaintSolarShadow(shadow, x - 2, y + 4);
            }
        }

        RuntimeImageValidator.VegetationShadowAssessment assessment =
            RuntimeImageValidator.MeasureRenderLabVegetationSunShadow(shadow);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsTrue(
            assessment.MeetsGate,
            $"pixels={assessment.CandidatePixels}, ROI={assessment.RoiCoverage:P2}, "
            + $"fill={assessment.RectangularFill:P1}, gaps={assessment.InteriorGapRatio:P1}, "
            + $"row variation={assessment.RowOccupancyVariation:0.00}");
        Assert.IsTrue(assessment.CandidatePixels >= 20);
        Assert.IsTrue(assessment.RectangularFill < 0.76);
    }

    /// <summary>A solid block footprint is rejected even when it is localized in the grass ROI.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void SolidSquareCasterFallbackFailsMorphologyGate()
    {
        using SKBitmap shadow = CreateShadowBitmap();
        for (int y = 101; y < 111; y++)
        {
            for (int x = 104; x < 116; x++)
            {
                PaintSolarShadow(shadow, x, y);
            }
        }

        RuntimeImageValidator.VegetationShadowAssessment assessment =
            RuntimeImageValidator.MeasureRenderLabVegetationSunShadow(shadow);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsFalse(assessment.MeetsGate);
        Assert.AreEqual(1.0, assessment.RectangularFill, 0.0001);
    }

    /// <summary>Native-looking green without cascade support cannot impersonate alpha-tested evidence.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void UnsupportedNativeShadowPixelsFailMorphologyGate()
    {
        using SKBitmap shadow = CreateShadowBitmap();
        for (int x = 104; x < 124; x++)
        {
            shadow.SetPixel(x, 104 + (x & 3), new SKColor(0, 210, 0, 255));
        }

        RuntimeImageValidator.VegetationShadowAssessment assessment =
            RuntimeImageValidator.MeasureRenderLabVegetationSunShadow(shadow);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsFalse(assessment.MeetsGate);
        Assert.AreEqual(0, assessment.CandidatePixels);
    }

    /// <summary>The dedicated validator requires the exact native-cascade activation evidence.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void DedicatedRuntimeGateRejectsMissingNativeCascadeLog()
    {
        string log =
            "[VintageRTX.Test] Render lab camera applied: close=true\n"
            + "[VintageRTX.Test] Geometry evidence game:tallgrass-tall-free: kind=StaticComplex, detailed non-cube=True, crossed planes=True\n";

        IReadOnlyList<string> failures =
            RuntimeImageValidator.ValidateRenderLabVegetationSunShadow(log);

        Assert.IsTrue(failures.Any(failure => failure.Contains(
            "Native solar shadow detail active",
            StringComparison.Ordinal)));
    }

    /// <summary>Creates a black diagnostic image at the lowest supported runtime aspect.</summary>
    /// <returns>Mutable synthetic voxel-shadow diagnostic.</returns>
    private static SKBitmap CreateShadowBitmap()
    {
        SKBitmap bitmap = new(320, 180, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Black);
        return bitmap;
    }

    /// <summary>Writes one projected-sun-shadow sample into the shader ABI's green channel.</summary>
    /// <param name="bitmap">Synthetic voxel-shadow diagnostic.</param>
    /// <param name="x">Horizontal pixel coordinate.</param>
    /// <param name="y">Vertical pixel coordinate.</param>
    private static void PaintSolarShadow(SKBitmap bitmap, int x, int y)
    {
        if ((uint)x < (uint)bitmap.Width && (uint)y < (uint)bitmap.Height)
        {
            bitmap.SetPixel(x, y, new SKColor(0, 210, 255, 255));
        }
    }
}
