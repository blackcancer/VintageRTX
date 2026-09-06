using System.Security.Cryptography;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Loads precomputed normal/roughness files into an atlas matching the terrain atlas.
/// No normal-map generation is performed in the game process.
/// </summary>
internal sealed class PbrTerrainRenderer : IRenderer
{
    /// <summary>
    /// Consecutive identical rendered revisions required before replacing an already usable PBR
    /// atlas. This spans two seconds at 60 FPS and prevents progressive asset loading from causing
    /// a succession of visible material and lighting changes.
    /// </summary>
    internal const int AtlasRevisionStabilityFrames = 120;
    /// <summary>Vintage Story asset category searched for generated/authored manifests.</summary>
    private const string ManifestAssetCategory = "config";
    /// <summary>Canonical manifest path inside each asset domain.</summary>
    private const string ManifestAssetPath = "vintagertx/pbr-manifest.json";
    /// <summary>UNorm8 roughness fallback used when a normal has no authored roughness map.</summary>
    private const byte NeutralRoughness = 184;
    /// <summary>
    /// Maximum tangent-space slope retained for albedo-derived manifest fallbacks. At 0.28 the
    /// largest tilt is 15.6 degrees, so opposite generated gradients cannot cross the runtime's
    /// 35-degree embossed-shimmer boundary. Authored sidecars are deliberately not clamped.
    /// </summary>
    internal const float GeneratedFallbackMaximumSlope = 0.28f;

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
    private int manifestOverrideCount;
    /// <summary>Reload-sensitive fingerprint of the currently observed terrain atlas layout.</summary>
    private ulong sourceAtlasRevision;
    /// <summary>Uncommitted terrain-atlas fingerprint currently accumulating stability evidence.</summary>
    private ulong pendingAtlasRevision;
    /// <summary>Consecutive rendered frames for which <see cref="pendingAtlasRevision"/> was unchanged.</summary>
    private int pendingAtlasRevisionFrames;
    /// <summary>Last page-ownership classification, used to log only safety transitions.</summary>
    private PbrAtlasSafetyKind observedAtlasSafety = PbrAtlasSafetyKind.Waiting;
    private Dictionary<AssetLocation, TextureAtlasPosition[]>? sourceAtlasPositions;
    private HashSet<AssetLocation>? ambiguousAtlasSources;
    private string status = "waiting for terrain atlas";

    /// <summary>Creates the lazy terrain-atlas bridge without issuing OpenGL calls before rendering.</summary>
    /// <param name="api">Client renderer, assets, shader registry, and block atlas.</param>
    /// <param name="sidecarAssets">Private index of globally visible PBR sidecar assets.</param>
    public PbrTerrainRenderer(ICoreClientAPI api, PbrSidecarAssetStore sidecarAssets)
    {
        this.api = api;
        this.sidecarAssets = sidecarAssets;
    }

    /// <summary>Runs after the opaque terrain program exists but before later post-processing stages.</summary>
    public double RenderOrder => 0.36;

    /// <summary>Requests rendering at every player distance because this service only binds shared state.</summary>
    public int RenderRange => 0;

    /// <summary>Gets atlas readiness, override counts, or a sticky fault reason.</summary>
    public string Status => faulted ? $"faulted: {status}" : status;

