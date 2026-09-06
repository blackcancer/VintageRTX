using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>
/// Adds the deterministic outdoor render-lab vegetation witness to the runtime image validator.
/// </summary>
internal static partial class RuntimeImageValidator
{
    /// <summary>Exact activation prefix emitted after the official far cascade becomes usable.</summary>
    private const string NativeSolarShadowLogToken =
        "[VintageRTX] Native solar shadow detail active:";

    /// <summary>Exact real-block evidence proving the render-lab tallgrass uses crossed planes.</summary>
    private const string TallgrassGeometryLogToken =
        "Geometry evidence game:tallgrass-tall-free: kind=StaticComplex, detailed non-cube=True, crossed planes=True";

    /// <summary>Measured morphology of the localized tallgrass solar-shadow candidate.</summary>
    /// <param name="Compatible">Whether the image and deterministic ROI can be evaluated.</param>
    /// <param name="CandidatePixels">Thresholded solar-shadow pixels in the selected component.</param>
    /// <param name="BoundingWidth">Candidate bounding-box width in pixels.</param>
    /// <param name="BoundingHeight">Candidate bounding-box height in pixels.</param>
    /// <param name="RoiCoverage">Candidate area divided by the deterministic ROI area.</param>
    /// <param name="RectangularFill">Candidate area divided by its bounding-box area.</param>
    /// <param name="InteriorGapRatio">Unshadowed pixels enclosed by occupied row spans.</param>
    /// <param name="RowOccupancyVariation">Coefficient of variation of occupied row widths.</param>
    /// <param name="MeetsGate">Whether the shadow is localized and inconsistent with a solid square caster.</param>
    internal readonly record struct VegetationShadowAssessment(
        bool Compatible,
        int CandidatePixels,
        int BoundingWidth,
        int BoundingHeight,
        double RoiCoverage,
        double RectangularFill,
        double InteriorGapRatio,
        double RowOccupancyVariation,
        bool MeetsGate);

