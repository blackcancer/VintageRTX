using System.IO.Compression;
using System.Text.Json;
using SkiaSharp;

namespace VintageRTX.PbrTextureGenerator;

internal sealed class NormalMapAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public NormalAnalysisReport Analyze(string packPath)
    {
        string fullPath = Path.GetFullPath(packPath);
        using ZipArchive archive = ZipFile.OpenRead(fullPath);
        ZipArchiveEntry manifestEntry = archive.Entries.Single(entry =>
            entry.FullName.EndsWith(
                "/config/vintagertx/pbr-manifest.json",
                StringComparison.Ordinal));
        using Stream manifestStream = manifestEntry.Open();
        PbrPackManifest manifest = JsonSerializer.Deserialize<PbrPackManifest>(
            manifestStream,
            JsonOptions)
            ?? throw new InvalidDataException("PBR manifest could not be decoded.");

        Dictionary<string, ZipArchiveEntry> entries = archive.Entries.ToDictionary(
            entry => Normalize(entry.FullName),
            StringComparer.Ordinal);
        List<float> pixelAngles = new(capacity: 2_000_000);
        List<float> textureRmsAngles = new(manifest.Textures.Count);
        Dictionary<string, List<float>> profileRmsAngles = new(StringComparer.Ordinal);
        double vectorLengthErrorSum = 0.0;
        double maximumVectorLengthError = 0.0;
        double xSum = 0.0;
        double ySum = 0.0;
        double zSum = 0.0;
        long pixelCount = 0;

        foreach (PbrPackTexture texture in manifest.Textures)
        {
            string normalPath = AssetToEntryPath(texture.Normal.Asset);
            if (!entries.TryGetValue(normalPath, out ZipArchiveEntry? normalEntry))
            {
                throw new InvalidDataException($"Normal map '{texture.Normal.Asset}' is missing.");
            }

            using Stream normalStream = normalEntry.Open();
            using SKBitmap bitmap = DecodeUnpremultiplied(normalStream, texture.Normal.Asset);
            double squaredAngleSum = 0.0;
            long texturePixelCount = 0;
            foreach (SKColor color in bitmap.Pixels)
            {
                if (color.Alpha == 0)
                {
                    continue;
                }

                float x = ((color.Red / 255f) * 2f) - 1f;
                float y = ((color.Green / 255f) * 2f) - 1f;
                float z = ((color.Blue / 255f) * 2f) - 1f;
                float vectorLength = MathF.Sqrt((x * x) + (y * y) + (z * z));
                float tangentLength = MathF.Sqrt((x * x) + (y * y));
                float angleDegrees = MathF.Atan2(tangentLength, z)
                    * (180f / MathF.PI);
                float lengthError = MathF.Abs(vectorLength - 1f);

                pixelAngles.Add(angleDegrees);
                squaredAngleSum += angleDegrees * angleDegrees;
                vectorLengthErrorSum += lengthError;
                maximumVectorLengthError = Math.Max(maximumVectorLengthError, lengthError);
                xSum += x;
                ySum += y;
                zSum += z;
                pixelCount++;
                texturePixelCount++;
            }

            float textureRms = texturePixelCount == 0
                ? 0f
                : MathF.Sqrt((float)(squaredAngleSum / texturePixelCount));
            textureRmsAngles.Add(textureRms);
            if (!profileRmsAngles.TryGetValue(texture.Profile, out List<float>? profileValues))
            {
                profileValues = [];
                profileRmsAngles.Add(texture.Profile, profileValues);
            }

            profileValues.Add(textureRms);
        }

        if (pixelCount == 0)
        {
            throw new InvalidDataException("PBR pack does not contain any opaque normal-map texel to analyze.");
        }

        pixelAngles.Sort();
        textureRmsAngles.Sort();
        NormalProfileAnalysis[] profiles = profileRmsAngles
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                pair.Value.Sort();
                return new NormalProfileAnalysis(
                    pair.Key,
                    pair.Value.Count,
                    pair.Value.Average(),
                    Quantile(pair.Value, 0.50),
                    Quantile(pair.Value, 0.90),
                    pair.Value.Count(value => value < 4f));
            })
            .ToArray();

        return new NormalAnalysisReport(
            fullPath,
            manifest.Generator.Version,
            manifest.Textures.Count,
            pixelCount,
            pixelAngles.Average(value => (double)value),
            Quantile(pixelAngles, 0.50),
            Quantile(pixelAngles, 0.90),
            Quantile(pixelAngles, 0.95),
            Quantile(pixelAngles, 0.99),
            textureRmsAngles.Average(value => (double)value),
            Quantile(textureRmsAngles, 0.50),
            Quantile(textureRmsAngles, 0.90),
            textureRmsAngles.Count(value => value < 2f),
            textureRmsAngles.Count(value => value < 4f),
            textureRmsAngles.Count(value => value < 8f),
            vectorLengthErrorSum / Math.Max(1, pixelCount),
            maximumVectorLengthError,
            xSum / Math.Max(1, pixelCount),
            ySum / Math.Max(1, pixelCount),
            zSum / Math.Max(1, pixelCount),
            profiles);
    }

    private static string AssetToEntryPath(string asset)
    {
        string normalized = Normalize(asset);
        int separator = normalized.IndexOf(':');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new InvalidDataException($"Normal AssetLocation '{asset}' is invalid.");
        }

        return $"assets/{normalized[..separator]}/{normalized[(separator + 1)..]}";
    }

    private static SKBitmap DecodeUnpremultiplied(Stream stream, string asset)
    {
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        using SKData data = SKData.CreateCopy(buffer.ToArray());
        using SKCodec codec = SKCodec.Create(data)
            ?? throw new InvalidDataException($"Normal map '{asset}' could not be decoded.");
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
            throw new InvalidDataException($"Normal map '{asset}' could not be decoded ({result}).");
        }

        return bitmap;
    }

    private static float Quantile(IReadOnlyList<float> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0f;
        }

        int index = (int)Math.Round((sorted.Count - 1) * fraction, MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string Normalize(string value) =>
        value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}

internal sealed record NormalAnalysisReport(
    string PackPath,
    string AlgorithmVersion,
    int TextureCount,
    long OpaquePixelCount,
    double MeanPixelAngleDegrees,
    float P50PixelAngleDegrees,
    float P90PixelAngleDegrees,
    float P95PixelAngleDegrees,
    float P99PixelAngleDegrees,
    double MeanTextureRmsAngleDegrees,
    float P50TextureRmsAngleDegrees,
    float P90TextureRmsAngleDegrees,
    int TextureCountBelow2Degrees,
    int TextureCountBelow4Degrees,
    int TextureCountBelow8Degrees,
    double MeanVectorLengthError,
    double MaximumVectorLengthError,
    double MeanX,
    double MeanY,
    double MeanZ,
    IReadOnlyList<NormalProfileAnalysis> Profiles);

internal sealed record NormalProfileAnalysis(
    string Profile,
    int TextureCount,
    double MeanTextureRmsAngleDegrees,
    float P50TextureRmsAngleDegrees,
    float P90TextureRmsAngleDegrees,
    int TextureCountBelow4Degrees);
