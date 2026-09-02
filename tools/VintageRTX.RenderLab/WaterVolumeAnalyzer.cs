using System.Numerics;

namespace VintageRTX.RenderLab;

/// <summary>
/// Carries liquid Band Metric measurements between the renderer and its assertions; property units follow their tested API contracts.
/// </summary>
internal sealed record LiquidBandMetric(
    int PixelCount,
    double MeanVerticalDepth,
    double MeanOpticalPathLength,
    double MeanRedTransmittance,
    double MeanGreenTransmittance,
    double MeanBlueTransmittance,
    double MeanBroadbandTransmittance,
    double MeanEmissionLinearLuminance,
    double MeanOpaqueLinearLuminance,
    double MeanSourceLinearLuminance,
    double MeanFinalLinearLuminance,
    double SourceToOpaqueLuminanceRatio,
    double FinalToOpaqueLuminanceRatio);

/// <summary>
/// Carries water Volume Report measurements between the renderer and its assertions; property units follow their tested API contracts.
/// </summary>
internal sealed record WaterVolumeReport(
    IReadOnlyDictionary<string, LiquidBandMetric> Liquids,
    double WaterContactLightPreservation,
    double WaterDepthTransmissionRatio,
    double WaterDeepBlueOverRedTransmission,
    double HoneyToWaterContactTransmissionRatio,
    double HoneyDeepRedOverBlueTransmission,
    double LavaDepthTransmissionRatio,
    double LavaDeepEmissionLinearLuminance,
    double LavaEmissionToOpaqueRatio,
    int ConfinedCandidatePixels,
    int ConfinedLiquidPixels,
    int OverflowPixels,
    double OverflowRatio);

/// <summary>
/// Supports water Volume Analysis within the deterministic VintageRTX test infrastructure.
/// </summary>
internal sealed record WaterVolumeAnalysis(
    WaterVolumeReport Report,
    byte[] DiagnosticPixels);

/// <summary>
/// Validates the transparent-source/opaque-G-buffer contract independently of
/// the display shader. Optical coefficients are measured from the authored
/// volume payload, while source/final luminance proves that the same pixels
/// survive raster composition and the production display pass.
/// </summary>
internal static class WaterVolumeAnalyzer
{
    /// <summary>
    /// Supports accumulator within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class Accumulator
    {
        /// <summary>
        /// Exposes the count state recorded by the test double for subsequent assertions.
        /// </summary>
        public int Count;
        /// <summary>
        /// Exposes the depth state recorded by the test double for subsequent assertions.
        /// </summary>
        public double Depth;
        /// <summary>
        /// Exposes the path state recorded by the test double for subsequent assertions.
        /// </summary>
        public double Path;
        /// <summary>
        /// Exposes the transmittance state recorded by the test double for subsequent assertions.
        /// </summary>
        public Vector3 Transmittance;
        /// <summary>
        /// Exposes the emission state recorded by the test double for subsequent assertions.
        /// </summary>
        public Vector3 Emission;
        /// <summary>
        /// Exposes the opaque Luminance state recorded by the test double for subsequent assertions.
        /// </summary>
        public double OpaqueLuminance;
        /// <summary>
        /// Exposes the source Luminance state recorded by the test double for subsequent assertions.
        /// </summary>
        public double SourceLuminance;
        /// <summary>
        /// Exposes the final Luminance state recorded by the test double for subsequent assertions.
        /// </summary>
        public double FinalLuminance;
    }

