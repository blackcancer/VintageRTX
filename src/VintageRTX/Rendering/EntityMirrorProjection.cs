using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>
/// Renders a horizontal mirrored world view into a tier-scaled colour/depth target. The primary
/// path replays official terrain and entity geometry under the reflected camera; the clean late-
/// opaque point projection remains a compatibility fallback and never contains the deferred arm.
/// </summary>
internal sealed class EntityMirrorProjection : IDisposable
{
    /// <summary>Stable name used by the engine memory-shader registry.</summary>
    private const string ShaderName = "vintagertx-entity-mirror";

    /// <summary>Allocation-free transparent colour used to clear the mirror carrier.</summary>
    private static readonly float[] TransparentClear = [0.0f, 0.0f, 0.0f, 0.0f];

    /// <summary>Allocation-free far depth used to clear the mirror depth attachment.</summary>
    private static readonly float[] FarDepthClear = [1.0f];

    /// <summary>
    /// Small world-space separation that keeps the liquid raster itself and numerically submerged
    /// fragments out of the reflected depth buffer before they can hide valid scenery.
    /// </summary>
    private const double MirrorClipBiasWorldBlocks = 0.015;

    private readonly ICoreClientAPI api;
    private IShaderProgram? shader;
    private int colorTextureId;
    private int depthTextureId;
    private int framebufferId;
    private int entityEvidenceColorTextureId;
    private int entityEvidenceDepthTextureId;
    private int entityEvidenceFramebufferId;
    /// <summary>Owned OpenGL projection whose near plane is the active liquid interface.</summary>
    private readonly double[] obliqueProjectionMatrix = new double[16];
    /// <summary>Projection shared by all producers of the current mirror depth.</summary>
    private readonly float[] mirrorDepthProjectionMatrix = new float[16];
    /// <summary>Inverse paired with the current mirror depth projection.</summary>
    private readonly float[] inverseMirrorDepthProjectionMatrix = new float[16];
    /// <summary>Reusable inverse-view scratch used while transforming the world clip plane.</summary>
    private readonly double[] inverseMirrorViewScratch = new double[16];
    /// <summary>Reusable inverse-projection scratch used to locate the opposite clip-space corner.</summary>
    private readonly double[] inverseProjectionScratch = new double[16];
    /// <summary>Suppresses repeated warnings while the fail-closed screen-derived fallback is active.</summary>
    private bool clipProjectionFailureLogged;
    private int vertexArrayId;
    private int width;
    private int height;
    private bool disposed;
    private readonly double[] mirroredCameraMatrix = new double[16];

