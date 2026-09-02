using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Rendering;

/// <summary>
/// Resolves asset-authored optical properties to stable byte identifiers and a
/// compact GPU lookup table. Named liquids deliberately have no C# coefficients:
/// their source of truth is the JSON block/item patches, either as synchronized
/// collectible attributes or through the 1.22 client patch bridge.
/// </summary>
internal sealed class LiquidOpticalRegistry
{
    /// <summary>Collectible JSON attribute containing the complete authored optical contract.</summary>
    public const string OpticalAttributeName = "vintageRtxOptics";
    /// <summary>Collectible JSON attribute containing SI transport and thermal properties.</summary>
    public const string PhysicalAttributeName = "vintageRtxPhysics";
    /// <summary>Reserved LUT row indicating that no liquid occupies the sample.</summary>
    public const byte NoLiquidProfileId = 0;
    /// <summary>Reserved neutral LUT row for malformed, missing, or capacity-exceeded profiles.</summary>
    public const byte UnknownLiquidProfileId = byte.MaxValue;
    /// <summary>RGBA texels allocated to each optical profile row.</summary>
    public const int LookupWidth = 10;
    /// <summary>Profile rows addressable by an unsigned byte identifier.</summary>
    public const int LookupHeight = 256;
    /// <summary>Float channels per LUT texel.</summary>
    public const int LookupChannels = 4;

    private readonly Dictionary<CollectibleObject, byte> profileIds =
        new(ReferenceEqualityComparer.Instance);
    private readonly LiquidOpticalProfile?[] profilesById = new LiquidOpticalProfile?[LookupHeight];

    /// <summary>
    /// Parses asset-authored profiles and assigns deterministic IDs by canonical profile identity,
    /// so load order and duplicate collectibles cannot change GPU metadata between runs.
    /// </summary>
    /// <param name="collectibles">Loaded blocks and items that may carry optical attributes.</param>
    /// <param name="logger">Optional runtime diagnostics destination.</param>
    public LiquidOpticalRegistry(IEnumerable<CollectibleObject> collectibles, ILogger? logger = null)
        : this(collectibles, null, logger)
    {
    }

    /// <summary>
    /// Parses asset-authored profiles with a client-side bridge to the same JSON
    /// patches when Vintage Story does not synchronize custom collectible attributes.
    /// </summary>
    /// <param name="collectibles">Loaded blocks and items that may carry optical attributes.</param>
    /// <param name="assets">Client assets containing the physical patch and binding contracts.</param>
    /// <param name="logger">Optional runtime diagnostics destination.</param>
    public LiquidOpticalRegistry(
        IEnumerable<CollectibleObject> collectibles,
        IAssetManager? assets,
        ILogger? logger = null)
    {
        LiquidClientFallbackData fallbackData = assets is null
            ? new LiquidClientFallbackData(
                new Dictionary<string, LiquidOpticalProfile>(StringComparer.Ordinal),
                new Dictionary<string, LiquidContainerOptics>(StringComparer.Ordinal))
            : LiquidOpticalFallbackCatalog.Load(assets, logger);
        IReadOnlyDictionary<string, LiquidOpticalProfile> fallbackProfiles =
            fallbackData.ProfilesByCodeRoot;
        ContainerFallbacks = fallbackData.ContainersByCodeRoot;
        List<(CollectibleObject Collectible, LiquidOpticalProfile Profile)> authored = [];
        foreach (CollectibleObject collectible in collectibles
            .Where(static candidate => candidate?.Code is not null)
            .OrderBy(static candidate => candidate.Code.ToString(), StringComparer.Ordinal))
        {
            if (TryReadProfile(collectible, out LiquidOpticalProfile profile)
                || LiquidOpticalFallbackCatalog.TryResolve(
                    collectible.Code,
                    fallbackProfiles,
                    out profile))
            {
                authored.Add((collectible, profile));
            }
        }

        Dictionary<string, byte> idByCanonicalProfile = new(StringComparer.Ordinal);
        int nextProfileId = 1;
        foreach ((CollectibleObject collectible, LiquidOpticalProfile profile) in authored
            .OrderBy(static entry => entry.Profile.CanonicalIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Collectible.Code.ToString(), StringComparer.Ordinal))
        {
            if (!idByCanonicalProfile.TryGetValue(profile.CanonicalIdentity, out byte profileId))
            {
                if (nextProfileId >= UnknownLiquidProfileId)
                {
                    profileIds[collectible] = UnknownLiquidProfileId;
                    logger?.Warning(
                        "[VintageRTX] Liquid optical profile limit reached; {0} uses the neutral fallback.",
                        collectible.Code);
                    continue;
                }

                profileId = checked((byte)nextProfileId++);
                idByCanonicalProfile.Add(profile.CanonicalIdentity, profileId);
                profilesById[profileId] = profile;
            }

            profileIds[collectible] = profileId;
        }

        ProfileCount = nextProfileId - 1;
        GpuLookup = BuildGpuLookup(profilesById);
        logger?.Notification(
            "[VintageRTX] Liquid optics registry: profiles={0}, annotated collectibles={1}, lookup={2}x{3} RGBA32F; id 0/255 are neutral.",
            ProfileCount,
            profileIds.Count,
            LookupWidth,
            LookupHeight);
    }

