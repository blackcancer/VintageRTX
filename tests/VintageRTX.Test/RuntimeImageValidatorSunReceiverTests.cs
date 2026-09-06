using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>Exercises physically targeted exterior sun-shadow image measurements.</summary>
[TestClass]
public sealed class RuntimeImageValidatorSunReceiverTests
{
    /// <summary>The central projected footprint is measured separately from unrelated landscape.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void ProjectedTargetDiscSeparatesLocalRoofShadowFromGlobalGround()
    {
        using SKBitmap normal = new(100, 100);
        using SKBitmap shadow = new(100, 100);
        SKColor upward = new(128, 255, 128, 255);
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                normal.SetPixel(x, y, upward);
                bool centralShadow = Math.Abs(x - 50) <= 8 && Math.Abs(y - 50) <= 8;
                shadow.SetPixel(x, y, centralShadow
                    ? new SKColor(0, 220, 0, 255)
                    : SKColors.Black);
            }
        }

        RuntimeImageValidator.SunReceiverCoverageAssessment assessment =
            RuntimeImageValidator.MeasureSunReceiverCoverage(normal, shadow, 0.18);

        Assert.AreEqual(1.0, assessment.GroundCoverage, 0.0001);
        Assert.IsTrue(assessment.ShadowedGroundRatio < 0.05);
        Assert.IsTrue(assessment.TargetGroundSamples > 128);
        Assert.IsTrue(assessment.TargetShadowedGroundRatio > 0.20);
    }

    /// <summary>Invalid pairing and radius contracts fail before any pixel traversal.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void ProjectedTargetDiscRejectsInvalidInputs()
    {
        using SKBitmap square = new(16, 16);
        using SKBitmap wide = new(17, 16);
        Assert.ThrowsException<ArgumentNullException>(() =>
            RuntimeImageValidator.MeasureSunReceiverCoverage(null!, square, 0.18));
        Assert.ThrowsException<ArgumentNullException>(() =>
            RuntimeImageValidator.MeasureSunReceiverCoverage(square, null!, 0.18));
        Assert.ThrowsException<ArgumentException>(() =>
            RuntimeImageValidator.MeasureSunReceiverCoverage(square, wide, 0.18));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            RuntimeImageValidator.MeasureSunReceiverCoverage(square, square, 0.0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            RuntimeImageValidator.MeasureSunReceiverCoverage(square, square, double.NaN));
    }
}
