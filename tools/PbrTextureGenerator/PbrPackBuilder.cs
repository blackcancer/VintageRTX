using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text;
using System.Diagnostics;

namespace VintageRTX.PbrTextureGenerator;

internal sealed class PbrPackBuilder
{
    public const string SchemaId = "vintagertx.pbr-manifest";
    public const int SchemaVersion = 3;
    public const string ManifestAssetPath = "config/vintagertx/pbr-manifest.json";

    private static readonly DateTimeOffset DeterministicZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PackBuildReport Build(PackBuildRequest request)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ResolvedPackSource resolvedSource = ResolveSource(request);
        string assetsRoot = resolvedSource.AssetsRoot;
        SourceModInfo sourceMod = resolvedSource.SourceMod;
        ValidateModId(request.PackId, "pack id");
        ValidateVersion(request.PackVersion, "pack version");

        IReadOnlyList<string> sourceTextures = DiscoverModTextures(
            assetsRoot,
            request.Scope,
            resolvedSource.DomainMappings.Select(mapping => mapping.Origin).ToArray());
        IReadOnlyDictionary<string, string> domainMappings = resolvedSource.DomainMappings
            .ToDictionary(mapping => mapping.Origin, mapping => mapping.AssetDomain, StringComparer.Ordinal);
        LogicalTextureResolution logicalResolution = ResolveLogicalTextures(
            assetsRoot,
            sourceTextures,
            domainMappings);
        List<string> texturesToGenerate = [];
        int authoredSidecarCount = 0;

        foreach (LogicalTextureSource source in logicalResolution.Textures)
        {
            string stem = Path.Combine(
                Path.GetDirectoryName(source.FullPath)!,
                Path.GetFileNameWithoutExtension(source.FullPath));
            if (File.Exists(stem + "_n.png") && File.Exists(stem + "_r.png"))
            {
                authoredSidecarCount++;
                continue;
            }

            texturesToGenerate.Add(source.FullPath);
        }

        if (texturesToGenerate.Count == 0)
        {
            throw new InvalidOperationException(
                "No textures require a generated PBR pair. All discovered albedos already have _n/_r sidecars.");
        }

        string outputZip = ResolveOutputZip(request.Output, request.PackId, request.PackVersion);
        string outputDirectory = Path.GetDirectoryName(outputZip)!;
        Directory.CreateDirectory(outputDirectory);
        string workRoot = Path.Combine(outputDirectory, ".vintagertx-pbr-pack-" + Guid.NewGuid().ToString("N"));

