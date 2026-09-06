using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace VintageRTX.PbrTextureGenerator;

internal sealed class PbrPackValidator
{
    private static readonly IReadOnlyDictionary<string, string> RequiredMapSuffixesV2 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["normal"] = "_n.png",
            ["roughness"] = "_r.png",
            ["metallic"] = "_m.png",
            ["emissive"] = "_e.png"
        };

    public PackValidationReport Validate(PackValidationRequest request)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string packPath = Path.GetFullPath(request.PackPath);
        string? sourceAssetsRoot = request.SourceAssetsRoot is null
            ? null
            : Path.GetFullPath(request.SourceAssetsRoot);
        List<string> errors = [];
        int manifestCount = 0;
        int textureCount = 0;
        int mapCount = 0;
        int sourceVerifiedCount = 0;
        int duplicateLogicalKeyCount = 0;
        HashSet<string> referencedMapEntries = new(StringComparer.Ordinal);

        if (!File.Exists(packPath))
        {
            return Finish($"Pack was not found: '{packPath}'.");
        }

        if (sourceAssetsRoot is not null && !Directory.Exists(sourceAssetsRoot))
        {
            return Finish($"Source assets directory was not found: '{sourceAssetsRoot}'.");
        }

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packPath);
            string[] duplicateZipEntries = archive.Entries
                .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            foreach (string duplicate in duplicateZipEntries)
            {
                errors.Add($"Duplicate ZIP entry: {duplicate}");
            }

            string? modId = ReadPackModId(archive, errors);
            ZipArchiveEntry[] manifests = archive.Entries
                .Where(entry => IsManifestEntry(entry.FullName))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
            manifestCount = manifests.Length;
            if (manifestCount == 0)
            {
                errors.Add("No indexed assets/<packdomain>/config/vintagertx/pbr-manifest.json entry was found.");
            }

            foreach (ZipArchiveEntry manifestEntry in manifests)
            {
                ValidateManifest(
                    archive,
                    manifestEntry,
                    modId,
                    sourceAssetsRoot,
                    request.Deep,
                    errors,
                    ref textureCount,
                    ref mapCount,
                    ref sourceVerifiedCount,
                    ref duplicateLogicalKeyCount,
                    referencedMapEntries);
            }

            foreach (ZipArchiveEntry entry in archive.Entries.Where(entry =>
                entry.FullName.StartsWith("assets/", StringComparison.Ordinal)
                && entry.FullName.Contains("/textures/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            {
                if (!RequiredMapSuffixesV2.Values.Any(suffix =>
                    entry.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Unexpected albedo/non-PBR PNG in pack texture tree: {entry.FullName}");
                }
                else if (!referencedMapEntries.Contains(entry.FullName))
                {
                    errors.Add($"Unindexed PBR sidecar in pack texture tree: {entry.FullName}");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException)
        {
            errors.Add($"Pack read failed: {exception.Message}");
        }

        stopwatch.Stop();
        return new PackValidationReport(
            packPath,
            Sha256File(packPath),
            manifestCount,
            textureCount,
            mapCount,
            sourceVerifiedCount,
            duplicateLogicalKeyCount,
            errors,
            stopwatch.ElapsedMilliseconds);

        PackValidationReport Finish(string error)
        {
            errors.Add(error);
            stopwatch.Stop();
            return new PackValidationReport(
                packPath,
                File.Exists(packPath) ? Sha256File(packPath) : string.Empty,
                manifestCount,
                textureCount,
                mapCount,
                sourceVerifiedCount,
                duplicateLogicalKeyCount,
                errors,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static void ValidateManifest(
        ZipArchive archive,
        ZipArchiveEntry manifestEntry,
        string? modId,
        string? sourceAssetsRoot,
        bool deep,
        ICollection<string> errors,
        ref int textureCount,
        ref int mapCount,
        ref int sourceVerifiedCount,
        ref int duplicateLogicalKeyCount,
        ISet<string> referencedMapEntries)
    {
        string packDomain = manifestEntry.FullName.Split('/')[1];
        using Stream stream = manifestEntry.Open();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        string? schema = GetString(root, "schema");
        int schemaVersion = GetInt(root, "schemaVersion");
        if (schema != PbrPackBuilder.SchemaId)
        {
            errors.Add($"{manifestEntry.FullName}: unexpected schema '{schema}'.");
        }

        if (schemaVersion is < 1 or > PbrPackBuilder.SchemaVersion)
        {
            errors.Add($"{manifestEntry.FullName}: unsupported schema version {schemaVersion}.");
        }

        if (modId is not null && !packDomain.Equals(modId, StringComparison.Ordinal))
        {
            errors.Add($"Manifest domain '{packDomain}' differs from modinfo modId '{modId}'.");
        }

        if (!TryGetProperty(root, "pack", out JsonElement pack)
            || GetString(pack, "modId") != packDomain)
        {
            errors.Add($"{manifestEntry.FullName}: pack.modId must equal '{packDomain}'.");
        }

        if (!TryGetProperty(root, "generator", out JsonElement generator)
            || GetString(generator, "execution") != "offline-only")
        {
            errors.Add($"{manifestEntry.FullName}: generator.execution must be 'offline-only'.");
        }

        string? defaultProvenance = GetString(root, "defaultProvenance");
        if (schemaVersion >= 4 && !IsMaterialProvenance(defaultProvenance))
        {
            errors.Add($"{manifestEntry.FullName}: schema v4 defaultProvenance must be 'generated' or 'authored'.");
        }

        if (!TryGetProperty(root, "textures", out JsonElement textures)
            || textures.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{manifestEntry.FullName}: textures array is missing.");
            return;
        }

        Dictionary<string, string> logicalKeys = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> requiredMaps = schemaVersion >= 2
            ? RequiredMapSuffixesV2
            : RequiredMapSuffixesV2.Where(pair => pair.Key is "normal" or "roughness")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        int textureIndex = 0;
        foreach (JsonElement texture in textures.EnumerateArray())
        {
            string context = $"{manifestEntry.FullName}:textures[{textureIndex}]";
            textureIndex++;
            textureCount++;
            string? entryProvenance = GetString(texture, "provenance");
            if (schemaVersion >= 4
                && entryProvenance is not null
                && !IsMaterialProvenance(entryProvenance))
            {
                errors.Add($"{context}: provenance must be 'generated' or 'authored'.");
            }
            if (!TryGetProperty(texture, "source", out JsonElement source))
            {
                errors.Add($"{context}: source is missing.");
                continue;
            }

            string? domain = GetString(source, "domain");
            string? sourcePath = GetString(source, "path");
            string? sourceSha = GetString(source, "sha256");
            string? origin = GetString(source, "origin") ?? domain;
            if (domain is null || origin is null || sourcePath is null || sourceSha is null)
            {
                errors.Add($"{context}: incomplete source identity.");
                continue;
            }

            string logicalKey = $"{domain}:{sourcePath}";
            if (!logicalKeys.TryAdd(logicalKey, origin))
            {
                duplicateLogicalKeyCount++;
                errors.Add($"{context}: duplicate logical source key '{logicalKey}'.");
            }

            int width = GetInt(texture, "width");
            int height = GetInt(texture, "height");
            if (width <= 0 || height <= 0)
            {
                errors.Add($"{context}: invalid dimensions {width}x{height}.");
            }

            if (sourceAssetsRoot is not null)
            {
                ValidateSource(
                    sourceAssetsRoot,
                    origin,
                    sourcePath,
                    sourceSha,
                    context,
                    errors,
                    ref sourceVerifiedCount);
            }

            foreach ((string propertyName, string suffix) in requiredMaps)
            {
                if (!TryGetProperty(texture, propertyName, out JsonElement map))
                {
                    errors.Add($"{context}: required {propertyName} map is missing.");
                    continue;
                }

                ValidateMap(
                    archive,
                    map,
                    packDomain,
                    propertyName,
                    suffix,
                    schemaVersion,
                    domain,
                    sourcePath,
                    width,
                    height,
                    deep,
                    context,
                    errors,
                    ref mapCount,
                    referencedMapEntries);
            }
        }
    }

    /// <summary>Checks one manifest provenance label without accepting implicit aliases.</summary>
    /// <param name="value">Candidate JSON string.</param>
    /// <returns>Whether the value names a supported material provenance.</returns>
    private static bool IsMaterialProvenance(string? value) =>
        value is not null
        && (value.Equals("generated", StringComparison.Ordinal)
            || value.Equals("authored", StringComparison.Ordinal));

    private static void ValidateSource(
        string sourceAssetsRoot,
        string origin,
        string sourcePath,
        string expectedSha,
        string context,
        ICollection<string> errors,
        ref int sourceVerifiedCount)
    {
        string root = Path.GetFullPath(sourceAssetsRoot);
        string candidate = Path.GetFullPath(Path.Combine(
            root,
            origin,
            sourcePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(candidate, root))
        {
            errors.Add($"{context}: source path escapes assets root: {origin}/{sourcePath}");
            return;
        }

        if (!File.Exists(candidate))
        {
            errors.Add($"{context}: source file is missing: {candidate}");
            return;
        }

        sourceVerifiedCount++;
        string actualSha = Sha256File(candidate);
        if (!actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{context}: source SHA-256 mismatch for {origin}/{sourcePath}.");
        }
    }

    private static void ValidateMap(
        ZipArchive archive,
        JsonElement map,
        string packDomain,
        string propertyName,
        string expectedSuffix,
        int schemaVersion,
        string sourceDomain,
        string sourcePath,
        int expectedWidth,
        int expectedHeight,
        bool deep,
        string context,
        ICollection<string> errors,
        ref int mapCount,
        ISet<string> referencedMapEntries)
    {
        string? asset = GetString(map, "asset");
        string? expectedSha = GetString(map, "sha256");
        if (asset is null || expectedSha is null)
        {
            errors.Add($"{context}: incomplete {propertyName} map reference.");
            return;
        }

        int separator = asset.IndexOf(':');
        if (separator <= 0 || separator == asset.Length - 1)
        {
            errors.Add($"{context}: invalid AssetLocation '{asset}'.");
            return;
        }

        string domain = asset[..separator];
        string assetPath = asset[(separator + 1)..];
        if (schemaVersion >= 3)
        {
            string expectedAssetPath = BuildAdjacentMapPath(sourcePath, expectedSuffix);
            if (!domain.Equals(sourceDomain, StringComparison.Ordinal)
                || !assetPath.Equals(expectedAssetPath, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{context}: {propertyName} must be the adjacent sidecar "
                    + $"'{sourceDomain}:{expectedAssetPath}', got '{asset}'.");
            }
        }
        else if (!domain.Equals(packDomain, StringComparison.Ordinal))
        {
            errors.Add(
                $"{context}: legacy {propertyName} asset domain '{domain}' must equal pack domain '{packDomain}'.");
        }

        if (!assetPath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{context}: {propertyName} asset must end with '{expectedSuffix}': {asset}");
        }

        string zipPath = $"assets/{domain}/{assetPath}";
        if (zipPath.Split('/').Any(part => part == ".."))
        {
            errors.Add($"{context}: map AssetLocation contains traversal: {asset}");
            return;
        }

        ZipArchiveEntry? entry = archive.GetEntry(zipPath);
        if (entry is null)
        {
            errors.Add($"{context}: map ZIP entry is missing: {zipPath}");
            return;
        }

        referencedMapEntries.Add(zipPath);
        mapCount++;
        byte[] bytes;
        using (Stream stream = entry.Open())
        using (MemoryStream buffer = new())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        string actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{context}: {propertyName} SHA-256 mismatch: {asset}");
        }

        using SKBitmap bitmap = DecodeUnpremultiplied(bytes, propertyName, asset);
        if (bitmap.Width != expectedWidth || bitmap.Height != expectedHeight)
        {
            errors.Add(
                $"{context}: {propertyName} dimensions {bitmap.Width}x{bitmap.Height} differ from "
                + $"manifest {expectedWidth}x{expectedHeight}.");
        }

        if (deep)
        {
            ValidatePixelEncoding(bitmap, propertyName, asset, context, errors);
        }
    }

    private static SKBitmap DecodeUnpremultiplied(byte[] bytes, string propertyName, string asset)
    {
        using SKData data = SKData.CreateCopy(bytes);
        using SKCodec codec = SKCodec.Create(data)
            ?? throw new InvalidDataException($"Could not decode {propertyName} map '{asset}'.");
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
            throw new InvalidDataException(
                $"Could not decode {propertyName} map '{asset}' ({result}).");
        }

        return bitmap;
    }

    private static void ValidatePixelEncoding(
        SKBitmap bitmap,
        string propertyName,
        string asset,
        string context,
        ICollection<string> errors)
    {
        int invalidPixels = 0;
        foreach (SKColor color in bitmap.Pixels)
        {
            if (color.Alpha == 0)
            {
                continue;
            }

            if (propertyName == "normal")
            {
                float x = ((color.Red / 255f) * 2f) - 1f;
                float y = ((color.Green / 255f) * 2f) - 1f;
                float z = ((color.Blue / 255f) * 2f) - 1f;
                float length = MathF.Sqrt((x * x) + (y * y) + (z * z));
                if (MathF.Abs(length - 1f) > 0.035f || z < -0.01f)
                {
                    invalidPixels++;
                }
            }
            else if (color.Red != color.Green || color.Red != color.Blue)
            {
                invalidPixels++;
            }
        }

        if (invalidPixels > 0)
        {
            errors.Add($"{context}: {propertyName} has {invalidPixels} invalid encoded pixels: {asset}");
        }
    }

    private static string? ReadPackModId(ZipArchive archive, ICollection<string> errors)
    {
        ZipArchiveEntry? entry = archive.GetEntry("modinfo.json");
        if (entry is null)
        {
            errors.Add("Root modinfo.json is missing.");
            return null;
        }

        using Stream stream = entry.Open();
        using JsonDocument document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        string? modId = GetString(document.RootElement, "modId") ?? GetString(document.RootElement, "modid");
        if (string.IsNullOrWhiteSpace(modId))
        {
            errors.Add("modinfo.json has no modId.");
        }

        return modId;
    }

    private static bool IsManifestEntry(string path)
    {
        string[] parts = path.Split('/');
        return parts.Length == 5
            && parts[0] == "assets"
            && parts[2] == "config"
            && parts[3] == "vintagertx"
            && parts[4] == "pbr-manifest.json";
    }

    private static string BuildAdjacentMapPath(string sourcePath, string suffix)
    {
        if (!sourcePath.StartsWith("textures/", StringComparison.Ordinal)
            || !sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Source asset '{sourcePath}' is not a PNG texture.");
        }

        return sourcePath[..^4] + suffix;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static bool IsUnderRoot(string candidate, string root)
    {
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

internal sealed record PackValidationRequest(
    string PackPath,
    string? SourceAssetsRoot,
    bool Deep);

internal sealed record PackValidationReport(
    string PackPath,
    string PackSha256,
    int ManifestCount,
    int TextureCount,
    int MapCount,
    int SourceVerifiedCount,
    int DuplicateLogicalKeyCount,
    IReadOnlyList<string> Errors,
    long ElapsedMilliseconds)
{
    public bool IsValid => Errors.Count == 0;
}
