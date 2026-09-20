using VintageRTX.Configuration;

namespace VintageRTX.Test;

/// <summary>
/// Supports scenario Definition within the deterministic VintageRTX test infrastructure.
/// </summary>
internal sealed record ScenarioDefinition(
    string Name,
    string Description,
    string World,
    bool Automated,
    string? RuntimeProbe,
    string[] RequiredLogTokens,
    double WorldHour,
    bool ClearWeather = true,
    double MaximumGpuMilliseconds = 2.50,
    double MaximumAverageFrameTimeCostMilliseconds = 3.00,
    double MaximumOnePercentLowFrameTimeCostMilliseconds = 5.00,
    double MinimumEffectFps = 60.0,
    double MinimumEffectOnePercentLowFps = 45.0,
    double MaximumJitterIncreaseMilliseconds = 1.50,
    ShadowValidation ShadowValidation = ShadowValidation.Projected,
    bool ValidateReflections = false,
    bool ValidateVoxelReflections = false,
    bool ValidateVoxelBounce = false,
    bool ValidateWetness = false,
    double? ForcedPrecipitation = null,
    bool UseIsolatedDataPath = false,
    string? WorldPreset = null,
    bool RunBenchmark = true,
    string? CaptureProfile = null,
    bool RequirePbrReferenceMaterials = false,
    VintageRtxRenderProfile? RenderProfile = null,
    string? WorldSeed = null);

/// <summary>
/// Identifies the shadow Validation variants used to drive deterministic renderer assertions.
/// </summary>
internal enum ShadowValidation
{
    /// <summary>
    /// Selects the projected state in the deterministic shadow Validation fixture.
    /// </summary>
    Projected,
    /// <summary>
    /// Selects the sun Projected state in the deterministic shadow Validation fixture.
    /// </summary>
    SunProjected,
    /// <summary>
    /// Selects the camera Aligned state in the deterministic shadow Validation fixture.
    /// </summary>
    CameraAligned,
    /// <summary>
    /// Selects the informational state in the deterministic shadow Validation fixture.
    /// </summary>
    Informational
}

/// <summary>
/// Supports scenario Catalog within the deterministic VintageRTX test infrastructure.
/// </summary>
internal static class ScenarioCatalog
{
    /// <summary>Fixed world-generation seed shared by every isolated RenderLab profile.</summary>
    internal const string RenderLabWorldSeed = "2090501";