    /// <summary>Gets unique authored profiles, excluding reserved neutral rows 0 and 255.</summary>
    public int ProfileCount { get; }

    /// <summary>
    /// Ten RGBA texels per profile row. With the explicit one-block-to-one-metre
    /// calibration, absorption and scattering coefficients use m^-1 and shader
    /// paths in world blocks are converted to metres before Beer-Lambert:
    /// 0=(IOR, transmission, micro-roughness, micro-normal strength),
    /// 1=(absorption RGB per block, opaque flag),
    /// 2=(scattering RGB per block, emission intensity),
    /// 3=(linear emission RGB, Henyey-Greenstein anisotropy),
    /// 4=(wind coupling, wave amplitude, wavelength, wave speed),
    /// 5=(damping, impact response, surface tension, optical viscosity),
    /// 6=(bubble rate, minimum radius, maximum radius, rise duration),
    /// 7=(bubble burst strength, bubble emission boost, reserved, reserved),
    /// 8=(density kg/m3, dynamic viscosity Pa*s, surface tension N/m, resolved-wave energy fraction),
    /// 9=(thermal temperature K, spectral emissivity, metres per world block, reserved).
    /// </summary>
    public float[] GpuLookup { get; }

    /// <summary>Gets client-reconstructed visible-container contracts from the server patch asset.</summary>
    public IReadOnlyDictionary<string, LiquidContainerOptics> ContainerFallbacks { get; }

    /// <summary>Resolves a collectible by reference identity to its stable LUT row.</summary>
    /// <param name="collectible">Exact loaded collectible instance.</param>
    /// <returns>The assigned profile or <see cref="UnknownLiquidProfileId"/> when unannotated.</returns>
    public byte GetProfileId(CollectibleObject collectible)
    {
        return profileIds.TryGetValue(collectible, out byte profileId)
            ? profileId
            : UnknownLiquidProfileId;
    }

    /// <summary>Returns the parsed CPU profile associated with a byte LUT row.</summary>
    /// <param name="profileId">Unsigned profile row.</param>
    /// <returns>The authored profile, or <see langword="null"/> for reserved/unassigned rows.</returns>
    public LiquidOpticalProfile? GetProfile(byte profileId)
    {
        return profilesById[profileId];
    }

