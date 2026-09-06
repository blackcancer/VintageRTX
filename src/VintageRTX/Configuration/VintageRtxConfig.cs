using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace VintageRTX.Configuration;

/// <summary>Selects a diagnostic output without changing the underlying transport computation.</summary>
public enum VintageRtxDebugView
{
    /// <summary>Composited and tone-mapped player image.</summary>
    Final = 0,
    /// <summary>Decoded full-resolution G-buffer surface normals.</summary>
    Normal = 1,
    /// <summary>View/world-position reconstruction used by ray traversal.</summary>
    Position = 2,
    /// <summary>Combined direct and indirect radiance before material composition.</summary>
    Lighting = 3,
    /// <summary>Albedo stored in the scene voxel volume.</summary>
    VoxelAlbedo = 4,
    /// <summary>Scene-space/voxel visibility confidence and hit state.</summary>
    VoxelVisibility = 5,
    /// <summary>Sun and local-light visibility after geometry-aware tracing.</summary>
    VoxelShadow = 6,
    /// <summary>Screen-space reflection contribution and confidence.</summary>
    Reflection = 7,
    /// <summary>Diffuse radiance reconstructed from the voxel field.</summary>
    VoxelBounce = 8,
    /// <summary>Off-screen reflection contribution traced through the voxel field.</summary>
    VoxelReflection = 9,
    /// <summary>Direct, sky, emissive, and bounce transport components encoded separately.</summary>
    TransportComponents = 10,
    /// <summary>Decoded roughness, metallic, emissive, and normal-sidecar response.</summary>
    Material = 11,
    /// <summary>Liquid profile, Fresnel, extinction, and refraction diagnostics.</summary>
    Water = 12,
    /// <summary>Rain exposure and transient wet-surface response.</summary>
    Wetness = 13,
    /// <summary>Raw opaque scene copy sampled by planar and screen-space reflections.</summary>
    ReflectionSource = 14,
    /// <summary>Signed dynamic liquid height and horizontal normal uploaded to the GPU.</summary>
    LiquidSurfaceField = 15,
    /// <summary>Raw forward-rasterized entity colour and coverage below the active liquid plane.</summary>
    EntityMirror = 16,
    /// <summary>Voxel/native solar occlusion and native-cascade support encoded independently.</summary>
    NativeSunShadow = 17
}

/// <summary>
/// Selects a complete hardware-oriented rendering budget without changing the authored
/// color grade or the physical material/liquid definitions.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum VintageRtxRenderProfile
{
    /// <summary>Preserves individually authored configuration values and the full adaptive range.</summary>
    Custom = 0,
    /// <summary>Uses the minimum full-frame transport tier for entry-level supported GPUs.</summary>
    Performance = 1,
    /// <summary>Balances coherent secondary transport with stable mid-range GPU frame pacing.</summary>
    Balanced = 2,
    /// <summary>Targets high-end GPUs with longer rays and higher spatial sampling.</summary>
    Quality = 3,
    /// <summary>Locks the complete renderer at the original high-end spatial quality.</summary>
    Ultra = 4,
    /// <summary>Targets current flagship GPUs with denser visibility, reflection, and bounce sampling.</summary>
    Extreme = 5,
    /// <summary>Prioritizes maximum native render fidelity over interactive frame rate.</summary>
    Cinematic = 6
}

/// <summary>
/// Serializable rendering contract. Every value is normalized by <see cref="Clamp"/> before use;
/// distances are world blocks unless explicitly described otherwise and strengths are unitless.
/// </summary>
public sealed class VintageRtxConfig
{
    /// <summary>Latest configuration schema understood without losing unknown future fields.</summary>
    public const int CurrentSchemaVersion = 14;

    /// <summary>Gets or sets the on-disk migration version; callers must not decrement it.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// Gets or sets the persistent hardware-oriented profile; <see cref="VintageRtxRenderProfile.Custom"/>
    /// preserves individually edited sampling and distance controls.
    /// </summary>
    public VintageRtxRenderProfile RenderProfile { get; set; } = VintageRtxRenderProfile.Custom;

    /// <summary>Gets or sets whether the post-processing renderer participates in the frame.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets exposure compensation in stops, clamped to -2..2 EV.</summary>
    public float Exposure { get; set; } = 0.0f;

