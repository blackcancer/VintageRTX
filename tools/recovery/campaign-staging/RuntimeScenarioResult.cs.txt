using System.Security.Cryptography;
using Newtonsoft.Json;

namespace VintageRTX.Test;

/// <summary>Immutable per-run result shared by the CLI, Test Explorer and durable JSON artifact.</summary>
/// <param name="ScenarioName">Exact catalogue name.</param>
/// <param name="ArtifactDirectory">Durable directory, or empty when execution could not start.</param>
/// <param name="TerminationReason">Why execution stopped, independently from the validation verdict.</param>
/// <param name="ExitCode">Existing CLI convention: zero succeeds; one fails; two is unsupported.</param>
/// <param name="Failures">All unchanged validator messages in their original order.</param>
internal sealed record RuntimeScenarioResult(string ScenarioName, string ArtifactDirectory,
    string TerminationReason, int ExitCode, IReadOnlyList<string> Failures)
{
    /// <summary>Includes actionable causes in MSTest's assertion instead of a generic integer mismatch.</summary>
    /// <returns>Scenario, termination, original failures and precise artifact location.</returns>
    internal string FormatFailure() => $"Real-case VintageRTX scenario '{ScenarioName}': {TerminationReason}; exit={ExitCode}."
        + Environment.NewLine + string.Join(Environment.NewLine, Failures.Select(static value => "FAIL " + value))
        + Environment.NewLine + "Artifacts: " + ArtifactDirectory;

    /// <summary>Persists the exact verdict after validation, without publishing user logs or credentials.</summary>
    internal void Write()
    {
        if (string.IsNullOrWhiteSpace(ArtifactDirectory)) return;
        Directory.CreateDirectory(ArtifactDirectory);
        File.WriteAllText(Path.Combine(ArtifactDirectory, "runtime-result.json"),
            JsonConvert.SerializeObject(this, Formatting.Indented));
        File.WriteAllText(Path.Combine(ArtifactDirectory, "runtime-result.txt"), FormatFailure());
    }

    /// <summary>Fingerprints the selected build before launch; this is not a loaded-module attestation.</summary>
    /// <param name="artifactRoot">Current run's durable directory.</param>
    /// <param name="modSearchPath">Validated Mods search root supplied to the game.</param>
    internal static void WriteInputIdentity(string artifactRoot, string modSearchPath)
    {
        string[] files = ["VintageRTX.dll", "modinfo.json", "assets/vintagertx/shaders/display.frag",
            "assets/game/shaders/chunkopaque.fsh", "assets/game/shaders/entityanimated.fsh"];
        Dictionary<string, string?> fingerprints = new(StringComparer.Ordinal);
        foreach (string relative in files)
        {
            string path = Path.Combine(modSearchPath, "vintagertx", relative.Replace('/', Path.DirectorySeparatorChar));
            using FileStream? input = File.Exists(path) ? File.OpenRead(path) : null;
            fingerprints[relative] = input is null ? null : Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
        Directory.CreateDirectory(artifactRoot);
        File.WriteAllText(Path.Combine(artifactRoot, "runtime-inputs.json"), JsonConvert.SerializeObject(new
        {
            schemaVersion = 1,
            identityKind = "selected-build-files-before-launch-not-loaded-module-proof",
            files = fingerprints
        }, Formatting.Indented));
    }
}
