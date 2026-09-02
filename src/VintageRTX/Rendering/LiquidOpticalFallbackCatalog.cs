using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Rendering;

/// <summary>Validated client reconstruction of liquid profiles and visible-container metadata.</summary>
/// <param name="ProfilesByCodeRoot">Physical liquid profiles keyed by collectible-code root.</param>
/// <param name="ContainersByCodeRoot">Visible-container contracts keyed by block-code root.</param>
internal readonly record struct LiquidClientFallbackData(
    IReadOnlyDictionary<string, LiquidOpticalProfile> ProfilesByCodeRoot,
    IReadOnlyDictionary<string, LiquidContainerOptics> ContainersByCodeRoot);

/// <summary>
/// Reconstructs client-side liquid profiles from the same JSON patch values
/// used by the server. Vintage Story 1.22 applies block/item definition patches
/// server-side, but does not expose their custom attributes on the corresponding
/// client collectibles. This adapter contains bindings only; physical values
/// remain authored once in the mod's patch assets.
/// </summary>
internal static class LiquidOpticalFallbackCatalog
{
    private static readonly AssetLocation OpticsPatchLocation =
        new("vintagertx", "patches/liquid-optical-profiles.json");
    private static readonly AssetLocation PhysicsPatchLocation =
        new("vintagertx", "patches/liquid-physical-properties.json");
    private static readonly AssetLocation MeasuredWaterPatchLocation =
        new("vintagertx", "patches/liquid-zz-measured-water-optics.json");
    private static readonly AssetLocation BindingsLocation =
        new("vintagertx", "config/liquid-client-bindings.json");

    /// <summary>Loads and validates the four assets needed for client-side profile reconstruction.</summary>
    /// <param name="assets">Active client asset manager, including external mod assets.</param>
    /// <param name="logger">Optional diagnostic destination.</param>
    /// <returns>Validated liquid and container bindings keyed by canonical collectible-code root.</returns>
    public static LiquidClientFallbackData Load(
        IAssetManager assets,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(assets);

        try
        {
            IAsset optics = RequireAsset(assets, OpticsPatchLocation);
            IAsset physics = RequireAsset(assets, PhysicsPatchLocation);
            IAsset measuredWater = RequireAsset(assets, MeasuredWaterPatchLocation);
            IAsset bindings = RequireAsset(assets, BindingsLocation);
            LiquidClientFallbackData result = Parse(
                optics.Data,
                physics.Data,
                measuredWater.Data,
                bindings.Data);
            logger?.Notification(
                "[VintageRTX] Client liquid patch bridge: profiles={0}, containers={1}; physical coefficients read from server patch assets.",
                result.ProfilesByCodeRoot.Count,
                result.ContainersByCodeRoot.Count);
            return result;
        }
        catch (Exception exception)
        {
            logger?.Warning(
                "[VintageRTX] Client liquid patch bridge unavailable; unannotated liquids use the neutral fallback: {0}",
                exception.Message);
            return new LiquidClientFallbackData(
                new Dictionary<string, LiquidOpticalProfile>(StringComparer.Ordinal),
                new Dictionary<string, LiquidContainerOptics>(StringComparer.Ordinal));
        }
    }

    /// <summary>Resolves an exact or variant collectible code against the validated binding roots.</summary>
    /// <param name="code">Loaded collectible asset code.</param>
    /// <param name="profilesByCodeRoot">Profiles indexed by roots such as <c>game:water</c>.</param>
    /// <param name="profile">Resolved physical profile when the code belongs to a binding.</param>
    /// <returns>Whether a matching exact/root-plus-hyphen binding exists.</returns>
    public static bool TryResolve(
        AssetLocation? code,
        IReadOnlyDictionary<string, LiquidOpticalProfile> profilesByCodeRoot,
        out LiquidOpticalProfile profile)
    {
        return TryResolveByCode(code, profilesByCodeRoot, out profile);
    }

    /// <summary>Resolves visible-container metadata for an exact or variant block code.</summary>
    /// <param name="code">Loaded container block asset code.</param>
    /// <param name="containersByCodeRoot">Container contracts indexed by code root.</param>
    /// <param name="container">Resolved contract when the block belongs to a binding.</param>
    /// <returns>Whether an exact/root-plus-hyphen binding exists.</returns>
    public static bool TryResolveContainer(
        AssetLocation? code,
        IReadOnlyDictionary<string, LiquidContainerOptics> containersByCodeRoot,
        out LiquidContainerOptics container)
    {
        return TryResolveByCode(code, containersByCodeRoot, out container);
    }