    /// <summary>Gets or sets the display contrast multiplier in the inclusive range 0.5..1.5.</summary>
    public float Contrast { get; set; } = 1.0f;

    /// <summary>Gets or sets global chroma scaling in the inclusive range 0..2.</summary>
    public float Saturation { get; set; } = 1.0f;

    /// <summary>Gets or sets selective low-saturation enhancement in the range -0.5..0.5.</summary>
    public float Vibrance { get; set; } = 0.0f;

    /// <summary>Gets or sets edge darkening strength in the inclusive range 0..0.5.</summary>
    public float Vignette { get; set; } = 0.04f;

    /// <summary>Enables short-range full-resolution directional lighting reconstruction.</summary>
    public bool ScreenSpaceLightingEnabled { get; set; } = true;

    /// <summary>Enables high-confidence reflection rays that remain inside the camera buffers.</summary>
    public bool ScreenSpaceReflectionsEnabled { get; set; } = true;

    /// <summary>Enables off-screen reflection fallback through the world voxel representation.</summary>
    public bool VoxelReflectionsEnabled { get; set; } = true;

    /// <summary>Enables voxel visibility, bounce, and emissive radiance transport.</summary>
    public bool VoxelLightingEnabled { get; set; } = true;

    /// <summary>Allows the performance monitor to reduce ray work to respect the GPU budget.</summary>
    public bool AdaptiveQualityEnabled { get; set; } = true;

    /// <summary>Gets or sets the VintageRTX GPU-frame target in milliseconds, clamped to 0.75..40.</summary>
    public float GpuBudgetMilliseconds { get; set; } = 2.00f;

    /// <summary>Enables history accumulation used to denoise stable surfaces and soft shadows.</summary>
    public bool TemporalAccumulationEnabled { get; set; } = true;

    /// <summary>Gets or sets retained history fraction in 0..0.95; higher values converge more slowly.</summary>
    public float TemporalHistoryWeight { get; set; } = 0.92f;

    /// <summary>Gets or sets diffuse indirect-radiance gain in the inclusive range 0..1.5.</summary>
    public float IndirectLightStrength { get; set; } = 0.82f;

    /// <summary>
    /// Controls how strongly traced visibility and indirect transport modify
    /// the full-resolution raster material carrier.
    /// </summary>
    public float RelightingStrength { get; set; } = 0.88f;

    /// <summary>Gets or sets sky/environment transport gain in the inclusive range 0..2.</summary>
    public float SkyLightStrength { get; set; } = 0.78f;

    /// <summary>Gets or sets voxel emissive-radiance gain in the inclusive range 0..2.5.</summary>
    public float EmissiveLightStrength { get; set; } = 1.25f;

    /// <summary>Gets or sets local contact-occlusion strength in the inclusive range 0..1.</summary>
    public float ContactShadowStrength { get; set; } = 0.38f;

    /// <summary>Gets or sets final reflection energy gain in the inclusive range 0..1.5.</summary>
    public float ReflectionStrength { get; set; } = 0.72f;

    /// <summary>Gets or sets maximum screen-space reflection travel in blocks, clamped to 1..48.</summary>
    public float ReflectionDistance { get; set; } = 10.0f;

    /// <summary>Gets or sets local-light shadow opacity in the inclusive range 0..1.</summary>
    public float PointLightShadowStrength { get; set; } = 0.72f;

    /// <summary>Gets or sets local-light indirect-radiance gain in the inclusive range 0..1.5.</summary>
    public float PointLightBounceStrength { get; set; } = 0.90f;

    /// <summary>Gets or sets maximum diffuse voxel-bounce travel in blocks, clamped to 2..24.</summary>
    public float VoxelBounceDistance { get; set; } = 6.0f;

    /// <summary>Gets or sets coherent bounce rays per pixel in the inclusive range 1..4.</summary>
    public int VoxelBounceRayCount { get; set; } = 2;

    /// <summary>Gets or sets local-light influence radius in blocks, clamped to 4..32.</summary>
    public float PointLightRadius { get; set; } = 18.0f;

