using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>Exercises fixed-camera temporal light and shadow measurements.</summary>
[TestClass]
public sealed class RuntimeImageValidatorLightStabilityTests
{
    /// <summary>The worst pairwise frame change is retained for both luma and point-shadow modes.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void TemporalTripletReportsWorstPairwiseVariation()
    {
        using SKBitmap first = SolidBitmap(SKColors.Black);
        using SKBitmap second = SolidBitmap(new SKColor(26, 26, 26, 255));
        using SKBitmap third = SolidBitmap(new SKColor(13, 13, 13, 255));

        RuntimeImageValidator.TemporalTripletAssessment luma =
            RuntimeImageValidator.MeasureTemporalTriplet(
                first,
                second,
                third,
                redChannelOnly: false,
                changeThreshold: 0.025);
        RuntimeImageValidator.TemporalTripletAssessment shadow =
            RuntimeImageValidator.MeasureTemporalTriplet(
                first,
                second,
                third,
                redChannelOnly: true,
                changeThreshold: 0.05);

        Assert.IsTrue(luma.Compatible);
        Assert.AreEqual(26.0 / 255.0, luma.MaximumMeanAbsoluteDelta, 0.0001);
        Assert.AreEqual(1.0, luma.MaximumChangedPixelRatio, 0.0001);
        Assert.AreEqual(1.0, luma.StableSampleRatio, 0.0001);
        Assert.AreEqual(luma, shadow);
    }

    /// <summary>Vanilla motion excludes moving geometry without hiding a change on a static receiver.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void MotionRejectedTripletMeasuresOnlyVanillaStableReceivers()
    {
        using SKBitmap baselineA = SolidBitmap(SKColors.Black);
        using SKBitmap baselineB = SolidBitmap(SKColors.Black);
        using SKBitmap baselineC = SolidBitmap(SKColors.Black);
        using SKBitmap effectA = SolidBitmap(SKColors.Black);
        using SKBitmap effectB = SolidBitmap(SKColors.Black);
        using SKBitmap effectC = SolidBitmap(SKColors.Black);

        for (int y = 0; y < 8; y += 2)
        {
            baselineB.SetPixel(0, y, SKColors.White);
            effectB.SetPixel(0, y, SKColors.White);
        }
        effectC.SetPixel(2, 0, new SKColor(51, 51, 51, 255));

        RuntimeImageValidator.TemporalTripletAssessment assessment =
            RuntimeImageValidator.MeasureMotionRejectedTemporalTriplet(
                effectA,
                effectB,
                effectC,
                baselineA,
                baselineB,
                baselineC,
                redChannelOnly: false,
                changeThreshold: 0.05,
                baselineMotionThreshold: 0.08,
                compareEffectDeltaFromBaseline: false,
                baselineMotionHaloPixels: 0);

        Assert.IsTrue(assessment.Compatible);
        Assert.AreEqual(0.75, assessment.StableSampleRatio, 0.0001);
        Assert.AreEqual(0.2 / 12.0, assessment.MaximumMeanAbsoluteDelta, 0.0001);
        Assert.AreEqual(1.0 / 12.0, assessment.MaximumChangedPixelRatio, 0.0001);
    }

