using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;

namespace VintageRTX.Test;

/// <summary>Verifies conservative zero-copy display-source selection without an OpenGL context.</summary>
[TestClass]
public sealed class DisplaySourceTextureResolverTests
{
    /// <summary>Accepts an exact Primary source only when the output texture is distinct.</summary>
    [TestMethod]
    public void ExactPrimarySourceAndDistinctOffscreenOutputAreBorrowed()
    {
        const int sourceTextureId = 47;
        List<FrameBufferRef> frameBuffers = CreateFrameBuffers(new FrameBufferRef
        {
            FboId = 31,
            Width = 1920,
            Height = 1009,
            ColorTextureIds = [sourceTextureId]
        });

        Assert.IsTrue(DisplaySourceTextureResolver.TryResolve(
            frameBuffers,
            31,
            1920,
            1009,
            53,
            out int resolvedTextureId));
        Assert.AreEqual(sourceTextureId, resolvedTextureId);
    }

    /// <summary>Covers every unsafe layout that must preserve the full-frame copy fallback.</summary>
    [TestMethod]
    public void DirectUnknownMismatchedAndFeedbackDestinationsAreRejected()
    {
        const int sourceTextureId = 47;
        const int framebufferId = 31;
        const int width = 1920;
        const int height = 1009;
        List<FrameBufferRef> frameBuffers = CreateFrameBuffers(new FrameBufferRef
        {
            FboId = framebufferId,
            Width = width,
            Height = height,
            ColorTextureIds = [sourceTextureId]
        });

        AssertRejected([], framebufferId, width, height, 53);
        AssertRejected(frameBuffers, 0, width, height, 53);
        AssertRejected(frameBuffers, framebufferId, width, height, 0);
        AssertRejected(frameBuffers, framebufferId + 1, width, height, 53);
        AssertRejected(frameBuffers, framebufferId, width - 1, height, 53);
        AssertRejected(frameBuffers, framebufferId, width, height - 1, 53);
        AssertRejected(frameBuffers, framebufferId, width, height, sourceTextureId);

        FrameBufferRef primary = frameBuffers[(int)EnumFrameBuffer.Primary];
        primary.Disposed = true;
        AssertRejected(frameBuffers, framebufferId, width, height, 53);
        primary.Disposed = false;
        primary.ColorTextureIds = null!;
        AssertRejected(frameBuffers, framebufferId, width, height, 53);
        primary.ColorTextureIds = [];
        AssertRejected(frameBuffers, framebufferId, width, height, 53);
        primary.ColorTextureIds = [0];
        AssertRejected(frameBuffers, framebufferId, width, height, 53);
    }

    /// <summary>Creates a registry whose Primary slot contains the supplied framebuffer.</summary>
    /// <param name="primary">Framebuffer placed in the engine-defined Primary slot.</param>
    /// <returns>A registry large enough to expose the Primary slot.</returns>
    private static List<FrameBufferRef> CreateFrameBuffers(FrameBufferRef primary)
    {
        List<FrameBufferRef> frameBuffers = [];
        for (int index = 0; index <= (int)EnumFrameBuffer.Primary; index++)
        {
            frameBuffers.Add(new FrameBufferRef());
        }

        frameBuffers[(int)EnumFrameBuffer.Primary] = primary;
        return frameBuffers;
    }

    /// <summary>Asserts that an unsafe source/destination pair requests the owned-copy path.</summary>
    /// <param name="frameBuffers">Candidate engine framebuffer registry.</param>
    /// <param name="readFramebufferId">Candidate current read framebuffer.</param>
    /// <param name="width">Candidate source width.</param>
    /// <param name="height">Candidate source height.</param>
    /// <param name="destinationTextureId">Candidate display output texture.</param>
    private static void AssertRejected(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int readFramebufferId,
        int width,
        int height,
        int destinationTextureId)
    {
        Assert.IsFalse(DisplaySourceTextureResolver.TryResolve(
            frameBuffers,
            readFramebufferId,
            width,
            height,
            destinationTextureId,
            out int sourceTextureId));
        Assert.AreEqual(0, sourceTextureId);
    }
}
