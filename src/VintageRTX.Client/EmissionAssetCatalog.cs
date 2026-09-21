using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VintageRTX.Core.Lighting;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Client;

/// <summary>
/// Reads the native patcher's final asset, never raw disk files or a second custom patch pipeline.
/// Per-type settings are parsed once per catalog revision/attribute token, not once per frame.
/// </summary>
internal sealed class EmissionAssetCatalog(IAssetManager assets, ILogger logger)
{
    internal const string AssetName = "vintagertx:config/emission.json";
    private readonly EmissionCatalogState state = new();
    private readonly Dictionary<(string Code, EmissionTarget Target), EmissionSelection> defaults = new();
    private ConditionalWeakTable<JToken, Dictionary<(string Code, EmissionTarget Target), EmissionSelection>> overrides = new();
    private static readonly EmissionSelection Rejected = new(null, null, EmissionProfile.Steady, false, 0);
    internal EmissionCatalog Catalog => state.Current;
    internal long Revision => state.Revision;
    internal string? LastError { get; private set; }
    internal string? Fingerprint { get; private set; }
    internal bool LoadAttempted { get; private set; }
    internal int ResolutionCount { get; private set; }
    internal int RejectedCount { get; private set; }

    /// <summary>Call after native AssetsLoaded patching. Failed reloads retain the previous catalog.</summary>
    internal bool LoadPatched()
    {
        LoadAttempted = true;
        try
        {
            IAsset? asset = assets.TryGet(new AssetLocation(AssetName));
            if (asset is null) throw new InvalidDataException("Missing " + AssetName);
            string json = asset.ToText();
            long before = state.Revision;
            if (!state.TryReplace(json, AssetName)) throw new InvalidDataException(state.LastError);
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            LastError = null;
            if (before != state.Revision)
            {
                InvalidateResolutions();
                logger.Notification("[VintageRTX] Emission catalog {0}: {1} profiles, {2} bindings; revision {3}, sha256 {4}.",
                    AssetName, Catalog.ProfileCount, Catalog.BindingCount, Revision, Fingerprint);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or FormatException or JsonException)
        {
            LastError = exception.Message;
            logger.Error("[VintageRTX] Emission catalog rejected; previous revision {0} retained: {1}", Revision, LastError);
            return false;
        }
    }

    internal void InvalidateResolutions()
    { defaults.Clear(); overrides = new(); ResolutionCount = 0; RejectedCount = 0; }

    internal EmissionSelection Resolve(string code, EmissionTarget target, JsonObject? attributes)
    {
        JToken? token = (attributes?.Token as JObject)?["vintageRtxEmission"];
        Dictionary<(string Code, EmissionTarget Target), EmissionSelection> cache = token is null
            ? defaults : overrides.GetValue(token, _ => new());
        var key = (code, target);
        if (cache.TryGetValue(key, out EmissionSelection? selection)) return selection;
        ResolutionCount++;
        try
        {
            EmissionOverride? settings = token is null ? null : EmissionOverride.Parse(token.ToString(Formatting.None),
                code + "/attributes/vintageRtxEmission");
            selection = Catalog.Resolve(code, target, settings);
        }
        catch (FormatException exception)
        {
            RejectedCount++;
            logger.Warning("[VintageRTX] Emission settings rejected for {0} {1}: {2}", target, code, exception.Message);
            selection = Rejected;
        }
        cache.Add(key, selection);
        return selection;
    }

    internal string Describe() => $"Emission asset={AssetName}, revision={Revision}, profiles={Catalog.ProfileCount}, bindings={Catalog.BindingCount}, resolved types={ResolutionCount}, rejected={RejectedCount}, sha256={Fingerprint ?? "none"}, last error={LastError ?? "none"}.";
}
