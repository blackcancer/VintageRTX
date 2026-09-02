using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>
/// Verifies the rain-height exposure contract shared by Vintage Story world data and the display
/// shader without requiring a live OpenGL context.
/// </summary>
[TestClass]
public sealed class RainWetnessShaderTests
{
    /// <summary>
    /// Verifies that the public rain-map block coordinate exposes its own upward receiver while a
    /// lower receiver or vertical face remains dry.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Weather")]
    public void RainHeightUsesTopmostBlockingBlockCoordinate()
    {
        string shader = DisplayShaderSource
            .LoadFromFileSystem(AppContext.BaseDirectory)
            .Fragment
            .ReplaceLineEndings("\n");
        StringAssert.Contains(
            shader,
            "float rainReceiverY = worldPosition.y + worldNormal.y * 0.035;");
        StringAssert.Contains(shader, "rainBlockY - 0.08");
        StringAssert.Contains(shader, "rainBlockY + 0.08");
        Assert.IsFalse(shader.Contains("rainBlockY + 0.48", StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains("rainBlockY + 0.92", StringComparison.Ordinal));

        double sameBlockExposure = EvaluateRainExposure(105.0, 1.0, 105.0);
        double upperFaceExposure = EvaluateRainExposure(106.0, 1.0, 105.0);
        double shelteredExposure = EvaluateRainExposure(113.0, 1.0, 121.0);
        double verticalFaceExposure = EvaluateRainExposure(105.0, 0.0, 105.0);

        Assert.IsTrue(sameBlockExposure >= 0.75);
        Assert.AreEqual(1.0, upperFaceExposure, 0.0);
        Assert.AreEqual(0.0, shelteredExposure, 0.0);
        Assert.AreEqual(0.0, verticalFaceExposure, 0.0);
    }

    /// <summary>Evaluates the shader's height and upward-normal exposure factors.</summary>
    /// <param name="worldY">Interpolated receiver Y in world blocks.</param>
    /// <param name="worldNormalY">Receiver world-normal Y component.</param>
    /// <param name="rainBlockY">Highest non-rain-permeable block-coordinate Y.</param>
    /// <returns>Bounded physical rain exposure.</returns>
    private static double EvaluateRainExposure(
        double worldY,
        double worldNormalY,
        double rainBlockY)
    {
        double receiverY = worldY + worldNormalY * 0.035;
        double heightExposure = SmoothStep(rainBlockY - 0.08, rainBlockY + 0.08, receiverY);
        double upwardExposure = SmoothStep(0.50, 0.92, worldNormalY);
        return heightExposure * upwardExposure;
    }

    /// <summary>Evaluates GLSL smoothstep with its clamped Hermite polynomial.</summary>
    /// <param name="minimum">Lower transition edge.</param>
    /// <param name="maximum">Upper transition edge.</param>
    /// <param name="value">Sample value.</param>
    /// <returns>Zero below the interval, one above it, and a smooth cubic transition inside.</returns>
    private static double SmoothStep(double minimum, double maximum, double value)
    {
        double progress = Math.Clamp((value - minimum) / (maximum - minimum), 0.0, 1.0);
        return progress * progress * (3.0 - 2.0 * progress);
    }
}