    /// <summary>
    /// Selects the image-space vegetation gate only for the authored outdoor render-lab witness.
    /// </summary>
    /// <param name="log">Merged runtime log used to identify the deterministic witness.</param>
    /// <returns>Whether the render-lab tallgrass image contract applies.</returns>
    internal static bool ShouldValidateRenderLabVegetationSunShadow(string log) =>
        log.Contains("Render lab camera applied:", StringComparison.OrdinalIgnoreCase)
        && log.Contains(TallgrassGeometryLogToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Requires the native cascade activation log and measures the real tallgrass shadow in the
    /// normalized open-sky grass receiver region of the locked render-lab camera.
    /// </summary>
    /// <param name="log">Merged runtime log containing the solar-shadow diagnostic capture.</param>
    /// <returns>Actionable failures, or an empty sequence when the witness passes.</returns>
    internal static IReadOnlyList<string> ValidateRenderLabVegetationSunShadow(string log)
    {
        List<string> failures = [];
        if (!log.Contains(NativeSolarShadowLogToken, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"missing log token: {NativeSolarShadowLogToken}");
        }
        if (!log.Contains(TallgrassGeometryLogToken, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("render-lab tallgrass crossed-plane geometry evidence is missing");
        }

        System.Text.RegularExpressions.Match match = NativeSunShadowMaskRegex()
            .Matches(log)
            .Cast<System.Text.RegularExpressions.Match>()
            .LastOrDefault()
            ?? System.Text.RegularExpressions.Match.Empty;
        if (!match.Success)
        {
            failures.Add("render-lab native vegetation solar-shadow capture was not logged");
            return failures;
        }

        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
        using SKBitmap? shadow = File.Exists(path)
            ? SKBitmap.Decode(File.ReadAllBytes(path))
            : null;
        if (shadow is null)
        {
            failures.Add($"render-lab native vegetation solar-shadow PNG could not be decoded: '{path}'");
            return failures;
        }

        VegetationShadowAssessment assessment = MeasureRenderLabVegetationSunShadow(shadow);
        Console.WriteLine(
            $"Tallgrass native solar shadow: pixels={assessment.CandidatePixels}, "
            + $"bounds={assessment.BoundingWidth}x{assessment.BoundingHeight}, "
            + $"ROI={assessment.RoiCoverage:P2}, fill={assessment.RectangularFill:P1}, "
            + $"gaps={assessment.InteriorGapRatio:P1}, "
            + $"row variation={assessment.RowOccupancyVariation:0.00}.");
        if (!assessment.MeetsGate)
        {
            failures.Add(
                "render-lab tallgrass solar shadow is absent, global, or a solid rectangular "
                + $"fallback (pixels={assessment.CandidatePixels}, ROI={assessment.RoiCoverage:P2}, "
                + $"fill={assessment.RectangularFill:P1}, gaps={assessment.InteriorGapRatio:P1}, "
                + $"row variation={assessment.RowOccupancyVariation:0.00})");
        }
        return failures;
    }

    /// <summary>
    /// Measures an eight-connected solar-shadow component in the fixed normalized region occupied
    /// by the render-lab tallgrass and its open-sky floor receiver. In the native-provenance ABI,
    /// green is alpha-tested native occlusion and blue is near/far cascade support; both are
    /// required so coarse voxel occupancy alone cannot satisfy the morphology gate.
    /// </summary>
    /// <param name="shadow">Decoded voxel-shadow diagnostic.</param>
    /// <returns>Scale-independent localization and alpha-cut silhouette evidence.</returns>
    internal static VegetationShadowAssessment MeasureRenderLabVegetationSunShadow(
        SKBitmap shadow) => MeasureVegetationSunShadowRegion(
            shadow,
            0.31,
            0.43,
            0.55,
            0.64);

    /// <summary>
    /// Measures the strongest localized alpha-cut solar-shadow component inside a normalized ROI.
    /// </summary>
    /// <param name="shadow">Decoded voxel-shadow diagnostic.</param>
    /// <param name="minimumNormalizedX">Inclusive normalized left edge.</param>
    /// <param name="maximumNormalizedX">Exclusive normalized right edge.</param>
    /// <param name="minimumNormalizedY">Inclusive normalized top edge.</param>
    /// <param name="maximumNormalizedY">Exclusive normalized bottom edge.</param>
    /// <returns>Scale-independent localization and cutout morphology evidence.</returns>
    private static VegetationShadowAssessment MeasureVegetationSunShadowRegion(
        SKBitmap shadow,
        double minimumNormalizedX,
        double maximumNormalizedX,
        double minimumNormalizedY,
        double maximumNormalizedY)
    {
        if (shadow.Width < 64 || shadow.Height < 64)
        {
            return default;
        }

        int minimumX = Math.Clamp(
            (int)Math.Floor(shadow.Width * minimumNormalizedX),
            0,
            shadow.Width - 1);
        int maximumX = Math.Clamp(
            (int)Math.Ceiling(shadow.Width * maximumNormalizedX),
            minimumX + 1,
            shadow.Width);
        int minimumY = Math.Clamp(
            (int)Math.Floor(shadow.Height * minimumNormalizedY),
            0,
            shadow.Height - 1);
        int maximumY = Math.Clamp(
            (int)Math.Ceiling(shadow.Height * maximumNormalizedY),
            minimumY + 1,
            shadow.Height);
        int roiWidth = maximumX - minimumX;
        int roiHeight = maximumY - minimumY;
        int roiPixels = checked(roiWidth * roiHeight);
        bool[] mask = new bool[roiPixels];
        for (int y = 0; y < roiHeight; y++)
        {
            for (int x = 0; x < roiWidth; x++)
            {
                SKColor sample = shadow.GetPixel(minimumX + x, minimumY + y);
                mask[y * roiWidth + x] = sample.Green >= 18
                    && sample.Blue >= 18;
            }
        }

        bool[] visited = new bool[roiPixels];
        ComponentBounds best = default;
        int minimumCandidatePixels = Math.Max(10, roiPixels / 7_500);
        int maximumCandidateBounds = Math.Max(256, (int)Math.Ceiling(roiPixels * 0.22));
        int[] queue = new int[roiPixels];
        for (int seed = 0; seed < roiPixels; seed++)
        {
            if (!mask[seed] || visited[seed])
            {
                continue;
            }

            int head = 0;
            int tail = 0;
            queue[tail++] = seed;
            visited[seed] = true;
            ComponentBounds component = new(
                0,
                seed % roiWidth,
                seed % roiWidth,
                seed / roiWidth,
                seed / roiWidth);
            while (head < tail)
            {
                int current = queue[head++];
                int x = current % roiWidth;
                int y = current / roiWidth;
                component = component.Include(x, y);
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }
                        int neighborX = x + offsetX;
                        int neighborY = y + offsetY;
                        if ((uint)neighborX >= (uint)roiWidth
                            || (uint)neighborY >= (uint)roiHeight)
                        {
                            continue;
                        }
                        int neighbor = neighborY * roiWidth + neighborX;
                        if (!mask[neighbor] || visited[neighbor])
                        {
                            continue;
                        }
                        visited[neighbor] = true;
                        queue[tail++] = neighbor;
                    }
                }
            }

            int boundsArea = component.Width * component.Height;
            if (component.Pixels >= minimumCandidatePixels
                && component.Width >= 3
                && component.Height >= 3
                && boundsArea <= maximumCandidateBounds
                && component.Pixels > best.Pixels)
            {
                best = component;
            }
        }

