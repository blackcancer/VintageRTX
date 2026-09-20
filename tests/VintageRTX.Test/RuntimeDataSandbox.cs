using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VintageRTX.Configuration;

namespace VintageRTX.Test;

/// <summary>
/// Creates a disposable Vintage Story data path for a runtime scenario. Both
/// generated laboratories and copies of real maps run without the user's
/// saves, mods, logs, or mutable configuration directories.
/// </summary>
internal sealed class RuntimeDataSandbox : IDisposable
{
    /// <summary>Prefix proving that a cleanup target belongs to the runtime harness.</summary>
    private const string TemporaryDirectoryPrefix = "vintagertx-runtime-";
    private readonly string captureRoot;
    private readonly DateTime captureCutoffUtc;
    private bool disposed;

    /// <summary>Initializes an isolated runtime data path after all required inputs have been copied.</summary>
    /// <param name="worldName">World name visible only inside the temporary data path.</param>
    /// <param name="captureRoot">Capture directory belonging to that data path.</param>
    /// <param name="captureCutoffUtc">Earliest capture accepted for this run.</param>
    /// <param name="dataRoot">Validated temporary data-path root.</param>
    private RuntimeDataSandbox(
        string worldName,
        string captureRoot,
        DateTime captureCutoffUtc,
        string dataRoot)
    {
        WorldName = worldName;
        this.captureRoot = captureRoot;
        this.captureCutoffUtc = captureCutoffUtc;
        DataRoot = dataRoot;
    }

    /// <summary>Gets the world name opened by the runtime harness.</summary>
    public string WorldName { get; }

    /// <summary>
    /// Gets the isolated data path passed to Vintage Story. The game still
    /// discovers its built-in mods from the installation, while the empty
    /// <c>Mods</c> directory here prevents user mods from entering the run.
    /// </summary>
    public string DataRoot { get; }

