using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using VintageRTX.Configuration;

namespace VintageRTX.Test;

/// <summary>
/// Coordinates the runtime harness while keeping filesystem, game, and graphics state isolated.
/// </summary>
internal static class RuntimeHarness
{
    // At the supported 15 FPS floor, the delayed capture sequence followed by
    // the complete A/B/A schedule can legitimately exceed six minutes. Keep a
    // bounded margin for concurrent game load without weakening any budget.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(12);

    /// <summary>
    /// Executes async as an isolated test step and propagates failures to the owning suite.
    /// </summary>
    /// <param name="scenario">The scenario input used to configure this deterministic test path.</param>
    /// <param name="cancellationToken">The cancellation Token input used to configure this deterministic test path.</param>
    /// <returns>A task that completes when the isolated test operation and its cleanup finish.</returns>
    public static async Task<int> RunAsync(ScenarioDefinition scenario, CancellationToken cancellationToken)
    {
        if (!scenario.Automated)
        {
            Console.Error.WriteLine($"Scenario '{scenario.Name}' is catalogued but still requires an automation driver.");
            return 2;
        }

        string repositoryRoot = TestPaths.FindRepositoryRoot();
        string gameRoot = TestPaths.ResolveGameRoot();
        string artifactRoot = Path.Combine(
            repositoryRoot,
            "tests",
            "artifacts",
            "runtime",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{scenario.Name}");
        string logRoot = Path.Combine(artifactRoot, "Logs");
        Directory.CreateDirectory(logRoot);
        using RuntimeDataSandbox dataSandbox = scenario.UseIsolatedDataPath
            ? RuntimeDataSandbox.CreateNewWorld(
                TestPaths.ResolveUserDataRoot(),
                Path.GetFileName(artifactRoot))
            : RuntimeDataSandbox.Create(
                TestPaths.ResolveUserDataRoot(),
                scenario.World,
                Path.GetFileName(artifactRoot));
        if (scenario.RenderProfile.HasValue)
        {
            dataSandbox.SeedVintageRtxConfiguration(scenario.RenderProfile.Value);
            dataSandbox.CopyVintageRtxConfigurationTo(
                Path.Combine(artifactRoot, "Config", "Seeded"));
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(gameRoot, "Vintagestory.exe"),
            WorkingDirectory = gameRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Every scenario, including a copied real map, gets its own data path.
        // Vintage Story loads base mods from gameRoot and only VintageRTX from
        // the explicit add-mod path below; user saves, mods, and logs cannot be
        // discovered through the isolated root.
        startInfo.ArgumentList.Add("--dataPath");
        startInfo.ArgumentList.Add(dataSandbox.DataRoot);
        startInfo.ArgumentList.Add("--openWorld");
        startInfo.ArgumentList.Add(dataSandbox.WorldName);
        if (!string.IsNullOrWhiteSpace(scenario.WorldPreset))
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(scenario.WorldPreset);
        }
        startInfo.ArgumentList.Add("--logPath");
        startInfo.ArgumentList.Add(logRoot);
        string modBuildPath = ResolveModPath(repositoryRoot);
        startInfo.ArgumentList.Add("--addModPath");
        startInfo.ArgumentList.Add(modBuildPath);
        string runtimeTestModBuildPath = ResolveRuntimeTestModPath(modBuildPath);
        startInfo.Environment["VINTAGERTX_AUTO_CAPTURE"] = "1";
        // Readback-heavy diagnostic captures must not overlap the A/B/A sample
        // windows: a sequence of fourteen PNG readbacks otherwise looks like a
        // renderer frame-time regression even though it is test instrumentation.
        // The capture service completes this sequence before allowing any
        // benchmark phase and then enforces a two-second settling window.
        startInfo.Environment["VINTAGERTX_AUTO_CAPTURE_FRAME"] = scenario.RunBenchmark
            ? scenario.CaptureProfile == "render-lab" ? "3000" : "4200"
            : scenario.CaptureProfile == "render-lab"
                ? "1200"
                : scenario.CaptureProfile == "water-reflection"
                    // The visual lake gate has no benchmark tail. Delay its main
                    // diagnostic sequence until the 1500-tick entity checkpoint
                    // and all three ballistic impacts can precede final evidence.
                    ? "3600"
                : scenario.ValidateReflections
                || scenario.ValidateVoxelReflections
                || scenario.ValidateVoxelBounce
                || scenario.ValidateWetness
                || scenario.ShadowValidation == ShadowValidation.SunProjected
                    // Exterior/reflection probes perform a bounded camera solve
                    // after teleport. Frame 1800 leaves ample convergence time at
                    // both 60 Hz and high refresh, while completing the full
                    // diagnostic sequence before harness completion.
                    ? "1800"
                    : "900";
        startInfo.Environment["VINTAGERTX_AUTO_GAMEMODE"] = "2";
        startInfo.Environment["VINTAGERTX_AUTO_BENCHMARK"] = scenario.RunBenchmark ? "1" : "0";
        if (scenario.RunBenchmark && scenario.CaptureProfile == "render-lab")
        {
            // The isolated laboratory has no pre-existing world/chunk history to
            // settle. Keep enough samples for a meaningful 1 % low while making
            // this the fast, repeatable performance loop between full-world gates.
            startInfo.Environment["VINTAGERTX_BENCHMARK_WORLD_WARMUP_MS"] = "15000";
            startInfo.Environment["VINTAGERTX_BENCHMARK_WARMUP_FRAMES"] = "90";
            startInfo.Environment["VINTAGERTX_BENCHMARK_SAMPLE_FRAMES"] = "300";
        }
        else if (scenario.RunBenchmark)
        {
            // Full worlds apply their deterministic scenario pose shortly after
            // the first placeholder voxel generation becomes ready. Wait past
            // the subsequent terrain rebuilds so asynchronous chunk/voxel work
            // cannot contaminate either side of the A/B/A frame-time sample.
            startInfo.Environment["VINTAGERTX_BENCHMARK_WORLD_WARMUP_MS"] = "90000";
        }
        if (!string.IsNullOrWhiteSpace(scenario.CaptureProfile))
        {
            startInfo.Environment["VINTAGERTX_AUTO_CAPTURE_PROFILE"] = scenario.CaptureProfile;
        }
        startInfo.Environment["VINTAGERTX_TEST_CAMERA_LOCKED"] = scenario.ValidateReflections
            || scenario.ValidateVoxelReflections
            || scenario.ValidateVoxelBounce
            || scenario.ValidateWetness
            || scenario.ShadowValidation == ShadowValidation.SunProjected
            || scenario.CaptureProfile == "render-lab"
                ? "1"
                : "0";
        startInfo.Environment["VINTAGERTX_TEST_RUN_ID"] = Path.GetFileName(artifactRoot);
        startInfo.Environment["VINTAGERTX_TEST_TIME_HOUR"] = scenario.WorldHour.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["VINTAGERTX_TEST_CLEAR_WEATHER"] = scenario.ClearWeather ? "1" : "0";
        if (scenario.ForcedPrecipitation.HasValue)
        {
            startInfo.Environment["VINTAGERTX_TEST_PRECIPITATION"] = scenario.ForcedPrecipitation.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }
        // Always expose the scenario identity so environment-only cases such
        // as lantern-night can enforce their own daylight constraints. A named
        // runtime probe still overrides it when the scenario needs injection.
        startInfo.Environment["VINTAGERTX_TEST_SCENARIO"] = scenario.RuntimeProbe
            ?? scenario.Name;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Vintage Story process could not be started.");
        Console.WriteLine($"Validated mod search path: {modBuildPath}.");
        Console.WriteLine($"Validated runtime-test mod search path: {runtimeTestModBuildPath}.");
        Task standardOutput = CaptureProcessStreamAsync(
            process.StandardOutput,
            Path.Combine(artifactRoot, "game-stdout.log"));
        Task standardError = CaptureProcessStreamAsync(
            process.StandardError,
            Path.Combine(artifactRoot, "game-stderr.log"));
        Console.WriteLine($"Runtime scenario '{scenario.Name}' started as PID {process.Id}.");
        Console.WriteLine($"Artifacts: {artifactRoot}");

        string clientLogPath = Path.Combine(logRoot, "client-main.log");
        string serverLogPath = Path.Combine(logRoot, "server-main.log");
        Stopwatch stopwatch = Stopwatch.StartNew();
        string log = string.Empty;
        try
        {
            while (stopwatch.Elapsed < Timeout && !process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(clientLogPath) || File.Exists(serverLogPath))
                {
                    log = await ReadCombinedRuntimeLogAsync(
                        clientLogPath,
                        serverLogPath,
                        cancellationToken);
                    if (RuntimeLogValidator.IsComplete(log, scenario)
                        || RuntimeLogValidator.HasFatalFailure(log))
                    {
                        break;
                    }
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }

            await Task.WhenAll(standardOutput, standardError);
        }

        Console.WriteLine($"Vintage Story exit code: {process.ExitCode}.");

        if (File.Exists(clientLogPath) || File.Exists(serverLogPath))
        {
            log = await ReadCombinedRuntimeLogAsync(
                clientLogPath,
                serverLogPath,
                cancellationToken);
        }

        List<string> failures = [.. RuntimeLogValidator.Validate(log, scenario)];
        failures.AddRange(RuntimeImageValidator.ValidateFinalPair(
            log,
            scenario.ShadowValidation,
            scenario.ValidateReflections,
            scenario.ValidateVoxelReflections,
            scenario.ValidateVoxelBounce,
            scenario.ValidateWetness,
            scenario.RequirePbrReferenceMaterials));
        if (string.Equals(
                scenario.Name,
                "water-reflection",
                StringComparison.OrdinalIgnoreCase))
        {
            failures.AddRange(RuntimeImageValidator.ValidateProjectileSurfaceFieldDeltas(log));
        }
        dataSandbox.CopyCapturesTo(Path.Combine(artifactRoot, "Captures"));
        string configurationArtifactRoot = Path.Combine(artifactRoot, "Config", "Final");
        dataSandbox.CopyVintageRtxConfigurationTo(configurationArtifactRoot);
        string configurationArtifactPath = Path.Combine(configurationArtifactRoot, "vintagertx.json");
        if (!File.Exists(configurationArtifactPath))
        {
            failures.Add("runtime VintageRTX configuration was not archived");
        }
        else if (scenario.RenderProfile.HasValue)
        {
            VintageRtxConfig? archivedConfig = JsonConvert.DeserializeObject<VintageRtxConfig>(
                File.ReadAllText(configurationArtifactPath));
            failures.AddRange(ValidateArchivedRenderProfile(
                archivedConfig,
                scenario.RenderProfile.Value));
            string? nativeCaptureTier = ExtractEffectiveTier(
                log,
                "Capture profile evidence: label=final",
                scenario.RenderProfile.Value);
            failures.AddRange(ValidateEffectiveTier(
                nativeCaptureTier,
                scenario.RenderProfile.Value,
                "native final capture"));
            string? benchmarkTier = scenario.RunBenchmark
                ? ExtractEffectiveTier(
                    log,
                    "Stabilized A/B/A benchmark started",
                    scenario.RenderProfile.Value)
                : null;
            if (scenario.RunBenchmark)
            {
                failures.AddRange(ValidateEffectiveTier(
                    benchmarkTier,
                    scenario.RenderProfile.Value,
                    "benchmark"));
            }

            WriteRenderingProfileEvidence(
                artifactRoot,
                scenario,
                archivedConfig,
                nativeCaptureTier,
                benchmarkTier);
        }
        if (!RuntimeLogValidator.IsComplete(log, scenario)
            && !RuntimeLogValidator.HasFatalFailure(log))
        {
            failures.Add(scenario.RunBenchmark
                ? "runtime scenario timed out before the stabilized benchmark completed"
                : "runtime scenario timed out before the focused capture sequence completed");
        }

        foreach (string failure in failures)
        {
            Console.Error.WriteLine($"FAIL {failure}");
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"PASS runtime scenario {scenario.Name}");
            return 0;
        }

        return 1;
    }