        try
        {
            string generatedRoot = Path.Combine(workRoot, "generated");
            GenerationReport generation = new PbrGenerator().Generate(new GenerationRequest(
                texturesToGenerate,
                generatedRoot,
                assetsRoot,
                request.Profile,
                request.FlipGreen,
                SkipExisting: false));

            List<PbrPackTexture> manifestTextures = [];
            HashSet<string> packagedMapEntries = new(StringComparer.OrdinalIgnoreCase);
            foreach (GeneratedTexture texture in generation.Textures.OrderBy(item => item.Source, StringComparer.Ordinal))
            {
                (string sourceOrigin, string sourceAssetPath) = SplitSourceLocation(texture.Source);
                string logicalDomain = domainMappings[sourceOrigin];
                sourceAssetPath = NormalizeLogicalAssetPath(sourceAssetPath);
                string normalAssetPath = BuildAdjacentMapPath(sourceAssetPath, "_n.png");
                string roughnessAssetPath = BuildAdjacentMapPath(sourceAssetPath, "_r.png");
                string metallicAssetPath = BuildAdjacentMapPath(sourceAssetPath, "_m.png");
                string emissiveAssetPath = BuildAdjacentMapPath(sourceAssetPath, "_e.png");
                string[] mapAssetPaths =
                [
                    normalAssetPath,
                    roughnessAssetPath,
                    metallicAssetPath,
                    emissiveAssetPath
                ];
                foreach (string mapAssetPath in mapAssetPaths)
                {
                    string entry = $"assets/{logicalDomain}/{mapAssetPath}";
                    if (!packagedMapEntries.Add(entry))
                    {
                        throw new InvalidDataException(
                            $"Logical sidecar collision at '{entry}'. Check origin-to-domain mappings.");
                    }
                }

                string normalDestination = Path.Combine(workRoot, "package", "assets", logicalDomain, normalAssetPath.Replace('/', Path.DirectorySeparatorChar));
                string roughnessDestination = Path.Combine(workRoot, "package", "assets", logicalDomain, roughnessAssetPath.Replace('/', Path.DirectorySeparatorChar));
                string metallicDestination = Path.Combine(workRoot, "package", "assets", logicalDomain, metallicAssetPath.Replace('/', Path.DirectorySeparatorChar));
                string emissiveDestination = Path.Combine(workRoot, "package", "assets", logicalDomain, emissiveAssetPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(normalDestination)!);
                Directory.CreateDirectory(Path.GetDirectoryName(roughnessDestination)!);
                Directory.CreateDirectory(Path.GetDirectoryName(metallicDestination)!);
                Directory.CreateDirectory(Path.GetDirectoryName(emissiveDestination)!);
                File.Copy(Path.Combine(generation.OutputRoot, texture.Normal.Replace('/', Path.DirectorySeparatorChar)), normalDestination);
                File.Copy(Path.Combine(generation.OutputRoot, texture.Roughness.Replace('/', Path.DirectorySeparatorChar)), roughnessDestination);
                File.Copy(Path.Combine(generation.OutputRoot, texture.Metallic.Replace('/', Path.DirectorySeparatorChar)), metallicDestination);
                File.Copy(Path.Combine(generation.OutputRoot, texture.Emissive.Replace('/', Path.DirectorySeparatorChar)), emissiveDestination);

                manifestTextures.Add(new PbrPackTexture(
                    new PbrSourceReference(
                        sourceMod.ModId,
                        sourceMod.Version,
                        sourceOrigin,
                        logicalDomain,
                        sourceAssetPath,
                        texture.SourceSha256),
                    new PbrMapReference(
                        $"{logicalDomain}:{normalAssetPath}",
                        texture.NormalSha256,
                        request.FlipGreen ? "directx-y-negative" : "opengl-y-positive"),
                    new PbrMapReference(
                        $"{logicalDomain}:{roughnessAssetPath}",
                        texture.RoughnessSha256,
                        "linear-unorm"),
                    new PbrMapReference(
                        $"{logicalDomain}:{metallicAssetPath}",
                        texture.MetallicSha256,
                        "linear-unorm"),
                    new PbrMapReference(
                        $"{logicalDomain}:{emissiveAssetPath}",
                        texture.EmissiveSha256,
                        "linear-unorm"),
                    texture.Profile,
                    texture.Width,
                    texture.Height));
            }

            string packageRoot = Path.Combine(workRoot, "package");
            WritePackModInfo(packageRoot, request, sourceMod);
            WritePackManifest(packageRoot, request, sourceMod, manifestTextures);
            CreateDeterministicZip(packageRoot, outputZip);
            stopwatch.Stop();

            return new PackBuildReport(
                outputZip,
                Sha256File(outputZip),
                sourceMod.ModId,
                sourceMod.Version,
                sourceTextures.Count,
                logicalResolution.Textures.Count,
                manifestTextures.Count,
                authoredSidecarCount,
                logicalResolution.Overrides,
                manifestTextures.Select(item => item.Source.Origin).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                manifestTextures.Select(item => item.Source.Domain).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            // workRoot is created by this invocation beneath the explicitly selected output directory.
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    public PackRepackageReport RepackageLegacyManifest(string inputZip, string? outputZip)
    {
        string inputPath = Path.GetFullPath(inputZip);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input PBR pack was not found: '{inputPath}'.", inputPath);
        }

        string outputPath = Path.GetFullPath(outputZip ?? inputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".tmp";
        int migratedManifestCount = 0;
        int entryCount;

        using (ZipArchive sourceArchive = ZipFile.OpenRead(inputPath))
        {
            var plannedEntries = sourceArchive.Entries
                .Select(entry => new
                {
                    Source = entry,
                    TargetName = MigrateLegacyManifestEntryName(entry.FullName)
                })
                .OrderBy(item => item.TargetName, StringComparer.Ordinal)
                .ToArray();

            string[] duplicateTargets = plannedEntries
                .GroupBy(item => item.TargetName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateTargets.Length > 0)
            {
                throw new InvalidDataException(
                    "Repack would create duplicate ZIP entries: " + string.Join(", ", duplicateTargets));
            }

            migratedManifestCount = plannedEntries.Count(item =>
                !item.Source.FullName.Equals(item.TargetName, StringComparison.Ordinal));
            if (migratedManifestCount == 0)
            {
                throw new InvalidDataException(
                    "No legacy assets/<domain>/vintagertx/pbr-manifest.json entry was found in the pack.");
            }

            using FileStream outputStream = File.Create(temporaryPath);
            using ZipArchive destinationArchive = new(outputStream, ZipArchiveMode.Create, leaveOpen: false);
            foreach (var item in plannedEntries)
            {
                ZipArchiveEntry destination = destinationArchive.CreateEntry(item.TargetName, CompressionLevel.Optimal);
                destination.LastWriteTime = DeterministicZipTimestamp;
                using Stream source = item.Source.Open();
                using Stream target = destination.Open();
                source.CopyTo(target);
            }

            entryCount = plannedEntries.Length;
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
        return new PackRepackageReport(outputPath, Sha256File(outputPath), entryCount, migratedManifestCount);
    }

    public PackDomainRemapReport RemapManifestDomain(
        string inputZip,
        string? outputZip,
        string sourceOrigin,
        string logicalAssetDomain)
    {
        ValidateModId(sourceOrigin, "source origin");
        ValidateModId(logicalAssetDomain, "logical asset domain");
        string inputPath = Path.GetFullPath(inputZip);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input PBR pack was not found: '{inputPath}'.", inputPath);
        }

        string outputPath = Path.GetFullPath(outputZip ?? inputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".tmp";
        int manifestCount = 0;
        int remappedTextureCount = 0;
        int entryCount;

        using (ZipArchive sourceArchive = ZipFile.OpenRead(inputPath))
        {
            ZipArchiveEntry[] orderedEntries = sourceArchive.Entries
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
            Dictionary<string, byte[]> transformedManifests = new(StringComparer.Ordinal);
            Dictionary<string, string> remappedEntries = new(StringComparer.Ordinal);
            foreach (ZipArchiveEntry manifestEntry in orderedEntries.Where(entry =>
                IsIndexedManifestEntry(entry.FullName)))
            {
                manifestCount++;
                using Stream source = manifestEntry.Open();
                using MemoryStream manifestBytes = new();
                source.CopyTo(manifestBytes);
                byte[] transformed = RemapManifestBytes(
                    manifestBytes.ToArray(),
                    sourceOrigin,
                    logicalAssetDomain,
                    remappedEntries,
                    out int entryRemapCount);
                remappedTextureCount += entryRemapCount;
                transformedManifests.Add(manifestEntry.FullName, transformed);
            }

            string[] duplicateTargets = orderedEntries
                .Select(entry => remappedEntries.GetValueOrDefault(entry.FullName, entry.FullName))
                .GroupBy(path => path, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateTargets.Length > 0)
            {
                throw new InvalidDataException(
                    "Domain remap would create duplicate ZIP entries: "
                    + string.Join(", ", duplicateTargets));
            }

            using FileStream outputStream = File.Create(temporaryPath);
            using ZipArchive destinationArchive = new(outputStream, ZipArchiveMode.Create, leaveOpen: false);
            foreach (ZipArchiveEntry sourceEntry in orderedEntries)
            {
                string targetName = remappedEntries.GetValueOrDefault(
                    sourceEntry.FullName,
                    sourceEntry.FullName);
                ZipArchiveEntry destination = destinationArchive.CreateEntry(targetName, CompressionLevel.Optimal);
                destination.LastWriteTime = DeterministicZipTimestamp;
                using Stream target = destination.Open();
                if (transformedManifests.TryGetValue(sourceEntry.FullName, out byte[]? transformed))
                {
                    target.Write(transformed);
                }
                else
                {
                    using Stream source = sourceEntry.Open();
                    source.CopyTo(target);
                }
            }

            entryCount = orderedEntries.Length;
        }

        if (manifestCount == 0 || remappedTextureCount == 0)
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException(
                $"No indexed manifest entries for source origin '{sourceOrigin}' were remapped.");
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
        return new PackDomainRemapReport(
            outputPath,
            Sha256File(outputPath),
            entryCount,
            manifestCount,
            remappedTextureCount,
            sourceOrigin,
            logicalAssetDomain);
    }

    private static ResolvedPackSource ResolveSource(PackBuildRequest request)
    {
        bool hasModRoot = !string.IsNullOrWhiteSpace(request.ModRoot);
        bool hasAssetsRoot = !string.IsNullOrWhiteSpace(request.SourceAssetsRoot);
        if (hasModRoot == hasAssetsRoot)
        {
            throw new ArgumentException("Choose exactly one source mode: --mod-root or --source-assets.");
        }

        if (hasModRoot)
        {
            string modRoot = Path.GetFullPath(request.ModRoot!);
            string assetsRoot = Path.Combine(modRoot, "assets");
            string sourceModInfoPath = Path.Combine(modRoot, "modinfo.json");
            if (!File.Exists(sourceModInfoPath) || !Directory.Exists(assetsRoot))
            {
                throw new InvalidOperationException(
                    $"'{modRoot}' is not an unpacked Vintage Story mod: modinfo.json and assets/ are required.");
            }

            SourceModInfo sourceMod = ReadSourceModInfo(sourceModInfoPath);
            IReadOnlyList<SourceDomainMapping> mappings = request.DomainMappings.Count == 0
                ? DiscoverIdentityDomainMappings(assetsRoot)
                : ValidateDomainMappings(request.DomainMappings);
            ValidateOriginsExist(assetsRoot, mappings.Select(mapping => mapping.Origin));
            return new ResolvedPackSource(assetsRoot, sourceMod, mappings);
        }

        string sourceAssetsRoot = Path.GetFullPath(request.SourceAssetsRoot!);
        if (!Directory.Exists(sourceAssetsRoot))
        {
            throw new DirectoryNotFoundException($"Source assets directory was not found: '{sourceAssetsRoot}'.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceModId)
            || string.IsNullOrWhiteSpace(request.SourceModVersion)
            || request.DomainMappings.Count == 0)
        {
            throw new ArgumentException(
                "--source-assets requires --source-mod-id, --source-mod-version and at least one --domain-map origin=assetdomain.");
        }

        ValidateModId(request.SourceModId, "source mod id");
        ValidateVersion(request.SourceModVersion, "source mod version");
        IReadOnlyList<SourceDomainMapping> selectedMappings = ValidateDomainMappings(request.DomainMappings);
        ValidateOriginsExist(sourceAssetsRoot, selectedMappings.Select(mapping => mapping.Origin));

        return new ResolvedPackSource(
            sourceAssetsRoot,
            new SourceModInfo(request.SourceModId, request.SourceModVersion),
            selectedMappings);
    }

    private static SourceModInfo ReadSourceModInfo(string path)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        string modId = GetStringProperty(root, "modid", "modId")
            ?? throw new InvalidDataException($"Source modinfo '{path}' has no modid.");
        string version = GetStringProperty(root, "version") ?? "unspecified";
        ValidateModId(modId, "source mod id");
        return new SourceModInfo(modId, version);
    }

    private static IReadOnlyList<string> DiscoverModTextures(
        string assetsRoot,
        string scope,
        IReadOnlyList<string> selectedDomains)
    {
        if (scope is not ("block" or "all"))
        {
            throw new ArgumentException("Pack scope must be 'block' or 'all'.");
        }

        List<string> textures = [];
        foreach (string selectedDomain in selectedDomains)
        {
            string domainRoot = Path.Combine(assetsRoot, selectedDomain);

            string textureRoot = scope == "block"
                ? Path.Combine(domainRoot, "textures", "block")
                : Path.Combine(domainRoot, "textures");
            if (!Directory.Exists(textureRoot))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(textureRoot, "*.png", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.EndsWith("_n", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_r", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_m", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_e", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                textures.Add(file);
            }
        }

        return textures;
    }

    private static IReadOnlyList<SourceDomainMapping> DiscoverIdentityDomainMappings(string assetsRoot)
    {
        return Directory.EnumerateDirectories(assetsRoot)
            .Select(Path.GetFileName)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .Select(domain => new SourceDomainMapping(domain!, domain!))
            .ToArray();
    }

    private static IReadOnlyList<SourceDomainMapping> ValidateDomainMappings(
        IReadOnlyList<SourceDomainMapping> mappings)
    {
        HashSet<string> origins = new(StringComparer.Ordinal);
        List<SourceDomainMapping> validated = new(mappings.Count);
        foreach (SourceDomainMapping mapping in mappings)
        {
            ValidateModId(mapping.Origin, "source origin");
            ValidateModId(mapping.AssetDomain, "logical asset domain");
            if (!origins.Add(mapping.Origin))
            {
                throw new ArgumentException($"Source origin '{mapping.Origin}' is mapped more than once.");
            }

            validated.Add(mapping);
        }

        return validated;
    }

    private static LogicalTextureResolution ResolveLogicalTextures(
        string assetsRoot,
        IReadOnlyList<string> sourceTextures,
        IReadOnlyDictionary<string, string> domainMappings)
    {
        Dictionary<string, LogicalTextureSource> selected = new(StringComparer.OrdinalIgnoreCase);
        List<PbrLogicalOverride> overrides = [];
        foreach (string fullPath in sourceTextures)
        {
            string relative = Path.GetRelativePath(assetsRoot, fullPath).Replace('\\', '/');
            (string origin, string assetPath) = SplitSourceLocation(relative);
            string normalizedAssetPath = NormalizeLogicalAssetPath(assetPath);
            string logicalDomain = domainMappings[origin];
            string logicalKey = $"{logicalDomain}:{normalizedAssetPath}";
            LogicalTextureSource candidate = new(fullPath, origin, logicalDomain, normalizedAssetPath);
            if (selected.TryGetValue(logicalKey, out LogicalTextureSource? previous))
            {
                string previousSha256 = Sha256File(previous.FullPath);
                string winnerSha256 = Sha256File(candidate.FullPath);
                overrides.Add(new PbrLogicalOverride(
                    logicalKey.ToLowerInvariant(),
                    previous.Origin,
                    candidate.Origin,
                    previousSha256,
                    winnerSha256,
                    previousSha256.Equals(winnerSha256, StringComparison.Ordinal)));
            }

            // Domain mappings are supplied from lowest to highest priority.
            // Replacing here makes the last mapping win without relying on
            // filesystem enumeration order.
            selected[logicalKey] = candidate;
        }

        return new LogicalTextureResolution(
            selected
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Value)
                .ToArray(),
            overrides
                .OrderBy(item => item.LogicalAssetKey, StringComparer.Ordinal)
                .ThenBy(item => item.OverriddenOrigin, StringComparer.Ordinal)
                .ThenBy(item => item.WinningOrigin, StringComparer.Ordinal)
                .ToArray());
    }

    private static void ValidateOriginsExist(string assetsRoot, IEnumerable<string> origins)
    {
        foreach (string origin in origins)
        {
            if (!Directory.Exists(Path.Combine(assetsRoot, origin)))
            {
                throw new DirectoryNotFoundException(
                    $"Source origin '{origin}' was not found below '{assetsRoot}'.");
            }
        }
    }

    private static (string Domain, string AssetPath) SplitSourceLocation(string source)
    {
        string normalized = source.Replace('\\', '/');
        int separator = normalized.IndexOf('/');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new InvalidDataException($"Source path '{source}' has no asset domain.");
        }

        string domain = normalized[..separator];
        ValidateModId(domain, "asset domain");
        return (domain, normalized[(separator + 1)..]);
    }

    private static string BuildAdjacentMapPath(string sourceAssetPath, string suffix)
    {
        const string texturePrefix = "textures/";
        if (!sourceAssetPath.StartsWith(texturePrefix, StringComparison.Ordinal)
            || !sourceAssetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Source asset '{sourceAssetPath}' is not a PNG texture.");
        }

        return sourceAssetPath[..^4] + suffix;
    }

    private static string NormalizeLogicalAssetPath(string path) =>
        path.Replace('\\', '/').ToLowerInvariant();

    private static void WritePackModInfo(string packageRoot, PackBuildRequest request, SourceModInfo sourceMod)
    {
        Directory.CreateDirectory(packageRoot);
        SortedDictionary<string, string> dependencies = new(StringComparer.Ordinal)
        {
            [sourceMod.ModId] = sourceMod.Version == "unspecified" ? string.Empty : sourceMod.Version
        };
        PackModInfo modInfo = new(
            "content",
            request.PackId,
            request.PackName ?? $"PBR for {sourceMod.ModId}",
            ["VintageRTX offline PBR generator"],
            $"Offline normal, roughness, metallic and emissive maps for {sourceMod.ModId}. Contains no runtime generator.",
            request.PackVersion,
            dependencies);
        WriteJson(Path.Combine(packageRoot, "modinfo.json"), modInfo);
    }

    private static void WritePackManifest(
        string packageRoot,
        PackBuildRequest request,
        SourceModInfo sourceMod,
        IReadOnlyList<PbrPackTexture> textures)
    {
        string manifestPath = Path.Combine(
            packageRoot,
            "assets",
            request.PackId,
            ManifestAssetPath.Replace('/', Path.DirectorySeparatorChar));
        PbrPackManifest manifest = new(
            SchemaId,
            SchemaVersion,
            new PbrPackIdentity(request.PackId, request.PackVersion),
            [new PbrSourceMod(
                sourceMod.ModId,
                sourceMod.Version,
                textures.Select(item => item.Source.Origin).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                textures.Select(item => item.Source.Domain).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray())],
            new PbrGeneratorIdentity("vintagertx-pbr-generator", PbrGenerator.AlgorithmVersion, "offline-only"),
            textures);
        WriteJson(manifestPath, manifest);
    }

    private static void CreateDeterministicZip(string packageRoot, string outputZip)
    {
        string temporaryZip = outputZip + ".tmp";
        using (FileStream stream = File.Create(temporaryZip))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (string file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(packageRoot, path).Replace('\\', '/'), StringComparer.Ordinal))
            {
                string entryName = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
                ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicZipTimestamp;
                using Stream destination = entry.Open();
                using FileStream source = File.OpenRead(file);
                source.CopyTo(destination);
            }
        }

        File.Move(temporaryZip, outputZip, overwrite: true);
    }

