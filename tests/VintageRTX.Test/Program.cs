using System.Text.RegularExpressions;

namespace VintageRTX.Test;

/// <summary>
/// Hosts the command-line test runner and maps any failed contract to a non-zero process exit code.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs every registered check and returns zero only when all test contracts pass.
    /// </summary>
    /// <param name="args">The args input used to configure this deterministic test path.</param>
    /// <returns>Zero when every registered contract passes; otherwise a non-zero process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        string command = args.Length == 0 ? "preflight" : args[0].ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "preflight":
                    return LiquidOpticsTests.Run() == 0 ? PreflightSuite.Run() : 1;
                case "liquid-optics":
                    return LiquidOpticsTests.Run();
                case "list":
                    ScenarioCatalog.Print();
                    return 0;
                case "runtime":
                    {
                        if (PreflightSuite.Run() != 0)
                        {
                            return 1;
                        }

                        string scenarioName = args.Length >= 2 ? args[1] : "reference-room";
                        ScenarioDefinition scenario = ScenarioCatalog.Get(scenarioName);
                        return await RuntimeHarness.RunAsync(scenario, CancellationToken.None);
                    }
                case "validate-log":
                    {
                        if (args.Length < 3)
                        {
                            throw new ArgumentException("validate-log requires <scenario> <client-main.log>.");
                        }

                        ScenarioDefinition scenario = ScenarioCatalog.Get(args[1]);
                        string clientLogPath = Path.GetFullPath(args[2]);
                        string serverLogPath = Path.Combine(
                            Path.GetDirectoryName(clientLogPath) ?? string.Empty,
                            "server-main.log");
                        string clientLog = await File.ReadAllTextAsync(clientLogPath);
                        string serverLog = File.Exists(serverLogPath)
                            ? await File.ReadAllTextAsync(serverLogPath)
                            : string.Empty;
                        string log = RuntimeHarness.MergeRuntimeLogs(clientLog, serverLog);
                        log = RebaseArchivedCapturePaths(log, args[2]);
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
                            failures.AddRange(
                                RuntimeImageValidator.ValidateProjectileSurfaceFieldDeltas(log));
                        }
                        foreach (string failure in failures)
                        {
                            Console.Error.WriteLine($"FAIL {failure}");
                        }
                        return failures.Count == 0 ? 0 : 1;
                    }
                case "analyze-image":
                    {
                        if (args.Length < 2)
                        {
                            throw new ArgumentException("analyze-image requires <capture.png>.");
                        }

                        RuntimeImageValidator.PrintChannelStatistics(args[1]);
                        return 0;
                    }
                case "diagnose-log":
                    {
                        if (args.Length < 2)
                        {
                            throw new ArgumentException("diagnose-log requires <client-main.log>.");
                        }

                        RuntimeImageValidator.PrintThinLeakDiagnostics(
                            await File.ReadAllTextAsync(args[1]));
                        return 0;
                    }
                case "diagnose-checkerboard":
                    {
                        if (args.Length < 3)
                        {
                            throw new ArgumentException(
                                "diagnose-checkerboard requires <before.png> <after.png>.");
                        }

                        RuntimeImageValidator.PrintCheckerboardDiagnostics(args[1], args[2]);
                        return 0;
                    }
                default:
                    Console.Error.WriteLine("Usage: VintageRTX.Test [preflight|liquid-optics|list|runtime <scenario>|validate-log <scenario> <client-main.log>|analyze-image <capture.png>|diagnose-log <client-main.log>|diagnose-checkerboard <before.png> <after.png>]");
                    return 2;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Executes the rebase Archived Capture Paths step used by the deterministic program fixture.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="logPath">Filesystem location constrained to the isolated test sandbox.</param>
    /// <returns>The rebase Archived Capture Paths result consumed by the caller&apos;s assertion.</returns>
    private static string RebaseArchivedCapturePaths(string log, string logPath)
    {
        DirectoryInfo? logDirectory = Directory.GetParent(Path.GetFullPath(logPath));
        string? artifactRoot = logDirectory?.Parent?.FullName;
        if (artifactRoot is null)
        {
            return log;
        }

        string captureDirectory = Path.Combine(artifactRoot, "Captures");
        if (!Directory.Exists(captureDirectory))
        {
            return log;
        }

        return Regex.Replace(
            log,
            @"[A-Za-z]:[\\/][^\r\n]*?[\\/]Captures[\\/](?<file>[^\s]+\.png)",
            match => Path.Combine(captureDirectory, match.Groups["file"].Value));
    }
}