    /// <summary>Compares every setting owned by an authored hardware profile with its canonical value.</summary>
    /// <param name="actual">Configuration archived after the game process stopped.</param>
    /// <param name="expectedProfile">Profile requested by the runtime scenario.</param>
    /// <returns>Actionable field-level mismatches; an empty list proves canonical persistence.</returns>
    internal static IReadOnlyList<string> ValidateArchivedRenderProfile(
        VintageRtxConfig? actual,
        VintageRtxRenderProfile expectedProfile)
    {
        if (actual is null)
        {
            return ["runtime rendering configuration could not be deserialized"];
        }

        VintageRtxConfig expected = new()
        {
            SchemaVersion = VintageRtxConfig.CurrentSchemaVersion
        };
        expected.ApplyRenderProfile(expectedProfile);
        (string Name, object? Actual, object? Expected)[] fields =
        [
            (nameof(VintageRtxConfig.SchemaVersion), actual.SchemaVersion, expected.SchemaVersion),
            (nameof(VintageRtxConfig.RenderProfile), actual.RenderProfile, expected.RenderProfile),
            (nameof(VintageRtxConfig.ScreenSpaceLightingEnabled), actual.ScreenSpaceLightingEnabled, expected.ScreenSpaceLightingEnabled),
            (nameof(VintageRtxConfig.ScreenSpaceReflectionsEnabled), actual.ScreenSpaceReflectionsEnabled, expected.ScreenSpaceReflectionsEnabled),
            (nameof(VintageRtxConfig.VoxelReflectionsEnabled), actual.VoxelReflectionsEnabled, expected.VoxelReflectionsEnabled),
            (nameof(VintageRtxConfig.VoxelLightingEnabled), actual.VoxelLightingEnabled, expected.VoxelLightingEnabled),
            (nameof(VintageRtxConfig.TemporalAccumulationEnabled), actual.TemporalAccumulationEnabled, expected.TemporalAccumulationEnabled),
            (nameof(VintageRtxConfig.SunShadowsEnabled), actual.SunShadowsEnabled, expected.SunShadowsEnabled),
            (nameof(VintageRtxConfig.AdaptiveQualityEnabled), actual.AdaptiveQualityEnabled, expected.AdaptiveQualityEnabled),
            (nameof(VintageRtxConfig.GpuBudgetMilliseconds), actual.GpuBudgetMilliseconds, expected.GpuBudgetMilliseconds),
            (nameof(VintageRtxConfig.ReflectionDistance), actual.ReflectionDistance, expected.ReflectionDistance),
            (nameof(VintageRtxConfig.VoxelBounceDistance), actual.VoxelBounceDistance, expected.VoxelBounceDistance),
            (nameof(VintageRtxConfig.VoxelBounceRayCount), actual.VoxelBounceRayCount, expected.VoxelBounceRayCount),
            (nameof(VintageRtxConfig.PointLightShadowSamples), actual.PointLightShadowSamples, expected.PointLightShadowSamples),
            (nameof(VintageRtxConfig.SunShadowDistance), actual.SunShadowDistance, expected.SunShadowDistance),
            (nameof(VintageRtxConfig.RayDistance), actual.RayDistance, expected.RayDistance),
            (nameof(VintageRtxConfig.RayCount), actual.RayCount, expected.RayCount),
            (nameof(VintageRtxConfig.RaySteps), actual.RaySteps, expected.RaySteps)
        ];

        return fields
            .Where(static field => !Equals(field.Actual, field.Expected))
            .Select(field =>
                $"runtime rendering profile field {field.Name} is {field.Actual ?? "missing"}, expected {field.Expected}")
            .ToArray();
    }

