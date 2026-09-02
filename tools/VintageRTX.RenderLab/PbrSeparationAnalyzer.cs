using System.Numerics;

namespace VintageRTX.RenderLab;

/// <summary>
/// Carries pbr Class Metric measurements between the renderer and its assertions; property units follow their tested API contracts.
/// </summary>
internal sealed record PbrClassMetric(
    int PixelCount,
    double GeometryCoverage,
    double MeanRoughness,
    double MeanMetallic,
    double MeanEmissive,
    double MeanLocalNormalVariationDegrees,
    double MeanSourceLinearLuminance,
    double MeanFinalLinearLuminance,
    double FinalToSourceLuminanceRatio,
    string DiagnosticColor);

/// <summary>
/// Carries pbr Separation Report measurements between the renderer and its assertions; property units follow their tested API contracts.
/// </summary>
internal sealed record PbrSeparationReport(
    int GeometryPixels,
    double ChangedGeometryFraction,
    double RoughnessSeparation,
    double MetallicSeparation,
    double FinalResponseSpread,
    IReadOnlyDictionary<string, PbrClassMetric> Classes);

/// <summary>
/// Supports pbr Separation Analysis within the deterministic VintageRTX test infrastructure.
/// </summary>
internal sealed record PbrSeparationAnalysis(
    PbrSeparationReport Report,
    byte[] ResponsePixels,
    byte[] ClassPixels);

