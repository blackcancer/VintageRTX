using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;

namespace VintageRTX.Test;

/// <summary>
/// Verifies the conservative framebuffer-borrowing boundary without requiring an OpenGL context.
/// </summary>
[TestClass]
public sealed class ReflectionSourceCaptureRendererTests
{
    /// <summary>
    /// Accepts only the exact live Primary framebuffer and rejects every layout that must retain
    /// the owned full-frame copy fallback.
    /// </summary>
    [TestMethod]
    public void PrimaryColorBorrowRequiresExactLiveFramebufferAndSize()
    {
        const int width = 1920;
        const int height = 1009;
        const int framebufferId = 31;
        const int textureId = 47;
        List<FrameBufferRef> frameBuffers = CreateFrameBuffers(new FrameBufferRef
        {
            FboId = framebufferId,
            Width = width,
            Height = height,
            DepthTextureId = 51,
            ColorTextureIds = [textureId, 48, 49, 50]
        });

        Assert.IsTrue(ReflectionSourceCaptureRenderer.TryResolvePrimarySnapshotSource(
            frameBuffers,
            width,
            height,
            out FrameBufferRef? resolvedPrimary,
            out string reason));
        Assert.AreSame(frameBuffers[(int)EnumFrameBuffer.Primary], resolvedPrimary);
        Assert.AreEqual("ready", reason);

        Assert.IsTrue(ReflectionSourceCaptureRenderer.TryResolvePrimaryColorTexture(
            frameBuffers,
            framebufferId,
            width,
            height,
            out int resolvedTextureId));
        Assert.AreEqual(textureId, resolvedTextureId);

        AssertRejected([], framebufferId, width, height);
        AssertRejected(frameBuffers, 0, width, height);
        AssertRejected(frameBuffers, framebufferId + 1, width, height);
        AssertRejected(frameBuffers, framebufferId, width - 1, height);
        AssertRejected(frameBuffers, framebufferId, width, height - 1);

        FrameBufferRef primary = frameBuffers[(int)EnumFrameBuffer.Primary];
        primary.Disposed = true;
        AssertRejected(frameBuffers, framebufferId, width, height);
        primary.Disposed = false;
        primary.ColorTextureIds = null!;
        AssertRejected(frameBuffers, framebufferId, width, height);
        primary.ColorTextureIds = [];
        AssertRejected(frameBuffers, framebufferId, width, height);
        primary.ColorTextureIds = [0];
        AssertRejected(frameBuffers, framebufferId, width, height);
    }

    /// <summary>
    /// Rejects incomplete owned-snapshot layouts and accepts four colour attachments plus depth.
    /// </summary>
    [TestMethod]
    public void PrimarySnapshotRequiresFourLiveAttachments()
    {
        FrameBufferRef primary = new()
        {
            FboId = 9,
            Width = 800,
            Height = 600,
            DepthTextureId = 5,
            ColorTextureIds = [1, 2, 3, 4]
        };
        List<FrameBufferRef> frameBuffers = CreateFrameBuffers(primary);

        Assert.IsTrue(ReflectionSourceCaptureRenderer.TryResolvePrimarySnapshotSource(
            frameBuffers,
            800,
            600,
            out FrameBufferRef? resolved,
            out string readyReason));
        Assert.AreSame(primary, resolved);
        Assert.AreEqual("ready", readyReason);

        primary.ColorTextureIds = [1, 2, 3];
        Assert.IsFalse(ReflectionSourceCaptureRenderer.TryResolvePrimarySnapshotSource(
            frameBuffers,
            800,
            600,
            out resolved,
            out string shortReason));
        Assert.IsNull(resolved);
        StringAssert.Contains(shortReason, "only 3");

        for (int index = 0; index < 4; index++)
        {
            primary.ColorTextureIds = [1, 2, 3, 4];
            primary.ColorTextureIds[index] = 0;
            Assert.IsFalse(ReflectionSourceCaptureRenderer.TryResolvePrimarySnapshotSource(
                frameBuffers,
                800,
                600,
                out resolved,
                out string missingReason));
            Assert.IsNull(resolved);
            StringAssert.Contains(missingReason, $"attachment {index}");
        }

        primary.ColorTextureIds = [1, 2, 3, 4];
        primary.DepthTextureId = 0;
        Assert.IsFalse(ReflectionSourceCaptureRenderer.TryResolvePrimarySnapshotSource(
            frameBuffers,
            800,
            600,
            out resolved,
            out string missingDepthReason));
        Assert.IsNull(resolved);
        StringAssert.Contains(missingDepthReason, "no depth texture");
    }

