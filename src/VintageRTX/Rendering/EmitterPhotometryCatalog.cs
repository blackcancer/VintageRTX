using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Rendering;

/// <summary>
/// Reconstructs client emitter profiles from the same JSON patch applied to server block
/// definitions. Vintage Story 1.22 does not mirror arbitrary patched block attributes to the
/// client, so code-root bindings live beside each physical profile in that patch asset.
/// </summary>
internal static class EmitterPhotometryCatalog
{
    private static readonly AssetLocation PatchLocation =
        new("vintagertx", "patches/emitter-photometry.json");

    /// <summary>Loads the emitter patch and returns validated profiles keyed by client code root.</summary>
    /// <param name="assets">Active client asset manager.</param>
    /// <param name="logger">Optional runtime diagnostic destination.</param>
    /// <returns>Immutable-by-contract mapping of canonical code roots to attribute objects.</returns>
    public static IReadOnlyDictionary<string, JObject> Load(
        IAssetManager assets,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(assets);

        try
        {
            IAsset patch = assets.TryGet(PatchLocation)
                ?? throw new FileNotFoundException(
                    $"Required emitter asset '{PatchLocation}' was not loaded.");
            IReadOnlyDictionary<string, JObject> result = Parse(patch.Data);
            logger?.Notification(
                "[VintageRTX] Client emitter patch bridge: code roots={0}; SI photometry read from server patch asset.",
                result.Count);
            return result;
        }
        catch (Exception exception)
        {
            logger?.Warning(
                "[VintageRTX] Client emitter patch bridge unavailable; local lights use game-range inverse-square fallbacks: {0}",
                exception.Message);
            return new Dictionary<string, JObject>(StringComparer.Ordinal);
        }
    }

    /// <summary>Parses and validates code-root bindings from a strict emitter patch document.</summary>
    /// <param name="patchData">UTF-8 JSON patch bytes.</param>
    /// <returns>Physical attribute objects keyed by normalized collectible-code root.</returns>
    internal static IReadOnlyDictionary<string, JObject> Parse(byte[] patchData)
    {
        ArgumentNullException.ThrowIfNull(patchData);
        JArray operations = JArray.Parse(Encoding.UTF8.GetString(patchData));
        Dictionary<string, JObject> result = new(StringComparer.Ordinal);
        foreach (JObject operation in operations.OfType<JObject>())
        {
            if (operation["value"]?[EmitterPhotometry.AttributeName] is not JObject value
                || value["clientCodeRoots"] is not JArray codeRoots
                || codeRoots.Count == 0)
            {
                throw new InvalidDataException(
                    "Every emitter patch must contain a physical profile and at least one client code root.");
            }

            int referenceLightValue = value.Value<int?>("referenceLightHsvValue") ?? 0;
            JObject attributes = new()
            {
                [EmitterPhotometry.AttributeName] = value.DeepClone(),
            };
            if (!EmitterPhotometry.TryRead(
                    new JsonObject(attributes),
                    referenceLightValue,
                    out _))
            {
                throw new InvalidDataException(
                    $"Emitter patch '{operation.Value<string>("file")}' has invalid SI photometry.");
            }

            foreach (JToken token in codeRoots)
            {
                string codeRoot = token.Value<string>()?.Trim().ToLowerInvariant() ?? string.Empty;
                if (codeRoot.Length == 0 || !codeRoot.Contains(':', StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Emitter client code roots must be canonical domain:path values.");
                }
                if (!result.TryAdd(codeRoot, (JObject)value.DeepClone()))
                {
                    throw new InvalidDataException(
                        $"Emitter client code root '{codeRoot}' is bound more than once.");
                }
            }
        }

        return result;
    }

    /// <summary>Resolves exact and hyphenated block variants to one scaled physical profile.</summary>
    /// <param name="code">Loaded block asset code.</param>
    /// <param name="actualLightValue">Runtime V component returned by <c>GetLightHsv</c>.</param>
    /// <param name="profilesByCodeRoot">Validated profiles returned by <see cref="Load"/>.</param>
    /// <param name="photometry">Resolved profile scaled to the current block light value.</param>
    /// <returns>Whether a valid exact or variant-root profile was resolved.</returns>
    public static bool TryResolve(
        AssetLocation? code,
        int actualLightValue,
        IReadOnlyDictionary<string, JObject> profilesByCodeRoot,
        out EmitterPhotometry photometry)
    {
        ArgumentNullException.ThrowIfNull(profilesByCodeRoot);
        photometry = default;
        if (code is null)
        {
            return false;
        }

        string canonicalCode = code.ToString();
        JObject? profile = null;
        if (!profilesByCodeRoot.TryGetValue(canonicalCode, out profile))
        {
            foreach ((string codeRoot, JObject candidate) in profilesByCodeRoot)
            {
                if (canonicalCode.Length > codeRoot.Length
                    && canonicalCode.StartsWith(codeRoot, StringComparison.Ordinal)
                    && canonicalCode[codeRoot.Length] == '-')
                {
                    profile = candidate;
                    break;
                }
            }
        }

        if (profile is null)
        {
            return false;
        }

        JObject attributes = new()
        {
            [EmitterPhotometry.AttributeName] = profile.DeepClone(),
        };
        return EmitterPhotometry.TryRead(
            new JsonObject(attributes),
            actualLightValue,
            out photometry);
    }
}