        if (best.Pixels == 0)
        {
            return new VegetationShadowAssessment(
                true, 0, 0, 0, 0.0, 1.0, 0.0, 0.0, false);
        }

        int boundingArea = best.Width * best.Height;
        int enclosedGaps = 0;
        List<int> occupiedRows = [];
        for (int y = best.MinimumY; y <= best.MaximumY; y++)
        {
            int first = -1;
            int last = -1;
            int occupied = 0;
            for (int x = best.MinimumX; x <= best.MaximumX; x++)
            {
                if (!mask[y * roiWidth + x])
                {
                    continue;
                }
                first = first < 0 ? x : first;
                last = x;
                occupied++;
            }
            if (occupied == 0)
            {
                continue;
            }
            occupiedRows.Add(occupied);
            enclosedGaps += last - first + 1 - occupied;
        }

        double meanRow = occupiedRows.Count > 0 ? occupiedRows.Average() : 0.0;
        double rowVariance = occupiedRows.Count > 0
            ? occupiedRows.Average(value => (value - meanRow) * (value - meanRow))
            : 0.0;
        double rowVariation = meanRow > 0.0 ? Math.Sqrt(rowVariance) / meanRow : 0.0;
        double roiCoverage = (double)best.Pixels / roiPixels;
        double rectangularFill = (double)best.Pixels / boundingArea;
        double interiorGapRatio = (double)enclosedGaps / boundingArea;
        bool localized = roiCoverage is >= 0.00008 and <= 0.10;
        bool alphaCut = rectangularFill is >= 0.02 and <= 0.76
            && (rectangularFill <= 0.55
                || interiorGapRatio >= 0.015
                || rowVariation >= 0.22);
        return new VegetationShadowAssessment(
            true,
            best.Pixels,
            best.Width,
            best.Height,
            roiCoverage,
            rectangularFill,
            interiorGapRatio,
            rowVariation,
            localized && alphaCut);
    }

    /// <summary>Eight-connected component extent in ROI-local pixels.</summary>
    /// <param name="Pixels">Number of thresholded pixels.</param>
    /// <param name="MinimumX">Inclusive left extent.</param>
    /// <param name="MaximumX">Inclusive right extent.</param>
    /// <param name="MinimumY">Inclusive top extent.</param>
    /// <param name="MaximumY">Inclusive bottom extent.</param>
    private readonly record struct ComponentBounds(
        int Pixels,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY)
    {
        /// <summary>Gets the component bounding width.</summary>
        public int Width => MaximumX - MinimumX + 1;

        /// <summary>Gets the component bounding height.</summary>
        public int Height => MaximumY - MinimumY + 1;

        /// <summary>Returns this component expanded to include one visited pixel.</summary>
        /// <param name="x">ROI-local horizontal coordinate.</param>
        /// <param name="y">ROI-local vertical coordinate.</param>
        /// <returns>Updated immutable component bounds.</returns>
        public ComponentBounds Include(int x, int y) => new(
            Pixels + 1,
            Math.Min(MinimumX, x),
            Math.Max(MaximumX, x),
            Math.Min(MinimumY, y),
            Math.Max(MaximumY, y));
    }
}