    /// <summary>
    /// Verifies reflection transport consumes the owned normal attachment rather than the live
    /// Primary normal later overwritten by official first-person hand and held-item shaders.
    /// </summary>
    [TestMethod]
    public void CleanSnapshotOwnsNormalInsteadOfMixingLiveFirstPersonData()
    {
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>(
            static (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ReflectionSourceCaptureRenderer renderer = new(api);
        Dictionary<string, object> fields = new(StringComparer.Ordinal)
        {
            ["colorTextureId"] = 101,
            ["glowTextureId"] = 102,
            ["normalTextureId"] = 103,
            ["positionTextureId"] = 104,
            ["depthTextureId"] = 105,
            ["width"] = 800,
            ["height"] = 600,
            ["ready"] = true
        };
        foreach ((string name, object value) in fields)
        {
            typeof(ReflectionSourceCaptureRenderer)
                .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(renderer, value);
        }

        GameGBuffer live = new(201, 202, 203);
        Assert.IsTrue(renderer.TryGetGBuffer(800, 600, in live, out GameGBuffer clean));
        Assert.AreEqual(102, clean.GlowTextureId);
        Assert.AreEqual(103, clean.NormalTextureId);
        Assert.AreEqual(104, clean.PositionTextureId);
        Assert.AreNotEqual(live.NormalTextureId, clean.NormalTextureId);
        Assert.IsFalse(renderer.TryGetGBuffer(799, 600, in live, out _));
    }

    /// <summary>Ensures first-person deferral fails open for every unsafe identity or render pass.</summary>
    [TestMethod]
    public void FirstPersonDeferralRequiresEnabledLocalDirectDraw()
    {
        Assert.IsTrue(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(
            isShadowPass: false,
            captureEnabled: true,
            isReplay: false,
            isFirstPerson: true,
            renderedEntityId: 42,
            localEntityId: 42));

        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(true, true, false, true, 42, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(false, false, false, true, 42, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(false, true, true, true, 42, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(false, true, false, false, 42, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(false, true, false, true, 41, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(false, true, false, true, null, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsEligibleForDeferral(false, true, false, true, 42, null));

        Assert.IsTrue(FirstPersonReflectionCapturePatch.ShouldRenderLocalPlayerAsWorldBodyDuringMirror(true, 42, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.ShouldRenderLocalPlayerAsWorldBodyDuringMirror(false, 42, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.ShouldRenderLocalPlayerAsWorldBodyDuringMirror(true, 41, 42));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.ShouldRenderLocalPlayerAsWorldBodyDuringMirror(true, null, 42));

        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsSelfForCurrentRender(true, true));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsSelfForCurrentRender(true, false));
        Assert.IsTrue(FirstPersonReflectionCapturePatch.IsSelfForCurrentRender(false, true));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.IsSelfForCurrentRender(false, false));
    }

    /// <summary>Proves the mirror pass selects and restores the official named body mode.</summary>
    [TestMethod]
    public void LocalMirrorBodyOverrideUsesThirdPersonAndRestoresExactMode()
    {
        RenderModeHolder holder = new();
        System.Reflection.FieldInfo field = typeof(RenderModeHolder).GetField(
            nameof(RenderModeHolder.Mode))!;

        Assert.IsTrue(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            holder,
            field,
            out object? previousMode));
        Assert.AreEqual(TestRenderMode.ThirdPerson, holder.Mode);
        Assert.AreEqual(TestRenderMode.FirstPerson, previousMode);
        Assert.IsTrue(FirstPersonReflectionCapturePatch.TryRestoreRenderMode(
            holder,
            field,
            previousMode));
        Assert.AreEqual(TestRenderMode.FirstPerson, holder.Mode);

        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            holder,
            typeof(RenderModeHolder).GetField(nameof(RenderModeHolder.NotAnEnum)),
            out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryOverrideToThirdPerson(
            holder,
            null,
            out _));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryRestoreRenderMode(null, field, previousMode));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryRestoreRenderMode(holder, null, previousMode));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TryRestoreRenderMode(holder, field, null));

        System.Reflection.FieldInfo referenceField = typeof(RenderModeHolder).GetField(
            nameof(RenderModeHolder.ReferenceValue))!;
        Assert.IsTrue(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            holder,
            referenceField,
            "restored"));
        Assert.AreEqual("restored", holder.ReferenceValue);
        Assert.IsTrue(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            holder,
            referenceField,
            null));
        Assert.IsNull(holder.ReferenceValue);
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            holder,
            field,
            null));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            holder,
            field,
            "incompatible"));
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            null,
            field,
            TestRenderMode.FirstPerson));

        System.Reflection.FieldInfo animationSelectorField = typeof(RenderModeHolder).GetField(
            nameof(RenderModeHolder.SelfNowShadowPass))!;
        Assert.IsTrue(FirstPersonReflectionCapturePatch.TrySelectThirdPersonAnimator(
            holder,
            animationSelectorField,
            out object? previousAnimationSelector));
        Assert.AreEqual(false, previousAnimationSelector);
        Assert.IsTrue(holder.SelfNowShadowPass);
        Assert.IsTrue(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            holder,
            animationSelectorField,
            previousAnimationSelector));
        Assert.IsFalse(holder.SelfNowShadowPass);
        Assert.IsFalse(FirstPersonReflectionCapturePatch.TrySelectThirdPersonAnimator(
            holder,
            field,
            out _));

        holder.SelfNowShadowPass = true;
        Assert.IsTrue(FirstPersonReflectionCapturePatch.TrySelectThirdPersonAnimator(
            holder,
            animationSelectorField,
            out previousAnimationSelector));
        Assert.AreEqual(true, previousAnimationSelector);
        Assert.IsTrue(FirstPersonReflectionCapturePatch.TrySetCompatibleFieldValue(
            holder,
            animationSelectorField,
            previousAnimationSelector));
        Assert.IsTrue(holder.SelfNowShadowPass);
    }

    /// <summary>Small enum mirroring the stable names of the official 1.22 render modes.</summary>
    private enum TestRenderMode
    {
        /// <summary>Camera-space arm mode.</summary>
        FirstPerson,
        /// <summary>World-space body mode required by the mirror.</summary>
        ThirdPerson,
    }

    /// <summary>Reflection-only holder used to validate enum-field switching without the game client.</summary>
    private sealed class RenderModeHolder
    {
        /// <summary>Mode changed and restored by the test.</summary>
        public TestRenderMode Mode = TestRenderMode.FirstPerson;
        /// <summary>Deliberately incompatible field used to exercise the safe rejection path.</summary>
        public int NotAnEnum = 0;
        /// <summary>Nullable state used to validate restoration of reference fields.</summary>
        public string? ReferenceValue = null;
        /// <summary>Mirrors EntityPlayer.selfNowShadowPass for third-person animator selection.</summary>
        public bool SelfNowShadowPass = false;
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

    /// <summary>Asserts that an unsafe borrowing candidate returns the owned-copy fallback.</summary>
    /// <param name="frameBuffers">Candidate engine framebuffer registry.</param>
    /// <param name="readFramebufferId">Candidate bound read framebuffer.</param>
    /// <param name="width">Candidate viewport width.</param>
    /// <param name="height">Candidate viewport height.</param>
    private static void AssertRejected(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int readFramebufferId,
        int width,
        int height)
    {
        Assert.IsFalse(ReflectionSourceCaptureRenderer.TryResolvePrimaryColorTexture(
            frameBuffers,
            readFramebufferId,
            width,
            height,
            out int textureId));
        Assert.AreEqual(0, textureId);
    }
}