    /// <summary>Creates an unallocated mirror projector.</summary>
    /// <param name="api">Client shader and OpenGL services.</param>
    internal EntityMirrorProjection(ICoreClientAPI api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>Gets the tier-scaled reflected world colour/coverage texture.</summary>
    internal int ColorTextureId => colorTextureId;

    /// <summary>Gets reflected world depth for reconstructing and clipping source geometry.</summary>
    internal int DepthTextureId => depthTextureId;

    /// <summary>
    /// Gets the inverse projection paired with DepthTextureId after successful Render.
    /// The ordinary inverse view reconstructs reflected geometry, not the original source point.
    /// </summary>
    internal float[] InverseDepthProjectionMatrix => inverseMirrorDepthProjectionMatrix;

    /// <summary>Gets the owned framebuffer used for raw entity-only evidence readback.</summary>
    internal int FramebufferId => framebufferId;

    /// <summary>Gets the entity-only evidence framebuffer used by automated overlay validation.</summary>
    internal int EntityEvidenceFramebufferId => entityEvidenceFramebufferId;

    /// <summary>Gets the allocated mirror-carrier width.</summary>
    internal int Width => width;

    /// <summary>Gets the allocated mirror-carrier height.</summary>
    internal int Height => height;

    /// <summary>Gets whether a complete target currently exists.</summary>
    internal bool IsAllocated =>
        !disposed
        && colorTextureId > 0
        && depthTextureId > 0
        && framebufferId > 0
        && entityEvidenceColorTextureId > 0
        && entityEvidenceDepthTextureId > 0
        && entityEvidenceFramebufferId > 0
        && vertexArrayId > 0
        && width > 0
        && height > 0;

    /// <summary>
    /// Clears and rebuilds the mirrored world for one dominant liquid plane. Callers restore GL
    /// state after the complete display pass, so this bounded helper intentionally performs no queries.
    /// </summary>
    /// <param name="frameWidth">Full source width.</param>
    /// <param name="frameHeight">Full source height.</param>
    /// <param name="cleanColorTextureId">Late-opaque colour without local first person.</param>
    /// <param name="cleanPositionTextureId">Matching late-opaque position.</param>
    /// <param name="terrainPositionTextureId">Position captured immediately before entities.</param>
    /// <param name="projectionMatrix">Current column-major perspective projection.</param>
    /// <param name="viewMatrix">Current floating-origin view transform.</param>
    /// <param name="inverseViewMatrix">Inverse floating-origin view matrix.</param>
    /// <param name="floatingOriginX">Absolute player-origin X restored after inverse view.</param>
    /// <param name="floatingOriginY">Absolute player-origin Y restored after inverse view.</param>
    /// <param name="floatingOriginZ">Absolute player-origin Z restored after inverse view.</param>
    /// <param name="surfaceWorldY">Dominant horizontal liquid interface.</param>
    /// <param name="maximumDistance">Maximum reflected entity distance in world blocks.</param>
    /// <param name="captureEntityEvidence">Whether to render an isolated entity carrier this frame.</param>
    /// <param name="resolutionDivisor">Full-frame divisor for the owned mirror target.</param>
    internal bool Render(
        int frameWidth,
        int frameHeight,
        int cleanColorTextureId,
        int cleanPositionTextureId,
        int terrainPositionTextureId,
        float[] projectionMatrix,
        float[] viewMatrix,
        float[] inverseViewMatrix,
        double floatingOriginX,
        double floatingOriginY,
        double floatingOriginZ,
        float surfaceWorldY,
        float maximumDistance,
        bool captureEntityEvidence,
        int resolutionDivisor)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        }
        if (cleanColorTextureId <= 0
            || cleanPositionTextureId <= 0
            || terrainPositionTextureId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanColorTextureId));
        }
        if (!float.IsFinite(surfaceWorldY) || !float.IsFinite(maximumDistance))
        {
            throw new ArgumentOutOfRangeException(nameof(surfaceWorldY));
        }
        if (resolutionDivisor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(resolutionDivisor));
        }

        BuildReflectedViewMatrix(
            api.Render.CameraMatrixOrigin,
            surfaceWorldY - floatingOriginY,
            mirroredCameraMatrix);
        bool mirrorProjectionReady = BuildObliqueMirrorProjection(
            api.Render.PerspectiveProjectionMat,
            mirroredCameraMatrix,
            surfaceWorldY - floatingOriginY,
            MirrorClipBiasWorldBlocks,
            obliqueProjectionMatrix,
            inverseMirrorViewScratch,
            inverseProjectionScratch);

        // The replay and the projected supplement write one depth attachment.
        // A failed oblique construction retains a consistent ordinary fallback pair.
        if (!mirrorProjectionReady)
        {
            if (projectionMatrix.Length < 16)
            {
                throw new ArgumentException("A complete fallback projection is required.", nameof(projectionMatrix));
            }
            for (int index = 0; index < 16; index++)
            {
                obliqueProjectionMatrix[index] = projectionMatrix[index];
            }
        }
        if (!TryCreateDepthProjectionPair(obliqueProjectionMatrix,
            mirrorDepthProjectionMatrix, inverseMirrorDepthProjectionMatrix,
            inverseMirrorViewScratch, inverseProjectionScratch))
        {
            if (!clipProjectionFailureLogged)
            {
                api.Logger.Warning("[VintageRTX] Mirror skipped for this frame: no finite invertible depth projection pair.");
                clipProjectionFailureLogged = true;
            }
            return false;
        }
        // Replay and shader must use the same float-representable coefficients.
        Array.Copy(inverseMirrorViewScratch, obliqueProjectionMatrix, 16);
        EnsureSize(
            Math.Max(1, (frameWidth + resolutionDivisor - 1) / resolutionDivisor),
            Math.Max(1, (frameHeight + resolutionDivisor - 1) / resolutionDivisor));
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebufferId);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(0, 0, width, height);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ProgramPointSize);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.ColorMask(true, true, true, true);
        GL.ClearBuffer(ClearBuffer.Color, 0, TransparentClear);
        GL.ClearBuffer(ClearBuffer.Depth, 0, FarDepthClear);
        if (mirrorProjectionReady)
        {
            _ = EntityMirrorGeometryReplayPatch.TryReplay(
                mirroredCameraMatrix,
                obliqueProjectionMatrix);
            clipProjectionFailureLogged = false;
        }
        else if (!clipProjectionFailureLogged)
        {
            clipProjectionFailureLogged = true;
            api.Logger.Warning(
                "[VintageRTX] Mirrored liquid clip projection is unavailable; "
                + "official world replay is skipped so submerged depth cannot occlude reflections.");
        }

        // Conservative entity supplement for future engine builds whose exact
        // renderer moves. It cannot reveal occluded faces by itself, but the
        // official replay remains beneath it whenever available.
        RenderProjectedEntities(
            framebufferId,
            false,
            frameWidth,
            frameHeight,
            cleanColorTextureId,
            cleanPositionTextureId,
            terrainPositionTextureId,
            mirrorDepthProjectionMatrix,
            viewMatrix,
            inverseViewMatrix,
            floatingOriginX,
            floatingOriginY,
            floatingOriginZ,
            surfaceWorldY,
            maximumDistance);
        if (captureEntityEvidence)
        {
            RenderProjectedEntities(
                entityEvidenceFramebufferId,
                true,
                frameWidth,
                frameHeight,
                cleanColorTextureId,
                cleanPositionTextureId,
                terrainPositionTextureId,
                mirrorDepthProjectionMatrix,
                viewMatrix,
                inverseViewMatrix,
                floatingOriginX,
                floatingOriginY,
                floatingOriginZ,
                surfaceWorldY,
                maximumDistance);
            // The primary first-person frame has no local third-person body,
            // so screen-derived points alone cannot prove its reflection.
            // Replay official entities without terrain into the evidence target
            // after the clean supplement; reflected depth keeps the result
            // entity-only while exposing occluded and lower body faces.
            if (mirrorProjectionReady)
            {
                _ = EntityMirrorGeometryReplayPatch.TryReplayEntitiesOnly(
                    mirroredCameraMatrix,
                    obliqueProjectionMatrix);
            }
        }
        return true;
    }

    /// <summary>
    /// Pairs the actual float-representable projection with its inverse before any GL work.
    /// Invalid inputs clear both outputs; an identity inverse is never fabricated on failure.
    /// </summary>
    /// <param name="source">Read-only candidate projection.</param>
    /// <param name="forward">Published float projection, cleared on failure.</param>
    /// <param name="inverse">Published inverse, cleared on failure.</param>
    /// <param name="roundedScratch">Detached storage for the float-rounded projection.</param>
    /// <param name="inverseScratch">Detached inverse storage.</param>
    /// <returns>True only for a finite invertible float projection/inverse pair.</returns>
    internal static bool TryCreateDepthProjectionPair(double[] source, float[] forward, float[] inverse,
        double[] roundedScratch, double[] inverseScratch)
    {
        Array.Clear(forward);
        Array.Clear(inverse);
        if (source.Length < 16 || forward.Length < 16 || inverse.Length < 16
            || roundedScratch.Length < 16 || inverseScratch.Length < 16) return false;
        for (int index = 0; index < 16; index++)
        {
            float value = (float)source[index];
            if (!float.IsFinite(value)) return false;
            roundedScratch[index] = value;
        }
        if (Mat4d.Invert(inverseScratch, roundedScratch) is null) return false;
        for (int index = 0; index < 16; index++)
            if (!float.IsFinite((float)inverseScratch[index])) return false;
        for (int index = 0; index < 16; index++)
        {
            forward[index] = (float)roundedScratch[index];
            inverse[index] = (float)inverseScratch[index];
        }
        return true;
    }

    /// <summary>Projects the clean late-opaque entity delta into one owned mirror framebuffer.</summary>
    /// <param name="targetFramebuffer">Destination framebuffer.</param>
    /// <param name="clearTarget">Whether to clear color/depth before drawing entity evidence.</param>
    /// <param name="frameWidth">Full source width.</param>
    /// <param name="frameHeight">Full source height.</param>
    /// <param name="cleanColorTextureId">Late-opaque color without first person.</param>
    /// <param name="cleanPositionTextureId">Matching late-opaque position.</param>
    /// <param name="terrainPositionTextureId">Position captured before entities.</param>
    /// <param name="projectionMatrix">Current projection.</param>
    /// <param name="viewMatrix">Current view transform.</param>
    /// <param name="inverseViewMatrix">Inverse view transform.</param>
    /// <param name="floatingOriginX">Floating-origin X.</param>
    /// <param name="floatingOriginY">Floating-origin Y.</param>
    /// <param name="floatingOriginZ">Floating-origin Z.</param>
    /// <param name="surfaceWorldY">Dominant liquid interface.</param>
    /// <param name="maximumDistance">Maximum entity distance.</param>
    private void RenderProjectedEntities(
        int targetFramebuffer,
        bool clearTarget,
        int frameWidth,
        int frameHeight,
        int cleanColorTextureId,
        int cleanPositionTextureId,
        int terrainPositionTextureId,
        float[] projectionMatrix,
        float[] viewMatrix,
        float[] inverseViewMatrix,
        double floatingOriginX,
        double floatingOriginY,
        double floatingOriginZ,
        float surfaceWorldY,
        float maximumDistance)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFramebuffer);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(0, 0, width, height);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ProgramPointSize);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.ColorMask(true, true, true, true);
        if (clearTarget)
        {
            GL.ClearBuffer(ClearBuffer.Color, 0, TransparentClear);
            GL.ClearBuffer(ClearBuffer.Depth, 0, FarDepthClear);
        }

        shader!.Use();
        shader.BindTexture2D("cleanColor", cleanColorTextureId, 0);
        shader.BindTexture2D("cleanPosition", cleanPositionTextureId, 1);
        shader.BindTexture2D("terrainPosition", terrainPositionTextureId, 2);
        shader.Uniform("sourceSize", (float)frameWidth, (float)frameHeight);
        shader.Uniform(
            "floatingWorldOrigin",
            (float)floatingOriginX,
            (float)floatingOriginY,
            (float)floatingOriginZ);
        shader.Uniform("surfaceWorldY", surfaceWorldY);
        shader.Uniform("maximumDistance", Math.Max(maximumDistance, 1.0f));
        shader.UniformMatrix("projection", projectionMatrix);
        shader.UniformMatrix("viewMatrix", viewMatrix);
        shader.UniformMatrix("inverseViewMatrix", inverseViewMatrix);
        GL.BindVertexArray(vertexArrayId);
        // One instance enumerates one source row and one vertex enumerates one source column.
        GL.DrawArraysInstanced(PrimitiveType.Points, 0, frameWidth, frameHeight);
        shader.Stop();
    }

    /// <summary>
    /// Composes a horizontal world reflection after the engine's floating-origin view matrix.
    /// Reflecting geometry and reflecting the camera are projectively equivalent; this form lets
    /// every official entity renderer keep its ordinary entity-local model transform.
    /// </summary>
    /// <param name="cameraMatrix">Column-major floating-origin view matrix.</param>
    /// <param name="surfaceLocalY">Liquid plane Y relative to the floating player origin.</param>
    /// <param name="destination">Sixteen-element column-major output.</param>
    internal static void BuildReflectedViewMatrix(
        double[] cameraMatrix,
        double surfaceLocalY,
        double[] destination)
    {
        if (cameraMatrix.Length < 16 || destination.Length < 16)
        {
            throw new ArgumentException("A complete 4x4 matrix is required.", nameof(cameraMatrix));
        }

        Array.Copy(cameraMatrix, destination, 16);
        for (int row = 0; row < 4; row++)
        {
            destination[4 + row] = -cameraMatrix[4 + row];
            destination[12 + row] = cameraMatrix[12 + row]
                + 2.0 * surfaceLocalY * cameraMatrix[4 + row];
        }
    }

    /// <summary>Allocates the RGBA16F colour and depth24 target and compiles its shader lazily.</summary>
    /// <param name="targetWidth">Positive tier-scaled width.</param>
    /// <param name="targetHeight">Positive tier-scaled height.</param>
    private void EnsureSize(int targetWidth, int targetHeight)
    {
        EnsureShader();
        colorTextureId = colorTextureId == 0 ? GL.GenTexture() : colorTextureId;
        depthTextureId = depthTextureId == 0 ? GL.GenTexture() : depthTextureId;
        framebufferId = framebufferId == 0 ? GL.GenFramebuffer() : framebufferId;
        entityEvidenceColorTextureId = entityEvidenceColorTextureId == 0
            ? GL.GenTexture()
            : entityEvidenceColorTextureId;
        entityEvidenceDepthTextureId = entityEvidenceDepthTextureId == 0
            ? GL.GenTexture()
            : entityEvidenceDepthTextureId;
        entityEvidenceFramebufferId = entityEvidenceFramebufferId == 0
            ? GL.GenFramebuffer()
            : entityEvidenceFramebufferId;
        vertexArrayId = vertexArrayId == 0 ? GL.GenVertexArray() : vertexArrayId;
        if (width == targetWidth && height == targetHeight)
        {
            return;
        }

        AllocateTexture(
            colorTextureId,
            PixelInternalFormat.Rgba16f,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear,
            targetWidth,
            targetHeight);
        AllocateTexture(
            depthTextureId,
            PixelInternalFormat.DepthComponent24,
            PixelFormat.DepthComponent,
            PixelType.Float,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            targetWidth,
            targetHeight);
        AllocateTexture(
            entityEvidenceColorTextureId,
            PixelInternalFormat.Rgba16f,
            PixelFormat.Rgba,
            PixelType.HalfFloat,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear,
            targetWidth,
            targetHeight);
        AllocateTexture(
            entityEvidenceDepthTextureId,
            PixelInternalFormat.DepthComponent24,
            PixelFormat.DepthComponent,
            PixelType.Float,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            targetWidth,
            targetHeight);
        AttachFramebuffer(framebufferId, colorTextureId, depthTextureId, "world mirror");
        AttachFramebuffer(
            entityEvidenceFramebufferId,
            entityEvidenceColorTextureId,
            entityEvidenceDepthTextureId,
            "entity evidence");

        width = targetWidth;
        height = targetHeight;
        api.Logger.Notification(
            "[VintageRTX] Tier-scaled entity mirror target resized to {0}x{1}.",
            width,
            height);
    }

    /// <summary>Attaches and validates one owned color/depth mirror target.</summary>
    /// <param name="targetFramebuffer">Framebuffer name.</param>
    /// <param name="targetColorTexture">RGBA16F color attachment.</param>
    /// <param name="targetDepthTexture">Depth24 attachment.</param>
    /// <param name="label">Diagnostic target label.</param>
    private static void AttachFramebuffer(
        int targetFramebuffer,
        int targetColorTexture,
        int targetDepthTexture,
        string label)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, targetFramebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            targetColorTexture,
            0);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D,
            targetDepthTexture,
            0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)
            != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"The {label} framebuffer is incomplete.");
        }
    }

    /// <summary>
    /// Replaces the OpenGL near row of a perspective projection with the liquid plane transformed
    /// into reflected-view space. The retained half-space is world Y greater than or equal to the
    /// biased interface, so submerged geometry is clipped before depth testing rather than masked
    /// after it has already hidden valid reflected scenery.
    /// </summary>
    /// <param name="projectionMatrix">Column-major OpenGL perspective projection.</param>
    /// <param name="mirroredViewMatrix">Column-major reflected floating-origin view.</param>
    /// <param name="surfaceLocalY">Liquid interface Y relative to the floating origin.</param>
    /// <param name="clipBias">Non-negative world separation above the liquid raster.</param>
    /// <param name="destination">Sixteen-element column-major oblique projection output.</param>
    /// <returns>True only when both matrices and the resulting clip plane are finite and invertible.</returns>
    internal static bool BuildObliqueMirrorProjection(
        double[] projectionMatrix,
        double[] mirroredViewMatrix,
        double surfaceLocalY,
        double clipBias,
        double[] destination)
    {
        return BuildObliqueMirrorProjection(
            projectionMatrix,
            mirroredViewMatrix,
            surfaceLocalY,
            clipBias,
            destination,
            new double[16],
            new double[16]);
    }

    /// <summary>Allocation-free implementation used by the live render path.</summary>
    /// <param name="projectionMatrix">Column-major OpenGL perspective projection.</param>
    /// <param name="mirroredViewMatrix">Column-major reflected floating-origin view.</param>
    /// <param name="surfaceLocalY">Liquid interface Y relative to the floating origin.</param>
    /// <param name="clipBias">Non-negative world separation above the liquid raster.</param>
    /// <param name="destination">Sixteen-element column-major oblique projection output.</param>
    /// <param name="inverseView">Sixteen-element reusable inverse-view storage.</param>
    /// <param name="inverseProjection">Sixteen-element reusable inverse-projection storage.</param>
    /// <returns>True when an exact OpenGL oblique near plane was produced.</returns>
    private static bool BuildObliqueMirrorProjection(
        double[] projectionMatrix,
        double[] mirroredViewMatrix,
        double surfaceLocalY,
        double clipBias,
        double[] destination,
        double[] inverseView,
        double[] inverseProjection)
    {
        if (projectionMatrix.Length < 16
            || mirroredViewMatrix.Length < 16
            || destination.Length < 16
            || inverseView.Length < 16
            || inverseProjection.Length < 16
            || !double.IsFinite(surfaceLocalY)
            || !double.IsFinite(clipBias)
            || clipBias < 0.0
            || !IsFiniteMatrix(projectionMatrix)
            || !IsFiniteMatrix(mirroredViewMatrix))
        {
            Array.Clear(destination);
            return false;
        }

        Array.Clear(inverseView);
        Array.Clear(inverseProjection);
        if (Mat4d.Invert(inverseView, mirroredViewMatrix) is null
            || Mat4d.Invert(inverseProjection, projectionMatrix) is null
            || !IsFiniteMatrix(inverseView)
            || !IsFiniteMatrix(inverseProjection))
        {
            Array.Clear(destination);
            return false;
        }

        // World/local plane (0, 1, 0, -height) retains p·x >= 0. Plane covectors
        // transform by inverse-transpose into the reflected camera's view space.
        double planeHeight = surfaceLocalY + clipBias;
        double planeD = -planeHeight;
        double planeX = inverseView[1] + inverseView[3] * planeD;
        double planeY = inverseView[5] + inverseView[7] * planeD;
        double planeZ = inverseView[9] + inverseView[11] * planeD;
        double planeW = inverseView[13] + inverseView[15] * planeD;
        double normalLength = Math.Sqrt(
            planeX * planeX + planeY * planeY + planeZ * planeZ);
        if (!IsUsableNormalLength(normalLength))
        {
            Array.Clear(destination);
            return false;
        }

        planeX /= normalLength;
        planeY /= normalLength;
        planeZ /= normalLength;
        planeW /= normalLength;
        // The oblique near-plane derivation assumes the reflected camera lies
        // on the rejected side. Underwater/coplanar cameras require a distinct
        // internal-reflection model, so suppress this above-water replay rather
        // than reversing the half-space or exposing submerged depth.
        if (planeW >= -1e-6)
        {
            Array.Clear(destination);
            return false;
        }

        // In OpenGL clip space the far corner opposite the new near plane is
        // (sign(A), sign(B), 1, 1). Its inverse projection supplies Lengyel's q.
        double cornerX = CopySignOne(planeX);
        double cornerY = CopySignOne(planeY);
        const double cornerZ = 1.0;
        const double cornerW = 1.0;
        double qX = inverseProjection[0] * cornerX
            + inverseProjection[4] * cornerY
            + inverseProjection[8] * cornerZ
            + inverseProjection[12] * cornerW;
        double qY = inverseProjection[1] * cornerX
            + inverseProjection[5] * cornerY
            + inverseProjection[9] * cornerZ
            + inverseProjection[13] * cornerW;
        double qZ = inverseProjection[2] * cornerX
            + inverseProjection[6] * cornerY
            + inverseProjection[10] * cornerZ
            + inverseProjection[14] * cornerW;
        double qW = inverseProjection[3] * cornerX
            + inverseProjection[7] * cornerY
            + inverseProjection[11] * cornerZ
            + inverseProjection[15] * cornerW;
        double denominator = planeX * qX
            + planeY * qY
            + planeZ * qZ
            + planeW * qW;
        if (!IsUsableClipDenominator(denominator))
        {
            Array.Clear(destination);
            return false;
        }

        double scale = 2.0 / denominator;
        double clipX = planeX * scale;
        double clipY = planeY * scale;
        double clipZ = planeZ * scale;
        double clipW = planeW * scale;
        if (!AreFiniteClipCoefficients(clipX, clipY, clipZ, clipW))
        {
            Array.Clear(destination);
            return false;
        }

        if (!TryWriteObliqueProjection(
            projectionMatrix, clipX, clipY, clipZ, clipW, destination)
            || Mat4d.Invert(inverseProjection, destination) is null
            || !IsFiniteMatrix(inverseProjection))
        {
            // Finite coefficients alone do not prove invertibility. In particular,
            // a non-perspective fixture can produce two linearly dependent rows.
            Array.Clear(destination);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Writes the derived clip plane into the third projection row and rejects finite operands whose
    /// subtraction overflows. Keeping this final numerical boundary isolated makes the defensive
    /// overflow path deterministic without relaxing any live matrix validation.
    /// </summary>
    /// <param name="projectionMatrix">Prevalidated finite sixteen-element source projection.</param>
    /// <param name="clipX">Finite normalized clip-plane X coefficient.</param>
    /// <param name="clipY">Finite normalized clip-plane Y coefficient.</param>
    /// <param name="clipZ">Finite normalized clip-plane Z coefficient.</param>
    /// <param name="clipW">Finite normalized clip-plane W coefficient.</param>
    /// <param name="destination">Prevalidated sixteen-element destination.</param>
    /// <returns>True when every written projection coefficient remains finite.</returns>
    internal static bool TryWriteObliqueProjection(
        double[] projectionMatrix,
        double clipX,
        double clipY,
        double clipZ,
        double clipW,
        double[] destination)
    {
        Array.Copy(projectionMatrix, destination, 16);
        // Column-major indices 2/6/10/14 form the projection's third row.
        // Subtracting row four makes z_clip + w_clip equal the retained plane.
        destination[2] = clipX - projectionMatrix[3];
        destination[6] = clipY - projectionMatrix[7];
        destination[10] = clipZ - projectionMatrix[11];
        destination[14] = clipW - projectionMatrix[15];
        if (!IsFiniteMatrix(destination))
        {
            Array.Clear(destination);
            return false;
        }

        return true;
    }

    /// <summary>Checks that a transformed plane normal can be normalized safely.</summary>
    /// <param name="normalLength">Computed Euclidean normal length.</param>
    /// <returns>True only for a finite, non-degenerate length.</returns>
    internal static bool IsUsableNormalLength(double normalLength) =>
        double.IsFinite(normalLength) && normalLength > 1e-10;

    /// <summary>Checks the Lengyel plane/corner denominator before reciprocal scaling.</summary>
    /// <param name="denominator">Plane dot opposite clip-space corner.</param>
    /// <returns>True only for a finite denominator separated from zero.</returns>
    internal static bool IsUsableClipDenominator(double denominator) =>
        double.IsFinite(denominator) && Math.Abs(denominator) > 1e-10;

    /// <summary>Checks all four scaled clip-plane coefficients before projection-row writes.</summary>
    /// <param name="clipX">Scaled X coefficient.</param>
    /// <param name="clipY">Scaled Y coefficient.</param>
    /// <param name="clipZ">Scaled Z coefficient.</param>
    /// <param name="clipW">Scaled W coefficient.</param>
    /// <returns>True only when every coefficient is finite.</returns>
    internal static bool AreFiniteClipCoefficients(
        double clipX,
        double clipY,
        double clipZ,
        double clipW) =>
        double.IsFinite(clipX)
        && double.IsFinite(clipY)
        && double.IsFinite(clipZ)
        && double.IsFinite(clipW);

    /// <summary>Returns +1 for zero/positive values and -1 for negative values.</summary>
    /// <param name="value">Finite plane component.</param>
    /// <returns>Signed unit corner coordinate.</returns>
    private static double CopySignOne(double value) => value < 0.0 ? -1.0 : 1.0;

    /// <summary>Checks the first sixteen entries of a column-major matrix for finite values.</summary>
    /// <param name="matrix">Matrix storage already known to contain at least sixteen entries.</param>
    /// <returns>True only when all matrix coefficients are finite.</returns>
    private static bool IsFiniteMatrix(double[] matrix)
    {
        for (int index = 0; index < 16; index++)
        {
            if (!double.IsFinite(matrix[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Allocates one clamp-to-edge two-dimensional target texture.</summary>
    /// <param name="textureId">Owned texture name.</param>
    /// <param name="internalFormat">Sized storage format.</param>
    /// <param name="pixelFormat">External component layout.</param>
    /// <param name="pixelType">External scalar type.</param>
    /// <param name="minimumFilter">Minification filter.</param>
    /// <param name="magnificationFilter">Magnification filter.</param>
    /// <param name="targetWidth">Allocation width.</param>
    /// <param name="targetHeight">Allocation height.</param>
    private static void AllocateTexture(
        int textureId,
        PixelInternalFormat internalFormat,
        PixelFormat pixelFormat,
        PixelType pixelType,
        TextureMinFilter minimumFilter,
        TextureMagFilter magnificationFilter,
        int targetWidth,
        int targetHeight)
    {
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)minimumFilter);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)magnificationFilter);
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
            targetWidth,
            targetHeight,
            0,
            pixelFormat,
            pixelType,
            IntPtr.Zero);
    }

    /// <summary>Loads and compiles the asset-backed projection program once.</summary>
    private void EnsureShader()
    {
        if (shader is not null && !shader.Disposed)
        {
            return;
        }

        if (!api.Shader.IsGLSLVersionSupported("330"))
        {
            throw new NotSupportedException("Entity mirror projection requires GLSL 3.30 or newer.");
        }

        DisplayShaderProgramSource source = EntityMirrorShaderSource.Load(api.Assets);
        IShaderProgram program = api.Shader.NewShaderProgram();
        program.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
        program.VertexShader.Code = source.Vertex;
        program.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
        program.FragmentShader.Code = source.Fragment;
        int passId = api.Shader.RegisterMemoryShaderProgram(ShaderName, program);
        bool compiled = program.Compile();
        if (passId < 0 || !compiled || program.LoadError || program.Disposed)
        {
            throw new InvalidOperationException(
                $"The entity mirror shader did not compile ({EntityMirrorShaderSource.VertexAssetCode}, "
                + $"{EntityMirrorShaderSource.FragmentAssetCode}).");
        }

        shader = program;
    }

    /// <summary>Deletes all owned OpenGL targets; the engine shader registry owns the program.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        DeleteTexture(ref colorTextureId);
        DeleteTexture(ref depthTextureId);
        DeleteTexture(ref entityEvidenceColorTextureId);
        DeleteTexture(ref entityEvidenceDepthTextureId);
        if (framebufferId != 0)
        {
            GL.DeleteFramebuffer(framebufferId);
            framebufferId = 0;
        }
        if (entityEvidenceFramebufferId != 0)
        {
            GL.DeleteFramebuffer(entityEvidenceFramebufferId);
            entityEvidenceFramebufferId = 0;
        }
        if (vertexArrayId != 0)
        {
            GL.DeleteVertexArray(vertexArrayId);
            vertexArrayId = 0;
        }

        shader = null;
        width = 0;
        height = 0;
        disposed = true;
    }

    /// <summary>Deletes one optional texture name and clears its slot.</summary>
    /// <param name="textureId">Owned texture slot.</param>
    private static void DeleteTexture(ref int textureId)
    {
        if (textureId == 0)
        {
            return;
        }

        GL.DeleteTexture(textureId);
        textureId = 0;
    }
}
