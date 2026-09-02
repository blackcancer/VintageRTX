using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;

namespace VintageRTX.Test;

/// <summary>Verifies the CPU-only Luma bridge layout and luminance contracts.</summary>
[TestClass]
public sealed class LumaBridgeContractTests
{
    /// <summary>Matches Vintage Story's luma shader for primaries and neutral grey.</summary>
    [TestMethod]
    public void ComputesExactEngineLumaForPrimariesAndGrey()
    {
        Assert.AreEqual(0.299f, LumaBridgeContract.ComputeLuma(1.0f, 0.0f, 0.0f), 0.000001f);
        Assert.AreEqual(0.587f, LumaBridgeContract.ComputeLuma(0.0f, 1.0f, 0.0f), 0.000001f);
        Assert.AreEqual(0.114f, LumaBridgeContract.ComputeLuma(0.0f, 0.0f, 1.0f), 0.000001f);
        Assert.AreEqual(0.5f, LumaBridgeContract.ComputeLuma(0.5f, 0.5f, 0.5f), 0.000001f);
    }

    /// <summary>Resolves an exact-size, live and non-aliasing Luma destination.</summary>
    [TestMethod]
    public void ResolvesDistinctOwnedSourceAndPublicLumaDestination()
    {
        List<FrameBufferRef> frameBuffers = Registry(Luma());

        Assert.IsTrue(LumaBridgeContract.TryResolve(
            frameBuffers,
            1280,
            720,
            71,
            72,
            out LumaBridgeLayout layout,
            out string reason));
        Assert.AreEqual("ready", reason);
        Assert.AreEqual(71, layout.SourceFramebufferId);
        Assert.AreEqual(72, layout.SourceTextureId);
        Assert.AreEqual(81, layout.DestinationFramebufferId);
        Assert.AreEqual(82, layout.DestinationTextureId);
        Assert.AreEqual(1280, layout.Width);
        Assert.AreEqual(720, layout.Height);
    }

    /// <summary>Rejects missing source objects, invalid sizes, and absent registry slots.</summary>
    [TestMethod]
    public void RejectsUnavailableSourceDimensionsAndRegistry()
    {
        AssertRejected(null, 1280, 720, 71, 72, "slot");
        AssertRejected([], 1280, 720, 71, 72, "slot");
        AssertRejected(Registry(Luma()), 0, 720, 71, 72, "dimensions");
        AssertRejected(Registry(Luma()), 1280, 0, 71, 72, "dimensions");
        AssertRejected(Registry(Luma()), 1280, 720, 0, 72, "temporary");
        AssertRejected(Registry(Luma()), 1280, 720, 71, 0, "temporary");
    }

    /// <summary>Rejects null, disposed, unnamed, mismatched, or attachment-less Luma layouts.</summary>
    [TestMethod]
    public void RejectsEveryInvalidLumaLayout()
    {
        List<FrameBufferRef> nullLuma = Registry(Luma());
        nullLuma[(int)EnumFrameBuffer.Luma] = null!;
        AssertRejected(nullLuma, 1280, 720, 71, 72, "null, disposed, or unnamed");

        FrameBufferRef disposed = Luma();
        disposed.Disposed = true;
        AssertRejected(Registry(disposed), 1280, 720, 71, 72, "null, disposed, or unnamed");

        FrameBufferRef unnamed = Luma();
        unnamed.FboId = 0;
        AssertRejected(Registry(unnamed), 1280, 720, 71, 72, "null, disposed, or unnamed");

        FrameBufferRef wrongWidth = Luma();
        wrongWidth.Width--;
        AssertRejected(Registry(wrongWidth), 1280, 720, 71, 72, "dimensions");

        FrameBufferRef wrongHeight = Luma();
        wrongHeight.Height--;
        AssertRejected(Registry(wrongHeight), 1280, 720, 71, 72, "dimensions");

        FrameBufferRef missingArray = Luma();
        missingArray.ColorTextureIds = null!;
        AssertRejected(Registry(missingArray), 1280, 720, 71, 72, "attachment zero");

        FrameBufferRef emptyArray = Luma();
        emptyArray.ColorTextureIds = [];
        AssertRejected(Registry(emptyArray), 1280, 720, 71, 72, "attachment zero");

        FrameBufferRef unnamedTexture = Luma();
        unnamedTexture.ColorTextureIds = [0];
        AssertRejected(Registry(unnamedTexture), 1280, 720, 71, 72, "attachment zero");
    }

    /// <summary>Rejects both framebuffer-name and texture-name feedback aliases.</summary>
    [TestMethod]
    public void RejectsFramebufferAndTextureAliases()
    {
        AssertRejected(Registry(Luma()), 1280, 720, 81, 72, "aliases");
        AssertRejected(Registry(Luma()), 1280, 720, 71, 82, "aliases");
    }

    /// <summary>Asserts one invalid contract call and its stable diagnostic token.</summary>
    /// <param name="frameBuffers">Candidate engine registry.</param>
    /// <param name="width">Candidate width.</param>
    /// <param name="height">Candidate height.</param>
    /// <param name="sourceFramebuffer">Candidate owned framebuffer.</param>
    /// <param name="sourceTexture">Candidate owned texture.</param>
    /// <param name="reasonToken">Expected rejection-message fragment.</param>
    private static void AssertRejected(
        IReadOnlyList<FrameBufferRef>? frameBuffers,
        int width,
        int height,
        int sourceFramebuffer,
        int sourceTexture,
        string reasonToken)
    {
        Assert.IsFalse(LumaBridgeContract.TryResolve(
            frameBuffers,
            width,
            height,
            sourceFramebuffer,
            sourceTexture,
            out LumaBridgeLayout layout,
            out string reason));
        Assert.AreEqual(default, layout);
        StringAssert.Contains(reason, reasonToken);
    }

    /// <summary>Creates a valid Luma fixture.</summary>
    /// <returns>Exact-size named Luma framebuffer.</returns>
    private static FrameBufferRef Luma() => new()
    {
        FboId = 81,
        Width = 1280,
        Height = 720,
        ColorTextureIds = [82]
    };

    /// <summary>Creates a sparse registry indexed through the public Luma slot.</summary>
    /// <param name="luma">Luma framebuffer to expose.</param>
    /// <returns>Framebuffer registry with the supplied Luma entry.</returns>
    private static List<FrameBufferRef> Registry(FrameBufferRef luma)
    {
        List<FrameBufferRef> result = [];
        for (int index = 0; index <= (int)EnumFrameBuffer.Luma; index++)
        {
            result.Add(new FrameBufferRef());
        }
        result[(int)EnumFrameBuffer.Luma] = luma;
        return result;
    }
}