    /// <summary>Copies one real reference map and client settings into a new isolated data path.</summary>
    /// <param name="sourceDataRoot">User data root containing the reference map.</param>
    /// <param name="worldName">Reference save filename without the <c>.vcdbs</c> extension.</param>
    /// <param name="runId">Identifier used to make the temporary path recognizable.</param>
    /// <returns>An isolated runtime fixture owning the temporary data path.</returns>
    public static RuntimeDataSandbox Create(string sourceDataRoot, string worldName, string runId)
    {
        string sourceSave = Path.Combine(sourceDataRoot, "Saves", worldName + ".vcdbs");
        if (!File.Exists(sourceSave))
        {
            throw new FileNotFoundException("Reference world save was not found.", sourceSave);
        }

        string sourceClientSettings = RequireClientSettings(sourceDataRoot);
        string dataRoot = CreateTemporaryDataRoot(runId);
        string testWorldName = "vintagertx-test-" + SanitizeRunId(runId);
        string destinationSave = Path.Combine(dataRoot, "Saves", testWorldName + ".vcdbs");
        List<(string Suffix, FileStream Stream)> snapshotFiles = [];
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "Saves"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "Mods"));
            CopyIsolatedClientSettings(
                sourceClientSettings,
                Path.Combine(dataRoot, "clientsettings.json"));

            // Holding every existing SQLite component with FileShare.Read
            // rejects a writer for this save throughout the snapshot. A game
            // using a different save is unaffected by these file-scoped locks.
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string source = sourceSave + suffix;
                if (File.Exists(source))
                {
                    snapshotFiles.Add((suffix, OpenStableSnapshotFile(source)));
                }
            }

            foreach ((string suffix, FileStream stream) in snapshotFiles)
            {
                CopyLockedFile(stream, destinationSave + suffix);
            }

            SanitizeCopiedPlayerData(destinationSave);

            return CreateFixture(testWorldName, dataRoot);
        }
        catch (IOException exception)
        {
            DeleteValidatedDataRoot(dataRoot);
            throw new InvalidOperationException(
                $"Reference world '{worldName}' is open or changed while its test snapshot was created. "
                + "Close that world, but other Vintage Story worlds may remain running.",
                exception);
        }
        catch
        {
            DeleteValidatedDataRoot(dataRoot);
            throw;
        }
        finally
        {
            foreach ((_, FileStream stream) in snapshotFiles)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Creates an empty isolated data path for a generated laboratory world.</summary>
    /// <param name="sourceDataRoot">User data root supplying client authentication and display settings.</param>
    /// <param name="runId">Identifier used to make the temporary path recognizable.</param>
    /// <returns>An isolated runtime fixture owning the temporary data path.</returns>
    public static RuntimeDataSandbox CreateNewWorld(string sourceDataRoot, string runId)
    {
        string sourceClientSettings = RequireClientSettings(sourceDataRoot);
        string dataRoot = CreateTemporaryDataRoot(runId);
        string worldName = "vintagertx-test-" + SanitizeRunId(runId);
        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "Saves"));
            Directory.CreateDirectory(Path.Combine(dataRoot, "Mods"));
            CopyIsolatedClientSettings(
                sourceClientSettings,
                Path.Combine(dataRoot, "clientsettings.json"));
            return CreateFixture(worldName, dataRoot);
        }
        catch
        {
            DeleteValidatedDataRoot(dataRoot);
            throw;
        }
    }

    /// <summary>Copies captures produced by this run into its durable artifact directory.</summary>
    /// <param name="destination">Durable capture artifact directory.</param>
    public void CopyCapturesTo(string destination)
    {
        if (!Directory.Exists(captureRoot))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(captureRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("-capture.json", StringComparison.OrdinalIgnoreCase)))
        {
            if (File.GetLastWriteTimeUtc(file) >= captureCutoffUtc)
            {
                CopySharedFile(file, Path.Combine(destination, Path.GetFileName(file)));
            }
        }
    }

    /// <summary>
    /// Copies the canonical VintageRTX configuration written by the isolated
    /// game into the durable run artifacts. Client settings are intentionally
    /// excluded because they may contain authentication data.
    /// </summary>
    /// <param name="destination">Durable configuration artifact directory.</param>
    public void CopyVintageRtxConfigurationTo(string destination)
    {
        string source = Path.Combine(DataRoot, "ModConfig", "vintagertx.json");
        if (!File.Exists(source))
        {
            return;
        }

        CopySharedFile(source, Path.Combine(destination, "vintagertx.json"));
    }

    /// <summary>Writes one canonical hardware profile into this isolated data path before launch.</summary>
    /// <param name="profile">Profile whose complete authored work budget must be exercised.</param>
    public void SeedVintageRtxConfiguration(VintageRtxRenderProfile profile)
    {
        VintageRtxConfig config = new()
        {
            SchemaVersion = VintageRtxConfig.CurrentSchemaVersion
        };
        config.ApplyRenderProfile(profile);
        string configRoot = Path.Combine(DataRoot, "ModConfig");
        Directory.CreateDirectory(configRoot);
        File.WriteAllText(
            Path.Combine(configRoot, "vintagertx.json"),
            JsonConvert.SerializeObject(config, Formatting.Indented));
    }

    /// <summary>Deletes only the validated temporary data path owned by this fixture.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DeleteValidatedDataRoot(DataRoot);
    }

    /// <summary>Creates a fully initialized fixture for one validated data root.</summary>
    /// <param name="worldName">World name visible inside the isolated data path.</param>
    /// <param name="dataRoot">Validated temporary data-path root.</param>
    /// <returns>The owning runtime fixture.</returns>
    private static RuntimeDataSandbox CreateFixture(string worldName, string dataRoot)
    {
        return new RuntimeDataSandbox(
            worldName,
            Path.Combine(dataRoot, "VintageRTX", "Captures"),
            DateTime.UtcNow.AddSeconds(-2),
            dataRoot);
    }

    /// <summary>Resolves and validates the source client settings file.</summary>
    /// <param name="sourceDataRoot">User data root containing the client settings.</param>
    /// <returns>The source settings path.</returns>
    private static string RequireClientSettings(string sourceDataRoot)
    {
        string sourceClientSettings = Path.Combine(sourceDataRoot, "clientsettings.json");
        if (!File.Exists(sourceClientSettings))
        {
            throw new FileNotFoundException(
                "Vintage Story client settings are required for an authenticated isolated runtime run.",
                sourceClientSettings);
        }

        return sourceClientSettings;
    }

    /// <summary>Creates a unique test-owned directory below the operating-system temporary root.</summary>
    /// <param name="runId">Human-readable run identifier.</param>
    /// <returns>The validated absolute temporary path.</returns>
    private static string CreateTemporaryDataRoot(string runId)
    {
        string temporaryRoot = GetTemporaryRoot();
        string directoryName = $"{TemporaryDirectoryPrefix}{SanitizeRunId(runId)}-{Guid.NewGuid():N}";
        string dataRoot = Path.GetFullPath(Path.Combine(temporaryRoot, directoryName));
        ValidateDataRoot(dataRoot);
        Directory.CreateDirectory(dataRoot);
        return dataRoot;
    }

    /// <summary>Replaces characters that are unsafe in a temporary path or world name.</summary>
    /// <param name="runId">Untrusted scenario run identifier.</param>
    /// <returns>A non-empty filesystem-safe identifier.</returns>
    private static string SanitizeRunId(string runId)
    {
        string safe = string.Concat(runId.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return string.IsNullOrWhiteSpace(safe) ? "run" : safe;
    }

    /// <summary>Gets the normalized operating-system temporary directory.</summary>
    /// <returns>The absolute temporary root without a trailing separator.</returns>
    private static string GetTemporaryRoot()
    {
        return Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    /// <summary>Rejects any target outside the narrowly named temporary subtree.</summary>
    /// <param name="dataRoot">Candidate data root.</param>
    private static void ValidateDataRoot(string dataRoot)
    {
        string temporaryRoot = GetTemporaryRoot();
        string resolvedDataRoot = Path.GetFullPath(dataRoot);
        if (!resolvedDataRoot.StartsWith(
                temporaryRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolvedDataRoot).StartsWith(
                TemporaryDirectoryPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to use an unvalidated runtime data path.");
        }
    }

    /// <summary>Deletes one validated data root, including read-only extracted mod files.</summary>
    /// <param name="dataRoot">Test-owned data root to remove.</param>
    private static void DeleteValidatedDataRoot(string dataRoot)
    {
        ValidateDataRoot(dataRoot);
        if (!Directory.Exists(dataRoot))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        const int maximumAttempts = 8;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                Directory.Delete(dataRoot, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                // The integrated server can release its map-cache SQLite file
                // a few scheduler slices after the process exit is observed.
                Thread.Sleep(Math.Min(100 * attempt, 500));
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(Math.Min(100 * attempt, 500));
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(
                    $"WARN isolated runtime data cleanup deferred for '{dataRoot}': {exception.Message}");
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.WriteLine(
                    $"WARN isolated runtime data cleanup deferred for '{dataRoot}': {exception.Message}");
                return;
            }
        }
    }

    /// <summary>Copies a file that may be shared by a completed game capture.</summary>
    /// <param name="source">Source file.</param>
    /// <param name="destination">New destination file.</param>
    private static void CopySharedFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.SequentialScan);
        using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.SequentialScan);
        input.CopyTo(output);
    }

    /// <summary>Copies a configuration file while excluding concurrent writers.</summary>
    /// <param name="source">Source configuration path.</param>
    /// <param name="destination">New destination file.</param>
    private static void CopyStableFile(string source, string destination)
    {
        using FileStream input = OpenStableSnapshotFile(source);
        CopyLockedFile(input, destination);
    }

    /// <summary>
    /// Copies client settings, then constrains mod discovery to the empty
    /// sandbox-local <c>Mods</c> directory. Absolute user mod paths must never
    /// leak from the source profile into a reproducible runtime scenario.
    /// </summary>
    /// <param name="source">User client settings used only as a display/authentication baseline.</param>
    /// <param name="destination">Sandbox-owned settings path.</param>
    private static void CopyIsolatedClientSettings(string source, string destination)
    {
        CopyStableFile(source, destination);
        JObject settings = JObject.Parse(File.ReadAllText(destination));
        JObject stringLists = settings["stringListSettings"] as JObject ?? new JObject();
        stringLists["modPaths"] = new JArray("Mods");
        settings["stringListSettings"] = stringLists;
        JObject intSettings = settings["intSettings"] as JObject ?? new JObject();
        intSettings["shadowMapQuality"] = 2;
        settings["intSettings"] = intSettings;
        settings.Remove("modPaths");
        File.WriteAllText(destination, settings.ToString(Formatting.Indented));
    }

    /// <summary>Opens one source component while denying writers.</summary>
    /// <param name="source">Exact database, WAL, shared-memory, or settings file.</param>
    /// <returns>A read stream held until its coherent copy has completed.</returns>
    private static FileStream OpenStableSnapshotFile(string source)
    {
        return new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
    }

    /// <summary>Copies a source already protected against concurrent writes.</summary>
    /// <param name="input">Locked source stream positioned anywhere.</param>
    /// <param name="destination">Validated disposable destination.</param>
    private static void CopyLockedFile(FileStream input, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        input.Position = 0;
        using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.SequentialScan);
        input.CopyTo(output);
    }

    /// <summary>
    /// Removes player-specific state from a copied Vintage Story database so inventories owned by
    /// unavailable third-party mods cannot abort an otherwise base-only runtime fixture. The source
    /// save remains read-only and untouched; chunks, map regions, and game data stay intact.
    /// </summary>
    /// <param name="destinationSave">Sandbox-owned SQLite save copied from the reference world.</param>
    private static void SanitizeCopiedPlayerData(string destinationSave)
    {
        // A few filesystem-isolation unit fixtures intentionally use plain-text stand-ins. Only a
        // real SQLite save has state that can or should be sanitized.
        Span<byte> header = stackalloc byte[16];
        using (FileStream input = new(
                   destinationSave,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   header.Length,
                   FileOptions.SequentialScan))
        {
            if (input.Read(header) != header.Length
                || !header.SequenceEqual("SQLite format 3\0"u8))
            {
                return;
            }
        }

        SQLitePCL.Batteries_V2.Init();
        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = destinationSave,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using SqliteConnection connection = new(connectionString.ToString());
        connection.Open();
        using (SqliteTransaction transaction = connection.BeginTransaction())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM playerdata;";
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        using SqliteCommand checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }
}