    /// <summary>
    /// Executes the analyze step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="scene">The scene input used to configure this deterministic test path.</param>
    /// <param name="finalPixels">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The analyze result consumed by the caller&apos;s assertion.</returns>
    public static WaterVolumeAnalysis Analyze(SyntheticScene scene, byte[] finalPixels)
    {
        int pixelCount = checked(scene.Width * scene.Height);
        int rgbaLength = checked(pixelCount * 4);
        if (scene.SourceColor.Length != rgbaLength
            || scene.OpaqueReferenceColor.Length != rgbaLength
            || finalPixels.Length != rgbaLength
            || scene.LiquidKind.Length != pixelCount
            || scene.LiquidDepth.Length != pixelCount
            || scene.LiquidPathLength.Length != pixelCount
            || scene.LiquidTransmittance.Length != pixelCount * 3
            || scene.LiquidEmission.Length != pixelCount * 3
            || scene.ConfinedLiquidCandidate.Length != pixelCount)
        {
            throw new InvalidDataException("Standalone liquid buffers do not share one extent.");
        }

        Dictionary<LabLiquid, Accumulator> accumulators = [];
        byte[] diagnostic = new byte[rgbaLength];
        int confinedCandidates = 0;
        int confinedPixels = 0;
        int overflowPixels = 0;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int rgba = pixel * 4;
            diagnostic[rgba + 3] = 255;
            bool candidate = scene.ConfinedLiquidCandidate[pixel] != 0;
            if (candidate)
            {
                confinedCandidates++;
                diagnostic[rgba] = 38;
                diagnostic[rgba + 1] = 38;
                diagnostic[rgba + 2] = 38;
            }

            LabLiquid liquid = (LabLiquid)scene.LiquidKind[pixel];
            if (liquid == LabLiquid.None)
                continue;

            if (!accumulators.TryGetValue(liquid, out Accumulator? accumulator))
            {
                accumulator = new Accumulator();
                accumulators.Add(liquid, accumulator);
            }

            int optical = pixel * 3;
            Vector3 transmission = new(
                scene.LiquidTransmittance[optical],
                scene.LiquidTransmittance[optical + 1],
                scene.LiquidTransmittance[optical + 2]);
            Vector3 emission = new(
                scene.LiquidEmission[optical],
                scene.LiquidEmission[optical + 1],
                scene.LiquidEmission[optical + 2]);
            accumulator.Count++;
            accumulator.Depth += scene.LiquidDepth[pixel];
            accumulator.Path += scene.LiquidPathLength[pixel];
            accumulator.Transmittance += transmission;
            accumulator.Emission += emission;
            accumulator.OpaqueLuminance += LinearLuminance(scene.OpaqueReferenceColor, rgba);
            accumulator.SourceLuminance += LinearLuminance(scene.SourceColor, rgba);
            accumulator.FinalLuminance += LinearLuminance(finalPixels, rgba);

            (byte red, byte green, byte blue) = DiagnosticColor(liquid);
            float pathModulation = 0.62f + 0.38f * Math.Clamp(scene.LiquidPathLength[pixel] / 3.0f, 0.0f, 1.0f);
            diagnostic[rgba] = (byte)Math.Clamp((int)MathF.Round(red * pathModulation), 0, 255);
            diagnostic[rgba + 1] = (byte)Math.Clamp((int)MathF.Round(green * pathModulation), 0, 255);
            diagnostic[rgba + 2] = (byte)Math.Clamp((int)MathF.Round(blue * pathModulation), 0, 255);

            if (liquid == LabLiquid.ConfinedWater)
            {
                confinedPixels++;
                if (!candidate)
                {
                    overflowPixels++;
                    diagnostic[rgba] = 255;
                    diagnostic[rgba + 1] = 0;
                    diagnostic[rgba + 2] = 0;
                }
            }
        }

        Dictionary<string, LiquidBandMetric> metrics = new(StringComparer.OrdinalIgnoreCase);
        foreach ((LabLiquid kind, Accumulator accumulator) in accumulators.OrderBy(pair => pair.Key))
        {
            double inverse = 1.0 / accumulator.Count;
            Vector3 transmission = accumulator.Transmittance * (float)inverse;
            Vector3 emission = accumulator.Emission * (float)inverse;
            double opaque = accumulator.OpaqueLuminance * inverse;
            double source = accumulator.SourceLuminance * inverse;
            double final = accumulator.FinalLuminance * inverse;
            metrics[Name(kind)] = new LiquidBandMetric(
                accumulator.Count,
                accumulator.Depth * inverse,
                accumulator.Path * inverse,
                transmission.X,
                transmission.Y,
                transmission.Z,
                Luminance(transmission),
                Luminance(emission),
                opaque,
                source,
                final,
                source / Math.Max(opaque, 0.001),
                final / Math.Max(opaque, 0.001));
        }