    private static readonly ScenarioDefinition[] Scenarios =
    [
        new(
            "render-lab",
            "Micro-scène déterministe et autonome pour itérer rapidement sur PBR, normales et ombres.",
            "vintagertx-render-lab",
            true,
            "render-lab",
            [
                "[VintageRTX.Test] Render lab built",
                $"[VintageRTX.Test] Runtime world seed: {RenderLabWorldSeed}.",
                "[VintageRTX.Test] Render lab fixed anchor requested: world=(512000,232,512000).",
                "Render lab camera applied",
                "Render lab solar aperture verified: side=+X",
                "Render lab materials verified",
                "Scenario render-lab injected 1 moving point light",
                "Render lab light rig verified: sources=3",
                "Render lab reflection targets verified: count=3, emissive=none",
                "Lantern cage evidence game:lantern-large-up: verdict=PASS",
                "Geometry evidence game:anvil-iron: kind=DynamicInstance, detailed non-cube=True",
                "Geometry evidence game:tallgrass-tall-free: kind=StaticComplex, detailed non-cube=True, crossed planes=True",
                "[VintageRTX] Native solar shadow detail active:",
                "-native-sun-shadow-vintagertx.png",
                "Rendering profile Quality active:",
                "Capture profile evidence: label=final, profile=Quality, effective-tier="
            ],
            12.0,
            ShadowValidation: ShadowValidation.Projected,
            ValidateReflections: true,
            ValidateVoxelReflections: true,
            UseIsolatedDataPath: true,
            WorldPreset: "preset-surviveandbuild",
            RunBenchmark: false,
            CaptureProfile: "render-lab",
            RequirePbrReferenceMaterials: true,
            RenderProfile: VintageRtxRenderProfile.Quality,
            WorldSeed: RenderLabWorldSeed),
        CreateRenderLabBenchmark(
            "render-lab-performance",
            "La même micro-scène autonome avec mesure A/B/A courte des FPS, 1 % low et du coût GPU.",
            VintageRtxRenderProfile.Performance),
        CreateRenderLabBenchmark(
            "render-lab-balanced",
            "RenderLab au profil Balanced pour les GPU milieu de gamme.",
            VintageRtxRenderProfile.Balanced),
        CreateRenderLabBenchmark(
            "render-lab-quality",
            "RenderLab au profil Quality pour valider le transport spatial haute fidélité.",
            VintageRtxRenderProfile.Quality),
        CreateRenderLabBenchmark(
            "render-lab-ultra",
            "RenderLab au profil Ultra fixe pour valider le coût maximal authored.",
            VintageRtxRenderProfile.Ultra),
        CreateRenderLabVisualProfile(
            "render-lab-extreme",
            "RenderLab au profil Extreme fixe pour contrôler le rendu des GPU haut de gamme récents sans seuil FPS.",
            VintageRtxRenderProfile.Extreme),
        CreateRenderLabVisualProfile(
            "render-lab-cinematic",
            "RenderLab au profil Cinematic fixe pour contrôler la fidélité native maximale sans seuil FPS.",
            VintageRtxRenderProfile.Cinematic),
        new(
            "reference-room",
            "Lanterne intérieure de foggy village, captures A/B et budget GPU.",
            "foggy village world",
            true,
            null,
            ["Startup validation state applied: /gamemode 2", "Deterministic environment applied", "Voxel scene generation 2 ready", "Stabilized A/B/A result"],
            12.0),
        new(
            "lantern-night",
            "Lanterne intérieure à heure nocturne fixe, profil Cinematic et sans seuil FPS, pour isoler sa photométrie et ses ombres projetées.",
            "foggy village world",
            true,
            null,
            [
                "Deterministic environment applied",
                "hour=0",
                "direct-sun=",
                "Lantern night camera applied",
                "Light-stability isolation removed",
                "intensity=6.50 cd",
                "source=0.030x0.080 m",
                "basis=LBL-Lumina-clean-kerosene-lantern-48lm-6to7cd",
                "Rendering profile Cinematic active:",
                "Capture profile evidence: label=final, profile=Cinematic, effective-tier=",
                "Automatic capture sequence completed: profile=light-stability, last=light-stability-shadow-c, captures=10"
            ],
            0.0,
            RunBenchmark: false,
            CaptureProfile: "light-stability",
            RenderProfile: VintageRtxRenderProfile.Cinematic),
        new(
            "held-light",
            "Source chaude attachée au joueur, équivalente à une torche/lanterne portée.",
            "foggy village world",
            true,
            "held-light",
            ["[VintageRTX.Test] Scenario held-light injected", "Dynamic point lights tracked:", "Stabilized A/B/A result"],
            21.0,
            ShadowValidation: ShadowValidation.CameraAligned),
        new(
            "many-lights-stress",
            "Douze lumières mobiles synthétiques pour valider le cap adaptatif et les 1 % low.",
            "foggy village world",
            true,
            "many-lights-stress",
            ["[VintageRTX.Test] Scenario many-lights-stress injected", "Dynamic point lights tracked:", "Adaptive quality", "Stabilized A/B/A result"],
            21.0,
            ShadowValidation: ShadowValidation.Informational),
        new(
            "exterior-roof",
            "Toit extérieur et ombre solaire longue sans terminaison sphérique.",
            "foggy village world",
            true,
            "exterior-roof",
            ["Exterior roof camera applied", "sun range=64", "Stabilized A/B/A result"],
            10.0,
            MaximumGpuMilliseconds: 3.00,
            ShadowValidation: ShadowValidation.SunProjected),
        new(
            "sunrise-exterior",
            "Lumière solaire rasante du matin, ombres longues et reconstruction temporelle.",
            "foggy village world",
            true,
            "exterior-roof",
            ["Exterior roof camera applied", "sun range=64", "Stabilized A/B/A result"],
            9.0,
            MaximumGpuMilliseconds: 3.00,
            ShadowValidation: ShadowValidation.SunProjected),
        new(
            "vegetation-shadow-map",
            "Cinq plantes alpha-découpées réelles, posées dans un chunk extérieur sain de foggy village et validées sans budget FPS.",
            "foggy village world",
            true,
            "vegetation-shadow-map",
            [
                "Startup validation state applied: /gamemode 2",
                "Deterministic environment verified: PASS",
                "Server vegetation placement command ready: scenario=vegetation-shadow-map, plants=5",
                "Vegetation map chunk audit: PASS",
                "BlockFenceStackAware=",
                "Vegetation map placement armed: count=5",
                "Vegetation map placement requested: count=5",
                "Server vegetation patch placed: count=5",
                "Vegetation map camera applied:",
                "Vegetation map geometry verified: PASS | count=5, alpha-cutout=5, crossed-planes=3, json-shapes=2",
                "Vegetation map capture gate released",
                "[VintageRTX] Native solar shadow detail active:",
                "-final-vintagertx.png",
                "-voxel-shadow-vintagertx.png",
                "-native-sun-shadow-vintagertx.png",
                "Automatic capture sequence completed: profile=vegetation-shadow-map"
            ],
            10.0,
            ShadowValidation: ShadowValidation.SunProjected,
            RunBenchmark: false,
            CaptureProfile: "vegetation-shadow-map",
            RenderProfile: VintageRtxRenderProfile.Ultra),
        new(
            "moving-camera",
            "Balayage de caméra réel avec rejet d'historique et capture pendant le mouvement.",
            "foggy village world",
            true,
            "moving-camera",
            ["Moving-camera probe started", "Temporal history reset after camera movement", "Moving-camera probe completed", "Stabilized A/B/A result"],
            12.0,
            ShadowValidation: ShadowValidation.Informational),
        new(
            "water-reflection",
            "Surface d'eau réelle et visiblement exposée, cadrée automatiquement dans un monde de jeu non créatif.",
            "vintage cave lands",
            true,
            "water-reflection",
            [
                "Water reflection held-item mask challenge retained through public inventory API",
                "Water reflection camera applied",
                "Reflection witnesses requested: opaque-item=game:stone-granite",
                "Server reflection witnesses spawned",
                "Server reflection witnesses stable: ticks=1500",
                "Server dropped-item impact requested: sequence=3/3",
                "Dropped-item surface impact applied",
                "Server liquid projectile requested: sequence=2/2, kind=arrow",
                "Server liquid projectile spawned: kind=stone",
                "Projectile surface impact applied",
                "source=server-authoritative",
                "projectile-stone-baseline-final-vintagertx.png",
                "projectile-stone-baseline-earlier-surface-field-vintagertx.png",
                "projectile-stone-baseline-prior-surface-field-vintagertx.png",
                "projectile-stone-baseline-surface-field-vintagertx.png",
                "projectile-stone-final-vintagertx.png",
                "projectile-stone-surface-field-vintagertx.png",
                "projectile-arrow-baseline-final-vintagertx.png",
                "projectile-arrow-baseline-earlier-surface-field-vintagertx.png",
                "projectile-arrow-baseline-prior-surface-field-vintagertx.png",
                "projectile-arrow-baseline-surface-field-vintagertx.png",
                "projectile-arrow-final-vintagertx.png",
                "projectile-arrow-surface-field-vintagertx.png",
                "impact-1-reflection-source-vintagertx.png",
                "impact-1-reflection-source-raw.png",
                "Local-body entity-mirror capture completed",
                "reflection-before.png",
                "Automatic capture sequence completed: profile=water-reflection, last=entity-mirror, captures=15",
                "Capture profile evidence: label=final, profile=Cinematic, effective-tier=high"
            ],
            12.0,
            ShadowValidation: ShadowValidation.Informational,
            ValidateReflections: true,
            ValidateVoxelReflections: true,
            RunBenchmark: false,
            CaptureProfile: "water-reflection",
            RenderProfile: VintageRtxRenderProfile.Cinematic),
        new(
            "rain-wetness",
            "Pluie forcée sur surfaces exposées face à un sol abrité, avec masque d'humidité et budget FPS.",
            "foggy village world",
            true,
            "rain-wetness",
            ["Deterministic environment verified: PASS", "Rain wetness exposure pair verified", "Weather wetness: precipitation=", "Stabilized A/B/A result"],
            12.0,
            ClearWeather: false,
            ShadowValidation: ShadowValidation.Informational,
            ValidateWetness: true,
            ForcedPrecipitation: 1.0),
        new(
            "cave-interior",
            "Volume souterrain couvert, sombre et ouvert, éclairé par une source chaude contrôlée.",
            "vintage cave lands",
            true,
            "cave-interior",
            ["Cave interior camera applied", "OnlySunLight=", "Scenario cave-interior injected 1 moving point light", "Stabilized A/B/A result"],
            12.0,
            MaximumGpuMilliseconds: 3.00,
            ShadowValidation: ShadowValidation.Informational,
            ValidateVoxelBounce: true),
        new(
            "nonstandard-geometry",
            "Escaliers, clôtures, lanternes, formes tesselées et blocs ciselés.",
            "foggy village world",
            true,
            "nonstandard-geometry",
            ["daylight=", "Detailed caster game:anvil", "transparent triangles ignored=", "geometry mesh=", "fallback="],
            0.0,
            MaximumAverageFrameTimeCostMilliseconds: 4.50),
        new(
            "third-party-pbr",
            "Pack PBR externe v1 et sidecars authored sans génération runtime.",
            "foggy village world",
            true,
            null,
            ["manifest overrides=", "runtime generation=disabled"],
            12.0),
        new(
            "resize-and-reload",
            "Redimensionnement, rechargement shader et recréation sûre des ressources GPU.",
            "foggy village world",
            true,
            "resize-and-reload",
            ["Resize requested through the public client command", "Alternate framebuffer size observed", "In-memory shader recompiled", "Resize/reload verification: PASS"],
            12.0)
    ];

