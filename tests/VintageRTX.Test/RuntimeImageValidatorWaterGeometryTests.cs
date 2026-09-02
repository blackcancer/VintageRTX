using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>Deterministic tests for shared-cell liquid and non-full geometry validation.</summary>
[TestClass]
public sealed class RuntimeImageValidatorWaterGeometryTests
{
    /// <summary>Rejects a near-neutral diagnostic patch occupying a complete block-scale area.</summary>
    [TestMethod]
    public void BlockScaleLiquidFaceProducesActionableDensity()
    {
        using SKBitmap water = CreatePhysicalWaterDiagnostic();
        Fill(water, 8, 8, 8, 8, new SKColor(224, 224, 224));

        double density = RuntimeImageValidator
            .MeasureUnresolvedSharedCellLiquidFaceDensity(water);

        Assert.AreEqual(0.0625, density, 0.00001);
        Assert.IsTrue(density > 0.01);
    }

    /// <summary>Allows sparse alpha-tested fragments without hiding the physical water surface.</summary>
    [TestMethod]
    public void SparsePartialSilhouetteDoesNotLookLikeAWholeWaterBlock()
    {
        using SKBitmap water = CreatePhysicalWaterDiagnostic();
        for (int index = 0; index < 8; index++)
        {
            water.SetPixel(8 + index * 2, 10 + index, new SKColor(224, 224, 224));
        }

        double density = RuntimeImageValidator
            .MeasureUnresolvedSharedCellLiquidFaceDensity(water);

        Assert.AreEqual(8.0 / (32.0 * 32.0), density, 0.00001);
        Assert.IsTrue(density <= 0.01);
    }

    /// <summary>Returns zero when a diagnostic contains no visible liquid receiver.</summary>
    [TestMethod]
    public void EmptyDiagnosticHasNoUnresolvedLiquidFace()
    {
        using SKBitmap water = new(16, 16);

        Assert.AreEqual(
            0.0,
            RuntimeImageValidator.MeasureUnresolvedSharedCellLiquidFaceDensity(water),
            0.0);
    }

    /// <summary>Creates a physical-water field whose path channel stays below the face marker.</summary>
    /// <returns>A deterministic 32 by 32 water diagnostic.</returns>
    private static SKBitmap CreatePhysicalWaterDiagnostic()
    {
        SKBitmap water = new(32, 32);
        Fill(water, 0, 0, water.Width, water.Height, new SKColor(220, 160, 150));
        return water;
    }

    /// <summary>Fills one rectangular region without depending on a raster canvas.</summary>
    /// <param name="bitmap">Destination bitmap.</param>
    /// <param name="left">Inclusive left coordinate.</param>
    /// <param name="top">Inclusive top coordinate.</param>
    /// <param name="width">Rectangle width.</param>
    /// <param name="height">Rectangle height.</param>
    /// <param name="color">Pixel color.</param>
    private static void Fill(
        SKBitmap bitmap,
        int left,
        int top,
        int width,
        int height,
        SKColor color)
    {
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < left + width; x++)
            {
                bitmap.SetPixel(x, y, color);
            }
        }
    }
}