    private static string MigrateLegacyManifestEntryName(string entryName)
    {
        string[] parts = entryName.Split('/');
        if (parts.Length == 4
            && parts[0] == "assets"
            && parts[2] == "vintagertx"
            && parts[3] == "pbr-manifest.json")
        {
            return $"assets/{parts[1]}/{ManifestAssetPath}";
        }

        return entryName;
    }

    private static bool IsIndexedManifestEntry(string entryName)
    {
        string[] parts = entryName.Split('/');
        return parts.Length == 5
            && parts[0] == "assets"
            && parts[2] == "config"
            && parts[3] == "vintagertx"
            && parts[4] == "pbr-manifest.json";
    }

    private static byte[] RemapManifestBytes(
        byte[] manifestBytes,
        string sourceOrigin,
        string logicalAssetDomain,
        IDictionary<string, string> remappedEntries,
        out int remappedTextureCount)
    {
        JsonObject root = JsonNode.Parse(manifestBytes)?.AsObject()
            ?? throw new InvalidDataException("PBR manifest JSON is empty.");
        JsonArray textures = root["textures"]?.AsArray()
            ?? throw new InvalidDataException("PBR manifest has no textures array.");
        remappedTextureCount = 0;

        foreach (JsonNode? textureNode in textures)
        {
            JsonObject source = textureNode?["source"]?.AsObject()
                ?? throw new InvalidDataException("PBR manifest texture has no source object.");
            string currentDomain = source["domain"]?.GetValue<string>()
                ?? throw new InvalidDataException("PBR manifest source has no domain.");
            string currentOrigin = source["origin"]?.GetValue<string>() ?? currentDomain;
            if (!currentOrigin.Equals(sourceOrigin, StringComparison.Ordinal))
            {
                continue;
            }

            source["origin"] = sourceOrigin;
            source["domain"] = logicalAssetDomain;
            int schemaVersion = root["schemaVersion"]?.GetValue<int>() ?? 1;
            if (schemaVersion >= 3)
            {
                string sourcePath = source["path"]?.GetValue<string>()
                    ?? throw new InvalidDataException("PBR manifest source has no path.");
                foreach ((string property, string suffix) in new[]
                {
                    ("normal", "_n.png"),
                    ("roughness", "_r.png"),
                    ("metallic", "_m.png"),
                    ("emissive", "_e.png")
                })
                {
                    JsonObject map = textureNode?[property]?.AsObject()
                        ?? throw new InvalidDataException($"PBR manifest texture has no {property} map.");
                    string oldAsset = map["asset"]?.GetValue<string>()
                        ?? throw new InvalidDataException($"PBR manifest {property} map has no asset.");
                    int separator = oldAsset.IndexOf(':');
                    if (separator <= 0 || separator == oldAsset.Length - 1)
                    {
                        throw new InvalidDataException($"Invalid PBR AssetLocation '{oldAsset}'.");
                    }

                    string newPath = BuildAdjacentMapPath(sourcePath, suffix);
                    string newAsset = $"{logicalAssetDomain}:{newPath}";
                    remappedEntries.Add(
                        $"assets/{oldAsset[..separator]}/{oldAsset[(separator + 1)..]}",
                        $"assets/{logicalAssetDomain}/{newPath}");
                    map["asset"] = newAsset;
                }
            }

            remappedTextureCount++;
        }

        Dictionary<(string ModId, string Version), (SortedSet<string> Origins, SortedSet<string> Domains)> sourceIdentities = [];
        foreach (JsonNode? textureNode in textures)
        {
            JsonObject source = textureNode!["source"]!.AsObject();
            string modId = source["modId"]!.GetValue<string>();
            string version = source["modVersion"]!.GetValue<string>();
            var key = (modId, version);
            if (!sourceIdentities.TryGetValue(key, out var identity))
            {
                identity = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                sourceIdentities[key] = identity;
            }

            identity.Origins.Add(source["origin"]?.GetValue<string>() ?? source["domain"]!.GetValue<string>());
            identity.Domains.Add(source["domain"]!.GetValue<string>());
        }

        if (root["sourceMods"] is JsonArray sourceMods)
        {
            foreach (JsonNode? sourceModNode in sourceMods)
            {
                JsonObject sourceMod = sourceModNode!.AsObject();
                var key = (sourceMod["modId"]!.GetValue<string>(), sourceMod["version"]!.GetValue<string>());
                if (!sourceIdentities.TryGetValue(key, out var identity))
                {
                    continue;
                }

                sourceMod["origins"] = new JsonArray(
                    identity.Origins.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                sourceMod["domains"] = new JsonArray(
                    identity.Domains.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            }
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions));
    }

    private static string ResolveOutputZip(string output, string packId, string packVersion)
    {
        string fullOutput = Path.GetFullPath(output);
        if (Path.GetExtension(fullOutput).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return fullOutput;
        }

        return Path.Combine(fullOutput, $"{packId}-{packVersion}.zip");
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        File.WriteAllBytes(path, bytes);
    }

    private static string? GetStringProperty(JsonElement root, params string[] names)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static void ValidateModId(string value, string label)
    {
        if (value.Length is < 2 or > 64
            || value.Any(character => !((character >= 'a' && character <= 'z') || char.IsAsciiDigit(character))))
        {
            throw new ArgumentException($"Invalid {label} '{value}'. Use 2-64 lowercase ASCII letters or digits.");
        }
    }

    private static void ValidateVersion(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"Invalid {label} '{value}'.");
        }
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record SourceModInfo(string ModId, string Version);

