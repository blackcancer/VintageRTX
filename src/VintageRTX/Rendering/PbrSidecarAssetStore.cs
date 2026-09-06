using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Indexes PBR sidecars for VintageRTX while leaving them visible to their
/// authoring mods through the shared asset catalog. Atlas-specific guards keep
/// the reserved suffixes out of vanilla albedo wildcard expansion.
/// </summary>
internal sealed class PbrSidecarAssetStore
{
    private readonly Dictionary<AssetLocation, IAsset> assets;
    private readonly AssetLocation[] normalLocations;

    /// <summary>Creates an immutable lookup view over globally registered sidecars.</summary>
    /// <param name="assets">Indexed sidecars keyed by their original asset locations.</param>
    private PbrSidecarAssetStore(Dictionary<AssetLocation, IAsset> assets)
    {
        this.assets = assets;
        normalLocations = assets.Keys
            .Where(location => location.Path.EndsWith("_n.png", StringComparison.Ordinal))
            .OrderBy(location => location.Domain, StringComparer.Ordinal)
            .ThenBy(location => location.Path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Gets normal-map locations in deterministic domain/path order for atlas construction.</summary>
    public IReadOnlyList<AssetLocation> NormalLocations => normalLocations;

    /// <summary>Gets the total number of normal, roughness, metallic, and emissive sidecars.</summary>
    public int Count => assets.Count;

    /// <summary>
    /// Indexes PBR sidecars without removing them from the shared asset catalog.
    /// Atlas-specific Harmony guards exclude the reserved suffixes from albedo wildcard expansion,
    /// while retaining normal asset visibility for the mod that authored the sidecars.
    /// </summary>
    /// <param name="api">Core API whose asset manager owns the mutable discovery dictionary.</param>
    /// <returns>The indexed sidecar store used by the PBR renderer.</returns>
    public static PbrSidecarAssetStore Capture(ICoreAPI api)
    {
        return Capture(api.Assets, api.Logger);
    }

    /// <summary>
    /// Preserves the startup delegate ABI used by the mod system. The legacy name no longer hides
    /// assets; it delegates to the non-destructive <see cref="Capture(ICoreAPI)"/> indexer.
    /// </summary>
    /// <param name="api">Core API whose catalog remains unchanged.</param>
    /// <returns>A private deterministic sidecar view.</returns>
    public static PbrSidecarAssetStore CaptureAndHide(ICoreAPI api)
    {
        return Capture(api);
    }

    /// <summary>Testable overload that indexes sidecars through explicit asset and log services.</summary>
    /// <param name="assetManager">Asset registry inspected without mutation.</param>
    /// <param name="logger">Destination for index cardinality diagnostics.</param>
    /// <returns>A private deterministic view over sidecars that remain globally registered.</returns>
    internal static PbrSidecarAssetStore Capture(
        IAssetManager assetManager,
        ILogger logger)
    {
        Dictionary<AssetLocation, IAsset> captured = [];
        KeyValuePair<AssetLocation, IAsset>[] sidecars = assetManager.AllAssets
            .Where(entry => IsPbrSidecar(entry.Key))
            .ToArray();

        foreach ((AssetLocation location, IAsset asset) in sidecars)
        {
            captured.TryAdd(location, asset);
        }

        PbrSidecarAssetStore store = new(captured);
        logger.Notification(
            "[VintageRTX] PBR sidecars indexed without hiding mod assets: total={0}, normals={1}, roughness={2}, metallic={3}, emissive={4}.",
            store.Count,
            captured.Keys.Count(location => location.Path.EndsWith("_n.png", StringComparison.Ordinal)),
            captured.Keys.Count(location => location.Path.EndsWith("_r.png", StringComparison.Ordinal)),
            captured.Keys.Count(location => location.Path.EndsWith("_m.png", StringComparison.Ordinal)),
            captured.Keys.Count(location => location.Path.EndsWith("_e.png", StringComparison.Ordinal)));
        return store;
    }

    /// <summary>Resolves and lazily loads an indexed sidecar without mutating global visibility.</summary>
    /// <param name="location">Exact domain/path identity of the requested sidecar.</param>
    /// <param name="asset">Loaded asset, or <see langword="null"/> when resolution/loading fails.</param>
    /// <returns><see langword="true"/> when the asset is ready for decoding.</returns>
    public bool TryGet(AssetLocation location, out IAsset? asset)
    {
        if (!assets.TryGetValue(location, out asset))
        {
            return false;
        }

        if (!asset.IsLoaded() && !asset.Origin.TryLoadAsset(asset))
        {
            asset = null;
            return false;
        }

        return true;
    }

    /// <summary>Checks both the texture namespace and the reserved PBR filename suffix.</summary>
    /// <param name="location">Candidate mod asset.</param>
    /// <returns>Whether vanilla albedo discovery must ignore this PNG.</returns>
    internal static bool IsPbrSidecar(AssetLocation location)
    {
        if (!location.Path.StartsWith("textures/", StringComparison.Ordinal)
            || !location.Path.EndsWith(".png", StringComparison.Ordinal))
        {
            return false;
        }

        return HasPbrSuffix(location);
    }

    /// <summary>Recognizes the <c>_n</c>, <c>_r</c>, <c>_m</c>, and <c>_e</c> contracts.</summary>
    /// <param name="location">Candidate location, optionally including the PNG extension.</param>
    /// <returns><see langword="false"/> for null, empty, or ordinary albedo names.</returns>
    internal static bool HasPbrSuffix(AssetLocation? location)
    {
        if (location is null || string.IsNullOrEmpty(location.Path))
        {
            return false;
        }

        ReadOnlySpan<char> path = location.Path.AsSpan();
        if (path.EndsWith(".png", StringComparison.Ordinal))
        {
            path = path[..^4];
        }

        return path.EndsWith("_n", StringComparison.Ordinal)
            || path.EndsWith("_r", StringComparison.Ordinal)
            || path.EndsWith("_m", StringComparison.Ordinal)
            || path.EndsWith("_e", StringComparison.Ordinal);
    }
}