    /// <summary>
    /// Parses the production assets without an API dependency so deterministic
    /// tests can prove the server patches and client bindings remain identical.
    /// </summary>
    /// <param name="opticsPatch">Base optical/dynamics JSON patch bytes.</param>
    /// <param name="physicsPatch">SI transport/thermal JSON patch bytes.</param>
    /// <param name="measuredWaterPatch">Late pure-water spectral correction bytes.</param>
    /// <param name="bindings">Client collectible-code binding bytes.</param>
    /// <returns>Validated liquid and container bindings keyed by canonical collectible-code root.</returns>
    internal static LiquidClientFallbackData Parse(
        byte[] opticsPatch,
        byte[] physicsPatch,
        byte[] measuredWaterPatch,
        byte[] bindings)
    {
        ArgumentNullException.ThrowIfNull(opticsPatch);
        ArgumentNullException.ThrowIfNull(physicsPatch);
        ArgumentNullException.ThrowIfNull(measuredWaterPatch);
        ArgumentNullException.ThrowIfNull(bindings);

        Dictionary<string, JObject> opticsByTarget = ReadWrappedPatch(
            opticsPatch,
            LiquidOpticalRegistry.OpticalAttributeName);
        Dictionary<string, JObject> containersByTarget = ReadWrappedPatch(
            opticsPatch,
            "vintageRtxLiquidContainer");
        Dictionary<string, JObject> physicsByTarget = ReadWrappedPatch(
            physicsPatch,
            LiquidOpticalRegistry.PhysicalAttributeName);
        ApplyDirectOverrides(
            measuredWaterPatch,
            "/attributes/" + LiquidOpticalRegistry.OpticalAttributeName,
            opticsByTarget);

        JObject bindingDocument = JObject.Parse(Encoding.UTF8.GetString(bindings));
        if (bindingDocument.Value<int?>("schemaVersion") != 1
            || bindingDocument["bindings"] is not JArray bindingArray
            || bindingDocument["containerBindings"] is not JArray containerBindingArray)
        {
            throw new InvalidDataException("Liquid client bindings schema is not version 1.");
        }

        Dictionary<string, LiquidOpticalProfile> profilesByCodeRoot =
            new(StringComparer.Ordinal);
        foreach (JObject binding in bindingArray.OfType<JObject>())
        {
            string target = binding.Value<string>("target")?.Trim() ?? string.Empty;
            if (target.Length == 0
                || !opticsByTarget.TryGetValue(target, out JObject? optics)
                || !physicsByTarget.TryGetValue(target, out JObject? physics))
            {
                throw new InvalidDataException(
                    $"Liquid client binding '{target}' has no complete optics/physics patch pair.");
            }

            JObject attributes = new()
            {
                [LiquidOpticalRegistry.OpticalAttributeName] = optics.DeepClone(),
                [LiquidOpticalRegistry.PhysicalAttributeName] = physics.DeepClone(),
            };
            if (!LiquidOpticalRegistry.TryReadProfile(new JsonObject(attributes), out LiquidOpticalProfile profile))
            {
                throw new InvalidDataException(
                    $"Liquid client binding '{target}' resolves to an invalid physical profile.");
            }

            if (binding["codeRoots"] is not JArray codeRoots || codeRoots.Count == 0)
            {
                throw new InvalidDataException(
                    $"Liquid client binding '{target}' has no collectible-code roots.");
            }

            foreach (JToken codeRootToken in codeRoots)
            {
                string codeRoot = codeRootToken.Value<string>()?.Trim().ToLowerInvariant()
                    ?? string.Empty;
                if (codeRoot.Length == 0 || !codeRoot.Contains(':', StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Liquid client binding '{target}' contains an invalid code root.");
                }

                if (!profilesByCodeRoot.TryAdd(codeRoot, profile))
                {
                    throw new InvalidDataException(
                        $"Liquid collectible-code root '{codeRoot}' is bound more than once.");
                }
            }
        }

        Dictionary<string, LiquidContainerOptics> containersByCodeRoot =
            new(StringComparer.Ordinal);
        foreach (JObject binding in containerBindingArray.OfType<JObject>())
        {
            string target = binding.Value<string>("target")?.Trim() ?? string.Empty;
            if (target.Length == 0
                || !containersByTarget.TryGetValue(target, out JObject? containerValue))
            {
                throw new InvalidDataException(
                    $"Liquid container binding '{target}' has no server patch contract.");
            }

            JObject attributes = new()
            {
                ["vintageRtxLiquidContainer"] = containerValue.DeepClone(),
            };
            if (!LiquidContainerOptics.TryRead(
                    new JsonObject(attributes),
                    out LiquidContainerOptics container))
            {
                throw new InvalidDataException(
                    $"Liquid container binding '{target}' resolves to invalid metadata.");
            }

            AddCodeRoots(binding, target, container, containersByCodeRoot);
        }

        return new LiquidClientFallbackData(profilesByCodeRoot, containersByCodeRoot);
    }

    /// <summary>Adds one binding's validated code roots to a deterministic destination map.</summary>
    /// <typeparam name="TValue">Profile or container metadata value type.</typeparam>
    /// <param name="binding">Binding JSON object.</param>
    /// <param name="target">Human-readable patch target for diagnostics.</param>
    /// <param name="value">Validated value assigned to every root.</param>
    /// <param name="destination">Destination keyed by normalized code root.</param>
    private static void AddCodeRoots<TValue>(
        JObject binding,
        string target,
        TValue value,
        IDictionary<string, TValue> destination)
    {
        if (binding["codeRoots"] is not JArray codeRoots || codeRoots.Count == 0)
        {
            throw new InvalidDataException(
                $"Liquid client binding '{target}' has no collectible-code roots.");
        }

        foreach (JToken codeRootToken in codeRoots)
        {
            string codeRoot = codeRootToken.Value<string>()?.Trim().ToLowerInvariant()
                ?? string.Empty;
            if (codeRoot.Length == 0 || !codeRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Liquid client binding '{target}' contains an invalid code root.");
            }

            if (!destination.TryAdd(codeRoot, value))
            {
                throw new InvalidDataException(
                    $"Liquid collectible-code root '{codeRoot}' is bound more than once.");
            }
        }
    }

    /// <summary>Matches exact codes first, then variants separated from their root by a hyphen.</summary>
    /// <typeparam name="TValue">Bound profile or container metadata type.</typeparam>
    /// <param name="code">Loaded collectible code.</param>
    /// <param name="valuesByCodeRoot">Validated values keyed by canonical root.</param>
    /// <param name="value">Resolved bound value.</param>
    /// <returns>Whether an exact or variant-root match exists.</returns>
    private static bool TryResolveByCode<TValue>(
        AssetLocation? code,
        IReadOnlyDictionary<string, TValue> valuesByCodeRoot,
        out TValue value)
    {
        ArgumentNullException.ThrowIfNull(valuesByCodeRoot);

        if (code is null)
        {
            value = default!;
            return false;
        }

        string canonicalCode = code.ToString();
        if (valuesByCodeRoot.TryGetValue(canonicalCode, out value!))
        {
            return true;
        }

        foreach ((string codeRoot, TValue candidate) in valuesByCodeRoot)
        {
            if (canonicalCode.Length > codeRoot.Length
                && canonicalCode.StartsWith(codeRoot, StringComparison.Ordinal)
                && canonicalCode[codeRoot.Length] == '-')
            {
                value = candidate;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Returns a required mod asset or raises a precise configuration error.</summary>
    /// <param name="assets">Active asset manager.</param>
    /// <param name="location">Canonical asset location.</param>
    /// <returns>The located asset.</returns>
    private static IAsset RequireAsset(IAssetManager assets, AssetLocation location)
    {
        return assets.TryGet(location)
            ?? throw new FileNotFoundException($"Required liquid asset '{location}' was not loaded.");
    }

    /// <summary>Reads patch operations whose values wrap one collectible attribute object.</summary>
    /// <param name="data">UTF-8 patch JSON bytes.</param>
    /// <param name="attributeName">Wrapped attribute name.</param>
    /// <returns>Deep-cloned attribute values keyed by patch target.</returns>
    private static Dictionary<string, JObject> ReadWrappedPatch(byte[] data, string attributeName)
    {
        JArray operations = JArray.Parse(Encoding.UTF8.GetString(data));
        Dictionary<string, JObject> result = new(StringComparer.Ordinal);
        foreach (JObject operation in operations.OfType<JObject>())
        {
            string target = operation.Value<string>("file")?.Trim() ?? string.Empty;
            if (target.Length == 0
                || operation["value"]?[attributeName] is not JObject value)
            {
                continue;
            }

            if (!result.TryAdd(target, (JObject)value.DeepClone()))
            {
                throw new InvalidDataException(
                    $"Liquid patch target '{target}' defines '{attributeName}' more than once.");
            }
        }

        return result;
    }

    /// <summary>Applies late add-merge corrections exactly as the server JSON patch loader does.</summary>
    /// <param name="data">UTF-8 override patch JSON bytes.</param>
    /// <param name="expectedPath">Attribute path accepted from the override file.</param>
    /// <param name="valuesByTarget">Mutable base values keyed by target asset.</param>
    private static void ApplyDirectOverrides(
        byte[] data,
        string expectedPath,
        IDictionary<string, JObject> valuesByTarget)
    {
        JArray operations = JArray.Parse(Encoding.UTF8.GetString(data));
        JsonMergeSettings mergeSettings = new()
        {
            MergeArrayHandling = MergeArrayHandling.Replace,
            MergeNullValueHandling = MergeNullValueHandling.Merge,
        };
        foreach (JObject operation in operations.OfType<JObject>())
        {
            string target = operation.Value<string>("file")?.Trim() ?? string.Empty;
            if (!string.Equals(operation.Value<string>("path"), expectedPath, StringComparison.Ordinal)
                || operation["value"] is not JObject correction
                || !valuesByTarget.TryGetValue(target, out JObject? destination))
            {
                continue;
            }

            destination.Merge(correction, mergeSettings);
        }
    }
}
