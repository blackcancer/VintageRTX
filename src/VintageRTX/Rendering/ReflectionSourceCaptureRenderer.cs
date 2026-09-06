using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Owns immutable late-opaque copies of Vintage Story's Primary colour, material, normal, and position attachments.
/// The local first-person draw is deferred until after this copy, then replayed into the ordinary
/// Primary framebuffer so arms and held items remain visible only in the direct view. Every G-buffer
/// component used by reflections is snapshot-owned because the 1.22.7 hand/item shaders write normals.
/// </summary>
internal sealed class ReflectionSourceCaptureRenderer : IRenderer
{
    /// <summary>Colour texture unit reserved by the display shader for the reflection source.</summary>
    private const TextureUnit CopyTextureUnit = TextureUnit.Texture17;
    /// <summary>Primary attachment index containing the completed opaque scene colour.</summary>
    private const int ColorAttachment = 0;
    /// <summary>Primary attachment index containing glow and packed material data.</summary>
    private const int GlowAttachment = 1;
    /// <summary>Primary attachment index containing view-space normals.</summary>
    private const int NormalAttachment = 2;
    /// <summary>Primary attachment index containing view-space positions.</summary>
    private const int PositionAttachment = 3;
    /// <summary>Client renderer and framebuffer registry.</summary>
    private readonly ICoreClientAPI api;
    /// <summary>Owned copy of Primary attachment zero.</summary>
    private int colorTextureId;
    /// <summary>Owned copy of Primary attachment one.</summary>
    private int glowTextureId;
    /// <summary>Owned copy of Primary attachment two.</summary>
    private int normalTextureId;
    /// <summary>Owned copy of Primary attachment three.</summary>
    private int positionTextureId;
    /// <summary>Owned copy of the late-opaque depth attachment.</summary>
    private int depthTextureId;
    /// <summary>Primary colour texture matched by the owned colour storage.</summary>
    private int colorSourceTextureId;
    /// <summary>Primary material texture matched by the owned material storage.</summary>
    private int glowSourceTextureId;
    /// <summary>Primary normal texture matched by the owned normal storage.</summary>
    private int normalSourceTextureId;
    /// <summary>Primary position texture matched by the owned position storage.</summary>
    private int positionSourceTextureId;
    /// <summary>Primary depth texture matched by the owned depth storage.</summary>
    private int depthSourceTextureId;
    /// <summary>Allocated snapshot width.</summary>
    private int width;
    /// <summary>Allocated snapshot height.</summary>
    private int height;
    /// <summary>Whether every owned attachment contains the current late-opaque frame.</summary>
    private bool ready;
    /// <summary>Backer for <see cref="Enabled"/> so disabling invalidates stale frame data.</summary>
    private bool enabled = true;
    /// <summary>Suppresses repeated layout warnings until a valid Primary target returns.</summary>
    private bool layoutFailureLogged;
    /// <summary>Suppresses repeated OpenGL copy failures until a successful snapshot returns.</summary>
    private bool copyFailureLogged;
    /// <summary>Whether effect-phase CPU-wall diagnostics should be accumulated.</summary>
    private bool cpuDiagnosticsEnabled;
    /// <summary>Effect frames represented by snapshot/replay diagnostic totals.</summary>
    private int cpuDiagnosticFrames;
    /// <summary>Accumulated ticks through validation, allocation, and the three image copies.</summary>
    private long snapshotDiagnosticTicks;
    /// <summary>Accumulated ticks through the official deferred first-person replay.</summary>
    private long replayDiagnosticTicks;
    /// <summary>Maximum total opaque-boundary ticks observed during one effect frame.</summary>
    private long maximumDiagnosticFrameTicks;

    /// <summary>Creates an initially enabled render-thread capture owned by the display renderer.</summary>
    /// <param name="api">Client framebuffer dimensions, logging, and OpenGL services.</param>
    public ReflectionSourceCaptureRenderer(ICoreClientAPI api)
    {
        this.api = api;
    }

