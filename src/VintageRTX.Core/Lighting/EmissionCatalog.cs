using System.Collections.Frozen;
using System.Numerics;
using System.Text.Json;
using VintageRTX.Core.Geometry;

namespace VintageRTX.Core.Lighting;

public enum EmissionTarget { Block, Item, Entity, Weather }
public sealed class EmissionConfigurationException(string message) : FormatException(message);

/// <summary>Type attributes override only their named fields, not the provider's runtime on/off state.</summary>
public sealed record EmissionOverride(string? Profile = null, bool? Enabled = null, double? IntensityScale = null,
    double? SourceRadius = null)
{
    public static EmissionOverride Parse(string json, string source = "attributes/vintageRtxEmission")
    {
        using JsonDocument document = EmissionJson.Read(json, source);
        var fields = EmissionJson.Fields(document.RootElement, source, "profile", "enabled", "intensityScale", "sourceRadius");
        string? profile = fields.ContainsKey("profile") ? EmissionJson.Name(fields["profile"], source + "/profile") : null;
        bool? enabled = fields.ContainsKey("enabled") ? EmissionJson.Boolean(fields, "enabled", true, source) : null;
        double? scale = fields.ContainsKey("intensityScale") ? EmissionJson.Number(fields, "intensityScale", 1, 0, 100, source) : null;
        double? radius = fields.ContainsKey("sourceRadius") ? EmissionJson.Number(fields, "sourceRadius", 0, 0, 16, source) : null;
        return new(profile, enabled, scale, radius);
    }
}

/// <summary>
/// Resolved source parameters. Radius is equivalent spherical radius in block units, not range.
/// ComponentCount modulates a total aggregate intensity; it never multiplies the provider's energy.
/// </summary>
public sealed record EmissionSelection(string? ProfileId, string? BindingId, EmissionProfile Profile,
    bool Enabled = true, double IntensityScale = 1, double SourceRadius = 0, int ComponentCount = 1)
{
    public LightDefinition? CreateLight(DVec3 position, Vector3 runtimeIntensity, double birthSeconds = 0, double? radius = null)
    {
        if (!LightDefinition.FiniteNonnegative(runtimeIntensity)) throw new ArgumentOutOfRangeException(nameof(runtimeIntensity));
        if (!double.IsFinite(IntensityScale) || IntensityScale < 0 || IntensityScale > 100)
            throw new ArgumentOutOfRangeException(nameof(IntensityScale));
        if (!double.IsFinite(SourceRadius) || SourceRadius < 0 || SourceRadius > 16)
            throw new ArgumentOutOfRangeException(nameof(SourceRadius));
        if (ComponentCount is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(ComponentCount));
        if (!Enabled || runtimeIntensity == Vector3.Zero || IntensityScale == 0 || ComponentCount == 0) return null;
        return new(position, runtimeIntensity * (float)IntensityScale, Profile, birthSeconds,
            radius ?? SourceRadius, ComponentCount);
    }
}

/// <summary>
/// Immutable configuration after the native JSON patcher. No heuristic based on RGB or item stack size.
/// A component-count lookup sees the CURRENT runtime code on every source observation.
/// </summary>
public sealed class EmissionCatalog
{
    private sealed record Binding(string Id, string[] Codes, EmissionTarget[] Targets,
        int Priority, EmissionSelection Selection, EmissionComponentCounts? Counts);
    private readonly FrozenDictionary<string, EmissionProfile> profiles;
    private readonly Binding[] bindings;
    public string Source { get; }
    public int ProfileCount => profiles.Count;
    public int BindingCount => bindings.Length;
    public static EmissionCatalog Empty { get; } = new("unconfigured", new(StringComparer.Ordinal), []);

    private EmissionCatalog(string source, Dictionary<string, EmissionProfile> profiles, Binding[] bindings)
    { Source = source; this.profiles = profiles.ToFrozenDictionary(StringComparer.Ordinal); this.bindings = bindings; }

