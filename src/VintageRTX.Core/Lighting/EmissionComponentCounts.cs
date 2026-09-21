using System.Text.Json;

namespace VintageRTX.Core.Lighting;

/// <summary>Asset-defined component counts matched against the current complete collectible code.</summary>
internal sealed class EmissionComponentCounts
{
    private readonly (string Pattern, int Count)[] values;
    private readonly string path;
    private EmissionComponentCounts((string Pattern, int Count)[] values, string path)
    { this.values = values; this.path = path; }

    internal static EmissionComponentCounts? Parse(Dictionary<string, JsonElement> fields, string path)
    {
        if (!fields.TryGetValue("componentCounts", out JsonElement element)) return null;
        path += "/componentCounts";
        var entries = EmissionJson.Fields(element, path);
        if (entries.Count > 128) throw EmissionJson.Error(path, "at most 128 count patterns");
        var values = new List<(string, int)>();
        foreach ((string pattern, JsonElement number) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            EmissionJson.ValidateName(pattern, path, wildcard: true);
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out int count) || count is < 0 or > 64)
                throw EmissionJson.Error(path + "/" + pattern, "expected integer component count in [0,64]");
            values.Add((pattern, count));
        }
        return new(values.ToArray(), path);
    }

    internal int Resolve(string code)
    {
        (int Exact, int Literals) best = (-1, -1);
        int result = 1; bool conflict = false;
        foreach ((string pattern, int count) in values)
        {
            if (!EmissionCodePattern.Matches(pattern, code)) continue;
            var rank = (pattern.Contains('*') ? 0 : 1, pattern.Count(c => c != '*'));
            int comparison = rank.CompareTo(best);
            if (comparison > 0) { best = rank; result = count; conflict = false; }
            else if (comparison == 0 && count != result) conflict = true;
        }
        if (conflict) throw EmissionJson.Error(path, "ambiguous component counts for " + code);
        return result;
    }
}

internal static class EmissionCodePattern
{
    internal static bool Matches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, retry = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == text[t]) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = t; }
            else if (star >= 0) { p = star + 1; t = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>
/// The provider supplies total group intensity. Average independent component modulations so adding
/// a candle does not multiply that total a second time. This does not position individual wick geometry.
/// </summary>
public static class EmissionGroupWaveform
{
    public static ulong ComponentSeed(ulong ownerSeed, int component)
    {
        if (component is < 0 or >= 64) throw new ArgumentOutOfRangeException(nameof(component));
        return component == 0 ? ownerSeed : EmissionWaveform.Mix(ownerSeed ^ EmissionWaveform.Mix((ulong)component));
    }

    public static double Evaluate(EmissionProfile profile, ulong seed, double seconds, double birthSeconds,
        int componentCount, double wind01 = 0)
    {
        if (componentCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(componentCount));
        double first = EmissionWaveform.Evaluate(profile, seed, seconds, birthSeconds, wind01);
        if (componentCount == 1 || profile.Kind != EmissionKind.Flame) return first;
        double sum = first;
        for (int component = 1; component < componentCount; component++)
            sum += EmissionWaveform.Evaluate(profile, ComponentSeed(seed, component), seconds, birthSeconds, wind01);
        return sum / componentCount;
    }
}