    /// <summary>Builds/binds the consolidated material atlas during the opaque stage.</summary>
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
            if (loaded)
            {
                BindTerrainShader();
            }
            else
            {
                DisableTerrainShader();
            }
        }
        catch (Exception exception)
        {
            faulted = true;
            status = exception.Message;
            api.Logger.Error("[VintageRTX] PBR terrain bridge disabled: {0}", exception);
        }
    }

    /// <summary>Clears shader-availability/fault state while retaining the expensive material atlas.</summary>
    /// <returns>Always <see langword="true"/> because rebinding is deferred to the next frame.</returns>
    public bool ReloadShader()
    {
        overrideAvailable = false;
        faulted = false;
        status = loaded ? "file-backed PBR atlas ready; shader reload pending" : "waiting for terrain atlas";
        return true;
    }

    /// <summary>
    /// Lazily mirrors atlas dimensions, selects a safe sampler unit, uploads authored sidecars and
    /// manifests, and restores every OpenGL binding touched during setup.
    /// </summary>
    private void EnsurePbrAtlas()
    {
        PbrAtlasSafetyResult atlasSafety = InspectAtlasSafety(
            api.BlockTextureAtlas.AtlasTextures,
            api.BlockTextureAtlas.Positions,
            api.BlockTextureAtlas.UnknownTexturePosition);
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
            status = atlasSafety.Reason;
            if (safetyChanged && atlasSafety.IsUnsafe)
            {
                api.Logger.Warning("[VintageRTX] {0}", atlasSafety.Reason);
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
            status = $"file-backed PBR atlas retained while revision settles ({pendingAtlasRevisionFrames}/{AtlasRevisionStabilityFrames})";
            return;
        }

        bool rebuilding = loaded || materialAtlasTexture != 0;
        if (rebuilding)
        {
            ReleaseMaterialAtlas();
        }

        LoadedTexture sourceAtlas = api.BlockTextureAtlas.AtlasTextures[0];
        sourceAtlasTexture = sourceAtlas.TextureId;
        sourceAtlasWidth = sourceAtlas.Width;
        sourceAtlasHeight = sourceAtlas.Height;
        sourceAtlasRevision = atlasSafety.Revision;
        observedAtlasSafety = atlasSafety.Kind;
        GL.GetInteger(GetPName.MaxTextureImageUnits, out int maximumFragmentTextureUnits);
        materialTextureUnit = SelectPbrTextureUnit(maximumFragmentTextureUnits);
        api.Logger.Notification(
            "[VintageRTX] Consolidated PBR terrain sampler: unit={0}, fragment units={1}.",
            materialTextureUnit,
            maximumFragmentTextureUnits);
        materialAtlasTexture = CreateNeutralAtlas(
            [0.5f, 0.5f, NeutralRoughness / 255.0f, 0.0f]);
        PbrAtlasLookup atlasLookup = BuildSourceAtlasPositionLookup();
        sourceAtlasPositions = atlasLookup.Positions;
        ambiguousAtlasSources = atlasLookup.AmbiguousSources;

        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTexture0);
        try
        {
            HashSet<AssetLocation> manifestFallbackSources = [];
            HashSet<AssetLocation> rejectedGeneratedSources = [];
            Dictionary<AssetLocation, HashSet<string>> manifestFallbackHashes = [];
            manifestOverrideCount = ApplyManifestOverrides(
                manifestFallbackSources,
                manifestFallbackHashes,
                rejectedGeneratedSources);
            sidecarOverrideCount = ApplySidecarOverrides(
                manifestFallbackSources,
                manifestFallbackHashes,
                rejectedGeneratedSources);
            api.Render.CheckGlError("VintageRTX file-backed PBR atlas loading");
        }
        finally
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture0);
            GL.ActiveTexture((TextureUnit)previousActiveTexture);
        }

        loaded = true;
        status = $"file-backed {sourceAtlasWidth}x{sourceAtlasHeight} PBR atlas; sidecars={sidecarOverrideCount}, manifests={manifestOverrideCount}";
        api.Logger.Notification(
            "[VintageRTX] File-backed PBR atlas {0} ({1}x{2}); sidecar overrides={3}, manifest overrides={4}, revision={5}, runtime generation=disabled.",
            rebuilding ? "rebuilt after atlas reload" : "loaded",
            sourceAtlasWidth,
            sourceAtlasHeight,
            sidecarOverrideCount,
            manifestOverrideCount,
            sourceAtlasRevision);
    }

    /// <summary>
    /// Debounces a replacement atlas layout while continuing to render the last complete material
    /// atlas. A changing revision restarts the evidence window; only one stable terminal layout is
    /// committed after progressive texture loading.
    /// </summary>
    /// <param name="observedRevision">Current complete layout fingerprint.</param>
    /// <returns>Whether the same replacement revision has persisted for the required frame window.</returns>
    private bool ObserveStableAtlasRevision(ulong observedRevision)
    {
        if (pendingAtlasRevision != observedRevision)
        {
            pendingAtlasRevision = observedRevision;
            pendingAtlasRevisionFrames = 1;
            return false;
        }

        pendingAtlasRevisionFrames++;
        if (pendingAtlasRevisionFrames < AtlasRevisionStabilityFrames)
        {
            return false;
        }

        ResetPendingAtlasRevision();
        return true;
    }

    /// <summary>Clears uncommitted atlas-revision evidence after reuse, replacement, or disposal.</summary>
    private void ResetPendingAtlasRevision()
    {
        pendingAtlasRevision = 0;
        pendingAtlasRevisionFrames = 0;
    }

    /// <summary>
    /// Proves that every terrain rectangle belongs to the sole live atlas page and fingerprints
    /// texture identity, placement, and <see cref="TextureAtlasPosition.reloadIteration"/>.
    /// Multiple pages are deliberately rejected because the public API exposes no safe callback
    /// at the batch boundary where the engine changes the albedo atlas binding.
    /// </summary>
    /// <param name="atlasTextures">Live terrain atlas pages in engine order.</param>
    /// <param name="positions">Atlas rectangles indexed by texture sub-ID.</param>
    /// <param name="unknownPosition">Sentinel that does not represent renderable material ownership.</param>
    /// <returns>A deterministic safety decision and reload-sensitive layout revision.</returns>
    internal static PbrAtlasSafetyResult InspectAtlasSafety(
        IReadOnlyList<LoadedTexture> atlasTextures,
        IReadOnlyList<TextureAtlasPosition?> positions,
        TextureAtlasPosition? unknownPosition)
    {
        ulong revision = 14695981039346656037UL;
        revision = MixAtlasRevision(revision, atlasTextures.Count);
        foreach (LoadedTexture texture in atlasTextures)
        {
            revision = MixAtlasRevision(revision, texture.TextureId);
            revision = MixAtlasRevision(revision, texture.Width);
            revision = MixAtlasRevision(revision, texture.Height);
            revision = MixAtlasRevision(revision, texture.Disposed ? 1 : 0);
        }

        revision = MixAtlasRevision(revision, positions.Count);
        bool foreignPagePosition = false;
        int primaryTextureId = atlasTextures.Count == 1 ? atlasTextures[0].TextureId : 0;
        foreach (TextureAtlasPosition? position in positions)
        {
            if (position is null)
            {
                revision = MixAtlasRevision(revision, -1);
                continue;
            }

            revision = MixAtlasRevision(revision, position.atlasTextureId);
            revision = MixAtlasRevision(revision, position.atlasNumber);
            revision = MixAtlasRevision(revision, position.reloadIteration);
            revision = MixAtlasRevision(revision, BitConverter.SingleToInt32Bits(position.x1));
            revision = MixAtlasRevision(revision, BitConverter.SingleToInt32Bits(position.y1));
            revision = MixAtlasRevision(revision, BitConverter.SingleToInt32Bits(position.x2));
            revision = MixAtlasRevision(revision, BitConverter.SingleToInt32Bits(position.y2));
            if (!ReferenceEquals(position, unknownPosition)
                && (position.atlasNumber != 0 || position.atlasTextureId != primaryTextureId))
            {
                foreignPagePosition = true;
            }
        }

        if (atlasTextures.Count == 0)
        {
            return new(
                PbrAtlasSafetyKind.Waiting,
                revision,
                "waiting for terrain atlas");
        }

        if (atlasTextures.Count != 1)
        {
            return new(
                PbrAtlasSafetyKind.UnsafeMultiplePages,
                revision,
                $"PBR disabled: terrain atlas exposes {atlasTextures.Count} pages and no verified per-batch page hook is available");
        }

        LoadedTexture primary = atlasTextures[0];
        if (primary.Disposed || primary.TextureId <= 0 || primary.Width <= 0 || primary.Height <= 0)
        {
            return new(
                PbrAtlasSafetyKind.Waiting,
                revision,
                "waiting for a valid terrain atlas page");
        }

        if (foreignPagePosition)
        {
            return new(
                PbrAtlasSafetyKind.UnsafeForeignPosition,
                revision,
                "PBR disabled: terrain positions are not owned exclusively by the proven atlas page");
        }

        return new(
            PbrAtlasSafetyKind.ProvenSinglePage,
            revision,
            "single terrain atlas page proven");
    }

    /// <summary>Appends one signed 32-bit value to a stable FNV-1a atlas-layout fingerprint.</summary>
    /// <param name="hash">Current fingerprint.</param>
    /// <param name="value">Texture, page, placement, or reload component.</param>
    /// <returns>Updated deterministic fingerprint.</returns>
    private static ulong MixAtlasRevision(ulong hash, int value)
    {
        unchecked
        {
            uint bits = (uint)value;
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(bits >> shift);
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }

    /// <summary>Allocates and clears the RGBA8 material atlas through a temporary framebuffer.</summary>
    /// <param name="neutral">Four linear/UNorm clear components matching normal XY, roughness, bits.</param>
    /// <returns>Owned OpenGL texture handle with source-atlas dimensions.</returns>
    private int CreateNeutralAtlas(float[] neutral)
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
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
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
                throw new InvalidOperationException("The file-backed PBR atlas framebuffer is incomplete.");
            }

            GL.ClearBuffer(ClearBuffer.Color, 0, neutral);
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

    /// <summary>Uploads suffix-based sidecars while preferring authored maps over manifest fallbacks.</summary>
    /// <param name="manifestFallbackSources">Sources already populated by generated manifests.</param>
    /// <param name="manifestFallbackHashes">Known generated-map hashes keyed by asset identity.</param>
    /// <param name="rejectedGeneratedSources">Sources whose generated suite failed source or map integrity.</param>
    /// <returns>Atlas rectangles updated.</returns>
    private int ApplySidecarOverrides(
        IReadOnlySet<AssetLocation> manifestFallbackSources,
        IReadOnlyDictionary<AssetLocation, HashSet<string>> manifestFallbackHashes,
        IReadOnlySet<AssetLocation> rejectedGeneratedSources)
    {
        int applied = 0;
        foreach (AssetLocation normalLocation in sidecarAssets.NormalLocations)
        {
            // NormalLocations is an immutable, suffix-filtered view built by PbrSidecarAssetStore.
            string basePath = normalLocation.Path[..^6];
            AssetLocation sourceLocation = new(normalLocation.Domain, basePath + ".png");
            AssetLocation roughnessLocation = new(normalLocation.Domain, basePath + "_r.png");
            AssetLocation metallicLocation = new(normalLocation.Domain, basePath + "_m.png");
            AssetLocation emissiveLocation = new(normalLocation.Domain, basePath + "_e.png");
            AssetLocation canonicalSource = CanonicalTextureAsset(sourceLocation);
            IAsset? normalAsset = ResolvePreferredSidecarCore(
                normalLocation,
                manifestFallbackHashes,
                inspectShadowedOrigins: false,
                out bool authoredNormal);
            if (normalAsset is null
                || !TryResolveAtlasPositions(sourceLocation, out TextureAtlasPosition[] positions))
            {
                continue;
            }

            IAsset? roughnessAsset = ResolvePreferredSidecarCore(
                roughnessLocation,
                manifestFallbackHashes,
                inspectShadowedOrigins: false,
                out bool authoredRoughness);
            IAsset? metallicAsset = ResolvePreferredSidecarCore(
                metallicLocation,
                manifestFallbackHashes,
                inspectShadowedOrigins: false,
                out bool authoredMetallic);
            IAsset? emissiveAsset = ResolvePreferredSidecarCore(
                emissiveLocation,
                manifestFallbackHashes,
                inspectShadowedOrigins: false,
                out bool authoredEmissive);
            bool hasAuthoredSidecar = authoredNormal
                || authoredRoughness
                || authoredMetallic
                || authoredEmissive;
            if (rejectedGeneratedSources.Contains(canonicalSource))
            {
                if (!authoredNormal)
                {
                    // A stale albedo-derived normal cannot safely anchor the optional
                    // material maps, even when one of those maps is authored.
                    continue;
                }

                roughnessAsset = authoredRoughness ? roughnessAsset : null;
                metallicAsset = authoredMetallic ? metallicAsset : null;
                emissiveAsset = authoredEmissive ? emissiveAsset : null;
            }

            if (manifestFallbackSources.Contains(canonicalSource) && !hasAuthoredSidecar)
            {
                // The manifest already uploaded the exact generated fallback.
                // Avoid decoding and uploading the same four maps twice.
                continue;
            }

            foreach (TextureAtlasPosition position in positions)
            {
                UploadOverride(
                    position,
                    normalAsset.Data,
                    roughnessAsset?.Data,
                    metallicAsset?.Data,
                    emissiveAsset?.Data,
                    maximumTangentSlope: null);
                applied++;
            }
        }

        return applied;
    }

    /// <summary>Validates manifest schema/checksums and uploads every unambiguous source rectangle.</summary>
    /// <param name="manifestFallbackSources">
    /// Receives canonical sources owned by a generated manifest, including rejected stale entries,
    /// so the suffix scan cannot reintroduce their generated bytes without source validation.
    /// </param>
    /// <param name="fallbackHashes">Receives generated-map hashes used to detect later authored overrides.</param>
    /// <param name="rejectedGeneratedSources">Receives sources whose generated maps must be suppressed.</param>
    /// <returns>Atlas rectangles updated.</returns>
    private int ApplyManifestOverrides(
        ISet<AssetLocation> manifestFallbackSources,
        IDictionary<AssetLocation, HashSet<string>> fallbackHashes,
        ISet<AssetLocation> rejectedGeneratedSources)
    {
        int applied = 0;
        int manifestCount = 0;
        int entryCount = 0;
        int atlasMatchCount = 0;
        int mapMatchCount = 0;
        int checksumRejectCount = 0;
        int identityRejectCount = 0;
        int duplicateRejectCount = 0;
        int provenanceRejectCount = 0;
        int alphaCutoutSkipCount = 0;
        int generatedAppliedCount = 0;
        int authoredAppliedCount = 0;
        HashSet<AssetLocation> claimedSources = [];
        foreach (IAsset manifestAsset in OrderManifestAssets(api.Assets.GetManyInCategory(
            ManifestAssetCategory,
            ManifestAssetPath,
            null,
            true)))
        {
            manifestCount++;
            PbrManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<PbrManifest>(
                    manifestAsset.Data,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException exception)
            {
                api.Logger.Warning(
                    "[VintageRTX] Ignoring malformed PBR manifest {0}: {1}",
                    manifestAsset.Location,
                    exception.Message);
                continue;
            }

            if (manifest?.Textures is not { Count: > 0 }
                || !string.Equals(manifest.Schema, "vintagertx.pbr-manifest", StringComparison.Ordinal)
                || manifest.SchemaVersion is < 1 or > 4)
            {
                continue;
            }

            foreach (PbrManifestTexture entry in manifest.Textures)
            {
                entryCount++;
                if (!HasExactManifestSidecarReferences(
                    manifest.SchemaVersion,
                    manifestAsset.Location.Domain,
                    entry,
                    out AssetLocation sourceLocation))
                {
                    identityRejectCount++;
                    api.Logger.Warning(
                        "[VintageRTX] Ignoring PBR manifest entry for {0}:{1}: sidecars do not exactly match the source albedo.",
                        entry.Source.Domain,
                        entry.Source.Path);
                    continue;
                }

                RegisterFallbackChecksum(
                    manifestAsset.Location.Domain,
                    entry.Normal.Asset,
                    entry.Normal.Sha256,
                    fallbackHashes);
                RegisterFallbackChecksum(
                    manifestAsset.Location.Domain,
                    entry.Roughness.Asset,
                    entry.Roughness.Sha256,
                    fallbackHashes);
                RegisterFallbackChecksum(
                    manifestAsset.Location.Domain,
                    entry.Metallic.Asset,
                    entry.Metallic.Sha256,
                    fallbackHashes);
                RegisterFallbackChecksum(
                    manifestAsset.Location.Domain,
                    entry.Emissive.Asset,
                    entry.Emissive.Sha256,
                    fallbackHashes);

                if (!PbrMaterialProvenanceContract.TryResolve(
                    manifest.SchemaVersion,
                    manifest.DefaultProvenance,
                    entry.Provenance,
                    out PbrMaterialProvenance materialProvenance))
                {
                    rejectedGeneratedSources.Add(sourceLocation);
                    provenanceRejectCount++;
                    api.Logger.Warning(
                        "[VintageRTX] Ignoring PBR manifest entry for {0}: schema v4 requires provenance 'generated' or 'authored'.",
                        sourceLocation);
                    continue;
                }

                if (!MatchesManifestSource(manifest.SchemaVersion, sourceLocation, entry.Source.Sha256))
                {
                    rejectedGeneratedSources.Add(sourceLocation);
                    identityRejectCount++;
                    api.Logger.Warning(
                        "[VintageRTX] Ignoring PBR manifest entry for {0}: source albedo is missing or its SHA-256 changed.",
                        sourceLocation);
                    continue;
                }

                if (!TryResolveAtlasPositions(sourceLocation, out TextureAtlasPosition[] positions))
                {
                    continue;
                }

                atlasMatchCount++;
                if (!TryResolvePbrAsset(manifestAsset.Location.Domain, entry.Normal.Asset, out IAsset? normalAsset))
                {
                    rejectedGeneratedSources.Add(sourceLocation);
                    continue;
                }

                mapMatchCount++;

                IAsset? roughnessAsset = null;
                IAsset? metallicAsset = null;
                IAsset? emissiveAsset = null;
                if (!string.IsNullOrWhiteSpace(entry.Roughness.Asset))
                {
                    TryResolvePbrAsset(manifestAsset.Location.Domain, entry.Roughness.Asset, out roughnessAsset);
                }
                if (!string.IsNullOrWhiteSpace(entry.Metallic.Asset))
                {
                    TryResolvePbrAsset(manifestAsset.Location.Domain, entry.Metallic.Asset, out metallicAsset);
                }
                if (!string.IsNullOrWhiteSpace(entry.Emissive.Asset))
                {
                    TryResolvePbrAsset(manifestAsset.Location.Domain, entry.Emissive.Asset, out emissiveAsset);
                }

                if (!MatchesOptionalSha256(normalAsset!.Data, entry.Normal.Sha256)
                    || (roughnessAsset is not null
                        && !MatchesOptionalSha256(roughnessAsset.Data, entry.Roughness.Sha256))
                    || (metallicAsset is not null
                        && !MatchesOptionalSha256(metallicAsset.Data, entry.Metallic.Sha256))
                    || (emissiveAsset is not null
                        && !MatchesOptionalSha256(emissiveAsset.Data, entry.Emissive.Sha256)))
                {
                    rejectedGeneratedSources.Add(sourceLocation);
                    checksumRejectCount++;
                    api.Logger.Warning(
                        "[VintageRTX] Ignoring PBR manifest entry for {0}: checksum mismatch.",
                        sourceLocation);
                    continue;
                }

                if (!claimedSources.Add(sourceLocation))
                {
                    duplicateRejectCount++;
                    api.Logger.Warning(
                        "[VintageRTX] Ignoring duplicate PBR manifest claim for {0}; deterministic manifest order keeps the first valid mapping.",
                        sourceLocation);
                    continue;
                }

                manifestFallbackSources.Add(sourceLocation);

                // A luminance-derived tangent normal is not a physically reliable fallback for
                // alpha-cutout cards. At distance, adjacent screen samples frequently belong to
                // different crossed foliage planes, so their geometric normals can differ by
                // 60-100 degrees even when the tangent normal is neutral. Do not claim synthetic
                // PBR metadata for those sources; a real authored sidecar can still override this
                // manifest claim during the later suffix scan.
                IAsset? sourceAsset = api.Assets.TryGet(sourceLocation);
                if (materialProvenance == PbrMaterialProvenance.Generated
                    && (sourceAsset is null || !IsOpaqueGeneratedFallbackSource(sourceAsset.Data)))
                {
                    alphaCutoutSkipCount++;
                    continue;
                }

                foreach (TextureAtlasPosition position in positions)
                {
                    UploadOverride(
                        position,
                        normalAsset.Data,
                        roughnessAsset?.Data,
                        metallicAsset?.Data,
                        emissiveAsset?.Data,
                        materialProvenance == PbrMaterialProvenance.Generated
                            ? GeneratedFallbackMaximumSlope
                            : null,
                        materialProvenance);
                    applied++;
                    if (materialProvenance == PbrMaterialProvenance.Generated)
                    {
                        generatedAppliedCount++;
                    }
                    else
                    {
                        authoredAppliedCount++;
                    }
                }
            }
        }

        api.Logger.Notification(
            "[VintageRTX] PBR manifest scan: files={0}, entries={1}, atlas matches={2}, map matches={3}, checksum rejects={4}, identity rejects={5}, duplicate rejects={6}, alpha-cutout skips={7}, applied={8}, generated applied={9}, authored applied={10}, provenance rejects={11}.",
            manifestCount,
            entryCount,
            atlasMatchCount,
            mapMatchCount,
            checksumRejectCount,
            identityRejectCount,
            duplicateRejectCount,
            alphaCutoutSkipCount,
            applied,
            generatedAppliedCount,
            authoredAppliedCount,
            provenanceRejectCount);

        return applied;
    }

    /// <summary>Sorts manifest assets by stable asset identity instead of mod-loader enumeration order.</summary>
    /// <param name="assets">Manifest assets returned by Vintage Story.</param>
    /// <returns>A snapshot ordered by domain and normalized path.</returns>
    internal static IAsset[] OrderManifestAssets(IEnumerable<IAsset> assets) => assets
        .OrderBy(asset => asset.Location.Domain, StringComparer.Ordinal)
        .ThenBy(asset => asset.Location.Path.Replace('\\', '/'), StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Resolves the canonical albedo and enforces the v3 adjacent-sidecar contract before any atlas write.
    /// </summary>
    /// <param name="schemaVersion">Manifest contract version.</param>
    /// <param name="manifestDomain">Domain used only by unqualified legacy references.</param>
    /// <param name="entry">Candidate albedo and material maps.</param>
    /// <param name="sourceLocation">Canonical <c>domain:textures/...png</c> identity on success.</param>
    /// <returns>
    /// <see langword="true"/> when the source is structurally valid and every v3 map is the exact
    /// <c>_n</c>, <c>_r</c>, <c>_m</c>, or <c>_e</c> neighbor of that source. Legacy manifests retain
    /// their explicitly referenced layout.
    /// </returns>
    internal static bool HasExactManifestSidecarReferences(
        int schemaVersion,
        string manifestDomain,
        PbrManifestTexture entry,
        out AssetLocation sourceLocation)
    {
        sourceLocation = null!;
        if (string.IsNullOrWhiteSpace(entry.Source.Domain)
            || string.IsNullOrWhiteSpace(entry.Source.Path))
        {
            return false;
        }

        sourceLocation = CanonicalTextureAsset(new AssetLocation(entry.Source.Domain, entry.Source.Path));
        if (schemaVersion < 3)
        {
            return true;
        }

        string stem = sourceLocation.Path[..^4];
        return !string.IsNullOrWhiteSpace(entry.Normal.Sha256)
            && !string.IsNullOrWhiteSpace(entry.Roughness.Sha256)
            && !string.IsNullOrWhiteSpace(entry.Metallic.Sha256)
            && !string.IsNullOrWhiteSpace(entry.Emissive.Sha256)
            && IsExactManifestMap(entry.Normal.Asset, manifestDomain, sourceLocation.Domain, stem + "_n.png")
            && IsExactManifestMap(entry.Roughness.Asset, manifestDomain, sourceLocation.Domain, stem + "_r.png")
            && IsExactManifestMap(entry.Metallic.Asset, manifestDomain, sourceLocation.Domain, stem + "_m.png")
            && IsExactManifestMap(entry.Emissive.Asset, manifestDomain, sourceLocation.Domain, stem + "_e.png");
    }

    /// <summary>Checks one manifest reference against a fully qualified adjacent sidecar identity.</summary>
    /// <param name="reference">Raw manifest map reference.</param>
    /// <param name="manifestDomain">Fallback domain for an unqualified reference.</param>
    /// <param name="expectedDomain">Source albedo domain.</param>
    /// <param name="expectedPath">Exact adjacent sidecar path.</param>
    /// <returns>Whether parsing produced the expected normalized asset identity.</returns>
    private static bool IsExactManifestMap(
        string reference,
        string manifestDomain,
        string expectedDomain,
        string expectedPath)
    {
        return !string.IsNullOrWhiteSpace(reference)
            && TryParseAssetReference(reference, manifestDomain, out AssetLocation location)
            && string.Equals(location.Domain, expectedDomain, StringComparison.Ordinal)
            && string.Equals(location.Path, expectedPath, StringComparison.Ordinal);
    }

    /// <summary>Verifies that generated material maps still describe the exact currently loaded albedo bytes.</summary>
    /// <param name="schemaVersion">Manifest contract version; v3 makes the source digest mandatory.</param>
    /// <param name="sourceLocation">Canonical source asset identity.</param>
    /// <param name="expectedSha256">Offline SHA-256 recorded when maps were generated.</param>
    /// <returns>Whether the source exists and satisfies the applicable integrity contract.</returns>
    private bool MatchesManifestSource(
        int schemaVersion,
        AssetLocation sourceLocation,
        string expectedSha256)
    {
        IAsset? sourceAsset = api.Assets.TryGet(sourceLocation);
        return sourceAsset is not null
            && (schemaVersion < 3 || !string.IsNullOrWhiteSpace(expectedSha256))
            && MatchesOptionalSha256(sourceAsset.Data, expectedSha256);
    }

    /// <summary>Resolves the newest authored sidecar across origins before accepting generated fallback bytes.</summary>
    /// <param name="location">Exact sidecar location.</param>
    /// <param name="fallbackHashes">Hashes that identify generated maps rather than authored content.</param>
    /// <param name="authored">Whether the result is proven not to match a generated hash.</param>
    /// <returns>Preferred asset, or <see langword="null"/> when unavailable.</returns>
    private IAsset? ResolvePreferredSidecar(
        AssetLocation location,
        IReadOnlyDictionary<AssetLocation, HashSet<string>> fallbackHashes,
        out bool authored)
    {
        return ResolvePreferredSidecarCore(
            location,
            fallbackHashes,
            inspectShadowedOrigins: true,
            out authored);
    }

    /// <summary>Resolves a visible sidecar and optionally inspects lower-priority asset origins.</summary>
    /// <param name="location">Exact sidecar location.</param>
    /// <param name="fallbackHashes">Hashes that identify generated maps rather than authored content.</param>
    /// <param name="inspectShadowedOrigins">
    /// Whether to search every asset origin after the visible asset matches a generated fallback.
    /// The bulk atlas path disables this quadratic search because manifests have already resolved
    /// active authored suites and the captured visible asset is the effective load-order override.
    /// </param>
    /// <param name="authored">Whether the result is proven not to match a generated hash.</param>
    /// <returns>Preferred asset, or <see langword="null"/> when unavailable.</returns>
    private IAsset? ResolvePreferredSidecarCore(
        AssetLocation location,
        IReadOnlyDictionary<AssetLocation, HashSet<string>> fallbackHashes,
        bool inspectShadowedOrigins,
        out bool authored)
    {
        IAsset? resolved = sidecarAssets.TryGet(location, out IAsset? isolated)
            ? isolated
            : api.Assets.TryGet(location);
        if (!fallbackHashes.TryGetValue(location, out HashSet<string>? generatedHashes))
        {
            authored = resolved is not null;
            return resolved;
        }

        if (resolved is not null && !MatchesAnySha256(resolved.Data, generatedHashes))
        {
            authored = true;
            return resolved;
        }

        if (!inspectShadowedOrigins)
        {
            authored = false;
            return resolved;
        }

        // A generated fallback pack normally depends on its source mod, so its
        // asset can be the visible overlay even when that source mod later adds
        // an authored sidecar. Inspect public asset origins from newest to
        // oldest and prefer the latest candidate not identified by a manifest
        // SHA. This keeps authored maps load-order independent.
        for (int originIndex = api.Assets.Origins.Count - 1; originIndex >= 0; originIndex--)
        {
            List<IAsset> candidates = api.Assets.Origins[originIndex].GetAssets(location, true);
            for (int candidateIndex = candidates.Count - 1; candidateIndex >= 0; candidateIndex--)
            {
                IAsset candidate = candidates[candidateIndex];
                if (candidate.Location.Equals(location)
                    && !MatchesAnySha256(candidate.Data, generatedHashes))
                {
                    authored = true;
                    return candidate;
                }
            }
        }

        authored = false;
        return resolved;
    }

    /// <summary>Associates a valid manifest checksum with its normalized asset location.</summary>
    /// <param name="defaultDomain">Manifest domain used by unqualified references.</param>
    /// <param name="assetReference">Qualified, domain/path, or texture-relative asset reference.</param>
    /// <param name="expectedSha256">Expected SHA-256 hex digest.</param>
    /// <param name="fallbackHashes">Destination multimap.</param>
    private static void RegisterFallbackChecksum(
        string defaultDomain,
        string assetReference,
        string expectedSha256,
        IDictionary<AssetLocation, HashSet<string>> fallbackHashes)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)
            || !TryParseAssetReference(assetReference, defaultDomain, out AssetLocation location))
        {
            return;
        }

        if (!fallbackHashes.TryGetValue(location, out HashSet<string>? hashes))
        {
            hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            fallbackHashes.Add(location, hashes);
        }

        hashes.Add(expectedSha256);
    }

    /// <summary>Resolves all exact, unambiguous atlas rectangles carrying one source albedo.</summary>
    /// <param name="sourceLocation">Source PNG identity.</param>
    /// <param name="positions">One or more atlas rectangles on success.</param>
    /// <returns>Whether safe placement is known; ambiguous composites are deliberately rejected.</returns>
    private bool TryResolveAtlasPositions(
        AssetLocation sourceLocation,
        out TextureAtlasPosition[] positions)
    {
        positions = [];
        if (!sourceLocation.Path.StartsWith("textures/", StringComparison.Ordinal)
            || !sourceLocation.Path.EndsWith(".png", StringComparison.Ordinal))
        {
            return false;
        }

        AssetLocation canonical = CanonicalTextureAsset(sourceLocation);
        if (sourceAtlasPositions is not null
            && sourceAtlasPositions.TryGetValue(canonical, out TextureAtlasPosition[]? mapped)
            && mapped.Length > 0)
        {
            positions = mapped;
            return true;
        }

        if (ambiguousAtlasSources?.Contains(canonical) == true)
        {
            // This source was observed on at least one rectangle with another
            // independent albedo. The direct atlas index cannot prove which
            // composite owns that rectangle, so never reintroduce it here.
            return false;
        }

        string atlasPath = sourceLocation.Path["textures/".Length..^4];
        TextureAtlasPosition direct = api.BlockTextureAtlas[
            new AssetLocation(sourceLocation.Domain, atlasPath)];
        if (IsSourceAtlasPosition(direct))
        {
            positions = [direct];
            return true;
        }

        return false;
    }

    /// <summary>Builds the production source-to-rectangle lookup from loaded block composites.</summary>
    /// <returns>Exact placements and ambiguity diagnostics for the current block atlas.</returns>
    private PbrAtlasLookup BuildSourceAtlasPositionLookup()
    {
        List<CompositeTexture> textures = [];
        TextureAtlasPosition[] atlasPositions = api.BlockTextureAtlas.Positions;
        foreach (Block block in api.World.Blocks)
        {
            if (block?.Textures is null)
            {
                continue;
            }

            foreach (CompositeTexture texture in block.Textures.Values)
            {
                textures.Add(texture);
            }
        }

        PbrAtlasLookup result =
            BuildSourceAtlasPositionLookupForTextures(
                textures,
                atlasPositions,
                IsSourceAtlasPosition);

        api.Logger.Notification(
            "[VintageRTX] PBR source-to-atlas lookup built: exact sources={0}, exact placements={1}, ambiguous rectangles={2}, skipped ambiguous links={3}, skipped composite rectangles={4}.",
            result.Positions.Count,
            result.ExactPlacementCount,
            result.AmbiguousRectangleCount,
            result.SkippedSourceLinkCount,
            result.SkippedCompositeRectangleCount);
        return result;
    }

    /// <summary>Builds deterministic placement/ambiguity data from explicit composites for tests and runtime.</summary>
    /// <param name="textures">Root composite graphs to traverse by reference identity.</param>
    /// <param name="atlasPositions">Vintage Story positions indexed by baked texture sub-ID.</param>
    /// <param name="isPositionUsable">Predicate restricting atlas number/texture ownership.</param>
    /// <returns>Only unambiguous source mappings plus exact rejection counts.</returns>
    internal static PbrAtlasLookup BuildSourceAtlasPositionLookupForTextures(
        IEnumerable<CompositeTexture> textures,
        TextureAtlasPosition[] atlasPositions,
        System.Func<TextureAtlasPosition, bool> isPositionUsable)
    {
        Dictionary<AssetLocation, List<TextureAtlasPosition>> building = [];
        int skippedCompositeRectangleCount = 0;
        foreach (CompositeTexture texture in textures)
        {
            AddCompositeTexture(
                texture,
                atlasPositions,
                building,
                isPositionUsable,
                ref skippedCompositeRectangleCount);
        }

        Dictionary<AtlasPositionKey, HashSet<AssetLocation>> ownersByPosition = [];
        foreach ((AssetLocation source, List<TextureAtlasPosition> positions) in building)
        {
            foreach (TextureAtlasPosition position in positions)
            {
                AtlasPositionKey key = AtlasPositionKey.From(position);
                if (!ownersByPosition.TryGetValue(key, out HashSet<AssetLocation>? owners))
                {
                    owners = [];
                    ownersByPosition.Add(key, owners);
                }

                owners.Add(source);
            }
        }

        HashSet<AtlasPositionKey> ambiguousPositions = ownersByPosition
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => pair.Key)
            .ToHashSet();
        HashSet<AssetLocation> ambiguousSources = ownersByPosition
            .Where(pair => pair.Value.Count > 1)
            .SelectMany(pair => pair.Value)
            .ToHashSet();
        int skippedSourceLinkCount = ownersByPosition
            .Where(pair => pair.Value.Count > 1)
            .Sum(pair => pair.Value.Count);

        Dictionary<AssetLocation, TextureAtlasPosition[]> exact = new(building.Count);
        foreach ((AssetLocation source, List<TextureAtlasPosition> mappedPositions) in building)
        {
            TextureAtlasPosition[] unambiguous = mappedPositions
                .Where(position => !ambiguousPositions.Contains(AtlasPositionKey.From(position)))
                .ToArray();
            if (unambiguous.Length > 0)
            {
                exact.Add(source, unambiguous);
            }
        }

        return new PbrAtlasLookup(
            exact,
            ambiguousSources,
            exact.Sum(pair => pair.Value.Length),
            ambiguousPositions.Count,
            skippedSourceLinkCount,
            skippedCompositeRectangleCount);
    }

    /// <summary>Traverses alternates/tiles iteratively and forwards every baked graph once.</summary>
    /// <param name="texture">Composite root.</param>
    /// <param name="atlasPositions">Positions indexed by baked sub-ID.</param>
    /// <param name="lookup">Mutable source-to-position multimap.</param>
    /// <param name="isPositionUsable">Atlas ownership predicate.</param>
    /// <param name="skippedCompositeRectangleCount">Count of rectangles lacking a unique base source.</param>
    private static void AddCompositeTexture(
        CompositeTexture? texture,
        TextureAtlasPosition[] atlasPositions,
        Dictionary<AssetLocation, List<TextureAtlasPosition>> lookup,
        System.Func<TextureAtlasPosition, bool> isPositionUsable,
        ref int skippedCompositeRectangleCount)
    {
        if (texture is null)
        {
            return;
        }

        Stack<CompositeTexture> pending = new();
        HashSet<CompositeTexture> visited = new(ReferenceEqualityComparer.Instance);
        pending.Push(texture);
        while (pending.TryPop(out CompositeTexture? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            AddBakedTexture(
                current.Base,
                current.Baked,
                atlasPositions,
                lookup,
                isPositionUsable,
                ref skippedCompositeRectangleCount);
            if (current.Alternates is not null)
            {
                foreach (CompositeTexture alternate in current.Alternates)
                {
                    if (alternate is not null)
                    {
                        pending.Push(alternate);
                    }
                }
            }

            if (current.Tiles is not null)
            {
                foreach (CompositeTexture tile in current.Tiles)
                {
                    if (tile is not null)
                    {
                        pending.Push(tile);
                    }
                }
            }
        }
    }

    /// <summary>Maps baked variants/tiles while refusing overlay composites as independent PBR aliases.</summary>
    /// <param name="baseTexture">Declared fallback base for the root only.</param>
    /// <param name="baked">Baked graph root.</param>
    /// <param name="atlasPositions">Positions indexed by baked sub-ID.</param>
    /// <param name="lookup">Mutable source-to-position multimap.</param>
    /// <param name="isPositionUsable">Atlas ownership predicate.</param>
    /// <param name="skippedCompositeRectangleCount">Count of ambiguous multi-file composites.</param>
    private static void AddBakedTexture(
        AssetLocation? baseTexture,
        BakedCompositeTexture? baked,
        TextureAtlasPosition[] atlasPositions,
        Dictionary<AssetLocation, List<TextureAtlasPosition>> lookup,
        System.Func<TextureAtlasPosition, bool> isPositionUsable,
        ref int skippedCompositeRectangleCount)
    {
        if (baked is null)
        {
            return;
        }

        Stack<(BakedCompositeTexture Texture, bool AllowDeclaredBaseFallback)> pending = new();
        HashSet<BakedCompositeTexture> visited = new(ReferenceEqualityComparer.Instance);
        pending.Push((baked, true));
        while (pending.TryPop(out var pendingTexture))
        {
            BakedCompositeTexture current = pendingTexture.Texture;
            if (!visited.Add(current))
            {
                continue;
            }

            if (current.TextureSubId >= 0 && current.TextureSubId < atlasPositions.Length)
            {
                TextureAtlasPosition position = atlasPositions[current.TextureSubId];
                if (position is not null && isPositionUsable(position))
                {
                    // TextureFilenames is [base, overlays...]. Every overlay is
                    // baked into this rectangle, but it is not an albedo alias:
                    // assigning its independent PBR maps here lets whichever
                    // manifest entry runs last replace the complete composite.
                    // Variants and connected tiles have their own baked node,
                    // TextureSubId and first (base) filename.
                    AssetLocation? exactBase = null;
                    if (current.TextureFilenames is { Length: 1 })
                    {
                        exactBase = current.TextureFilenames[0];
                    }
                    else if ((current.TextureFilenames is null or { Length: 0 })
                        && pendingTexture.AllowDeclaredBaseFallback)
                    {
                        exactBase = baseTexture;
                    }

                    if (exactBase is null)
                    {
                        skippedCompositeRectangleCount++;
                    }
                    else
                    {
                        AddSource(exactBase, position, lookup);
                    }
                }
            }

            if (current.BakedVariants is not null)
            {
                foreach (BakedCompositeTexture variant in current.BakedVariants)
                {
                    if (variant is not null)
                    {
                        pending.Push((variant, false));
                    }
                }
            }

            if (current.BakedTiles is not null)
            {
                foreach (BakedCompositeTexture tile in current.BakedTiles)
                {
                    if (tile is not null)
                    {
                        pending.Push((tile, false));
                    }
                }
            }
        }
    }

    /// <summary>Adds one normalized source/rectangle pair without duplicate placement entries.</summary>
    /// <param name="source">Candidate source asset.</param>
    /// <param name="position">Exact atlas rectangle.</param>
    /// <param name="lookup">Mutable source-to-position multimap.</param>
    private static void AddSource(
        AssetLocation? source,
        TextureAtlasPosition position,
        Dictionary<AssetLocation, List<TextureAtlasPosition>> lookup)
    {
        if (source is null)
        {
            return;
        }

        AssetLocation canonical = CanonicalTextureAsset(source);
        if (!lookup.TryGetValue(canonical, out List<TextureAtlasPosition>? positions))
        {
            positions = [];
            lookup.Add(canonical, positions);
        }

        if (!positions.Any(existing =>
            existing.atlasNumber == position.atlasNumber
            && existing.x1 == position.x1
            && existing.y1 == position.y1
            && existing.x2 == position.x2
            && existing.y2 == position.y2))
        {
            positions.Add(position);
        }
    }

    /// <summary>Normalizes slashes, case, texture prefix, and PNG extension for identity comparisons.</summary>
    /// <param name="source">Possibly abbreviated asset location.</param>
    /// <returns>Canonical domain plus <c>textures/...png</c> path.</returns>
    private static AssetLocation CanonicalTextureAsset(AssetLocation source)
    {
        string path = source.Path.Replace('\\', '/').ToLowerInvariant();
        if (!path.StartsWith("textures/", StringComparison.Ordinal))
        {
            path = "textures/" + path;
        }

        if (!path.EndsWith(".png", StringComparison.Ordinal))
        {
            path += ".png";
        }

        return new AssetLocation(source.Domain, path);
    }

    /// <summary>Checks that a rectangle belongs to atlas zero and the exact borrowed source texture.</summary>
    /// <param name="position">Candidate position, including unknown-texture sentinel.</param>
    /// <returns>Whether material bytes may safely be uploaded to the mirrored rectangle.</returns>
    private bool IsSourceAtlasPosition(TextureAtlasPosition? position)
    {
        return position is not null
            && !ReferenceEquals(position, api.BlockTextureAtlas.UnknownTexturePosition)
            && position.atlasNumber == 0
            && position.atlasTextureId == sourceAtlasTexture;
    }

    /// <summary>Parses and resolves a manifest map from the private sidecar index or shared asset catalog.</summary>
    /// <param name="defaultDomain">Domain for unqualified texture references.</param>
    /// <param name="reference">Manifest asset reference.</param>
    /// <param name="asset">Resolved bytes on success.</param>
    /// <returns>Whether an asset exists.</returns>
    private bool TryResolvePbrAsset(string defaultDomain, string reference, out IAsset? asset)
    {
        asset = null;
        if (!TryParseAssetReference(reference, defaultDomain, out AssetLocation location))
        {
            return false;
        }

        asset = sidecarAssets.TryGet(location, out IAsset? isolated)
            ? isolated
            : api.Assets.TryGet(location);
        return asset is not null;
    }

    /// <summary>Parses supported <c>domain:path</c>, <c>domain/path</c>, and local texture references.</summary>
    /// <param name="reference">Raw manifest string.</param>
    /// <param name="defaultDomain">Fallback domain for <c>textures/</c> paths.</param>
    /// <param name="location">Normalized lower-case slash-separated location.</param>
    /// <returns>Whether the reference is structurally valid.</returns>
    internal static bool TryParseAssetReference(
        string reference,
        string defaultDomain,
        out AssetLocation location)
    {
        string normalized = reference.Replace('\\', '/').Trim().ToLowerInvariant();
        int colon = normalized.IndexOf(':');
        if (colon > 0 && colon < normalized.Length - 1)
        {
            location = new AssetLocation(normalized[..colon], normalized[(colon + 1)..]);
            return true;
        }

        if (normalized.StartsWith("textures/", StringComparison.Ordinal))
        {
            location = new AssetLocation(defaultDomain, normalized);
            return true;
        }

        int slash = normalized.IndexOf('/');
        if (slash > 0
            && slash < normalized.Length - 1
            && normalized[(slash + 1)..].StartsWith("textures/", StringComparison.Ordinal))
        {
            location = new AssetLocation(normalized[..slash], normalized[(slash + 1)..]);
            return true;
        }

        location = null!;
        return false;
    }

    /// <summary>Accepts an omitted checksum or verifies the full SHA-256 digest case-insensitively.</summary>
    /// <param name="data">Asset bytes.</param>
    /// <param name="expected">Optional hexadecimal digest.</param>
    /// <returns>Whether integrity requirements are satisfied.</returns>
    private static bool MatchesOptionalSha256(byte[] data, string expected)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(
                Convert.ToHexString(SHA256.HashData(data)),
                expected,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Identifies generated content by matching its SHA-256 against a known set.</summary>
    /// <param name="data">Candidate map bytes.</param>
    /// <param name="expected">Known generated digests.</param>
    /// <returns>Whether the bytes match any digest.</returns>
    private static bool MatchesAnySha256(byte[] data, IReadOnlySet<string> expected)
    {
        string actual = Convert.ToHexString(SHA256.HashData(data));
        return expected.Contains(actual);
    }

    /// <summary>
    /// Determines whether an albedo can safely receive a luminance-derived manifest fallback.
    /// Alpha-cutout and translucent images are excluded because their visible screen-space normal
    /// discontinuities come from card geometry and silhouettes, not from a continuous height field.
    /// </summary>
    /// <param name="sourceImage">Encoded source albedo bytes.</param>
    /// <returns><see langword="true"/> only when every decoded source texel is fully opaque.</returns>
    internal static bool IsOpaqueGeneratedFallbackSource(byte[] sourceImage)
    {
        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(sourceImage);
        }
        catch (ArgumentException)
        {
            // SkiaSharp can surface an invalid codec as ArgumentNullException
            // instead of returning null. A corrupt albedo is never eligible
            // for synthesized normal data.
            return false;
        }

        using (decoded)
        {
            if (decoded is null || (long)decoded.Width * decoded.Height <= 0)
            {
                return false;
            }

            for (int y = 0; y < decoded.Height; y++)
            {
                for (int x = 0; x < decoded.Width; x++)
                {
                    if (decoded.GetPixel(x, y).Alpha != byte.MaxValue)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>Resamples four sidecars into one rectangle and uploads the packed RGBA8 payload.</summary>
    /// <param name="position">Normalized atlas rectangle.</param>
    /// <param name="normalPng">Required tangent-space normal PNG.</param>
    /// <param name="roughnessPng">Optional scalar roughness PNG.</param>
    /// <param name="metallicPng">Optional scalar metallic PNG.</param>
    /// <param name="emissivePng">Optional scalar emissive PNG.</param>
    /// <param name="maximumTangentSlope">
    /// Optional albedo-derived fallback calibration; <see langword="null"/> preserves authored normals exactly.
    /// </param>
    /// <param name="materialProvenance">
    /// Whether scalar material maps are generated fallbacks or intentional authored overrides.
    /// </param>
    private void UploadOverride(
        TextureAtlasPosition position,
        byte[] normalPng,
        byte[]? roughnessPng,
        byte[]? metallicPng,
        byte[]? emissivePng,
        float? maximumTangentSlope,
        PbrMaterialProvenance materialProvenance = PbrMaterialProvenance.Authored)
    {
        int targetX = (int)MathF.Round(position.x1 * sourceAtlasWidth);
        int targetY = (int)MathF.Round(position.y1 * sourceAtlasHeight);
        int targetWidth = Math.Max(1, (int)MathF.Round((position.x2 - position.x1) * sourceAtlasWidth));
        int targetHeight = Math.Max(1, (int)MathF.Round((position.y2 - position.y1) * sourceAtlasHeight));
        byte[] pixels = BuildConsolidatedPbrPixels(
            normalPng,
            roughnessPng,
            metallicPng,
            emissivePng,
            targetWidth,
            targetHeight,
            maximumTangentSlope,
            materialProvenance);

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

    /// <summary>
    /// Decodes/resizes sidecars and packs normal X/Y, roughness, and quantized metallic/emissive bits
    /// into the single RGBA8 sampler ABI used by the chunk shader.
    /// </summary>
    /// <param name="normalPng">Required normal PNG bytes.</param>
    /// <param name="roughnessPng">Optional roughness PNG bytes.</param>
    /// <param name="metallicPng">Optional metallic PNG bytes.</param>
    /// <param name="emissivePng">Optional emissive PNG bytes.</param>
    /// <param name="width">Target atlas-rectangle width.</param>
    /// <param name="height">Target atlas-rectangle height.</param>
    /// <param name="maximumTangentSlope">
    /// Optional tangent-slope cap for generated fallbacks; omitted for authored normal maps.
    /// </param>
    /// <param name="materialProvenance">
    /// Whether scalar material maps are generated fallbacks or intentional authored overrides.
    /// </param>
    /// <returns>Tightly packed row-major RGBA8 pixels.</returns>
    internal static byte[] BuildConsolidatedPbrPixels(
        byte[] normalPng,
        byte[]? roughnessPng,
        byte[]? metallicPng,
        byte[]? emissivePng,
        int width,
        int height,
        float? maximumTangentSlope = null,
        PbrMaterialProvenance materialProvenance = PbrMaterialProvenance.Authored)
    {
        using SKBitmap decodedNormal = DecodeUnpremultiplied(normalPng, "normal");
        using SKBitmap? decodedRoughness = roughnessPng is null
            ? null
            : DecodeUnpremultiplied(roughnessPng, "roughness");
        using SKBitmap? decodedMetallic = metallicPng is null
            ? null
            : DecodeUnpremultiplied(metallicPng, "metallic");
        using SKBitmap? decodedEmissive = emissivePng is null
            ? null
            : DecodeUnpremultiplied(emissivePng, "emissive");

        using SKBitmap normal = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap? roughness = decodedRoughness is null
            ? null
            : new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap? metallic = decodedMetallic is null
            ? null
            : new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap? emissive = decodedEmissive is null
            ? null
            : new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        SKSamplingOptions sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
        if (!decodedNormal.ScalePixels(normal, sampling)
            || (decodedRoughness is not null && !decodedRoughness.ScalePixels(roughness!, sampling))
            || (decodedMetallic is not null && !decodedMetallic.ScalePixels(metallic!, sampling))
            || (decodedEmissive is not null && !decodedEmissive.ScalePixels(emissive!, sampling)))
        {
            throw new InvalidDataException("A file-backed PBR texture could not be resized to its atlas rectangle.");
        }

        bool materialSidecarPresent = decodedMetallic is not null || decodedEmissive is not null;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor normalColor = normal.GetPixel(x, y);
                (byte normalX, byte normalY) = CalibrateGeneratedNormal(
                    normalColor,
                    maximumTangentSlope);
                int offset = (y * width + x) * 4;
                pixels[offset] = normalX;
                pixels[offset + 1] = normalY;
                pixels[offset + 2] = roughness?.GetPixel(x, y).Red ?? NeutralRoughness;
                byte metallicValue = metallic?.GetPixel(x, y).Red ?? 0;
                byte emissiveValue = emissive?.GetPixel(x, y).Red ?? 0;
                bool reliableMaterialOverride = materialSidecarPresent
                    && (materialProvenance == PbrMaterialProvenance.Authored
                        || QuantizeMaterialScalar(metallicValue, 3) != 0
                        || QuantizeMaterialScalar(emissiveValue, 7) != 0);
                pixels[offset + 3] = PackMaterialBits(
                    metallicValue,
                    emissiveValue,
                    reliableMaterialOverride,
                    pbrPayloadPresent: true);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Limits an albedo-derived normal by physical tangent slope while retaining the encoded
    /// direction. A missing cap returns the original authored channels byte for byte.
    /// </summary>
    /// <param name="normal">Resampled RGBA8 tangent normal.</param>
    /// <param name="maximumTangentSlope">Positive finite slope cap, or <see langword="null"/>.</param>
    /// <returns>Calibrated encoded X/Y channels.</returns>
    internal static (byte X, byte Y) CalibrateGeneratedNormal(
        SKColor normal,
        float? maximumTangentSlope)
    {
        if (maximumTangentSlope is null)
        {
            return (normal.Red, normal.Green);
        }

        float slopeLimit = maximumTangentSlope.Value;
        if (!float.IsFinite(slopeLimit) || slopeLimit <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTangentSlope),
                "Generated normal slope must be positive and finite.");
        }

        float x = ((normal.Red / 255f) * 2f) - 1f;
        float y = ((normal.Green / 255f) * 2f) - 1f;
        float xyLengthSquared = (x * x) + (y * y);
        float maximumXyLengthSquared = (slopeLimit * slopeLimit) / (1f + (slopeLimit * slopeLimit));
        if (xyLengthSquared <= maximumXyLengthSquared)
        {
            return (normal.Red, normal.Green);
        }

        float scale = MathF.Sqrt(maximumXyLengthSquared / xyLengthSquared);
        return (EncodeSignedNormal(x * scale), EncodeSignedNormal(y * scale));
    }

    /// <summary>Encodes one signed tangent-normal component as UNorm8.</summary>
    private static byte EncodeSignedNormal(float value) =>
        (byte)Math.Clamp((int)MathF.Round(((Math.Clamp(value, -1f, 1f) * 0.5f) + 0.5f) * 255f), 0, 255);

    /// <summary>Packs material scalars plus distinct PBR-presence and reliable-override flags.</summary>
    /// <param name="metallic">UNorm8 source metallic intensity.</param>
    /// <param name="emissive">UNorm8 source emissive intensity.</param>
    /// <param name="reliableMaterialOverride">Whether scalar maps may replace the voxel material class.</param>
    /// <param name="pbrPayloadPresent">Whether a file-backed normal/roughness payload exists.</param>
    /// <returns>
    /// Bits 0..1 metallic, 2..4 emissive, bit 5 file-backed PBR, bit 6 reliable scalar override;
    /// bit 7 remains reserved for terrain/entity surface flags.
    /// </returns>
    internal static byte PackMaterialBits(
        byte metallic,
        byte emissive,
        bool reliableMaterialOverride,
        bool pbrPayloadPresent)
    {
        int metallicBits = QuantizeMaterialScalar(metallic, 3);
        int emissiveBits = QuantizeMaterialScalar(emissive, 7);
        return (byte)(metallicBits
            | (emissiveBits << 2)
            | (pbrPayloadPresent ? 32 : 0)
            | (reliableMaterialOverride ? 64 : 0));
    }

    /// <summary>Quantizes one UNorm8 material scalar into a bounded packed code.</summary>
    /// <param name="value">UNorm8 metallic or emissive value.</param>
    /// <param name="maximumCode">Largest representable packed value.</param>
    /// <returns>Nearest value in the inclusive range zero through <paramref name="maximumCode"/>.</returns>
    private static int QuantizeMaterialScalar(byte value, int maximumCode) =>
        (int)MathF.Round(value / 255.0f * maximumCode);

    /// <summary>Decodes PNG bytes to unpremultiplied RGBA so scalar channels remain numerically exact.</summary>
    /// <param name="png">Encoded map bytes.</param>
    /// <param name="mapKind">Diagnostic map label used in exceptions.</param>
    /// <returns>Caller-owned decoded bitmap.</returns>
    private static SKBitmap DecodeUnpremultiplied(byte[] png, string mapKind)
    {
        using SKData data = SKData.CreateCopy(png);
        using SKCodec codec = SKCodec.Create(data)
            ?? throw new InvalidDataException($"A file-backed PBR {mapKind} texture could not be decoded.");
        SKImageInfo info = new(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        SKBitmap bitmap = new(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new InvalidDataException(
                $"A file-backed PBR {mapKind} texture could not be decoded ({result}).");
        }

        return bitmap;
    }

    /// <summary>Binds the material sampler and enables PBR only when the live chunk shader exposes both uniforms.</summary>
    private void BindTerrainShader()
    {
        SetTerrainShaderState(enablePbr: true);
    }

    /// <summary>
    /// Forces the chunk shader onto its neutral legacy path after an atlas reload invalidates page
    /// ownership. This prevents a stale page-zero material sampler from affecting another page.
    /// </summary>
    private void DisableTerrainShader()
    {
        SetTerrainShaderState(enablePbr: false);
    }

    /// <summary>Binds or explicitly disables the material sampler on the live chunk program.</summary>
    /// <param name="enablePbr">Whether the single-page atlas proof and owned material texture are valid.</param>
    private void SetTerrainShaderState(bool enablePbr)
    {
        IShaderProgram terrainShader = api.Shader.GetProgram((int)EnumShaderProgram.Chunkopaque);
        if (terrainShader is null || terrainShader.Disposed || terrainShader.ProgramId <= 0)
        {
            return;
        }

        int materialSampler = GL.GetUniformLocation(terrainShader.ProgramId, "vintagertxMaterialTex");
        int enabledUniform = GL.GetUniformLocation(terrainShader.ProgramId, "vintagertxPbrEnabled");
        if (materialSampler < 0 || enabledUniform < 0)
        {
            if (enablePbr && !overrideAvailable)
            {
                status = "file-backed PBR atlas ready; chunk shader override unavailable";
                api.Logger.Warning("[VintageRTX] PBR chunk shader uniforms are unavailable.");
            }

            return;
        }

        overrideAvailable = true;
        GL.GetInteger(GetPName.CurrentProgram, out int previousProgram);
        GL.GetInteger(GetPName.ActiveTexture, out int previousActiveTexture);
        GL.UseProgram(terrainShader.ProgramId);
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

    /// <summary>Selects the highest safe unit above vanilla 0..6 while excluding unreliable 15/16.</summary>
    /// <param name="maximumFragmentTextureUnits">OpenGL fragment sampler capacity.</param>
    /// <returns>A unit index suitable for the consolidated sampler.</returns>
    /// <exception cref="InvalidOperationException">Fewer than eight fragment units are available.</exception>
    internal static int SelectPbrTextureUnit(int maximumFragmentTextureUnits)
    {
        // Units 0-6 are populated by the generated Chunkopaque wrapper. Units
        // 15/16 are deliberately excluded after real captures proved the
        // split normal sampler unreliable. One consolidated sampler carries
        // normal XY, roughness and exact material bits.
        for (int unit = maximumFragmentTextureUnits - 1; unit >= 7; unit--)
        {
            if (unit is 15 or 16)
            {
                continue;
            }

            return unit;
        }

        throw new InvalidOperationException(
            $"At least eight fragment texture units are required for the PBR terrain bridge; OpenGL reported {maximumFragmentTextureUnits}.");
    }

    /// <summary>Deletes and forgets the owned material atlas while retaining shader compatibility state.</summary>
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
        sourceAtlasPositions = null;
        ambiguousAtlasSources = null;
        sidecarOverrideCount = 0;
        manifestOverrideCount = 0;
    }

    /// <summary>Deletes the owned material atlas; the borrowed vanilla source atlas is untouched.</summary>
    public void Dispose()
    {
        ReleaseMaterialAtlas();
        sourceAtlasRevision = 0;
        ResetPendingAtlasRevision();
        observedAtlasSafety = PbrAtlasSafetyKind.Waiting;
    }
}

/// <summary>Classification of whether a terrain atlas can safely share one material sampler.</summary>
internal enum PbrAtlasSafetyKind
{
    /// <summary>No complete live atlas is available yet.</summary>
    Waiting,
    /// <summary>Exactly one page owns every renderable terrain rectangle.</summary>
    ProvenSinglePage,
    /// <summary>Several pages exist but the public API cannot report batch page changes.</summary>
    UnsafeMultiplePages,
    /// <summary>A rectangle refers to a page other than the single candidate page.</summary>
    UnsafeForeignPosition
}

/// <summary>Deterministic page-safety decision and reload-sensitive atlas-layout fingerprint.</summary>
/// <param name="Kind">Page ownership classification.</param>
/// <param name="Revision">FNV-1a identity over textures, placements, and reload iterations.</param>
/// <param name="Reason">Stable runtime diagnostic.</param>
internal readonly record struct PbrAtlasSafetyResult(
    PbrAtlasSafetyKind Kind,
    ulong Revision,
    string Reason)
{
    /// <summary>Gets whether binding the page-zero material atlas is proven safe.</summary>
    public bool IsSinglePageProven => Kind == PbrAtlasSafetyKind.ProvenSinglePage;

    /// <summary>Gets whether the engine can render an unobservable foreign atlas page.</summary>
    public bool IsUnsafe => Kind is PbrAtlasSafetyKind.UnsafeMultiplePages
        or PbrAtlasSafetyKind.UnsafeForeignPosition;
}

/// <summary>Exact source placements plus ambiguity/rejection diagnostics from composite traversal.</summary>
internal sealed record PbrAtlasLookup(
    Dictionary<AssetLocation, TextureAtlasPosition[]> Positions,
    HashSet<AssetLocation> AmbiguousSources,
    int ExactPlacementCount,
    int AmbiguousRectangleCount,
    int SkippedSourceLinkCount,
    int SkippedCompositeRectangleCount);

/// <summary>Value identity for one atlas rectangle, including texture and atlas number ownership.</summary>
internal readonly record struct AtlasPositionKey(
    int AtlasTextureId,
    byte AtlasNumber,
    float X1,
    float Y1,
    float X2,
    float Y2)
{
    /// <summary>Copies mutable Vintage Story position fields into an immutable dictionary key.</summary>
    /// <param name="position">Source atlas rectangle.</param>
    /// <returns>Value-comparable rectangle identity.</returns>
    public static AtlasPositionKey From(TextureAtlasPosition position) => new(
        position.atlasTextureId,
        position.atlasNumber,
        position.x1,
        position.y1,
        position.x2,
        position.y2);
}

/// <summary>Deserialized root of the versioned <c>vintagertx.pbr-manifest</c> contract.</summary>
internal sealed class PbrManifest
{
    /// <summary>Gets or sets the schema discriminator; must equal <c>vintagertx.pbr-manifest</c>.</summary>
    public string Schema { get; set; } = string.Empty;

    /// <summary>Gets or sets the supported schema version in the inclusive range 1..4.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets the schema-v4 provenance inherited by entries that omit an override.</summary>
    public string DefaultProvenance { get; set; } = string.Empty;

    /// <summary>Gets or sets texture entries applied in manifest order.</summary>
    public List<PbrManifestTexture> Textures { get; set; } = [];
}

/// <summary>Associates one source albedo with normal, roughness, metallic, and emissive maps.</summary>
internal sealed class PbrManifestTexture
{
    /// <summary>Gets or sets source-mod and source-texture identity.</summary>
    public PbrSourceReference Source { get; set; } = new();

    /// <summary>Gets or sets the required normal-map reference.</summary>
    public PbrMapReference Normal { get; set; } = new();

    /// <summary>Gets or sets the optional roughness-map reference.</summary>
    public PbrMapReference Roughness { get; set; } = new();

    /// <summary>Gets or sets the optional metallic-map reference.</summary>
    public PbrMapReference Metallic { get; set; } = new();

    /// <summary>Gets or sets the optional emissive-map reference.</summary>
    public PbrMapReference Emissive { get; set; } = new();

    /// <summary>Gets or sets an optional schema-v4 per-entry provenance override.</summary>
    public string Provenance { get; set; } = string.Empty;
}

/// <summary>Manifest provenance and canonical albedo identity used for atlas placement.</summary>
internal sealed class PbrSourceReference
{
    /// <summary>Gets or sets the mod identifier that owns the source texture.</summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source mod version recorded by offline generation.</summary>
    public string ModVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the Vintage Story asset domain.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Gets or sets the source albedo path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the source albedo SHA-256, mandatory for v3 and optional only for legacy manifests.</summary>
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>One PBR map asset reference with optional integrity and encoding metadata.</summary>
internal sealed class PbrMapReference
{
    /// <summary>Gets or sets the qualified or manifest-relative asset location.</summary>
    public string Asset { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional SHA-256 digest used for integrity/fallback identity.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets the producer-defined channel encoding label.</summary>
    public string Encoding { get; set; } = string.Empty;
}