    private sealed record ResolvedPackSource(
        string AssetsRoot,
        SourceModInfo SourceMod,
        IReadOnlyList<SourceDomainMapping> DomainMappings);

    private sealed record LogicalTextureSource(
        string FullPath,
        string Origin,
        string Domain,
        string AssetPath);

    private sealed record LogicalTextureResolution(
        IReadOnlyList<LogicalTextureSource> Textures,
        IReadOnlyList<PbrLogicalOverride> Overrides);
}

internal sealed record SourceDomainMapping(string Origin, string AssetDomain);

internal sealed record PackBuildRequest(
    string? ModRoot,
    string? SourceAssetsRoot,
    string? SourceModId,
    string? SourceModVersion,
    IReadOnlyList<SourceDomainMapping> DomainMappings,
    string Output,
    string PackId,
    string PackVersion,
    string? PackName,
    string Scope,
    string Profile,
    bool FlipGreen);

internal sealed record PackBuildReport(
    string ZipPath,
    string ZipSha256,
    string SourceModId,
    string SourceModVersion,
    int CandidateTextureCount,
    int LogicalTextureCount,
    int GeneratedTextureCount,
    int AuthoredSidecarCount,
    IReadOnlyList<PbrLogicalOverride> Overrides,
    IReadOnlyList<string> SourceOrigins,
    IReadOnlyList<string> SourceDomains,
    long ElapsedMilliseconds)
{
    public int LogicalOverrideCount => Overrides.Count;

    public int IdenticalLogicalOverrideCount => Overrides.Count(item => item.ContentIdentical);

    public int DivergentLogicalOverrideCount => Overrides.Count(item => !item.ContentIdentical);
}