    /// <summary>Validates that an observed adaptive tier is permitted by the requested profile.</summary>
    /// <param name="tier">Tier parsed from native capture or benchmark evidence.</param>
    /// <param name="profile">Persisted hardware profile defining the adaptive floor.</param>
    /// <param name="evidenceName">Human-readable evidence source used in failures.</param>
    /// <returns>An empty sequence for a permitted tier, otherwise one actionable failure.</returns>
    internal static IReadOnlyList<string> ValidateEffectiveTier(
        string? tier,
        VintageRtxRenderProfile profile,
        string evidenceName)
    {
        string[] allowed = profile switch
        {
            VintageRtxRenderProfile.Performance => ["performance"],
            VintageRtxRenderProfile.Balanced => ["balanced", "performance"],
            VintageRtxRenderProfile.Quality => ["high", "balanced", "performance"],
            VintageRtxRenderProfile.Ultra => ["high"],
            VintageRtxRenderProfile.Extreme => ["high"],
            VintageRtxRenderProfile.Cinematic => ["high"],
            _ => []
        };
        return tier is not null && allowed.Contains(tier, StringComparer.OrdinalIgnoreCase)
            ? []
            : [$"{evidenceName} effective tier is {tier ?? "missing"}, not permitted by profile {profile}"];
    }

