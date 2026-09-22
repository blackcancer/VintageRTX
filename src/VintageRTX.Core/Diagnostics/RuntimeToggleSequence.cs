namespace VintageRTX.Core.Diagnostics;

/// <summary>State machine used inside a running world; advancing it is not in-game acceptance.</summary>
public enum RuntimeTogglePhase { Warmup, NativeBefore, Enabled, Coverage, NativeAfter, Complete }
public readonly record struct RuntimeToggleStep(int? SetMode, string? Capture, bool Complete);

/// <summary>
/// Bounded A/B/A smoke campaign. Modes change after a finished frame; a newly requested mode must
/// be seen on subsequent frames before capture. CPU tests cannot manufacture the GPU observations.
/// </summary>
public sealed class RuntimeToggleSequence
{
    private readonly double started, settleSeconds, timeoutSeconds;
    private readonly int minimumFrames;
    private double phaseStarted, previous;
    private int readyFrames;
    public RuntimeTogglePhase Phase { get; private set; }
    public bool Complete => Phase == RuntimeTogglePhase.Complete;
    public RuntimeToggleSequence(double now, double settleSeconds = .75, int minimumFrames = 8, double timeoutSeconds = 90)
    {
        if (!double.IsFinite(now) || !double.IsFinite(settleSeconds) || settleSeconds < 0
            || minimumFrames < 1 || !double.IsFinite(timeoutSeconds) || timeoutSeconds <= settleSeconds)
            throw new ArgumentOutOfRangeException(nameof(now));
        started = previous = phaseStarted = now;
        this.settleSeconds = settleSeconds; this.minimumFrames = minimumFrames; this.timeoutSeconds = timeoutSeconds;
    }
    public RuntimeToggleStep Advance(double now, bool actualModeReady)
    {
        if (!double.IsFinite(now) || now < previous) throw new ArgumentOutOfRangeException(nameof(now));
        previous = now;
        if (Complete) return new(null, null, true);
        if (now - started > timeoutSeconds) throw new TimeoutException($"In-game campaign exceeded its {timeoutSeconds}-second budget; inspect readiness in runtime-result.json.");
        if (!actualModeReady) { readyFrames = 0; phaseStarted = now; return default; }
        readyFrames++;
        // Warmup also leaves time to close the command input; no pause/input automation is used.
        double wait = Phase == RuntimeTogglePhase.Warmup ? Math.Max(2, settleSeconds) : settleSeconds;
        if (readyFrames < minimumFrames || now - phaseStarted < wait) return default;
        readyFrames = 0; phaseStarted = now;
        return Phase switch
        {
            RuntimeTogglePhase.Warmup => Move(RuntimeTogglePhase.NativeBefore, 0, null),
            RuntimeTogglePhase.NativeBefore => Move(RuntimeTogglePhase.Enabled, 1, "native-before"),
            RuntimeTogglePhase.Enabled => Move(RuntimeTogglePhase.Coverage, 2, "enabled"),
            RuntimeTogglePhase.Coverage => Move(RuntimeTogglePhase.NativeAfter, 0, "coverage"),
            RuntimeTogglePhase.NativeAfter => Move(RuntimeTogglePhase.Complete, null, "native-after"),
            _ => throw new InvalidOperationException("Unknown runtime phase.")
        };
    }
    private RuntimeToggleStep Move(RuntimeTogglePhase next, int? mode, string? capture)
    { Phase = next; return new(mode, capture, Complete); }
}

public sealed record RuntimeToggleVerdict(string Status, string Reason, double EffectMeanAbsoluteError,
    double NativeDriftMeanAbsoluteError, double ChangedPixelFraction);

public static class RuntimeToggleAnalysis
{
    /// <summary>
    /// Display-space A/B/A comparison, not radiometric, GI or reflection qualification. An unchanged
    /// frame is INCONCLUSIVE, never PASS. A drifting native reference invalidates attribution to RTX.
    /// </summary>
    public static RuntimeToggleVerdict Compare(ReadOnlySpan<byte> nativeBefore, ReadOnlySpan<byte> enabled,
        ReadOnlySpan<byte> nativeAfter)
    {
        if (nativeBefore.Length == 0 || nativeBefore.Length % 4 != 0 || enabled.Length != nativeBefore.Length
            || nativeAfter.Length != nativeBefore.Length) throw new ArgumentException("Expected three equally sized nonempty RGBA8 images.");
        long effect = 0, drift = 0; int changed = 0;
        for (int i = 0; i < nativeBefore.Length; i += 4)
        {
            int localEffect = 0, localDrift = 0;
            for (int c = 0; c < 3; c++)
            {
                // Compare B to the midpoint of both native samples, without integer rounding.
                effect += Math.Abs(2 * enabled[i + c] - nativeBefore[i + c] - nativeAfter[i + c]);
                int e = Math.Abs(enabled[i + c] - nativeBefore[i + c]);
                int d = Math.Abs(nativeBefore[i + c] - nativeAfter[i + c]);
                drift += d; localEffect = Math.Max(localEffect, e); localDrift = Math.Max(localDrift, d);
            }
            if (localEffect >= 4 && localEffect > 2 * localDrift) changed++;
        }
        int pixels = nativeBefore.Length / 4;
        double effectMae = effect / (pixels * 3.0 * 255 * 2), driftMae = drift / (pixels * 3.0 * 255);
        double fraction = changed / (double)pixels;
        if (driftMae > 2.0 / 255)
            return new("INCONCLUSIVE", "Native A/A reference drifted (weather, animation, camera or world changes).", effectMae, driftMae, fraction);
        if (effectMae <= Math.Max(1.0 / 1024, 2 * driftMae + 1.0 / 1024) || fraction < .005)
            return new("INCONCLUSIVE", "No attributable visible replacement; choose a stationary illuminated receiver and inspect coverage.", effectMae, driftMae, fraction);
        return new("PASS", "World toggle smoke only: visible change and return to a stable native image. Physical lighting, latency, shadows, reflections and performance are NOT qualified.", effectMae, driftMae, fraction);
    }
}
