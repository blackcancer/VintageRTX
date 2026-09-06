using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Mirrors file-backed entity sidecars into the animated-entity atlas. The bridge deliberately
/// targets only the dedicated entity atlas: held items can alternate between block and item atlases
/// inside the same shader and therefore require a later verified per-draw atlas-ownership hook.
/// </summary>
internal sealed class PbrEntityRenderer : IRenderer
{
    /// <summary>UNorm8 roughness used outside rectangles carrying an entity sidecar suite.</summary>
    private const byte NeutralRoughness = 184;
    /// <summary>
    /// Reserved material bit identifying an uploaded entity rectangle. Terrain uses the same bit
    /// for vegetation, but the negative G-buffer marker makes the two meanings unambiguous.
    /// </summary>
    private const byte EntitySurfaceBit = 128;

    private readonly ICoreClientAPI api;
    private readonly PbrSidecarAssetStore sidecarAssets;
    private int materialAtlasTexture;
    private int sourceAtlasTexture;
    private int sourceAtlasWidth;
    private int sourceAtlasHeight;
    private int materialTextureUnit = -1;
    private bool loaded;
    private bool faulted;
    private bool overrideAvailable;
    private int sidecarOverrideCount;
    private ulong sourceAtlasRevision;
    /// <summary>Uncommitted entity-atlas fingerprint currently accumulating stability evidence.</summary>
    private ulong pendingAtlasRevision;
    /// <summary>Consecutive rendered frames for which <see cref="pendingAtlasRevision"/> was unchanged.</summary>
    private int pendingAtlasRevisionFrames;
    private PbrAtlasSafetyKind observedAtlasSafety = PbrAtlasSafetyKind.Waiting;
    private string status = "waiting for entity atlas";

    /// <summary>Creates a lazy entity-atlas bridge without issuing OpenGL calls during startup.</summary>
    /// <param name="api">Client renderer, assets, shader registry, and entity atlas.</param>
    /// <param name="sidecarAssets">Indexed PBR sidecars that remain visible to their authoring mods.</param>
    public PbrEntityRenderer(ICoreClientAPI api, PbrSidecarAssetStore sidecarAssets)
    {
        this.api = api;
        this.sidecarAssets = sidecarAssets;
    }

    /// <summary>Runs after terrain PBR state is bound and before ordinary opaque entity rendering.</summary>
    public double RenderOrder => 0.365;

    /// <summary>Requests every frame because the renderer only maintains shared shader state.</summary>
    public int RenderRange => 0;

    /// <summary>Gets atlas readiness, applied entity rectangles, or a sticky fault reason.</summary>
    public string Status => faulted ? $"faulted: {status}" : status;

