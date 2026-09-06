using Vintagestory.API.Datastructures;

namespace VintageRTX.Rendering;

/// <summary>
/// Describes one finite flame-like emitter in SI photometric units. VintageRTX treats one world
/// block as one metre, matching the human-scale geometry used by the game. The renderer converts
/// illuminance to its scene-linear carrier only after applying inverse-square transport.
/// </summary>
/// <param name="Basis">Short provenance label stored in diagnostics.</param>
/// <param name="LuminousIntensityCandela">Directional luminous intensity in candela.</param>
/// <param name="SourceHalfWidthMetres">Horizontal half-width of the luminous source in metres.</param>
/// <param name="SourceHalfHeightMetres">Vertical half-height of the luminous source in metres.</param>
/// <param name="CutoffIlluminanceLux">Numerical trace cutoff in lux.</param>
/// <param name="ChromaticityX">CIE 1931 x chromaticity, or zero when game colour is retained.</param>
/// <param name="ChromaticityY">CIE 1931 y chromaticity, or zero when game colour is retained.</param>
internal readonly record struct EmitterPhotometry(
    string Basis,
    float LuminousIntensityCandela,
    float SourceHalfWidthMetres,
    float SourceHalfHeightMetres,
    float CutoffIlluminanceLux,
    float ChromaticityX,
    float ChromaticityY)
{
    /// <summary>Attribute inserted by Vintage Story JSON patches and available to third-party emitters.</summary>
    public const string AttributeName = "vintageRtxEmitter";

    /// <summary>
    /// Contributions below 0.02 lux are smoothly truncated to bound traversal. This is a numerical
    /// cutoff, not an alternate attenuation law: energy above it always follows inverse-square falloff.
    /// </summary>
    public const float DefaultCutoffIlluminanceLux = 0.02f;

    /// <summary>
    /// Fixed camera/exposure conversion from lux to the renderer's scene-linear radiance carrier.
    /// The factor includes the Lambertian 1/pi conversion and an indoor exposure multiplier of 1.1.
    /// </summary>
    public const float LuxToRendererRadiance = 1.1f / MathF.PI;

    /// <summary>Gets whether the profile supplies a valid CIE 1931 chromaticity.</summary>
    public bool HasChromaticity => ChromaticityX > 0.0f
        && ChromaticityY > 0.0f
        && ChromaticityX + ChromaticityY < 1.0f;

    /// <summary>Computes the distance where the unoccluded axial illuminance reaches the trace cutoff.</summary>
    /// <returns>Finite trace radius in metres.</returns>
    public float TraceRadiusMetres()
    {
        return MathF.Sqrt(
            Math.Max(LuminousIntensityCandela, 0.0f)
            / Math.Max(CutoffIlluminanceLux, 0.000001f));
    }

    /// <summary>
    /// Computes the apparent vertical half-extent of an axis-aligned flame ellipsoid in the plane
    /// perpendicular to a source-to-receiver ray. Looking horizontally retains the flame height;
    /// looking along its vertical axis exposes only its radial width.
    /// </summary>
    /// <param name="rayVerticalComponent">Normalized source-to-receiver ray Y component.</param>
    /// <returns>Projected half-extent in metres.</returns>
    public float ProjectedVerticalHalfExtentMetres(float rayVerticalComponent)
    {
        float vertical = Math.Clamp(
            float.IsFinite(rayVerticalComponent) ? rayVerticalComponent : 0.0f,
            -1.0f,
            1.0f);
        float verticalSquared = vertical * vertical;
        return MathF.Sqrt(
            SourceHalfHeightMetres * SourceHalfHeightMetres * (1.0f - verticalSquared)
            + SourceHalfWidthMetres * SourceHalfWidthMetres * verticalSquared);
    }

    /// <summary>
    /// Reads a mod-extensible <c>vintageRtxEmitter</c> attribute and scales its reference candela
    /// value by the block's authored light-HSV value.
    /// </summary>
    /// <param name="attributes">Block attributes after JSON patch composition.</param>
    /// <param name="actualLightValue">Runtime V component returned by <c>GetLightHsv</c>.</param>
    /// <param name="photometry">Validated and scaled profile.</param>
    /// <returns>Whether all mandatory SI and chromaticity fields are coherent.</returns>
    public static bool TryRead(
        JsonObject? attributes,
        int actualLightValue,
        out EmitterPhotometry photometry)
    {
        photometry = default;
        if (attributes is null)
        {
            return false;
        }

        JsonObject value = attributes[AttributeName];
        if (!value.Exists
            || !value.KeyExists("luminousIntensityCd")
            || !value.KeyExists("sourceHalfWidthM")
            || !value.KeyExists("sourceHalfHeightM")
            || !value.KeyExists("referenceLightHsvValue"))
        {
            return false;
        }

        string basis = value["basis"].AsString("authored-si-photometry").Trim();
        float referenceCandela = value["luminousIntensityCd"].AsFloat(float.NaN);
        float sourceHalfWidth = value["sourceHalfWidthM"].AsFloat(float.NaN);
        float sourceHalfHeight = value["sourceHalfHeightM"].AsFloat(float.NaN);
        float referenceLightValue = value["referenceLightHsvValue"].AsFloat(float.NaN);
        float cutoffIlluminance = value["cutoffIlluminanceLux"]
            .AsFloat(DefaultCutoffIlluminanceLux);
        float chromaticityX = value["chromaticityX"].AsFloat(0.0f);
        float chromaticityY = value["chromaticityY"].AsFloat(0.0f);
        if (basis.Length == 0
            || !float.IsFinite(referenceCandela) || referenceCandela <= 0.0f
            || !float.IsFinite(sourceHalfWidth) || sourceHalfWidth <= 0.0f || sourceHalfWidth > 0.5f
            || !float.IsFinite(sourceHalfHeight) || sourceHalfHeight <= 0.0f || sourceHalfHeight > 0.5f
            || !float.IsFinite(referenceLightValue) || referenceLightValue <= 0.0f
            || !float.IsFinite(cutoffIlluminance) || cutoffIlluminance <= 0.0f
            || !float.IsFinite(chromaticityX) || !float.IsFinite(chromaticityY)
            || chromaticityX < 0.0f || chromaticityY < 0.0f
            || (chromaticityX > 0.0f || chromaticityY > 0.0f)
                && (chromaticityX <= 0.0f
                    || chromaticityY <= 0.0f
                    || chromaticityX + chromaticityY >= 1.0f))
        {
            return false;
        }

        float lightScale = Math.Max(actualLightValue, 0) / referenceLightValue;
        photometry = new EmitterPhotometry(
            basis,
            referenceCandela * lightScale,
            sourceHalfWidth,
            sourceHalfHeight,
            cutoffIlluminance,
            chromaticityX,
            chromaticityY);
        return photometry.LuminousIntensityCandela > 0.0f;
    }

    /// <summary>
    /// Constructs a physically attenuated fallback from the engine's visible range when a mod has
    /// not authored SI metadata. The range determines candela at the common lux cutoff; it never
    /// changes the inverse-square law used by the shader.
    /// </summary>
    /// <param name="blockCode">Canonical block code used only to estimate finite flame dimensions.</param>
    /// <param name="lightValue">Authored light-HSV value.</param>
    /// <returns>Fallback photometry with explicit non-measured provenance.</returns>
    public static EmitterPhotometry FromGameLight(string blockCode, int lightValue)
    {
        float nominalRange = Math.Clamp(lightValue * 0.665f, 4.0f, 24.0f);
        float candela = CandelaAtCutoffRange(
            nominalRange,
            DefaultCutoffIlluminanceLux);
        (float halfWidth, float halfHeight) = FallbackSourceHalfSize(blockCode);
        return new EmitterPhotometry(
            "game-range-calibrated-inverse-square",
            candela,
            halfWidth,
            halfHeight,
            DefaultCutoffIlluminanceLux,
            0.0f,
            0.0f);
    }

    /// <summary>Calculates point-source illuminance using the CIE inverse-square and cosine law.</summary>
    /// <param name="candela">Luminous intensity in candela.</param>
    /// <param name="distanceMetres">Source-to-receiver distance in metres.</param>
    /// <param name="receiverCosine">Cosine between receiver normal and source direction.</param>
    /// <param name="sourceRadiusMetres">Finite-source radius used only to avoid a near-field singularity.</param>
    /// <returns>Illuminance in lux.</returns>
    public static float PointIlluminanceLux(
        float candela,
        float distanceMetres,
        float receiverCosine,
        float sourceRadiusMetres)
    {
        float minimumDistance = Math.Max(sourceRadiusMetres, 0.0005f);
        float distanceSquared = Math.Max(
            distanceMetres * distanceMetres,
            minimumDistance * minimumDistance);
        return Math.Max(candela, 0.0f)
            * Math.Clamp(receiverCosine, 0.0f, 1.0f)
            / distanceSquared;
    }

    /// <summary>Derives candela from a finite range and the illuminance retained at that range.</summary>
    /// <param name="rangeMetres">Axial range in metres.</param>
    /// <param name="cutoffIlluminanceLux">Retained illuminance at the range boundary.</param>
    /// <returns>Luminous intensity in candela.</returns>
    public static float CandelaAtCutoffRange(float rangeMetres, float cutoffIlluminanceLux)
    {
        return Math.Max(cutoffIlluminanceLux, 0.0f)
            * Math.Max(rangeMetres, 0.0f)
            * Math.Max(rangeMetres, 0.0f);
    }

    /// <summary>Converts the profile's CIE xy chromaticity into normalized display-sRGB.</summary>
    /// <param name="red">Normalized sRGB red.</param>
    /// <param name="green">Normalized sRGB green.</param>
    /// <param name="blue">Normalized sRGB blue.</param>
    /// <returns>Whether a valid authored chromaticity was converted.</returns>
    public bool TryGetSrgb(out float red, out float green, out float blue)
    {
        red = green = blue = 0.0f;
        if (!HasChromaticity)
        {
            return false;
        }

        float xValue = ChromaticityX / ChromaticityY;
        float zValue = (1.0f - ChromaticityX - ChromaticityY) / ChromaticityY;
        float linearRed = Math.Max(3.2406f * xValue - 1.5372f - 0.4986f * zValue, 0.0f);
        float linearGreen = Math.Max(-0.9689f * xValue + 1.8758f + 0.0415f * zValue, 0.0f);
        float linearBlue = Math.Max(0.0557f * xValue - 0.2040f + 1.0570f * zValue, 0.0f);
        float maximum = Math.Max(linearRed, Math.Max(linearGreen, linearBlue));
        if (maximum <= 0.000001f || !float.IsFinite(maximum))
        {
            return false;
        }

        red = LinearToSrgb(linearRed / maximum);
        green = LinearToSrgb(linearGreen / maximum);
        blue = LinearToSrgb(linearBlue / maximum);
        return true;
    }

    /// <summary>Returns a finite-source estimate for unprofiled flame families.</summary>
    /// <param name="blockCode">Canonical block code.</param>
    /// <returns>Horizontal and vertical half-size in metres.</returns>
    private static (float HalfWidth, float HalfHeight) FallbackSourceHalfSize(string blockCode)
    {
        if (blockCode.Contains("firepit", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("forge", StringComparison.OrdinalIgnoreCase)
            || blockCode.Contains("bloomery", StringComparison.OrdinalIgnoreCase))
        {
            return (0.18f, 0.18f);
        }
        if (blockCode.Contains("torch", StringComparison.OrdinalIgnoreCase))
        {
            return (0.035f, 0.09f);
        }
        if (blockCode.Contains("candle", StringComparison.OrdinalIgnoreCase))
        {
            return (0.006f, 0.025f);
        }

        return (0.025f, 0.025f);
    }

    /// <summary>Encodes one normalized linear-light channel as display sRGB.</summary>
    /// <param name="value">Normalized linear channel.</param>
    /// <returns>sRGB channel.</returns>
    private static float LinearToSrgb(float value)
    {
        float clamped = Math.Clamp(value, 0.0f, 1.0f);
        return clamped <= 0.0031308f
            ? clamped * 12.92f
            : 1.055f * MathF.Pow(clamped, 1.0f / 2.4f) - 0.055f;
    }
}
