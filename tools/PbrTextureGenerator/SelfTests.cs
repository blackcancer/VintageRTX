using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using SkiaSharp;

namespace VintageRTX.PbrTextureGenerator;

internal static class SelfTests
{
    public static void Run(string? requestedOutputRoot)
    {
        string parentRoot = requestedOutputRoot is null
            ? Path.GetTempPath()
            : Path.GetFullPath(requestedOutputRoot);
        string root = Path.Combine(parentRoot, "vintagertx-pbr-self-test-" + Guid.NewGuid().ToString("N"));

        string sourceRoot = Path.Combine(root, "assets");
        string sourcePath = Path.Combine(sourceRoot, "survival", "textures", "block", "stone", "brick", "periodic.png");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        WritePeriodicFixture(sourcePath);
        string inputHash = Sha256(sourcePath);
        DateTime inputTimestamp = File.GetLastWriteTimeUtc(sourcePath);
        File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) | FileAttributes.ReadOnly);

        try
        {
            PbrGenerator generator = new();
            GenerationRequest firstRequest = new(
                [sourcePath],
                Path.Combine(root, "run-a"),
                sourceRoot,
                "auto",
                FlipGreen: false,
                SkipExisting: false);
            GenerationRequest secondRequest = firstRequest with { OutputRoot = Path.Combine(root, "run-b") };
            GenerationReport first = generator.Generate(firstRequest);
            GenerationReport second = generator.Generate(secondRequest);

            Assert(first.Textures.Count == 1, "one fixture should generate one texture pair");
            Assert(first.Textures[0].Profile == "brick", "automatic path classification should choose brick");
            Assert(inputHash == Sha256(sourcePath), "source bytes must remain unchanged");
            Assert(inputTimestamp == File.GetLastWriteTimeUtc(sourcePath), "source timestamp must remain unchanged");
            AssertEquivalentOutputs(first, second);
            ValidateNormalMap(Path.Combine(first.OutputRoot, first.Textures[0].Normal));
            ValidateNormalYAxisConvention(generator, root);
            ValidateRoughnessOrdering(generator, sourcePath, sourceRoot, root);
            ValidateMaterialMaps(generator, sourcePath, sourceRoot, root);
            ValidateProfileRouting();
            ValidateThirdPartyPack(root);
            ValidateSourceAssetPrecedencePack(root);

            Console.WriteLine($"Self-test PASS ({root})");
            Console.WriteLine("  source immutability: PASS");
            Console.WriteLine("  byte-for-byte determinism: PASS");
            Console.WriteLine("  tangent normal amplitude, OpenGL +Y convention and tiling seam: PASS");
            Console.WriteLine("  profile routing, roughness and material maps: PASS");
            Console.WriteLine("  third-party native-sidecar pack and manifest v4: PASS");
            Console.WriteLine("  source-asset precedence and logical collision journal: PASS");
            Console.WriteLine("  standalone pack integrity and deep pixel validation: PASS");
        }
        finally
        {
            File.SetAttributes(sourcePath, FileAttributes.Normal);
        }
    }

    private static void ValidateThirdPartyPack(string root)
    {
        string modRoot = Path.Combine(root, "third-party-mod");
        string textureRoot = Path.Combine(modRoot, "assets", "sampledomain", "textures", "block");
        string generatedAlbedo = Path.Combine(textureRoot, "stone", "generated.png");
        string authoredAlbedo = Path.Combine(textureRoot, "wood", "authored.png");
        string orientationAlbedo = Path.Combine(textureRoot, "plant", "side_normal.png");
        Directory.CreateDirectory(Path.GetDirectoryName(generatedAlbedo)!);
        Directory.CreateDirectory(Path.GetDirectoryName(authoredAlbedo)!);
        Directory.CreateDirectory(Path.GetDirectoryName(orientationAlbedo)!);
        File.WriteAllText(
            Path.Combine(modRoot, "modinfo.json"),
            "{\"type\":\"content\",\"modid\":\"samplemod\",\"name\":\"Sample\",\"version\":\"2.3.4\"}");
        WritePeriodicFixture(generatedAlbedo);
        WritePeriodicFixture(authoredAlbedo);
        WritePeriodicFixture(orientationAlbedo);
        WritePeriodicFixture(Path.Combine(Path.GetDirectoryName(authoredAlbedo)!, "authored_n.png"));
        WritePeriodicFixture(Path.Combine(Path.GetDirectoryName(authoredAlbedo)!, "authored_r.png"));
        WritePeriodicFixture(Path.Combine(Path.GetDirectoryName(authoredAlbedo)!, "authored_m.png"));
        WritePeriodicFixture(Path.Combine(Path.GetDirectoryName(authoredAlbedo)!, "authored_e.png"));

        PbrPackBuilder builder = new();
        PackBuildRequest baseRequest = new(
            modRoot,
            null,
            null,
            null,
            [],
            Path.Combine(root, "pack-a"),
            "samplepbr",
            "1.0.0",
            "Sample PBR",
            "block",
            "auto",
            FlipGreen: false);
        PackBuildReport first = builder.Build(baseRequest);
        PackBuildReport second = builder.Build(baseRequest with { Output = Path.Combine(root, "pack-b") });

        Assert(first.ZipSha256 == second.ZipSha256, "pack ZIP must be byte-for-byte deterministic");
        Assert(first.GeneratedTextureCount == 2, "albedos without authored sidecars should be generated");
        Assert(first.CandidateTextureCount == 3, "all third-party albedos should be counted as candidates");
        Assert(first.AuthoredSidecarCount == 1, "complete authored sidecar pair should be detected and skipped");
        Assert(first.SourceDomains.SequenceEqual(["sampledomain"]), "source asset domain should be retained");

        PackValidationReport validation = new PbrPackValidator().Validate(new PackValidationRequest(
            first.ZipPath,
            Path.Combine(modRoot, "assets"),
            Deep: true));
        Assert(validation.IsValid, "standalone validator should accept a generated pack: " + string.Join(" | ", validation.Errors));
        Assert(validation.ManifestCount == 1, "standalone validator should find the indexed manifest");
        Assert(validation.TextureCount == 2, "standalone validator should count generated textures");

        NormalAnalysisReport normalAnalysis = new NormalMapAnalyzer().Analyze(first.ZipPath);
        Assert(normalAnalysis.TextureCount == 2, "normal analyzer should cover every generated third-party texture");
        Assert(normalAnalysis.P50PixelAngleDegrees >= 2f, "normal analyzer should expose measurable generated relief");
        Assert(normalAnalysis.P95PixelAngleDegrees <= 17f, "albedo-derived normals must remain below the conservative embossing bound");
        Assert(normalAnalysis.MaximumVectorLengthError < 0.01, "normal analyzer should verify RGBA8 vector normalization");
        Assert(validation.MapCount == 8, "standalone validator should verify all v2 maps");
        Assert(validation.SourceVerifiedCount == 2, "standalone validator should verify source identities and SHA-256");
        Assert(validation.DuplicateLogicalKeyCount == 0, "standalone validator should reject duplicate atlas keys");

        byte[] untamperedSource = File.ReadAllBytes(generatedAlbedo);
        try
        {
            File.WriteAllBytes(generatedAlbedo, [.. untamperedSource, 0x00]);
            PackValidationReport tamperedValidation = new PbrPackValidator().Validate(new PackValidationRequest(
                first.ZipPath,
                Path.Combine(modRoot, "assets"),
                Deep: false));
            Assert(!tamperedValidation.IsValid, "standalone validator should reject a changed source");
            Assert(
                tamperedValidation.Errors.Any(error => error.Contains("source SHA-256 mismatch", StringComparison.Ordinal)),
                "standalone validator should report the changed source SHA-256");
        }
        finally
        {
            File.WriteAllBytes(generatedAlbedo, untamperedSource);
        }

        using ZipArchive archive = ZipFile.OpenRead(first.ZipPath);
        string[] entries = archive.Entries.Select(entry => entry.FullName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert(entries.Contains("modinfo.json"), "installable pack must contain root modinfo.json");
        Assert(entries.Contains("assets/samplepbr/config/vintagertx/pbr-manifest.json"), "pack must contain the indexed config manifest path");
        Assert(!entries.Contains("assets/samplepbr/vintagertx/pbr-manifest.json"), "pack must not use the unindexed legacy manifest category");
        Assert(entries.Contains("assets/sampledomain/textures/block/stone/generated_n.png"), "normal must be adjacent in the source asset domain");
        Assert(entries.Contains("assets/sampledomain/textures/block/stone/generated_r.png"), "roughness must be adjacent in the source asset domain");
        Assert(entries.Contains("assets/sampledomain/textures/block/stone/generated_m.png"), "metallicity must be adjacent in the source asset domain");
        Assert(entries.Contains("assets/sampledomain/textures/block/stone/generated_e.png"), "emission must be adjacent in the source asset domain");
        Assert(entries.Contains("assets/sampledomain/textures/block/plant/side_normal_n.png"), "an albedo ending in _normal must not be mistaken for a sidecar");
        Assert(!entries.Any(entry => entry.Contains("/textures/pbr/", StringComparison.Ordinal)), "central PBR texture tree must not be produced");
        Assert(!entries.Any(entry => entry.EndsWith("/generated.png", StringComparison.Ordinal)), "source albedo must never be redistributed");
        Assert(!entries.Any(entry => entry.Contains("authored", StringComparison.Ordinal)), "authored source-mod sidecars should not be duplicated");
        Assert(
            archive.Entries.All(entry =>
                entry.LastWriteTime.Year == 1980
                && entry.LastWriteTime.Month == 1
                && entry.LastWriteTime.Day == 1
                && entry.LastWriteTime.Hour == 0
                && entry.LastWriteTime.Minute == 0
                && entry.LastWriteTime.Second == 0),
            "ZIP timestamps must be deterministic");

        ZipArchiveEntry manifestEntry = archive.GetEntry("assets/samplepbr/config/vintagertx/pbr-manifest.json")!;
        using Stream manifestStream = manifestEntry.Open();
        using JsonDocument manifest = JsonDocument.Parse(manifestStream);
        JsonElement manifestRoot = manifest.RootElement;
        Assert(manifestRoot.GetProperty("schema").GetString() == "vintagertx.pbr-manifest", "manifest schema id");
        Assert(manifestRoot.GetProperty("schemaVersion").GetInt32() == 4, "manifest schema version");
        Assert(manifestRoot.GetProperty("defaultProvenance").GetString() == "generated", "manifest material provenance");
        Assert(manifestRoot.GetProperty("generator").GetProperty("execution").GetString() == "offline-only", "manifest must declare offline-only generation");
        List<string> manifestSourcePaths = [];
        foreach (JsonElement texture in manifestRoot.GetProperty("textures").EnumerateArray())
        {
            JsonElement source = texture.GetProperty("source");
            Assert(source.GetProperty("domain").GetString() == "sampledomain", "manifest source domain");
            string sourcePath = source.GetProperty("path").GetString()!;
            string sidecarStem = $"sampledomain:{sourcePath[..^4]}";
            Assert(texture.GetProperty("normal").GetProperty("asset").GetString() == sidecarStem + "_n.png", "manifest adjacent normal reference");
            Assert(texture.GetProperty("roughness").GetProperty("asset").GetString() == sidecarStem + "_r.png", "manifest adjacent roughness reference");
            Assert(texture.GetProperty("metallic").GetProperty("asset").GetString() == sidecarStem + "_m.png", "manifest adjacent metallic reference");
            Assert(texture.GetProperty("emissive").GetProperty("asset").GetString() == sidecarStem + "_e.png", "manifest adjacent emissive reference");
            manifestSourcePaths.Add(sourcePath);
        }

        Assert(manifestSourcePaths.Contains("textures/block/stone/generated.png"), "manifest generated source asset path");
        Assert(manifestSourcePaths.Contains("textures/block/plant/side_normal.png"), "manifest _normal albedo source asset path");

        PackBuildReport assetsMode = builder.Build(new PackBuildRequest(
            null,
            Path.Combine(modRoot, "assets"),
            "samplemod",
            "2.3.4",
            [new SourceDomainMapping("sampledomain", "game")],
            Path.Combine(root, "pack-assets-mode"),
            "sampleassetspbr",
            "1.0.0",
            "Sample Assets PBR",
            "block",
            "auto",
            FlipGreen: false));
        Assert(assetsMode.GeneratedTextureCount == 2, "source-assets mode should generate every missing pair");
        Assert(assetsMode.AuthoredSidecarCount == 1, "source-assets mode should preserve authored sidecars");
        Assert(assetsMode.SourceOrigins.SequenceEqual(["sampledomain"]), "source-assets report should retain physical origin");
        Assert(assetsMode.SourceDomains.SequenceEqual(["game"]), "source-assets report should expose logical asset domain");
        Assert(File.Exists(assetsMode.ZipPath), "source-assets mode should create an installable ZIP");
        using (ZipArchive assetsModeArchive = ZipFile.OpenRead(assetsMode.ZipPath))
        {
            Assert(
                assetsModeArchive.GetEntry("assets/game/textures/block/stone/generated_n.png") is not null,
                "source-assets mapping must package sidecars beneath the logical game domain");
            Assert(
                !assetsModeArchive.Entries.Any(entry => entry.FullName.Contains("/textures/pbr/", StringComparison.Ordinal)),
                "source-assets mapping must not use a central PBR texture tree");
            ZipArchiveEntry assetsManifestEntry = assetsModeArchive.GetEntry(
                "assets/sampleassetspbr/config/vintagertx/pbr-manifest.json")!;
            using Stream assetsManifestStream = assetsManifestEntry.Open();
            using JsonDocument assetsManifest = JsonDocument.Parse(assetsManifestStream);
            foreach (JsonElement texture in assetsManifest.RootElement.GetProperty("textures").EnumerateArray())
            {
                JsonElement mappedSource = texture.GetProperty("source");
                Assert(mappedSource.GetProperty("origin").GetString() == "sampledomain", "manifest should retain physical source origin");
                Assert(mappedSource.GetProperty("domain").GetString() == "game", "manifest should map source to logical game domain");
            }
        }

        string legacyZip = Path.Combine(root, "legacy-manifest.zip");
        CreateLegacyManifestFixture(first.ZipPath, legacyZip);
        PackRepackageReport migrationA = builder.RepackageLegacyManifest(
            legacyZip,
            Path.Combine(root, "migrated-a.zip"));
        PackRepackageReport migrationB = builder.RepackageLegacyManifest(
            legacyZip,
            Path.Combine(root, "migrated-b.zip"));
        Assert(migrationA.ZipSha256 == migrationB.ZipSha256, "legacy manifest repack must be deterministic");
        Assert(migrationA.MigratedManifestCount == 1, "exactly one legacy manifest should migrate");
        using ZipArchive migratedArchive = ZipFile.OpenRead(migrationA.ZipPath);
        Assert(
            migratedArchive.GetEntry("assets/samplepbr/config/vintagertx/pbr-manifest.json") is not null,
            "repacked manifest should use the public config category");
        Assert(
            migratedArchive.GetEntry("assets/samplepbr/vintagertx/pbr-manifest.json") is null,
            "repacked archive should remove the unindexed legacy entry");

        PackDomainRemapReport remapA = builder.RemapManifestDomain(
            first.ZipPath,
            Path.Combine(root, "domain-remap-a.zip"),
            "sampledomain",
            "game");
        PackDomainRemapReport remapB = builder.RemapManifestDomain(
            first.ZipPath,
            Path.Combine(root, "domain-remap-b.zip"),
            "sampledomain",
            "game");
        Assert(remapA.ZipSha256 == remapB.ZipSha256, "domain remap must be byte-for-byte deterministic");
        Assert(remapA.RemappedTextureCount == 2, "every generated source should be domain-remapped");
        using ZipArchive remappedArchive = ZipFile.OpenRead(remapA.ZipPath);
        Assert(
            remappedArchive.GetEntry("assets/game/textures/block/stone/generated_n.png") is not null,
            "domain remap must relocate generated sidecars to the logical asset domain");
        ZipArchiveEntry remappedManifestEntry = remappedArchive.GetEntry(
            "assets/samplepbr/config/vintagertx/pbr-manifest.json")!;
        using Stream remappedManifestStream = remappedManifestEntry.Open();
        using JsonDocument remappedManifest = JsonDocument.Parse(remappedManifestStream);
        foreach (JsonElement texture in remappedManifest.RootElement.GetProperty("textures").EnumerateArray())
        {
            JsonElement remappedSource = texture.GetProperty("source");
            Assert(remappedSource.GetProperty("origin").GetString() == "sampledomain", "domain remap should record physical origin");
            Assert(remappedSource.GetProperty("domain").GetString() == "game", "domain remap should use logical game domain");
            string remappedSourcePath = remappedSource.GetProperty("path").GetString()!;
            Assert(
                texture.GetProperty("normal").GetProperty("asset").GetString()
                    == $"game:{remappedSourcePath[..^4]}_n.png",
                "domain remap should rewrite adjacent map AssetLocations");
        }
        PackValidationReport remappedValidation = new PbrPackValidator().Validate(new PackValidationRequest(
            remapA.ZipPath,
            Path.Combine(modRoot, "assets"),
            Deep: true));
        Assert(
            remappedValidation.IsValid,
            "domain-remapped native sidecar pack should validate: "
            + string.Join(" | ", remappedValidation.Errors));
    }

    private static void ValidateSourceAssetPrecedencePack(string root)
    {
        string assetsRoot = Path.Combine(root, "precedence-assets");
        string gameShared = Path.Combine(assetsRoot, "game", "textures", "entity", "shared.png");
        string survivalShared = Path.Combine(assetsRoot, "survival", "textures", "entity", "shared.png");
        string creativeIdentical = Path.Combine(assetsRoot, "creative", "textures", "entity", "identical.png");
        string survivalIdentical = Path.Combine(assetsRoot, "survival", "textures", "entity", "identical.png");
        WriteSolidFixture(gameShared, new SKColor(180, 40, 30, 255));
        WriteSolidFixture(survivalShared, new SKColor(25, 80, 210, 255));
        Directory.CreateDirectory(Path.GetDirectoryName(creativeIdentical)!);
        WritePeriodicFixture(creativeIdentical);
        Directory.CreateDirectory(Path.GetDirectoryName(survivalIdentical)!);
        File.Copy(creativeIdentical, survivalIdentical);

        PbrPackBuilder builder = new();
        PackBuildRequest lowToHighRequest = new(
            null,
            assetsRoot,
            "game",
            "1.22.7",
            [
                new SourceDomainMapping("game", "game"),
                new SourceDomainMapping("creative", "game"),
                new SourceDomainMapping("survival", "game")
            ],
            Path.Combine(root, "precedence-pack"),
            "precedencepbr",
            "1.0.0",
            "Precedence PBR",
            "all",
            "auto",
            FlipGreen: false);
        PackBuildReport lowToHigh = builder.Build(lowToHighRequest);

        Assert(lowToHigh.CandidateTextureCount == 4, "physical collision candidates should remain observable");
        Assert(lowToHigh.LogicalTextureCount == 2, "logical collision resolution should deduplicate mapped assets");
        Assert(lowToHigh.GeneratedTextureCount == 2, "only effective logical winners should be generated");
        Assert(lowToHigh.LogicalOverrideCount == 2, "every replaced physical source should be journaled");
        Assert(lowToHigh.IdenticalLogicalOverrideCount == 1, "byte-identical duplicate should be classified explicitly");
        Assert(lowToHigh.DivergentLogicalOverrideCount == 1, "content-changing override should be classified explicitly");
        Assert(lowToHigh.SourceOrigins.SequenceEqual(["survival"]), "last domain mapping should win both collisions");

        PbrLogicalOverride divergent = lowToHigh.Overrides.Single(item =>
            item.LogicalAssetKey == "game:textures/entity/shared.png");
        Assert(!divergent.ContentIdentical, "different source bytes must not be reported as an identical duplicate");
        Assert(divergent.OverriddenOrigin == "game" && divergent.WinningOrigin == "survival",
            "divergent collision should follow low-to-high domain-map precedence");
        Assert(divergent.OverriddenSha256 == Sha256(gameShared)
            && divergent.WinningSha256 == Sha256(survivalShared),
            "divergent collision journal should retain both source fingerprints");

        PbrLogicalOverride identical = lowToHigh.Overrides.Single(item =>
            item.LogicalAssetKey == "game:textures/entity/identical.png");
        Assert(identical.ContentIdentical, "equal source bytes should be reported as an identical duplicate");
        Assert(identical.OverriddenOrigin == "creative" && identical.WinningOrigin == "survival",
            "identical collision should still record the effective source origin");
        Assert(identical.OverriddenSha256 == identical.WinningSha256,
            "identical collision fingerprints should match");

        PackValidationReport validation = new PbrPackValidator().Validate(new PackValidationRequest(
            lowToHigh.ZipPath,
            assetsRoot,
            Deep: true));
        Assert(validation.IsValid,
            "precedence-resolved pack should pass deep validation: " + string.Join(" | ", validation.Errors));
        Assert(validation.TextureCount == 2 && validation.MapCount == 8,
            "precedence pack should contain exactly four sidecars per logical texture");

        using (ZipArchive archive = ZipFile.OpenRead(lowToHigh.ZipPath))
        {
            Assert(archive.Entries.Where(entry => entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .All(entry => entry.FullName.EndsWith("_n.png", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("_r.png", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("_m.png", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("_e.png", StringComparison.Ordinal)),
                "precedence pack must never redistribute source albedos");
            ZipArchiveEntry manifestEntry = archive.GetEntry(
                "assets/precedencepbr/config/vintagertx/pbr-manifest.json")!;
            using Stream manifestStream = manifestEntry.Open();
            using JsonDocument manifest = JsonDocument.Parse(manifestStream);
            foreach (JsonElement texture in manifest.RootElement.GetProperty("textures").EnumerateArray())
            {
                JsonElement source = texture.GetProperty("source");
                Assert(source.GetProperty("origin").GetString() == "survival",
                    "manifest should retain the winning physical origin");
                string sourcePath = source.GetProperty("path").GetString()!;
                Assert(sourcePath == sourcePath.ToLowerInvariant(),
                    "logical asset paths should be normalized to lower case");
            }
        }

        PackBuildReport reversed = builder.Build(lowToHighRequest with
        {
            DomainMappings =
            [
                new SourceDomainMapping("survival", "game"),
                new SourceDomainMapping("creative", "game"),
                new SourceDomainMapping("game", "game")
            ],
            Output = Path.Combine(root, "precedence-pack-reversed")
        });
        PbrLogicalOverride reversedDivergent = reversed.Overrides.Single(item =>
            item.LogicalAssetKey == "game:textures/entity/shared.png");
        Assert(reversedDivergent.WinningOrigin == "game",
            "reversing domain-map order should reverse the divergent winner");
        PbrLogicalOverride reversedIdentical = reversed.Overrides.Single(item =>
            item.LogicalAssetKey == "game:textures/entity/identical.png");
        Assert(reversedIdentical.WinningOrigin == "creative",
            "the final origin containing a logical asset should win");
        Assert(reversed.ZipSha256 != lowToHigh.ZipSha256,
            "changing a divergent winner should change the deterministic pack fingerprint");
    }

    private static void CreateLegacyManifestFixture(string currentZip, string legacyZip)
    {
        const string currentManifest = "assets/samplepbr/config/vintagertx/pbr-manifest.json";
        const string legacyManifest = "assets/samplepbr/vintagertx/pbr-manifest.json";
        DateTimeOffset timestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using ZipArchive sourceArchive = ZipFile.OpenRead(currentZip);
        using FileStream outputStream = File.Create(legacyZip);
        using ZipArchive destinationArchive = new(outputStream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (ZipArchiveEntry sourceEntry in sourceArchive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            string targetName = sourceEntry.FullName == currentManifest ? legacyManifest : sourceEntry.FullName;
            ZipArchiveEntry targetEntry = destinationArchive.CreateEntry(targetName, CompressionLevel.Optimal);
            targetEntry.LastWriteTime = timestamp;
            using Stream source = sourceEntry.Open();
            using Stream target = targetEntry.Open();
            source.CopyTo(target);
        }
    }

    private static void AssertEquivalentOutputs(GenerationReport first, GenerationReport second)
    {
        GeneratedTexture a = first.Textures[0];
        GeneratedTexture b = second.Textures[0];
        Assert(a.NormalSha256 == b.NormalSha256, "normal output must be byte-for-byte deterministic");
        Assert(a.RoughnessSha256 == b.RoughnessSha256, "roughness output must be byte-for-byte deterministic");
        Assert(a.MetallicSha256 == b.MetallicSha256, "metallic output must be byte-for-byte deterministic");
        Assert(a.EmissiveSha256 == b.EmissiveSha256, "emissive output must be byte-for-byte deterministic");

        // Root-relative manifests contain no timestamp or machine-specific absolute path.
        Assert(
            File.ReadAllBytes(first.ManifestPath).SequenceEqual(File.ReadAllBytes(second.ManifestPath)),
            "manifests must be byte-for-byte deterministic");
    }

    private static void ValidateNormalMap(string normalPath)
    {
        using SKBitmap bitmap = SKBitmap.Decode(normalPath)
            ?? throw new InvalidDataException("Self-test normal map could not be decoded.");
        Assert(bitmap.Width == 32 && bitmap.Height == 32, "normal dimensions must match the source");

        SKColor[] pixels = bitmap.Pixels;
        int minRed = pixels.Min(color => color.Red);
        int maxRed = pixels.Max(color => color.Red);
        Assert(maxRed - minRed >= 8, "normal map must contain measurable relief");
        List<float> tiltAngles = [];

        foreach (SKColor color in pixels)
        {
            if (color.Alpha == 0)
            {
                continue;
            }

            float x = ((color.Red / 255f) * 2f) - 1f;
            float y = ((color.Green / 255f) * 2f) - 1f;
            float z = ((color.Blue / 255f) * 2f) - 1f;
            float length = MathF.Sqrt((x * x) + (y * y) + (z * z));
            float angle = MathF.Atan2(MathF.Sqrt((x * x) + (y * y)), MathF.Max(z, 0.000001f));
            tiltAngles.Add(angle * (180f / MathF.PI));
            Assert(MathF.Abs(length - 1f) < 0.025f, "encoded normals must remain normalized within RGBA8 tolerance");
            Assert(z >= 0f, "generated tangent normals must face away from the surface");
        }

        tiltAngles.Sort();
        float medianTilt = tiltAngles[tiltAngles.Count / 2];
        float maximumAllowedTilt = MathF.Atan(PbrGenerator.GeneratedNormalMaximumSlope)
            * (180f / MathF.PI)
            + 1.0f;
        Assert(medianTilt >= 3f, "generated normal response is too weak for low-resolution block textures");
        Assert(medianTilt <= 18f, "generated albedo relief is too embossed for a conservative fallback");
        Assert(tiltAngles[^1] <= maximumAllowedTilt, "generated normal slope limiter was exceeded");

        float seamDifference = 0f;
        for (int y = 0; y < bitmap.Height; y++)
        {
            seamDifference += Math.Abs(bitmap.GetPixel(0, y).Red - bitmap.GetPixel(bitmap.Width - 1, y).Red);
        }

        seamDifference /= bitmap.Height;
        Assert(seamDifference < 65f, "wrap sampling should not introduce a hard vertical seam");
    }

    private static void ValidateNormalYAxisConvention(PbrGenerator generator, string root)
    {
        string assetsRoot = Path.Combine(root, "orientation-assets");
        string sourcePath = Path.Combine(
            assetsRoot,
            "sample",
            "textures",
            "block",
            "vertical-ramp.png");
        WriteVerticalRampFixture(sourcePath);
        GenerationRequest request = new(
            [sourcePath],
            Path.Combine(root, "orientation-opengl"),
            assetsRoot,
            "generic",
            FlipGreen: false,
            SkipExisting: false);
        GenerationReport openGl = generator.Generate(request);
        GenerationReport directX = generator.Generate(request with
        {
            OutputRoot = Path.Combine(root, "orientation-directx"),
            FlipGreen = true
        });

        using SKBitmap openGlNormal = SKBitmap.Decode(Path.Combine(openGl.OutputRoot, openGl.Textures[0].Normal))
            ?? throw new InvalidDataException("OpenGL orientation normal could not be decoded.");
        using SKBitmap directXNormal = SKBitmap.Decode(Path.Combine(directX.OutputRoot, directX.Textures[0].Normal))
            ?? throw new InvalidDataException("DirectX orientation normal could not be decoded.");
        SKColor openGlCenter = openGlNormal.GetPixel(openGlNormal.Width / 2, openGlNormal.Height / 2);
        SKColor directXCenter = directXNormal.GetPixel(directXNormal.Width / 2, directXNormal.Height / 2);
        Assert(openGlCenter.Green > 132, "OpenGL +Y normal must encode a positive green response for a downward image ramp");
        Assert(directXCenter.Green < 124, "--flip-green must encode the opposite DirectX Y response");
        Assert(Math.Abs(openGlCenter.Red - directXCenter.Red) <= 1
            && Math.Abs(openGlCenter.Blue - directXCenter.Blue) <= 1
            && Math.Abs((openGlCenter.Green + directXCenter.Green) - 255) <= 1,
            "--flip-green must change only the signed tangent Y channel");
    }

    private static void ValidateRoughnessOrdering(
        PbrGenerator generator,
        string sourcePath,
        string sourceRoot,
        string root)
    {
        float polishedMetal = GenerateAndAverageRoughness(generator, sourcePath, sourceRoot, root, "polished-metal");
        float polished = GenerateAndAverageRoughness(generator, sourcePath, sourceRoot, root, "polished");
        float metal = GenerateAndAverageRoughness(generator, sourcePath, sourceRoot, root, "metal");
        float anvil = GenerateAndAverageRoughness(generator, sourcePath, sourceRoot, root, "anvil");
        float stone = GenerateAndAverageRoughness(generator, sourcePath, sourceRoot, root, "stone");
        float cloth = GenerateAndAverageRoughness(generator, sourcePath, sourceRoot, root, "cloth");
        Assert(
            polishedMetal < polished && polished < metal && metal < anvil && anvil < stone && stone < cloth,
            "material profiles should distinguish polished metal, polished dielectric, metal, forged anvil, matte stone and cloth roughness");
    }

    private static void ValidateMaterialMaps(
        PbrGenerator generator,
        string sourcePath,
        string sourceRoot,
        string root)
    {
        GenerationReport metal = generator.Generate(new GenerationRequest(
            [sourcePath],
            Path.Combine(root, "material-metal"),
            sourceRoot,
            "metal",
            FlipGreen: false,
            SkipExisting: false));
        using SKBitmap metallic = SKBitmap.Decode(Path.Combine(metal.OutputRoot, metal.Textures[0].Metallic))
            ?? throw new InvalidDataException("Could not decode metallic output.");
        Assert(
            metallic.Pixels.Where(color => color.Alpha > 0).Average(color => color.Red / 255f) > 0.80f,
            "metal profile must produce a strong metallic response");

        GenerationReport anvil = generator.Generate(new GenerationRequest(
            [sourcePath],
            Path.Combine(root, "material-anvil"),
            sourceRoot,
            "anvil",
            FlipGreen: false,
            SkipExisting: false));
        using SKBitmap anvilMetallic = SKBitmap.Decode(Path.Combine(anvil.OutputRoot, anvil.Textures[0].Metallic))
            ?? throw new InvalidDataException("Could not decode anvil metallic output.");
        Assert(
            anvilMetallic.Pixels.Where(color => color.Alpha > 0).Average(color => color.Red / 255f) > 0.94f,
            "anvil profile must remain an exposed metal rather than a polished dielectric");

        GenerationReport polished = generator.Generate(new GenerationRequest(
            [sourcePath],
            Path.Combine(root, "material-polished"),
            sourceRoot,
            "polished",
            FlipGreen: false,
            SkipExisting: false));
        using SKBitmap polishedMetallic = SKBitmap.Decode(Path.Combine(polished.OutputRoot, polished.Textures[0].Metallic))
            ?? throw new InvalidDataException("Could not decode polished dielectric metallic output.");
        Assert(
            polishedMetallic.Pixels.Where(color => color.Alpha > 0).All(color => color.Red == 0),
            "a polished stone or painted panel remains dielectric even though it is reflective");

        string emissiveSource = Path.Combine(
            sourceRoot,
            "survival",
            "textures",
            "block",
            "wood",
            "torch-lit.png");
        Directory.CreateDirectory(Path.GetDirectoryName(emissiveSource)!);
        File.Copy(sourcePath, emissiveSource);
        GenerationReport emissive = generator.Generate(new GenerationRequest(
            [emissiveSource],
            Path.Combine(root, "material-emissive"),
            sourceRoot,
            "auto",
            FlipGreen: false,
            SkipExisting: false));
        using SKBitmap emissiveMap = SKBitmap.Decode(Path.Combine(emissive.OutputRoot, emissive.Textures[0].Emissive))
            ?? throw new InvalidDataException("Could not decode emissive output.");
        Assert(
            emissiveMap.Pixels.Any(color => color.Alpha > 0 && color.Red > 0),
            "lit-source heuristic must identify emissive texels");

        string whiteHighlightSource = Path.Combine(
            sourceRoot,
            "survival",
            "textures",
            "block",
            "wood",
            "lantern-white-highlight.png");
        WriteSolidFixture(whiteHighlightSource, new SKColor(248, 248, 248, 255));
        GenerationReport whiteHighlight = generator.Generate(new GenerationRequest(
            [whiteHighlightSource],
            Path.Combine(root, "material-white-highlight"),
            sourceRoot,
            "auto",
            FlipGreen: false,
            SkipExisting: false));
        using SKBitmap whiteHighlightMap = SKBitmap.Decode(Path.Combine(
            whiteHighlight.OutputRoot,
            whiteHighlight.Textures[0].Emissive))
            ?? throw new InvalidDataException("Could not decode white-highlight emissive output.");
        Assert(
            whiteHighlightMap.Pixels.All(color => color.Red == 0),
            "white texture highlights must not become generated emission");
    }

    private static float GenerateAndAverageRoughness(
        PbrGenerator generator,
        string sourcePath,
        string sourceRoot,
        string root,
        string profile)
    {
        GenerationReport report = generator.Generate(new GenerationRequest(
            [sourcePath],
            Path.Combine(root, "roughness-" + profile),
            sourceRoot,
            profile,
            FlipGreen: false,
            SkipExisting: false));
        string roughnessPath = Path.Combine(report.OutputRoot, report.Textures[0].Roughness);
        using SKBitmap bitmap = SKBitmap.Decode(roughnessPath)
            ?? throw new InvalidDataException($"Could not decode {profile} roughness output.");
        return bitmap.Pixels.Where(color => color.Alpha > 0).Average(color => color.Red / 255f);
    }

    private static void ValidateProfileRouting()
    {
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/wood/planks/oak.png").Name == "wood", "wood route");
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/metal/anvil/copper.png").Name == "anvil", "forged anvil route");
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/metal/polished-panel-steel.png").Name == "polished-metal", "polished metal route");
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/stone/polished-panel-granite.png").Name == "polished", "polished dielectric route");
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/metal/plate-copper.png").Name == "metal", "generic metal route");
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/cloth/linen.png").Name == "cloth", "cloth route");
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/glass/leaded.png").Name == "glass", "glass route");
        Assert(MaterialProfile.Resolve("auto", "survival/textures/block/stone/brick/granite.png").Name == "brick", "brick route before stone");
    }

    private static void WritePeriodicFixture(string path)
    {
        const int size = 32;
        SKColor[] pixels = new SKColor[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float wave = (MathF.Sin(2f * MathF.PI * x / size) + MathF.Cos(4f * MathF.PI * y / size)) * 0.5f;
                bool mortar = (x % 8 == 0) || (y % 8 == 0);
                int luminance = mortar ? 40 : (int)Math.Clamp(145f + (wave * 45f), 0f, 255f);
                byte alpha = (x is 14 or 15) && (y is 14 or 15) ? (byte)0 : (byte)255;
                pixels[(y * size) + x] = new SKColor((byte)luminance, (byte)(luminance * 0.72f), (byte)(luminance * 0.55f), alpha);
            }
        }

        using SKBitmap bitmap = new(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Pixels = pixels;
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void WriteSolidFixture(string path, SKColor color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using SKBitmap bitmap = new(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void WriteVerticalRampFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using SKBitmap bitmap = new(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < bitmap.Height; y++)
        {
            byte luminance = (byte)(24 + y * 13);
            for (int x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(luminance, luminance, luminance, 255));
            }
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + message + ".");
        }
    }
}