    /// <summary>Creates one short A/B/A RenderLab row for a persisted hardware profile.</summary>
    /// <param name="name">Unique Test Explorer scenario name.</param>
    /// <param name="description">User-facing profile and hardware intent.</param>
    /// <param name="profile">Persisted profile seeded before game launch.</param>
    /// <returns>A deterministic generated-world scenario with profile-scaled performance gates.</returns>
    private static ScenarioDefinition CreateRenderLabBenchmark(
        string name,
        string description,
        VintageRtxRenderProfile profile)
    {
        VintageRtxConfig config = new();
        config.ApplyRenderProfile(profile);
        (double minimumFps, double minimumOnePercentLow) = profile switch
        {
            VintageRtxRenderProfile.Performance => (60.0, 45.0),
            VintageRtxRenderProfile.Balanced => (55.0, 40.0),
            VintageRtxRenderProfile.Quality => (45.0, 32.0),
            VintageRtxRenderProfile.Ultra => (35.0, 25.0),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "RenderLab requires an authored profile.")
        };

        return new ScenarioDefinition(
            name,
            description,
            "vintagertx-render-lab",
            true,
            "render-lab",
            [
                "[VintageRTX.Test] Render lab built",
                $"[VintageRTX.Test] Runtime world seed: {RenderLabWorldSeed}.",
                "[VintageRTX.Test] Render lab fixed anchor requested: world=(512000,232,512000).",
                "Render lab camera applied",
                "Render lab solar aperture verified: side=+X",
                "Render lab materials verified",
                "Scenario render-lab injected 1 moving point light",
                "Render lab light rig verified: sources=3",
                "Render lab reflection targets verified: count=3, emissive=none",
                "Lantern cage evidence game:lantern-large-up: verdict=PASS",
                "Geometry evidence game:anvil-iron: kind=DynamicInstance, detailed non-cube=True",
                "Geometry evidence game:tallgrass-tall-free: kind=StaticComplex, detailed non-cube=True, crossed planes=True",
                "[VintageRTX] Native solar shadow detail active:",
                "-native-sun-shadow-vintagertx.png",
                $"Rendering profile {profile} active:",
                $"Capture profile evidence: label=final, profile={profile}, effective-tier=",
                "Stabilized A/B/A result"
            ],
            12.0,
            MaximumGpuMilliseconds: config.GpuBudgetMilliseconds,
            MaximumAverageFrameTimeCostMilliseconds: config.GpuBudgetMilliseconds,
            MaximumOnePercentLowFrameTimeCostMilliseconds: config.GpuBudgetMilliseconds + 2.0,
            MinimumEffectFps: minimumFps,
            MinimumEffectOnePercentLowFps: minimumOnePercentLow,
            ShadowValidation: ShadowValidation.Projected,
            ValidateReflections: true,
            ValidateVoxelReflections: true,
            UseIsolatedDataPath: true,
            WorldPreset: "preset-surviveandbuild",
            RunBenchmark: true,
            CaptureProfile: "render-lab",
            RequirePbrReferenceMaterials: true,
            RenderProfile: profile,
            WorldSeed: RenderLabWorldSeed);
    }

    /// <summary>Creates one capture-only RenderLab row for a fixed maximum-fidelity profile.</summary>
    /// <param name="name">Unique Test Explorer scenario name.</param>
    /// <param name="description">User-facing profile and visual-validation intent.</param>
    /// <param name="profile">Persisted fixed profile seeded before game launch.</param>
    /// <returns>A deterministic generated-world scenario without a frame-rate acceptance gate.</returns>
    private static ScenarioDefinition CreateRenderLabVisualProfile(
        string name,
        string description,
        VintageRtxRenderProfile profile)
    {
        if (profile is not VintageRtxRenderProfile.Extreme
            and not VintageRtxRenderProfile.Cinematic)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile,
                "Capture-only RenderLab requires Extreme or Cinematic.");
        }

        return new ScenarioDefinition(
            name,
            description,
            "vintagertx-render-lab",
            true,
            "render-lab",
            [
                "[VintageRTX.Test] Render lab built",
                $"[VintageRTX.Test] Runtime world seed: {RenderLabWorldSeed}.",
                "[VintageRTX.Test] Render lab fixed anchor requested: world=(512000,232,512000).",
                "Render lab camera applied",
                "Render lab solar aperture verified: side=+X",
                "Render lab materials verified",
                "Scenario render-lab injected 1 moving point light",
                "Render lab light rig verified: sources=3",
                "Render lab reflection targets verified: count=3, emissive=none",
                "Lantern cage evidence game:lantern-large-up: verdict=PASS",
                "Geometry evidence game:anvil-iron: kind=DynamicInstance, detailed non-cube=True",
                "Geometry evidence game:tallgrass-tall-free: kind=StaticComplex, detailed non-cube=True, crossed planes=True",
                "[VintageRTX] Native solar shadow detail active:",
                "-native-sun-shadow-vintagertx.png",
                $"Rendering profile {profile} active:",
                $"Capture profile evidence: label=final, profile={profile}, effective-tier="
            ],
            12.0,
            ShadowValidation: ShadowValidation.Projected,
            ValidateReflections: true,
            ValidateVoxelReflections: true,
            UseIsolatedDataPath: true,
            WorldPreset: "preset-surviveandbuild",
            RunBenchmark: false,
            CaptureProfile: "render-lab",
            RequirePbrReferenceMaterials: true,
            RenderProfile: profile,
            WorldSeed: RenderLabWorldSeed);
    }

    /// <summary>
    /// Enumerates every automated runtime scenario in declaration order so
    /// Test Explorer cannot silently omit newly catalogued coverage.
    /// </summary>
    public static IEnumerable<ScenarioDefinition> AutomatedScenarios =>
        Scenarios.Where(static scenario => scenario.Automated);

    /// <summary>
    /// Returns requested fixture operation from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The get result consumed by the caller&apos;s assertion.</returns>
    public static ScenarioDefinition Get(string name)
    {
        return Scenarios.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown scenario '{name}'. Run 'list' to see available scenarios.");
    }

    /// <summary>
    /// Executes the print step used by the deterministic scenario Catalog fixture.
    /// </summary>
    public static void Print()
    {
        foreach (ScenarioDefinition scenario in Scenarios)
        {
            Console.WriteLine($"{scenario.Name,-24} {(scenario.Automated ? "automated" : "planned"),-10} {scenario.Description}");
        }
    }
}
