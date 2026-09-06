using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>
/// Supports runtime Image Validator within the deterministic VintageRTX test infrastructure.
/// </summary>
internal static partial class RuntimeImageValidator
{
    /// <summary>One decoded normal plus the material/depth identity of its G-buffer pixel.</summary>
    /// <param name="Normal">Decoded world-space unit normal, or zero for invalid geometry.</param>
    /// <param name="MaterialRed">Red material-mask channel.</param>
    /// <param name="MaterialGreen">Green material-mask channel.</param>
    /// <param name="MaterialBlue">Blue authored-material coverage channel.</param>
    /// <param name="Depth">Quantized diagnostic depth.</param>
    internal readonly record struct PbrNormalNeighborhoodSample(
        Vector3 Normal,
        byte MaterialRed,
        byte MaterialGreen,
        byte MaterialBlue,
        byte Depth);

    /// <summary>Pure classification of one four-neighbor authored-normal cross.</summary>
    /// <param name="CountsTowardExcessiveDenominator">Whether all samples share one continuous authored receiver.</param>
    /// <param name="IsExcessive">Whether multiple same-receiver normal discontinuities exceed 35 degrees.</param>
    /// <param name="IsResponsive">Whether accepted local RMS reaches one degree.</param>
    /// <param name="AngularRmsDegrees">Root-mean-square center-to-neighbor angle.</param>
    internal readonly record struct PbrNormalNeighborhoodAssessment(
        bool CountsTowardExcessiveDenominator,
        bool IsExcessive,
        bool IsResponsive,
        double AngularRmsDegrees);

    /// <summary>Ground and solar-occlusion evidence for a targeted exterior frame.</summary>
    /// <param name="GroundCoverage">Fraction of the complete image occupied by upward receivers.</param>
    /// <param name="ShadowedGroundRatio">Fraction of every upward receiver carrying solar occlusion.</param>
    /// <param name="TargetGroundSamples">Upward-receiver samples inside the camera-centered target disc.</param>
    /// <param name="TargetShadowedGroundRatio">Solar occlusion among receivers inside that target disc.</param>
    internal readonly record struct SunReceiverCoverageAssessment(
        double GroundCoverage,
        double ShadowedGroundRatio,
        int TargetGroundSamples,
        double TargetShadowedGroundRatio);

    /// <summary>Worst pairwise variation across three fixed-camera frames of one scalar channel.</summary>
    /// <param name="Compatible">Whether all three images share non-zero dimensions.</param>
    /// <param name="MaximumMeanAbsoluteDelta">Largest pairwise mean absolute normalized-channel delta.</param>
    /// <param name="MaximumChangedPixelRatio">Largest pairwise fraction exceeding the requested change threshold.</param>
    /// <param name="StableSampleRatio">Fraction retained after optional same-time Vanilla motion rejection.</param>
    /// <param name="FirstMean">Mean retained scalar value in the first frame.</param>
    /// <param name="SecondMean">Mean retained scalar value in the second frame.</param>
    /// <param name="ThirdMean">Mean retained scalar value in the third frame.</param>
    /// <param name="FirstSecondMeanAbsoluteDelta">Mean absolute delta from the first to second frame.</param>
    /// <param name="SecondThirdMeanAbsoluteDelta">Mean absolute delta from the second to third frame.</param>
    /// <param name="FirstThirdMeanAbsoluteDelta">Mean absolute delta from the first to third frame.</param>
    internal readonly record struct TemporalTripletAssessment(
        bool Compatible,
        double MaximumMeanAbsoluteDelta,
        double MaximumChangedPixelRatio,
        double StableSampleRatio,
        double FirstMean,
        double SecondMean,
        double ThirdMean,
        double FirstSecondMeanAbsoluteDelta,
        double SecondThirdMeanAbsoluteDelta,
        double FirstThirdMeanAbsoluteDelta);

    /// <summary>Structural evidence for the two deterministic water-reflection witnesses.</summary>
    /// <param name="Compatible">Whether source and diagnostic dimensions can be compared.</param>
    /// <param name="HumanoidSourcePixels">Warm opaque/alpha-tested pixels in the authored humanoid source band.</param>
    /// <param name="ItemSourcePixels">Warm opaque pixels in the authored dropped-item source band.</param>
    /// <param name="HumanoidReflectionArea">Largest locally contrasted component below the humanoid.</param>
    /// <param name="ItemReflectionArea">Largest locally contrasted component in the deeper item band.</param>
    /// <param name="HumanoidReflectionCentroidY">Normalized vertical centroid of the upper reflected component.</param>
    /// <param name="ItemReflectionCentroidY">Normalized vertical centroid of the lower reflected component.</param>
    /// <param name="MeetsProvisionalGate">Whether the physically ordered, central components meet scale-aware provisional minima.</param>
    internal readonly record struct ReflectionWitnessAssessment(
        bool Compatible,
        int HumanoidSourcePixels,
        int ItemSourcePixels,
        int HumanoidReflectionArea,
        int ItemReflectionArea,
        double HumanoidReflectionCentroidY,
        double ItemReflectionCentroidY,
        bool MeetsProvisionalGate);

    /// <summary>Localized geometric response measured around one projected projectile contact.</summary>
    /// <param name="Compatible">Whether both field images and the projected anchor are comparable.</param>
    /// <param name="RadiusPixels">Scale-aware disc radius used around the exact contact.</param>
    /// <param name="LocalSamples">Valid surface-field pixels inside the contact disc.</param>
    /// <param name="BackgroundSamples">Valid pixels in the surrounding comparison annulus.</param>
    /// <param name="PeakHeightDeltaMetres">Largest encoded absolute height change inside the disc.</param>
    /// <param name="LocalSignalRmsMetres">RMS height/normal-equivalent change inside the disc.</param>
    /// <param name="BackgroundSignalRmsMetres">RMS change in the surrounding annulus.</param>
    /// <param name="LocalizationRatio">Local RMS divided by annulus RMS.</param>
    /// <param name="CoherentWaveRmsMetres">RMS of the signed high-frequency residual averaged by contact-centred rings.</param>
    /// <param name="ControlWaveRmsMetres">RMS ring coherence in four equally deep counterfactual discs.</param>
    /// <param name="WaveLocalizationRatio">Contact-centred coherence divided by counterfactual coherence.</param>
    /// <param name="MeetsGate">Whether the contact produced a localized, resolvable geometric change.</param>
    internal readonly record struct ProjectileSurfaceDeltaAssessment(
        bool Compatible,
        int RadiusPixels,
        int LocalSamples,
        int BackgroundSamples,
        double PeakHeightDeltaMetres,
        double LocalSignalRmsMetres,
        double BackgroundSignalRmsMetres,
        double LocalizationRatio,
        double CoherentWaveRmsMetres,
        double ControlWaveRmsMetres,
        double WaveLocalizationRatio,
        bool MeetsGate);

    /// <summary>Contact-centred ring coherence and same-depth counterfactual evidence.</summary>
    /// <param name="Compatible">Whether the contact and all four controls retain enough continuous liquid pixels.</param>
    /// <param name="LocalSamples">Continuous liquid samples contributing to accepted contact-centred radial bins.</param>
    /// <param name="ControlSamples">Continuous liquid samples contributing across the four controls.</param>
    /// <param name="LocalRmsMetres">RMS of signed radial-bin means at the contact.</param>
    /// <param name="ControlRmsMetres">RMS of the corresponding four counterfactual scores.</param>
    /// <param name="LocalizationRatio">Local coherence divided by counterfactual coherence.</param>
    private readonly record struct ProjectileWaveCoherenceAssessment(
        bool Compatible,
        int LocalSamples,
        int ControlSamples,
        double LocalRmsMetres,
        double ControlRmsMetres,
        double LocalizationRatio);

    /// <summary>Local differential projection from horizontal world coordinates to framebuffer pixels.</summary>
    /// <param name="Center">Projected centre in top-left framebuffer pixels.</param>
    /// <param name="ScreenPerWorldX">Pixel displacement produced by one world block along X.</param>
    /// <param name="ScreenPerWorldZ">Pixel displacement produced by one world block along Z.</param>
    /// <param name="SupportRadiusWorldBlocks">Exact compact impulse support used by the solver.</param>
    internal readonly record struct ProjectileSurfaceFrame(
        Vector2 Center,
        Vector2 ScreenPerWorldX,
        Vector2 ScreenPerWorldZ,
        double SupportRadiusWorldBlocks);

    /// <summary>
    /// Lagrange coefficients that predict the unforced response from three pre-impact fields.
    /// The measured response itself always retains coefficient one, so these weights cannot
    /// amplify a projectile impulse.
    /// </summary>
    /// <param name="Earlier">Coefficient of the oldest pre-impact field.</param>
    /// <param name="Prior">Coefficient of the middle pre-impact field.</param>
    /// <param name="Baseline">Coefficient of the latest pre-impact field.</param>
    internal readonly record struct TemporalCounterfactualWeights(
        double Earlier,
        double Prior,
        double Baseline);

    /// <summary>Server-authoritative gravity-capillary packet attached to one captured callback.</summary>
    /// <param name="PacketSequence">Packet sequence emitted by the impact solver.</param>
    /// <param name="AmplitudeMetres">Logged peak packet height before propagation damping.</param>
    /// <param name="WavelengthMetres">Logged physical carrier wavelength.</param>
    internal readonly record struct ProjectileWavePacket(
        int PacketSequence,
        double AmplitudeMetres,
        double WavelengthMetres);

    /// <summary>Detrended low-order wave content and raw energy in one equal-area world patch.</summary>
    /// <param name="Compatible">Whether the projected patch contains enough valid liquid samples.</param>
    /// <param name="Samples">Valid jointly encoded liquid pixels.</param>
    /// <param name="PeakHeightDeltaMetres">Largest signed-height magnitude in the patch.</param>
    /// <param name="RawRmsMetres">RMS height plus normal-equivalent change.</param>
    /// <param name="CoherentRmsMetres">Legacy fixed-bin radial/dipole reconstruction.</param>
    /// <param name="MatchedCarrierRmsMetres">Phase-invariant radial projection at the logged wave number.</param>
    /// <param name="MatchedCarrierCompatible">Whether the carrier projection has a stable two-phase basis.</param>
    /// <param name="NormalEnvelopeRmsMetres">Band-limited radial coherence of horizontal-normal magnitude.</param>
    /// <param name="NormalEnvelopeCompatible">Whether enough anti-aliased radial envelope bins are populated.</param>
    private readonly record struct WorldWavePatchAssessment(
        bool Compatible,
        int Samples,
        double PeakHeightDeltaMetres,
        double RawRmsMetres,
        double CoherentRmsMetres,
        double MatchedCarrierRmsMetres,
        bool MatchedCarrierCompatible,
        double NormalEnvelopeRmsMetres,
        bool NormalEnvelopeCompatible);

    /// <summary>
    /// Structural evidence that the entity-only mirror contains the local player's world body but
    /// not the large camera-space first-person arm/held-item overlay.
    /// </summary>
    /// <param name="Compatible">Whether the raw carrier is large enough for scale-aware analysis.</param>
    /// <param name="SignificantComponentCount">Number of non-black connected components above the noise floor.</param>
    /// <param name="WorldBodyArea">Area of the best near-camera upright world-body candidate.</param>
    /// <param name="WorldBodyCentroidX">Normalized horizontal centroid of that candidate.</param>
    /// <param name="WorldBodyCentroidY">Normalized vertical centroid of that candidate.</param>
    /// <param name="WorldBodyHeightRatio">Candidate height divided by the raw carrier height.</param>
    /// <param name="FirstPersonOverlayArea">Largest large component attached to the left camera-overlay band.</param>
    /// <param name="HasWorldBody">Whether a compact lower-frame world-body candidate was found.</param>
    /// <param name="HasFirstPersonOverlay">Whether a large left-edge arm/held-item component was found.</param>
    /// <param name="MeetsGate">Whether the body is present and the camera-space overlay is absent.</param>
    internal readonly record struct EntityMirrorLocalBodyAssessment(
        bool Compatible,
        int SignificantComponentCount,
        int WorldBodyArea,
        double WorldBodyCentroidX,
        double WorldBodyCentroidY,
        double WorldBodyHeightRatio,
        int FirstPersonOverlayArea,
        bool HasWorldBody,
        bool HasFirstPersonOverlay,
        bool MeetsGate);

    /// <summary>Scale-independent morphology for one significant entity-only carrier component.</summary>
    /// <param name="Area">Connected geometry pixels.</param>
    /// <param name="MinimumX">Inclusive left bound.</param>
    /// <param name="MinimumY">Inclusive top bound.</param>
    /// <param name="MaximumX">Inclusive right bound.</param>
    /// <param name="MaximumY">Inclusive bottom bound.</param>
    /// <param name="CentroidX">Normalized horizontal centroid.</param>
    /// <param name="CentroidY">Normalized vertical centroid.</param>
    /// <param name="FillRatio">Connected area divided by bounding-box area.</param>
    internal readonly record struct EntityMirrorComponentAssessment(
        int Area,
        int MinimumX,
        int MinimumY,
        int MaximumX,
        int MaximumY,
        double CentroidX,
        double CentroidY,
        double FillRatio)
    {
        /// <summary>Inclusive component width.</summary>
        internal int Width => MaximumX - MinimumX + 1;

        /// <summary>Inclusive component height.</summary>
        internal int Height => MaximumY - MinimumY + 1;
    }

    /// <summary>One eight-connected non-black component in the raw entity-only carrier.</summary>
    private readonly record struct EntityMirrorComponent(
        int Area,
        long SumX,
        long SumY,
        int MinimumX,
        int MinimumY,
        int MaximumX,
        int MaximumY)
    {
        /// <summary>Inclusive component width.</summary>
        internal int Width => MaximumX - MinimumX + 1;

        /// <summary>Inclusive component height.</summary>
        internal int Height => MaximumY - MinimumY + 1;
    }

    /// <summary>One connected reflection component measured on both sides of the witness-band split.</summary>
    /// <param name="Id">Stable flood-fill identifier inside the shared central corridor.</param>
    /// <param name="HumanoidArea">Pixels belonging to the upper humanoid-reflection band.</param>
    /// <param name="HumanoidSumY">Sum of absolute image rows contributing to <paramref name="HumanoidArea"/>.</param>
    /// <param name="ItemArea">Pixels belonging to the lower item-reflection band.</param>
    /// <param name="ItemSumY">Sum of absolute image rows contributing to <paramref name="ItemArea"/>.</param>
    private readonly record struct CentralReflectionComponent(
        int Id,
        int HumanoidArea,
        long HumanoidSumY,
        int ItemArea,
        long ItemSumY);

    /// <summary>Two band measurements and whether they originate from different connected components.</summary>
    /// <param name="HumanoidArea">Area selected in the upper humanoid-reflection band.</param>
    /// <param name="HumanoidCentroidY">Normalized vertical centroid of the selected upper area.</param>
    /// <param name="ItemArea">Area selected in the lower item-reflection band.</param>
    /// <param name="ItemCentroidY">Normalized vertical centroid of the selected lower area.</param>
    /// <param name="HasDistinctComponents">Whether the upper and lower measurements have different flood-fill identifiers.</param>
    private readonly record struct CentralReflectionPair(
        int HumanoidArea,
        double HumanoidCentroidY,
        int ItemArea,
        double ItemCentroidY,
        bool HasDistinctComponents);

