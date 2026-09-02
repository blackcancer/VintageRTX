using Vintagestory.API.Common;

namespace VintageRTX.Configuration;

/// <summary>
/// Owns the persisted VintageRTX configuration and guarantees that consumers only observe a
/// migrated, range-clamped instance. Invalid JSON is recoverable and is replaced with defaults.
/// </summary>
internal sealed class ConfigStore
{
    /// <summary>Relative Vintage Story mod-config path used for both reads and atomic rewrites.</summary>
    private const string FileName = "vintagertx.json";
    private readonly ICoreAPI api;
    /// <summary>Prevents this build from deleting fields introduced by a newer configuration schema.</summary>
    private bool futureSchemaReadOnly;

    /// <summary>
    /// Loads and normalizes configuration immediately so render services never see raw disk data.
    /// </summary>
    /// <param name="api">Vintage Story API providing storage and diagnostic logging.</param>
    public ConfigStore(ICoreAPI api)
    {
        this.api = api;
        Current = Load();
    }

    /// <summary>Gets the last successfully normalized configuration snapshot.</summary>
    public VintageRtxConfig Current { get; private set; }

    /// <summary>Re-reads disk state, applies migrations and bounds, then replaces <see cref="Current"/>.</summary>
    /// <returns>The normalized replacement instance.</returns>
    public VintageRtxConfig Reload()
    {
        Current = Load();
        return Current;
    }

    /// <summary>Clamps in-memory values before persisting them to prevent invalid runtime state.</summary>
    public void Save()
    {
        if (futureSchemaReadOnly)
        {
            api.Logger.Warning(
                "[VintageRTX] Configuration schema {0} is newer than supported schema {1}; changes are kept in memory but the file is not overwritten.",
                Current.SchemaVersion,
                VintageRtxConfig.CurrentSchemaVersion);
            return;
        }

        Current.Clamp();
        api.StoreModConfig(Current, FileName);
    }

    /// <summary>
    /// Performs the fault-tolerant load/migrate/clamp pipeline and always rewrites canonical JSON.
    /// </summary>
    /// <returns>A usable configuration even when the previous file was absent or malformed.</returns>
    private VintageRtxConfig Load()
    {
        VintageRtxConfig config;

        try
        {
            config = api.LoadModConfig<VintageRtxConfig>(FileName) ?? new VintageRtxConfig();
        }
        catch (Exception exception)
        {
            api.Logger.Warning("[VintageRTX] Invalid configuration; defaults restored: {0}", exception.Message);
            config = new VintageRtxConfig();
        }

        futureSchemaReadOnly = config.SchemaVersion > VintageRtxConfig.CurrentSchemaVersion;
        if (futureSchemaReadOnly)
        {
            api.Logger.Warning(
                "[VintageRTX] Configuration schema {0} is newer than supported schema {1}; loading known fields read-only to preserve future data.",
                config.SchemaVersion,
                VintageRtxConfig.CurrentSchemaVersion);
            config.Clamp();
            return config;
        }

        if (config.Migrate())
        {
            api.Logger.Notification("[VintageRTX] Configuration migrated to schema {0}.", config.SchemaVersion);
        }

        config.Clamp();
        api.StoreModConfig(config, FileName);
        return config;
    }
}