    /// <summary>
    /// Gets or sets the fallback luminous half-size in metres for dynamic emitters without an
    /// authored photometric profile, clamped to 0..0.5. Static flames use their own SI dimensions.
    /// </summary>
    public float PointLightSourceRadius { get; set; } = 0.025f;

    /// <summary>Gets or sets coherent area-light samples per pixel in the inclusive range 1..8.</summary>
    public int PointLightShadowSamples { get; set; } = 4;

    /// <summary>Enables hybrid long-range solar visibility tracing.</summary>
    public bool SunShadowsEnabled { get; set; } = true;

    /// <summary>Gets or sets direct solar-radiance gain in the inclusive range 0..2.5.</summary>
    public float SunLightStrength { get; set; } = 1.10f;

    /// <summary>
    /// Gets or sets minimum solar shadow reach in blocks, clamped to 16..640. Runtime coverage
    /// expands to the client/server-approved block view distance using a coarser distant LOD.
    /// </summary>
    public float SunShadowDistance { get; set; } = 64.0f;

    /// <summary>Gets or sets short-range scene-space lighting distance in blocks, clamped to 0.25..8.</summary>
    public float RayDistance { get; set; } = 2.4f;

    /// <summary>Gets or sets scene-space directions sampled per pixel in the inclusive range 1..8.</summary>
    public int RayCount { get; set; } = 3;

    /// <summary>Gets or sets integration steps per scene-space ray in the inclusive range 4..24.</summary>
    public int RaySteps { get; set; } = 8;

    /// <summary>Gets or sets the diagnostic output selected for capture and live inspection.</summary>
    public VintageRtxDebugView DebugView { get; set; } = VintageRtxDebugView.Final;

    /// <summary>
    /// Gets the least expensive adaptive tier allowed by the selected profile: zero is high,
    /// one is balanced, and two is performance.
    /// </summary>
    internal int AdaptiveQualityFloor => RenderProfile switch
    {
        VintageRtxRenderProfile.Performance => 2,
        VintageRtxRenderProfile.Balanced => 1,
        _ => 0
    };

    /// <summary>Clamps every configurable scalar and restores an invalid debug enumeration.</summary>
    public void Clamp()
    {
        Exposure = Math.Clamp(Exposure, -2.0f, 2.0f);
        Contrast = Math.Clamp(Contrast, 0.5f, 1.5f);
        Saturation = Math.Clamp(Saturation, 0.0f, 2.0f);
        Vibrance = Math.Clamp(Vibrance, -0.5f, 0.5f);
        Vignette = Math.Clamp(Vignette, 0.0f, 0.5f);
        GpuBudgetMilliseconds = Math.Clamp(GpuBudgetMilliseconds, 0.75f, 40.0f);
        TemporalHistoryWeight = Math.Clamp(TemporalHistoryWeight, 0.0f, 0.95f);
        IndirectLightStrength = Math.Clamp(IndirectLightStrength, 0.0f, 1.5f);
        RelightingStrength = Math.Clamp(RelightingStrength, 0.0f, 1.0f);
        SkyLightStrength = Math.Clamp(SkyLightStrength, 0.0f, 2.0f);
        EmissiveLightStrength = Math.Clamp(EmissiveLightStrength, 0.0f, 2.5f);
        ContactShadowStrength = Math.Clamp(ContactShadowStrength, 0.0f, 1.0f);
        ReflectionStrength = Math.Clamp(ReflectionStrength, 0.0f, 1.5f);
        ReflectionDistance = Math.Clamp(ReflectionDistance, 1.0f, 48.0f);
        PointLightShadowStrength = Math.Clamp(PointLightShadowStrength, 0.0f, 1.0f);
        PointLightBounceStrength = Math.Clamp(PointLightBounceStrength, 0.0f, 1.5f);
        VoxelBounceDistance = Math.Clamp(VoxelBounceDistance, 2.0f, 24.0f);
        VoxelBounceRayCount = Math.Clamp(VoxelBounceRayCount, 1, 4);
        PointLightRadius = Math.Clamp(PointLightRadius, 4.0f, 32.0f);
        PointLightSourceRadius = Math.Clamp(PointLightSourceRadius, 0.0f, 0.5f);
        PointLightShadowSamples = Math.Clamp(PointLightShadowSamples, 1, 8);
        SunLightStrength = Math.Clamp(SunLightStrength, 0.0f, 2.5f);
        SunShadowDistance = Math.Clamp(SunShadowDistance, 16.0f, 640.0f);
        RayDistance = Math.Clamp(RayDistance, 0.25f, 8.0f);
        RayCount = Math.Clamp(RayCount, 1, 8);
        RaySteps = Math.Clamp(RaySteps, 4, 24);

        if (!Enum.IsDefined(DebugView))
        {
            DebugView = VintageRtxDebugView.Final;
        }

        if (!Enum.IsDefined(RenderProfile))
        {
            RenderProfile = VintageRtxRenderProfile.Custom;
        }
    }