    public static EmissionCatalog Parse(string json, string source = "vintagertx:config/emission.json")
    {
        using JsonDocument document = EmissionJson.Read(json, source);
        var root = EmissionJson.Fields(document.RootElement, source, "schemaVersion", "profiles", "bindings");
        if (EmissionJson.Integer(root, "schemaVersion", -1, source) != 1)
            throw EmissionJson.Error(source + "/schemaVersion", "expected schemaVersion 1");
        var profileElements = EmissionJson.Fields(EmissionJson.Required(root, "profiles", source), source + "/profiles");
        var bindingElements = EmissionJson.Fields(EmissionJson.Required(root, "bindings", source), source + "/bindings");
        if (profileElements.Count > 2048 || bindingElements.Count > 8192)
            throw EmissionJson.Error(source, "catalog exceeds 2048 profiles or 8192 bindings");
        var profiles = new Dictionary<string, EmissionProfile>(StringComparer.Ordinal);
        foreach ((string id, JsonElement element) in profileElements)
        {
            string path = source + "/profiles/" + id;
            EmissionJson.ValidateName(id, path);
            var fields = EmissionJson.Fields(element, path, "kind", "amplitude", "frequencyHz", "windSensitivity", "durationSeconds");
            string kindName = EmissionJson.Text(EmissionJson.Required(fields, "kind", path), path + "/kind");
            EmissionKind kind = kindName switch
            {
                "steady" => EmissionKind.Steady, "flame" => EmissionKind.Flame,
                "engineDriven" => EmissionKind.EngineDriven, "lightning" => EmissionKind.Lightning,
                _ => throw EmissionJson.Error(path + "/kind", "expected steady, flame, engineDriven or lightning")
            };
            profiles.Add(id, new(kind,
                EmissionJson.Number(fields, "amplitude", 0, 0, .8, path),
                EmissionJson.Number(fields, "frequencyHz", 1, .000001, 100, path),
                EmissionJson.Number(fields, "windSensitivity", 0, 0, 1, path),
                EmissionJson.Number(fields, "durationSeconds", .4, .000001, 60, path)));
        }
        var bindings = new List<Binding>();
        foreach ((string id, JsonElement element) in bindingElements.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string path = source + "/bindings/" + id;
            EmissionJson.ValidateName(id, path);
            var fields = EmissionJson.Fields(element, path, "codes", "targets", "profile", "priority", "enabled",
                "intensityScale", "sourceRadius", "componentCounts");
            string profileId = EmissionJson.Name(EmissionJson.Required(fields, "profile", path), path + "/profile");
            if (!profiles.TryGetValue(profileId, out EmissionProfile? profile))
                throw EmissionJson.Error(path + "/profile", "unknown profile '" + profileId + "'");
            string[] codes = EmissionJson.StringArray(EmissionJson.Required(fields, "codes", path), path + "/codes");
            foreach (string code in codes) EmissionJson.ValidateName(code, path + "/codes", wildcard: true);
            EmissionTarget[] targets = EmissionJson.StringArray(EmissionJson.Required(fields, "targets", path), path + "/targets")
                .Select(value => value switch
                {
                    "block" => EmissionTarget.Block, "item" => EmissionTarget.Item,
                    "entity" => EmissionTarget.Entity, "weather" => EmissionTarget.Weather,
                    _ => throw EmissionJson.Error(path + "/targets", "unknown target '" + value + "'")
                }).ToArray();
            int priority = EmissionJson.Integer(fields, "priority", 0, path);
            if (priority is < -10000 or > 10000) throw EmissionJson.Error(path + "/priority", "outside [-10000,10000]");
            ValidateEventTarget(profile, targets, path);
            bindings.Add(new(id, codes, targets, priority, new(profileId, id, profile,
                EmissionJson.Boolean(fields, "enabled", true, path),
                EmissionJson.Number(fields, "intensityScale", 1, 0, 100, path),
                EmissionJson.Number(fields, "sourceRadius", 0, 0, 16, path)),
                EmissionComponentCounts.Parse(fields, path)));
        }
        return new(source, profiles, bindings.ToArray());
    }