    /// <summary>Builds and binds entity material state during the opaque stage.</summary>
    /// <param name="deltaTime">Unused frame duration required by <see cref="IRenderer"/>.</param>
    /// <param name="stage">Current Vintage Story render stage.</param>
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Opaque || faulted)
        {
            return;
        }

        try
        {
            EnsurePbrAtlas();
            SetEntityShaderState(loaded);
        }
        catch (Exception exception)
        {
            faulted = true;
            status = exception.Message;
            api.Logger.Error("[VintageRTX] PBR entity bridge disabled: {0}", exception);
        }
    }

    /// <summary>Clears shader-availability/fault state while retaining the expensive material atlas.</summary>
    /// <returns>Always <see langword="true"/> because rebinding is deferred to the next frame.</returns>
    public bool ReloadShader()
    {
        overrideAvailable = false;
        faulted = false;
        status = loaded ? "file-backed entity PBR atlas ready; shader reload pending" : "waiting for entity atlas";
        return true;
    }

    /// <summary>Creates or rebuilds the mirrored material atlas after a proven source-atlas change.</summary>
    private void EnsurePbrAtlas()
    {
        ITextureAtlasAPI entityAtlas = api.EntityTextureAtlas;
        PbrAtlasSafetyResult atlasSafety = PbrTerrainRenderer.InspectAtlasSafety(
            entityAtlas.AtlasTextures,
            entityAtlas.Positions,
            entityAtlas.UnknownTexturePosition);
        if (!atlasSafety.IsSinglePageProven)
        {
            bool safetyChanged = observedAtlasSafety != atlasSafety.Kind
                || sourceAtlasRevision != atlasSafety.Revision;
            if (loaded || materialAtlasTexture != 0)
            {
                ReleaseMaterialAtlas();
            }

            sourceAtlasRevision = atlasSafety.Revision;
            observedAtlasSafety = atlasSafety.Kind;
            status = atlasSafety.Kind == PbrAtlasSafetyKind.Waiting
                ? "waiting for entity atlas"
                : atlasSafety.Reason.Replace("terrain", "entity", StringComparison.Ordinal);
            if (safetyChanged && atlasSafety.IsUnsafe)
            {
                api.Logger.Warning("[VintageRTX] {0}", status);
            }

            return;
        }

        if (loaded && sourceAtlasRevision == atlasSafety.Revision)
        {
            ResetPendingAtlasRevision();
            return;
        }

        if (loaded && !ObserveStableAtlasRevision(atlasSafety.Revision))
        {
            status = $"file-backed entity PBR atlas retained while revision settles ({pendingAtlasRevisionFrames}/{PbrTerrainRenderer.AtlasRevisionStabilityFrames})";
            return;
        }

        bool rebuilding = loaded || materialAtlasTexture != 0;
        if (rebuilding)
        {
            ReleaseMaterialAtlas();
        }

        LoadedTexture sourceAtlas = entityAtlas.AtlasTextures[0];
        sourceAtlasTexture = sourceAtlas.TextureId;
        sourceAtlasWidth = sourceAtlas.Width;
        sourceAtlasHeight = sourceAtlas.Height;
        sourceAtlasRevision = atlasSafety.Revision;
        observedAtlasSafety = atlasSafety.Kind;
        GL.GetInteger(GetPName.MaxTextureImageUnits, out int maximumFragmentTextureUnits);
        materialTextureUnit = SelectEntityPbrTextureUnit(maximumFragmentTextureUnits);
        materialAtlasTexture = CreateNeutralAtlas();

        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTexture0);
        try
        {
            sidecarOverrideCount = ApplySidecarOverrides(entityAtlas);
            api.Render.CheckGlError("VintageRTX file-backed entity PBR atlas loading");
        }
        finally
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture0);
            GL.ActiveTexture((TextureUnit)previousActiveTexture);
        }

        loaded = true;
        status = $"file-backed {sourceAtlasWidth}x{sourceAtlasHeight} entity PBR atlas; sidecars={sidecarOverrideCount}";
        api.Logger.Notification(
            "[VintageRTX] File-backed entity PBR atlas {0} ({1}x{2}); sidecar overrides={3}, sampler unit={4}, revision={5}.",
            rebuilding ? "rebuilt after atlas reload" : "loaded",
            sourceAtlasWidth,
            sourceAtlasHeight,
            sidecarOverrideCount,
            materialTextureUnit,
            sourceAtlasRevision);
    }

    /// <summary>
    /// Debounces replacement entity-atlas layouts so progressive skin and texture discovery cannot
    /// repeatedly replace normal, roughness, metallic, and emissive responses on visible entities.
    /// </summary>
    /// <param name="observedRevision">Current complete entity-atlas layout fingerprint.</param>
    /// <returns>Whether the replacement layout persisted for the shared stability window.</returns>
    private bool ObserveStableAtlasRevision(ulong observedRevision)
    {
        if (pendingAtlasRevision != observedRevision)
        {
            pendingAtlasRevision = observedRevision;
            pendingAtlasRevisionFrames = 1;
            return false;
        }

        pendingAtlasRevisionFrames++;
        if (pendingAtlasRevisionFrames < PbrTerrainRenderer.AtlasRevisionStabilityFrames)
        {
            return false;
        }

        ResetPendingAtlasRevision();
        return true;
    }

    /// <summary>Clears uncommitted entity-atlas revision evidence.</summary>
    private void ResetPendingAtlasRevision()
    {
        pendingAtlasRevision = 0;
        pendingAtlasRevisionFrames = 0;
    }

    /// <summary>Allocates and clears the RGBA8 entity material atlas through a temporary framebuffer.</summary>
    /// <returns>Owned OpenGL texture handle with source-atlas dimensions.</returns>
    private int CreateNeutralAtlas()
    {
        GL.GetInteger(GetPName.FramebufferBinding, out int previousFramebuffer);
        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTexture0);

        int texture = GL.GenTexture();
        int framebuffer = GL.GenFramebuffer();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                sourceAtlasWidth,
                sourceAtlasHeight,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException("The file-backed entity PBR atlas framebuffer is incomplete.");
            }

            GL.ClearBuffer(
                ClearBuffer.Color,
                0,
                new[] { 0.5f, 0.5f, NeutralRoughness / 255.0f, 0.0f });
            return texture;
        }
        catch
        {
            GL.DeleteTexture(texture);
            throw;
        }
        finally
        {
            GL.DeleteFramebuffer(framebuffer);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture0);
            GL.ActiveTexture((TextureUnit)previousActiveTexture);
        }
    }

    /// <summary>Uploads exact adjacent sidecars only into matching, proven entity-atlas rectangles.</summary>
    /// <param name="entityAtlas">Dedicated animated-entity texture atlas.</param>
    /// <returns>Number of entity atlas rectangles populated.</returns>
    private int ApplySidecarOverrides(ITextureAtlasAPI entityAtlas)
    {
        int applied = 0;
        foreach (AssetLocation normalLocation in sidecarAssets.NormalLocations)
        {
            if (!IsEntityNormalSidecar(normalLocation))
            {
                continue;
            }

            string stem = normalLocation.Path[..^6];
            AssetLocation sourceLocation = new(normalLocation.Domain, stem + ".png");
            if (api.Assets.TryGet(sourceLocation) is null
                || !sidecarAssets.TryGet(normalLocation, out IAsset? normalAsset))
            {
                continue;
            }

            AssetLocation atlasLocation = ToEntityAtlasLocation(sourceLocation);
            TextureAtlasPosition? position = entityAtlas[atlasLocation];
            if (!IsUsableAtlasPosition(
                position,
                entityAtlas.UnknownTexturePosition,
                sourceAtlasTexture))
            {
                continue;
            }

            _ = sidecarAssets.TryGet(new AssetLocation(normalLocation.Domain, stem + "_r.png"), out IAsset? roughnessAsset);
            _ = sidecarAssets.TryGet(new AssetLocation(normalLocation.Domain, stem + "_m.png"), out IAsset? metallicAsset);
            _ = sidecarAssets.TryGet(new AssetLocation(normalLocation.Domain, stem + "_e.png"), out IAsset? emissiveAsset);
            UploadOverride(
                position!,
                normalAsset!.Data,
                roughnessAsset?.Data,
                metallicAsset?.Data,
                emissiveAsset?.Data);
            applied++;
        }

        return applied;
    }

    /// <summary>Recognizes only adjacent normal maps in the dedicated entity texture namespace.</summary>
    /// <param name="location">Candidate indexed sidecar.</param>
    /// <returns>Whether the location can map to an animated-entity albedo.</returns>
    internal static bool IsEntityNormalSidecar(AssetLocation location) =>
        location.Path.StartsWith("textures/entity/", StringComparison.Ordinal)
        && location.Path.EndsWith("_n.png", StringComparison.Ordinal);

    /// <summary>Converts a canonical asset path into the key used by the public entity atlas indexer.</summary>
    /// <param name="sourceLocation">Canonical <c>domain:textures/entity/...png</c> source identity.</param>
    /// <returns>Domain-preserving, extension-free <c>entity/...</c> atlas identity.</returns>
    internal static AssetLocation ToEntityAtlasLocation(AssetLocation sourceLocation)
    {
        const string texturePrefix = "textures/";
        string path = sourceLocation.Path.Replace('\\', '/').ToLowerInvariant();
        if (!path.StartsWith(texturePrefix, StringComparison.Ordinal)
            || !path.EndsWith(".png", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An entity atlas source must be a canonical textures/*.png asset.",
                nameof(sourceLocation));
        }

        return new AssetLocation(sourceLocation.Domain, path[texturePrefix.Length..^4]);
    }

    /// <summary>Checks exact page, texture ownership, and unknown-sentinel exclusion.</summary>
    /// <param name="position">Candidate entity-atlas rectangle.</param>
    /// <param name="unknownPosition">Atlas-specific missing-texture sentinel.</param>
    /// <param name="sourceTextureId">Borrowed page-zero entity atlas texture.</param>
    /// <returns>Whether a material upload can target the rectangle without cross-texture corruption.</returns>
    internal static bool IsUsableAtlasPosition(
        TextureAtlasPosition? position,
        TextureAtlasPosition? unknownPosition,
        int sourceTextureId) => position is not null
            && !ReferenceEquals(position, unknownPosition)
            && position.atlasNumber == 0
            && position.atlasTextureId == sourceTextureId;

    /// <summary>Resamples and uploads one entity sidecar suite, marking only that rectangle present.</summary>
    /// <param name="position">Normalized destination rectangle.</param>
    /// <param name="normalPng">Required tangent-space normal PNG.</param>
    /// <param name="roughnessPng">Optional scalar roughness PNG.</param>
    /// <param name="metallicPng">Optional scalar metallic PNG.</param>
    /// <param name="emissivePng">Optional scalar emissive PNG.</param>
    private void UploadOverride(
        TextureAtlasPosition position,
        byte[] normalPng,
        byte[]? roughnessPng,
        byte[]? metallicPng,
        byte[]? emissivePng)
    {
        int targetX = (int)MathF.Round(position.x1 * sourceAtlasWidth);
        int targetY = (int)MathF.Round(position.y1 * sourceAtlasHeight);
        int targetWidth = Math.Max(1, (int)MathF.Round((position.x2 - position.x1) * sourceAtlasWidth));
        int targetHeight = Math.Max(1, (int)MathF.Round((position.y2 - position.y1) * sourceAtlasHeight));
        byte[] pixels = PbrTerrainRenderer.BuildConsolidatedPbrPixels(
            normalPng,
            roughnessPng,
            metallicPng,
            emissivePng,
            targetWidth,
            targetHeight,
            PbrTerrainRenderer.GeneratedFallbackMaximumSlope,
            PbrMaterialProvenance.Generated);
        MarkEntitySurfacePresence(pixels);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, materialAtlasTexture);
        GL.TexSubImage2D(
            TextureTarget.Texture2D,
            0,
            targetX,
            targetY,
            targetWidth,
            targetHeight,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            pixels);
    }

    /// <summary>Sets the reserved entity-presence bit in every packed RGBA8 texel.</summary>
    /// <param name="pixels">Tightly packed RGBA8 material pixels.</param>
    /// <exception cref="ArgumentException">The byte count is not a whole number of RGBA texels.</exception>
    internal static void MarkEntitySurfacePresence(byte[] pixels)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException("Packed entity material data must contain complete RGBA texels.", nameof(pixels));
        }

        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] |= EntitySurfaceBit;
        }
    }

    /// <summary>Selects a second high sampler unit distinct from the terrain PBR sampler.</summary>
    /// <param name="maximumFragmentTextureUnits">OpenGL fragment sampler capacity.</param>
    /// <returns>A safe entity material sampler unit.</returns>
    /// <exception cref="InvalidOperationException">No second safe unit exists above vanilla units 0..6.</exception>
    internal static int SelectEntityPbrTextureUnit(int maximumFragmentTextureUnits)
    {
        int terrainUnit = PbrTerrainRenderer.SelectPbrTextureUnit(maximumFragmentTextureUnits);
        for (int unit = maximumFragmentTextureUnits - 1; unit >= 7; unit--)
        {
            if (unit == terrainUnit || unit is 15 or 16)
            {
                continue;
            }

            return unit;
        }

        throw new InvalidOperationException(
            $"A second safe fragment texture unit is required for entity PBR; OpenGL reported {maximumFragmentTextureUnits}.");
    }

    /// <summary>Binds or explicitly disables the material sampler on the live animated-entity program.</summary>
    /// <param name="enablePbr">Whether atlas ownership and the owned material texture are valid.</param>
    private void SetEntityShaderState(bool enablePbr)
    {
        IShaderProgram entityShader = api.Shader.GetProgram((int)EnumShaderProgram.Entityanimated);
        if (entityShader is null || entityShader.Disposed || entityShader.ProgramId <= 0)
        {
            return;
        }

        int materialSampler = GL.GetUniformLocation(entityShader.ProgramId, "vintagertxEntityMaterialTex");
        int enabledUniform = GL.GetUniformLocation(entityShader.ProgramId, "vintagertxEntityPbrEnabled");
        if (materialSampler < 0 || enabledUniform < 0)
        {
            if (enablePbr && !overrideAvailable)
            {
                status = "file-backed entity PBR atlas ready; shader override unavailable";
                api.Logger.Warning("[VintageRTX] PBR animated-entity shader uniforms are unavailable.");
            }

            return;
        }

        overrideAvailable = true;
        GL.GetInteger(GetPName.CurrentProgram, out int previousProgram);
        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.UseProgram(entityShader.ProgramId);
        GL.Uniform1(enabledUniform, enablePbr ? 1 : 0);
        if (enablePbr)
        {
            GL.Uniform1(materialSampler, materialTextureUnit);
            GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + materialTextureUnit));
            GL.BindTexture(TextureTarget.Texture2D, materialAtlasTexture);
        }

        GL.ActiveTexture((TextureUnit)previousActiveTexture);
        GL.UseProgram(previousProgram);
    }

    /// <summary>Deletes and forgets the owned entity material atlas.</summary>
    private void ReleaseMaterialAtlas()
    {
        if (materialAtlasTexture != 0)
        {
            GL.DeleteTexture(materialAtlasTexture);
            materialAtlasTexture = 0;
        }

        loaded = false;
        sourceAtlasTexture = 0;
        sourceAtlasWidth = 0;
        sourceAtlasHeight = 0;
        sidecarOverrideCount = 0;
    }

    /// <summary>Deletes the owned material atlas; the borrowed vanilla entity atlas is untouched.</summary>
    public void Dispose()
    {
        ReleaseMaterialAtlas();
        sourceAtlasRevision = 0;
        ResetPendingAtlasRevision();
        observedAtlasSafety = PbrAtlasSafetyKind.Waiting;
    }
}