    /// <summary>
    /// Applies every schema migration in order while preserving user-tuned values unless they equal
    /// a superseded default. The operation is idempotent at the current schema.
    /// </summary>
    /// <returns>Whether any schema step changed the configuration.</returns>
    public bool Migrate()
    {
        bool migrated = false;
        if (SchemaVersion < 2)
        {
            // v2 replaces the short directional segment with a long-reach hybrid sun trace.
            SunShadowDistance = 64.0f;
            SchemaVersion = 2;
            migrated = true;
        }

        if (SchemaVersion < 3)
        {
            // v3 replaces the legacy room-wide point radius with a localized physical range.
            if (Math.Abs(PointLightRadius - 28.0f) < 0.01f)
            {
                PointLightRadius = 18.0f;
            }

            SchemaVersion = 3;
            migrated = true;
        }

        if (SchemaVersion < 4)
        {
            // v4 adds camera-safe temporal accumulation for denoised area-light shadows.
            TemporalAccumulationEnabled = true;
            TemporalHistoryWeight = 0.78f;
            PointLightSourceRadius = 0.18f;
            SchemaVersion = 4;
            migrated = true;
        }

        if (SchemaVersion < 5)
        {
            // v5 tightens the area light and increases temporal convergence to
            // remove residual visibility noise without widening cast shadows.
            TemporalHistoryWeight = 0.88f;
            PointLightSourceRadius = 0.10f;
            SchemaVersion = 5;
            migrated = true;
        }

        if (SchemaVersion < 6)
        {
            // v6 replaces independent per-pixel white noise with coherent
            // low-discrepancy area-light samples and stronger static convergence.
            PointLightShadowSamples = 3;
            if (Math.Abs(TemporalHistoryWeight - 0.88f) < 0.001f)
            {
                TemporalHistoryWeight = 0.92f;
            }

            SchemaVersion = 6;
            migrated = true;
        }

        if (SchemaVersion < 7)
        {
            // v7 adds a bounded scene-space voxel bounce. It is deliberately
            // capped at two coherent rays so adaptive quality can preserve
            // stable frame pacing on the same hardware as the v6 renderer.
            VoxelBounceDistance = 6.0f;
            VoxelBounceRayCount = 2;
            SchemaVersion = 7;
            migrated = true;
        }

        if (SchemaVersion < 8)
        {
            // v8 removes the residual point-cloud penumbra. High quality uses
            // the complete four-sample coherent disk; adaptive tiers retain
            // their existing three/two-sample caps for stable frame pacing.
            PointLightShadowSamples = 4;
            SchemaVersion = 8;
            migrated = true;
        }

        if (SchemaVersion < 9)
        {
            // v9 adds bounded off-screen scene-space reflections. SSR remains
            // the highest-confidence path; the voxel trace fills genuine misses.
            VoxelReflectionsEnabled = true;
            SchemaVersion = 9;
            migrated = true;
        }

        if (SchemaVersion < 10)
        {
            // v10 is the first transport-dominant renderer. Earlier versions
            // only overlaid small ray-traced corrections on Vintage Story's
            // already-lit framebuffer, which could never create an RTX/PTGI
            // contrast range. Preserve custom values, but upgrade untouched v9
            // defaults to the physically based relighting calibration.
            if (Math.Abs(GpuBudgetMilliseconds - 1.60f) < 0.01f)
            {
                GpuBudgetMilliseconds = 3.50f;
            }
            if (Math.Abs(IndirectLightStrength - 0.32f) < 0.01f)
            {
                IndirectLightStrength = 0.82f;
            }
            if (Math.Abs(PointLightBounceStrength - 0.28f) < 0.01f)
            {
                PointLightBounceStrength = 0.90f;
            }
            if (Math.Abs(SunLightStrength - 0.28f) < 0.01f)
            {
                SunLightStrength = 1.10f;
            }

            RelightingStrength = 0.88f;
            SkyLightStrength = 0.78f;
            EmissiveLightStrength = 1.25f;
            SchemaVersion = 10;
            migrated = true;
        }

        if (SchemaVersion < 11)
        {
            // v11 keeps the transport-dominant image but converges to the
            // performance tier quickly enough to preserve frame pacing at
            // native 1080p. High quality remains available when adaptive
            // quality is disabled explicitly.
            if (Math.Abs(GpuBudgetMilliseconds - 3.50f) < 0.01f)
            {
                GpuBudgetMilliseconds = 2.00f;
            }

            SchemaVersion = 11;
            migrated = true;
        }

        if (SchemaVersion < 12)
        {
            // v12 anchors the hybrid transport to the game's full-resolution
            // material carrier. Remove the old cinematic grade from untouched
            // configurations so warm emitters remain local and shadow detail
            // is not crushed a second time after radiance composition.
            if (Math.Abs(Contrast - 1.06f) < 0.001f)
            {
                Contrast = 1.0f;
            }
            if (Math.Abs(Saturation - 1.04f) < 0.001f)
            {
                Saturation = 1.0f;
            }
            if (Math.Abs(Vibrance - 0.08f) < 0.001f)
            {
                Vibrance = 0.0f;
            }
            if (Math.Abs(Vignette - 0.10f) < 0.001f)
            {
                Vignette = 0.04f;
            }

            SchemaVersion = 12;
            migrated = true;
        }

        if (SchemaVersion < 13)
        {
            // v13 persists the hardware rendering profile. Existing users keep
            // their exact v12 work budgets as Custom instead of receiving an
            // unsolicited visual/performance change during migration.
            RenderProfile = VintageRtxRenderProfile.Custom;
            SchemaVersion = 13;
            migrated = true;
        }

        if (SchemaVersion < 14)
        {
            // v14 replaces the oversized generic spherical emitter with a compact finite-source
            // fallback. Authored static sources supply their measured flame width and height;
            // only the untouched v5 default is migrated so user calibration remains intact.
            if (Math.Abs(PointLightSourceRadius - 0.10f) < 0.001f)
            {
                PointLightSourceRadius = 0.025f;
            }

            SchemaVersion = CurrentSchemaVersion;
            migrated = true;
        }

        return migrated;
    }