/// <summary>
/// Decodes the exact deferred PBR payload used by the production shader. The
/// standalone material debug view deliberately reserves blue for payload
/// presence, which is useful for transport validation but visually overwhelms
/// roughness, metal and emission. These diagnostics expose the authored
/// response without changing any production render path.
/// </summary>
internal static class PbrSeparationAnalyzer
{
    /// <summary>
    /// Identifies the material Class variants used to drive deterministic renderer assertions.
    /// </summary>
    private enum MaterialClass
    {
        /// <summary>
        /// Classifies the synthetic sample as rough Dielectric for material-separation assertions.
        /// </summary>
        RoughDielectric,
        /// <summary>
        /// Classifies the synthetic sample as polished Dielectric for material-separation assertions.
        /// </summary>
        PolishedDielectric,
        /// <summary>
        /// Classifies the synthetic sample as fluid for material-separation assertions.
        /// </summary>
        Fluid,
        /// <summary>
        /// Classifies the synthetic sample as anvil metal for object-specific energy assertions.
        /// </summary>
        AnvilMetal,
        /// <summary>
        /// Classifies the synthetic sample as lantern cage metal for object-specific energy assertions.
        /// </summary>
        LanternCageMetal,
        /// <summary>
        /// Classifies the synthetic sample as emissive for material-separation assertions.
        /// </summary>
        Emissive,
        /// <summary>
        /// Classifies the synthetic sample as vegetation for material-separation assertions.
        /// </summary>
        Vegetation
    }

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
        /// Exposes the roughness state recorded by the test double for subsequent assertions.
        /// </summary>
        public double Roughness;
        /// <summary>
        /// Exposes the metallic state recorded by the test double for subsequent assertions.
        /// </summary>
        public double Metallic;
        /// <summary>
        /// Exposes the emissive state recorded by the test double for subsequent assertions.
        /// </summary>
        public double Emissive;
        /// <summary>
        /// Exposes the normal Variation state recorded by the test double for subsequent assertions.
        /// </summary>
        public double NormalVariation;
        /// <summary>
        /// Exposes the normal Pairs state recorded by the test double for subsequent assertions.
        /// </summary>
        public int NormalPairs;
        /// <summary>
        /// Exposes the source Luminance state recorded by the test double for subsequent assertions.
        /// </summary>
        public double SourceLuminance;
        /// <summary>
        /// Exposes the final Luminance state recorded by the test double for subsequent assertions.
        /// </summary>
        public double FinalLuminance;
    }

    private static readonly IReadOnlyDictionary<MaterialClass, (byte R, byte G, byte B, string Hex)> ClassColors =
        new Dictionary<MaterialClass, (byte, byte, byte, string)>
        {
            [MaterialClass.RoughDielectric] = (184, 126, 72, "#B87E48"),
            [MaterialClass.PolishedDielectric] = (45, 205, 224, "#2DCDE0"),
            [MaterialClass.Fluid] = (42, 105, 238, "#2A69EE"),
            [MaterialClass.AnvilMetal] = (226, 66, 207, "#E242CF"),
            [MaterialClass.LanternCageMetal] = (139, 92, 246, "#8B5CF6"),
            [MaterialClass.Emissive] = (255, 188, 46, "#FFBC2E"),
            [MaterialClass.Vegetation] = (74, 191, 72, "#4ABF48")
        };

    /// <summary>
    /// Executes the analyze step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="scene">The scene input used to configure this deterministic test path.</param>
    /// <param name="finalPixels">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The analyze result consumed by the caller&apos;s assertion.</returns>
    public static PbrSeparationAnalysis Analyze(
        SyntheticScene scene,
        byte[] finalPixels)
    {
        int expectedLength = checked(scene.Width * scene.Height * 4);
        if (scene.SourceColor.Length != expectedLength
            || scene.NormalRoughness.Length != expectedLength
            || scene.Material.Length != expectedLength
            || scene.SurfaceIdentities.Length != scene.Width * scene.Height
            || finalPixels.Length != expectedLength)
        {
            throw new InvalidDataException("Standalone PBR buffers do not share one RGBA extent.");
        }

        byte[] response = new byte[expectedLength];
        byte[] classes = new byte[expectedLength];
        sbyte[] classIds = new sbyte[checked(scene.Width * scene.Height)];
        Array.Fill(classIds, (sbyte)-1);
        Dictionary<MaterialClass, Accumulator> accumulators = [];
        int geometryPixels = 0;
        int changedGeometryPixels = 0;
        for (int offset = 0; offset < expectedLength; offset += 4)
        {
            response[offset + 3] = 255;
            classes[offset + 3] = 255;
            Vector3 normal = new(
                scene.NormalRoughness[offset],
                scene.NormalRoughness[offset + 1],
                scene.NormalRoughness[offset + 2]);
            if (normal.LengthSquared() <= 0.01f)
            {
                continue;
            }

            int flags = scene.Material[offset + 2];
            if ((flags & 64) == 0)
            {
                continue;
            }

            geometryPixels++;
            int maximumDisplayDelta = Math.Max(
                Math.Abs(finalPixels[offset] - scene.SourceColor[offset]),
                Math.Max(
                    Math.Abs(finalPixels[offset + 1] - scene.SourceColor[offset + 1]),
                    Math.Abs(finalPixels[offset + 2] - scene.SourceColor[offset + 2])));
            if (maximumDisplayDelta >= 2)
            {
                changedGeometryPixels++;
            }
            float roughness = DecodeRoughness(scene.NormalRoughness[offset + 3]);
            float metallic = (flags & 7) / 7.0f;
            float emissive = ((flags >> 3) & 7) / 7.0f;
            bool vegetation = (flags & 128) != 0;
            bool liquid = scene.LiquidKind[offset / 4] != (byte)LabLiquid.None;
            MaterialClass materialClass = Classify(
                roughness,
                metallic,
                emissive,
                vegetation,
                liquid,
                scene.SurfaceIdentities[offset / 4]);
            classIds[offset / 4] = (sbyte)materialClass;
            if (!accumulators.TryGetValue(materialClass, out Accumulator? accumulator))
            {
                accumulator = new Accumulator();
                accumulators.Add(materialClass, accumulator);
            }

            accumulator.Count++;
            accumulator.Roughness += roughness;
            accumulator.Metallic += metallic;
            accumulator.Emissive += emissive;
            accumulator.SourceLuminance += LinearLuminance(scene.SourceColor, offset);
            accumulator.FinalLuminance += LinearLuminance(finalPixels, offset);

            response[offset] = ToByte(metallic);
            response[offset + 1] = ToByte(1.0f - roughness);
            response[offset + 2] = ToByte(emissive);
            (byte red, byte green, byte blue, _) = ClassColors[materialClass];
            float classModulation = 0.72f + (1.0f - roughness) * 0.28f;
            classes[offset] = (byte)Math.Clamp((int)MathF.Round(red * classModulation), 0, 255);
            classes[offset + 1] = (byte)Math.Clamp((int)MathF.Round(green * classModulation), 0, 255);
            classes[offset + 2] = (byte)Math.Clamp((int)MathF.Round(blue * classModulation), 0, 255);
        }

        if (geometryPixels == 0)
        {
            throw new InvalidDataException("Standalone PBR analysis found no authored geometry.");
        }

        AccumulateLocalNormalVariation(scene, classIds, accumulators);

        Dictionary<string, PbrClassMetric> metrics = new(StringComparer.OrdinalIgnoreCase);
        foreach ((MaterialClass materialClass, Accumulator accumulator) in accumulators.OrderBy(pair => pair.Key))
        {
            double inverseCount = 1.0 / accumulator.Count;
            metrics[ClassName(materialClass)] = new PbrClassMetric(
                accumulator.Count,
                accumulator.Count / (double)geometryPixels,
                accumulator.Roughness * inverseCount,
                accumulator.Metallic * inverseCount,
                accumulator.Emissive * inverseCount,
                accumulator.NormalPairs > 0
                    ? accumulator.NormalVariation / accumulator.NormalPairs
                    : 0.0,
                accumulator.SourceLuminance * inverseCount,
                accumulator.FinalLuminance * inverseCount,
                accumulator.FinalLuminance
                    / Math.Max(accumulator.SourceLuminance, accumulator.Count * 0.001),
                ClassColors[materialClass].Hex);
        }

        PbrClassMetric rough = Required(metrics, "rough-dielectric");
        PbrClassMetric polished = Required(metrics, "polished-dielectric");
        PbrClassMetric anvilMetal = Required(metrics, "anvil-metal");
        PbrClassMetric lanternCageMetal = Required(metrics, "lantern-cage-metal");
        double metallicReference = metrics
            .Where(pair => pair.Key is not "anvil-metal" and not "lantern-cage-metal")
            .Max(pair => pair.Value.MeanMetallic);
        double finalMinimum = metrics.Values.Min(metric => metric.MeanFinalLinearLuminance);
        double finalMaximum = metrics.Values.Max(metric => metric.MeanFinalLinearLuminance);
        PbrSeparationReport report = new(
            geometryPixels,
            changedGeometryPixels / (double)geometryPixels,
            rough.MeanRoughness - polished.MeanRoughness,
            Math.Min(anvilMetal.MeanMetallic, lanternCageMetal.MeanMetallic) - metallicReference,
            finalMaximum - finalMinimum,
            metrics);
        try
        {
            Validate(report);
        }
        catch (InvalidDataException)
        {
            // Keep the strict material gate, but expose every measured class
            // before propagating the failure. Otherwise the first failing
            // assertion hides the evidence needed to distinguish a malformed
            // fixture from a production shader transport regression.
            Print(report);
            throw;
        }
        return new PbrSeparationAnalysis(report, response, classes);
    }

    /// <summary>
    /// Executes the print step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="report">The report input used to configure this deterministic test path.</param>
    public static void Print(PbrSeparationReport report)
    {
        foreach ((string name, PbrClassMetric metric) in report.Classes)
        {
            Console.WriteLine(
                $"PBR {name}: coverage={metric.GeometryCoverage:P2}, roughness={metric.MeanRoughness:0.000}, "
                + $"metal={metric.MeanMetallic:0.000}, emission={metric.MeanEmissive:0.000}, "
                + $"local normal variation={metric.MeanLocalNormalVariationDegrees:0.000}deg, "
                + $"source/final luma={metric.MeanSourceLinearLuminance:0.000}/{metric.MeanFinalLinearLuminance:0.000} "
                + $"({metric.FinalToSourceLuminanceRatio:P0}).");
        }
        Console.WriteLine(
            $"PBR separation: changed geometry={report.ChangedGeometryFraction:P2}, "
            + $"roughness gap={report.RoughnessSeparation:0.000}, "
            + $"metallic gap={report.MetallicSeparation:0.000}, "
            + $"final response spread={report.FinalResponseSpread:0.000}.");
    }

    /// <summary>
    /// Validates requested fixture operation and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="report">The report input used to configure this deterministic test path.</param>
    private static void Validate(PbrSeparationReport report)
    {
        string[] required =
        [
            "rough-dielectric", "polished-dielectric", "fluid",
            "anvil-metal", "lantern-cage-metal", "emissive", "vegetation"
        ];
        foreach (string name in required)
        {
            PbrClassMetric metric = Required(report.Classes, name);
            if (metric.PixelCount < 8 || metric.GeometryCoverage < 0.00005)
            {
                throw new InvalidDataException($"Standalone PBR class '{name}' is not measurably visible.");
            }
        }
        if (report.ChangedGeometryFraction < 0.05)
        {
            throw new InvalidDataException(
                $"Standalone final transport bypassed authored geometry "
                + $"({report.ChangedGeometryFraction:P2} changed by at least two display levels).");
        }
        if (report.RoughnessSeparation < 0.35)
        {
            throw new InvalidDataException(
                $"Standalone rough/polished separation collapsed ({report.RoughnessSeparation:0.000}).");
        }
        if (report.MetallicSeparation < 0.65)
        {
            throw new InvalidDataException(
                $"Standalone metal/dielectric separation collapsed ({report.MetallicSeparation:0.000}).");
        }
        if (Required(report.Classes, "emissive").MeanEmissive < 0.85)
        {
            throw new InvalidDataException("Standalone emissive reference lost its authored response.");
        }
        PbrClassMetric rough = Required(report.Classes, "rough-dielectric");
        PbrClassMetric polished = Required(report.Classes, "polished-dielectric");
        if (rough.MeanLocalNormalVariationDegrees
            <= polished.MeanLocalNormalVariationDegrees + 0.005)
        {
            throw new InvalidDataException(
                "Standalone rough dielectric no longer carries more local normal detail than polished material.");
        }
        foreach (string metalName in new[] { "anvil-metal", "lantern-cage-metal" })
        {
            PbrClassMetric metal = Required(report.Classes, metalName);
            if (metal.MeanFinalLinearLuminance < 0.020
                || metal.FinalToSourceLuminanceRatio < 1.0)
            {
                throw new InvalidDataException(
                    $"Standalone {metalName} remains energy-starved "
                    + $"({metal.MeanFinalLinearLuminance:0.000}, {metal.FinalToSourceLuminanceRatio:P0} of source).");
            }
        }
        PbrClassMetric vegetation = Required(report.Classes, "vegetation");
        if (vegetation.MeanFinalLinearLuminance < 0.004
            || vegetation.FinalToSourceLuminanceRatio < 0.80)
        {
            throw new InvalidDataException(
                $"Standalone vegetation remains near-black "
                + $"({vegetation.MeanFinalLinearLuminance:0.000}, {vegetation.FinalToSourceLuminanceRatio:P0} of source).");
        }
        if (polished.FinalToSourceLuminanceRatio < 0.62)
        {
            throw new InvalidDataException(
                $"Standalone polished dielectric lost its readable material response "
                + $"({polished.FinalToSourceLuminanceRatio:P0} of source).");
        }
    }

    /// <summary>
    /// Executes the accumulate Local Normal Variation step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="scene">The scene input used to configure this deterministic test path.</param>
    /// <param name="classIds">The class Ids input used to configure this deterministic test path.</param>
    /// <param name="accumulators">The accumulators input used to configure this deterministic test path.</param>
    private static void AccumulateLocalNormalVariation(
        SyntheticScene scene,
        sbyte[] classIds,
        IReadOnlyDictionary<MaterialClass, Accumulator> accumulators)
    {
        for (int y = 0; y < scene.Height; y++)
        {
            for (int x = 0; x < scene.Width; x++)
            {
                int pixel = y * scene.Width + x;
                if (classIds[pixel] < 0)
                {
                    continue;
                }
                if (x + 1 < scene.Width)
                {
                    AccumulateNormalPair(scene, classIds, accumulators, pixel, pixel + 1);
                }
                if (y + 1 < scene.Height)
                {
                    AccumulateNormalPair(scene, classIds, accumulators, pixel, pixel + scene.Width);
                }
            }
        }
    }

    /// <summary>
    /// Executes the accumulate Normal Pair step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="scene">The scene input used to configure this deterministic test path.</param>
    /// <param name="classIds">The class Ids input used to configure this deterministic test path.</param>
    /// <param name="accumulators">The accumulators input used to configure this deterministic test path.</param>
    /// <param name="firstPixel">The first Pixel input used to configure this deterministic test path.</param>
    /// <param name="secondPixel">Duration supplied to the simulation, in seconds unless the tested API states otherwise.</param>
    private static void AccumulateNormalPair(
        SyntheticScene scene,
        sbyte[] classIds,
        IReadOnlyDictionary<MaterialClass, Accumulator> accumulators,
        int firstPixel,
        int secondPixel)
    {
        if (classIds[firstPixel] != classIds[secondPixel])
        {
            return;
        }
        Vector3 first = ReadNormal(scene.NormalRoughness, firstPixel * 4);
        Vector3 second = ReadNormal(scene.NormalRoughness, secondPixel * 4);
        float dot = Math.Clamp(Vector3.Dot(first, second), -1.0f, 1.0f);
        // A face boundary is geometry, not normal-map response. Restrict this
        // metric to coherent neighboring samples on the same local surface.
        if (dot < 0.94f)
        {
            return;
        }
        MaterialClass materialClass = (MaterialClass)classIds[firstPixel];
        Accumulator accumulator = accumulators[materialClass];
        accumulator.NormalVariation += Math.Acos(dot) * 180.0 / Math.PI;
        accumulator.NormalPairs++;
    }

    /// <summary>
    /// Reads normal from isolated test input and rejects malformed state at the fixture boundary.
    /// </summary>
    /// <param name="normalRoughness">The normal Roughness input used to configure this deterministic test path.</param>
    /// <param name="offset">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <returns>The read Normal result consumed by the caller&apos;s assertion.</returns>
    private static Vector3 ReadNormal(float[] normalRoughness, int offset) =>
        Vector3.Normalize(new Vector3(
            normalRoughness[offset],
            normalRoughness[offset + 1],
            normalRoughness[offset + 2]));

    /// <summary>
    /// Executes the classify step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="roughness">The roughness input used to configure this deterministic test path.</param>
    /// <param name="metallic">The metallic input used to configure this deterministic test path.</param>
    /// <param name="emissive">The emissive input used to configure this deterministic test path.</param>
    /// <param name="vegetation">The vegetation input used to configure this deterministic test path.</param>
    /// <param name="liquid">The liquid input used to configure this deterministic test path.</param>
    /// <param name="surfaceIdentity">Explicit authored object identity recorded by the synthetic tracer.</param>
    /// <returns>The classify result consumed by the caller&apos;s assertion.</returns>
    private static MaterialClass Classify(
        float roughness,
        float metallic,
        float emissive,
        bool vegetation,
        bool liquid,
        LabSurfaceIdentity surfaceIdentity)
    {
        // Transparent liquids deliberately retain the opaque receiver's PBR
        // payload in the G-buffer. Classify through the independent liquid mask
        // so the material report does not mislabel those receivers as masonry.
        if (liquid) return MaterialClass.Fluid;
        if (emissive >= 0.5f) return MaterialClass.Emissive;
        if (vegetation) return MaterialClass.Vegetation;
        if (metallic >= 0.5f)
        {
            return surfaceIdentity switch
            {
                LabSurfaceIdentity.AnvilMetal => MaterialClass.AnvilMetal,
                LabSurfaceIdentity.LanternCageMetal => MaterialClass.LanternCageMetal,
                _ => throw new InvalidDataException(
                    "Standalone metallic geometry is missing an explicit authored surface identity.")
            };
        }
        if (surfaceIdentity != LabSurfaceIdentity.None)
        {
            throw new InvalidDataException(
                $"Standalone surface identity '{surfaceIdentity}' does not carry a metallic payload.");
        }
        if (roughness <= 0.105f) return MaterialClass.Fluid;
        if (roughness < 0.45f) return MaterialClass.PolishedDielectric;
        return MaterialClass.RoughDielectric;
    }

    /// <summary>
    /// Executes the class Name step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="materialClass">The material Class input used to configure this deterministic test path.</param>
    /// <returns>The class Name result consumed by the caller&apos;s assertion.</returns>
    private static string ClassName(MaterialClass materialClass) => materialClass switch
    {
        MaterialClass.RoughDielectric => "rough-dielectric",
        MaterialClass.PolishedDielectric => "polished-dielectric",
        MaterialClass.Fluid => "fluid",
        MaterialClass.AnvilMetal => "anvil-metal",
        MaterialClass.LanternCageMetal => "lantern-cage-metal",
        MaterialClass.Emissive => "emissive",
        MaterialClass.Vegetation => "vegetation",
        _ => throw new ArgumentOutOfRangeException(nameof(materialClass), materialClass, null)
    };

    /// <summary>
    /// Executes the required step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="metrics">The metrics input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The required result consumed by the caller&apos;s assertion.</returns>
    private static PbrClassMetric Required(
        IReadOnlyDictionary<string, PbrClassMetric> metrics,
        string name) => metrics.TryGetValue(name, out PbrClassMetric? metric)
            ? metric
            : throw new InvalidDataException($"Standalone PBR class '{name}' is absent.");

    /// <summary>
    /// Executes the decode Roughness step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="packedSurfaceAlpha">The packed Surface Alpha input used to configure this deterministic test path.</param>
    /// <returns>The decode Roughness result consumed by the caller&apos;s assertion.</returns>
    private static float DecodeRoughness(float packedSurfaceAlpha)
    {
        int payload = Math.Clamp(
            (int)MathF.Floor(packedSurfaceAlpha * 1025.0f - 1.0f + 0.5f),
            0,
            1023);
        return Math.Clamp((payload / 32 + 0.5f) / 32.0f, 0.04f, 1.0f);
    }

    /// <summary>
    /// Executes the linear Luminance step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="rgba">The rgba input used to configure this deterministic test path.</param>
    /// <param name="offset">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <returns>The linear Luminance result consumed by the caller&apos;s assertion.</returns>
    private static double LinearLuminance(byte[] rgba, int offset) =>
        SrgbToLinear(rgba[offset] / 255.0)
            * 0.2126
        + SrgbToLinear(rgba[offset + 1] / 255.0)
            * 0.7152
        + SrgbToLinear(rgba[offset + 2] / 255.0)
            * 0.0722;

    /// <summary>
    /// Executes the srgb To Linear step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The srgb To Linear result consumed by the caller&apos;s assertion.</returns>
    private static double SrgbToLinear(double value) => value <= 0.04045
        ? value / 12.92
        : Math.Pow((value + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Executes the to Byte step used by the deterministic pbr Separation Analyzer fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The to Byte result consumed by the caller&apos;s assertion.</returns>
    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);
}
