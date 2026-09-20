using System.Text.RegularExpressions;

namespace VintageRTX.Test;

/// <summary>Execution completion is independent from acceptance; failed assertions cannot extend a finished run.</summary>
internal static class RuntimeScenarioCompletion
{
    /// <summary>Checks durable capture completion and the benchmark, not successful material/geometry assertions.</summary>
    /// <param name="log">Only the current isolated run's combined client/server log.</param>
    /// <param name="scenario">Capture and benchmark lifecycle requested for this run.</param>
    /// <returns>True only after the expected evidence producers finished.</returns>
    internal static bool HasCollectedEvidence(string log, ScenarioDefinition scenario)
    {
        if (!HasCaptureCompletion(log, scenario)
            || (scenario.RunBenchmark && !log.Contains("Stabilized A/B/A result", StringComparison.OrdinalIgnoreCase)))
            return false;
        // The water campaign has independent late physics witnesses beyond its automatic PNG sequence.
        // Retain its existing strict timeline until those producers expose their own terminal marker.
        if (string.Equals(scenario.Name, "water-reflection", StringComparison.OrdinalIgnoreCase))
            return RuntimeLogValidator.IsComplete(log, scenario);
        if (string.Equals(scenario.Name, "moving-camera", StringComparison.OrdinalIgnoreCase))
            return log.Contains("Moving-camera probe completed", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(scenario.Name, "resize-and-reload", StringComparison.OrdinalIgnoreCase))
            return log.Contains("Resize/reload verification:", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Reports the unfinished producers without falsely labeling all early exits as timeouts.</summary>
    /// <param name="log">Current runtime log.</param>
    /// <param name="scenario">Expected capture and benchmark configuration.</param>
    /// <returns>Specific missing evidence, or notice of pending independent scenario witnesses.</returns>
    internal static string DescribeMissingEvidence(string log, ScenarioDefinition scenario)
    {
        List<string> pending = [];
        if (!HasCaptureCompletion(log, scenario)) pending.Add("durable automatic capture completion");
        if (scenario.RunBenchmark && !log.Contains("Stabilized A/B/A result", StringComparison.OrdinalIgnoreCase))
            pending.Add("stabilized A/B/A result");
        return pending.Count > 0 ? string.Join("; ", pending) : "independent scenario lifecycle witnesses";
    }

    /// <summary>Matches the current profile's anchored durable marker, not a filename printed in an error.</summary>
    /// <param name="log">Current isolated run log.</param>
    /// <param name="scenario">Expected capture profile.</param>
    /// <returns>Whether a producer committed its entire automatic capture sequence.</returns>
    private static bool HasCaptureCompletion(string log, ScenarioDefinition scenario)
    {
        string profile = string.IsNullOrWhiteSpace(scenario.CaptureProfile)
            ? "default" : scenario.CaptureProfile.Trim().ToLowerInvariant();
        return Regex.IsMatch(log,
            @"(?:^|\n)[^\r\n]*\[VintageRTX\] Automatic capture sequence completed: profile="
            + Regex.Escape(profile) + @", last=[a-z0-9-]+, captures=[1-9][0-9]*\.[ \t]*(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
