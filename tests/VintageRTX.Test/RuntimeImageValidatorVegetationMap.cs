using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>Validates projected stock-plant shadows in the copied foggy-village save.</summary>
internal static partial class RuntimeImageValidator
{
    /// <summary>Exact aggregate proof emitted after all five replicated blocks are inspected.</summary>
    private const string VegetationMapGeometryToken =
        "Vegetation map geometry verified: PASS | count=5, alpha-cutout=5, crossed-planes=3, json-shapes=2";

    /// <summary>Measured ownership and support of the source-separated solar-shadow diagnostic.</summary>
    /// <param name="Compatible">Whether the source image can be evaluated.</param>
    /// <param name="VoxelOcclusionRatio">ROI fraction carrying local or complete voxel occlusion.</param>
    /// <param name="NativeOcclusionRatio">ROI fraction carrying native shadow-map occlusion.</param>
    /// <param name="CascadeSupportRatio">ROI fraction covered by a usable native cascade.</param>
    /// <param name="SupportedNativeRatio">Native-occluded samples backed by cascade support.</param>
    /// <param name="DifferentSourceRatio">ROI fraction where voxel and native occlusion differ materially.</param>
    /// <param name="NativeMorphology">Alpha-cut morphology measured exclusively from the green channel.</param>
    /// <param name="MeetsGate">Whether all source ownership and plant-shadow requirements pass.</param>
    internal readonly record struct NativeSunShadowAssessment(
        bool Compatible,
        double VoxelOcclusionRatio,
        double NativeOcclusionRatio,
        double CascadeSupportRatio,
        double SupportedNativeRatio,
        double DifferentSourceRatio,
        VegetationShadowAssessment NativeMorphology,
        bool MeetsGate);

