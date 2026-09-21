using System.Numerics;

namespace VintageRTX.Core.Lighting;

/// <summary>Behavior, never inferred from RGB. EngineDriven preserves the current engine envelope.</summary>
public enum EmissionKind { Steady, Flame, Lightning, EngineDriven }

/// <summary>Art-directed temporal parameters, not claimed measurements of a real flame.</summary>
public sealed record EmissionProfile
{
    public EmissionKind Kind { get; }
    public double Amplitude { get; }
    public double FrequencyHz { get; }
    public double WindSensitivity { get; }
    public double DurationSeconds { get; }
    public EmissionProfile(EmissionKind kind, double amplitude = 0, double frequencyHz = 1,
        double windSensitivity = 0, double durationSeconds = 0.4)
    {
        if (!Enum.IsDefined(kind) || !double.IsFinite(amplitude) || amplitude < 0 || amplitude > 0.8
            || !double.IsFinite(frequencyHz) || frequencyHz <= 0 || frequencyHz > 100
            || !double.IsFinite(windSensitivity) || windSensitivity < 0 || windSensitivity > 1
            || !double.IsFinite(durationSeconds) || durationSeconds <= 0 || durationSeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(kind), "Invalid temporal emission profile.");
        Kind = kind; Amplitude = amplitude; FrequencyHz = frequencyHz;
        WindSensitivity = windSensitivity; DurationSeconds = durationSeconds;
    }
    // Standalone reference presets, not runtime family mappings. The client resolves patched assets.
    public static EmissionProfile Steady { get; } = new(EmissionKind.Steady);
    public static EmissionProfile Engine { get; } = new(EmissionKind.EngineDriven);
    public static EmissionProfile Torch { get; } = new(EmissionKind.Flame, 0.22, 7, 0.35);
    public static EmissionProfile Fire { get; } = new(EmissionKind.Flame, 0.32, 5, 0.5);
    public static EmissionProfile Lantern { get; } = new(EmissionKind.Flame, 0.07, 5, 0.04);
    public static EmissionProfile OilLamp { get; } = new(EmissionKind.Flame, 0.10, 6, 0.1);
    public static EmissionProfile Candle { get; } = new(EmissionKind.Flame, 0.12, 6, 0.2);
    public static EmissionProfile Lightning { get; } = new(EmissionKind.Lightning, durationSeconds: 0.4);
}

/// <summary>Single authority for all passes. Uses absolute seconds, not frame count or mutable RNG.</summary>
public static class EmissionWaveform
{
    public static double Evaluate(EmissionProfile profile, ulong seed, double timeSeconds,
        double birthSeconds, double wind01 = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(birthSeconds) || !double.IsFinite(wind01))
            throw new ArgumentOutOfRangeException(nameof(timeSeconds));
        double age = timeSeconds - birthSeconds;
        if (age < 0) return 0;
        if (profile.Kind is EmissionKind.Steady or EmissionKind.EngineDriven) return 1;
        if (profile.Kind == EmissionKind.Lightning)
        {
            if (age >= profile.DurationSeconds) return 0;
            // A finite, non-periodic double flash. Actual weather providers may instead supply
            // EngineDriven intensity when the engine exposes the event's exact envelope.
            double u = age / profile.DurationSeconds;
            double first = Pulse(u, 0, 0.035, 0.23);
            double second = 0.55 * Pulse(u, 0.28, 0.025, 0.48);
            return Math.Min(1, first + second);
        }
        // Smooth bounded value noise: no negative emission, no global synchronized sine.
        // Modulation changes intensity, not source position, identity, tint or topology.
        double t = age * profile.FrequencyHz;
        if (Math.Abs(t) > 1e12) throw new ArgumentOutOfRangeException(nameof(timeSeconds));
        double signal = 0.55 * Noise(seed, t) + 0.3 * Noise(seed ^ 0xa0761d6478bd642fUL, t * 0.37)
            + 0.15 * Noise(seed ^ 0xe7037ed1a0b428dbUL, t * 2.13);
        double amplitude = Math.Min(0.8, profile.Amplitude * (1 + profile.WindSensitivity * Math.Clamp(wind01, 0, 1)));
        return 1 + amplitude * signal;
    }
    private static double Pulse(double t, double start, double attack, double decay)
    {
        double x = t - start;
        if (x <= 0 || x >= attack + decay) return 0;
        return x < attack ? Smooth(x / attack) : 1 - Smooth((x - attack) / decay);
    }
    private static double Smooth(double x) => x * x * x * (x * (x * 6 - 15) + 10);
    private static double Noise(ulong seed, double t)
    {
        long cell = (long)Math.Floor(t);
        double f = Smooth(t - cell);
        double a = Unit(Mix(seed ^ unchecked((ulong)cell)));
        double b = Unit(Mix(seed ^ unchecked((ulong)(cell + 1))));
        return (a + (b - a) * f) * 2 - 1;
    }
    public static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9e3779b97f4a7c15UL;
            x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
            x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
            return x ^ (x >> 31);
        }
    }
    public static double Unit(ulong bits) => (bits >> 11) * (1.0 / 9007199254740992.0);
}

/// <summary>Explicit catalog lookup. There are no longer any hardcoded game-family rules in C#.</summary>
public static class EmissionProfiles
{
    public static EmissionProfile ForCode(EmissionCatalog catalog, string code, bool intrinsicEntity = false) =>
        catalog.Resolve(code, intrinsicEntity ? EmissionTarget.Entity : EmissionTarget.Block).Profile;
}

/// <summary>Scene-linear RGB. Non-color maps must not go through this transfer.</summary>
public static class ColorSpace
{
    public static float Decode(float x) => x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    public static float Encode(float x) => x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - 0.055f;
    public static Vector3 Decode(Vector3 rgb) => new(Decode(rgb.X), Decode(rgb.Y), Decode(rgb.Z));
}
