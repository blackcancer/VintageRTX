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