        LiquidBandMetric waterShallow = Required(metrics, "water-shallow");
        LiquidBandMetric waterDeep = Required(metrics, "water-deep");
        LiquidBandMetric honeyShallow = Required(metrics, "honey-shallow");
        LiquidBandMetric honeyDeep = Required(metrics, "honey-deep");
        LiquidBandMetric lavaShallow = Required(metrics, "lava-shallow");
        LiquidBandMetric lavaDeep = Required(metrics, "lava-deep");
        _ = Required(metrics, "confined-water");

        WaterVolumeReport report = new(
            metrics,
            waterShallow.SourceToOpaqueLuminanceRatio,
            waterDeep.MeanBroadbandTransmittance / waterShallow.MeanBroadbandTransmittance,
            waterDeep.MeanBlueTransmittance / Math.Max(waterDeep.MeanRedTransmittance, 0.001),
            honeyShallow.MeanBroadbandTransmittance / waterShallow.MeanBroadbandTransmittance,
            honeyDeep.MeanRedTransmittance / Math.Max(honeyDeep.MeanBlueTransmittance, 0.001),
            lavaDeep.MeanBroadbandTransmittance / lavaShallow.MeanBroadbandTransmittance,
            lavaDeep.MeanEmissionLinearLuminance,
            lavaDeep.MeanEmissionLinearLuminance
                / Math.Max(lavaDeep.MeanOpaqueLinearLuminance, 0.001),
            confinedCandidates,
            confinedPixels,
            overflowPixels,
            overflowPixels / (double)Math.Max(confinedPixels, 1));
        Validate(report);
        return new WaterVolumeAnalysis(report, diagnostic);
    }

    /// <summary>
    /// Executes the print step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="report">The report input used to configure this deterministic test path.</param>
    public static void Print(WaterVolumeReport report)
    {
        foreach ((string name, LiquidBandMetric metric) in report.Liquids)
        {
            Console.WriteLine(
                $"Liquid {name}: pixels={metric.PixelCount}, depth/path={metric.MeanVerticalDepth:0.00}/"
                + $"{metric.MeanOpticalPathLength:0.00}, T={metric.MeanRedTransmittance:0.000}/"
                + $"{metric.MeanGreenTransmittance:0.000}/{metric.MeanBlueTransmittance:0.000}, "
                + $"emission={metric.MeanEmissionLinearLuminance:0.000}, "
                + $"source/final vs opaque={metric.SourceToOpaqueLuminanceRatio:P0}/"
                + $"{metric.FinalToOpaqueLuminanceRatio:P0}.");
        }
        Console.WriteLine(
            $"Liquid contracts: water contact={report.WaterContactLightPreservation:P0}, "
            + $"water deep/shallow T={report.WaterDepthTransmissionRatio:P0}, "
            + $"honey/water contact T={report.HoneyToWaterContactTransmissionRatio:P0}, "
            + $"lava emission={report.LavaDeepEmissionLinearLuminance:0.000}, "
            + $"confined overflow={report.OverflowPixels}/{report.ConfinedLiquidPixels}.");
    }

    /// <summary>
    /// Validates requested fixture operation and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="report">The report input used to configure this deterministic test path.</param>
    private static void Validate(WaterVolumeReport report)
    {
        foreach ((string name, LiquidBandMetric metric) in report.Liquids)
        {
            if (metric.PixelCount < 8)
                throw new InvalidDataException($"Standalone liquid band '{name}' is not measurably visible.");
        }
        if (report.WaterContactLightPreservation < 0.55)
            throw new InvalidDataException("Shallow water no longer conserves enough receiver light at contact.");
        if (report.WaterDepthTransmissionRatio >= 0.78)
            throw new InvalidDataException("Water luminance/transmission no longer decays measurably with depth.");
        if (report.WaterDeepBlueOverRedTransmission < 1.65)
            throw new InvalidDataException("Deep water lost its blue-biased spectral attenuation.");
        if (report.HoneyToWaterContactTransmissionRatio >= 0.90)
            throw new InvalidDataException("Honey is no longer more absorbing than water at comparable depth.");
        if (report.HoneyDeepRedOverBlueTransmission < 3.0)
            throw new InvalidDataException("Honey lost its amber chromatic separation.");
        if (report.LavaDepthTransmissionRatio >= 0.50)
            throw new InvalidDataException("Lava is no longer strongly absorbing with depth.");
        if (report.LavaDeepEmissionLinearLuminance < 0.35
            || report.LavaEmissionToOpaqueRatio < 1.2)
            throw new InvalidDataException(
                $"Lava no longer remains a measurable emissive volume "
                + $"(emission={report.LavaDeepEmissionLinearLuminance:0.000}, "
                + $"emission/opaque={report.LavaEmissionToOpaqueRatio:0.000}).");
        if (report.ConfinedCandidatePixels < report.ConfinedLiquidPixels
            || report.ConfinedLiquidPixels < 8)
            throw new InvalidDataException("The confined bucket/barrel liquid fixture is not measurable.");
        if (report.OverflowPixels != 0)
            throw new InvalidDataException(
                $"Confined liquid overflowed its test envelope ({report.OverflowPixels} pixels).");
    }

    /// <summary>
    /// Executes the diagnostic Color step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="liquid">The liquid input used to configure this deterministic test path.</param>
    /// <returns>The diagnostic Color result consumed by the caller&apos;s assertion.</returns>
    private static (byte Red, byte Green, byte Blue) DiagnosticColor(LabLiquid liquid) => liquid switch
    {
        LabLiquid.WaterShallow => (52, 179, 238),
        LabLiquid.WaterDeep => (25, 72, 204),
        LabLiquid.HoneyShallow => (236, 172, 38),
        LabLiquid.HoneyDeep => (154, 72, 12),
        LabLiquid.LavaShallow => (255, 102, 18),
        LabLiquid.LavaDeep => (220, 24, 6),
        LabLiquid.ConfinedWater => (50, 238, 204),
        _ => (0, 0, 0)
    };

    /// <summary>
    /// Executes the name step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="liquid">The liquid input used to configure this deterministic test path.</param>
    /// <returns>The name result consumed by the caller&apos;s assertion.</returns>
    private static string Name(LabLiquid liquid) => liquid switch
    {
        LabLiquid.WaterShallow => "water-shallow",
        LabLiquid.WaterDeep => "water-deep",
        LabLiquid.HoneyShallow => "honey-shallow",
        LabLiquid.HoneyDeep => "honey-deep",
        LabLiquid.LavaShallow => "lava-shallow",
        LabLiquid.LavaDeep => "lava-deep",
        LabLiquid.ConfinedWater => "confined-water",
        _ => throw new ArgumentOutOfRangeException(nameof(liquid), liquid, null)
    };

    /// <summary>
    /// Executes the required step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="metrics">The metrics input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The required result consumed by the caller&apos;s assertion.</returns>
    private static LiquidBandMetric Required(
        IReadOnlyDictionary<string, LiquidBandMetric> metrics,
        string name) => metrics.TryGetValue(name, out LiquidBandMetric? metric)
            ? metric
            : throw new InvalidDataException($"Standalone liquid band '{name}' is absent.");

    /// <summary>
    /// Executes the linear Luminance step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="rgba">The rgba input used to configure this deterministic test path.</param>
    /// <param name="offset">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <returns>The linear Luminance result consumed by the caller&apos;s assertion.</returns>
    private static double LinearLuminance(byte[] rgba, int offset) =>
        SrgbToLinear(rgba[offset] / 255.0) * 0.2126
        + SrgbToLinear(rgba[offset + 1] / 255.0) * 0.7152
        + SrgbToLinear(rgba[offset + 2] / 255.0) * 0.0722;

    /// <summary>
    /// Executes the srgb To Linear step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The srgb To Linear result consumed by the caller&apos;s assertion.</returns>
    private static double SrgbToLinear(double value) => value <= 0.04045
        ? value / 12.92
        : Math.Pow((value + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Executes the luminance step used by the deterministic water Volume Analyzer fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The luminance result consumed by the caller&apos;s assertion.</returns>
    private static double Luminance(Vector3 value) =>
        value.X * 0.2126 + value.Y * 0.7152 + value.Z * 0.0722;
}
