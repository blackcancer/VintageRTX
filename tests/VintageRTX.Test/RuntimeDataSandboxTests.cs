using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VintageRTX.Configuration;

namespace VintageRTX.Test;

/// <summary>Verifies filesystem isolation for generated and real-map runtime scenarios.</summary>
[TestClass]
public sealed class RuntimeDataSandboxTests
{
    /// <summary>
    /// Verifies that a real map and client settings are copied, user mods are
    /// excluded, another save can remain open, and cleanup preserves sources.
    /// </summary>
    [TestMethod]
    public void CreateCopiesOnlyRequiredRealMapInputs()
    {
        string sourceRoot = CreateSourceRoot();
        string targetSave = Path.Combine(sourceRoot, "Saves", "target.vcdbs");
        string otherSave = Path.Combine(sourceRoot, "Saves", "other.vcdbs");
        File.WriteAllText(targetSave, "target database");
        File.WriteAllText(targetSave + "-wal", "target wal");
        File.WriteAllText(targetSave + "-shm", "target shm");
        File.WriteAllText(otherSave, "other database");
        string userMod = Path.Combine(sourceRoot, "Mods", "unrelated-user-mod.zip");
        File.WriteAllText(userMod, "must not be copied");

        string? isolatedRoot = null;
        try
        {
            using (FileStream otherWorldLock = new(
                       otherSave,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (RuntimeDataSandbox sandbox = RuntimeDataSandbox.Create(
                       sourceRoot,
                       "target",
                       "real-map-isolation"))
            {
                isolatedRoot = sandbox.DataRoot;
                Assert.AreNotEqual(Path.GetFullPath(sourceRoot), isolatedRoot);
                Assert.IsTrue(File.Exists(Path.Combine(isolatedRoot, "clientsettings.json")));
                JObject copiedSettings = JObject.Parse(File.ReadAllText(
                    Path.Combine(isolatedRoot, "clientsettings.json")));
                Assert.AreEqual(1.25, copiedSettings.Value<double>("guiScale"), 0.001);
                CollectionAssert.AreEqual(
                    new[] { "Mods" },
                    copiedSettings["stringListSettings"]!["modPaths"]!
                        .Values<string>()
                        .ToArray());
                Assert.AreEqual(
                    2,
                    copiedSettings["intSettings"]!.Value<int>("shadowMapQuality"),
                    "The isolated runtime must enable both native sun-shadow cascades.");
                Assert.IsNull(copiedSettings["modPaths"]);

                string copiedSave = Path.Combine(
                    isolatedRoot,
                    "Saves",
                    sandbox.WorldName + ".vcdbs");
                Assert.AreEqual("target database", File.ReadAllText(copiedSave));
                Assert.AreEqual("target wal", File.ReadAllText(copiedSave + "-wal"));
                Assert.AreEqual("target shm", File.ReadAllText(copiedSave + "-shm"));
                Assert.IsTrue(Directory.Exists(Path.Combine(isolatedRoot, "Mods")));
                Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(
                    Path.Combine(isolatedRoot, "Mods")).Count());
                Assert.IsFalse(File.Exists(Path.Combine(
                    isolatedRoot,
                    "Saves",
                    "other.vcdbs")));
            }

            Assert.IsNotNull(isolatedRoot);
            Assert.IsFalse(Directory.Exists(isolatedRoot));
            Assert.AreEqual("target database", File.ReadAllText(targetSave));
            Assert.AreEqual("must not be copied", File.ReadAllText(userMod));
            Assert.AreEqual("other database", File.ReadAllText(otherSave));
        }
        finally
        {
            DeleteSourceRoot(sourceRoot);
        }
    }

    /// <summary>Verifies that a generated laboratory also starts with no user mods or saves.</summary>
    [TestMethod]
    public void CreateNewWorldUsesEmptyIsolatedDirectories()
    {
        string sourceRoot = CreateSourceRoot();
        string? isolatedRoot = null;
        try
        {
            File.WriteAllText(Path.Combine(sourceRoot, "Saves", "user.vcdbs"), "user save");
            File.WriteAllText(Path.Combine(sourceRoot, "Mods", "user.zip"), "user mod");
            using (RuntimeDataSandbox sandbox = RuntimeDataSandbox.CreateNewWorld(
                       sourceRoot,
                       "generated-lab"))
            {
                isolatedRoot = sandbox.DataRoot;
                Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(
                    Path.Combine(isolatedRoot, "Saves")).Count());
                Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(
                    Path.Combine(isolatedRoot, "Mods")).Count());

                string artifactRoot = Path.Combine(isolatedRoot, "artifact-copy");
                sandbox.CopyVintageRtxConfigurationTo(artifactRoot);
                Assert.IsFalse(Directory.Exists(artifactRoot));

                sandbox.SeedVintageRtxConfiguration(VintageRtxRenderProfile.Performance);
                VintageRtxConfig seeded = JsonConvert.DeserializeObject<VintageRtxConfig>(
                    File.ReadAllText(Path.Combine(
                        isolatedRoot,
                        "ModConfig",
                        "vintagertx.json")))!;
                Assert.AreEqual(VintageRtxConfig.CurrentSchemaVersion, seeded.SchemaVersion);
                Assert.AreEqual(VintageRtxRenderProfile.Performance, seeded.RenderProfile);
                Assert.AreEqual(4.50f, seeded.GpuBudgetMilliseconds);
                Assert.IsTrue(seeded.ScreenSpaceReflectionsEnabled);
                Assert.IsTrue(seeded.VoxelReflectionsEnabled);

                sandbox.CopyVintageRtxConfigurationTo(artifactRoot);
                VintageRtxConfig archived = JsonConvert.DeserializeObject<VintageRtxConfig>(
                    File.ReadAllText(Path.Combine(artifactRoot, "vintagertx.json")))!;
                Assert.AreEqual(VintageRtxRenderProfile.Performance, archived.RenderProfile);

                string modConfigRoot = Path.Combine(isolatedRoot, "ModConfig");
                Directory.CreateDirectory(modConfigRoot);
                File.WriteAllText(
                    Path.Combine(modConfigRoot, "vintagertx.json"),
                    "{ \"schemaVersion\": 12, \"saturation\": 1.0 }");
                string secondArtifactRoot = Path.Combine(isolatedRoot, "artifact-copy-legacy");
                sandbox.CopyVintageRtxConfigurationTo(secondArtifactRoot);
                Assert.AreEqual(
                    "{ \"schemaVersion\": 12, \"saturation\": 1.0 }",
                    File.ReadAllText(Path.Combine(secondArtifactRoot, "vintagertx.json")));
            }

            Assert.IsNotNull(isolatedRoot);
            Assert.IsFalse(Directory.Exists(isolatedRoot));
            Assert.IsTrue(File.Exists(Path.Combine(sourceRoot, "Saves", "user.vcdbs")));
            Assert.IsTrue(File.Exists(Path.Combine(sourceRoot, "Mods", "user.zip")));
        }
        finally
        {
            DeleteSourceRoot(sourceRoot);
        }
    }

    /// <summary>Verifies isolated seed injection and rejects malformed generated configurations.</summary>
    [TestMethod]
    public void GeneratedServerConfigurationReceivesOnlyTheRequestedWorldSeed()
    {
        string sourceRoot = CreateSourceRoot();
        try
        {
            string configurationPath = Path.Combine(sourceRoot, "serverconfig.json");
            File.WriteAllText(
                configurationPath,
                "{ \"ServerName\": \"fixture\", \"WorldConfig\": { \"Seed\": null, \"WorldType\": \"standard\" } }");

            RuntimeHarness.ApplyWorldSeedToGeneratedServerConfiguration(
                configurationPath,
                ScenarioCatalog.RenderLabWorldSeed);

            JObject configuration = JObject.Parse(File.ReadAllText(configurationPath));
            Assert.AreEqual("fixture", configuration.Value<string>("ServerName"));
            Assert.AreEqual(
                ScenarioCatalog.RenderLabWorldSeed,
                configuration["WorldConfig"]!.Value<string>("Seed"));
            Assert.AreEqual(
                "standard",
                configuration["WorldConfig"]!.Value<string>("WorldType"));

            File.WriteAllText(configurationPath, "{ \"ServerName\": \"malformed\" }");
            Assert.ThrowsException<InvalidDataException>(() =>
                RuntimeHarness.ApplyWorldSeedToGeneratedServerConfiguration(
                    configurationPath,
                    ScenarioCatalog.RenderLabWorldSeed));
            Assert.ThrowsException<ArgumentException>(() =>
                RuntimeHarness.ApplyWorldSeedToGeneratedServerConfiguration(
                    string.Empty,
                    ScenarioCatalog.RenderLabWorldSeed));
            Assert.ThrowsException<ArgumentException>(() =>
                RuntimeHarness.ApplyWorldSeedToGeneratedServerConfiguration(
                    configurationPath,
                    " "));
        }
        finally
        {
            DeleteSourceRoot(sourceRoot);
        }
    }

    /// <summary>Verifies the saved-world path and private endpoint used during deterministic precreation.</summary>
    [TestMethod]
    public void GeneratedServerConfigurationReceivesSafeWorldCreationSettings()
    {
        string sourceRoot = CreateSourceRoot();
        try
        {
            string configurationPath = Path.Combine(sourceRoot, "serverconfig.json");
            string savePath = Path.Combine(sourceRoot, "Saves", "deterministic.vcdbs");
            File.WriteAllText(
                configurationPath,
                "{ \"ServerName\": \"fixture\", \"Ip\": null, \"Port\": 42420, \"AdvertiseServer\": true, \"Upnp\": true, \"WorldConfig\": { \"Seed\": null, \"SaveFileLocation\": \"old.vcdbs\", \"WorldType\": \"standard\" } }");

            RuntimeHarness.ApplyWorldCreationSettingsToGeneratedServerConfiguration(
                configurationPath,
                ScenarioCatalog.RenderLabWorldSeed,
                savePath,
                43871);

            JObject configuration = JObject.Parse(File.ReadAllText(configurationPath));
            Assert.AreEqual("fixture", configuration.Value<string>("ServerName"));
            Assert.AreEqual("127.0.0.1", configuration.Value<string>("Ip"));
            Assert.AreEqual(43871, configuration.Value<int>("Port"));
            Assert.IsFalse(configuration.Value<bool>("AdvertiseServer"));
            Assert.IsFalse(configuration.Value<bool>("Upnp"));
            Assert.AreEqual(
                ScenarioCatalog.RenderLabWorldSeed,
                configuration["WorldConfig"]!.Value<string>("Seed"));
            Assert.AreEqual(
                Path.GetFullPath(savePath),
                configuration["WorldConfig"]!.Value<string>("SaveFileLocation"));
            Assert.AreEqual(
                "standard",
                configuration["WorldConfig"]!.Value<string>("WorldType"));

            Assert.ThrowsException<ArgumentException>(() =>
                RuntimeHarness.ApplyWorldCreationSettingsToGeneratedServerConfiguration(
                    configurationPath,
                    ScenarioCatalog.RenderLabWorldSeed,
                    " ",
                    43871));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                RuntimeHarness.ApplyWorldCreationSettingsToGeneratedServerConfiguration(
                    configurationPath,
                    ScenarioCatalog.RenderLabWorldSeed,
                    savePath,
                    0));
        }
        finally
        {
            DeleteSourceRoot(sourceRoot);
        }
    }

    /// <summary>
    /// Verifies that a copied real-map database loses mod-owned player inventories while the source
    /// player row and all world tables remain unchanged.
    /// </summary>
    [TestMethod]
    public void CreateSanitizesOnlyCopiedPlayerData()
    {
        string sourceRoot = CreateSourceRoot();
        string sourceSave = Path.Combine(sourceRoot, "Saves", "target.vcdbs");
        string? isolatedRoot = null;
        try
        {
            CreateRepresentativeSave(sourceSave);
            using (RuntimeDataSandbox sandbox = RuntimeDataSandbox.Create(
                       sourceRoot,
                       "target",
                       "playerdata-sanitization"))
            {
                isolatedRoot = sandbox.DataRoot;
                string copiedSave = Path.Combine(
                    isolatedRoot,
                    "Saves",
                    sandbox.WorldName + ".vcdbs");
                Assert.AreEqual(0L, ExecuteScalar(copiedSave, "SELECT COUNT(*) FROM playerdata;"));
                Assert.AreEqual(1L, ExecuteScalar(copiedSave, "SELECT COUNT(*) FROM chunk;"));
            }

            Assert.AreEqual(1L, ExecuteScalar(sourceSave, "SELECT COUNT(*) FROM playerdata;"));
            Assert.AreEqual(1L, ExecuteScalar(sourceSave, "SELECT COUNT(*) FROM chunk;"));
        }
        finally
        {
            Assert.IsNotNull(isolatedRoot);
            Assert.IsFalse(Directory.Exists(isolatedRoot));
            DeleteSourceRoot(sourceRoot);
        }
    }

    /// <summary>Creates the minimal SQLite schema needed to model a modded real-world save.</summary>
    /// <param name="path">Disposable source save path.</param>
    private static void CreateRepresentativeSave(string path)
    {
        SQLitePCL.Batteries_V2.Init();
        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE playerdata (playerid INTEGER PRIMARY KEY AUTOINCREMENT, playeruid TEXT, data BLOB);"
            + "CREATE TABLE chunk (position INTEGER PRIMARY KEY, data BLOB);"
            + "INSERT INTO playerdata(playeruid, data) VALUES ('fixture-player', X'010203');"
            + "INSERT INTO chunk(position, data) VALUES (42, X'040506');";
        command.ExecuteNonQuery();
    }

    /// <summary>Reads one integer scalar from a disposable SQLite fixture.</summary>
    /// <param name="path">Database path.</param>
    /// <param name="sql">Scalar query.</param>
    /// <returns>The converted 64-bit scalar.</returns>
    private static long ExecuteScalar(string path, string sql)
    {
        SQLitePCL.Batteries_V2.Init();
        using SqliteConnection connection = new($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Creates a disposable stand-in for the user's data root.</summary>
    /// <returns>The absolute source-root path.</returns>
    private static string CreateSourceRoot()
    {
        string sourceRoot = Path.Combine(
            Path.GetTempPath(),
            "vintagertx-sandbox-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Saves"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Mods"));
        File.WriteAllText(
            Path.Combine(sourceRoot, "clientsettings.json"),
            """
            {
              "guiScale": 1.25,
              "intSettings": {
                "shadowMapQuality": 0
              },
              "stringListSettings": {
                "modPaths": ["Mods", "C:\\Users\\fixture\\VintagestoryData\\Mods"]
              }
            }
            """);
        return sourceRoot;
    }

    /// <summary>Removes only the source stand-in created by this test class.</summary>
    /// <param name="sourceRoot">Candidate source stand-in.</param>
    private static void DeleteSourceRoot(string sourceRoot)
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string resolved = Path.GetFullPath(sourceRoot);
        if (!resolved.StartsWith(
                temporaryRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith(
                "vintagertx-sandbox-source-",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete an unvalidated test source root.");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}