    /// <summary>Extracts the effective adaptive tier from one profile evidence log line.</summary>
    /// <param name="log">Merged client and server runtime log.</param>
    /// <param name="evidenceMarker">Stable text identifying capture or benchmark evidence.</param>
    /// <param name="profile">Requested profile that must occur on the same line.</param>
    /// <returns>The normalized tier label, or <see langword="null"/> when evidence is absent.</returns>
    internal static string? ExtractEffectiveTier(
        string log,
        string evidenceMarker,
        VintageRtxRenderProfile profile)
    {
        const string TierMarker = "effective-tier=";
        string profileMarker = $"profile={profile}";
        foreach (string line in log.Split('\n').Reverse())
        {
            if (!line.Contains(evidenceMarker, StringComparison.OrdinalIgnoreCase)
                || !line.Contains(profileMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int tierStart = line.IndexOf(TierMarker, StringComparison.OrdinalIgnoreCase);
            if (tierStart < 0)
            {
                return null;
            }

            tierStart += TierMarker.Length;
            int tierEnd = line.IndexOfAny([',', ';', ')', '.', '\r'], tierStart);
            string tier = (tierEnd >= 0 ? line[tierStart..tierEnd] : line[tierStart..]).Trim();
            return tier.Length == 0 ? null : tier.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>Builds a stable hash over only the settings owned by a hardware profile.</summary>
    /// <param name="config">Configuration whose canonical hardware budget must be fingerprinted.</param>
    /// <returns>Uppercase SHA-256 fingerprint suitable for a durable artifact manifest.</returns>
    internal static string CreateRenderProfileFingerprint(VintageRtxConfig config)
    {
        string canonical = string.Join(
            "|",
            config.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            config.RenderProfile,
            config.ScreenSpaceLightingEnabled,
            config.ScreenSpaceReflectionsEnabled,
            config.VoxelReflectionsEnabled,
            config.VoxelLightingEnabled,
            config.TemporalAccumulationEnabled,
            config.SunShadowsEnabled,
            config.AdaptiveQualityEnabled,
            config.GpuBudgetMilliseconds.ToString("R", CultureInfo.InvariantCulture),
            config.ReflectionDistance.ToString("R", CultureInfo.InvariantCulture),
            config.VoxelBounceDistance.ToString("R", CultureInfo.InvariantCulture),
            config.VoxelBounceRayCount.ToString(CultureInfo.InvariantCulture),
            config.PointLightShadowSamples.ToString(CultureInfo.InvariantCulture),
            config.SunShadowDistance.ToString("R", CultureInfo.InvariantCulture),
            config.RayDistance.ToString("R", CultureInfo.InvariantCulture),
            config.RayCount.ToString(CultureInfo.InvariantCulture),
            config.RaySteps.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Writes the requested profile, canonical hash, and observed native tiers beside a runtime run.</summary>
    /// <param name="artifactRoot">Durable root for the completed runtime scenario.</param>
    /// <param name="scenario">Scenario defining the requested profile and benchmark mode.</param>
    /// <param name="finalConfig">Configuration archived after the game process stopped.</param>
    /// <param name="nativeCaptureTier">Tier used by the native final comparison capture.</param>
    /// <param name="benchmarkTier">Tier active when the A/B/A measurement began.</param>
    internal static void WriteRenderingProfileEvidence(
        string artifactRoot,
        ScenarioDefinition scenario,
        VintageRtxConfig? finalConfig,
        string? nativeCaptureTier,
        string? benchmarkTier)
    {
        if (!scenario.RenderProfile.HasValue)
        {
            return;
        }

        VintageRtxConfig expected = new()
        {
            SchemaVersion = VintageRtxConfig.CurrentSchemaVersion
        };
        expected.ApplyRenderProfile(scenario.RenderProfile.Value);
        string configRoot = Path.Combine(artifactRoot, "Config");
        Directory.CreateDirectory(configRoot);
        File.WriteAllText(
            Path.Combine(configRoot, "profile-evidence.json"),
            JsonConvert.SerializeObject(
                new
                {
                    RequestedProfile = scenario.RenderProfile.Value,
                    ExpectedInitialTier = scenario.RenderProfile.Value switch
                    {
                        VintageRtxRenderProfile.Performance => "performance",
                        VintageRtxRenderProfile.Balanced => "balanced",
                        _ => "high"
                    },
                    NativeFinalCaptureTier = nativeCaptureTier,
                    BenchmarkTier = benchmarkTier,
                    SeededConfiguration = "Seeded/vintagertx.json",
                    FinalConfiguration = "Final/vintagertx.json",
                    ExpectedFingerprint = CreateRenderProfileFingerprint(expected),
                    FinalFingerprint = finalConfig is null
                        ? null
                        : CreateRenderProfileFingerprint(finalConfig),
                    CanonicalConfigurationValid = ValidateArchivedRenderProfile(
                        finalConfig,
                        scenario.RenderProfile.Value).Count == 0
                },
                Formatting.Indented));
    }

    /// <summary>
    /// Resolves the parent directory passed to Vintage Story's add-mod-path
    /// option and verifies that it contains a complete unpacked VintageRTX mod.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root used for the Release fallback.</param>
    /// <returns>The absolute parent directory containing the <c>vintagertx</c> mod folder.</returns>
    /// <exception cref="DirectoryNotFoundException">The configured or fallback directory does not contain the required mod files.</exception>
    private static string ResolveModPath(string repositoryRoot)
    {
        string? configured = Environment.GetEnvironmentVariable("VINTAGERTX_TEST_MOD_PATH");
#if DEBUG
        const string BuildConfiguration = "Debug";
#else
        const string BuildConfiguration = "Release";
#endif
        string projectOutputCandidate = Path.Combine(
            repositoryRoot,
            "src",
            "VintageRTX",
            "bin",
            BuildConfiguration,
            "Mods");
        // The project output is the only implicit source of truth. An older
        // copied fixture under tests/artifacts can remain structurally valid
        // while silently running stale renderer code against newer validators.
        // A deliberately isolated package is still supported through the
        // explicit VINTAGERTX_TEST_MOD_PATH override.
        string candidate = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : projectOutputCandidate;

        string directManifest = Path.Combine(candidate, "modinfo.json");
        string directAssembly = Path.Combine(candidate, "VintageRTX.dll");
        if (string.Equals(Path.GetFileName(candidate), "vintagertx", StringComparison.OrdinalIgnoreCase)
            && File.Exists(directManifest)
            && File.Exists(directAssembly))
        {
            candidate = Directory.GetParent(candidate)?.FullName
                ?? throw new DirectoryNotFoundException(
                    $"VintageRTX mod directory '{candidate}' has no parent directory.");
        }

        candidate = Path.GetFullPath(candidate);
        string modDirectory = Path.Combine(candidate, "vintagertx");
        string manifest = Path.Combine(modDirectory, "modinfo.json");
        string assembly = Path.Combine(modDirectory, "VintageRTX.dll");
        if (!File.Exists(manifest) || !File.Exists(assembly))
        {
            throw new DirectoryNotFoundException(
                $"VintageRTX test mod path '{candidate}' must contain "
                + "'vintagertx/modinfo.json' and 'vintagertx/VintageRTX.dll'.");
        }

        return candidate;
    }

    /// <summary>
    /// Resolves the parent directory containing the server-only runtime support
    /// mod. Keeping it separate preserves VintageRTX's shipping client-only
    /// manifest while allowing an integrated single-player server to spawn
    /// authoritative item entities during automated tests.
    /// </summary>
    /// <param name="modBuildPath">Validated parent directory already passed once to <c>--addModPath</c>.</param>
    /// <returns>The absolute parent directory accepted by <c>--addModPath</c>.</returns>
    /// <exception cref="DirectoryNotFoundException">The support mod was not built for the current configuration.</exception>
    private static string ResolveRuntimeTestModPath(string modBuildPath)
    {
        string parent = Path.GetFullPath(modBuildPath);
        string modDirectory = Path.Combine(parent, "vintagertxruntimetest");
        string manifest = Path.Combine(modDirectory, "modinfo.json");
        string assembly = Path.Combine(modDirectory, "VintageRTX.RuntimeTestSupport.dll");
        if (!File.Exists(manifest) || !File.Exists(assembly))
        {
            throw new DirectoryNotFoundException(
                $"VintageRTX runtime-test mod path '{parent}' must contain "
                + "'vintagertxruntimetest/modinfo.json' and "
                + "'vintagertxruntimetest/VintageRTX.RuntimeTestSupport.dll'.");
        }

        return parent;
    }

    /// <summary>
    /// Reads shared Text Async from isolated test input and rejects malformed state at the fixture boundary.
    /// </summary>
    /// <param name="path">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="cancellationToken">The cancellation Token input used to configure this deterministic test path.</param>
    /// <returns>A task that completes when the isolated test operation and its cleanup finish.</returns>
    private static async Task<string> ReadSharedTextAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the client and integrated-server logs through their shared-write handles, then merges
    /// them into the chronological stream consumed by completion and cross-side validation.
    /// </summary>
    /// <param name="clientPath">Isolated client-main log path.</param>
    /// <param name="serverPath">Isolated server-main log path.</param>
    /// <param name="cancellationToken">Cancellation propagated to both asynchronous reads.</param>
    /// <returns>Chronologically merged client/server log text.</returns>
    private static async Task<string> ReadCombinedRuntimeLogAsync(
        string clientPath,
        string serverPath,
        CancellationToken cancellationToken)
    {
        string client = File.Exists(clientPath)
            ? await ReadSharedTextAsync(clientPath, cancellationToken)
            : string.Empty;
        string server = File.Exists(serverPath)
            ? await ReadSharedTextAsync(serverPath, cancellationToken)
            : string.Empty;
        return MergeRuntimeLogs(client, server);
    }

    /// <summary>
    /// Stably merges Vintage Story client and integrated-server lines by their second-resolution
    /// timestamp. A server stability checkpoint precedes a same-second client request; that request
    /// then precedes its server mutation and same-second client observation. This preserves the
    /// scenario's checkpoint-to-request-to-spawn-to-impact causality despite the log format exposing
    /// timestamps at only one-second resolution.
    /// </summary>
    /// <param name="clientLog">Complete or partial client-main text.</param>
    /// <param name="serverLog">Complete or partial server-main text.</param>
    /// <returns>Merged lines with one platform newline separator.</returns>
    internal static string MergeRuntimeLogs(string clientLog, string serverLog)
    {
        List<(DateTime Timestamp, int Priority, int Sequence, string Line)> lines = [];
        int sequence = 0;
        foreach ((string content, bool server) in new[]
        {
            (clientLog, false),
            (serverLog, true)
        })
        {
            foreach (string line in content.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                int timestampEnd = line.IndexOf(" [", StringComparison.Ordinal);
                DateTime timestamp = timestampEnd > 0
                    && DateTime.TryParseExact(
                        line.AsSpan(0, timestampEnd),
                        "d.M.yyyy HH:mm:ss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime parsed)
                            ? parsed
                            : DateTime.MaxValue;
                int priority = line.Contains(
                        "Server reflection witnesses stable:",
                        StringComparison.OrdinalIgnoreCase)
                    ? -1
                    : line.Contains(" requested:", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : server ? 1 : 2;
                lines.Add((timestamp, priority, sequence++, line));
            }
        }

        return string.Join(
            Environment.NewLine,
            lines.OrderBy(static entry => entry.Timestamp)
                .ThenBy(static entry => entry.Priority)
                .ThenBy(static entry => entry.Sequence)
                .Select(static entry => entry.Line));
    }

    /// <summary>
    /// Executes the capture Process Stream Async step used by the deterministic runtime Harness fixture.
    /// </summary>
    /// <param name="reader">The reader input used to configure this deterministic test path.</param>
    /// <param name="destination">The destination input used to configure this deterministic test path.</param>
    /// <returns>A task that completes when the isolated test operation and its cleanup finish.</returns>
    private static async Task CaptureProcessStreamAsync(StreamReader reader, string destination)
    {
        await using FileStream stream = new(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using StreamWriter writer = new(stream) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
        }
    }
}
