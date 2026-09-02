using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Locks the RGB ownership contract of the voxel-shadow diagnostic.</summary>
[TestClass]
public sealed class RuntimeImageValidatorShadowChannelTests
{
    /// <summary>Each scenario reads only the shadow channel assigned by the shader contract.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("Shadow")]
    public void ShadowValidationModesSelectTheirOwnedDebugChannel()
    {
        SKColor diagnostic = new(51, 102, 204);

        Assert.AreEqual(
            0.80,
            RuntimeImageValidator.SelectShadowDebugEnergy(diagnostic, ShadowValidation.CameraAligned),
            1e-12);
        Assert.AreEqual(
            0.40,
            RuntimeImageValidator.SelectShadowDebugEnergy(diagnostic, ShadowValidation.SunProjected),
            1e-12);
        Assert.AreEqual(
            0.20,
            RuntimeImageValidator.SelectShadowDebugEnergy(diagnostic, ShadowValidation.Projected),
            1e-12);
        Assert.AreEqual(
            0.20,
            RuntimeImageValidator.SelectShadowDebugEnergy(diagnostic, ShadowValidation.Informational),
            1e-12);
    }

    /// <summary>The shader writes aggregate point, sun and held-camera shadows to R, G and B respectively.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadow")]
    public void DisplayShaderAssignsCameraAlignedShadowOnlyToBlue()
    {
        string fragment = DisplayShaderSource.LoadFromFileSystem(AppContext.BaseDirectory)
            .Fragment
            .ReplaceLineEndings("\n");
        int debugBlock = fragment.IndexOf("if (debugView == 6)", StringComparison.Ordinal);
        Assert.IsTrue(debugBlock >= 0);
        int redChannel = fragment.IndexOf(
            "clamp(voxelLighting.shadow, 0.0, 1.0)",
            debugBlock,
            StringComparison.Ordinal);
        int greenChannel = fragment.IndexOf(
            "clamp(normalizedSunShadow, 0.0, 1.0)",
            debugBlock,
            StringComparison.Ordinal);
        int blueChannel = fragment.IndexOf(
            "clamp(voxelLighting.cameraAlignedShadow, 0.0, 1.0)",
            debugBlock,
            StringComparison.Ordinal);
        int debugBlockEnd = fragment.IndexOf("if (debugView == 7)", debugBlock, StringComparison.Ordinal);

        Assert.IsTrue(redChannel > debugBlock);
        Assert.IsTrue(greenChannel > redChannel);
        Assert.IsTrue(blueChannel > greenChannel);
        Assert.IsTrue(debugBlockEnd > blueChannel);
        int cameraAlignedExpression = blueChannel + "clamp(".Length;
        Assert.AreEqual(
            cameraAlignedExpression,
            fragment.IndexOf("voxelLighting.cameraAlignedShadow", debugBlock, StringComparison.Ordinal));
        Assert.AreEqual(
            -1,
            fragment.IndexOf(
                "voxelLighting.cameraAlignedShadow",
                cameraAlignedExpression + "voxelLighting.cameraAlignedShadow".Length,
                debugBlockEnd
                    - cameraAlignedExpression
                    - "voxelLighting.cameraAlignedShadow".Length,
                StringComparison.Ordinal));
    }
}