    /// <summary>
    /// Builds the production LUT packing from an explicit profile map. This is
    /// intentionally internal so deterministic render fixtures can exercise the
    /// exact runtime layout without duplicating it. Rows 0 and 255 are reserved
    /// for the neutral no-liquid and unknown-liquid fallbacks.
    /// </summary>
    internal static float[] BuildGpuLookupForProfiles(
        IReadOnlyDictionary<byte, LiquidOpticalProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        float[] lookup = new float[LookupWidth * LookupHeight * LookupChannels];
        for (int profileId = 0; profileId < LookupHeight; profileId++)
        {
            WriteNeutralProfile(lookup, profileId);
        }

        foreach ((byte profileId, LiquidOpticalProfile profile) in profiles
            .OrderBy(static entry => entry.Key))
        {
            if (profileId is NoLiquidProfileId or UnknownLiquidProfileId)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profiles),
                    profileId,
                    "Liquid profile rows 0 and 255 are reserved for neutral fallbacks.");
            }

            WriteProfile(lookup, profileId, profile);
        }

        return lookup;
    }

    /// <summary>Parses the complete optics/dynamics attribute atomically; partial contracts are rejected.</summary>
    /// <param name="collectible">Block or item carrying JSON attributes.</param>
    /// <param name="profile">Finite, range-checked optical profile on success.</param>
    /// <returns>Whether every required scalar, RGB triplet, and dynamics field is valid.</returns>
    internal static bool TryReadProfile(
        CollectibleObject collectible,
        out LiquidOpticalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(collectible);
        return TryReadProfile(collectible.Attributes, out profile);
    }

    /// <summary>Parses one raw collectible-attribute object using the production validation contract.</summary>
    /// <param name="attributes">Combined optics and SI physics attributes.</param>
    /// <param name="profile">Finite, range-checked optical profile on success.</param>
    /// <returns>Whether every required scalar, RGB triplet, and dynamics field is valid.</returns>
    internal static bool TryReadProfile(
        JsonObject? attributes,
        out LiquidOpticalProfile profile)
    {
        profile = default;
        if (attributes is null)
        {
            return false;
        }

        JsonObject optics = attributes[OpticalAttributeName];
        JsonObject physics = attributes[PhysicalAttributeName];
        if (!optics.Exists
            || !physics.Exists
            || !HasRequiredFields(optics, physics)
            || !TryReadRgb(optics["absorptionRgb"], out LiquidRgb absorption)
            || !TryReadRgb(optics["scatteringRgb"], out LiquidRgb scattering)
            || !TryReadRgb(optics["emissionRgb"], out LiquidRgb emission))
        {
            return false;
        }

        string profileKey = optics["profile"].AsString(string.Empty).Trim().ToLowerInvariant();
        float ior = optics["ior"].AsFloat(float.NaN);
        float transmission = optics["transmission"].AsFloat(float.NaN);
        float emissionIntensity = optics["emissionIntensity"].AsFloat(float.NaN);
        float roughness = optics["roughness"].AsFloat(float.NaN);
        float microNormalStrength = optics["microNormalStrength"].AsFloat(float.NaN);
        float opticalViscosity = optics["opticalViscosity"].AsFloat(float.NaN);
        float scatteringAnisotropy = optics["scatteringAnisotropy"].AsFloat(float.NaN);
        string physicalBasisKey = physics["basis"].AsString(string.Empty).Trim().ToLowerInvariant();
        float referenceTemperatureKelvins = physics["referenceTemperatureK"].AsFloat(float.NaN);
        float densityKilogramsPerCubicMetre = physics["densityKgM3"].AsFloat(float.NaN);
        float dynamicViscosityPascalSeconds = physics["dynamicViscosityPaS"].AsFloat(float.NaN);
        float surfaceTensionNewtonsPerMetre = physics["surfaceTensionNm"].AsFloat(float.NaN);
        float resolvedWaveEnergyFraction = physics["resolvedWaveEnergyFraction"].AsFloat(float.NaN);
        float thermalTemperatureKelvins = physics["thermalTemperatureK"].AsFloat(float.NaN);
        float thermalEmissivity = physics["thermalEmissivity"].AsFloat(float.NaN);
        JsonObject dynamics = optics["surfaceDynamics"];
        float windCoupling = dynamics["windCoupling"].AsFloat(float.NaN);
        float waveAmplitude = dynamics["waveAmplitude"].AsFloat(float.NaN);
        float waveLength = dynamics["waveLength"].AsFloat(float.NaN);
        float waveSpeed = dynamics["waveSpeed"].AsFloat(float.NaN);
        float damping = dynamics["damping"].AsFloat(float.NaN);
        float impactResponse = dynamics["impactResponse"].AsFloat(float.NaN);
        float surfaceTension = dynamics["surfaceTension"].AsFloat(float.NaN);
        float bubbleRate = dynamics["bubbleRate"].AsFloat(float.NaN);
        float bubbleRadiusMinimum = dynamics["bubbleRadiusMin"].AsFloat(float.NaN);
        float bubbleRadiusMaximum = dynamics["bubbleRadiusMax"].AsFloat(float.NaN);
        float bubbleRiseDuration = dynamics["bubbleRiseDuration"].AsFloat(float.NaN);
        float bubbleBurstStrength = dynamics["bubbleBurstStrength"].AsFloat(float.NaN);
        float bubbleEmissionBoost = dynamics["bubbleEmissionBoost"].AsFloat(float.NaN);
        bool opaque = optics["opaque"].AsBool(false);
        if (profileKey.Length == 0
            || !IsInRange(ior, 1.0f, 3.0f)
            || !IsInRange(transmission, 0.0f, 1.0f)
            || !IsInRange(emissionIntensity, 0.0f, 64.0f)
            || !IsInRange(roughness, 0.0f, 1.0f)
            || !IsInRange(microNormalStrength, 0.0f, 4.0f)
            || !IsInRange(opticalViscosity, 0.0f, 1.0f)
            || !IsInRange(scatteringAnisotropy, -0.95f, 0.95f)
            || physicalBasisKey.Length == 0
            || !IsInRange(referenceTemperatureKelvins, 200.0f, 3000.0f)
            || !IsInRange(densityKilogramsPerCubicMetre, 100.0f, 10000.0f)
            || !IsInRange(dynamicViscosityPascalSeconds, 0.000001f, 100000.0f)
            || !IsInRange(surfaceTensionNewtonsPerMetre, 0.0001f, 10.0f)
            || !IsInRange(resolvedWaveEnergyFraction, 0.0f, 1.0f)
            || !IsInRange(thermalTemperatureKelvins, 0.0f, 3000.0f)
            || (thermalTemperatureKelvins > 0.0f && thermalTemperatureKelvins < 200.0f)
            || !IsInRange(thermalEmissivity, 0.0f, 1.0f)
            || ((thermalTemperatureKelvins == 0.0f) != (thermalEmissivity == 0.0f))
            || !IsInRange(windCoupling, 0.0f, 4.0f)
            || !IsInRange(waveAmplitude, 0.0f, 4.0f)
            || !IsInRange(waveLength, 0.01f, 256.0f)
            || !IsInRange(waveSpeed, 0.0f, 32.0f)
            || !IsInRange(damping, 0.0f, 1.0f)
            || !IsInRange(impactResponse, 0.0f, 4.0f)
            || !IsInRange(surfaceTension, 0.0f, 4.0f)
            || !IsInRange(bubbleRate, 0.0f, 64.0f)
            || !IsInRange(bubbleRadiusMinimum, 0.0f, 4.0f)
            || !IsInRange(bubbleRadiusMaximum, bubbleRadiusMinimum, 4.0f)
            || !IsInRange(bubbleRiseDuration, 0.0f, 64.0f)
            || !IsInRange(bubbleBurstStrength, 0.0f, 16.0f)
            || !IsInRange(bubbleEmissionBoost, 0.0f, 64.0f)
            || !absorption.IsInRange(0.0f, 64.0f)
            || !scattering.IsInRange(0.0f, 64.0f)
            || !emission.IsInRange(0.0f, 64.0f))
        {
            return false;
        }

        profile = new LiquidOpticalProfile(
            profileKey,
            ior,
            absorption,
            scattering,
            transmission,
            emission,
            emissionIntensity,
            roughness,
            microNormalStrength,
            opticalViscosity,
            scatteringAnisotropy,
            new LiquidSurfaceDynamics(
                windCoupling,
                waveAmplitude,
                waveLength,
                waveSpeed,
                damping,
                impactResponse,
                surfaceTension,
                bubbleRate,
                bubbleRadiusMinimum,
                bubbleRadiusMaximum,
                bubbleRiseDuration,
                bubbleBurstStrength,
                bubbleEmissionBoost),
            opaque,
            new LiquidPhysicalProperties(
                physicalBasisKey,
                referenceTemperatureKelvins,
                densityKilogramsPerCubicMetre,
                dynamicViscosityPascalSeconds,
                surfaceTensionNewtonsPerMetre,
                resolvedWaveEnergyFraction,
                thermalTemperatureKelvins,
                thermalEmissivity));
        return true;
    }

    /// <summary>Checks structural completeness before conversions can substitute default values.</summary>
    /// <param name="optics">Candidate optics JSON object.</param>
    /// <param name="physics">Candidate SI physical-properties JSON object.</param>
    /// <returns>Whether all optical and nested surface-dynamics keys exist.</returns>
    private static bool HasRequiredFields(JsonObject optics, JsonObject physics)
    {
        return optics.KeyExists("profile")
            && optics.KeyExists("ior")
            && optics.KeyExists("absorptionRgb")
            && optics.KeyExists("scatteringRgb")
            && optics.KeyExists("transmission")
            && optics.KeyExists("emissionRgb")
            && optics.KeyExists("emissionIntensity")
            && optics.KeyExists("roughness")
            && optics.KeyExists("microNormalStrength")
            && optics.KeyExists("opticalViscosity")
            && optics.KeyExists("scatteringAnisotropy")
            && optics.KeyExists("surfaceDynamics")
            && optics["surfaceDynamics"].KeyExists("windCoupling")
            && optics["surfaceDynamics"].KeyExists("waveAmplitude")
            && optics["surfaceDynamics"].KeyExists("waveLength")
            && optics["surfaceDynamics"].KeyExists("waveSpeed")
            && optics["surfaceDynamics"].KeyExists("damping")
            && optics["surfaceDynamics"].KeyExists("impactResponse")
            && optics["surfaceDynamics"].KeyExists("surfaceTension")
            && optics["surfaceDynamics"].KeyExists("bubbleRate")
            && optics["surfaceDynamics"].KeyExists("bubbleRadiusMin")
            && optics["surfaceDynamics"].KeyExists("bubbleRadiusMax")
            && optics["surfaceDynamics"].KeyExists("bubbleRiseDuration")
            && optics["surfaceDynamics"].KeyExists("bubbleBurstStrength")
            && optics["surfaceDynamics"].KeyExists("bubbleEmissionBoost")
            && optics.KeyExists("opaque")
            && physics.KeyExists("basis")
            && physics.KeyExists("referenceTemperatureK")
            && physics.KeyExists("densityKgM3")
            && physics.KeyExists("dynamicViscosityPaS")
            && physics.KeyExists("surfaceTensionNm")
            && physics.KeyExists("resolvedWaveEnergyFraction")
            && physics.KeyExists("thermalTemperatureK")
            && physics.KeyExists("thermalEmissivity");
    }

    /// <summary>Reads exactly three finite linear RGB components without clamping authored errors.</summary>
    /// <param name="value">JSON array candidate.</param>
    /// <param name="rgb">Decoded triplet on success.</param>
    /// <returns>Whether the array has exactly three finite numbers.</returns>
    private static bool TryReadRgb(JsonObject value, out LiquidRgb rgb)
    {
        rgb = default;
        JsonObject[]? components = value.AsArray();
        if (components is not { Length: 3 })
        {
            return false;
        }

        float red = components[0].AsFloat(float.NaN);
        float green = components[1].AsFloat(float.NaN);
        float blue = components[2].AsFloat(float.NaN);
        if (!float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue))
        {
            return false;
        }

        rgb = new LiquidRgb(red, green, blue);
        return true;
    }

    /// <summary>Validates a finite inclusive scalar range.</summary>
    /// <param name="value">Candidate scalar.</param>
    /// <param name="minimum">Inclusive lower bound.</param>
    /// <param name="maximum">Inclusive upper bound.</param>
    /// <returns>Whether the value is finite and within bounds.</returns>
    private static bool IsInRange(float value, float minimum, float maximum)
    {
        return float.IsFinite(value) && value >= minimum && value <= maximum;
    }

    /// <summary>Creates the fixed 8x256 RGBA32F upload payload, initializing every row to neutral.</summary>
    /// <param name="profiles">Nullable profiles indexed by assigned byte ID.</param>
    /// <returns>Row-major float payload ready for <c>TexImage2D</c>.</returns>
    private static float[] BuildGpuLookup(LiquidOpticalProfile?[] profiles)
    {
        float[] lookup = new float[LookupWidth * LookupHeight * LookupChannels];
        for (int profileId = 0; profileId < LookupHeight; profileId++)
        {
            WriteNeutralProfile(lookup, profileId);
            if (profiles[profileId] is LiquidOpticalProfile profile)
            {
                WriteProfile(lookup, profileId, profile);
            }
        }

        return lookup;
    }

    /// <summary>Writes an optically neutral non-liquid fallback into one row's first texel.</summary>
    /// <param name="destination">Complete row-major LUT payload.</param>
    /// <param name="profileId">Row index in 0..255.</param>
    private static void WriteNeutralProfile(float[] destination, int profileId)
    {
        int offset = profileId * LookupWidth * LookupChannels;
        destination[offset] = 1.0f;
        destination[offset + 1] = 1.0f;
        destination[offset + 2] = 1.0f;
        destination[offset + 3] = 0.0f;
    }

    /// <summary>Packs one validated profile according to the shader's eight-texel ABI.</summary>
    /// <param name="destination">Complete row-major LUT payload.</param>
    /// <param name="profileId">Non-reserved row index.</param>
    /// <param name="profile">Validated CPU profile.</param>
    private static void WriteProfile(
        float[] destination,
        int profileId,
        LiquidOpticalProfile profile)
    {
        int offset = profileId * LookupWidth * LookupChannels;
        destination[offset] = profile.IndexOfRefraction;
        destination[offset + 1] = profile.Transmission;
        destination[offset + 2] = profile.Roughness;
        destination[offset + 3] = profile.MicroNormalStrength;

        destination[offset + 4] = profile.Absorption.Red;
        destination[offset + 5] = profile.Absorption.Green;
        destination[offset + 6] = profile.Absorption.Blue;
        destination[offset + 7] = profile.Opaque ? 1.0f : 0.0f;

        destination[offset + 8] = profile.Scattering.Red;
        destination[offset + 9] = profile.Scattering.Green;
        destination[offset + 10] = profile.Scattering.Blue;
        destination[offset + 11] = profile.EmissionIntensity;

        destination[offset + 12] = profile.Emission.Red;
        destination[offset + 13] = profile.Emission.Green;
        destination[offset + 14] = profile.Emission.Blue;
        destination[offset + 15] = profile.ScatteringAnisotropy;

        destination[offset + 16] = profile.SurfaceDynamics.WindCoupling;
        destination[offset + 17] = profile.SurfaceDynamics.WaveAmplitude;
        destination[offset + 18] = profile.SurfaceDynamics.WaveLength;
        destination[offset + 19] = profile.SurfaceDynamics.WaveSpeed;

        destination[offset + 20] = profile.SurfaceDynamics.Damping;
        destination[offset + 21] = profile.SurfaceDynamics.ImpactResponse;
        destination[offset + 22] = profile.SurfaceDynamics.SurfaceTension;
        destination[offset + 23] = profile.OpticalViscosity;

        destination[offset + 24] = profile.SurfaceDynamics.BubbleRate;
        destination[offset + 25] = profile.SurfaceDynamics.BubbleRadiusMinimum;
        destination[offset + 26] = profile.SurfaceDynamics.BubbleRadiusMaximum;
        destination[offset + 27] = profile.SurfaceDynamics.BubbleRiseDuration;

        destination[offset + 28] = profile.SurfaceDynamics.BubbleBurstStrength;
        destination[offset + 29] = profile.SurfaceDynamics.BubbleEmissionBoost;

        destination[offset + 32] = profile.PhysicalProperties.DensityKilogramsPerCubicMetre;
        destination[offset + 33] = profile.PhysicalProperties.DynamicViscosityPascalSeconds;
        destination[offset + 34] = profile.PhysicalProperties.SurfaceTensionNewtonsPerMetre;
        destination[offset + 35] = profile.PhysicalProperties.ResolvedWaveEnergyFraction;

        destination[offset + 36] = profile.PhysicalProperties.ThermalTemperatureKelvins;
        destination[offset + 37] = profile.PhysicalProperties.ThermalEmissivity;
        destination[offset + 38] = (float)LiquidPhysicalModel.MetresPerWorldBlock;
    }
}