    /// <summary>
    /// Executes the print Channel Statistics step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="path">Filesystem location constrained to the isolated test sandbox.</param>
    public static void PrintChannelStatistics(string path)
    {
        using SKBitmap? bitmap = SKBitmap.Decode(File.ReadAllBytes(path));
        if (bitmap is null)
        {
            throw new InvalidDataException($"PNG could not be decoded: {path}");
        }

        int[][] histograms = [new int[256], new int[256], new int[256]];
        int samples = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                histograms[0][pixel.Red]++;
                histograms[1][pixel.Green]++;
                histograms[2][pixel.Blue]++;
                samples++;
            }
        }

        string[] names = ["R", "G", "B"];
        for (int channel = 0; channel < histograms.Length; channel++)
        {
            Console.WriteLine(
                $"{names[channel]}: p10={Percentile(histograms[channel], samples, 0.10) / 255.0:0.000}, "
                + $"p25={Percentile(histograms[channel], samples, 0.25) / 255.0:0.000}, "
                + $"p50={Percentile(histograms[channel], samples, 0.50) / 255.0:0.000}, "
                + $"p75={Percentile(histograms[channel], samples, 0.75) / 255.0:0.000}, "
                + $"p90={Percentile(histograms[channel], samples, 0.90) / 255.0:0.000}, "
                + $"p99={Percentile(histograms[channel], samples, 0.99) / 255.0:0.000}.");
        }

        (double displayClipping, double neutralClipping, double highlightChroma, int p99Luminance)
            = MeasureDisplayColorimetry(bitmap);
        Console.WriteLine(
            $"Display colorimetry: p99 luminance={p99Luminance / 255.0:0.000}, "
            + $"gamut-edge pixels={displayClipping:P2}, neutral clipping={neutralClipping:P3}, "
            + $"high-luminance/high-chroma pixels={highlightChroma:P2}.");
    }

    /// <summary>
    /// Executes the print Thin Leak Diagnostics step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="captureDirectory">
    /// Optional archived capture directory used when the absolute paths recorded by the
    /// isolated runtime no longer exist.
    /// </param>
    public static void PrintThinLeakDiagnostics(string log, string? captureDirectory = null)
    {
        Match finalMatch = FinalPairRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!finalMatch.Success)
        {
            throw new InvalidDataException("final before/after capture pair was not logged");
        }

        using SKBitmap before = DecodeLoggedImage(finalMatch.Groups[1].Value, captureDirectory);
        using SKBitmap after = DecodeLoggedImage(finalMatch.Groups[2].Value, captureDirectory);
        using SKBitmap normal = DecodeLastLoggedImage(log, NormalMaskRegex(), captureDirectory);
        using SKBitmap position = DecodeLastLoggedImage(log, PositionMaskRegex(), captureDirectory);
        using SKBitmap material = DecodeLastLoggedImage(log, VoxelMaterialMaskRegex(), captureDirectory);
        using SKBitmap visibility = DecodeLastLoggedImage(log, VoxelVisibilityMaskRegex(), captureDirectory);
        using SKBitmap shadow = DecodeLastLoggedImage(log, ShadowMaskRegex(), captureDirectory);
        using SKBitmap nativeSun = DecodeLastLoggedImage(log, NativeSunShadowMaskRegex(), captureDirectory);
        using SKBitmap screenLighting = DecodeLastLoggedImage(log, ScreenLightingMaskRegex(), captureDirectory);
        using SKBitmap reflection = DecodeLastLoggedImage(log, ReflectionMaskRegex(), captureDirectory);
        using SKBitmap voxelReflection = DecodeLastLoggedImage(log, VoxelReflectionMaskRegex(), captureDirectory);
        using SKBitmap bounce = DecodeLastLoggedImage(log, VoxelBounceMaskRegex(), captureDirectory);
        using SKBitmap components = DecodeLastLoggedImage(log, TransportComponentsMaskRegex(), captureDirectory);

        int reported = 0;
        int total = 0;
        int coherentDepth = 0;
        int coherentNormal = 0;
        int coherentSurface = 0;
        int missingVoxelMaterial = 0;
        int nativeSupported = 0;
        int voxelNativeDisagreement = 0;
        for (int y = 1; y < after.Height - 1; y++)
        {
            for (int x = 1; x < after.Width - 1; x++)
            {
                double centerAfter = Luminance(after.GetPixel(x, y));
                double centerDelta = centerAfter - Luminance(before.GetPixel(x, y));
                double horizontalDelta = (
                    Luminance(after.GetPixel(x - 1, y)) - Luminance(before.GetPixel(x - 1, y))
                    + Luminance(after.GetPixel(x + 1, y)) - Luminance(before.GetPixel(x + 1, y))) * 0.5;
                double verticalDelta = (
                    Luminance(after.GetPixel(x, y - 1)) - Luminance(before.GetPixel(x, y - 1))
                    + Luminance(after.GetPixel(x, y + 1)) - Luminance(before.GetPixel(x, y + 1))) * 0.5;
                double darkestAxis = Math.Min(horizontalDelta, verticalDelta);
                if (centerAfter <= 0.22
                    || centerDelta <= -0.08
                    || darkestAxis >= -0.14
                    || centerDelta - darkestAxis <= 0.16)
                {
                    continue;
                }

                total++;
                bool horizontalAxis = horizontalDelta <= verticalDelta;
                SKColor neighborA = position.GetPixel(
                    horizontalAxis ? x - 1 : x,
                    horizontalAxis ? y : y - 1);
                SKColor neighborB = position.GetPixel(
                    horizontalAxis ? x + 1 : x,
                    horizontalAxis ? y : y + 1);
                double centerDepth = position.GetPixel(x, y).Red / 255.0;
                bool depthMatches = Math.Abs(centerDepth - neighborA.Red / 255.0) <= 0.012
                    && Math.Abs(centerDepth - neighborB.Red / 255.0) <= 0.012;
                Vector3 centerNormal = DecodeDiagnosticNormal(normal.GetPixel(x, y));
                Vector3 normalA = DecodeDiagnosticNormal(normal.GetPixel(
                    horizontalAxis ? x - 1 : x,
                    horizontalAxis ? y : y - 1));
                Vector3 normalB = DecodeDiagnosticNormal(normal.GetPixel(
                    horizontalAxis ? x + 1 : x,
                    horizontalAxis ? y : y + 1));
                bool normalsMatch = Vector3.Dot(centerNormal, normalA) >= 0.92f
                    && Vector3.Dot(centerNormal, normalB) >= 0.92f;
                coherentDepth += depthMatches ? 1 : 0;
                coherentNormal += normalsMatch ? 1 : 0;
                coherentSurface += depthMatches && normalsMatch ? 1 : 0;
                SKColor materialPixel = material.GetPixel(x, y);
                missingVoxelMaterial += materialPixel.Red == 0
                    && materialPixel.Green == 0
                    && materialPixel.Blue == 0
                        ? 1
                        : 0;
                SKColor nativeSunPixel = nativeSun.GetPixel(x, y);
                bool hasNativeSupport = nativeSunPixel.Blue >= 128;
                nativeSupported += hasNativeSupport ? 1 : 0;
                voxelNativeDisagreement += hasNativeSupport
                    && Math.Abs(nativeSunPixel.Red - nativeSunPixel.Green) >= 64
                        ? 1
                        : 0;
                if (reported < 24 && (reported == 0 || (x + y * 3) % 17 == 0))
                {
                    SKColor beforeA = before.GetPixel(
                        horizontalAxis ? x - 1 : x,
                        horizontalAxis ? y : y - 1);
                    SKColor beforeB = before.GetPixel(
                        horizontalAxis ? x + 1 : x,
                        horizontalAxis ? y : y + 1);
                    SKColor afterA = after.GetPixel(
                        horizontalAxis ? x - 1 : x,
                        horizontalAxis ? y : y - 1);
                    SKColor afterB = after.GetPixel(
                        horizontalAxis ? x + 1 : x,
                        horizontalAxis ? y : y + 1);
                    Console.WriteLine(
                        $"Leak ({x},{y}) before={Format(before.GetPixel(x, y))} "
                        + $"after={Format(after.GetPixel(x, y))} normal={Format(normal.GetPixel(x, y))} "
                        + $"axisBefore={Format(beforeA)}/{Format(beforeB)} "
                        + $"axisAfter={Format(afterA)}/{Format(afterB)} "
                        + $"position={Format(position.GetPixel(x, y))} material={Format(material.GetPixel(x, y))} "
                        + $"visibility={Format(visibility.GetPixel(x, y))} shadow={Format(shadow.GetPixel(x, y))} "
                        + $"native={Format(nativeSunPixel)} "
                        + $"ssgi={Format(screenLighting.GetPixel(x, y))} reflection={Format(reflection.GetPixel(x, y))} "
                        + $"voxelReflection={Format(voxelReflection.GetPixel(x, y))} bounce={Format(bounce.GetPixel(x, y))} "
                        + $"components={Format(components.GetPixel(x, y))}");
                    reported++;
                }
            }
        }

        Console.WriteLine(
            $"Thin leak diagnostics: {total} pixels, {reported} representative samples; "
            + $"coherent depth={coherentDepth}, coherent normal={coherentNormal}, "
            + $"coherent surface={coherentSurface}, missing voxel material={missingVoxelMaterial}, "
            + $"native support={nativeSupported}, voxel/native disagreement={voxelNativeDisagreement}.");
    }

    /// <summary>Decodes the normal diagnostic's signed unit vector.</summary>
    /// <param name="color">RGB diagnostic sample encoded from -1..1 to 0..1.</param>
    /// <returns>A normalized signed vector, or the forward fallback for a degenerate sample.</returns>
    private static Vector3 DecodeDiagnosticNormal(SKColor color)
    {
        Vector3 normal = new(
            color.Red / 127.5f - 1.0f,
            color.Green / 127.5f - 1.0f,
            color.Blue / 127.5f - 1.0f);
        return normal.LengthSquared() > 0.0001f
            ? Vector3.Normalize(normal)
            : Vector3.UnitZ;
    }

    /// <summary>
    /// Executes the decode Last Logged Image step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="regex">Coordinate component in the space defined by the tested API.</param>
    /// <param name="captureDirectory">Optional directory containing archived capture PNG files.</param>
    /// <returns>The decode Last Logged Image result consumed by the caller&apos;s assertion.</returns>
    private static SKBitmap DecodeLastLoggedImage(
        string log,
        Regex regex,
        string? captureDirectory = null)
    {
        Match match = regex.Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            throw new InvalidDataException($"diagnostic capture for '{regex}' was not logged");
        }
        return DecodeLoggedImage(match.Groups[1].Value, captureDirectory);
    }

    /// <summary>
    /// Executes the decode Logged Image step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="loggedPath">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="captureDirectory">Optional directory containing an archived copy of the logged PNG.</param>
    /// <returns>The decode Logged Image result consumed by the caller&apos;s assertion.</returns>
    private static SKBitmap DecodeLoggedImage(
        string loggedPath,
        string? captureDirectory = null)
    {
        string path = loggedPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (!File.Exists(path) && !string.IsNullOrWhiteSpace(captureDirectory))
        {
            path = Path.Combine(captureDirectory, Path.GetFileName(path));
        }
        SKBitmap? bitmap = File.Exists(path) ? SKBitmap.Decode(File.ReadAllBytes(path)) : null;
        return bitmap ?? throw new InvalidDataException($"diagnostic PNG could not be decoded: '{path}'");
    }

    /// <summary>
    /// Executes the format step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="color">The color input used to configure this deterministic test path.</param>
    /// <returns>The format result consumed by the caller&apos;s assertion.</returns>
    private static string Format(SKColor color) => $"({color.Red},{color.Green},{color.Blue})";

    /// <summary>
    /// Validates final Pair and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="shadowValidation">The shadow Validation input used to configure this deterministic test path.</param>
    /// <param name="validateReflections">The validate Reflections input used to configure this deterministic test path.</param>
    /// <param name="validateVoxelReflections">The validate Voxel Reflections input used to configure this deterministic test path.</param>
    /// <param name="validateVoxelBounce">The validate Voxel Bounce input used to configure this deterministic test path.</param>
    /// <param name="validateWetness">The validate Wetness input used to configure this deterministic test path.</param>
    /// <param name="requirePbrReferenceMaterials">Whether the scene deliberately frames metallic, rough, and polished references.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    public static IReadOnlyList<string> ValidateFinalPair(
        string log,
        ShadowValidation shadowValidation = ShadowValidation.Projected,
        bool validateReflections = false,
        bool validateVoxelReflections = false,
        bool validateVoxelBounce = false,
        bool validateWetness = false,
        bool requirePbrReferenceMaterials = false)
    {
        Match match = FinalPairRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            return ["final before/after capture pair was not logged"];
        }

        string beforePath = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        string afterPath = match.Groups[2].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (!File.Exists(beforePath) || !File.Exists(afterPath))
        {
            return [$"final capture file missing: before='{beforePath}', after='{afterPath}'"];
        }

        using SKBitmap? before = SKBitmap.Decode(File.ReadAllBytes(beforePath));
        using SKBitmap? after = SKBitmap.Decode(File.ReadAllBytes(afterPath));
        if (before is null || after is null)
        {
            return [$"final before/after capture pair could not be decoded: before='{beforePath}', after='{afterPath}'"];
        }
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return ["final before/after capture dimensions differ"];
        }

        double absoluteDelta = 0.0;
        int darkened = 0;
        int brightened = 0;
        int samples = 0;
        for (int y = 0; y < before.Height; y += 2)
        {
            for (int x = 0; x < before.Width; x += 2)
            {
                double beforeLuma = Luminance(before.GetPixel(x, y));
                double afterLuma = Luminance(after.GetPixel(x, y));
                double delta = afterLuma - beforeLuma;
                absoluteDelta += Math.Abs(delta);
                darkened += delta < -0.025 ? 1 : 0;
                brightened += delta > 0.025 ? 1 : 0;
                samples++;
            }
        }

        double meanAbsoluteDelta = absoluteDelta / samples;
        double darkenedRatio = (double)darkened / samples;
        double brightenedRatio = (double)brightened / samples;
        double checkerboardCorrelation = MeasureCheckerboardCorrelation(before, after);
        double isolatedHighlightDensity = MeasureIsolatedHighlightDensity(before, after);
        double thinLeakDensity = MeasureThinLeakDensity(before, after);
        (double displayClipping, double neutralClipping, double highlightChroma, int p99Luminance)
            = MeasureDisplayColorimetry(after);
        Console.WriteLine(
            $"Image delta: MAE={meanAbsoluteDelta:0.0000}, darkened={darkenedRatio:P1}, "
            + $"brightened={brightenedRatio:P1}, checkerboard={checkerboardCorrelation:0.0000}, "
            + $"isolated highlights={isolatedHighlightDensity:P3}, thin leaks={thinLeakDensity:P3}.");
        Console.WriteLine(
            $"Display colorimetry: p99 luminance={p99Luminance / 255.0:0.000}, "
            + $"gamut-edge pixels={displayClipping:P2}, neutral clipping={neutralClipping:P3}, "
            + $"high-luminance/high-chroma pixels={highlightChroma:P2}.");

        List<string> failures = [];
        if (meanAbsoluteDelta < 0.004)
        {
            failures.Add($"visual effect is too close to vanilla (luma MAE {meanAbsoluteDelta:0.0000})");
        }
        if (checkerboardCorrelation > 0.08)
        {
            failures.Add(
                $"temporal reconstruction left a screen-space checkerboard "
                + $"(correlation {checkerboardCorrelation:0.0000})");
        }
        if (isolatedHighlightDensity > 0.0012)
        {
            failures.Add(
                $"specular transport left a firefly/point-cloud pattern "
                + $"(isolated highlight density {isolatedHighlightDensity:P3})");
        }
        if (shadowValidation == ShadowValidation.SunProjected
            && thinLeakDensity > 0.00005)
        {
            failures.Add(
                $"relighting left bright one-pixel seams in shadowed geometry "
                + $"(thin leak density {thinLeakDensity:P3})");
        }
        failures.AddRange(ValidateShadowMask(log, shadowValidation));
        if (ShouldValidateRenderLabVegetationSunShadow(log))
        {
            failures.AddRange(ValidateRenderLabVegetationSunShadow(log));
        }
        if (ShouldValidateRealMapVegetationSunShadow(log))
        {
            failures.AddRange(ValidateRealMapVegetationSunShadow(log));
        }
        failures.AddRange(ValidatePbrMaterialMask(log, requirePbrReferenceMaterials));
        failures.AddRange(ValidatePbrNormalMask(log));
        if (validateReflections)
        {
            failures.AddRange(ValidateReflectionMask(log));
        }
        if (validateVoxelReflections)
        {
            failures.AddRange(ValidateVoxelReflectionMask(log));
        }
        if (validateVoxelBounce)
        {
            failures.AddRange(ValidateVoxelBounceMask(log));
        }
        if (validateWetness)
        {
            failures.AddRange(ValidateWetnessMask(log));
        }

        return failures;
    }

    /// <summary>
    /// Validates temporally separated final and point-shadow frames from the fixed lantern scene.
    /// Sparse animated flame pixels are tolerated; broad receiver illumination or visibility
    /// changes are rejected because camera, weather, clock, geometry, and emitter are locked.
    /// </summary>
    /// <param name="log">Merged client/server log containing the persisted capture paths.</param>
    /// <returns>Actionable stability failures, or an empty list for a stable triplet.</returns>
    public static IReadOnlyList<string> ValidateLightStability(string log)
    {
        string[] labels =
        [
            "final",
            "light-stability-final-b",
            "light-stability-final-c",
            "voxel-shadow",
            "light-stability-shadow-b",
            "light-stability-shadow-c"
        ];
        string[] baselinePaths = new string[labels.Length];
        string[] effectPaths = new string[labels.Length];
        for (int index = 0; index < labels.Length; index++)
        {
            if (!TryFindCapturePairPaths(
                    log,
                    labels[index],
                    out baselinePaths[index],
                    out effectPaths[index]))
            {
                return [$"light-stability capture was not logged: {labels[index]}"];
            }
            if (!File.Exists(baselinePaths[index]))
            {
                return [$"light-stability Vanilla witness is missing: {baselinePaths[index]}"];
            }
            if (!File.Exists(effectPaths[index]))
            {
                return [$"light-stability capture file is missing: {effectPaths[index]}"];
            }
        }

        using SKBitmap? finalBaselineA = SKBitmap.Decode(File.ReadAllBytes(baselinePaths[0]));
        using SKBitmap? finalBaselineB = SKBitmap.Decode(File.ReadAllBytes(baselinePaths[1]));
        using SKBitmap? finalBaselineC = SKBitmap.Decode(File.ReadAllBytes(baselinePaths[2]));
        using SKBitmap? shadowBaselineA = SKBitmap.Decode(File.ReadAllBytes(baselinePaths[3]));
        using SKBitmap? shadowBaselineB = SKBitmap.Decode(File.ReadAllBytes(baselinePaths[4]));
        using SKBitmap? shadowBaselineC = SKBitmap.Decode(File.ReadAllBytes(baselinePaths[5]));
        using SKBitmap? finalA = SKBitmap.Decode(File.ReadAllBytes(effectPaths[0]));
        using SKBitmap? finalB = SKBitmap.Decode(File.ReadAllBytes(effectPaths[1]));
        using SKBitmap? finalC = SKBitmap.Decode(File.ReadAllBytes(effectPaths[2]));
        using SKBitmap? shadowA = SKBitmap.Decode(File.ReadAllBytes(effectPaths[3]));
        using SKBitmap? shadowB = SKBitmap.Decode(File.ReadAllBytes(effectPaths[4]));
        using SKBitmap? shadowC = SKBitmap.Decode(File.ReadAllBytes(effectPaths[5]));
        if (finalA is null || finalB is null || finalC is null
            || shadowA is null || shadowB is null || shadowC is null
            || finalBaselineA is null || finalBaselineB is null || finalBaselineC is null
            || shadowBaselineA is null || shadowBaselineB is null || shadowBaselineC is null)
        {
            return ["one or more light-stability captures could not be decoded"];
        }

        TemporalTripletAssessment final = MeasureMotionRejectedTemporalTriplet(
            finalA,
            finalB,
            finalC,
            finalBaselineA,
            finalBaselineB,
            finalBaselineC,
            redChannelOnly: false,
            changeThreshold: 0.025,
            baselineMotionThreshold: 0.02,
            compareEffectDeltaFromBaseline: true,
            baselineMotionHaloPixels: 2);
        TemporalTripletAssessment shadow = MeasureMotionRejectedTemporalTriplet(
            shadowA,
            shadowB,
            shadowC,
            shadowBaselineA,
            shadowBaselineB,
            shadowBaselineC,
            redChannelOnly: true,
            changeThreshold: 0.05,
            baselineMotionThreshold: 0.02,
            compareEffectDeltaFromBaseline: false,
            baselineMotionHaloPixels: 2);
        Console.WriteLine(
            $"Fixed-light temporal stability: final MAE={final.MaximumMeanAbsoluteDelta:0.0000}, "
            + $"final changed={final.MaximumChangedPixelRatio:P2}, final stable receivers={final.StableSampleRatio:P2}, "
            + $"final correction means={final.FirstMean:0.0000}/{final.SecondMean:0.0000}/{final.ThirdMean:0.0000}, "
            + $"final pair MAE={final.FirstSecondMeanAbsoluteDelta:0.0000}/{final.SecondThirdMeanAbsoluteDelta:0.0000}/{final.FirstThirdMeanAbsoluteDelta:0.0000}, "
            + $"point-shadow MAE={shadow.MaximumMeanAbsoluteDelta:0.0000}, "
            + $"point-shadow changed={shadow.MaximumChangedPixelRatio:P2}, "
            + $"point-shadow stable receivers={shadow.StableSampleRatio:P2}, "
            + $"point-shadow means={shadow.FirstMean:0.0000}/{shadow.SecondMean:0.0000}/{shadow.ThirdMean:0.0000}, "
            + $"point-shadow pair MAE={shadow.FirstSecondMeanAbsoluteDelta:0.0000}/{shadow.SecondThirdMeanAbsoluteDelta:0.0000}/{shadow.FirstThirdMeanAbsoluteDelta:0.0000}.");

        List<string> failures = [];
        if (!final.Compatible || !shadow.Compatible)
        {
            failures.Add("light-stability triplet dimensions are incompatible");
            return failures;
        }
        if (final.StableSampleRatio < 0.40 || shadow.StableSampleRatio < 0.40)
        {
            failures.Add(
                $"light-stability Vanilla witnesses retain too few static receivers "
                + $"(final {final.StableSampleRatio:P2}, point-shadow {shadow.StableSampleRatio:P2})");
            return failures;
        }
        if (final.MaximumMeanAbsoluteDelta > 0.010
            || final.MaximumChangedPixelRatio > 0.08)
        {
            failures.Add(
                $"fixed lantern illumination flickers or moves (MAE {final.MaximumMeanAbsoluteDelta:0.0000}, "
                + $"changed {final.MaximumChangedPixelRatio:P2})");
        }
        if (shadow.MaximumMeanAbsoluteDelta > 0.008
            || shadow.MaximumChangedPixelRatio > 0.04)
        {
            failures.Add(
                $"fixed lantern projected shadow is temporally unstable (MAE {shadow.MaximumMeanAbsoluteDelta:0.0000}, "
                + $"changed {shadow.MaximumChangedPixelRatio:P2})");
        }

        return failures;
    }

    /// <summary>Measures the worst of the three pairwise scalar-image differences.</summary>
    /// <param name="first">First fixed-camera frame.</param>
    /// <param name="second">Second fixed-camera frame.</param>
    /// <param name="third">Third fixed-camera frame.</param>
    /// <param name="redChannelOnly">Whether to inspect only the red point-shadow carrier.</param>
    /// <param name="changeThreshold">Normalized delta above which a pixel is counted as changed.</param>
    /// <returns>Worst pairwise mean and changed-pixel ratio.</returns>
    internal static TemporalTripletAssessment MeasureTemporalTriplet(
        SKBitmap first,
        SKBitmap second,
        SKBitmap third,
        bool redChannelOnly,
        double changeThreshold)
    {
        return MeasureMotionRejectedTemporalTriplet(
            first,
            second,
            third,
            first,
            second,
            third,
            redChannelOnly,
            changeThreshold,
            baselineMotionThreshold: 1.0,
            compareEffectDeltaFromBaseline: false,
            baselineMotionHaloPixels: 0);
    }

    /// <summary>
    /// Measures temporal effect variation only where the simultaneous Vanilla frames prove
    /// that geometry and animation remained stable throughout the triplet.
    /// </summary>
    /// <param name="first">First effect frame.</param>
    /// <param name="second">Second effect frame.</param>
    /// <param name="third">Third effect frame.</param>
    /// <param name="baselineFirst">Simultaneous Vanilla witness for <paramref name="first"/>.</param>
    /// <param name="baselineSecond">Simultaneous Vanilla witness for <paramref name="second"/>.</param>
    /// <param name="baselineThird">Simultaneous Vanilla witness for <paramref name="third"/>.</param>
    /// <param name="redChannelOnly">Whether to inspect only the red point-shadow carrier.</param>
    /// <param name="changeThreshold">Normalized effect delta above which a stable pixel is counted as changed.</param>
    /// <param name="baselineMotionThreshold">Maximum Vanilla RGB-channel delta retained as static geometry.</param>
    /// <param name="compareEffectDeltaFromBaseline">Whether to compare the visual RTX correction rather than absolute effect colour.</param>
    /// <param name="baselineMotionHaloPixels">Pixel radius eroded around Vanilla motion before measurement.</param>
    /// <returns>Worst pairwise effect variation and the fraction of geometrically stable samples.</returns>
    internal static TemporalTripletAssessment MeasureMotionRejectedTemporalTriplet(
        SKBitmap first,
        SKBitmap second,
        SKBitmap third,
        SKBitmap baselineFirst,
        SKBitmap baselineSecond,
        SKBitmap baselineThird,
        bool redChannelOnly,
        double changeThreshold,
        double baselineMotionThreshold,
        bool compareEffectDeltaFromBaseline = false,
        int baselineMotionHaloPixels = 0)
    {
        if (first.Width <= 0
            || first.Height <= 0
            || second.Width != first.Width
            || second.Height != first.Height
            || third.Width != first.Width
            || third.Height != first.Height
            || baselineFirst.Width != first.Width
            || baselineFirst.Height != first.Height
            || baselineSecond.Width != first.Width
            || baselineSecond.Height != first.Height
            || baselineThird.Width != first.Width
            || baselineThird.Height != first.Height
            || !double.IsFinite(changeThreshold)
            || changeThreshold < 0.0
            || changeThreshold > 1.0
            || !double.IsFinite(baselineMotionThreshold)
            || baselineMotionThreshold < 0.0
            || baselineMotionThreshold > 1.0
            || baselineMotionHaloPixels < 0
            || baselineMotionHaloPixels > 32)
        {
            return default;
        }

        double[] sums = new double[3];
        double[] frameSums = new double[3];
        int[] changed = new int[3];
        int sampled = 0;
        int stable = 0;
        for (int y = 0; y < first.Height; y += 2)
        {
            for (int x = 0; x < first.Width; x += 2)
            {
                sampled++;
                if (!IsVanillaNeighborhoodStable(x, y))
                {
                    continue;
                }

                stable++;
                SKColor baselineA = baselineFirst.GetPixel(x, y);
                SKColor baselineB = baselineSecond.GetPixel(x, y);
                SKColor baselineC = baselineThird.GetPixel(x, y);
                SKColor effectA = first.GetPixel(x, y);
                SKColor effectB = second.GetPixel(x, y);
                SKColor effectC = third.GetPixel(x, y);
                double valueA = redChannelOnly ? effectA.Red / 255.0 : Luminance(effectA);
                double valueB = redChannelOnly ? effectB.Red / 255.0 : Luminance(effectB);
                double valueC = redChannelOnly ? effectC.Red / 255.0 : Luminance(effectC);
                if (compareEffectDeltaFromBaseline)
                {
                    valueA -= Luminance(baselineA);
                    valueB -= Luminance(baselineB);
                    valueC -= Luminance(baselineC);
                }
                frameSums[0] += valueA;
                frameSums[1] += valueB;
                frameSums[2] += valueC;
                AccumulatePair(0, Math.Abs(valueB - valueA));
                AccumulatePair(1, Math.Abs(valueC - valueB));
                AccumulatePair(2, Math.Abs(valueC - valueA));
            }
        }

        return new TemporalTripletAssessment(
            true,
            stable == 0 ? 0.0 : sums.Max() / stable,
            stable == 0 ? 0.0 : (double)changed.Max() / stable,
            sampled == 0 ? 0.0 : (double)stable / sampled,
            stable == 0 ? 0.0 : frameSums[0] / stable,
            stable == 0 ? 0.0 : frameSums[1] / stable,
            stable == 0 ? 0.0 : frameSums[2] / stable,
            stable == 0 ? 0.0 : sums[0] / stable,
            stable == 0 ? 0.0 : sums[1] / stable,
            stable == 0 ? 0.0 : sums[2] / stable);

        void AccumulatePair(int pairIndex, double delta)
        {
            sums[pairIndex] += delta;
            changed[pairIndex] += delta > changeThreshold ? 1 : 0;
        }

        bool IsVanillaNeighborhoodStable(int centerX, int centerY)
        {
            int radius = baselineMotionHaloPixels;
            int step = radius == 0 ? 1 : 2;
            for (int offsetY = -radius; offsetY <= radius; offsetY += step)
            {
                int sampleY = Math.Clamp(centerY + offsetY, 0, first.Height - 1);
                for (int offsetX = -radius; offsetX <= radius; offsetX += step)
                {
                    int sampleX = Math.Clamp(centerX + offsetX, 0, first.Width - 1);
                    SKColor firstWitness = baselineFirst.GetPixel(sampleX, sampleY);
                    SKColor secondWitness = baselineSecond.GetPixel(sampleX, sampleY);
                    SKColor thirdWitness = baselineThird.GetPixel(sampleX, sampleY);
                    double maximumDelta = Math.Max(
                        MaximumRgbDelta(firstWitness, secondWitness),
                        Math.Max(
                            MaximumRgbDelta(secondWitness, thirdWitness),
                            MaximumRgbDelta(firstWitness, thirdWitness)));
                    if (maximumDelta > baselineMotionThreshold)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>Returns the largest normalized RGB-channel delta between two pixels.</summary>
    private static double MaximumRgbDelta(SKColor left, SKColor right) =>
        Math.Max(
            Math.Abs(right.Red - left.Red),
            Math.Max(Math.Abs(right.Green - left.Green), Math.Abs(right.Blue - left.Blue))) / 255.0;

    /// <summary>Finds both paths for one exact automatic-capture comparison label.</summary>
    private static bool TryFindCapturePairPaths(
        string log,
        string label,
        out string baselinePath,
        out string effectPath)
    {
        string escapedLabel = Regex.Escape(label);
        Match match = Regex.Matches(
                log,
                $@"Comparison capture saved: (.+?-{escapedLabel}-before\.png) and (.+?-{escapedLabel}-vintagertx\.png)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .LastOrDefault() ?? Match.Empty;
        baselinePath = match.Success
            ? match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar)
            : string.Empty;
        effectPath = match.Success
            ? match.Groups[2].Value.Trim().Replace('/', Path.DirectorySeparatorChar)
            : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// Executes the measure Display Colorimetry step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="bitmap">The bitmap input used to configure this deterministic test path.</param>
    /// <returns>The measure Display Colorimetry result consumed by the caller&apos;s assertion.</returns>
    private static (double DisplayClipping, double NeutralClipping, double HighlightChroma, int P99Luminance)
        MeasureDisplayColorimetry(SKBitmap bitmap)
    {
        int[] luminanceHistogram = new int[256];
        int displayClipping = 0;
        int neutralClipping = 0;
        int highlightChroma = 0;
        int samples = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                int maximum = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
                int minimum = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                int luminance = Math.Clamp((int)Math.Round(Luminance(pixel) * 255.0), 0, 255);
                luminanceHistogram[luminance]++;
                displayClipping += maximum >= 254 ? 1 : 0;
                neutralClipping += minimum >= 254 ? 1 : 0;
                highlightChroma += luminance >= 153 && maximum - minimum >= 140 ? 1 : 0;
                samples++;
            }
        }

        return (
            (double)displayClipping / samples,
            (double)neutralClipping / samples,
            (double)highlightChroma / samples,
            Percentile(luminanceHistogram, samples, 0.99));
    }

    /// <summary>
    /// Validates pbr Material Mask and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="requirePbrReferenceMaterials">Whether all authored reference material classes must be visible.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidatePbrMaterialMask(
        string log,
        bool requirePbrReferenceMaterials)
    {
        Match match = PbrMaterialMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            return ["PBR material diagnostic capture was not logged"];
        }

        using SKBitmap? bitmap = DecodeOptionalLoggedImage(match.Groups[1].Value);
        if (bitmap is null)
        {
            return ["PBR material diagnostic capture could not be decoded"];
        }

        int samples = 0;
        int authoredSamples = 0;
        int authoredMetal = 0;
        int authoredRough = 0;
        int authoredSmooth = 0;
        HashSet<int> smoothnessBuckets = [];
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                samples++;
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Blue < 128)
                {
                    continue;
                }

                authoredSamples++;
                authoredMetal += pixel.Red >= 128 ? 1 : 0;
                authoredRough += pixel.Green <= 128 ? 1 : 0;
                authoredSmooth += pixel.Green >= 179 ? 1 : 0;
                smoothnessBuckets.Add(pixel.Green / 16);
            }
        }

        double authoredCoverage = samples > 0 ? (double)authoredSamples / samples : 0.0;
        double metalCoverage = samples > 0 ? (double)authoredMetal / samples : 0.0;
        double roughRatio = authoredSamples > 0 ? (double)authoredRough / authoredSamples : 0.0;
        double smoothRatio = authoredSamples > 0 ? (double)authoredSmooth / authoredSamples : 0.0;
        Console.WriteLine(
            $"PBR material mask: file-backed={authoredCoverage:P1}, "
            + $"metal={metalCoverage:P2}, rough={roughRatio:P1}, smooth={smoothRatio:P1}, "
            + $"smoothness buckets={smoothnessBuckets.Count}.");
        List<string> failures = [];
        if (authoredCoverage < 0.10)
        {
            failures.Add(
                $"file-backed PBR payload is absent from visible terrain "
                + $"({authoredCoverage:P1} of sampled pixels)");
        }
        if (smoothnessBuckets.Count < 3)
        {
            failures.Add(
                $"file-backed roughness is flat or not decoded "
                + $"({smoothnessBuckets.Count} quantized smoothness buckets)");
        }
        if (requirePbrReferenceMaterials && metalCoverage < 0.001)
        {
            failures.Add(
                $"file-backed metallic material is absent or too small to assess "
                + $"({metalCoverage:P3} of sampled pixels)");
        }
        if (requirePbrReferenceMaterials
            && (roughRatio < 0.02 || smoothRatio < 0.01))
        {
            failures.Add(
                $"rough and polished file-backed materials are not visually separated "
                + $"(rough {roughRatio:P1}, smooth {smoothRatio:P1})");
        }

        return failures;
    }

    /// <summary>
    /// Validates pbr Normal Mask and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidatePbrNormalMask(string log)
    {
        Match normalMatch = NormalMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        Match materialMatch = PbrMaterialMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        Match positionMatch = PositionMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!normalMatch.Success || !materialMatch.Success || !positionMatch.Success)
        {
            return ["PBR normal/material/position diagnostic captures were not all logged"];
        }

        using SKBitmap? normal = DecodeOptionalLoggedImage(normalMatch.Groups[1].Value);
        using SKBitmap? material = DecodeOptionalLoggedImage(materialMatch.Groups[1].Value);
        using SKBitmap? position = DecodeOptionalLoggedImage(positionMatch.Groups[1].Value);
        if (normal is null || material is null
            || position is null
            || normal.Width != material.Width
            || normal.Height != material.Height
            || normal.Width != position.Width
            || normal.Height != position.Height)
        {
            return ["PBR normal/material/position diagnostic captures could not be paired"];
        }

        // Measure local angular variation only inside an authored-material
        // neighbourhood on one continuous receiver. Material RGB can vary by a
        // few quantization codes after capture, while a depth jump or a larger
        // material change identifies a block edge, silhouette or intersecting
        // foliage plane. Those discontinuities belong to geometry and must not
        // be counted as overdriven tangent-space normal-map detail.
        List<double> localAngularRms = [];
        int responsive = 0;
        int completeNeighborhoods = 0;
        int excessiveNeighborhoods = 0;
        ReadOnlySpan<(int X, int Y)> offsets =
        [(-2, 0), (2, 0), (0, -2), (0, 2)];
        PbrNormalNeighborhoodSample[] neighbors = new PbrNormalNeighborhoodSample[offsets.Length];
        for (int y = 2; y < normal.Height - 2; y += 2)
        {
            for (int x = 2; x < normal.Width - 2; x += 2)
            {
                SKColor centerMaterial = material.GetPixel(x, y);
                byte centerDepth = position.GetPixel(x, y).Red;
                PbrNormalNeighborhoodSample center = new(
                    DecodeNormal(normal.GetPixel(x, y)),
                    centerMaterial.Red,
                    centerMaterial.Green,
                    centerMaterial.Blue,
                    centerDepth);
                for (int neighborIndex = 0; neighborIndex < offsets.Length; neighborIndex++)
                {
                    (int offsetX, int offsetY) = offsets[neighborIndex];
                    int neighborX = x + offsetX;
                    int neighborY = y + offsetY;
                    SKColor neighborMaterial = material.GetPixel(neighborX, neighborY);
                    byte neighborDepth = position.GetPixel(neighborX, neighborY).Red;
                    neighbors[neighborIndex] = new PbrNormalNeighborhoodSample(
                        DecodeNormal(normal.GetPixel(neighborX, neighborY)),
                        neighborMaterial.Red,
                        neighborMaterial.Green,
                        neighborMaterial.Blue,
                        neighborDepth);
                }

                PbrNormalNeighborhoodAssessment assessment = AssessPbrNormalNeighborhood(
                    in center,
                    neighbors);
                if (!assessment.CountsTowardExcessiveDenominator)
                {
                    continue;
                }

                completeNeighborhoods++;
                if (assessment.IsExcessive)
                {
                    excessiveNeighborhoods++;
                    continue;
                }

                localAngularRms.Add(assessment.AngularRmsDegrees);
                responsive += assessment.IsResponsive ? 1 : 0;
            }
        }

        localAngularRms.Sort();
        double median = Quantile(localAngularRms, 0.50);
        double p90 = Quantile(localAngularRms, 0.90);
        double responsiveRatio = localAngularRms.Count > 0
            ? (double)responsive / localAngularRms.Count
            : 0.0;
        double excessiveRatio = completeNeighborhoods > 0
            ? (double)excessiveNeighborhoods / completeNeighborhoods
            : 0.0;
        double globalAngularRms = localAngularRms.Count > 0
            ? Math.Sqrt(localAngularRms.Sum(static angle => angle * angle) / localAngularRms.Count)
            : 0.0;
        Console.WriteLine(
            $"PBR normal response: neighborhoods={localAngularRms.Count}, "
            + $"local RMS median={median:0.000} deg, p90={p90:0.000} deg, "
            + $"responsive={responsiveRatio:P1}, global angular RMS={globalAngularRms:0.000} deg, "
            + $"excessive={excessiveRatio:P1}.");

        List<string> failures = [];
        if (localAngularRms.Count < 64)
        {
            failures.Add(
                $"file-backed PBR normal response has too little measurable terrain "
                + $"({localAngularRms.Count} interior neighborhoods)");
        }
        if (!HasAppliedPbrNormalResponse(p90, responsiveRatio, globalAngularRms))
        {
            failures.Add(
                $"file-backed normal maps are flat or not applied in the rendered G-buffer "
                + $"(local angular RMS p90 {p90:0.000} deg, responsive {responsiveRatio:P1}, "
                + $"global angular RMS {globalAngularRms:0.000} deg)");
        }
        if (p90 > 18.0 || excessiveRatio > 0.08)
        {
            failures.Add(
                $"file-backed normal maps are overdriven and would produce embossed shimmer "
                + $"(local angular RMS p90 {p90:0.000} deg, excessive {excessiveRatio:P1})");
        }

        return failures;
    }

    /// <summary>
    /// Classifies a four-neighbor normal cross while keeping receiver discontinuities
    /// out of both the accepted population and the excessive-angle denominator.
    /// </summary>
    /// <param name="center">Center normal, authored material identity and depth.</param>
    /// <param name="neighbors">Exactly four axial neighbors sampled at the same radius.</param>
    /// <returns>Continuity, excessive-angle and local-response evidence for the cross.</returns>
    internal static PbrNormalNeighborhoodAssessment AssessPbrNormalNeighborhood(
        in PbrNormalNeighborhoodSample center,
        ReadOnlySpan<PbrNormalNeighborhoodSample> neighbors)
    {
        const int authoredCoverageMinimum = 128;
        const int maximumMaterialDelta = 4;
        const int maximumDepthDelta = 1;
        const double excessiveAngleDegrees = 35.0;
        const double responsiveRmsDegrees = 1.0;
        if (neighbors.Length != 4)
        {
            throw new ArgumentException("A PBR normal cross requires exactly four neighbors.", nameof(neighbors));
        }

        if (center.MaterialBlue < authoredCoverageMinimum
            || !TryNormalizeDiagnosticNormal(center.Normal, out Vector3 centerNormal))
        {
            return default;
        }

        double squaredAngleSum = 0.0;
        int excessiveNeighborCount = 0;
        foreach (PbrNormalNeighborhoodSample neighbor in neighbors)
        {
            int materialDelta = Math.Max(
                Math.Abs(center.MaterialRed - neighbor.MaterialRed),
                Math.Max(
                    Math.Abs(center.MaterialGreen - neighbor.MaterialGreen),
                    Math.Abs(center.MaterialBlue - neighbor.MaterialBlue)));
            if (neighbor.MaterialBlue < authoredCoverageMinimum
                || materialDelta > maximumMaterialDelta
                || Math.Abs(center.Depth - neighbor.Depth) > maximumDepthDelta
                || !TryNormalizeDiagnosticNormal(neighbor.Normal, out Vector3 neighborNormal))
            {
                return default;
            }

            double dot = Math.Clamp(Vector3.Dot(centerNormal, neighborNormal), -1.0f, 1.0f);
            double angle = Math.Acos(dot) * 180.0 / Math.PI;
            squaredAngleSum += angle * angle;
            excessiveNeighborCount += angle > excessiveAngleDegrees ? 1 : 0;
        }

        double angularRms = Math.Sqrt(squaredAngleSum / neighbors.Length);
        // One axial outlier is the signature of a receiver/silhouette edge
        // slipping through the 8-bit depth diagnostic: the other three samples
        // still agree with the center. A truly overdriven center texel or a
        // discontinuous tangent field disagrees in at least two directions.
        // Exclude the unresolved single edge from both response and excessive
        // populations instead of misreporting macro geometry as normal-map gain.
        if (excessiveNeighborCount == 1)
        {
            return default;
        }

        bool excessive = excessiveNeighborCount >= 2;
        return new PbrNormalNeighborhoodAssessment(
            CountsTowardExcessiveDenominator: true,
            IsExcessive: excessive,
            IsResponsive: !excessive && angularRms >= responsiveRmsDegrees,
            AngularRmsDegrees: angularRms);
    }

    /// <summary>
    /// Combines angular amplitude and spatial coverage into evidence that normal maps
    /// affect the G-buffer. The concentrated branch retains independent amplitude and
    /// coverage floors so a few geometry edges cannot satisfy it, then requires actual
    /// spatial RMS energy over every accepted neighborhood rather than a percentile proxy.
    /// </summary>
    /// <param name="p90AngularRmsDegrees">Ninetieth percentile local angular RMS.</param>
    /// <param name="responsiveRatio">Fraction of accepted neighborhoods above one degree RMS.</param>
    /// <param name="globalAngularRmsDegrees">RMS angular energy over all accepted neighborhoods.</param>
    /// <returns><see langword="true"/> when distributed or concentrated response is physically credible.</returns>
    internal static bool HasAppliedPbrNormalResponse(
        double p90AngularRmsDegrees,
        double responsiveRatio,
        double globalAngularRmsDegrees)
    {
        if (!double.IsFinite(p90AngularRmsDegrees)
            || !double.IsFinite(responsiveRatio)
            || !double.IsFinite(globalAngularRmsDegrees)
            || p90AngularRmsDegrees < 0.0
            || globalAngularRmsDegrees < 0.0
            || responsiveRatio is < 0.0 or > 1.0)
        {
            return false;
        }

        bool distributedResponse = p90AngularRmsDegrees >= 0.65
            && responsiveRatio >= 0.18;
        bool concentratedResponse = p90AngularRmsDegrees >= 1.0
            && responsiveRatio >= 0.12
            && globalAngularRmsDegrees >= 0.40;
        return distributedResponse || concentratedResponse;
    }

    /// <summary>Normalizes a finite diagnostic normal and rejects zero/invalid vectors.</summary>
    /// <param name="candidate">Decoded vector to validate.</param>
    /// <param name="normalized">Unit vector when validation succeeds.</param>
    /// <returns><see langword="true"/> for a finite vector with meaningful length.</returns>
    private static bool TryNormalizeDiagnosticNormal(Vector3 candidate, out Vector3 normalized)
    {
        float lengthSquared = candidate.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 0.01f)
        {
            normalized = Vector3.Zero;
            return false;
        }

        normalized = Vector3.Normalize(candidate);
        return float.IsFinite(normalized.X)
            && float.IsFinite(normalized.Y)
            && float.IsFinite(normalized.Z);
    }

    /// <summary>
    /// Executes the decode Normal step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="encoded">Stable identifier selecting the deterministic fixture case.</param>
    /// <returns>The decode Normal result consumed by the caller&apos;s assertion.</returns>
    private static Vector3 DecodeNormal(SKColor encoded)
    {
        Vector3 normal = new(
            encoded.Red / 127.5f - 1.0f,
            encoded.Green / 127.5f - 1.0f,
            encoded.Blue / 127.5f - 1.0f);
        return normal.LengthSquared() > 0.01f
            ? Vector3.Normalize(normal)
            : Vector3.Zero;
    }

    /// <summary>
    /// Executes the quantile step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="sortedValues">The sorted Values input used to configure this deterministic test path.</param>
    /// <param name="fraction">Injected operation used to isolate the test from external state.</param>
    /// <returns>The quantile result consumed by the caller&apos;s assertion.</returns>
    private static double Quantile(IReadOnlyList<double> sortedValues, double fraction)
    {
        if (sortedValues.Count == 0)
        {
            return 0.0;
        }

        int index = (int)Math.Round(
            Math.Clamp(fraction, 0.0, 1.0) * (sortedValues.Count - 1),
            MidpointRounding.AwayFromZero);
        return sortedValues[index];
    }

    /// <summary>
    /// Executes the measure Checkerboard Correlation step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="bitmap">The bitmap input used to configure this deterministic test path.</param>
    /// <returns>The measure Checkerboard Correlation result consumed by the caller&apos;s assertion.</returns>
    internal static double MeasureCheckerboardCorrelation(SKBitmap bitmap)
    {
        double signedResidual = 0.0;
        double absoluteResidual = 0.0;
        for (int y = 1; y < bitmap.Height - 1; y++)
        {
            for (int x = 1; x < bitmap.Width - 1; x++)
            {
                double center = Luminance(bitmap.GetPixel(x, y));
                double neighbours = (
                    Luminance(bitmap.GetPixel(x - 1, y))
                    + Luminance(bitmap.GetPixel(x + 1, y))
                    + Luminance(bitmap.GetPixel(x, y - 1))
                    + Luminance(bitmap.GetPixel(x, y + 1))) * 0.25;
                double residual = center - neighbours;
                signedResidual += ((x + y) & 1) == 0 ? residual : -residual;
                absoluteResidual += Math.Abs(residual);
            }
        }

        return absoluteResidual > 0.000001
            ? Math.Abs(signedResidual) / absoluteResidual
            : 0.0;
    }

    /// <summary>
    /// Executes the measure Checkerboard Correlation step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="before">The before input used to configure this deterministic test path.</param>
    /// <param name="after">The after input used to configure this deterministic test path.</param>
    /// <returns>The measure Checkerboard Correlation result consumed by the caller&apos;s assertion.</returns>
    internal static double MeasureCheckerboardCorrelation(SKBitmap before, SKBitmap after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return 1.0;
        }

        double signedResidual = 0.0;
        double absoluteResidual = 0.0;
        for (int y = 1; y < after.Height - 1; y++)
        {
            for (int x = 1; x < after.Width - 1; x++)
            {
                double center = Luminance(after.GetPixel(x, y))
                    - Luminance(before.GetPixel(x, y));
                double neighbours = 0.0;
                neighbours += Luminance(after.GetPixel(x - 1, y))
                    - Luminance(before.GetPixel(x - 1, y));
                neighbours += Luminance(after.GetPixel(x + 1, y))
                    - Luminance(before.GetPixel(x + 1, y));
                neighbours += Luminance(after.GetPixel(x, y - 1))
                    - Luminance(before.GetPixel(x, y - 1));
                neighbours += Luminance(after.GetPixel(x, y + 1))
                    - Luminance(before.GetPixel(x, y + 1));
                double residual = center - neighbours * 0.25;
                signedResidual += ((x + y) & 1) == 0 ? residual : -residual;
                absoluteResidual += Math.Abs(residual);
            }
        }

        return absoluteResidual > 0.000001
            ? Math.Abs(signedResidual) / absoluteResidual
            : 0.0;
    }

    /// <summary>
    /// Executes the print Checkerboard Diagnostics step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="beforePath">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="afterPath">Filesystem location constrained to the isolated test sandbox.</param>
    public static void PrintCheckerboardDiagnostics(string beforePath, string afterPath)
    {
        using SKBitmap? before = SKBitmap.Decode(File.ReadAllBytes(beforePath));
        using SKBitmap? after = SKBitmap.Decode(File.ReadAllBytes(afterPath));
        if (before is null || after is null
            || before.Width != after.Width
            || before.Height != after.Height)
        {
            throw new InvalidDataException("checkerboard diagnostic images are missing or incompatible");
        }

        const int columns = 8;
        const int rows = 4;
        List<(int Column, int Row, double Correlation, double Energy, double Signed)> tiles = [];
        for (int row = 0; row < rows; row++)
        {
            int minimumY = Math.Max(1, row * after.Height / rows);
            int maximumY = Math.Min(after.Height - 1, (row + 1) * after.Height / rows);
            for (int column = 0; column < columns; column++)
            {
                int minimumX = Math.Max(1, column * after.Width / columns);
                int maximumX = Math.Min(after.Width - 1, (column + 1) * after.Width / columns);
                double signedResidual = 0.0;
                double absoluteResidual = 0.0;
                for (int y = minimumY; y < maximumY; y++)
                {
                    for (int x = minimumX; x < maximumX; x++)
                    {
                        double center = Luminance(after.GetPixel(x, y))
                            - Luminance(before.GetPixel(x, y));
                        double neighbours = 0.0;
                        neighbours += Luminance(after.GetPixel(x - 1, y))
                            - Luminance(before.GetPixel(x - 1, y));
                        neighbours += Luminance(after.GetPixel(x + 1, y))
                            - Luminance(before.GetPixel(x + 1, y));
                        neighbours += Luminance(after.GetPixel(x, y - 1))
                            - Luminance(before.GetPixel(x, y - 1));
                        neighbours += Luminance(after.GetPixel(x, y + 1))
                            - Luminance(before.GetPixel(x, y + 1));
                        double residual = center - neighbours * 0.25;
                        signedResidual += ((x + y) & 1) == 0 ? residual : -residual;
                        absoluteResidual += Math.Abs(residual);
                    }
                }

                double correlation = absoluteResidual > 0.000001
                    ? Math.Abs(signedResidual) / absoluteResidual
                    : 0.0;
                tiles.Add((column, row, correlation, absoluteResidual, signedResidual));
            }
        }

        foreach ((int column, int row, double correlation, double energy, double signed) in tiles
            .OrderByDescending(tile => Math.Abs(tile.Signed))
            .Take(12))
        {
            Console.WriteLine(
                $"Checker tile ({column},{row}): correlation={correlation:0.0000}, "
                + $"energy={energy:0.0}, signed={signed:+0.0;-0.0;0.0}");
        }
    }

    /// <summary>
    /// Executes the measure Isolated Highlight Density step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="before">The before input used to configure this deterministic test path.</param>
    /// <param name="after">The after input used to configure this deterministic test path.</param>
    /// <returns>The measure Isolated Highlight Density result consumed by the caller&apos;s assertion.</returns>
    internal static double MeasureIsolatedHighlightDensity(SKBitmap before, SKBitmap after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return 1.0;
        }

        int isolated = 0;
        int samples = 0;
        for (int y = 1; y < after.Height - 1; y++)
        {
            for (int x = 1; x < after.Width - 1; x++)
            {
                double centerAfter = Luminance(after.GetPixel(x, y));
                double centerDelta = centerAfter - Luminance(before.GetPixel(x, y));
                double neighbourMaximumDelta = double.NegativeInfinity;
                double neighbourMaximumAfter = double.NegativeInfinity;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        double neighbourAfter = Luminance(
                            after.GetPixel(x + offsetX, y + offsetY));
                        double neighbourDelta = neighbourAfter
                            - Luminance(before.GetPixel(x + offsetX, y + offsetY));
                        neighbourMaximumDelta = Math.Max(neighbourMaximumDelta, neighbourDelta);
                        neighbourMaximumAfter = Math.Max(neighbourMaximumAfter, neighbourAfter);
                    }
                }

                // A firefly is a displayed local radiance maximum. A dark
                // albedo texel can receive a larger before/after correction
                // than its already-bright neighbour without ever becoming a
                // visible point; classifying that ordinary textured relight
                // made this metric depend on wind-driven foliage phase.
                if (centerAfter > 0.22
                    && centerDelta > 0.12
                    && centerDelta - neighbourMaximumDelta > 0.075
                    && centerAfter > neighbourMaximumAfter)
                {
                    isolated++;
                }
                samples++;
            }
        }

        return samples > 0 ? (double)isolated / samples : 0.0;
    }

    /// <summary>
    /// Executes the measure Thin Leak Density step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="before">The before input used to configure this deterministic test path.</param>
    /// <param name="after">The after input used to configure this deterministic test path.</param>
    /// <returns>The measure Thin Leak Density result consumed by the caller&apos;s assertion.</returns>
    internal static double MeasureThinLeakDensity(SKBitmap before, SKBitmap after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return 1.0;
        }

        int leaks = 0;
        int samples = 0;
        for (int y = 1; y < after.Height - 1; y++)
        {
            for (int x = 1; x < after.Width - 1; x++)
            {
                double centerAfter = Luminance(after.GetPixel(x, y));
                double centerDelta = centerAfter - Luminance(before.GetPixel(x, y));
                double horizontalDelta = (
                    Luminance(after.GetPixel(x - 1, y)) - Luminance(before.GetPixel(x - 1, y))
                    + Luminance(after.GetPixel(x + 1, y)) - Luminance(before.GetPixel(x + 1, y))) * 0.5;
                double verticalDelta = (
                    Luminance(after.GetPixel(x, y - 1)) - Luminance(before.GetPixel(x, y - 1))
                    + Luminance(after.GetPixel(x, y + 1)) - Luminance(before.GetPixel(x, y + 1))) * 0.5;
                double darkestAxis = Math.Min(horizontalDelta, verticalDelta);
                if (centerAfter > 0.22
                    && centerDelta > -0.08
                    && darkestAxis < -0.14
                    && centerDelta - darkestAxis > 0.16)
                {
                    leaks++;
                }
                samples++;
            }
        }

        return samples > 0 ? (double)leaks / samples : 0.0;
    }

    /// <summary>
    /// Measures how much non-black radiance belongs to filled two-dimensional
    /// regions instead of one-pixel outlines. A real reflected object retains
    /// interior colour; a depth-edge/specular filter produces almost only a
    /// contour and must not be accepted as a readable reflection.
    /// </summary>
    /// <param name="bitmap">Diagnostic radiance image to inspect.</param>
    /// <param name="minimumLuminance">Minimum normalized luminance considered visible radiance.</param>
    /// <returns>The fraction of visible samples whose four cardinal neighbours are also visible.</returns>
    internal static double MeasureRadianceInteriorRatio(
        SKBitmap bitmap,
        double minimumLuminance = 0.025)
    {
        int visible = 0;
        int interior = 0;
        for (int y = 1; y < bitmap.Height - 1; y++)
        {
            for (int x = 1; x < bitmap.Width - 1; x++)
            {
                if (Luminance(bitmap.GetPixel(x, y)) < minimumLuminance)
                {
                    continue;
                }

                visible++;
                interior += Luminance(bitmap.GetPixel(x - 1, y)) >= minimumLuminance
                    && Luminance(bitmap.GetPixel(x + 1, y)) >= minimumLuminance
                    && Luminance(bitmap.GetPixel(x, y - 1)) >= minimumLuminance
                    && Luminance(bitmap.GetPixel(x, y + 1)) >= minimumLuminance
                        ? 1
                        : 0;
            }
        }

        return visible > 0 ? (double)interior / visible : 0.0;
    }

    /// <summary>
    /// Measures whether reflected vegetation forms vertically coherent two-dimensional silhouettes
    /// instead of several unrelated horizontal strips. The water diagnostic supplies the receiver
    /// boundary, so direct scenery above the shoreline cannot inflate the result.
    /// </summary>
    /// <param name="reflection">Reflection-radiance diagnostic captured by the water scenario.</param>
    /// <param name="water">Water-surface diagnostic captured from the same frame and camera.</param>
    /// <returns>
    /// The fraction of vegetation-coloured reflected pixels belonging to each active column's
    /// longest sufficiently thick vertical run; zero for incompatible or empty fixtures.
    /// </returns>
    internal static double MeasureReflectedSilhouetteContinuity(
        SKBitmap reflection,
        SKBitmap water)
    {
        if (reflection.Width != water.Width
            || reflection.Height != water.Height
            || reflection.Width == 0
            || reflection.Height == 0)
        {
            return 0.0;
        }

        int reflectedSilhouettePixels = 0;
        int coherentSilhouettePixels = 0;
        int reflectionBandDepth = Math.Max(8, (int)Math.Ceiling(reflection.Height * 0.22));
        int minimumVerticalRun = Math.Max(3, reflection.Height / 64);
        int maximumAlphaGap = Math.Clamp(reflection.Height / 180, 2, 6);
        for (int x = 0; x < reflection.Width; x++)
        {
            int waterStartY = -1;
            for (int y = 0; y < water.Height; y++)
            {
                SKColor waterPixel = water.GetPixel(x, y);
                if (Math.Max(waterPixel.Red, Math.Max(waterPixel.Green, waterPixel.Blue)) >= 24)
                {
                    waterStartY = y;
                    break;
                }
            }

            if (waterStartY < 0)
            {
                continue;
            }

            int longestVerticalVegetationRun = 0;
            int currentVerticalVegetationRun = 0;
            int currentVerticalSpan = 0;
            int currentAlphaGap = 0;
            int reflectionBandEndY = Math.Min(reflection.Height, waterStartY + reflectionBandDepth);
            for (int y = waterStartY; y < reflectionBandEndY; y++)
            {
                SKColor waterPixel = water.GetPixel(x, y);
                bool onWater = Math.Max(
                    waterPixel.Red,
                    Math.Max(waterPixel.Green, waterPixel.Blue)) >= 24;
                SKColor reflectedPixel = reflection.GetPixel(x, y);
                // The controlled water-reflection framing contains a green tree line against
                // blue open-sky radiance. This chromatic predicate selects the silhouette but
                // does not reward merely increasing its pixel count.
                bool reflectedVegetation = onWater
                    && reflectedPixel.Green >= reflectedPixel.Blue + 16;
                if (reflectedVegetation)
                {
                    reflectedSilhouettePixels++;
                    currentVerticalVegetationRun++;
                    currentVerticalSpan++;
                    currentAlphaGap = 0;
                    if (currentVerticalSpan >= minimumVerticalRun)
                    {
                        longestVerticalVegetationRun = Math.Max(
                            longestVerticalVegetationRun,
                            currentVerticalVegetationRun);
                    }
                }
                else if (currentVerticalVegetationRun > 0
                    && currentAlphaGap < maximumAlphaGap)
                {
                    // Real leaf textures contain small transparent holes and
                    // the half-resolution carrier adds a bounded ripple gap.
                    // Bridge only short alpha gaps; the synthetic separated-
                    // band fixture uses wider gaps and remains rejected.
                    currentAlphaGap++;
                    currentVerticalSpan++;
                }
                else
                {
                    currentVerticalVegetationRun = 0;
                    currentVerticalSpan = 0;
                    currentAlphaGap = 0;
                }
            }

            coherentSilhouettePixels += longestVerticalVegetationRun;
        }

        return reflectedSilhouettePixels > 0
            ? (double)coherentSilhouettePixels / reflectedSilhouettePixels
            : 0.0;
    }

    /// <summary>
    /// Measures block-scale unresolved liquid faces in the physical-water diagnostic. View 12
    /// reserves near-neutral high energy for a vertical liquid face that is simultaneously backed
    /// by shoreline and partial-geometry evidence; the ordinary physical surface cannot reach the
    /// same three-channel floor because its green path-depth channel or blue transmittance remains
    /// lower. Sparse alpha-tested fragments are tolerated, while a complete cell-sized slab is not.
    /// </summary>
    /// <param name="water">Physical water diagnostic captured by the real-game scenario.</param>
    /// <returns>The fraction of visible liquid pixels occupied by unresolved shared-cell faces.</returns>
    internal static double MeasureUnresolvedSharedCellLiquidFaceDensity(SKBitmap water)
    {
        if (water.Width == 0 || water.Height == 0)
        {
            return 0.0;
        }

        int visibleLiquidPixels = 0;
        int unresolvedFacePixels = 0;
        for (int y = 0; y < water.Height; y++)
        {
            for (int x = 0; x < water.Width; x++)
            {
                SKColor pixel = water.GetPixel(x, y);
                if (Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)) < 24)
                {
                    continue;
                }

                visibleLiquidPixels++;
                unresolvedFacePixels += Math.Min(
                    pixel.Red,
                    Math.Min(pixel.Green, pixel.Blue)) >= 176
                    ? 1
                    : 0;
            }
        }

        return visibleLiquidPixels > 0
            ? (double)unresolvedFacePixels / visibleLiquidPixels
            : 0.0;
    }

    /// <summary>
    /// Finds the near-camera local body in the lower band of its dedicated reflected view while
    /// independently rejecting both the smaller upper-frame remote witnesses and a large component
    /// attached to the left first-person overlay band. The raw carrier is cleared to black by the
    /// renderer; alpha is deliberately ignored because PNG readback may normalize cleared alpha.
    /// </summary>
    /// <param name="entityMirror">Native-resolution raw entity-only mirror capture.</param>
    /// <returns>Scale-aware body-presence and first-person-contamination evidence.</returns>
    internal static EntityMirrorLocalBodyAssessment AssessLocalPlayerWorldBody(
        SKBitmap entityMirror)
    {
        if (entityMirror.Width < 64 || entityMirror.Height < 64)
        {
            return default;
        }

        int width = entityMirror.Width;
        int height = entityMirror.Height;
        int imageArea = checked(width * height);
        int significantArea = Math.Max(12, imageArea / 24_000);
        List<EntityMirrorComponent> components = FindEntityMirrorComponents(entityMirror)
            .Where(component => component.Area >= significantArea)
            .ToList();

        int minimumBodyArea = Math.Max(96, imageArea / 500);
        int minimumBodyWidth = Math.Max(8, (int)Math.Ceiling(width * 0.035));
        int minimumBodyHeight = Math.Max(12, (int)Math.Ceiling(height * 0.10));
        EntityMirrorComponent? worldBody = components
            .Where(component =>
            {
                double centroidX = (double)component.SumX / component.Area / width;
                double centroidY = (double)component.SumY / component.Area / height;
                double fill = (double)component.Area / checked(component.Width * component.Height);
                return component.Area >= minimumBodyArea
                    && component.Width >= minimumBodyWidth
                    && component.Height >= minimumBodyHeight
                    && component.Height >= component.Width * 0.75
                    && centroidX is >= 0.22 and <= 0.78
                    && centroidY >= 0.66
                    && component.MinimumX >= width * 0.10
                    && component.MaximumX <= width * 0.90
                    && component.MaximumY >= height * 0.72
                    && fill is >= 0.04 and <= 0.96;
            })
            .OrderByDescending(static component => component.Area)
            .Select(static component => (EntityMirrorComponent?)component)
            .FirstOrDefault();

        int minimumOverlayArea = Math.Max(64, imageArea / 5_000);
        EntityMirrorComponent? firstPersonOverlay = components
            .Where(component =>
            {
                bool attachedToLeftEdge = component.MinimumX <= Math.Max(1, width / 32);
                bool fillsLeftOverlayBand = component.MinimumX <= width * 0.10
                    && component.Width >= width * 0.12;
                return component.Area >= minimumOverlayArea
                    && component.MaximumY >= height * 0.35
                    && (component.Width >= width * 0.10
                        || component.Height >= height * 0.10)
                    && (attachedToLeftEdge || fillsLeftOverlayBand);
            })
            .OrderByDescending(static component => component.Area)
            .Select(static component => (EntityMirrorComponent?)component)
            .FirstOrDefault();

        int bodyArea = worldBody?.Area ?? 0;
        double bodyCentroidX = worldBody.HasValue
            ? (double)worldBody.Value.SumX / bodyArea / width
            : 0.0;
        double bodyCentroidY = worldBody.HasValue
            ? (double)worldBody.Value.SumY / bodyArea / height
            : 0.0;
        double bodyHeightRatio = worldBody.HasValue
            ? (double)worldBody.Value.Height / height
            : 0.0;
        int overlayArea = firstPersonOverlay?.Area ?? 0;
        return new EntityMirrorLocalBodyAssessment(
            true,
            components.Count,
            bodyArea,
            bodyCentroidX,
            bodyCentroidY,
            bodyHeightRatio,
            overlayArea,
            worldBody.HasValue,
            firstPersonOverlay.HasValue,
            worldBody.HasValue && !firstPersonOverlay.HasValue);
    }

    /// <summary>
    /// Exposes significant connected-component morphology for deterministic real-capture fixtures.
    /// This diagnostic uses the exact same black rejection and eight-connectivity as the gate.
    /// </summary>
    /// <param name="entityMirror">Native entity-only carrier.</param>
    /// <returns>Significant components ordered from largest to smallest.</returns>
    internal static IReadOnlyList<EntityMirrorComponentAssessment> AssessEntityMirrorComponents(
        SKBitmap entityMirror)
    {
        if (entityMirror.Width < 1 || entityMirror.Height < 1)
        {
            return [];
        }

        int imageArea = checked(entityMirror.Width * entityMirror.Height);
        int significantArea = Math.Max(12, imageArea / 24_000);
        return FindEntityMirrorComponents(entityMirror)
            .Where(component => component.Area >= significantArea)
            .OrderByDescending(static component => component.Area)
            .Select(component => new EntityMirrorComponentAssessment(
                component.Area,
                component.MinimumX,
                component.MinimumY,
                component.MaximumX,
                component.MaximumY,
                (double)component.SumX / component.Area / entityMirror.Width,
                (double)component.SumY / component.Area / entityMirror.Height,
                (double)component.Area / checked(component.Width * component.Height)))
            .ToArray();
    }

    /// <summary>Extracts eight-connected non-black geometry from an entity-only raw carrier.</summary>
    private static IReadOnlyList<EntityMirrorComponent> FindEntityMirrorComponents(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        bool[] visited = new bool[checked(width * height)];
        Queue<int> pending = new();
        List<EntityMirrorComponent> components = [];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int seed = y * width + x;
                if (visited[seed])
                {
                    continue;
                }

                visited[seed] = true;
                if (!IsEntityMirrorGeometry(bitmap.GetPixel(x, y)))
                {
                    continue;
                }

                pending.Enqueue(seed);
                int area = 0;
                long sumX = 0;
                long sumY = 0;
                int minimumX = x;
                int minimumY = y;
                int maximumX = x;
                int maximumY = y;
                while (pending.Count > 0)
                {
                    int pixelIndex = pending.Dequeue();
                    int pixelX = pixelIndex % width;
                    int pixelY = pixelIndex / width;
                    area++;
                    sumX += pixelX;
                    sumY += pixelY;
                    minimumX = Math.Min(minimumX, pixelX);
                    minimumY = Math.Min(minimumY, pixelY);
                    maximumX = Math.Max(maximumX, pixelX);
                    maximumY = Math.Max(maximumY, pixelY);

                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        int neighborY = pixelY + offsetY;
                        if (neighborY < 0 || neighborY >= height)
                        {
                            continue;
                        }
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                            {
                                continue;
                            }
                            int neighborX = pixelX + offsetX;
                            if (neighborX < 0 || neighborX >= width)
                            {
                                continue;
                            }

                            int neighborIndex = neighborY * width + neighborX;
                            if (visited[neighborIndex])
                            {
                                continue;
                            }
                            visited[neighborIndex] = true;
                            if (IsEntityMirrorGeometry(bitmap.GetPixel(neighborX, neighborY)))
                            {
                                pending.Enqueue(neighborIndex);
                            }
                        }
                    }
                }

                components.Add(new EntityMirrorComponent(
                    area,
                    sumX,
                    sumY,
                    minimumX,
                    minimumY,
                    maximumX,
                    maximumY));
            }
        }

        return components;
    }

    /// <summary>Rejects clear/background black while retaining dark textured entity geometry.</summary>
    private static bool IsEntityMirrorGeometry(SKColor pixel) =>
        Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)) >= 6
        && pixel.Red + pixel.Green + pixel.Blue >= 12;

    /// <summary>
    /// Looks for the two server-pinned source silhouettes and for two distinct, vertically ordered
    /// reflection components below them. The search is intentionally central: the local first-person
    /// arm and its post-final bloom occupy the left edge and cannot satisfy this gate.
    /// </summary>
    /// <param name="source">Raw clean pre-final reflection source.</param>
    /// <param name="reflection">Reflection-radiance diagnostic from the same locked camera.</param>
    /// <returns>Scale-aware source and reflected-component evidence.</returns>
    internal static ReflectionWitnessAssessment AssessCentralReflectionWitnesses(
        SKBitmap source,
        SKBitmap reflection)
    {
        if (source.Width != reflection.Width
            || source.Height != reflection.Height
            || source.Width < 32
            || source.Height < 32)
        {
            return default;
        }

        int humanoidSourcePixels = CountWarmWitnessPixels(
            source,
            0.455,
            0.545,
            0.375,
            0.500);
        int itemSourcePixels = CountWarmWitnessPixels(
            source,
            0.455,
            0.545,
            0.490,
            0.530);
        int directBottomY = FindLastWarmWitnessRow(
            source,
            0.445,
            0.555,
            0.375,
            0.530);
        int hardReflectionStartY = Math.Clamp(
            (int)Math.Ceiling(reflection.Height * 0.523),
            0,
            reflection.Height);
        int reflectionStartY = Math.Max(
            hardReflectionStartY,
            directBottomY >= 0 ? directBottomY + 2 : hardReflectionStartY);
        CentralReflectionPair reflectedPair = FindDistinctCentralReflectionPair(
            reflection,
            0.445,
            0.555,
            reflectionStartY,
            0.545,
            0.630);

        int imageArea = checked(source.Width * source.Height);
        // Provisional until one corrected runtime capture establishes empirical margins. These
        // minima are resolution-scaled and structural; none is derived by lowering a threshold to
        // accept the known-negative 075614 run (which contains zero contrasted pixels here).
        int minimumHumanoidSourcePixels = Math.Max(8, imageArea / 12_000);
        int minimumItemSourcePixels = Math.Max(3, imageArea / 160_000);
        int minimumHumanoidReflectionArea = Math.Max(8, imageArea / 20_000);
        int minimumItemReflectionArea = Math.Max(3, imageArea / 160_000);
        bool physicallyOrdered = reflectedPair.HumanoidCentroidY >= 0.523
            && reflectedPair.ItemCentroidY >= 0.545
            && reflectedPair.ItemCentroidY >= reflectedPair.HumanoidCentroidY + 0.025;
        bool meetsGate = humanoidSourcePixels >= minimumHumanoidSourcePixels
            && itemSourcePixels >= minimumItemSourcePixels
            && reflectedPair.HumanoidArea >= minimumHumanoidReflectionArea
            && reflectedPair.ItemArea >= minimumItemReflectionArea
            && reflectedPair.HasDistinctComponents
            && physicallyOrdered;
        return new ReflectionWitnessAssessment(
            true,
            humanoidSourcePixels,
            itemSourcePixels,
            reflectedPair.HumanoidArea,
            reflectedPair.ItemArea,
            reflectedPair.HumanoidCentroidY,
            reflectedPair.ItemCentroidY,
            meetsGate);
    }

    /// <summary>Counts brown/neutral witness pixels in one normalized clean-source rectangle.</summary>
    private static int CountWarmWitnessPixels(
        SKBitmap bitmap,
        double minimumX,
        double maximumX,
        double minimumY,
        double maximumY)
    {
        (int x0, int x1) = PixelRange(bitmap.Width, minimumX, maximumX);
        (int y0, int y1) = PixelRange(bitmap.Height, minimumY, maximumY);
        int count = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                count += IsWarmWitnessPixel(pixel) ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>Finds the last warm source-witness row used to exclude all direct geometry.</summary>
    /// <param name="bitmap">Raw clean reflection source containing the direct witnesses.</param>
    /// <param name="minimumX">Inclusive normalized left edge.</param>
    /// <param name="maximumX">Exclusive normalized right edge.</param>
    /// <param name="minimumY">Inclusive normalized top edge.</param>
    /// <param name="maximumY">Exclusive normalized bottom edge.</param>
    /// <returns>Last matching absolute image row, or -1 when no witness pixel is present.</returns>
    private static int FindLastWarmWitnessRow(
        SKBitmap bitmap,
        double minimumX,
        double maximumX,
        double minimumY,
        double maximumY)
    {
        (int x0, int x1) = PixelRange(bitmap.Width, minimumX, maximumX);
        (int y0, int y1) = PixelRange(bitmap.Height, minimumY, maximumY);
        int lastRow = -1;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (IsWarmWitnessPixel(bitmap.GetPixel(x, y)))
                {
                    lastRow = y;
                }
            }
        }

        return lastRow;
    }

    /// <summary>Classifies one clean-source pixel as belonging to either deterministic warm witness.</summary>
    /// <param name="pixel">Source colour to classify.</param>
    /// <returns>True for bounded brown or warm-neutral witness radiance.</returns>
    private static bool IsWarmWitnessPixel(SKColor pixel)
    {
        double luminance = Luminance(pixel);
        bool warmOrdered = pixel.Red >= pixel.Green
            && pixel.Green >= pixel.Blue
            && pixel.Red >= pixel.Blue + 6;
        return luminance is >= 0.10 and <= 0.62 && warmOrdered;
    }

    /// <summary>
    /// Labels one shared four-connected contrast map and selects separate components for the
    /// humanoid and item reflection bands. A vertical trail crossing the split retains one
    /// identifier and therefore cannot masquerade as both deterministic witnesses.
    /// </summary>
    /// <param name="bitmap">Reflection-radiance diagnostic.</param>
    /// <param name="minimumX">Inclusive normalized left edge of the shared corridor.</param>
    /// <param name="maximumX">Exclusive normalized right edge of the shared corridor.</param>
    /// <param name="minimumY">First eligible absolute row after all direct source geometry.</param>
    /// <param name="bandSplitY">Normalized boundary between humanoid and item reflections.</param>
    /// <param name="maximumY">Exclusive normalized bottom edge of the item band.</param>
    /// <returns>Strongest distinct pair, or independent same-component evidence marked non-distinct.</returns>
    private static CentralReflectionPair FindDistinctCentralReflectionPair(
        SKBitmap bitmap,
        double minimumX,
        double maximumX,
        int minimumY,
        double bandSplitY,
        double maximumY)
    {
        (int x0, int x1) = PixelRange(bitmap.Width, minimumX, maximumX);
        int y0 = Math.Clamp(minimumY, 0, bitmap.Height);
        int splitY = Math.Clamp(
            (int)Math.Ceiling(bitmap.Height * bandSplitY),
            y0,
            bitmap.Height);
        int y1 = Math.Clamp(
            (int)Math.Ceiling(bitmap.Height * maximumY),
            splitY,
            bitmap.Height);
        int width = x1 - x0;
        int height = y1 - y0;
        if (width <= 0 || height <= 0 || splitY <= y0 || y1 <= splitY)
        {
            return default;
        }

        bool[] candidates = new bool[width * height];
        int lateralOffset = Math.Max(3, bitmap.Width / 24);
        for (int localY = 0; localY < height; localY++)
        {
            int y = y0 + localY;
            for (int localX = 0; localX < width; localX++)
            {
                int x = x0 + localX;
                int leftX = Math.Max(0, x - lateralOffset);
                int rightX = Math.Min(bitmap.Width - 1, x + lateralOffset);
                double carrier = (
                    Luminance(bitmap.GetPixel(leftX, y))
                    + Luminance(bitmap.GetPixel(rightX, y))) * 0.5;
                double center = Luminance(bitmap.GetPixel(x, y));
                candidates[localY * width + localX] = carrier - center >= 0.12
                    && center <= carrier * 0.84;
            }
        }

        bool[] visited = new bool[candidates.Length];
        List<CentralReflectionComponent> components = [];
        Queue<int> queue = new();
        int componentId = 0;
        for (int index = 0; index < candidates.Length; index++)
        {
            if (!candidates[index] || visited[index])
            {
                continue;
            }

            visited[index] = true;
            queue.Enqueue(index);
            int humanoidArea = 0;
            long humanoidSumY = 0L;
            int itemArea = 0;
            long itemSumY = 0L;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int localX = current % width;
                int localY = current / width;
                int absoluteY = y0 + localY;
                if (absoluteY < splitY)
                {
                    humanoidArea++;
                    humanoidSumY += absoluteY;
                }
                else
                {
                    itemArea++;
                    itemSumY += absoluteY;
                }

                Enqueue(localX - 1, localY);
                Enqueue(localX + 1, localY);
                Enqueue(localX, localY - 1);
                Enqueue(localX, localY + 1);
            }

            components.Add(new CentralReflectionComponent(
                componentId++,
                humanoidArea,
                humanoidSumY,
                itemArea,
                itemSumY));
        }

        CentralReflectionComponent largestHumanoid = components
            .OrderByDescending(component => component.HumanoidArea)
            .FirstOrDefault();
        CentralReflectionComponent largestItem = components
            .OrderByDescending(component => component.ItemArea)
            .FirstOrDefault();
        CentralReflectionPair strongestDistinctPair = default;
        long strongestPairProduct = 0L;
        foreach (CentralReflectionComponent humanoid in components)
        {
            if (humanoid.HumanoidArea <= 0)
            {
                continue;
            }

            foreach (CentralReflectionComponent item in components)
            {
                if (item.Id == humanoid.Id || item.ItemArea <= 0)
                {
                    continue;
                }

                long areaProduct = (long)humanoid.HumanoidArea * item.ItemArea;
                if (areaProduct <= strongestPairProduct)
                {
                    continue;
                }

                strongestPairProduct = areaProduct;
                strongestDistinctPair = CreatePair(humanoid, item, true);
            }
        }

        return strongestPairProduct > 0L
            ? strongestDistinctPair
            : CreatePair(
                largestHumanoid,
                largestItem,
                largestHumanoid.HumanoidArea > 0
                    && largestItem.ItemArea > 0
                    && largestHumanoid.Id != largestItem.Id);

        void Enqueue(int localX, int localY)
        {
            if (localX < 0 || localX >= width || localY < 0 || localY >= height)
            {
                return;
            }

            int neighbor = localY * width + localX;
            if (!candidates[neighbor] || visited[neighbor])
            {
                return;
            }

            visited[neighbor] = true;
            queue.Enqueue(neighbor);
        }

        CentralReflectionPair CreatePair(
            CentralReflectionComponent humanoid,
            CentralReflectionComponent item,
            bool hasDistinctComponents)
        {
            return new CentralReflectionPair(
                humanoid.HumanoidArea,
                humanoid.HumanoidArea > 0
                    ? (double)humanoid.HumanoidSumY / humanoid.HumanoidArea / bitmap.Height
                    : 0.0,
                item.ItemArea,
                item.ItemArea > 0
                    ? (double)item.ItemSumY / item.ItemArea / bitmap.Height
                    : 0.0,
                hasDistinctComponents);
        }
    }

    /// <summary>Converts an inclusive-exclusive normalized interval into a non-empty pixel range.</summary>
    private static (int Start, int End) PixelRange(
        int extent,
        double normalizedStart,
        double normalizedEnd)
    {
        int start = Math.Clamp((int)Math.Floor(extent * normalizedStart), 0, extent - 1);
        int end = Math.Clamp((int)Math.Ceiling(extent * normalizedEnd), start + 1, extent);
        return (start, end);
    }

    /// <summary>
    /// Validates voxel Bounce Mask and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidateVoxelBounceMask(string log)
    {
        Match match = VoxelBounceMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            return ["voxel-bounce diagnostic capture was not logged"];
        }

        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? bitmap = File.Exists(path) ? SKBitmap.Decode(File.ReadAllBytes(path)) : null;
        if (bitmap is null)
        {
            return [$"voxel-bounce diagnostic capture could not be decoded: '{path}'"];
        }

        int visible = 0;
        int warm = 0;
        int samples = 0;
        double sum = 0.0;
        double squaredSum = 0.0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                double energy = Luminance(pixel);
                visible += energy >= 0.008 ? 1 : 0;
                warm += pixel.Red >= pixel.Blue + 2 && energy >= 0.008 ? 1 : 0;
                sum += energy;
                squaredSum += energy * energy;
                samples++;
            }
        }

        double visibleRatio = (double)visible / samples;
        double warmRatio = (double)warm / samples;
        double mean = sum / samples;
        double deviation = Math.Sqrt(Math.Max(0.0, squaredSum / samples - mean * mean));
        Console.WriteLine(
            $"Voxel bounce mask: visible={visibleRatio:P1}, warm={warmRatio:P1}, "
            + $"mean={mean:0.000}, deviation={deviation:0.000}.");

        List<string> failures = [];
        if (visibleRatio < 0.01)
        {
            failures.Add($"off-screen voxel bounce is visually absent ({visibleRatio:P2} of sampled pixels)");
        }
        if (deviation < 0.006)
        {
            failures.Add($"voxel bounce lacks localized spatial structure (deviation {deviation:0.000})");
        }
        if (warmRatio < 0.005)
        {
            failures.Add($"voxel bounce does not preserve the controlled warm source ({warmRatio:P2})");
        }

        return failures;
    }

    /// <summary>
    /// Validates voxel Reflection Mask and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidateVoxelReflectionMask(string log)
    {
        Match match = VoxelReflectionMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            return ["voxel-reflection diagnostic capture was not logged"];
        }

        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? bitmap = File.Exists(path) ? SKBitmap.Decode(File.ReadAllBytes(path)) : null;
        if (bitmap is null)
        {
            return [$"voxel-reflection diagnostic capture could not be decoded: '{path}'"];
        }

        int visible = 0;
        int samples = 0;
        double sum = 0.0;
        double squaredSum = 0.0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                double energy = Luminance(bitmap.GetPixel(x, y));
                visible += energy >= 0.008 ? 1 : 0;
                sum += energy;
                squaredSum += energy * energy;
                samples++;
            }
        }

        double visibleRatio = (double)visible / samples;
        double mean = sum / samples;
        double deviation = Math.Sqrt(Math.Max(0.0, squaredSum / samples - mean * mean));
        double interiorRatio = MeasureRadianceInteriorRatio(bitmap, 0.008);
        Console.WriteLine(
            $"Voxel reflection mask: visible={visibleRatio:P1}, mean={mean:0.000}, "
            + $"deviation={deviation:0.000}, filled radiance={interiorRatio:P1}.");

        List<string> failures = [];
        if (visibleRatio < 0.005)
        {
            failures.Add($"off-screen voxel reflections are visually absent ({visibleRatio:P2} of sampled pixels)");
        }
        if (deviation < 0.004)
        {
            failures.Add($"voxel reflections lack localized spatial structure (deviation {deviation:0.000})");
        }
        if (interiorRatio < 0.18)
        {
            failures.Add(
                $"voxel reflections contain outlines instead of readable reflected surfaces "
                + $"({interiorRatio:P1} filled radiance)");
        }

        return failures;
    }

    /// <summary>
    /// Validates reflection Mask and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidateReflectionMask(string log)
    {
        Match match = ReflectionMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            return ["reflection diagnostic capture was not logged"];
        }

        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? bitmap = File.Exists(path) ? SKBitmap.Decode(File.ReadAllBytes(path)) : null;
        if (bitmap is null)
        {
            return [$"reflection diagnostic capture could not be decoded: '{path}'"];
        }

        int reflected = 0;
        int samples = 0;
        double sum = 0.0;
        double squaredSum = 0.0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                double energy = Luminance(bitmap.GetPixel(x, y));
                reflected += energy >= 0.025 ? 1 : 0;
                sum += energy;
                squaredSum += energy * energy;
                samples++;
            }
        }

        double ratio = (double)reflected / samples;
        double mean = sum / samples;
        double deviation = Math.Sqrt(Math.Max(0.0, squaredSum / samples - mean * mean));
        double interiorRatio = MeasureRadianceInteriorRatio(bitmap);
        double? silhouetteContinuity = null;
        double? unresolvedSharedCellFaceDensity = null;
        string? silhouetteFailure = null;
        ReflectionWitnessAssessment? witnessEvidence = null;
        string? witnessFailure = null;
        EntityMirrorLocalBodyAssessment? localBodyEvidence = null;
        string? localBodyFailure = null;
        if (log.Contains("Water reflection camera applied", StringComparison.OrdinalIgnoreCase))
        {
            Match waterMatch = WaterMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
            if (!waterMatch.Success)
            {
                silhouetteFailure = "water-reflection silhouette validation lacks its water diagnostic capture";
            }
            else
            {
                using SKBitmap? water = DecodeOptionalLoggedImage(waterMatch.Groups[1].Value);
                if (water is null || water.Width != bitmap.Width || water.Height != bitmap.Height)
                {
                    silhouetteFailure = "water-reflection silhouette validation could not pair reflection and water diagnostics";
                }
                else
                {
                    silhouetteContinuity = MeasureReflectedSilhouetteContinuity(bitmap, water);
                    unresolvedSharedCellFaceDensity =
                        MeasureUnresolvedSharedCellLiquidFaceDensity(water);
                }
            }

            Match sourceMatch = RawReflectionSourceRegex().Matches(log).Cast<Match>().LastOrDefault()
                ?? Match.Empty;
            if (!sourceMatch.Success)
            {
                witnessFailure = "water-reflection entity validation lacks its raw clean reflection source";
            }
            else
            {
                using SKBitmap? source = DecodeOptionalLoggedImage(sourceMatch.Groups[1].Value);
                if (source is null || source.Width != bitmap.Width || source.Height != bitmap.Height)
                {
                    witnessFailure = "water-reflection entity validation could not pair source and reflection diagnostics";
                }
                else
                {
                    witnessEvidence = AssessCentralReflectionWitnesses(source, bitmap);
                }
            }

            Match entityMirrorMatch = RawEntityMirrorRegex().Matches(log).Cast<Match>().LastOrDefault()
                ?? Match.Empty;
            if (!entityMirrorMatch.Success)
            {
                localBodyFailure = "water-reflection local-player validation lacks its dedicated raw entity-only mirror";
            }
            else
            {
                using SKBitmap? entityMirror = DecodeOptionalLoggedImage(entityMirrorMatch.Groups[1].Value);
                if (entityMirror is null)
                {
                    localBodyFailure = "water-reflection dedicated local-player raw entity mirror could not be decoded";
                }
                else
                {
                    localBodyEvidence = AssessLocalPlayerWorldBody(entityMirror);
                    if (!localBodyEvidence.Value.Compatible)
                    {
                        localBodyFailure = "water-reflection dedicated local-player raw entity mirror is too small for structural validation";
                    }
                }
            }
        }

        Console.WriteLine(
            $"Reflection mask: visible={ratio:P1}, mean={mean:0.000}, "
            + $"deviation={deviation:0.000}, filled radiance={interiorRatio:P1}.");
        if (silhouetteContinuity.HasValue)
        {
            Console.WriteLine(
                $"Water reflection silhouette: vertical continuity={silhouetteContinuity.Value:P1}.");
        }
        if (unresolvedSharedCellFaceDensity.HasValue)
        {
            Console.WriteLine(
                $"Water shared-cell geometry: unresolved face density="
                + $"{unresolvedSharedCellFaceDensity.Value:P2}.");
        }
        if (witnessEvidence.HasValue)
        {
            ReflectionWitnessAssessment evidence = witnessEvidence.Value;
            Console.WriteLine(
                $"Water reflection witnesses (provisional structural gate): "
                + $"source humanoid={evidence.HumanoidSourcePixels}, item={evidence.ItemSourcePixels}; "
                + $"reflected humanoid area={evidence.HumanoidReflectionArea} at y={evidence.HumanoidReflectionCentroidY:0.000}, "
                + $"item area={evidence.ItemReflectionArea} at y={evidence.ItemReflectionCentroidY:0.000}.");
        }
        if (localBodyEvidence.HasValue)
        {
            EntityMirrorLocalBodyAssessment evidence = localBodyEvidence.Value;
            Console.WriteLine(
                $"Local-player entity mirror: components={evidence.SignificantComponentCount}, "
                + $"world-body area={evidence.WorldBodyArea}, centroid=({evidence.WorldBodyCentroidX:0.000},"
                + $"{evidence.WorldBodyCentroidY:0.000}), height={evidence.WorldBodyHeightRatio:P1}; "
                + $"first-person overlay area={evidence.FirstPersonOverlayArea}.");
        }

        List<string> failures = [];
        if (silhouetteFailure is not null)
        {
            failures.Add(silhouetteFailure);
        }
        else if (silhouetteContinuity.HasValue && silhouetteContinuity.Value < 0.65)
        {
            failures.Add(
                $"water reflections fragment tree silhouettes into horizontal strips "
                + $"({silhouetteContinuity.Value:P1} vertical continuity, expected at least 65%)");
        }
        if (unresolvedSharedCellFaceDensity.HasValue
            && unresolvedSharedCellFaceDensity.Value > 0.01)
        {
            failures.Add(
                "water forms block-scale faces through non-full shoreline geometry "
                + $"({unresolvedSharedCellFaceDensity.Value:P2} unresolved diagnostic pixels, "
                + "expected at most 1%)");
        }
        if (witnessFailure is not null)
        {
            failures.Add(witnessFailure);
        }
        else if (witnessEvidence.HasValue && !witnessEvidence.Value.MeetsProvisionalGate)
        {
            ReflectionWitnessAssessment evidence = witnessEvidence.Value;
            failures.Add(
                "water reflection does not contain both server-pinned entity witnesses below their clean-source silhouettes "
                + $"(provisional structural gate: source {evidence.HumanoidSourcePixels}/{evidence.ItemSourcePixels}, "
                + $"reflected areas {evidence.HumanoidReflectionArea}/{evidence.ItemReflectionArea}, "
                + $"centroid y {evidence.HumanoidReflectionCentroidY:0.000}/{evidence.ItemReflectionCentroidY:0.000})");
        }
        if (localBodyFailure is not null)
        {
            failures.Add(localBodyFailure);
        }
        else if (localBodyEvidence.HasValue && !localBodyEvidence.Value.MeetsGate)
        {
            EntityMirrorLocalBodyAssessment evidence = localBodyEvidence.Value;
            failures.Add(
                "entity mirror does not contain a clean local-player world body "
                + $"(body area={evidence.WorldBodyArea}, centroid="
                + $"{evidence.WorldBodyCentroidX:0.000}/{evidence.WorldBodyCentroidY:0.000}, "
                + $"height={evidence.WorldBodyHeightRatio:P1}, "
                + $"first-person overlay area={evidence.FirstPersonOverlayArea})");
        }
        // A few isolated hit pixels are the classic SSR edge/noise failure,
        // not a readable reflection. Require a material area large enough to
        // be visible at normal gameplay scale.
        // The automated water framing exposes roughly one third of the image as
        // lake. A 2% threshold accepted the old far-shore-only strip, even though
        // the near water had no environment reflection at all. Require broad
        // planar coverage so that regression can no longer report a false pass.
        if (ratio < 0.12)
        {
            failures.Add($"screen-space reflections are visually absent ({ratio:P2} of sampled pixels)");
        }
        if (deviation < 0.02)
        {
            failures.Add($"reflection diagnostic lacks spatial structure (deviation {deviation:0.000})");
        }
        if (interiorRatio < 0.22)
        {
            failures.Add(
                $"screen-space reflections contain outlines instead of readable reflected surfaces "
                + $"({interiorRatio:P1} filled radiance)");
        }

        return failures;
    }

    /// <summary>
    /// Validates wetness Mask and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidateWetnessMask(string log)
    {
        Match wetnessMatch = WetnessMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        Match normalMatch = NormalMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!wetnessMatch.Success || !normalMatch.Success)
        {
            return ["wetness/normal diagnostic captures were not both logged"];
        }

        using SKBitmap? wetness = DecodeOptionalLoggedImage(wetnessMatch.Groups[1].Value);
        using SKBitmap? normal = DecodeOptionalLoggedImage(normalMatch.Groups[1].Value);
        if (wetness is null || normal is null
            || wetness.Width != normal.Width
            || wetness.Height != normal.Height)
        {
            return ["wetness/normal diagnostic captures could not be paired"];
        }

        int geometry = 0;
        int wet = 0;
        int rainExposed = 0;
        int shelteredOrVertical = 0;
        double weatherSum = 0.0;
        for (int y = 0; y < wetness.Height; y += 2)
        {
            for (int x = 0; x < wetness.Width; x += 2)
            {
                SKColor normalPixel = normal.GetPixel(x, y);
                bool hasGeometry = normalPixel.Red + normalPixel.Green + normalPixel.Blue >= 96;
                if (!hasGeometry)
                {
                    continue;
                }

                SKColor wetnessPixel = wetness.GetPixel(x, y);
                double wetAmount = wetnessPixel.Red / 255.0;
                double exposure = wetnessPixel.Green / 255.0;
                geometry++;
                wet += wetAmount >= 0.12 ? 1 : 0;
                rainExposed += exposure >= 0.25 ? 1 : 0;
                shelteredOrVertical += exposure <= 0.04 ? 1 : 0;
                weatherSum += wetnessPixel.Blue / 255.0;
            }
        }

        double wetRatio = geometry > 0 ? (double)wet / geometry : 0.0;
        double exposedRatio = geometry > 0 ? (double)rainExposed / geometry : 0.0;
        double dryGeometryRatio = geometry > 0 ? (double)shelteredOrVertical / geometry : 0.0;
        double weatherMean = geometry > 0 ? weatherSum / geometry : 0.0;
        Console.WriteLine(
            $"Rain wetness mask: wet={wetRatio:P1}, exposed={exposedRatio:P1}, "
            + $"sheltered/vertical={dryGeometryRatio:P1}, smoothed weather={weatherMean:0.000}.");

        List<string> failures = [];
        if (geometry == 0)
        {
            failures.Add("wetness frame contains no valid G-buffer geometry");
        }
        if (wetRatio < 0.01 || exposedRatio < 0.01)
        {
            failures.Add($"rain-exposed wet surfaces are visually absent (wet {wetRatio:P1}, exposed {exposedRatio:P1})");
        }
        if (wetRatio > 0.75 || dryGeometryRatio < 0.05)
        {
            failures.Add($"wetness is global instead of localized (wet {wetRatio:P1}, dry/vertical {dryGeometryRatio:P1})");
        }
        if (weatherMean < 0.35)
        {
            failures.Add($"CPU weather smoothing did not accumulate during forced rain ({weatherMean:0.000})");
        }

        return failures;
    }

    /// <summary>
    /// Executes the decode Optional Logged Image step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="loggedPath">Filesystem location constrained to the isolated test sandbox.</param>
    /// <returns>The decode Optional Logged Image result consumed by the caller&apos;s assertion.</returns>
    private static SKBitmap? DecodeOptionalLoggedImage(string loggedPath)
    {
        string path = loggedPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        return File.Exists(path) ? SKBitmap.Decode(File.ReadAllBytes(path)) : null;
    }

    /// <summary>
    /// Validates shadow Mask and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="shadowValidation">The shadow Validation input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidateShadowMask(
        string log,
        ShadowValidation shadowValidation)
    {
        Match match = ShadowMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            return ["voxel shadow-mask capture was not logged"];
        }

        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? bitmap = File.Exists(path) ? SKBitmap.Decode(File.ReadAllBytes(path)) : null;
        if (bitmap is null)
        {
            return [$"voxel shadow-mask capture could not be decoded: '{path}'"];
        }

        int projected = 0;
        int samples = 0;
        double sum = 0.0;
        double squaredSum = 0.0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                double shadowEnergy = SelectShadowDebugEnergy(pixel, shadowValidation);
                projected += shadowEnergy >= 0.58 ? 1 : 0;
                sum += shadowEnergy;
                squaredSum += shadowEnergy * shadowEnergy;
                samples++;
            }
        }

        double ratio = (double)projected / samples;
        double mean = sum / samples;
        double deviation = Math.Sqrt(Math.Max(0.0, squaredSum / samples - mean * mean));
        Console.WriteLine($"Shadow mask: projected={ratio:P1}, mean={mean:0.000}, deviation={deviation:0.000}.");

        List<string> failures = [];
        if (shadowValidation == ShadowValidation.Informational)
        {
            return failures;
        }

        if (shadowValidation == ShadowValidation.SunProjected)
        {
            failures.AddRange(ValidateGeometryCoverage(log, 0.55));
            failures.AddRange(ValidateSunReceiverCoverage(log, 0.30, 0.10));
        }

        if (shadowValidation == ShadowValidation.CameraAligned)
        {
            if (ratio > 0.02 || mean > 0.03)
            {
                failures.Add(
                    $"camera-aligned held light generated false self-occlusion "
                    + $"(projected={ratio:P1}, mean={mean:0.000})");
            }

            return failures;
        }

        if (ratio < 0.005)
        {
            failures.Add($"projected-shadow mask is visually absent ({ratio:P2} of sampled pixels)");
        }
        double maximumProjectedRatio = shadowValidation == ShadowValidation.SunProjected
            ? 0.95
            : 0.60;
        if (ratio > maximumProjectedRatio)
        {
            failures.Add($"projected-shadow mask is global instead of localized ({ratio:P1} of sampled pixels)");
        }
        if (deviation < 0.06)
        {
            failures.Add($"projected-shadow mask lacks spatial contrast (deviation {deviation:0.000})");
        }

        return failures;
    }

    /// <summary>Selects the shadow diagnostic channel owned by one validation mode.</summary>
    /// <param name="pixel">RGB debug-shadow sample.</param>
    /// <param name="shadowValidation">Scenario-specific shadow contract.</param>
    /// <returns>Normalized aggregate point, projected sun or camera-aligned shadow energy.</returns>
    internal static double SelectShadowDebugEnergy(
        SKColor pixel,
        ShadowValidation shadowValidation)
    {
        byte encoded = shadowValidation switch
        {
            ShadowValidation.CameraAligned => pixel.Blue,
            ShadowValidation.SunProjected => pixel.Green,
            _ => pixel.Red
        };
        return encoded / 255.0;
    }

    /// <summary>
    /// Validates geometry Coverage and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="minimumRatio">The minimum Ratio input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidateGeometryCoverage(string log, double minimumRatio)
    {
        Match match = NormalMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!match.Success)
        {
            return ["normal-mask capture was not logged for exterior geometry coverage"];
        }

        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? bitmap = File.Exists(path) ? SKBitmap.Decode(File.ReadAllBytes(path)) : null;
        if (bitmap is null)
        {
            return [$"normal-mask capture could not be decoded: '{path}'"];
        }

        int geometry = 0;
        int samples = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                geometry += Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)) >= 20 ? 1 : 0;
                samples++;
            }
        }

        double ratio = (double)geometry / samples;
        Console.WriteLine($"Exterior geometry coverage: {ratio:P1}.");
        return ratio >= minimumRatio
            ? []
            : [$"exterior camera is dominated by sky instead of shadow receivers ({ratio:P1} geometry)"];
    }

    /// <summary>
    /// Validates sun Receiver Coverage and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="minimumGroundRatio">The minimum Ground Ratio input used to configure this deterministic test path.</param>
    /// <param name="minimumShadowedGroundRatio">The minimum Shadowed Ground Ratio input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    private static IReadOnlyList<string> ValidateSunReceiverCoverage(
        string log,
        double minimumGroundRatio,
        double minimumShadowedGroundRatio)
    {
        Match normalMatch = NormalMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        Match shadowMatch = ShadowMaskRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!normalMatch.Success || !shadowMatch.Success)
        {
            return ["normal/shadow captures were not logged for the long-range ground receiver"];
        }

        string normalPath = normalMatch.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        string shadowPath = shadowMatch.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? normal = File.Exists(normalPath) ? SKBitmap.Decode(File.ReadAllBytes(normalPath)) : null;
        using SKBitmap? shadow = File.Exists(shadowPath) ? SKBitmap.Decode(File.ReadAllBytes(shadowPath)) : null;
        if (normal is null || shadow is null || normal.Width != shadow.Width || normal.Height != shadow.Height)
        {
            return ["normal/shadow captures could not be paired for the long-range ground receiver"];
        }

        SunReceiverCoverageAssessment assessment = MeasureSunReceiverCoverage(
            normal,
            shadow,
            targetRadiusRatio: 0.18);
        Console.WriteLine(
            $"Exterior ground receiver: coverage={assessment.GroundCoverage:P1}, "
            + $"shadowed={assessment.ShadowedGroundRatio:P1}, "
            + $"target samples={assessment.TargetGroundSamples}, "
            + $"target shadowed={assessment.TargetShadowedGroundRatio:P1}.");

        List<string> failures = [];
        if (assessment.GroundCoverage < minimumGroundRatio)
        {
            failures.Add(
                $"exterior frame lacks receiving ground ({assessment.GroundCoverage:P1}, "
                + $"expected {minimumGroundRatio:P0})");
        }
        bool hasPhysicalTarget = log.Contains(
            "physical shadow target=",
            StringComparison.OrdinalIgnoreCase);
        double evaluatedShadowedRatio = hasPhysicalTarget
            ? assessment.TargetShadowedGroundRatio
            : assessment.ShadowedGroundRatio;
        if (hasPhysicalTarget && assessment.TargetGroundSamples < 128)
        {
            failures.Add(
                $"physical roof-shadow target lacks enough receiving ground "
                + $"({assessment.TargetGroundSamples} samples)");
        }
        if (evaluatedShadowedRatio < minimumShadowedGroundRatio)
        {
            failures.Add(
                hasPhysicalTarget
                    ? $"roof shadow misses its physically projected ground target "
                        + $"({evaluatedShadowedRatio:P1}, expected {minimumShadowedGroundRatio:P0})"
                    : $"roof shadow does not reach enough of the distant ground receiver "
                        + $"({evaluatedShadowedRatio:P1}, expected {minimumShadowedGroundRatio:P0})");
        }

        return failures;
    }

    /// <summary>
    /// Measures all upward receivers and a camera-centered target disc. Exterior-roof probes aim
    /// the view directly at the similar-triangle sun/ground intersection, so the disc represents
    /// the physically predicted footprint rather than an arbitrary portion of the landscape.
    /// </summary>
    /// <param name="normal">World-normal diagnostic.</param>
    /// <param name="shadow">Solar-shadow diagnostic whose green channel stores occlusion.</param>
    /// <param name="targetRadiusRatio">Disc radius relative to the shorter image edge.</param>
    /// <returns>Global framing plus localized receiver evidence.</returns>
    internal static SunReceiverCoverageAssessment MeasureSunReceiverCoverage(
        SKBitmap normal,
        SKBitmap shadow,
        double targetRadiusRatio)
    {
        ArgumentNullException.ThrowIfNull(normal);
        ArgumentNullException.ThrowIfNull(shadow);
        if (normal.Width != shadow.Width || normal.Height != shadow.Height)
        {
            throw new ArgumentException("Normal and shadow diagnostics must have identical dimensions.");
        }
        if (!double.IsFinite(targetRadiusRatio) || targetRadiusRatio is <= 0.0 or > 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRadiusRatio));
        }

        int ground = 0;
        int shadowedGround = 0;
        int targetGround = 0;
        int targetShadowedGround = 0;
        int samples = 0;
        double centerX = (normal.Width - 1) * 0.5;
        double centerY = (normal.Height - 1) * 0.5;
        double targetRadius = Math.Min(normal.Width, normal.Height) * targetRadiusRatio;
        double targetRadiusSquared = targetRadius * targetRadius;
        for (int y = 0; y < normal.Height; y += 2)
        {
            for (int x = 0; x < normal.Width; x += 2)
            {
                SKColor encodedNormal = normal.GetPixel(x, y);
                // The debug normal contains the authored tangent-space normal,
                // not only the block-face normal. Classify the macro surface
                // with a tolerant upward cone so rough snow/soil still counts
                // as horizontal receiving terrain while vertical walls remain
                // excluded by the channel-dominance checks.
                bool upwardReceiver = encodedNormal.Green >= 175
                    && encodedNormal.Green >= encodedNormal.Red + 20
                    && encodedNormal.Green >= encodedNormal.Blue + 20;
                if (upwardReceiver)
                {
                    bool shadowed = shadow.GetPixel(x, y).Green >= 148;
                    ground++;
                    shadowedGround += shadowed ? 1 : 0;
                    double deltaX = x - centerX;
                    double deltaY = y - centerY;
                    if (deltaX * deltaX + deltaY * deltaY <= targetRadiusSquared)
                    {
                        targetGround++;
                        targetShadowedGround += shadowed ? 1 : 0;
                    }
                }

                samples++;
            }
        }

        return new SunReceiverCoverageAssessment(
            GroundCoverage: samples > 0 ? (double)ground / samples : 0.0,
            ShadowedGroundRatio: ground > 0 ? (double)shadowedGround / ground : 0.0,
            TargetGroundSamples: targetGround,
            TargetShadowedGroundRatio: targetGround > 0
                ? (double)targetShadowedGround / targetGround
                : 0.0);
    }

    /// <summary>
    /// Validates three pre-impact fields and one response around exact stone and arrow contacts.
    /// A bounded quadratic temporal counterfactual removes both wind velocity and curvature before
    /// equal-world-area contact/control comparison accepts a localized excess.
    /// </summary>
    /// <param name="log">Merged runtime log containing saved paths and projected anchors.</param>
    /// <returns>Missing, incompatible, or non-responsive projectile-field diagnostics.</returns>
    internal static IReadOnlyList<string> ValidateProjectileSurfaceFieldDeltas(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<string> failures = [];
        List<ProjectileSurfaceDeltaAssessment> assessments = [];
        foreach (string kind in new[] { "stone", "arrow" })
        {
            string earlierLabel = $"projectile-{kind}-baseline-earlier-surface-field";
            string priorLabel = $"projectile-{kind}-baseline-prior-surface-field";
            string baselineLabel = $"projectile-{kind}-baseline-surface-field";
            string responseLabel = $"projectile-{kind}-surface-field";
            if (!TryFindSavedEffectPath(log, earlierLabel, out string earlierPath)
                || !TryFindSavedEffectPath(log, priorLabel, out string priorPath)
                || !TryFindSavedEffectPath(log, baselineLabel, out string baselinePath)
                || !TryFindSavedEffectPath(log, responseLabel, out string responsePath))
            {
                failures.Add(
                    $"projectile {kind} image validation: ordered earlier/prior/baseline/response surface-field sequence is missing");
                continue;
            }

            int[] sourceSequences = ProjectileSynchronizedSurfaceCaptureRegex().Matches(log)
                .Cast<Match>()
                .Where(match => string.Equals(
                    match.Groups[1].Value,
                    kind,
                    StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        match.Groups[3].Value,
                        kind,
                        StringComparison.OrdinalIgnoreCase))
                .Select(match => int.Parse(
                    match.Groups[2].Value,
                    CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray();
            if (sourceSequences.Length != 1)
            {
                failures.Add(
                    $"projectile {kind} image validation: unique synchronized callback sequence is missing or ambiguous");
                continue;
            }

            int sourceSequence = sourceSequences[0];
            ProjectileWavePacket[] packets = ProjectileSurfaceImpactPacketRegex().Matches(log)
                .Cast<Match>()
                .Where(match => int.Parse(
                    match.Groups[1].Value,
                    CultureInfo.InvariantCulture) == sourceSequence)
                .Select(match => new ProjectileWavePacket(
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    ParseInvariant(match.Groups[3].Value),
                    ParseInvariant(match.Groups[4].Value)))
                .Distinct()
                .ToArray();
            if (packets.Length != 1 || packets[0].PacketSequence != sourceSequence)
            {
                failures.Add(
                    $"projectile {kind} image validation: unique wave packet for synchronized callback "
                    + $"sequence {sourceSequence} is missing or inconsistent");
                continue;
            }

            ProjectileWavePacket packet = packets[0];
            Match? anchor = ProjectileImpactScreenAnchorRegex().Matches(log)
                .Cast<Match>()
                .FirstOrDefault(match => int.Parse(
                        match.Groups[1].Value,
                        CultureInfo.InvariantCulture) == sourceSequence
                    && string.Equals(
                        match.Groups[2].Value,
                        kind,
                        StringComparison.OrdinalIgnoreCase));
            if (anchor is null)
            {
                failures.Add(
                    $"projectile {kind} image validation: exact screen anchor for synchronized callback "
                    + $"sequence {sourceSequence} is missing");
                continue;
            }

            Match[] surfaceFrames = ProjectileImpactSurfaceFrameRegex().Matches(log)
                .Cast<Match>()
                .Where(match => int.Parse(
                        match.Groups[1].Value,
                        CultureInfo.InvariantCulture) == sourceSequence
                    && string.Equals(
                        match.Groups[2].Value,
                        kind,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Match? contactFrameMatch = surfaceFrames.FirstOrDefault(static match =>
                string.Equals(match.Groups[3].Value, "contact", StringComparison.OrdinalIgnoreCase));
            if (contactFrameMatch is null)
            {
                failures.Add(
                    $"projectile {kind} image validation: world-plane surface frame is missing");
                continue;
            }

            if (!File.Exists(earlierPath)
                || !File.Exists(priorPath)
                || !File.Exists(baselinePath)
                || !File.Exists(responsePath))
            {
                failures.Add(
                    $"projectile {kind} image validation: field PNG missing: "
                    + $"earlier='{earlierPath}', prior='{priorPath}', baseline='{baselinePath}', "
                    + $"response='{responsePath}'");
                continue;
            }

            using SKBitmap? earlier = SKBitmap.Decode(File.ReadAllBytes(earlierPath));
            using SKBitmap? prior = SKBitmap.Decode(File.ReadAllBytes(priorPath));
            using SKBitmap? baseline = SKBitmap.Decode(File.ReadAllBytes(baselinePath));
            using SKBitmap? response = SKBitmap.Decode(File.ReadAllBytes(responsePath));
            if (earlier is null || prior is null || baseline is null || response is null)
            {
                failures.Add($"projectile {kind} image validation: field PNG could not be decoded");
                continue;
            }

            if (!TryResolveQuadraticCounterfactualWeights(
                    earlierPath,
                    priorPath,
                    baselinePath,
                    responsePath,
                    out TemporalCounterfactualWeights counterfactualWeights))
            {
                failures.Add(
                    $"projectile {kind} image validation: capture timestamps do not form a stable "
                    + "earlier/prior/baseline/response timeline");
                continue;
            }

            double screenX = ParseInvariant(anchor.Groups[3].Value);
            double screenY = ParseInvariant(anchor.Groups[4].Value);
            int loggedWidth = int.Parse(
                anchor.Groups[5].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            int loggedHeight = int.Parse(
                anchor.Groups[6].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            if (loggedWidth != baseline.Width || loggedHeight != baseline.Height)
            {
                failures.Add(
                    $"projectile {kind} image validation: anchor viewport {loggedWidth}x{loggedHeight} "
                    + $"does not match field {baseline.Width}x{baseline.Height}");
                continue;
            }


            ProjectileSurfaceFrame contactFrame = ParseProjectileSurfaceFrame(contactFrameMatch);
            ProjectileSurfaceFrame[] controlFrames = surfaceFrames
                .Where(static match => match.Groups[3].Value.StartsWith(
                    "control-",
                    StringComparison.OrdinalIgnoreCase))
                .GroupBy(
                    static match => match.Groups[3].Value,
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .Select(ParseProjectileSurfaceFrame)
                .ToArray();
            if (Vector2.Distance(
                    contactFrame.Center,
                    new Vector2((float)screenX, (float)screenY)) > 2.0f)
            {
                failures.Add(
                    $"projectile {kind} image validation: surface frame does not match exact screen anchor");
                continue;
            }

            ProjectileSurfaceDeltaAssessment assessment = MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                response,
                counterfactualWeights,
                packet,
                contactFrame,
                controlFrames);
            assessments.Add(assessment);
            double contactPixelsPerWorld = MinimumPixelsPerWorld(contactFrame);
            double controlPixelsPerWorld = controlFrames.Length > 0
                ? controlFrames.Min(MinimumPixelsPerWorld)
                : contactPixelsPerWorld;
            double pixelsPerWavelength = packet.WavelengthMetres
                * Math.Min(contactPixelsPerWorld, controlPixelsPerWorld);
            double envelopeSigmaMetres = Math.Max(packet.WavelengthMetres * 0.35, 0.05);
            double envelopeFwhmPixels = 2.354820045
                * envelopeSigmaMetres
                * contactPixelsPerWorld;
            string frequencyMode = pixelsPerWavelength >= 2.0
                ? "matched-carrier"
                : envelopeFwhmPixels >= 2.0
                    ? "anti-aliased-normal-envelope"
                    : "unresolved";
            Console.WriteLine(
                $"Projectile {kind} field delta: anchor=({screenX:0.0},{screenY:0.0}), "
                + $"radius={assessment.RadiusPixels}px, samples={assessment.LocalSamples}/"
                + $"{assessment.BackgroundSamples}, peak-height={assessment.PeakHeightDeltaMetres * 1000.0:0.00}mm, "
                + $"local-rms={assessment.LocalSignalRmsMetres * 1000.0:0.000}mm, "
                + $"annulus-rms={assessment.BackgroundSignalRmsMetres * 1000.0:0.000}mm, "
                + $"raw-localization={assessment.LocalizationRatio:0.00}x, "
                + $"frequency-wave={assessment.CoherentWaveRmsMetres * 1000.0:0.000}mm, "
                + $"same-depth-frequency-control={assessment.ControlWaveRmsMetres * 1000.0:0.000}mm, "
                + $"wave-localization={assessment.WaveLocalizationRatio:0.00}x, "
                + $"frequency-mode={frequencyMode}, packet-sequence={packet.PacketSequence}, "
                + $"amplitude={packet.AmplitudeMetres * 1000.0:0.00}mm, "
                + $"wavelength={packet.WavelengthMetres * 1000.0:0.00}mm/"
                + $"{pixelsPerWavelength:0.00}px, envelope-fwhm={envelopeFwhmPixels:0.00}px, "
                + $"wind-counterfactual=({counterfactualWeights.Earlier:0.000},"
                + $"{counterfactualWeights.Prior:0.000},{counterfactualWeights.Baseline:0.000}), "
                + $"source-sequence={sourceSequence}.");
            if (!assessment.Compatible)
            {
                failures.Add($"projectile {kind} image validation: field images or impact ROI are incompatible");
            }
            else if (!assessment.MeetsGate)
            {
                failures.Add(
                    $"projectile {kind} image validation: no localized geometric response at the exact impact "
                    + $"(peak {assessment.PeakHeightDeltaMetres * 1000.0:0.00} mm, "
                    + $"raw localization {assessment.LocalizationRatio:0.00}x, "
                    + $"frequency-aware wave {assessment.CoherentWaveRmsMetres * 1000.0:0.000} mm, "
                    + $"localization {assessment.WaveLocalizationRatio:0.00}x, mode {frequencyMode})");
            }
        }

        if (assessments.Count == 2
            && assessments[0].Compatible
            && assessments[1].Compatible
            && Math.Abs(
                assessments[0].PeakHeightDeltaMetres
                - assessments[1].PeakHeightDeltaMetres) < 1e-9
            && Math.Abs(
                assessments[0].LocalSignalRmsMetres
                - assessments[1].LocalSignalRmsMetres) < 1e-9)
        {
            failures.Add(
                "projectile image validation: stone ricochet and arrow entry responses are numerically identical");
        }

        return failures;
    }

    /// <summary>
    /// Measures encoded height/normal change inside an exact impact disc against a concentric
    /// annulus. The field stores signed height in R-B at a 50 mm display scale.
    /// </summary>
    /// <param name="baseline">Field immediately before projectile spawn.</param>
    /// <param name="response">Field shortly after the exact liquid callback.</param>
    /// <param name="screenX">Projected contact X in top-left framebuffer coordinates.</param>
    /// <param name="screenY">Projected contact Y in top-left framebuffer coordinates.</param>
    /// <returns>Scale-aware local and annular response statistics.</returns>
    internal static ProjectileSurfaceDeltaAssessment MeasureProjectileSurfaceDelta(
        SKBitmap baseline,
        SKBitmap response,
        double screenX,
        double screenY)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(response);
        if (baseline.Width != response.Width
            || baseline.Height != response.Height
            || baseline.Width < 32
            || baseline.Height < 32
            || !double.IsFinite(screenX)
            || !double.IsFinite(screenY))
        {
            return default;
        }

        int radius = Math.Clamp(Math.Min(baseline.Width, baseline.Height) / 18, 12, 72);
        int centerX = (int)Math.Round(screenX);
        int centerY = (int)Math.Round(screenY);
        if (centerX < -radius
            || centerY < -radius
            || centerX >= baseline.Width + radius
            || centerY >= baseline.Height + radius)
        {
            return default;
        }

        int outerRadius = radius * 3;
        int innerRadius = radius * 3 / 2;
        int localRadiusSquared = radius * radius;
        int innerRadiusSquared = innerRadius * innerRadius;
        int outerRadiusSquared = outerRadius * outerRadius;
        double localSignalSquared = 0.0;
        double backgroundSignalSquared = 0.0;
        double peakHeightDelta = 0.0;
        int localSamples = 0;
        int backgroundSamples = 0;
        int minimumX = Math.Max(0, centerX - outerRadius);
        int maximumX = Math.Min(baseline.Width - 1, centerX + outerRadius);
        int minimumY = Math.Max(0, centerY - outerRadius);
        int maximumY = Math.Min(baseline.Height - 1, centerY + outerRadius);
        for (int y = minimumY; y <= maximumY; y++)
        {
            int dy = y - centerY;
            for (int x = minimumX; x <= maximumX; x++)
            {
                int dx = x - centerX;
                int distanceSquared = dx * dx + dy * dy;
                bool local = distanceSquared <= localRadiusSquared;
                bool background = distanceSquared >= innerRadiusSquared
                    && distanceSquared <= outerRadiusSquared;
                if (!local && !background)
                {
                    continue;
                }

                SKColor before = baseline.GetPixel(x, y);
                SKColor after = response.GetPixel(x, y);
                if (!IsLiquidSurfaceFieldPixel(before) || !IsLiquidSurfaceFieldPixel(after))
                {
                    continue;
                }

                double heightDelta = 0.05
                    * ((after.Red - after.Blue) - (before.Red - before.Blue))
                    / 255.0;
                double normalEquivalent = 0.0125
                    * (after.Green - before.Green)
                    / 255.0;
                double signalSquared = heightDelta * heightDelta
                    + normalEquivalent * normalEquivalent;
                if (local)
                {
                    localSignalSquared += signalSquared;
                    peakHeightDelta = Math.Max(peakHeightDelta, Math.Abs(heightDelta));
                    localSamples++;
                }
                else
                {
                    backgroundSignalSquared += signalSquared;
                    backgroundSamples++;
                }
            }
        }

        int minimumLocalSamples = Math.Max(48, radius * radius / 2);
        int minimumBackgroundSamples = Math.Max(96, radius * radius);
        bool compatible = localSamples >= minimumLocalSamples
            && backgroundSamples >= minimumBackgroundSamples;
        double localRms = localSamples > 0
            ? Math.Sqrt(localSignalSquared / localSamples)
            : 0.0;
        double backgroundRms = backgroundSamples > 0
            ? Math.Sqrt(backgroundSignalSquared / backgroundSamples)
            : 0.0;
        double localization = localRms / Math.Max(backgroundRms, 0.00002);
        ProjectileWaveCoherenceAssessment wave = MeasureProjectileWaveCoherence(
            baseline,
            response,
            screenX,
            screenY,
            radius);
        compatible = compatible && wave.Compatible;
        // One quarter of a single-channel carrier step is resolvable only after coherent averaging;
        // the 30% excess must then survive an RMS combination of all four same-depth controls.
        const double encodedHeightMetresPerChannelStep = 0.05 / 255.0;
        const double minimumCounterfactualExcessRatio = 1.30;
        bool meetsGate = compatible
            && peakHeightDelta >= 0.00035
            && localRms >= 0.00012
            && wave.LocalRmsMetres >= encodedHeightMetresPerChannelStep / 4.0
            && wave.LocalizationRatio >= minimumCounterfactualExcessRatio;
        return new ProjectileSurfaceDeltaAssessment(
            compatible,
            radius,
            localSamples,
            backgroundSamples,
            peakHeightDelta,
            localRms,
            backgroundRms,
            localization,
            wave.LocalRmsMetres,
            wave.ControlRmsMetres,
            wave.LocalizationRatio,
            meetsGate);
    }

    /// <summary>
    /// Measures one impact and physically equal unforced controls in local coordinates of the
    /// horizontal liquid plane. This preserves circular world-space rings under perspective instead
    /// of comparing fixed framebuffer circles whose world area changes with camera depth.
    /// </summary>
    /// <param name="earlier">Oldest undisturbed surface field used to estimate wind curvature.</param>
    /// <param name="prior">Middle undisturbed surface field used to estimate wind curvature.</param>
    /// <param name="baseline">Latest undisturbed surface field saved before projectile spawn.</param>
    /// <param name="response">Surface field saved shortly after the exact liquid callback.</param>
    /// <param name="counterfactualWeights">Timestamp-derived quadratic prediction coefficients.</param>
    /// <param name="packet">Logged physical wavelength and amplitude for the synchronized callback.</param>
    /// <param name="contact">Projected tangent frame at the physical contact.</param>
    /// <param name="controls">Projected tangent frames outside the solver impulse support.</param>
    /// <returns>Equal-world-area local and counterfactual response statistics.</returns>
    internal static ProjectileSurfaceDeltaAssessment MeasureWorldProjectedProjectileSurfaceDelta(
        SKBitmap earlier,
        SKBitmap prior,
        SKBitmap baseline,
        SKBitmap response,
        TemporalCounterfactualWeights counterfactualWeights,
        ProjectileWavePacket packet,
        ProjectileSurfaceFrame contact,
        IReadOnlyList<ProjectileSurfaceFrame> controls)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(controls);
        if (earlier.Width != prior.Width
            || earlier.Height != prior.Height
            || prior.Width != baseline.Width
            || prior.Height != baseline.Height
            || baseline.Width != response.Width
            || baseline.Height != response.Height
            || baseline.Width < 32
            || baseline.Height < 32
            || !AreStableCounterfactualWeights(counterfactualWeights)
            || packet.PacketSequence <= 0
            || !double.IsFinite(packet.AmplitudeMetres)
            || packet.AmplitudeMetres <= 0.0
            || !double.IsFinite(packet.WavelengthMetres)
            || packet.WavelengthMetres <= 0.0)
        {
            return default;
        }

        double contactPixelsPerWorld = MinimumPixelsPerWorld(contact);
        double minimumPixelsPerWorld = controls
            .Select(MinimumPixelsPerWorld)
            .Append(contactPixelsPerWorld)
            .Min();
        if (!double.IsFinite(contactPixelsPerWorld)
            || !double.IsFinite(minimumPixelsPerWorld)
            || minimumPixelsPerWorld <= 0.0)
        {
            return default;
        }

        double envelopeSigmaMetres = Math.Max(packet.WavelengthMetres * 0.35, 0.05);
        double envelopeFwhmPixels = 2.354820045
            * envelopeSigmaMetres
            * contactPixelsPerWorld;
        double envelopeBinWidthMetres = Math.Max(
            envelopeSigmaMetres,
            2.0 / minimumPixelsPerWorld);
        bool carrierResolvable = packet.WavelengthMetres * minimumPixelsPerWorld >= 2.0;
        bool normalEnvelopeResolvable = envelopeFwhmPixels >= 2.0
            && contact.SupportRadiusWorldBlocks / envelopeBinWidthMetres >= 3.0;

        WorldWavePatchAssessment local = MeasureWorldWavePatch(
            earlier,
            prior,
            baseline,
            response,
            counterfactualWeights,
            packet.WavelengthMetres,
            envelopeBinWidthMetres,
            contact);
        WorldWavePatchAssessment[] validControls = controls
            .Select(frame => MeasureWorldWavePatch(
                earlier,
                prior,
                baseline,
                response,
                counterfactualWeights,
                packet.WavelengthMetres,
                envelopeBinWidthMetres,
                frame))
            .Where(static assessment => assessment.Compatible)
            .ToArray();
        int backgroundSamples = validControls.Sum(static assessment => assessment.Samples);
        double backgroundRawSquared = validControls.Sum(static assessment =>
            assessment.RawRmsMetres
            * assessment.RawRmsMetres
            * assessment.Samples);
        double backgroundRms = backgroundSamples > 0
            ? Math.Sqrt(backgroundRawSquared / backgroundSamples)
            : 0.0;
        double localFrequencyRms = carrierResolvable
            ? local.MatchedCarrierRmsMetres
            : normalEnvelopeResolvable
                ? local.NormalEnvelopeRmsMetres
                : 0.0;
        double controlWaveRms = validControls.Length > 0
            ? Math.Sqrt(validControls.Average(assessment =>
                Math.Pow(
                    carrierResolvable
                        ? assessment.MatchedCarrierRmsMetres
                        : normalEnvelopeResolvable
                            ? assessment.NormalEnvelopeRmsMetres
                            : 0.0,
                    2.0)))
            : 0.0;
        double waveLocalization = localFrequencyRms
            / Math.Max(controlWaveRms, 0.00002);
        double rawLocalization = local.RawRmsMetres
            / Math.Max(backgroundRms, 0.00002);
        bool frequencyCompatible = carrierResolvable
            ? local.MatchedCarrierCompatible
                && validControls.All(static assessment => assessment.MatchedCarrierCompatible)
            : normalEnvelopeResolvable
                && local.NormalEnvelopeCompatible
                && validControls.All(static assessment => assessment.NormalEnvelopeCompatible);
        bool compatible = local.Compatible
            && validControls.Length >= 2
            && frequencyCompatible;
        double encodedSignalMetresPerChannelStep = carrierResolvable
            ? 0.05 / 255.0
            : 0.0125 / 255.0;
        const double minimumCounterfactualExcessRatio = 1.30;
        bool meetsGate = compatible
            && local.PeakHeightDeltaMetres >= 0.00035
            && local.RawRmsMetres >= 0.00012
            && localFrequencyRms >= encodedSignalMetresPerChannelStep / 4.0
            && waveLocalization >= minimumCounterfactualExcessRatio;
        int radiusPixels = (int)Math.Round(
            contact.SupportRadiusWorldBlocks
            * Math.Max(
                contact.ScreenPerWorldX.Length(),
                contact.ScreenPerWorldZ.Length()));
        return new ProjectileSurfaceDeltaAssessment(
            compatible,
            Math.Max(1, radiusPixels),
            local.Samples,
            backgroundSamples,
            local.PeakHeightDeltaMetres,
            local.RawRmsMetres,
            backgroundRms,
            rawLocalization,
            localFrequencyRms,
            controlWaveRms,
            waveLocalization,
            meetsGate);
    }

    /// <summary>Measures raw energy and contact-centred radial/dipole wave modes in one world patch.</summary>
    /// <param name="earlier">Oldest pre-impact encoded surface field.</param>
    /// <param name="prior">Middle pre-impact encoded surface field.</param>
    /// <param name="baseline">Latest pre-impact encoded surface field.</param>
    /// <param name="response">Post-impact encoded surface field.</param>
    /// <param name="counterfactualWeights">Timestamp-derived quadratic prediction coefficients.</param>
    /// <param name="wavelengthMetres">Server-authoritative packet carrier wavelength.</param>
    /// <param name="envelopeBinWidthMetres">Shared anti-aliasing width for radial normal bins.</param>
    /// <param name="frame">Local world-to-screen differential and physical support radius.</param>
    /// <returns>Detrended wave content without any amplitude gain.</returns>
    private static WorldWavePatchAssessment MeasureWorldWavePatch(
        SKBitmap earlier,
        SKBitmap prior,
        SKBitmap baseline,
        SKBitmap response,
        TemporalCounterfactualWeights counterfactualWeights,
        double wavelengthMetres,
        double envelopeBinWidthMetres,
        ProjectileSurfaceFrame frame)
    {
        Vector2 basisX = frame.ScreenPerWorldX;
        Vector2 basisZ = frame.ScreenPerWorldZ;
        double determinant = basisX.X * basisZ.Y - basisZ.X * basisX.Y;
        double radius = frame.SupportRadiusWorldBlocks;
        if (!double.IsFinite(determinant)
            || Math.Abs(determinant) < 0.01
            || !double.IsFinite(radius)
            || radius <= 0.0
            || !double.IsFinite(wavelengthMetres)
            || wavelengthMetres <= 0.0
            || !double.IsFinite(envelopeBinWidthMetres)
            || envelopeBinWidthMetres <= 0.0
            || !float.IsFinite(frame.Center.X)
            || !float.IsFinite(frame.Center.Y))
        {
            return default;
        }

        int horizontalExtent = (int)Math.Ceiling(
            radius * (Math.Abs(basisX.X) + Math.Abs(basisZ.X)));
        int verticalExtent = (int)Math.Ceiling(
            radius * (Math.Abs(basisX.Y) + Math.Abs(basisZ.Y)));
        int minimumX = Math.Max(0, (int)Math.Floor(frame.Center.X) - horizontalExtent);
        int maximumX = Math.Min(baseline.Width - 1, (int)Math.Ceiling(frame.Center.X) + horizontalExtent);
        int minimumY = Math.Max(0, (int)Math.Floor(frame.Center.Y) - verticalExtent);
        int maximumY = Math.Min(baseline.Height - 1, (int)Math.Ceiling(frame.Center.Y) + verticalExtent);
        List<(double X, double Z, double Height, double Normal)> samples = [];
        double rawSquared = 0.0;
        double peakHeight = 0.0;
        double sumX = 0.0;
        double sumZ = 0.0;
        double sumXX = 0.0;
        double sumXZ = 0.0;
        double sumZZ = 0.0;
        double sumHeight = 0.0;
        double sumXHeight = 0.0;
        double sumZHeight = 0.0;
        double sumNormal = 0.0;
        double sumXNormal = 0.0;
        double sumZNormal = 0.0;
        double inverseRadius = 1.0 / radius;
        for (int y = minimumY; y <= maximumY; y++)
        {
            double pixelY = y - frame.Center.Y;
            for (int x = minimumX; x <= maximumX; x++)
            {
                double pixelX = x - frame.Center.X;
                double worldX = (pixelX * basisZ.Y - basisZ.X * pixelY) / determinant;
                double worldZ = (basisX.X * pixelY - pixelX * basisX.Y) / determinant;
                double normalizedX = worldX * inverseRadius;
                double normalizedZ = worldZ * inverseRadius;
                if (normalizedX * normalizedX + normalizedZ * normalizedZ > 1.0)
                {
                    continue;
                }

                SKColor oldest = earlier.GetPixel(x, y);
                SKColor middle = prior.GetPixel(x, y);
                SKColor before = baseline.GetPixel(x, y);
                SKColor after = response.GetPixel(x, y);
                if (!IsLiquidSurfaceFieldPixel(oldest)
                    || !IsLiquidSurfaceFieldPixel(middle)
                    || !IsLiquidSurfaceFieldPixel(before)
                    || !IsLiquidSurfaceFieldPixel(after))
                {
                    continue;
                }

                double oldestHeight = 0.05 * (oldest.Red - oldest.Blue) / 255.0;
                double middleHeight = 0.05 * (middle.Red - middle.Blue) / 255.0;
                double beforeHeight = 0.05 * (before.Red - before.Blue) / 255.0;
                double afterHeight = 0.05 * (after.Red - after.Blue) / 255.0;
                double predictedHeight = counterfactualWeights.Earlier * oldestHeight
                    + counterfactualWeights.Prior * middleHeight
                    + counterfactualWeights.Baseline * beforeHeight;
                double heightDelta = afterHeight - predictedHeight;
                double predictedNormalCarrier = counterfactualWeights.Earlier * oldest.Green
                    + counterfactualWeights.Prior * middle.Green
                    + counterfactualWeights.Baseline * before.Green;
                double normalEquivalent = 0.0125
                    * (after.Green - predictedNormalCarrier)
                    / 255.0;
                samples.Add((normalizedX, normalizedZ, heightDelta, normalEquivalent));
                rawSquared += heightDelta * heightDelta
                    + normalEquivalent * normalEquivalent;
                peakHeight = Math.Max(peakHeight, Math.Abs(heightDelta));
                sumX += normalizedX;
                sumZ += normalizedZ;
                sumXX += normalizedX * normalizedX;
                sumXZ += normalizedX * normalizedZ;
                sumZZ += normalizedZ * normalizedZ;
                sumHeight += heightDelta;
                sumXHeight += normalizedX * heightDelta;
                sumZHeight += normalizedZ * heightDelta;
                sumNormal += normalEquivalent;
                sumXNormal += normalizedX * normalEquivalent;
                sumZNormal += normalizedZ * normalEquivalent;
            }
        }

        const int minimumSamples = 96;
        if (samples.Count < minimumSamples
            || !TrySolveAffinePlane(
                samples.Count,
                sumX,
                sumZ,
                sumXX,
                sumXZ,
                sumZZ,
                sumHeight,
                sumXHeight,
                sumZHeight,
                out double planeConstant,
                out double planeX,
                out double planeZ)
            || !TrySolveAffinePlane(
                samples.Count,
                sumX,
                sumZ,
                sumXX,
                sumXZ,
                sumZZ,
                sumNormal,
                sumXNormal,
                sumZNormal,
                out double normalPlaneConstant,
                out double normalPlaneX,
                out double normalPlaneZ))
        {
            return default;
        }

        const int radialBinCount = 12;
        double[] radialSum = new double[radialBinCount];
        double[] dipoleXSum = new double[radialBinCount];
        double[] dipoleZSum = new double[radialBinCount];
        int[] radialCounts = new int[radialBinCount];
        foreach ((double normalizedX, double normalizedZ, double height, _) in samples)
        {
            double normalizedRadius = Math.Sqrt(
                normalizedX * normalizedX + normalizedZ * normalizedZ);
            int bin = Math.Min(
                radialBinCount - 1,
                (int)(normalizedRadius * radialBinCount));
            double residual = height
                - planeConstant
                - planeX * normalizedX
                - planeZ * normalizedZ;
            double inverseLength = normalizedRadius > 1.0e-6
                ? 1.0 / normalizedRadius
                : 0.0;
            radialSum[bin] += residual;
            dipoleXSum[bin] += residual * normalizedX * inverseLength;
            dipoleZSum[bin] += residual * normalizedZ * inverseLength;
            radialCounts[bin]++;
        }

        double coherentSquared = 0.0;
        int coherentSamples = 0;
        int acceptedBins = 0;
        for (int bin = 0; bin < radialBinCount; bin++)
        {
            int count = radialCounts[bin];
            if (count < 12)
            {
                continue;
            }

            double radialMean = radialSum[bin] / count;
            double dipoleX = 2.0 * dipoleXSum[bin] / count;
            double dipoleZ = 2.0 * dipoleZSum[bin] / count;
            // Orthogonal constant/cos(theta)/sin(theta) basis energy. The one-half factors are
            // Parseval normalization, so the reconstructed RMS is not an arbitrary amplification.
            coherentSquared += count
                * (radialMean * radialMean
                    + 0.5 * dipoleX * dipoleX
                    + 0.5 * dipoleZ * dipoleZ);
            coherentSamples += count;
            acceptedBins++;
        }

        double waveNumberPerMetre = 2.0 * Math.PI / wavelengthMetres;
        double carrierCosineSquared = 0.0;
        double carrierSineSquared = 0.0;
        double carrierCosineSine = 0.0;
        double carrierHeightCosine = 0.0;
        double carrierHeightSine = 0.0;
        foreach ((double normalizedX, double normalizedZ, double height, _) in samples)
        {
            double radiusMetres = radius * Math.Sqrt(
                normalizedX * normalizedX + normalizedZ * normalizedZ);
            double cosine = Math.Cos(waveNumberPerMetre * radiusMetres);
            double sine = Math.Sin(waveNumberPerMetre * radiusMetres);
            double residual = height
                - planeConstant
                - planeX * normalizedX
                - planeZ * normalizedZ;
            carrierCosineSquared += cosine * cosine;
            carrierSineSquared += sine * sine;
            carrierCosineSine += cosine * sine;
            carrierHeightCosine += residual * cosine;
            carrierHeightSine += residual * sine;
        }

        double carrierDeterminant = carrierCosineSquared * carrierSineSquared
            - carrierCosineSine * carrierCosineSine;
        bool matchedCarrierCompatible = double.IsFinite(carrierDeterminant)
            && carrierDeterminant > 1.0e-9;
        double carrierCosineCoefficient = matchedCarrierCompatible
            ? (carrierHeightCosine * carrierSineSquared
                - carrierHeightSine * carrierCosineSine) / carrierDeterminant
            : 0.0;
        double carrierSineCoefficient = matchedCarrierCompatible
            ? (carrierHeightSine * carrierCosineSquared
                - carrierHeightCosine * carrierCosineSine) / carrierDeterminant
            : 0.0;
        double matchedCarrierSquared = 0.0;
        if (matchedCarrierCompatible)
        {
            foreach ((double normalizedX, double normalizedZ, _, _) in samples)
            {
                double radiusMetres = radius * Math.Sqrt(
                    normalizedX * normalizedX + normalizedZ * normalizedZ);
                double predicted = carrierCosineCoefficient
                        * Math.Cos(waveNumberPerMetre * radiusMetres)
                    + carrierSineCoefficient
                        * Math.Sin(waveNumberPerMetre * radiusMetres);
                matchedCarrierSquared += predicted * predicted;
            }
        }

        int envelopeBinCount = Math.Clamp(
            (int)Math.Ceiling(radius / envelopeBinWidthMetres),
            1,
            64);
        double[] envelopeNormalSum = new double[envelopeBinCount];
        int[] envelopeCounts = new int[envelopeBinCount];
        foreach ((double normalizedX, double normalizedZ, _, double normal) in samples)
        {
            double normalizedRadius = Math.Sqrt(
                normalizedX * normalizedX + normalizedZ * normalizedZ);
            int bin = Math.Min(
                envelopeBinCount - 1,
                (int)(normalizedRadius * radius / envelopeBinWidthMetres));
            double normalResidual = normal
                - normalPlaneConstant
                - normalPlaneX * normalizedX
                - normalPlaneZ * normalizedZ;
            envelopeNormalSum[bin] += normalResidual;
            envelopeCounts[bin]++;
        }

        double envelopeSquared = 0.0;
        int envelopeSamples = 0;
        int acceptedEnvelopeBins = 0;
        for (int bin = 0; bin < envelopeBinCount; bin++)
        {
            int count = envelopeCounts[bin];
            if (count < 8)
            {
                continue;
            }

            double mean = envelopeNormalSum[bin] / count;
            envelopeSquared += count * mean * mean;
            envelopeSamples += count;
            acceptedEnvelopeBins++;
        }

        bool normalEnvelopeCompatible = envelopeBinCount >= 3
            && acceptedEnvelopeBins >= Math.Max(3, envelopeBinCount / 2)
            && envelopeSamples >= minimumSamples;
        bool compatible = samples.Count >= minimumSamples;
        return new WorldWavePatchAssessment(
            compatible,
            samples.Count,
            peakHeight,
            Math.Sqrt(rawSquared / samples.Count),
            coherentSamples > 0
                ? Math.Sqrt(coherentSquared / coherentSamples)
                : 0.0,
            matchedCarrierCompatible
                ? Math.Sqrt(matchedCarrierSquared / samples.Count)
                : 0.0,
            matchedCarrierCompatible,
            normalEnvelopeCompatible
                ? Math.Sqrt(envelopeSquared / envelopeSamples)
                : 0.0,
            normalEnvelopeCompatible);
    }

    /// <summary>Solves the least-squares affine height plane used only to remove smooth wind drift.</summary>
    /// <param name="count">Positive sample count.</param>
    /// <param name="sumX">Sum of normalized X.</param>
    /// <param name="sumZ">Sum of normalized Z.</param>
    /// <param name="sumXX">Sum of squared normalized X.</param>
    /// <param name="sumXZ">Sum of normalized XZ products.</param>
    /// <param name="sumZZ">Sum of squared normalized Z.</param>
    /// <param name="sumHeight">Sum of signed height deltas.</param>
    /// <param name="sumXHeight">Sum of X-weighted height deltas.</param>
    /// <param name="sumZHeight">Sum of Z-weighted height deltas.</param>
    /// <param name="constant">Solved constant coefficient.</param>
    /// <param name="coefficientX">Solved X coefficient.</param>
    /// <param name="coefficientZ">Solved Z coefficient.</param>
    /// <returns>Whether the normal matrix was finite and non-singular.</returns>
    private static bool TrySolveAffinePlane(
        int count,
        double sumX,
        double sumZ,
        double sumXX,
        double sumXZ,
        double sumZZ,
        double sumHeight,
        double sumXHeight,
        double sumZHeight,
        out double constant,
        out double coefficientX,
        out double coefficientZ)
    {
        double determinant = count * (sumXX * sumZZ - sumXZ * sumXZ)
            - sumX * (sumX * sumZZ - sumXZ * sumZ)
            + sumZ * (sumX * sumXZ - sumXX * sumZ);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1.0e-9)
        {
            constant = 0.0;
            coefficientX = 0.0;
            coefficientZ = 0.0;
            return false;
        }

        constant = (
            sumHeight * (sumXX * sumZZ - sumXZ * sumXZ)
            - sumX * (sumXHeight * sumZZ - sumXZ * sumZHeight)
            + sumZ * (sumXHeight * sumXZ - sumXX * sumZHeight)) / determinant;
        coefficientX = (
            count * (sumXHeight * sumZZ - sumXZ * sumZHeight)
            - sumHeight * (sumX * sumZZ - sumXZ * sumZ)
            + sumZ * (sumX * sumZHeight - sumXHeight * sumZ)) / determinant;
        coefficientZ = (
            count * (sumXX * sumZHeight - sumXHeight * sumXZ)
            - sumX * (sumX * sumZHeight - sumXHeight * sumZ)
            + sumHeight * (sumX * sumXZ - sumXX * sumZ)) / determinant;
        return double.IsFinite(constant)
            && double.IsFinite(coefficientX)
            && double.IsFinite(coefficientZ);
    }

    /// <summary>Parses one logged projective liquid-plane frame.</summary>
    /// <param name="match">Successful <see cref="ProjectileImpactSurfaceFrameRegex"/> match.</param>
    /// <returns>Finite tangent-frame values consumed by world-space measurement.</returns>
    private static ProjectileSurfaceFrame ParseProjectileSurfaceFrame(Match match) => new(
        new Vector2(
            (float)ParseInvariant(match.Groups[4].Value),
            (float)ParseInvariant(match.Groups[5].Value)),
        new Vector2(
            (float)ParseInvariant(match.Groups[6].Value),
            (float)ParseInvariant(match.Groups[7].Value)),
        new Vector2(
            (float)ParseInvariant(match.Groups[8].Value),
            (float)ParseInvariant(match.Groups[9].Value)),
        ParseInvariant(match.Groups[10].Value));

    /// <summary>Returns the least-resolved screen direction of one world-plane differential.</summary>
    /// <param name="frame">World X/Z to screen-pixel differential.</param>
    /// <returns>Smallest singular value in pixels per world block.</returns>
    private static double MinimumPixelsPerWorld(ProjectileSurfaceFrame frame)
    {
        Vector2 basisX = frame.ScreenPerWorldX;
        Vector2 basisZ = frame.ScreenPerWorldZ;
        double xx = Vector2.Dot(basisX, basisX);
        double zz = Vector2.Dot(basisZ, basisZ);
        double xz = Vector2.Dot(basisX, basisZ);
        double trace = xx + zz;
        double discriminant = Math.Sqrt(Math.Max(
            0.0,
            (xx - zz) * (xx - zz) + 4.0 * xz * xz));
        return Math.Sqrt(Math.Max(0.0, 0.5 * (trace - discriminant)));
    }

    /// <summary>
    /// Removes only the local 9-by-9 mean from signed height change, then measures the component
    /// coherent around the exact contact. Four non-overlapping discs at the same screen depth form
    /// a conservative counterfactual for perspective-scaled wind texture and camera-wide evolution.
    /// </summary>
    /// <param name="baseline">Field immediately before projectile spawn.</param>
    /// <param name="response">Field shortly after the exact liquid callback.</param>
    /// <param name="screenX">Projected contact X in top-left framebuffer coordinates.</param>
    /// <param name="screenY">Projected contact Y in top-left framebuffer coordinates.</param>
    /// <param name="measurementRadius">Scale-aware radius used by the raw disc/annulus measurement.</param>
    /// <returns>Signed ring coherence at the contact and in same-depth control discs.</returns>
    private static ProjectileWaveCoherenceAssessment MeasureProjectileWaveCoherence(
        SKBitmap baseline,
        SKBitmap response,
        double screenX,
        double screenY,
        int measurementRadius)
    {
        int radius = Math.Clamp(measurementRadius / 2, 12, 36);
        const int highPassRadius = 4;
        int binWidth = Math.Max(1, radius / 14);
        int stride = baseline.Width + 1;
        double[] heightIntegral = new double[stride * (baseline.Height + 1)];
        int[] validIntegral = new int[heightIntegral.Length];
        for (int y = 0; y < baseline.Height; y++)
        {
            double rowHeight = 0.0;
            int rowValid = 0;
            int integralRow = (y + 1) * stride;
            int precedingRow = y * stride;
            for (int x = 0; x < baseline.Width; x++)
            {
                SKColor before = baseline.GetPixel(x, y);
                SKColor after = response.GetPixel(x, y);
                bool valid = IsLiquidSurfaceFieldPixel(before)
                    && IsLiquidSurfaceFieldPixel(after);
                if (valid)
                {
                    rowHeight += 0.05
                        * ((after.Red - after.Blue) - (before.Red - before.Blue))
                        / 255.0;
                    rowValid++;
                }

                int integralIndex = integralRow + x + 1;
                heightIntegral[integralIndex] = heightIntegral[precedingRow + x + 1]
                    + rowHeight;
                validIntegral[integralIndex] = validIntegral[precedingRow + x + 1]
                    + rowValid;
            }
        }

        (double localRms, int localSamples) = MeasureRadialHighPassCoherence(
            heightIntegral,
            validIntegral,
            stride,
            baseline.Width,
            baseline.Height,
            screenX,
            screenY,
            radius,
            highPassRadius,
            binWidth);
        double controlSquared = 0.0;
        int controlSamples = 0;
        bool controlsCompatible = true;
        foreach (int offset in new[] { -3, -2, 2, 3 })
        {
            (double controlRms, int samples) = MeasureRadialHighPassCoherence(
                heightIntegral,
                validIntegral,
                stride,
                baseline.Width,
                baseline.Height,
                screenX + offset * radius,
                screenY,
                radius,
                highPassRadius,
                binWidth);
            controlSquared += controlRms * controlRms;
            controlSamples += samples;
            controlsCompatible &= samples >= Math.Max(64, radius * radius / 2);
        }

        double controlRmsCombined = Math.Sqrt(controlSquared / 4.0);
        bool compatible = localSamples >= Math.Max(64, radius * radius)
            && controlsCompatible;
        return new ProjectileWaveCoherenceAssessment(
            compatible,
            localSamples,
            controlSamples,
            localRms,
            controlRmsCombined,
            localRms / Math.Max(controlRmsCombined, 0.00002));
    }

    /// <summary>Measures angularly coherent signed residuals in radial bins around one disc centre.</summary>
    /// <param name="heightIntegral">Summed-area table of signed encoded height delta.</param>
    /// <param name="validIntegral">Summed-area table of jointly valid liquid-field pixels.</param>
    /// <param name="stride">Integral-image row stride.</param>
    /// <param name="width">Source field width.</param>
    /// <param name="height">Source field height.</param>
    /// <param name="centerX">Candidate disc centre X.</param>
    /// <param name="centerY">Candidate disc centre Y.</param>
    /// <param name="radius">Disc radius.</param>
    /// <param name="highPassRadius">Box radius whose mean is subtracted without gain.</param>
    /// <param name="binWidth">Radial-bin width in pixels.</param>
    /// <returns>RMS radial coherence and accepted sample count.</returns>
    private static (double RmsMetres, int Samples) MeasureRadialHighPassCoherence(
        double[] heightIntegral,
        int[] validIntegral,
        int stride,
        int width,
        int height,
        double centerX,
        double centerY,
        int radius,
        int highPassRadius,
        int binWidth)
    {
        int binCount = radius / binWidth + 1;
        double[] radialSums = new double[binCount];
        int[] radialCounts = new int[binCount];
        int minimumX = Math.Max(highPassRadius, (int)Math.Floor(centerX - radius));
        int maximumX = Math.Min(width - highPassRadius - 1, (int)Math.Ceiling(centerX + radius));
        int minimumY = Math.Max(highPassRadius, (int)Math.Floor(centerY - radius));
        int maximumY = Math.Min(height - highPassRadius - 1, (int)Math.Ceiling(centerY + radius));
        double radiusSquared = radius * radius;
        int filterWidth = highPassRadius * 2 + 1;
        int filterSamples = filterWidth * filterWidth;
        for (int y = minimumY; y <= maximumY; y++)
        {
            double dy = y - centerY;
            for (int x = minimumX; x <= maximumX; x++)
            {
                double dx = x - centerX;
                double distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > radiusSquared)
                {
                    continue;
                }

                int left = x - highPassRadius;
                int top = y - highPassRadius;
                int rightExclusive = x + highPassRadius + 1;
                int bottomExclusive = y + highPassRadius + 1;
                int validCount = SumIntegralRectangle(
                    validIntegral,
                    stride,
                    left,
                    top,
                    rightExclusive,
                    bottomExclusive);
                if (validCount != filterSamples)
                {
                    continue;
                }

                double localMean = SumIntegralRectangle(
                    heightIntegral,
                    stride,
                    left,
                    top,
                    rightExclusive,
                    bottomExclusive) / filterSamples;
                double centerHeight = SumIntegralRectangle(
                    heightIntegral,
                    stride,
                    x,
                    y,
                    x + 1,
                    y + 1);
                int bin = Math.Min(
                    radialSums.Length - 1,
                    (int)Math.Sqrt(distanceSquared) / binWidth);
                radialSums[bin] += centerHeight - localMean;
                radialCounts[bin]++;
            }
        }

        double coherentSquared = 0.0;
        int acceptedSamples = 0;
        int minimumBinSamples = Math.Max(8, binWidth * 6);
        for (int bin = 0; bin < radialSums.Length; bin++)
        {
            int samples = radialCounts[bin];
            if (samples < minimumBinSamples)
            {
                continue;
            }

            coherentSquared += radialSums[bin] * radialSums[bin] / samples;
            acceptedSamples += samples;
        }

        return acceptedSamples > 0
            ? (Math.Sqrt(coherentSquared / acceptedSamples), acceptedSamples)
            : default;
    }

    /// <summary>Queries a half-open rectangle in one row-major summed-area table.</summary>
    /// <typeparam name="T">Integral numeric type.</typeparam>
    /// <param name="integral">Summed-area table with a zero top row and left column.</param>
    /// <param name="stride">Integral-image row stride.</param>
    /// <param name="left">Inclusive left coordinate.</param>
    /// <param name="top">Inclusive top coordinate.</param>
    /// <param name="rightExclusive">Exclusive right coordinate.</param>
    /// <param name="bottomExclusive">Exclusive bottom coordinate.</param>
    /// <returns>Rectangle sum.</returns>
    private static T SumIntegralRectangle<T>(
        T[] integral,
        int stride,
        int left,
        int top,
        int rightExclusive,
        int bottomExclusive)
        where T : System.Numerics.INumber<T>
    {
        return integral[bottomExclusive * stride + rightExclusive]
            - integral[top * stride + rightExclusive]
            - integral[bottomExclusive * stride + left]
            + integral[top * stride + left];
    }

    /// <summary>Recognizes pixels emitted by the LiquidSurfaceField debug channel.</summary>
    /// <param name="pixel">One effect-frame pixel.</param>
    /// <returns>Whether its complementary red/blue carrier plausibly encodes liquid height.</returns>
    private static bool IsLiquidSurfaceFieldPixel(SKColor pixel) =>
        pixel.Red + pixel.Blue >= 160
        && Math.Max(pixel.Red, pixel.Blue) >= 80
        && pixel.Green < 192;

    /// <summary>Finds the saved effect image for one exact capture label.</summary>
    /// <param name="log">Runtime log containing complete comparison-save lines.</param>
    /// <param name="label">Stable label without the before/effect suffix.</param>
    /// <param name="path">Absolute effect PNG path when found.</param>
    /// <returns>Whether a complete matching save line was parsed.</returns>
    private static bool TryFindSavedEffectPath(string log, string label, out string path)
    {
        string suffix = $"{label}-vintagertx.png";
        foreach (string line in log.Split('\n').Reverse())
        {
            int suffixIndex = line.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (suffixIndex < 0
                || !line.Contains("Comparison capture saved:", StringComparison.OrdinalIgnoreCase)
                || !line.Contains($"{label}-before.png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = line.LastIndexOf(
                " and ",
                suffixIndex,
                StringComparison.OrdinalIgnoreCase);
            if (separator < 0)
            {
                continue;
            }

            int end = suffixIndex + suffix.Length;
            path = line[(separator + 5)..end]
                .Trim()
                .Replace('/', Path.DirectorySeparatorChar);
            return path.Length > 0;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>
    /// Derives a second-order wind counterfactual from three pre-impact capture timestamps.
    /// Bounds on cadence and coefficient norm reject stalled or ill-conditioned extrapolation rather
    /// than multiplying encoded-field quantization until it resembles a projectile response.
    /// </summary>
    /// <param name="earlierPath">Oldest undisturbed field path.</param>
    /// <param name="priorPath">Middle undisturbed field path.</param>
    /// <param name="baselinePath">Latest undisturbed field path.</param>
    /// <param name="responsePath">Post-callback field path.</param>
    /// <param name="weights">Lagrange coefficients evaluated at the response timestamp.</param>
    /// <returns>Whether the filenames contain a strictly ordered, stable timeline.</returns>
    private static bool TryResolveQuadraticCounterfactualWeights(
        string earlierPath,
        string priorPath,
        string baselinePath,
        string responsePath,
        out TemporalCounterfactualWeights weights)
    {
        weights = default;
        if (!TryParseCaptureTimestamp(earlierPath, out DateTime earlier)
            || !TryParseCaptureTimestamp(priorPath, out DateTime prior)
            || !TryParseCaptureTimestamp(baselinePath, out DateTime baseline)
            || !TryParseCaptureTimestamp(responsePath, out DateTime response))
        {
            return false;
        }

        double earlierSeconds = (prior - earlier).TotalSeconds;
        double priorSeconds = (baseline - prior).TotalSeconds;
        double responseSeconds = (response - baseline).TotalSeconds;
        if (earlierSeconds < 0.050
            || priorSeconds < 0.050
            || responseSeconds < 0.050
            || Math.Max(earlierSeconds, priorSeconds) / Math.Min(earlierSeconds, priorSeconds) > 3.0
            || responseSeconds / ((earlierSeconds + priorSeconds) * 0.5) > 3.0)
        {
            return false;
        }

        double oldestTime = -(earlierSeconds + priorSeconds);
        double middleTime = -priorSeconds;
        double newestTime = 0.0;
        double targetTime = responseSeconds;
        weights = new TemporalCounterfactualWeights(
            (targetTime - middleTime) * (targetTime - newestTime)
                / ((oldestTime - middleTime) * (oldestTime - newestTime)),
            (targetTime - oldestTime) * (targetTime - newestTime)
                / ((middleTime - oldestTime) * (middleTime - newestTime)),
            (targetTime - oldestTime) * (targetTime - middleTime)
                / ((newestTime - oldestTime) * (newestTime - middleTime)));
        return AreStableCounterfactualWeights(weights);
    }

    /// <summary>Rejects non-finite or high-gain timestamp extrapolation coefficients.</summary>
    /// <param name="weights">Candidate quadratic counterfactual coefficients.</param>
    /// <returns>Whether the coefficients preserve constants and have bounded quantization gain.</returns>
    private static bool AreStableCounterfactualWeights(TemporalCounterfactualWeights weights)
    {
        double sum = weights.Earlier + weights.Prior + weights.Baseline;
        double norm = Math.Abs(weights.Earlier)
            + Math.Abs(weights.Prior)
            + Math.Abs(weights.Baseline);
        return double.IsFinite(weights.Earlier)
            && double.IsFinite(weights.Prior)
            && double.IsFinite(weights.Baseline)
            && Math.Abs(sum - 1.0) <= 1.0e-9
            && norm <= 18.0;
    }

    /// <summary>Parses the capture service's leading yyyyMMdd-HHmmssfff local timestamp.</summary>
    /// <param name="path">Saved effect path.</param>
    /// <param name="timestamp">Parsed local wall-clock value.</param>
    /// <returns>Whether the filename has the exact capture timestamp prefix.</returns>
    private static bool TryParseCaptureTimestamp(string path, out DateTime timestamp)
    {
        string name = Path.GetFileName(path);
        timestamp = default;
        return name.Length >= 18 && DateTime.TryParseExact(
            name[..18],
            "yyyyMMdd-HHmmssfff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    /// <summary>Parses invariant decimal text while accepting localized comma logs.</summary>
    /// <param name="value">Finite decimal token.</param>
    /// <returns>Parsed double.</returns>
    private static double ParseInvariant(string value) => double.Parse(
        value.Replace(',', '.'),
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Computes normalized Rec.709 luminance for one display pixel.</summary>
    /// <param name="color">Display-referred RGB sample.</param>
    /// <returns>Normalized luminance in [0, 1].</returns>
    private static double Luminance(SKColor color)
    {
        return (color.Red * 0.2126 + color.Green * 0.7152 + color.Blue * 0.0722) / 255.0;
    }

    /// <summary>
    /// Executes the percentile step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <param name="histogram">The histogram input used to configure this deterministic test path.</param>
    /// <param name="total">The total input used to configure this deterministic test path.</param>
    /// <param name="percentile">The percentile input used to configure this deterministic test path.</param>
    /// <returns>The percentile result consumed by the caller&apos;s assertion.</returns>
    private static int Percentile(int[] histogram, int total, double percentile)
    {
        int target = Math.Max(1, (int)Math.Ceiling(total * percentile));
        int cumulative = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
            {
                return value;
            }
        }

        return histogram.Length - 1;
    }

    /// <summary>
    /// Executes the final Pair Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The final Pair Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: (.+?-final-before\.png) and (.+?-final-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex FinalPairRegex();

    /// <summary>
    /// Executes the shadow Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The shadow Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-voxel-shadow-before\.png and (.+?-voxel-shadow-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex ShadowMaskRegex();

    /// <summary>
    /// Executes the normal Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The normal Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-normal-before\.png and (.+?-normal-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex NormalMaskRegex();

    /// <summary>
    /// Executes the position Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The position Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-position-before\.png and (.+?-position-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex PositionMaskRegex();

    /// <summary>
    /// Executes the voxel Material Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The voxel Material Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-voxel-albedo-before\.png and (.+?-voxel-albedo-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex VoxelMaterialMaskRegex();

    /// <summary>
    /// Executes the voxel Visibility Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The voxel Visibility Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-voxel-visibility-before\.png and (.+?-voxel-visibility-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex VoxelVisibilityMaskRegex();

    /// <summary>
    /// Executes the screen Lighting Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The screen Lighting Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-lighting-before\.png and (.+?-lighting-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenLightingMaskRegex();

    /// <summary>
    /// Executes the transport Components Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The transport Components Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-transport-components-before\.png and (.+?-transport-components-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex TransportComponentsMaskRegex();

    // Do not let the generic reflection matcher consume the later
    // "voxel-reflection" capture and silently validate the wrong channel.
    /// <summary>
    /// Executes the reflection Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The reflection Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?(?<!-voxel)-reflection-before\.png and (.+?(?<!-voxel)-reflection-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex ReflectionMaskRegex();

    /// <summary>Locates the clean pre-final source paired with a reflection-source diagnostic.</summary>
    /// <returns>The raw reflection-source regex used by the positive entity-reflection gate.</returns>
    [GeneratedRegex(@"and raw pre-final (.+?-reflection-source-raw\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex RawReflectionSourceRegex();

    /// <summary>Locates the dedicated native-resolution local-body carrier saved before composition.</summary>
    /// <returns>The exact local-body entity-mirror regex used by the body and overlay guard.</returns>
    [GeneratedRegex(@"and raw pre-final (.+?-local-body-entity-mirror-raw\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex RawEntityMirrorRegex();

    /// <summary>
    /// Locates the water-surface diagnostic paired with the reflection capture.
    /// </summary>
    /// <returns>The water mask regex used by the silhouette-continuity guard.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-water-before\.png and (.+?-water-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex WaterMaskRegex();

    /// <summary>
    /// Executes the voxel Bounce Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The voxel Bounce Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-voxel-bounce-before\.png and (.+?-voxel-bounce-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex VoxelBounceMaskRegex();

    /// <summary>
    /// Executes the voxel Reflection Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The voxel Reflection Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-voxel-reflection-before\.png and (.+?-voxel-reflection-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex VoxelReflectionMaskRegex();

    /// <summary>
    /// Executes the wetness Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The wetness Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-wetness-before\.png and (.+?-wetness-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex WetnessMaskRegex();

    /// <summary>
    /// Executes the pbr Material Mask Regex step used by the deterministic runtime Image Validator fixture.
    /// </summary>
    /// <returns>The pbr Material Mask Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Comparison capture saved: .+?-material-before\.png and (.+?-material-vintagertx\.png)", RegexOptions.IgnoreCase)]
    private static partial Regex PbrMaterialMaskRegex();

    /// <summary>Matches the exact top-left framebuffer anchor projected for a projectile contact.</summary>
    /// <returns>Sequence, kind, pixel X/Y and viewport dimensions.</returns>
    [GeneratedRegex(@"Projectile impact screen anchor:\s*sequence=(\d+),\s*projectile=(stone|arrow),\s*pixel=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*viewport=(\d+)x(\d+)\.", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectileImpactScreenAnchorRegex();

    /// <summary>Matches the callback sequence whose field response capture was actually queued.</summary>
    /// <returns>Projectile kind, source sequence and response-label projectile kind.</returns>
    [GeneratedRegex(@"Projectile-synchronized capture queued:\s*projectile=(stone|arrow),\s*source-sequence=(\d+),[^\r\n]*\bview=LiquidSurfaceField,\s*label=projectile-(stone|arrow)-surface-field\.", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectileSynchronizedSurfaceCaptureRegex();

    /// <summary>Matches wavelength/amplitude evidence emitted for one authoritative impact packet.</summary>
    /// <returns>Callback sequence, packet sequence, amplitude and wavelength.</returns>
    [GeneratedRegex(@"Projectile surface impact applied:\s*sequence=(\d+),[^\r\n]*?subgrid-packet-sequence=(\d+),[^\r\n]*?amplitude=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*m,\s*wavelength=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*m[.,]", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectileSurfaceImpactPacketRegex();

    /// <summary>Matches one contact/control differential frame projected from the liquid plane.</summary>
    /// <returns>Sequence, kind, role, centre, X/Z tangents and exact solver support radius.</returns>
    [GeneratedRegex(@"Projectile impact surface frame:\s*sequence=(\d+),\s*projectile=(stone|arrow),\s*role=(contact|control-[1-4]),\s*center=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*dscreen-dworld-x=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*dscreen-dworld-z=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*support-radius=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*m\.", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectileImpactSurfaceFrameRegex();
}