    public EmissionProfile GetProfile(string id) => profiles.TryGetValue(id, out EmissionProfile? profile)
        ? profile : throw EmissionJson.Error(Source, "unknown profile '" + id + "'");

    /// <summary>
    /// Highest priority, exact match, then literal specificity. Override every conflicting field:
    /// an explicit profile must not silently select a different intensity, radius or candle count.
    /// </summary>
    public EmissionSelection Resolve(string code, EmissionTarget target, EmissionOverride? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        EmissionSelection fallback = new(null, null,
            target is EmissionTarget.Entity or EmissionTarget.Weather ? EmissionProfile.Engine : EmissionProfile.Steady);
        EmissionSelection? winner = null;
        (int Priority, int Exact, int Literals) winningRank = (int.MinValue, 0, 0);
        bool profileConflict = false, enabledConflict = false, scaleConflict = false, radiusConflict = false, countConflict = false;
        foreach (Binding binding in bindings)
        {
            if (!binding.Targets.Contains(target)) continue;
            (int Exact, int Literals) specificity = (-1, -1);
            foreach (string pattern in binding.Codes)
            {
                if (!EmissionCodePattern.Matches(pattern, code)) continue;
                var rank = (pattern.Contains('*') ? 0 : 1, pattern.Count(character => character != '*'));
                if (rank.CompareTo(specificity) > 0) specificity = rank;
            }
            if (specificity.Exact < 0) continue;
            var candidateRank = (binding.Priority, specificity.Exact, specificity.Literals);
            int comparison = candidateRank.CompareTo(winningRank);
            if (comparison < 0) continue;
            EmissionSelection candidate = binding.Selection with { ComponentCount = binding.Counts?.Resolve(code) ?? 1 };
            if (comparison > 0)
            {
                winner = candidate; winningRank = candidateRank;
                profileConflict = enabledConflict = scaleConflict = radiusConflict = countConflict = false;
            }
            else if (winner is not null)
            {
                profileConflict |= winner.Profile != candidate.Profile;
                enabledConflict |= winner.Enabled != candidate.Enabled;
                scaleConflict |= winner.IntensityScale != candidate.IntensityScale;
                radiusConflict |= winner.SourceRadius != candidate.SourceRadius;
                countConflict |= winner.ComponentCount != candidate.ComponentCount;
            }
        }
        if ((profileConflict && attributes?.Profile is null) || (enabledConflict && attributes?.Enabled is null)
            || (scaleConflict && attributes?.IntensityScale is null) || (radiusConflict && attributes?.SourceRadius is null) || countConflict)
            throw EmissionJson.Error(Source, "ambiguous bindings for " + target + " " + code
                + "; set distinct priorities or override every conflicting field");
        EmissionSelection result = winner ?? fallback;
        if (attributes is not null)
        {
            EmissionProfile profile = attributes.Profile is null ? result.Profile : GetProfile(attributes.Profile);
            double scale = attributes.IntensityScale ?? result.IntensityScale;
            double radius = attributes.SourceRadius ?? result.SourceRadius;
            if (!double.IsFinite(scale) || scale < 0 || scale > 100)
                throw EmissionJson.Error(Source, "invalid attribute intensityScale");
            if (!double.IsFinite(radius) || radius < 0 || radius > 16)
                throw EmissionJson.Error(Source, "invalid attribute sourceRadius");
            result = result with { ProfileId = attributes.Profile ?? result.ProfileId, Profile = profile,
                Enabled = attributes.Enabled ?? result.Enabled, IntensityScale = scale, SourceRadius = radius };
            ValidateEventTarget(profile, [target], Source + "/attributes/vintageRtxEmission");
        }
        return result;
    }