/// <summary>
/// Complete per-liquid optical/material contract. Absorption and scattering use inverse metres;
/// emission is linear RGB, roughness/transmission are unitless, and IOR is relative to vacuum.
/// </summary>
internal readonly record struct LiquidOpticalProfile(
    string ProfileKey,
    float IndexOfRefraction,
    LiquidRgb Absorption,
    LiquidRgb Scattering,
    float Transmission,
    LiquidRgb Emission,
    float EmissionIntensity,
    float Roughness,
    float MicroNormalStrength,
    float OpticalViscosity,
    float ScatteringAnisotropy,
    LiquidSurfaceDynamics SurfaceDynamics,
    bool Opaque,
    LiquidPhysicalProperties PhysicalProperties = default)
{
    /// <summary>Gets a culture-independent, round-trip identity used for deterministic deduplication.</summary>
    public string CanonicalIdentity => string.Join(
        "|",
        ProfileKey,
        Format(IndexOfRefraction),
        Absorption.CanonicalIdentity,
        Scattering.CanonicalIdentity,
        Format(Transmission),
        Emission.CanonicalIdentity,
        Format(EmissionIntensity),
        Format(Roughness),
        Format(MicroNormalStrength),
        Format(OpticalViscosity),
        Format(ScatteringAnisotropy),
        SurfaceDynamics.CanonicalIdentity,
        Opaque ? "1" : "0",
        PhysicalProperties.CanonicalIdentity);

    /// <summary>Formats a float without locale or precision loss.</summary>
    /// <param name="value">Finite profile value.</param>
    /// <returns>Invariant round-trip representation.</returns>
    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// SI material properties separated from empirical surface forcing. Temperature is kelvins,
/// density kg/m3, dynamic viscosity Pa*s, surface tension N/m, and emissivity is unitless.
/// </summary>
internal readonly record struct LiquidPhysicalProperties(
    string BasisKey,
    float ReferenceTemperatureKelvins,
    float DensityKilogramsPerCubicMetre,
    float DynamicViscosityPascalSeconds,
    float SurfaceTensionNewtonsPerMetre,
    float ResolvedWaveEnergyFraction,
    float ThermalTemperatureKelvins,
    float ThermalEmissivity)
{
    /// <summary>Gets a stable exact identity for deterministic profile deduplication.</summary>
    public string CanonicalIdentity => string.Join(
        ",",
        BasisKey ?? string.Empty,
        Format(ReferenceTemperatureKelvins),
        Format(DensityKilogramsPerCubicMetre),
        Format(DynamicViscosityPascalSeconds),
        Format(SurfaceTensionNewtonsPerMetre),
        Format(ResolvedWaveEnergyFraction),
        Format(ThermalTemperatureKelvins),
        Format(ThermalEmissivity));

    /// <summary>Formats an SI scalar without locale or precision loss.</summary>
    /// <param name="value">Finite physical value.</param>
    /// <returns>Invariant round-trip representation.</returns>
    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Asset-authored free-surface closure layered above measured bulk properties. Spatial values use
/// world blocks (one metre by renderer convention), rates and durations use seconds. WindCoupling,
/// ImpactResponse, and bubble strengths are bounded resolved-energy/display closures; Damping is
/// an additional loss rate in s^-1. SurfaceTension is retained only for legacy compatibility because
/// the solver consumes the measured N/m value from <see cref="LiquidSurfacePhysicalProperties"/>.
/// </summary>
internal readonly record struct LiquidSurfaceDynamics(
    float WindCoupling,
    float WaveAmplitude,
    float WaveLength,
    float WaveSpeed,
    float Damping,
    float ImpactResponse,
    float SurfaceTension,
    float BubbleRate,
    float BubbleRadiusMinimum,
    float BubbleRadiusMaximum,
    float BubbleRiseDuration,
    float BubbleBurstStrength,
    float BubbleEmissionBoost)
{
    /// <summary>Gets a stable exact identity covering waves, impacts, and bubble behavior.</summary>
    public string CanonicalIdentity => string.Join(
        ",",
        Format(WindCoupling),
        Format(WaveAmplitude),
        Format(WaveLength),
        Format(WaveSpeed),
        Format(Damping),
        Format(ImpactResponse),
        Format(SurfaceTension),
        Format(BubbleRate),
        Format(BubbleRadiusMinimum),
        Format(BubbleRadiusMaximum),
        Format(BubbleRiseDuration),
        Format(BubbleBurstStrength),
        Format(BubbleEmissionBoost));

    /// <summary>Formats a simulation coefficient without locale or precision loss.</summary>
    /// <param name="value">Finite authored value.</param>
    /// <returns>Invariant round-trip representation.</returns>
    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}

/// <summary>Linear-light RGB triplet used for spectral approximations in the liquid model.</summary>
internal readonly record struct LiquidRgb(float Red, float Green, float Blue)
{
    /// <summary>Gets a culture-independent identity retaining exact component bit-round-trips.</summary>
    public string CanonicalIdentity => string.Join(
        ",",
        Red.ToString("R", CultureInfo.InvariantCulture),
        Green.ToString("R", CultureInfo.InvariantCulture),
        Blue.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Validates all three finite components against a shared inclusive interval.</summary>
    /// <param name="minimum">Inclusive component lower bound.</param>
    /// <param name="maximum">Inclusive component upper bound.</param>
    /// <returns>Whether red, green, and blue are finite and in range.</returns>
    public bool IsInRange(float minimum, float maximum)
    {
        return float.IsFinite(Red)
            && float.IsFinite(Green)
            && float.IsFinite(Blue)
            && Red >= minimum && Red <= maximum
            && Green >= minimum && Green <= maximum
            && Blue >= minimum && Blue <= maximum;
    }
}