    /// <summary>
    /// Runs after terrain, entities, particles, and alpha-tested foliage but before the engine's
    /// reserved first-person and custom held-item slots at orders 0.8 and 0.9.
    /// </summary>
    public double RenderOrder => 0.79;

    /// <summary>Requests every camera distance because the capture covers the complete frame.</summary>
    public int RenderRange => 0;

    /// <summary>
    /// Gets or sets whether opaque attachments should be copied and local first-person draws deferred.
    /// Disabling immediately invalidates previous-frame data so a later re-enable cannot sample it.
    /// </summary>
    internal bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            if (!value)
            {
                ready = false;
            }
        }
    }

    /// <summary>Gets the owned clean colour attachment, or zero before allocation.</summary>
    internal int TextureId => colorTextureId;

    /// <summary>Gets the owned clean view-position attachment, or zero before allocation.</summary>
    internal int PositionTextureId => positionTextureId;

    /// <summary>Gets the owned late-opaque depth attachment, including alpha-tested geometry.</summary>
    internal int DepthTextureId => depthTextureId;

    /// <summary>Exposes the owning API to the tightly scoped Harmony identity and replay checks.</summary>
    internal ICoreClientAPI Api => api;

    /// <summary>Reports whether every owned attachment contains a current exact-size snapshot.</summary>
    /// <param name="frameWidth">Expected viewport width.</param>
    /// <param name="frameHeight">Expected viewport height.</param>
    /// <returns>True only after a successful exact-size late-opaque copy.</returns>
    internal bool IsReady(int frameWidth, int frameHeight) =>
        ready
        && colorTextureId != 0
        && glowTextureId != 0
        && normalTextureId != 0
        && positionTextureId != 0
        && depthTextureId != 0
        && width == frameWidth
        && height == frameHeight;

    /// <summary>Returns the complete owned material, normal, and position snapshot.</summary>
    /// <param name="frameWidth">Expected viewport width.</param>
    /// <param name="frameHeight">Expected viewport height.</param>
    /// <param name="liveGBuffer">Validated live handles retained only as a compatibility input.</param>
    /// <param name="gBuffer">Complete clean G-buffer handles, or default when unavailable.</param>
    /// <returns>True when the clean snapshot is complete and matches the display dimensions.</returns>
    internal bool TryGetGBuffer(
        int frameWidth,
        int frameHeight,
        in GameGBuffer liveGBuffer,
        out GameGBuffer gBuffer)
    {
        _ = liveGBuffer;
        if (!IsReady(frameWidth, frameHeight))
        {
            gBuffer = default;
            return false;
        }

        gBuffer = new GameGBuffer(
            glowTextureId,
            normalTextureId,
            positionTextureId);
        return true;
    }

    /// <summary>
    /// Copies the clean late-opaque Primary attachments and always replays a draw skipped by the
    /// Harmony prefix, including when allocation, validation, or copying fails.
    /// </summary>
    /// <param name="deltaTime">Unused frame duration supplied by the engine.</param>
    /// <param name="stage">Render stage invoking this renderer.</param>
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = deltaTime;
        if (stage != EnumRenderStage.Opaque)
        {
            return;
        }

        long diagnosticStart = cpuDiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0L;
        long diagnosticSnapshotEnd = 0L;
        // Snapshot replay state before enqueueing the four image copies. Reading driver state
        // afterwards can serialize the copy workload even though none of these values depends on it.
        ReplayGlState? deferredDrawState = FirstPersonReflectionCapturePatch.HasDeferredDraw
            ? ReplayGlState.Capture()
            : null;
        try
        {
            if (!Enabled)
            {
                ready = false;
                return;
            }

            int frameWidth = api.Render.FrameWidth;
            int frameHeight = api.Render.FrameHeight;
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                ready = false;
                return;
            }

            if (!TryResolvePrimarySnapshotSource(
                    api.Render.FrameBuffers,
                    frameWidth,
                    frameHeight,
                    out FrameBufferRef? primary,
                    out string reason))
            {
                ready = false;
                if (!layoutFailureLogged)
                {
                    layoutFailureLogged = true;
                    api.Logger.Warning(
                        "[VintageRTX] Clean opaque reflection snapshot unavailable; {0}.",
                        reason);
                }

                return;
            }

            EnsureTextures(primary!, frameWidth, frameHeight);
            CopyPrimaryAttachments(primary!, frameWidth, frameHeight);
            ready = true;
            layoutFailureLogged = false;
            copyFailureLogged = false;
        }
        catch (Exception exception)
        {
            ready = false;
            if (!copyFailureLogged)
            {
                copyFailureLogged = true;
                api.Logger.Error(
                    "[VintageRTX] Clean opaque reflection snapshot failed; live G-buffer fallback remains active: {0}",
                    exception);
            }
        }
        finally
        {
            if (cpuDiagnosticsEnabled)
            {
                diagnosticSnapshotEnd = Stopwatch.GetTimestamp();
            }
            ReplayDeferredFirstPersonDraw(deferredDrawState);
            if (cpuDiagnosticsEnabled)
            {
                long diagnosticEnd = Stopwatch.GetTimestamp();
                long snapshotTicks = Math.Max(0L, diagnosticSnapshotEnd - diagnosticStart);
                long replayTicks = Math.Max(0L, diagnosticEnd - diagnosticSnapshotEnd);
                snapshotDiagnosticTicks += snapshotTicks;
                replayDiagnosticTicks += replayTicks;
                maximumDiagnosticFrameTicks = Math.Max(
                    maximumDiagnosticFrameTicks,
                    snapshotTicks + replayTicks);
                cpuDiagnosticFrames++;
            }
        }
    }

    /// <summary>Starts a fresh effect-phase CPU-wall measurement at the opaque boundary.</summary>
    internal void BeginCpuDiagnostics()
    {
        cpuDiagnosticFrames = 0;
        snapshotDiagnosticTicks = 0L;
        replayDiagnosticTicks = 0L;
        maximumDiagnosticFrameTicks = 0L;
        cpuDiagnosticsEnabled = true;
    }

    /// <summary>Stops and reports effect-phase snapshot/replay wall time without affecting rendering.</summary>
    internal void EndAndLogCpuDiagnostics()
    {
        cpuDiagnosticsEnabled = false;
        if (cpuDiagnosticFrames <= 0)
        {
            return;
        }

        double divisor = cpuDiagnosticFrames;
        api.Logger.Notification(
            "[VintageRTX] Effect opaque CPU-wall stages | frames={0}, avg ms snapshot={1:0.000}, replay={2:0.000}; max measured-boundary={3:0.000}ms.",
            cpuDiagnosticFrames,
            snapshotDiagnosticTicks * 1000.0 / Stopwatch.Frequency / divisor,
            replayDiagnosticTicks * 1000.0 / Stopwatch.Frequency / divisor,
            maximumDiagnosticFrameTicks * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>
    /// Establishes the ordinary opaque entity state expected by the deferred official method, then
    /// restores the capability, blending, program, vertex-array, and texture state observed at 0.79.
    /// </summary>
    /// <param name="capturedState">
    /// State captured before snapshot commands, or null when no draw was pending at the boundary.
    /// </param>
    private void ReplayDeferredFirstPersonDraw(ReplayGlState? capturedState)
    {
        if (!FirstPersonReflectionCapturePatch.HasDeferredDraw)
        {
            return;
        }

        ReplayGlState state = capturedState ?? ReplayGlState.Capture();
        IShaderProgram? previousShader = api.Render.CurrentActiveShader;
        try
        {
            previousShader?.Stop();
            api.Render.GlMatrixModeModelView();
            api.Render.GlDisableCullFace();
            api.Render.GlToggleBlend(true, EnumBlendMode.Standard);
            api.Render.GLEnableDepthTest();
            api.Render.GLDepthMask(true);
            FirstPersonReflectionCapturePatch.ReplayDeferredDraw();
        }
        finally
        {
            IShaderProgram? activeShader = api.Render.CurrentActiveShader;
            if (activeShader is not null && !ReferenceEquals(activeShader, previousShader))
            {
                activeShader.Stop();
            }

            if (previousShader is not null)
            {
                previousShader.Use();
            }
            else
            {
                GL.UseProgram(state.Program);
            }

            state.Restore();
        }
    }

    /// <summary>Allocates or resizes all owned snapshot textures to the engine's exact formats.</summary>
    /// <param name="primary">Validated Primary framebuffer whose texture formats must be preserved.</param>
    /// <param name="frameWidth">Required viewport width.</param>
    /// <param name="frameHeight">Required viewport height.</param>
    private void EnsureTextures(FrameBufferRef primary, int frameWidth, int frameHeight)
    {
        colorTextureId = EnsureTextureName(colorTextureId);
        glowTextureId = EnsureTextureName(glowTextureId);
        normalTextureId = EnsureTextureName(normalTextureId);
        positionTextureId = EnsureTextureName(positionTextureId);
        depthTextureId = EnsureTextureName(depthTextureId);
        int currentColorSource = primary.ColorTextureIds[ColorAttachment];
        int currentGlowSource = primary.ColorTextureIds[GlowAttachment];
        int currentNormalSource = primary.ColorTextureIds[NormalAttachment];
        int currentPositionSource = primary.ColorTextureIds[PositionAttachment];
        int currentDepthSource = primary.DepthTextureId;
        if (width == frameWidth
            && height == frameHeight
            && colorSourceTextureId == currentColorSource
            && glowSourceTextureId == currentGlowSource
            && normalSourceTextureId == currentNormalSource
            && positionSourceTextureId == currentPositionSource
            && depthSourceTextureId == currentDepthSource)
        {
            return;
        }

        PixelInternalFormat colorFormat = ResolveInternalFormat(currentColorSource);
        PixelInternalFormat glowFormat = ResolveInternalFormat(currentGlowSource);
        PixelInternalFormat normalFormat = ResolveInternalFormat(currentNormalSource);
        PixelInternalFormat positionFormat = ResolveInternalFormat(currentPositionSource);
        PixelInternalFormat depthFormat = ResolveInternalFormat(currentDepthSource);
        AllocateTexture(
            colorTextureId,
            colorFormat,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear,
            PixelFormat.Rgba,
            frameWidth,
            frameHeight);
        AllocateTexture(
            glowTextureId,
            glowFormat,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            PixelFormat.Rgba,
            frameWidth,
            frameHeight);
        AllocateTexture(
            normalTextureId,
            normalFormat,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            PixelFormat.Rgba,
            frameWidth,
            frameHeight);
        AllocateTexture(
            positionTextureId,
            positionFormat,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            PixelFormat.Rgba,
            frameWidth,
            frameHeight);
        AllocateTexture(
            depthTextureId,
            depthFormat,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            PixelFormat.DepthComponent,
            frameWidth,
            frameHeight);
        colorSourceTextureId = currentColorSource;
        glowSourceTextureId = currentGlowSource;
        normalSourceTextureId = currentNormalSource;
        positionSourceTextureId = currentPositionSource;
        depthSourceTextureId = currentDepthSource;
        width = frameWidth;
        height = frameHeight;
        ready = false;
        api.Logger.Notification(
            "[VintageRTX] Clean opaque colour/material/normal/position/depth snapshot resized to {0}x{1}; formats={2}/{3}/{4}/{5}/{6}.",
            frameWidth,
            frameHeight,
            colorFormat,
            glowFormat,
            normalFormat,
            positionFormat,
            depthFormat);
    }

    /// <summary>Creates one OpenGL texture name when the supplied slot is still empty.</summary>
    /// <param name="textureId">Existing texture name or zero.</param>
    /// <returns>The existing or newly generated texture name.</returns>
    private static int EnsureTextureName(int textureId) =>
        textureId == 0 ? GL.GenTexture() : textureId;

    /// <summary>
    /// Reads one engine texture's sized internal format during allocation only. Matching storage
    /// permits direct OpenGL 4.3 image copies without a framebuffer bind or per-frame state query.
    /// </summary>
    /// <param name="sourceTextureId">Validated engine-owned two-dimensional texture.</param>
    /// <returns>The exact sized format required by the owned snapshot.</returns>
    private static PixelInternalFormat ResolveInternalFormat(int sourceTextureId)
    {
        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.ActiveTexture(CopyTextureUnit);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTextureBinding);
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, sourceTextureId);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D,
                0,
                GetTextureParameter.TextureInternalFormat,
                out int internalFormat);
            return RequireSizedInternalFormat(internalFormat, sourceTextureId);
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, previousTextureBinding);
            GL.ActiveTexture((TextureUnit)previousActiveTexture);
        }
    }

    /// <summary>Validates one driver-reported internal format before owned storage allocation.</summary>
    /// <param name="internalFormat">Raw OpenGL internal-format enumeration.</param>
    /// <param name="sourceTextureId">Engine source texture used in diagnostics.</param>
    /// <returns>The corresponding sized pixel format.</returns>
    /// <exception cref="InvalidOperationException">The driver reports no allocated level-zero format.</exception>
    internal static PixelInternalFormat RequireSizedInternalFormat(
        int internalFormat,
        int sourceTextureId)
    {
        if (internalFormat == 0)
        {
            throw new InvalidOperationException(
                $"Primary texture {sourceTextureId} exposes no sized internal format.");
        }

        return (PixelInternalFormat)internalFormat;
    }

    /// <summary>Allocates one clamp-to-edge snapshot while preserving the caller's texture state.</summary>
    /// <param name="targetTextureId">Owned texture name.</param>
    /// <param name="internalFormat">Precision required by this Primary attachment.</param>
    /// <param name="minFilter">Minification filter for later shader reads.</param>
    /// <param name="magFilter">Magnification filter for later shader reads.</param>
    /// <param name="pixelFormat">Colour or depth component layout accepted by the storage.</param>
    /// <param name="frameWidth">Allocation width.</param>
    /// <param name="frameHeight">Allocation height.</param>
    private static void AllocateTexture(
        int targetTextureId,
        PixelInternalFormat internalFormat,
        TextureMinFilter minFilter,
        TextureMagFilter magFilter,
        PixelFormat pixelFormat,
        int frameWidth,
        int frameHeight)
    {
        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.ActiveTexture(CopyTextureUnit);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTextureBinding);
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, targetTextureId);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)minFilter);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)magFilter);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                internalFormat,
                frameWidth,
                frameHeight,
                0,
                pixelFormat,
                PixelType.Float,
                IntPtr.Zero);
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, previousTextureBinding);
            GL.ActiveTexture((TextureUnit)previousActiveTexture);
        }
    }

    /// <summary>
    /// Copies Primary colour, packed material, normal, and position with OpenGL 4.3 image copies. This path
    /// changes no binding and avoids the synchronous state reads formerly paid every frame.
    /// </summary>
    /// <param name="primary">Validated live Primary framebuffer.</param>
    /// <param name="frameWidth">Copy width.</param>
    /// <param name="frameHeight">Copy height.</param>
    private void CopyPrimaryAttachments(
        FrameBufferRef primary,
        int frameWidth,
        int frameHeight)
    {
        CopyAttachment(primary.ColorTextureIds[ColorAttachment], colorTextureId, frameWidth, frameHeight);
        CopyAttachment(primary.ColorTextureIds[GlowAttachment], glowTextureId, frameWidth, frameHeight);
        CopyAttachment(primary.ColorTextureIds[NormalAttachment], normalTextureId, frameWidth, frameHeight);
        CopyAttachment(primary.ColorTextureIds[PositionAttachment], positionTextureId, frameWidth, frameHeight);
        CopyAttachment(primary.DepthTextureId, depthTextureId, frameWidth, frameHeight);
    }

    /// <summary>Copies one engine texture into compatible exact-size owned storage.</summary>
    /// <param name="sourceTextureId">Engine-owned source texture.</param>
    /// <param name="targetTextureId">Owned destination texture.</param>
    /// <param name="frameWidth">Copy width.</param>
    /// <param name="frameHeight">Copy height.</param>
    private static void CopyAttachment(
        int sourceTextureId,
        int targetTextureId,
        int frameWidth,
        int frameHeight)
    {
        GL.CopyImageSubData(
            sourceTextureId,
            ImageTarget.Texture2D,
            0,
            0,
            0,
            0,
            targetTextureId,
            ImageTarget.Texture2D,
            0,
            0,
            0,
            0,
            frameWidth,
            frameHeight,
            1);
    }

    /// <summary>Validates the Primary layout required for a complete clean colour/material/normal/position snapshot.</summary>
    /// <param name="frameBuffers">Live engine framebuffer registry.</param>
    /// <param name="frameWidth">Required colour width.</param>
    /// <param name="frameHeight">Required colour height.</param>
    /// <param name="primary">Validated Primary framebuffer, or null on rejection.</param>
    /// <param name="reason">Stable rejection reason suitable for transition logs.</param>
    /// <returns>True only for a live exact-size Primary framebuffer with attachments zero through three.</returns>
    internal static bool TryResolvePrimarySnapshotSource(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int frameWidth,
        int frameHeight,
        out FrameBufferRef? primary,
        out string reason)
    {
        primary = null;
        int primaryIndex = (int)EnumFrameBuffer.Primary;
        // EnumFrameBuffer.Primary is an engine-defined non-negative registry slot.
        if (frameBuffers.Count <= primaryIndex)
        {
            reason = "the Primary framebuffer is not available";
            return false;
        }

        FrameBufferRef candidate = frameBuffers[primaryIndex];
        if (candidate.Disposed || candidate.FboId <= 0)
        {
            reason = "the Primary framebuffer is disposed or has no OpenGL name";
            return false;
        }

        if (candidate.Width != frameWidth || candidate.Height != frameHeight)
        {
            reason = $"the Primary framebuffer size {candidate.Width}x{candidate.Height} does not match {frameWidth}x{frameHeight}";
            return false;
        }

        if (candidate.ColorTextureIds is null
            || candidate.ColorTextureIds.Length <= PositionAttachment)
        {
            reason = $"the Primary framebuffer exposes only {candidate.ColorTextureIds?.Length ?? 0} colour attachments";
            return false;
        }

        for (int index = ColorAttachment; index <= PositionAttachment; index++)
        {
            if (candidate.ColorTextureIds[index] <= 0)
            {
                reason = $"Primary colour attachment {index} has no texture";
                return false;
            }
        }

        if (candidate.DepthTextureId <= 0)
        {
            reason = "the Primary framebuffer has no depth texture";
            return false;
        }

        primary = candidate;
        reason = "ready";
        return true;
    }

    /// <summary>
    /// Retains the former conservative colour-only boundary for compatibility with deterministic
    /// tests and diagnostics; production snapshots use <see cref="TryResolvePrimarySnapshotSource"/>.
    /// </summary>
    /// <param name="frameBuffers">Live engine framebuffer registry.</param>
    /// <param name="readFramebufferId">Currently bound OpenGL read framebuffer name.</param>
    /// <param name="frameWidth">Required colour width.</param>
    /// <param name="frameHeight">Required colour height.</param>
    /// <param name="colorTextureId">Borrowed attachment-zero name when valid.</param>
    /// <returns>True only when the read target is the exact recognised Primary framebuffer.</returns>
    internal static bool TryResolvePrimaryColorTexture(
        IReadOnlyList<FrameBufferRef> frameBuffers,
        int readFramebufferId,
        int frameWidth,
        int frameHeight,
        out int colorTextureId)
    {
        colorTextureId = 0;
        if (!TryResolvePrimarySnapshotSource(
                frameBuffers,
                frameWidth,
                frameHeight,
                out FrameBufferRef? primary,
                out _)
            || primary!.FboId != readFramebufferId)
        {
            return false;
        }

        colorTextureId = primary.ColorTextureIds[ColorAttachment];
        return true;
    }

    /// <summary>Deletes all owned textures and uninstalls the render hook on the render thread.</summary>
    public void Dispose()
    {
        FirstPersonReflectionCapturePatch.Uninstall();
        DeleteTexture(ref colorTextureId);
        DeleteTexture(ref glowTextureId);
        DeleteTexture(ref normalTextureId);
        DeleteTexture(ref positionTextureId);
        DeleteTexture(ref depthTextureId);
        colorSourceTextureId = 0;
        glowSourceTextureId = 0;
        normalSourceTextureId = 0;
        positionSourceTextureId = 0;
        depthSourceTextureId = 0;
        width = 0;
        height = 0;
        ready = false;
        layoutFailureLogged = false;
        copyFailureLogged = false;
    }

    /// <summary>Deletes one optional owned texture and clears its slot.</summary>
    /// <param name="textureId">Owned OpenGL texture name.</param>
    private static void DeleteTexture(ref int textureId)
    {
        if (textureId == 0)
        {
            return;
        }

        GL.DeleteTexture(textureId);
        textureId = 0;
    }

    /// <summary>OpenGL state modified while replaying the deferred official entity draw.</summary>
    private readonly record struct ReplayGlState(
        bool DepthTest,
        bool DepthWrite,
        bool Blend,
        bool CullFace,
        int BlendSourceRgb,
        int BlendDestinationRgb,
        int BlendSourceAlpha,
        int BlendDestinationAlpha,
        int BlendEquationRgb,
        int BlendEquationAlpha,
        int ActiveTexture,
        int VertexArray,
        int Program)
    {
        /// <summary>Captures the exact capability and blending state changed by replay preparation.</summary>
        /// <returns>Immutable state suitable for restoration after the official draw.</returns>
        internal static ReplayGlState Capture()
        {
            GL.GetBoolean(GetPName.DepthWritemask, out bool depthWrite);
            GL.GetInteger(GetPName.BlendSrcRgb, out int blendSourceRgb);
            GL.GetInteger(GetPName.BlendDstRgb, out int blendDestinationRgb);
            GL.GetInteger(GetPName.BlendSrcAlpha, out int blendSourceAlpha);
            GL.GetInteger(GetPName.BlendDstAlpha, out int blendDestinationAlpha);
            GL.GetInteger(GetPName.BlendEquationRgb, out int blendEquationRgb);
            GL.GetInteger(GetPName.BlendEquationAlpha, out int blendEquationAlpha);
            GL.GetInteger(GetPName.ActiveTexture, out int activeTexture);
            GL.GetInteger(GetPName.VertexArrayBinding, out int vertexArray);
            GL.GetInteger(GetPName.CurrentProgram, out int program);
            return new ReplayGlState(
                GL.IsEnabled(EnableCap.DepthTest),
                depthWrite,
                GL.IsEnabled(EnableCap.Blend),
                GL.IsEnabled(EnableCap.CullFace),
                blendSourceRgb,
                blendDestinationRgb,
                blendSourceAlpha,
                blendDestinationAlpha,
                blendEquationRgb,
                blendEquationAlpha,
                activeTexture,
                vertexArray,
                program);
        }

        /// <summary>Restores every captured OpenGL state value.</summary>
        internal void Restore()
        {
            SetEnabled(EnableCap.DepthTest, DepthTest);
            GL.DepthMask(DepthWrite);
            SetEnabled(EnableCap.Blend, Blend);
            SetEnabled(EnableCap.CullFace, CullFace);
            GL.BlendFuncSeparate(
                (BlendingFactorSrc)BlendSourceRgb,
                (BlendingFactorDest)BlendDestinationRgb,
                (BlendingFactorSrc)BlendSourceAlpha,
                (BlendingFactorDest)BlendDestinationAlpha);
            GL.BlendEquationSeparate(
                (BlendEquationMode)BlendEquationRgb,
                (BlendEquationMode)BlendEquationAlpha);
            GL.BindVertexArray(VertexArray);
            GL.ActiveTexture((TextureUnit)ActiveTexture);
        }

        /// <summary>Restores one OpenGL capability to its captured state.</summary>
        /// <param name="capability">Capability to restore.</param>
        /// <param name="enabled">Captured enable state.</param>
        private static void SetEnabled(EnableCap capability, bool enabled)
        {
            if (enabled)
            {
                GL.Enable(capability);
            }
            else
            {
                GL.Disable(capability);
            }
        }
    }
}