    /// <summary>Determines whether the copied-map vegetation image contract applies.</summary>
    /// <param name="log">Merged client/server runtime log.</param>
    /// <returns>Whether the dedicated camera and real plant witness were both established.</returns>
    internal static bool ShouldValidateRealMapVegetationSunShadow(string log) =>
        log.Contains("Vegetation map camera applied:", StringComparison.OrdinalIgnoreCase)
        && log.Contains(VegetationMapGeometryToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Requires native solar detail and a non-rectangular localized green-channel shadow within the
    /// broad central receiver framed by the locked oblique camera.
    /// </summary>
    /// <param name="log">Merged log containing exact placement evidence and the shadow PNG path.</param>
    /// <returns>Actionable failures, or an empty list when the real-map plant shadow passes.</returns>
    internal static IReadOnlyList<string> ValidateRealMapVegetationSunShadow(string log)
    {
        List<string> failures = [];
        if (!log.Contains(NativeSolarShadowLogToken, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"missing log token: {NativeSolarShadowLogToken}");
        }
        if (!log.Contains(VegetationMapGeometryToken, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("real-map vegetation alpha-cut geometry evidence is missing");
        }

        System.Text.RegularExpressions.Match match = ShadowMaskRegex()
            .Matches(log)
            .Cast<System.Text.RegularExpressions.Match>()
            .LastOrDefault()
            ?? System.Text.RegularExpressions.Match.Empty;
        if (!match.Success)
        {
            failures.Add("real-map vegetation voxel-shadow capture was not logged");
            return failures;
        }

        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? shadow = File.Exists(path)
            ? SKBitmap.Decode(File.ReadAllBytes(path))
            : null;
        if (shadow is null)
        {
            failures.Add($"real-map vegetation voxel-shadow PNG could not be decoded: '{path}'");
            return failures;
        }

        VegetationShadowAssessment assessment = MeasureRealMapVegetationSunShadow(shadow);
        Console.WriteLine(
            $"Real-map vegetation solar shadow: pixels={assessment.CandidatePixels}, "
            + $"bounds={assessment.BoundingWidth}x{assessment.BoundingHeight}, "
            + $"ROI={assessment.RoiCoverage:P2}, fill={assessment.RectangularFill:P1}, "
            + $"gaps={assessment.InteriorGapRatio:P1}, "
            + $"row variation={assessment.RowOccupancyVariation:0.00}.");
        if (!assessment.MeetsGate)
        {
            failures.Add(
                "real-map vegetation shadow is absent, global, or a solid block fallback "
                + $"(pixels={assessment.CandidatePixels}, ROI={assessment.RoiCoverage:P2}, "
                + $"fill={assessment.RectangularFill:P1}, gaps={assessment.InteriorGapRatio:P1}, "
                + $"row variation={assessment.RowOccupancyVariation:0.00})");
        }

        System.Text.RegularExpressions.Match provenanceMatch = NativeSunShadowMaskRegex()
            .Matches(log)
            .Cast<System.Text.RegularExpressions.Match>()
            .LastOrDefault()
            ?? System.Text.RegularExpressions.Match.Empty;
        if (!provenanceMatch.Success)
        {
            failures.Add("real-map native sun-shadow provenance capture was not logged");
            return failures;
        }

        string provenancePath = provenanceMatch.Groups[1].Value
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? provenance = File.Exists(provenancePath)
            ? SKBitmap.Decode(File.ReadAllBytes(provenancePath))
            : null;
        if (provenance is null)
        {
            failures.Add(
                $"real-map native sun-shadow provenance PNG could not be decoded: '{provenancePath}'");
            return failures;
        }

        NativeSunShadowAssessment sourceAssessment = MeasureNativeSunShadowProvenance(provenance);
        Console.WriteLine(
            $"Native sun-shadow provenance: voxel={sourceAssessment.VoxelOcclusionRatio:P2}, "
            + $"native={sourceAssessment.NativeOcclusionRatio:P2}, "
            + $"support={sourceAssessment.CascadeSupportRatio:P1}, "
            + $"supported native={sourceAssessment.SupportedNativeRatio:P1}, "
            + $"source difference={sourceAssessment.DifferentSourceRatio:P2}, "
            + $"native fill={sourceAssessment.NativeMorphology.RectangularFill:P1}.");
        if (!sourceAssessment.MeetsGate)
        {
            failures.Add(
                "native sun-shadow provenance does not independently expose voxel occlusion, "
                + "native alpha-cut occlusion, and cascade support "
                + $"(voxel={sourceAssessment.VoxelOcclusionRatio:P2}, "
                + $"native={sourceAssessment.NativeOcclusionRatio:P2}, "
                + $"support={sourceAssessment.CascadeSupportRatio:P1}, "
                + $"supported native={sourceAssessment.SupportedNativeRatio:P1}, "
                + $"difference={sourceAssessment.DifferentSourceRatio:P2})");
        }

        return failures;
    }

    /// <summary>
    /// Measures the central and lower receiver used by the real-map camera while retaining the
    /// same connected-component and alpha-cut thresholds as the deterministic RenderLab witness.
    /// </summary>
    /// <param name="shadow">Decoded shader shadow diagnostic.</param>
    /// <returns>Localized vegetation-shadow morphology.</returns>
    internal static VegetationShadowAssessment MeasureRealMapVegetationSunShadow(
        SKBitmap shadow) => MeasureVegetationSunShadowRegion(
            shadow,
            0.08,
            0.92,
            0.28,
            0.96);

    /// <summary>
    /// Measures the provenance ABI where red is voxel occlusion, green is native shadow-map
    /// occlusion, and blue is native cascade support. Native morphology is evaluated from green
    /// through the same real-map plant ROI used by the independent voxel-shadow artifact.
    /// </summary>
    /// <param name="provenance">Decoded native-sun-shadow diagnostic.</param>
    /// <returns>Channel ownership, support, source separation, and alpha-cut morphology evidence.</returns>
    internal static NativeSunShadowAssessment MeasureNativeSunShadowProvenance(
        SKBitmap provenance)
    {
        if (provenance.Width < 64 || provenance.Height < 64)
        {
            return default;
        }

        int minimumX = (int)Math.Floor(provenance.Width * 0.08);
        int maximumX = (int)Math.Ceiling(provenance.Width * 0.92);
        int minimumY = (int)Math.Floor(provenance.Height * 0.28);
        int maximumY = (int)Math.Ceiling(provenance.Height * 0.96);
        int samples = 0;
        int voxelOccluded = 0;
        int nativeOccluded = 0;
        int cascadeSupported = 0;
        int supportedNative = 0;
        int differentSources = 0;
        for (int y = minimumY; y < maximumY; y++)
        {
            for (int x = minimumX; x < maximumX; x++)
            {
                SKColor pixel = provenance.GetPixel(x, y);
                bool voxel = pixel.Red >= 12;
                bool native = pixel.Green >= 12;
                bool support = pixel.Blue >= 12;
                voxelOccluded += voxel ? 1 : 0;
                nativeOccluded += native ? 1 : 0;
                cascadeSupported += support ? 1 : 0;
                supportedNative += native && support ? 1 : 0;
                differentSources += Math.Abs(pixel.Red - pixel.Green) >= 12 ? 1 : 0;
                samples++;
            }
        }

        double voxelRatio = (double)voxelOccluded / samples;
        double nativeRatio = (double)nativeOccluded / samples;
        double supportRatio = (double)cascadeSupported / samples;
        double supportedNativeRatio = nativeOccluded > 0
            ? (double)supportedNative / nativeOccluded
            : 0.0;
        double differenceRatio = (double)differentSources / samples;
        VegetationShadowAssessment nativeMorphology =
            MeasureRealMapVegetationSunShadow(provenance);
        bool sourcesPresent = voxelRatio is >= 0.00005 and <= 0.98
            && nativeRatio is >= 0.00005 and <= 0.98;
        bool nativeSupported = supportRatio >= 0.005
            && supportedNativeRatio >= 0.50;
        bool independentlyEncoded = differenceRatio >= 0.00005;
        return new NativeSunShadowAssessment(
            true,
            voxelRatio,
            nativeRatio,
            supportRatio,
            supportedNativeRatio,
            differenceRatio,
            nativeMorphology,
            sourcesPresent
                && nativeSupported
                && independentlyEncoded
                && nativeMorphology.MeetsGate);
    }

    /// <summary>Matches the effect-side source-separated native solar-shadow PNG.</summary>
    /// <returns>The native sun-shadow provenance capture matcher.</returns>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"Comparison capture saved: .+?-native-sun-shadow-before\.png and (.+?-native-sun-shadow-vintagertx\.png)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NativeSunShadowMaskRegex();
}