    private static void ValidateEventTarget(EmissionProfile profile, EmissionTarget[] targets, string path)
    {
        if (profile.Kind == EmissionKind.Lightning && targets.Any(target => target != EmissionTarget.Weather))
            throw EmissionJson.Error(path, "lightning requires a weather event with an explicit birth time, not a persistent block/item/entity");
    }
}

/// <summary>A failed asset reload retains the previous complete catalog and its revision.</summary>
public sealed class EmissionCatalogState
{
    public EmissionCatalog Current { get; private set; } = EmissionCatalog.Empty;
    public long Revision { get; private set; }
    public string? LastError { get; private set; }
    private string? previousJson;
    public bool TryReplace(string json, string source)
    {
        try
        {
            if (json == previousJson) { LastError = null; return true; }
            EmissionCatalog candidate = EmissionCatalog.Parse(json, source);
            Current = candidate; previousJson = json; Revision++; LastError = null;
            return true;
        }
        catch (FormatException exception) { LastError = exception.Message; return false; }
    }
}

internal static class EmissionJson
{
    internal static EmissionConfigurationException Error(string path, string detail) => new(path + ": " + detail);
    internal static JsonDocument Read(string json, string source)
    {
        if (json is null || json.Length > 2 * 1024 * 1024) throw Error(source, "missing or oversized JSON");
        try { return JsonDocument.Parse(json, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 24 }); }
        catch (JsonException exception) { throw Error(source, exception.Message); }
    }
    internal static Dictionary<string, JsonElement> Fields(JsonElement value, string path, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error(path, "expected object");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty field in value.EnumerateObject())
        {
            if (!result.TryAdd(field.Name, field.Value)) throw Error(path + "/" + field.Name, "duplicate field");
            if (allowed.Length != 0 && !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw Error(path + "/" + field.Name, "unknown field");
        }
        return result;
    }
    internal static JsonElement Required(Dictionary<string, JsonElement> fields, string name, string path) =>
        fields.TryGetValue(name, out JsonElement element) ? element : throw Error(path + "/" + name, "required field");
    internal static string Text(JsonElement element, string path) => element.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()! : throw Error(path, "expected nonempty string");
    internal static string Name(JsonElement element, string path)
    { string value = Text(element, path); ValidateName(value, path); return value; }
    internal static void ValidateName(string value, string path, bool wildcard = false)
    {
        int colon = value.IndexOf(':');
        if (value.Length > 256 || colon <= 0 || colon == value.Length - 1 || value.LastIndexOf(':') != colon
            || value.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or ':' or '/' or '.' or '_' or '-'
                || wildcard && c == '*')))
            throw Error(path, "expected lowercase domain:path" + (wildcard ? " (optional '*' wildcards)" : ""));
    }
    internal static string[] StringArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 32)
            throw Error(path, "expected 1..32 strings");
        string[] result = element.EnumerateArray().Select(value => Text(value, path)).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) throw Error(path, "duplicate entries");
        return result;
    }
    internal static double Number(Dictionary<string, JsonElement> fields, string name, double fallback,
        double minimum, double maximum, string path)
    {
        if (!fields.TryGetValue(name, out JsonElement element)) return fallback;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double value)
            || !double.IsFinite(value) || value < minimum || value > maximum)
            throw Error(path + "/" + name, "expected finite number in [" + minimum + "," + maximum + "]");
        return value;
    }
    internal static int Integer(Dictionary<string, JsonElement> fields, string name, int fallback, string path)
    {
        if (!fields.TryGetValue(name, out JsonElement element)) return fallback;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value)
            ? value : throw Error(path + "/" + name, "expected integer");
    }
    internal static bool Boolean(Dictionary<string, JsonElement> fields, string name, bool fallback, string path)
    {
        if (!fields.TryGetValue(name, out JsonElement element)) return fallback;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean()
            : throw Error(path + "/" + name, "expected boolean");
    }
}
