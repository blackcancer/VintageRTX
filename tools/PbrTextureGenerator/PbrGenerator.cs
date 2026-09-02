using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace VintageRTX.PbrTextureGenerator;

internal sealed class PbrGenerator
{
    public const string AlgorithmVersion = "vintagertx-pbr-v6";
    // Luminance is weak evidence of height: painted pixels must not become deep
    // embossing. The offline fallback therefore stays below a 1:1 slope while
    // authored mod normals remain untouched by this conservative policy.
    internal const float GeneratedNormalVisibilityGain = 0.90f;
    // Keep the maximum tangent tilt below 16 degrees. Two opposite fallback
    // gradients therefore remain below a 32-degree discontinuity, leaving a
    // safe margin beneath the runtime's 35-degree embossed-shimmer gate.
    internal const float GeneratedNormalMaximumSlope = 0.28f;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GenerationReport Generate(GenerationRequest request)
    {
        string outputRoot = Path.GetFullPath(request.OutputRoot);
        string? assetsRoot = request.AssetsRoot is null ? null : Path.GetFullPath(request.AssetsRoot);
        IReadOnlyList<SourceTexture> sources = DiscoverSources(request.Inputs, assetsRoot);

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("No source PNG textures matched the requested inputs.");
        }

        Directory.CreateDirectory(outputRoot);
        List<GeneratedTexture> generated = new(sources.Count);

        foreach (SourceTexture source in sources)
        {
            MaterialProfile profile = MaterialProfile.Resolve(request.Profile, source.RelativePath);
            generated.Add(GenerateOne(source, outputRoot, profile, request.FlipGreen, request.SkipExisting));
        }

        GenerationManifest manifest = new(
            AlgorithmVersion,
            request.FlipGreen ? "OpenGL tangent space, green channel flipped by request" : "OpenGL tangent space (+Y)",
            generated.OrderBy(item => item.Source, StringComparer.Ordinal).ToArray());

        string manifestPath = Path.Combine(outputRoot, "pbr-manifest.json");
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        WriteAtomically(manifestPath, manifestBytes);