internal sealed record PbrLogicalOverride(
    string LogicalAssetKey,
    string OverriddenOrigin,
    string WinningOrigin,
    string OverriddenSha256,
    string WinningSha256,
    bool ContentIdentical);

internal sealed record PackRepackageReport(
    string ZipPath,
    string ZipSha256,
    int EntryCount,
    int MigratedManifestCount);

internal sealed record PackDomainRemapReport(
    string ZipPath,
    string ZipSha256,
    int EntryCount,
    int ManifestCount,
    int RemappedTextureCount,
    string SourceOrigin,
    string LogicalAssetDomain);

internal sealed record PackModInfo(
    string Type,
    string ModId,
    string Name,
    IReadOnlyList<string> Authors,
    string Description,
    string Version,
    IReadOnlyDictionary<string, string> Dependencies);

internal sealed record PbrPackManifest(
    string Schema,
    int SchemaVersion,
    PbrPackIdentity Pack,
    IReadOnlyList<PbrSourceMod> SourceMods,
    PbrGeneratorIdentity Generator,
    IReadOnlyList<PbrPackTexture> Textures);

internal sealed record PbrPackIdentity(string ModId, string Version);

internal sealed record PbrSourceMod(
    string ModId,
    string Version,
    IReadOnlyList<string> Origins,
    IReadOnlyList<string> Domains);

internal sealed record PbrGeneratorIdentity(string Id, string Version, string Execution);

internal sealed record PbrPackTexture(
    PbrSourceReference Source,
    PbrMapReference Normal,
    PbrMapReference Roughness,
    PbrMapReference Metallic,
    PbrMapReference Emissive,
    string Profile,
    int Width,
    int Height);

internal sealed record PbrSourceReference(
    string ModId,
    string ModVersion,
    string Origin,
    string Domain,
    string Path,
    string Sha256);

internal sealed record PbrMapReference(string Asset, string Sha256, string Encoding);
