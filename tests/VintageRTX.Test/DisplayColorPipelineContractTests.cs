using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;

namespace VintageRTX.Test;

/// <summary>Verifies the pure 1.22.7 pre-final/display colour-source contract.</summary>
[TestClass]
public sealed class DisplayColorPipelineContractTests
{
    /// <summary>Checks the engine stages are not conflated with GL attachment encoding.</summary>
    [TestMethod]
    public void RelevantStagesExposePreFinalThenDisplayReferredSignals()
    {
        Assert.AreEqual(
            DisplayColorSignal.SceneReferredPreFinal,
            DisplayColorPipelineContract.ExpectedSignal(EnumRenderStage.AfterPostProcessing));
        Assert.AreEqual(
            DisplayColorSignal.DisplayReferred,
            DisplayColorPipelineContract.ExpectedSignal(EnumRenderStage.AfterBlit));
        Assert.AreEqual(
            DisplayColorSignal.Unknown,
            DisplayColorPipelineContract.ExpectedSignal(EnumRenderStage.Ortho));
        Assert.AreEqual(
            GlColorEncoding.Linear,
            DisplayColorPipelineContract.ClassifyAttachmentEncoding(
                DisplayColorPipelineContract.LinearColorEncoding));
        Assert.AreEqual(
            GlColorEncoding.Srgb,
            DisplayColorPipelineContract.ClassifyAttachmentEncoding(
                DisplayColorPipelineContract.SrgbColorEncoding));
        Assert.AreEqual(
            GlColorEncoding.Unknown,
            DisplayColorPipelineContract.ClassifyAttachmentEncoding(0));
    }

    /// <summary>Accepts only an exact-size live named Primary colour attachment.</summary>
    [TestMethod]
    public void PrimaryProbeRejectsUnknownLayoutsWithoutTouchingGl()
    {
        List<FrameBufferRef> frameBuffers = CreateRegistry(new FrameBufferRef
        {
            FboId = 41,
            Width = 1280,
            Height = 720,
            ColorTextureIds = [73]
        });

        Assert.IsTrue(DisplayColorPipelineContract.TryResolvePrimary(
            frameBuffers,
            1280,
            720,
            out FrameBufferRef? primary,
            out int textureId));
        Assert.AreSame(frameBuffers[(int)EnumFrameBuffer.Primary], primary);
        Assert.AreEqual(73, textureId);

        Assert.IsFalse(DisplayColorPipelineContract.TryResolvePrimary(
            frameBuffers,
            0,
            720,
            out _,
            out _));
        Assert.IsFalse(DisplayColorPipelineContract.TryResolvePrimary(
            frameBuffers,
            1280,
            0,
            out _,
            out _));

        frameBuffers[(int)EnumFrameBuffer.Primary].Width = 1279;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolvePrimary(
            frameBuffers,
            1280,
            720,
            out _,
            out _));
        frameBuffers[(int)EnumFrameBuffer.Primary].Width = 1280;
        frameBuffers[(int)EnumFrameBuffer.Primary].FboId = 0;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolvePrimary(
            frameBuffers,
            1280,
            720,
            out _,
            out _));
        Assert.IsFalse(DisplayColorPipelineContract.TryResolvePrimary(
            [],
            1280,
            720,
            out _,
            out _));
    }

    /// <summary>Resolves the exact Luma input used by final.fsh and rejects stale dimensions.</summary>
    [TestMethod]
    public void LumaProbeUsesThePublicFinalShaderInputSlot()
    {
        FrameBufferRef luma = new()
        {
            FboId = 52,
            Width = 1920,
            Height = 1080,
            ColorTextureIds = [91]
        };
        List<FrameBufferRef> frameBuffers = CreateRegistry(
            new FrameBufferRef
            {
                FboId = 41,
                Width = 1920,
                Height = 1080,
                ColorTextureIds = [73]
            },
            luma);

        Assert.IsTrue(DisplayColorPipelineContract.TryResolveLuma(
            frameBuffers,
            1920,
            1080,
            out FrameBufferRef? resolved,
            out int textureId));
        Assert.AreSame(luma, resolved);
        Assert.AreEqual(91, textureId);

        luma.Height = 1079;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLuma(
            frameBuffers,
            1920,
            1080,
            out _,
            out _));
    }

    /// <summary>Accepts only the exact public liquid-depth attachment used by chunkliquiddepth.</summary>
    [TestMethod]
    public void LiquidDepthProbeRequiresLiveExactSizeDepthStorage()
    {
        List<FrameBufferRef> frameBuffers = [];
        for (int index = 0; index <= (int)EnumFrameBuffer.LiquidDepth; index++)
        {
            frameBuffers.Add(new FrameBufferRef());
        }
        FrameBufferRef liquidDepth = new()
        {
            FboId = 64,
            Width = 1920,
            Height = 1080,
            DepthTextureId = 117,
            ColorTextureIds = [118]
        };
        frameBuffers[(int)EnumFrameBuffer.LiquidDepth] = liquidDepth;

        Assert.IsTrue(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            1920,
            1080,
            out FrameBufferRef? resolved,
            out int textureId));
        Assert.AreSame(liquidDepth, resolved);
        Assert.AreEqual(117, textureId);

        liquidDepth.DepthTextureId = 0;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            1920,
            1080,
            out _,
            out _));
        liquidDepth.DepthTextureId = 117;
        liquidDepth.Width = 960;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            1920,
            1080,
            out _,
            out _));

        liquidDepth.Width = 1920;
        liquidDepth.Height = 720;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            1920,
            1080,
            out _,
            out _));
        liquidDepth.Height = 1080;
        liquidDepth.FboId = 0;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            1920,
            1080,
            out _,
            out _));
        liquidDepth.FboId = 64;
        liquidDepth.Disposed = true;
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            1920,
            1080,
            out _,
            out _));
        liquidDepth.Disposed = false;

        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            0,
            1080,
            out _,
            out _));
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            frameBuffers,
            1920,
            0,
            out _,
            out _));
        Assert.IsFalse(DisplayColorPipelineContract.TryResolveLiquidDepth(
            [],
            1920,
            1080,
            out _,
            out _));
    }

    /// <summary>Creates a framebuffer registry containing one Primary entry.</summary>
    /// <param name="primary">Primary framebuffer to expose.</param>
    /// <param name="luma">Optional Luma framebuffer exposed at its public registry slot.</param>
    /// <returns>Registry indexed by the public framebuffer enum.</returns>
    private static List<FrameBufferRef> CreateRegistry(
        FrameBufferRef primary,
        FrameBufferRef? luma = null)
    {
        List<FrameBufferRef> result = [];
        int maximumIndex = luma is null
            ? (int)EnumFrameBuffer.Primary
            : (int)EnumFrameBuffer.Luma;
        for (int index = 0; index <= maximumIndex; index++)
        {
            result.Add(new FrameBufferRef());
        }
        result[(int)EnumFrameBuffer.Primary] = primary;
        if (luma is not null)
        {
            result[(int)EnumFrameBuffer.Luma] = luma;
        }
        return result;
    }
}