        return new GenerationReport(outputRoot, manifestPath, generated);
    }

    private static GeneratedTexture GenerateOne(
        SourceTexture source,
        string outputRoot,
        MaterialProfile profile,
        bool flipGreen,
        bool skipExisting)
    {
        byte[] sourceBytes = File.ReadAllBytes(source.FullPath);
        string sourceHashBefore = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();

        using SKBitmap decoded = SKBitmap.Decode(sourceBytes)
            ?? throw new InvalidDataException($"Skia could not decode '{source.FullPath}'.");
        using SKBitmap sourceBitmap = decoded.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidDataException($"Skia could not convert '{source.FullPath}' to RGBA8.");

        TextureMaps maps = BuildMaps(sourceBitmap, profile, source.RelativePath, flipGreen);
        string relativeDirectory = Path.GetDirectoryName(source.RelativePath) ?? string.Empty;
        string outputDirectory = Path.Combine(outputRoot, relativeDirectory);
        string sourceName = Path.GetFileNameWithoutExtension(source.RelativePath);
        string normalPath = Path.Combine(outputDirectory, sourceName + "_n.png");
        string roughnessPath = Path.Combine(outputDirectory, sourceName + "_r.png");
        string metallicPath = Path.Combine(outputDirectory, sourceName + "_m.png");
        string emissivePath = Path.Combine(outputDirectory, sourceName + "_e.png");

        Directory.CreateDirectory(outputDirectory);
        if (!skipExisting || !File.Exists(normalPath))
        {
            WriteAtomically(normalPath, maps.NormalPng);
        }

        if (!skipExisting || !File.Exists(roughnessPath))
        {
            WriteAtomically(roughnessPath, maps.RoughnessPng);
        }

        if (!skipExisting || !File.Exists(metallicPath))
        {
            WriteAtomically(metallicPath, maps.MetallicPng);
        }

        if (!skipExisting || !File.Exists(emissivePath))
        {
            WriteAtomically(emissivePath, maps.EmissivePng);
        }

        string sourceHashAfter = Sha256File(source.FullPath);
        if (!sourceHashBefore.Equals(sourceHashAfter, StringComparison.Ordinal))
        {
            throw new IOException($"Source texture changed while it was being read: '{source.FullPath}'.");
        }

        return new GeneratedTexture(
            NormalizeManifestPath(source.RelativePath),
            NormalizeManifestPath(Path.GetRelativePath(outputRoot, normalPath)),
            NormalizeManifestPath(Path.GetRelativePath(outputRoot, roughnessPath)),
            NormalizeManifestPath(Path.GetRelativePath(outputRoot, metallicPath)),
            NormalizeManifestPath(Path.GetRelativePath(outputRoot, emissivePath)),
            sourceHashBefore,
            Sha256File(normalPath),
            Sha256File(roughnessPath),
            Sha256File(metallicPath),
            Sha256File(emissivePath),
            profile.Name,
            sourceBitmap.Width,
            sourceBitmap.Height,
            new ProfileParameters(
                profile.HeightStrength,
                profile.FineWeight,
                profile.CoarseWeight,
                profile.BilateralRadius,
                profile.EdgeSigma,
                profile.BaseRoughness,
                profile.DetailRoughness,
                profile.DarkRoughness,
                profile.Metallic));
    }

    private static TextureMaps BuildMaps(
        SKBitmap bitmap,
        MaterialProfile profile,
        string sourcePath,
        bool flipGreen)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        SKColor[] pixels = bitmap.Pixels;
        float[] heightMap = new float[pixels.Length];
        float[] alphaMap = new float[pixels.Length];

        for (int index = 0; index < pixels.Length; index++)
        {
            SKColor pixel = pixels[index];
            float red = SrgbToLinear(pixel.Red / 255f);
            float green = SrgbToLinear(pixel.Green / 255f);
            float blue = SrgbToLinear(pixel.Blue / 255f);
            heightMap[index] = (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);
            alphaMap[index] = pixel.Alpha / 255f;
        }

        float[] smoothHeight = BilateralFilter(
            heightMap,
            alphaMap,
            width,
            height,
            profile.BilateralRadius,
            profile.EdgeSigma);

        SKColor[] normalPixels = new SKColor[pixels.Length];
        SKColor[] roughnessPixels = new SKColor[pixels.Length];
        SKColor[] metallicPixels = new SKColor[pixels.Length];
        SKColor[] emissivePixels = new SKColor[pixels.Length];
        bool emissiveCandidate = IsEmissiveCandidate(sourcePath);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                byte alpha = pixels[index].Alpha;
                if (alpha == 0)
                {
                    normalPixels[index] = new SKColor(128, 128, 255, 0);
                    roughnessPixels[index] = new SKColor(0, 0, 0, 0);
                    metallicPixels[index] = new SKColor(0, 0, 0, 0);
                    emissivePixels[index] = new SKColor(0, 0, 0, 0);
                    continue;
                }

                (float fineX, float fineY) = ScharrGradient(heightMap, width, height, x, y, 1);
                int coarseRadius = Math.Min(2, Math.Max(1, Math.Min(width, height) / 8));
                (float coarseX, float coarseY) = ScharrGradient(smoothHeight, width, height, x, y, coarseRadius);
                float gradientX = profile.HeightStrength * ((profile.FineWeight * fineX) + (profile.CoarseWeight * coarseX));
                float gradientY = profile.HeightStrength * ((profile.FineWeight * fineY) + (profile.CoarseWeight * coarseY));
                gradientX *= GeneratedNormalVisibilityGain;
                gradientY *= GeneratedNormalVisibilityGain;
                float generatedSlope = MathF.Sqrt((gradientX * gradientX) + (gradientY * gradientY));
                if (generatedSlope > GeneratedNormalMaximumSlope)
                {
                    float slopeLimit = GeneratedNormalMaximumSlope / generatedSlope;
                    gradientX *= slopeLimit;
                    gradientY *= slopeLimit;
                }

                float nx = -gradientX;
                // Image rows increase downwards while OpenGL texture V increases upwards.
                float ny = flipGreen ? -gradientY : gradientY;
                float nz = 1f;
                float inverseLength = 1f / MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                nx *= inverseLength;
                ny *= inverseLength;
                nz *= inverseLength;

                normalPixels[index] = new SKColor(
                    EncodeSigned(nx),
                    EncodeSigned(ny),
                    EncodeSigned(nz),
                    alpha);

                float localVariance = LocalVariance(heightMap, alphaMap, width, height, x, y);
                float gradientMagnitude = MathF.Sqrt((fineX * fineX) + (fineY * fineY));
                float detail = Clamp01((gradientMagnitude * 8f) + (MathF.Sqrt(localVariance) * 2f));
                float luminance = heightMap[index];
                float roughness = profile.BaseRoughness
                    + (profile.DetailRoughness * (detail - 0.35f))
                    + (profile.DarkRoughness * (0.5f - luminance));
                byte roughnessByte = EncodeUnit(Math.Clamp(roughness, 0.04f, 0.98f));
                roughnessPixels[index] = new SKColor(roughnessByte, roughnessByte, roughnessByte, alpha);

                byte metallicByte = EncodeUnit(profile.Metallic);
                metallicPixels[index] = new SKColor(metallicByte, metallicByte, metallicByte, alpha);

                SKColor sourcePixel = pixels[index];
                float maximum = Math.Max(sourcePixel.Red, Math.Max(sourcePixel.Green, sourcePixel.Blue)) / 255f;
                float minimum = Math.Min(sourcePixel.Red, Math.Min(sourcePixel.Green, sourcePixel.Blue)) / 255f;
                float saturation = maximum <= 0.001f ? 0f : (maximum - minimum) / maximum;
                float brightness = SmoothStep(0.48f, 0.90f, maximum);
                // A bright white texel is commonly a painted highlight, snow,
                // glass glare or an atlas edge. Requiring chroma prevents those
                // features from becoming emissive merely because the asset path
                // contains lantern/torch/glow, while retaining coloured flames.
                float chromaConfidence = SmoothStep(0.10f, 0.32f, saturation);
                float emissive = emissiveCandidate
                    ? brightness * chromaConfidence * (0.55f + (0.45f * saturation))
                    : 0f;
                byte emissiveByte = EncodeUnit(emissive);
                emissivePixels[index] = new SKColor(emissiveByte, emissiveByte, emissiveByte, alpha);
            }
        }

        return new TextureMaps(
            EncodePng(width, height, normalPixels),
            EncodePng(width, height, roughnessPixels),
            EncodePng(width, height, metallicPixels),
            EncodePng(width, height, emissivePixels));
    }

    private static bool IsEmissiveCandidate(string sourcePath)
    {
        string path = sourcePath.Replace('\\', '/').ToLowerInvariant();
        if (path.Contains("_off", StringComparison.Ordinal)
            || path.Contains("-off", StringComparison.Ordinal)
            || path.Contains("/unlit", StringComparison.Ordinal))
        {
            return false;
        }

        string[] markers =
        [
            "/torch", "torch-", "torch_", "/lantern", "lantern-", "lantern_",
            "/lamp", "lamp-", "lamp_", "/candle", "candle-", "candle_",
            "/fire", "fire-", "fire_", "/flame", "flame-", "flame_",
            "/ember", "ember-", "ember_", "/glow", "glow-", "glow_",
            "/lava", "lava-", "lava_", "/magma", "magma-", "magma_",
            "/molten", "molten-", "molten_", "/forge", "forge-", "forge_",
            "/bloomery", "bloomery-", "bloomery_"
        ];
        return markers.Any(path.Contains);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Clamp01((value - edge0) / Math.Max(0.0001f, edge1 - edge0));
        return t * t * (3f - (2f * t));
    }

    private static float[] BilateralFilter(
        float[] source,
        float[] alpha,
        int width,
        int height,
        int radius,
        float rangeSigma)
    {
        if (radius <= 0)
        {
            return (float[])source.Clone();
        }

        float[] result = new float[source.Length];
        float inverseRange = 1f / Math.Max(0.0001f, 2f * rangeSigma * rangeSigma);
        float spatialSigma = Math.Max(0.75f, radius * 0.85f);
        float inverseSpatial = 1f / (2f * spatialSigma * spatialSigma);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int centerIndex = (y * width) + x;
                if (alpha[centerIndex] <= 0f)
                {
                    result[centerIndex] = source[centerIndex];
                    continue;
                }

                float center = source[centerIndex];
                float weightedSum = 0f;
                float weightSum = 0f;

                for (int offsetY = -radius; offsetY <= radius; offsetY++)
                {
                    for (int offsetX = -radius; offsetX <= radius; offsetX++)
                    {
                        int sampleX = Wrap(x + offsetX, width);
                        int sampleY = Wrap(y + offsetY, height);
                        int sampleIndex = (sampleY * width) + sampleX;
                        if (alpha[sampleIndex] <= 0f)
                        {
                            continue;
                        }

                        float delta = source[sampleIndex] - center;
                        float spatialDistance = (offsetX * offsetX) + (offsetY * offsetY);
                        float weight = MathF.Exp(-(spatialDistance * inverseSpatial) - ((delta * delta) * inverseRange));
                        weight *= alpha[sampleIndex];
                        weightedSum += source[sampleIndex] * weight;
                        weightSum += weight;
                    }
                }

                result[centerIndex] = weightSum > 0f ? weightedSum / weightSum : center;
            }
        }

        return result;
    }

    private static (float X, float Y) ScharrGradient(
        float[] source,
        int width,
        int height,
        int x,
        int y,
        int radius)
    {
        float topLeft = Sample(source, width, height, x - radius, y - radius);
        float top = Sample(source, width, height, x, y - radius);
        float topRight = Sample(source, width, height, x + radius, y - radius);
        float left = Sample(source, width, height, x - radius, y);
        float right = Sample(source, width, height, x + radius, y);
        float bottomLeft = Sample(source, width, height, x - radius, y + radius);
        float bottom = Sample(source, width, height, x, y + radius);
        float bottomRight = Sample(source, width, height, x + radius, y + radius);

        float gradientX = ((3f * topRight) + (10f * right) + (3f * bottomRight)
            - (3f * topLeft) - (10f * left) - (3f * bottomLeft)) / (32f * radius);
        float gradientY = ((3f * bottomLeft) + (10f * bottom) + (3f * bottomRight)
            - (3f * topLeft) - (10f * top) - (3f * topRight)) / (32f * radius);
        return (gradientX, gradientY);
    }

    private static float LocalVariance(
        float[] source,
        float[] alpha,
        int width,
        int height,
        int x,
        int y)
    {
        float sum = 0f;
        float squaredSum = 0f;
        float weightSum = 0f;

        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int sampleX = Wrap(x + offsetX, width);
                int sampleY = Wrap(y + offsetY, height);
                int index = (sampleY * width) + sampleX;
                float weight = alpha[index];
                float value = source[index];
                sum += value * weight;
                squaredSum += value * value * weight;
                weightSum += weight;
            }
        }

        if (weightSum <= 0f)
        {
            return 0f;
        }

        float mean = sum / weightSum;
        return Math.Max(0f, (squaredSum / weightSum) - (mean * mean));
    }

    private static IReadOnlyList<SourceTexture> DiscoverSources(IReadOnlyList<string> inputs, string? assetsRoot)
    {
        Dictionary<string, SourceTexture> sources = new(StringComparer.OrdinalIgnoreCase);

        foreach (string rawInput in inputs)
        {
            string resolvedInput = ResolveInput(rawInput, assetsRoot);
            if (File.Exists(resolvedInput))
            {
                AddSource(resolvedInput, DetermineRelativeRoot(resolvedInput, assetsRoot, isDirectory: false), sources);
                continue;
            }

            if (Directory.Exists(resolvedInput))
            {
                string relativeRoot = DetermineRelativeRoot(resolvedInput, assetsRoot, isDirectory: true);
                foreach (string file in Directory.EnumerateFiles(resolvedInput, "*.png", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    AddSource(file, relativeRoot, sources);
                }

                continue;
            }

            throw new FileNotFoundException($"Input texture or directory was not found: '{resolvedInput}'.", resolvedInput);
        }

        return sources.Values.OrderBy(source => source.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void AddSource(
        string fullPath,
        string relativeRoot,
        IDictionary<string, SourceTexture> sources)
    {
        string name = Path.GetFileNameWithoutExtension(fullPath);
        if (name.EndsWith("_n", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_r", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_m", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_e", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string canonicalPath = Path.GetFullPath(fullPath);
        string relativePath = Path.GetRelativePath(relativeRoot, canonicalPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Source '{canonicalPath}' is outside relative root '{relativeRoot}'.");
        }

        sources[canonicalPath] = new SourceTexture(canonicalPath, relativePath);
    }

    private static string ResolveInput(string input, string? assetsRoot)
    {
        if (Path.IsPathFullyQualified(input))
        {
            return Path.GetFullPath(input);
        }

        return Path.GetFullPath(Path.Combine(assetsRoot ?? Environment.CurrentDirectory, input));
    }

    private static string DetermineRelativeRoot(string input, string? assetsRoot, bool isDirectory)
    {
        if (assetsRoot is not null)
        {
            string relative = Path.GetRelativePath(assetsRoot, input);
            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Input '{input}' is outside assets root '{assetsRoot}'.");
            }

            return assetsRoot;
        }

        return isDirectory ? input : Path.GetDirectoryName(input)!;
    }

    private static byte[] EncodePng(int width, int height, SKColor[] pixels)
    {
        using SKBitmap output = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        output.Pixels = pixels;
        using SKImage image = SKImage.FromBitmap(output);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + ".tmp");
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static float Sample(float[] source, int width, int height, int x, int y) =>
        source[(Wrap(y, height) * width) + Wrap(x, width)];

    private static int Wrap(int coordinate, int extent)
    {
        int wrapped = coordinate % extent;
        return wrapped < 0 ? wrapped + extent : wrapped;
    }

    private static float SrgbToLinear(float value) =>
        value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static byte EncodeSigned(float value) => EncodeUnit((value * 0.5f) + 0.5f);

    private static byte EncodeUnit(float value) =>
        (byte)Math.Clamp((int)MathF.Round(Clamp01(value) * 255f), 0, 255);

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

    private static string NormalizeManifestPath(string path) => path.Replace('\\', '/');

    private sealed record SourceTexture(string FullPath, string RelativePath);

    private sealed record TextureMaps(
        byte[] NormalPng,
        byte[] RoughnessPng,
        byte[] MetallicPng,
        byte[] EmissivePng);
}

internal sealed record GenerationRequest(
    IReadOnlyList<string> Inputs,
    string OutputRoot,
    string? AssetsRoot,
    string Profile,
    bool FlipGreen,
    bool SkipExisting);

internal sealed record GenerationReport(
    string OutputRoot,
    string ManifestPath,
    IReadOnlyList<GeneratedTexture> Textures);

internal sealed record GenerationManifest(
    string AlgorithmVersion,
    string NormalConvention,
    IReadOnlyList<GeneratedTexture> Textures);

internal sealed record GeneratedTexture(
    string Source,
    string Normal,
    string Roughness,
    string Metallic,
    string Emissive,
    string SourceSha256,
    string NormalSha256,
    string RoughnessSha256,
    string MetallicSha256,
    string EmissiveSha256,
    string Profile,
    int Width,
    int Height,
    ProfileParameters Parameters);

internal sealed record ProfileParameters(
    float HeightStrength,
    float FineWeight,
    float CoarseWeight,
    int BilateralRadius,
    float EdgeSigma,
    float BaseRoughness,
    float DetailRoughness,
    float DarkRoughness,
    float Metallic);