    /// <summary>The simultaneous Vanilla carrier is subtracted before comparing RTX corrections.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void MotionRejectedTripletCanCompareOnlyTheRtxCorrection()
    {
        using SKBitmap baselineA = SolidBitmap(SKColors.Black);
        using SKBitmap baselineB = SolidBitmap(new SKColor(51, 51, 51, 255));
        using SKBitmap baselineC = SolidBitmap(new SKColor(102, 102, 102, 255));
        using SKBitmap effectA = SolidBitmap(new SKColor(26, 26, 26, 255));
        using SKBitmap effectB = SolidBitmap(new SKColor(77, 77, 77, 255));
        using SKBitmap effectC = SolidBitmap(new SKColor(128, 128, 128, 255));

        RuntimeImageValidator.TemporalTripletAssessment assessment =
            RuntimeImageValidator.MeasureMotionRejectedTemporalTriplet(
                effectA,
                effectB,
                effectC,
                baselineA,
                baselineB,
                baselineC,
                redChannelOnly: false,
                changeThreshold: 0.025,
                baselineMotionThreshold: 1.0,
                compareEffectDeltaFromBaseline: true,
                baselineMotionHaloPixels: 0);

        Assert.IsTrue(assessment.Compatible);
        Assert.AreEqual(1.0, assessment.StableSampleRatio, 0.0001);
        Assert.AreEqual(0.0, assessment.MaximumMeanAbsoluteDelta, 0.0001);
        Assert.AreEqual(26.0 / 255.0, assessment.FirstMean, 0.0001);
    }

    /// <summary>A one-sample halo rejects receiver pixels adjacent to proven Vanilla motion.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void MotionRejectedTripletErodesTheStableMaskAroundMotion()
    {
        using SKBitmap baselineA = SolidBitmap(SKColors.Black);
        using SKBitmap baselineB = SolidBitmap(SKColors.Black);
        using SKBitmap baselineC = SolidBitmap(SKColors.Black);
        using SKBitmap effectA = SolidBitmap(SKColors.Black);
        using SKBitmap effectB = SolidBitmap(SKColors.Black);
        using SKBitmap effectC = SolidBitmap(SKColors.Black);
        baselineB.SetPixel(2, 2, SKColors.White);

        RuntimeImageValidator.TemporalTripletAssessment assessment =
            RuntimeImageValidator.MeasureMotionRejectedTemporalTriplet(
                effectA,
                effectB,
                effectC,
                baselineA,
                baselineB,
                baselineC,
                redChannelOnly: true,
                changeThreshold: 0.05,
                baselineMotionThreshold: 0.08,
                compareEffectDeltaFromBaseline: false,
                baselineMotionHaloPixels: 2);

        Assert.IsTrue(assessment.Compatible);
        Assert.AreEqual(7.0 / 16.0, assessment.StableSampleRatio, 0.0001);
        Assert.AreEqual(0.0, assessment.MaximumMeanAbsoluteDelta, 0.0001);
    }

    /// <summary>Dimension and threshold contract violations return an incompatible zero assessment.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadows")]
    public void TemporalTripletRejectsIncompatibleInputs()
    {
        using SKBitmap square = SolidBitmap(SKColors.Black);
        using SKBitmap wide = new(9, 8);

        Assert.IsFalse(RuntimeImageValidator.MeasureTemporalTriplet(
            square,
            wide,
            square,
            redChannelOnly: false,
            changeThreshold: 0.025).Compatible);
        Assert.IsFalse(RuntimeImageValidator.MeasureTemporalTriplet(
            square,
            square,
            square,
            redChannelOnly: false,
            changeThreshold: double.NaN).Compatible);
        Assert.IsFalse(RuntimeImageValidator.MeasureTemporalTriplet(
            square,
            square,
            square,
            redChannelOnly: false,
            changeThreshold: 1.01).Compatible);
        Assert.IsFalse(RuntimeImageValidator.MeasureMotionRejectedTemporalTriplet(
            square,
            square,
            square,
            square,
            wide,
            square,
            redChannelOnly: false,
            changeThreshold: 0.025,
            baselineMotionThreshold: 0.08).Compatible);
        Assert.IsFalse(RuntimeImageValidator.MeasureMotionRejectedTemporalTriplet(
            square,
            square,
            square,
            square,
            square,
            square,
            redChannelOnly: false,
            changeThreshold: 0.025,
            baselineMotionThreshold: double.NaN).Compatible);
    }

    /// <summary>Creates a small deterministic opaque fixture.</summary>
    private static SKBitmap SolidBitmap(SKColor color)
    {
        SKBitmap bitmap = new(8, 8);
        bitmap.Erase(color);
        return bitmap;
    }
}
