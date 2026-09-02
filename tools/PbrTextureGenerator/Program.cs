namespace VintageRTX.PbrTextureGenerator;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "generate" => RunGenerate(args[1..]),
                "pack" => RunPack(args[1..]),
                "validate-pack" => RunValidatePack(args[1..]),
                "analyze-normals" => RunAnalyzeNormals(args[1..]),
                "repack-manifest" => RunRepackManifest(args[1..]),
                "remap-domain" => RunRemapDomain(args[1..]),
                "profiles" => ListProfiles(),
                "self-test" => RunSelfTest(args[1..]),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ERROR: " + exception.Message);
            return 1;
        }
    }

    private static int RunValidatePack(string[] args)
    {
        string? input = null;
        string? sourceAssetsRoot = null;
        bool deep = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    input = NextValue(args, ref index, "--input");
                    break;
                case "--source-assets":
                    sourceAssetsRoot = NextValue(args, ref index, "--source-assets");
                    break;
                case "--deep":
                    deep = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown validate-pack option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("validate-pack requires --input.");
        }

        PackValidationReport report = new PbrPackValidator().Validate(new PackValidationRequest(
            input,
            sourceAssetsRoot,
            deep));
        Console.WriteLine($"Pack: {report.PackPath}");
        Console.WriteLine($"SHA-256: {report.PackSha256}");
        Console.WriteLine($"Manifests: {report.ManifestCount}");
        Console.WriteLine($"Textures: {report.TextureCount}");
        Console.WriteLine($"Maps: {report.MapCount}");
        Console.WriteLine($"Sources verified: {report.SourceVerifiedCount}");
        Console.WriteLine($"Duplicate logical keys: {report.DuplicateLogicalKeyCount}");
        Console.WriteLine($"Deep pixel validation: {(deep ? "enabled" : "disabled")}");
        Console.WriteLine($"Elapsed: {TimeSpan.FromMilliseconds(report.ElapsedMilliseconds):c}");
        Console.WriteLine($"Valid: {report.IsValid}");
        foreach (string error in report.Errors.Take(50))
        {
            Console.Error.WriteLine("  ERROR: " + error);
        }

        if (report.Errors.Count > 50)
        {
            Console.Error.WriteLine($"  ... {report.Errors.Count - 50} additional error(s) omitted.");
        }

        return report.IsValid ? 0 : 1;
    }

    private static int RunAnalyzeNormals(string[] args)
    {
        string? input = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--input")
            {
                throw new ArgumentException($"Unknown analyze-normals option '{args[index]}'.");
            }

            input = NextValue(args, ref index, "--input");
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("analyze-normals requires --input.");
        }

        NormalAnalysisReport report = new NormalMapAnalyzer().Analyze(input);
        Console.WriteLine($"Pack: {report.PackPath}");
        Console.WriteLine($"Algorithm: {report.AlgorithmVersion}");
        Console.WriteLine($"Textures: {report.TextureCount}");
        Console.WriteLine($"Opaque pixels: {report.OpaquePixelCount}");
        Console.WriteLine(
            $"Pixel tilt degrees: mean={report.MeanPixelAngleDegrees:F3}, p50={report.P50PixelAngleDegrees:F3}, "
            + $"p90={report.P90PixelAngleDegrees:F3}, p95={report.P95PixelAngleDegrees:F3}, p99={report.P99PixelAngleDegrees:F3}");
        Console.WriteLine(
            $"Texture RMS tilt degrees: mean={report.MeanTextureRmsAngleDegrees:F3}, "
            + $"p50={report.P50TextureRmsAngleDegrees:F3}, p90={report.P90TextureRmsAngleDegrees:F3}");
        Console.WriteLine(
            $"Quasi-flat textures: <2deg={report.TextureCountBelow2Degrees}, "
            + $"<4deg={report.TextureCountBelow4Degrees}, <8deg={report.TextureCountBelow8Degrees}");
        Console.WriteLine(
            $"Decoded vector length error: mean={report.MeanVectorLengthError:F6}, max={report.MaximumVectorLengthError:F6}");
        Console.WriteLine($"Mean decoded vector: ({report.MeanX:F6}, {report.MeanY:F6}, {report.MeanZ:F6})");
        foreach (NormalProfileAnalysis profile in report.Profiles)
        {
            Console.WriteLine(
                $"  {profile.Profile,-8} textures={profile.TextureCount,4}, mean-rms={profile.MeanTextureRmsAngleDegrees,7:F3}, "
                + $"p50={profile.P50TextureRmsAngleDegrees,7:F3}, p90={profile.P90TextureRmsAngleDegrees,7:F3}, "
                + $"<4deg={profile.TextureCountBelow4Degrees,4}");
        }

        return 0;
    }

    private static int RunRemapDomain(string[] args)
    {
        string? input = null;
        string? output = null;
        string? origin = null;
        string? assetDomain = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    input = NextValue(args, ref index, "--input");
                    break;
                case "--output":
                    output = NextValue(args, ref index, "--output");
                    break;
                case "--origin":
                    origin = NextValue(args, ref index, "--origin");
                    break;
                case "--asset-domain":
                    assetDomain = NextValue(args, ref index, "--asset-domain");
                    break;
                default:
                    throw new ArgumentException($"Unknown remap-domain option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(input)
            || string.IsNullOrWhiteSpace(origin)
            || string.IsNullOrWhiteSpace(assetDomain))
        {
            throw new ArgumentException(
                "remap-domain requires --input, --origin and --asset-domain. Omit --output to replace atomically.");
        }

        PackDomainRemapReport report = new PbrPackBuilder().RemapManifestDomain(
            input,
            output,
            origin,
            assetDomain);
        Console.WriteLine($"Remapped: {report.ZipPath}");
        Console.WriteLine($"SHA-256: {report.ZipSha256}");
        Console.WriteLine($"Entries: {report.EntryCount}");
        Console.WriteLine($"Manifests: {report.ManifestCount}");
        Console.WriteLine($"Remapped textures: {report.RemappedTextureCount}");
        Console.WriteLine($"Mapping: {report.SourceOrigin} -> {report.LogicalAssetDomain}");
        return 0;
    }

    private static int RunRepackManifest(string[] args)
    {
        string? input = null;
        string? output = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    input = NextValue(args, ref index, "--input");
                    break;
                case "--output":
                    output = NextValue(args, ref index, "--output");
                    break;
                default:
                    throw new ArgumentException($"Unknown repack-manifest option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("repack-manifest requires --input. Omit --output to replace it atomically.");
        }

        PackRepackageReport report = new PbrPackBuilder().RepackageLegacyManifest(input, output);
        Console.WriteLine($"Repacked: {report.ZipPath}");
        Console.WriteLine($"SHA-256: {report.ZipSha256}");
        Console.WriteLine($"Entries: {report.EntryCount}");
        Console.WriteLine($"Migrated manifests: {report.MigratedManifestCount}");
        return 0;
    }

    private static int RunPack(string[] args)
    {
        string? modRoot = null;
        string? sourceAssetsRoot = null;
        string? sourceModId = null;
        string? sourceModVersion = null;
        List<SourceDomainMapping> domainMappings = [];
        string? output = null;
        string? packId = null;
        string packVersion = "1.0.0";
        string? packName = null;
        string scope = "block";
        string profile = "auto";
        bool flipGreen = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--mod-root":
                    modRoot = NextValue(args, ref index, "--mod-root");
                    break;
                case "--source-assets":
                    sourceAssetsRoot = NextValue(args, ref index, "--source-assets");
                    break;
                case "--source-mod-id":
                    sourceModId = NextValue(args, ref index, "--source-mod-id");
                    break;
                case "--source-mod-version":
                    sourceModVersion = NextValue(args, ref index, "--source-mod-version");
                    break;
                case "--source-domain":
                    string identityDomain = NextValue(args, ref index, "--source-domain");
                    domainMappings.Add(new SourceDomainMapping(identityDomain, identityDomain));
                    break;
                case "--domain-map":
                    domainMappings.Add(ParseDomainMapping(NextValue(args, ref index, "--domain-map")));
                    break;
                case "--output":
                    output = NextValue(args, ref index, "--output");
                    break;
                case "--pack-id":
                    packId = NextValue(args, ref index, "--pack-id");
                    break;
                case "--pack-version":
                    packVersion = NextValue(args, ref index, "--pack-version");
                    break;
                case "--pack-name":
                    packName = NextValue(args, ref index, "--pack-name");
                    break;
                case "--scope":
                    scope = NextValue(args, ref index, "--scope").ToLowerInvariant();
                    break;
                case "--profile":
                    profile = NextValue(args, ref index, "--profile");
                    break;
                case "--flip-green":
                    flipGreen = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown pack option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(output)
            || string.IsNullOrWhiteSpace(packId))
        {
            throw new ArgumentException("pack requires --output, --pack-id and one source mode.");
        }

        if (!profile.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !MaterialProfile.All.ContainsKey(profile))
        {
            throw new ArgumentException($"Unknown profile '{profile}'. Use 'profiles' to list available profiles.");
        }

        PackBuildReport report = new PbrPackBuilder().Build(new PackBuildRequest(
            modRoot,
            sourceAssetsRoot,
            sourceModId,
            sourceModVersion,
            domainMappings,
            output,
            packId,
            packVersion,
            packName,
            scope,
            profile,
            flipGreen));

        Console.WriteLine($"Pack: {report.ZipPath}");
        Console.WriteLine($"SHA-256: {report.ZipSha256}");
        Console.WriteLine($"Source mod: {report.SourceModId} {report.SourceModVersion}");
        Console.WriteLine($"Source origins: {string.Join(", ", report.SourceOrigins)}");
        Console.WriteLine($"Logical asset domains: {string.Join(", ", report.SourceDomains)}");
        Console.WriteLine($"Physical candidate albedos: {report.CandidateTextureCount}");
        Console.WriteLine($"Effective logical albedos: {report.LogicalTextureCount}");
        Console.WriteLine($"Generated pairs: {report.GeneratedTextureCount}");
        Console.WriteLine($"Existing authored sidecar pairs skipped: {report.AuthoredSidecarCount}");
        Console.WriteLine(
            $"Logical overrides: {report.LogicalOverrideCount} "
            + $"(identical={report.IdenticalLogicalOverrideCount}, divergent={report.DivergentLogicalOverrideCount})");
        foreach (PbrLogicalOverride logicalOverride in report.Overrides)
        {
            string kind = logicalOverride.ContentIdentical ? "identical" : "divergent";
            Console.WriteLine(
                $"  {logicalOverride.LogicalAssetKey}: {logicalOverride.OverriddenOrigin} -> "
                + $"{logicalOverride.WinningOrigin} ({kind})");
        }

        Console.WriteLine($"Elapsed: {TimeSpan.FromMilliseconds(report.ElapsedMilliseconds):c}");
        return 0;
    }

    private static SourceDomainMapping ParseDomainMapping(string value)
    {
        string[] parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("--domain-map must use origin=assetdomain, for example survival=game.");
        }

        return new SourceDomainMapping(parts[0], parts[1]);
    }

    private static int RunGenerate(string[] args)
    {
        List<string> inputs = [];
        string? output = null;
        string? assetsRoot = null;
        string profile = "auto";
        bool flipGreen = false;
        bool skipExisting = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    inputs.Add(NextValue(args, ref index, "--input"));
                    break;
                case "--output":
                    output = NextValue(args, ref index, "--output");
                    break;
                case "--assets-root":
                    assetsRoot = NextValue(args, ref index, "--assets-root");
                    break;
                case "--profile":
                    profile = NextValue(args, ref index, "--profile");
                    break;
                case "--flip-green":
                    flipGreen = true;
                    break;
                case "--skip-existing":
                    skipExisting = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown generate option '{args[index]}'.");
            }
        }

        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one --input path is required.");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException("--output is required.");
        }

        if (!profile.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !MaterialProfile.All.ContainsKey(profile))
        {
            throw new ArgumentException($"Unknown profile '{profile}'. Use 'profiles' to list available profiles.");
        }

        PbrGenerator generator = new();
        GenerationReport report = generator.Generate(new GenerationRequest(
            inputs,
            output,
            assetsRoot,
            profile,
            flipGreen,
            skipExisting));

        Console.WriteLine($"Generated {report.Textures.Count} PBR texture pair(s) in {report.OutputRoot}");
        foreach (IGrouping<string, GeneratedTexture> group in report.Textures.GroupBy(texture => texture.Profile).OrderBy(group => group.Key))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        Console.WriteLine($"Manifest: {report.ManifestPath}");
        return 0;
    }

    private static int ListProfiles()
    {
        Console.WriteLine("Available material profiles:");
        foreach (MaterialProfile profile in MaterialProfile.All.Values.OrderBy(profile => profile.Name))
        {
            Console.WriteLine(
                $"  {profile.Name,-8} height={profile.HeightStrength:F2} roughness={profile.BaseRoughness:F2} "
                + $"detail={profile.DetailRoughness:F2}");
        }

        Console.WriteLine("  auto     classifies from the asset-relative path");
        return 0;
    }

    private static int RunSelfTest(string[] args)
    {
        string? output = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--output")
            {
                throw new ArgumentException($"Unknown self-test option '{args[index]}'.");
            }

            output = NextValue(args, ref index, "--output");
        }

        SelfTests.Run(output);
        return 0;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static void PrintUsage()
    {
        Console.WriteLine("VintageRTX offline PBR texture generator");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  generate --input PATH [--input PATH ...] --output PATH [options]");
        Console.WriteLine("  pack (--mod-root PATH | --source-assets PATH) --output PATH --pack-id ID [options]");
        Console.WriteLine("  validate-pack --input PACK.zip [--source-assets PATH] [--deep]");
        Console.WriteLine("  analyze-normals --input PACK.zip");
        Console.WriteLine("  repack-manifest --input PACK.zip [--output PACK.zip]");
        Console.WriteLine("  remap-domain --input PACK.zip --origin ORIGIN --asset-domain DOMAIN [--output PACK.zip]");
        Console.WriteLine("  profiles");
        Console.WriteLine("  self-test [--output PATH]");
        Console.WriteLine();
        Console.WriteLine("Generate options:");
        Console.WriteLine("  --assets-root PATH  resolve relative inputs and preserve asset-domain paths");
        Console.WriteLine("  --profile NAME      auto (default), generic, stone, brick, wood, metal, anvil, polished, polished-metal, cloth, glass");
        Console.WriteLine("  --flip-green        output DirectX-style -Y normals instead of OpenGL +Y");
        Console.WriteLine("  --skip-existing     keep existing generated PNG files");
        Console.WriteLine();
        Console.WriteLine("Pack options:");
        Console.WriteLine("  --source-mod-id ID       required with --source-assets");
        Console.WriteLine("  --source-mod-version VER required with --source-assets");
        Console.WriteLine("  --domain-map ORIGIN=DOMAIN repeatable low-to-high priority mapping; the last logical collision wins");
        Console.WriteLine("  --source-domain DOMAIN     compatibility alias for --domain-map DOMAIN=DOMAIN");
        Console.WriteLine("  --pack-version VER  generated content-mod version (default 1.0.0)");
        Console.WriteLine("  --pack-name NAME    display name in the generated modinfo.json");
        Console.WriteLine("  --scope block|all   block textures only (default) or all mod textures");
        Console.WriteLine("  --profile NAME      auto (default) or a forced material profile");
        Console.WriteLine("  --flip-green        package DirectX-style -Y normals");
        Console.WriteLine();
        Console.WriteLine("Validate-pack options:");
        Console.WriteLine("  --source-assets PATH  verify source origin/path and source SHA-256 against an assets root");
        Console.WriteLine("  --deep                decode every map and validate normal/scalar pixel encoding");
    }
}