    /// <summary>
    /// Applies one complete hardware rendering profile. Every authored profile retains the PBR,
    /// liquid, temporal, shadow, screen-space reflection, and voxel-reflection paths; only their
    /// spatial reach, sample count, and adaptive quality ceiling differ.
    /// </summary>
    /// <param name="profile">Supported hardware-oriented rendering profile.</param>
    /// <exception cref="ArgumentOutOfRangeException">The profile enumeration is invalid.</exception>
    public void ApplyRenderProfile(VintageRtxRenderProfile profile)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown rendering profile.");
        }

        RenderProfile = profile;
        if (profile == VintageRtxRenderProfile.Custom)
        {
            Clamp();
            return;
        }

        ScreenSpaceLightingEnabled = true;
        ScreenSpaceReflectionsEnabled = true;
        VoxelReflectionsEnabled = true;
        VoxelLightingEnabled = true;
        TemporalAccumulationEnabled = true;
        SunShadowsEnabled = true;

        switch (profile)
        {
            case VintageRtxRenderProfile.Performance:
                AdaptiveQualityEnabled = true;
                GpuBudgetMilliseconds = 4.50f;
                ReflectionDistance = 8.0f;
                VoxelBounceDistance = 4.0f;
                VoxelBounceRayCount = 1;
                PointLightShadowSamples = 2;
                SunShadowDistance = 64.0f;
                RayDistance = 1.5f;
                RayCount = 1;
                RaySteps = 4;
                break;
            case VintageRtxRenderProfile.Balanced:
                AdaptiveQualityEnabled = true;
                GpuBudgetMilliseconds = 6.00f;
                ReflectionDistance = 10.0f;
                VoxelBounceDistance = 6.0f;
                VoxelBounceRayCount = 1;
                PointLightShadowSamples = 3;
                SunShadowDistance = 64.0f;
                RayDistance = 2.4f;
                RayCount = 2;
                RaySteps = 6;
                break;
            case VintageRtxRenderProfile.Quality:
                AdaptiveQualityEnabled = true;
                GpuBudgetMilliseconds = 8.50f;
                ReflectionDistance = 16.0f;
                VoxelBounceDistance = 8.0f;
                VoxelBounceRayCount = 2;
                PointLightShadowSamples = 4;
                SunShadowDistance = 80.0f;
                RayDistance = 3.2f;
                RayCount = 3;
                RaySteps = 9;
                break;
            case VintageRtxRenderProfile.Ultra:
                AdaptiveQualityEnabled = false;
                GpuBudgetMilliseconds = 12.0f;
                ReflectionDistance = 24.0f;
                VoxelBounceDistance = 12.0f;
                VoxelBounceRayCount = 2;
                PointLightShadowSamples = 4;
                SunShadowDistance = 96.0f;
                RayDistance = 4.5f;
                RayCount = 4;
                RaySteps = 12;
                break;
            case VintageRtxRenderProfile.Extreme:
                AdaptiveQualityEnabled = false;
                GpuBudgetMilliseconds = 20.0f;
                ReflectionDistance = 32.0f;
                VoxelBounceDistance = 16.0f;
                VoxelBounceRayCount = 3;
                PointLightShadowSamples = 6;
                SunShadowDistance = 96.0f;
                RayDistance = 6.0f;
                RayCount = 6;
                RaySteps = 18;
                break;
            case VintageRtxRenderProfile.Cinematic:
                AdaptiveQualityEnabled = false;
                GpuBudgetMilliseconds = 40.0f;
                ReflectionDistance = 48.0f;
                VoxelBounceDistance = 24.0f;
                VoxelBounceRayCount = 4;
                PointLightShadowSamples = 8;
                SunShadowDistance = 96.0f;
                RayDistance = 8.0f;
                RayCount = 8;
                RaySteps = 24;
                break;
        }

        Clamp();
    }

    /// <summary>
    /// Applies one authored color/lighting grade without changing hardware-profile ray budgets.
    /// </summary>
    /// <param name="preset">Case-insensitive <c>neutral</c>, <c>cinematic</c>, or <c>vivid</c> name.</param>
    /// <exception cref="ArgumentOutOfRangeException">The name is not a supported preset.</exception>
    public void ApplyPreset(string preset)
    {
        switch (preset.ToLowerInvariant())
        {
            case "neutral":
                Exposure = 0.0f;
                Contrast = 1.0f;
                Saturation = 1.0f;
                Vibrance = 0.0f;
                Vignette = 0.0f;
                IndirectLightStrength = 0.62f;
                RelightingStrength = 0.78f;
                SkyLightStrength = 0.72f;
                EmissiveLightStrength = 1.05f;
                ContactShadowStrength = 0.22f;
                break;
            case "cinematic":
                Exposure = -0.03f;
                Contrast = 1.08f;
                Saturation = 1.02f;
                Vibrance = 0.10f;
                Vignette = 0.14f;
                IndirectLightStrength = 0.94f;
                RelightingStrength = 0.94f;
                SkyLightStrength = 0.76f;
                EmissiveLightStrength = 1.42f;
                ContactShadowStrength = 0.44f;
                break;
            case "vivid":
                Exposure = 0.02f;
                Contrast = 1.06f;
                Saturation = 1.12f;
                Vibrance = 0.16f;
                Vignette = 0.08f;
                IndirectLightStrength = 1.02f;
                RelightingStrength = 0.90f;
                SkyLightStrength = 0.88f;
                EmissiveLightStrength = 1.34f;
                ContactShadowStrength = 0.32f;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown rendering preset.");
        }

        Clamp();
    }
}
