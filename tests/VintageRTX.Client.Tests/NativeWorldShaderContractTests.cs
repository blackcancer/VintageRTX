using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Client.Tests;

/// <summary>Qualifies the actual installed shader inputs, not a guessed or legacy renderer layout.</summary>
[TestClass]
public sealed class NativeWorldShaderContractTests
{
    [TestMethod]
    public void SupportedClientProvidesTheOpaqueAndCompositionShaderChain()
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY")
            ?? throw new InvalidOperationException("VINTAGE_STORY is required for native shader qualification.");
        string assets = Path.Combine(game, "assets");
        string[] required = ["chunkopaque.fsh", "chunkopaque.vsh", "entityanimated.fsh", "entityanimated.vsh",
            "fogandlight.fsh", "fogandlight.vsh", "final.fsh", "luma.fsh", "vertexflagbits.ash", "colormap.fsh"];
        string? evidence = Environment.GetEnvironmentVariable("VINTAGERTX_DIRECT_EVIDENCE");
        var fingerprints = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in required)
        {
            string[] paths = Directory.GetFiles(assets, name, SearchOption.AllDirectories);
            Assert.AreEqual(1, paths.Length, "Ambiguous or missing official shader: " + name);
            string source = File.ReadAllText(paths[0]);
            Assert.IsFalse(string.IsNullOrWhiteSpace(source), name);
            if (name.StartsWith("chunkopaque.", StringComparison.Ordinal)
                || name.StartsWith("entityanimated.", StringComparison.Ordinal))
                StringAssert.Contains(source, "void main", name);
            fingerprints[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[0])));
            if (evidence is not null)
            {
                string directory = Path.Combine(evidence, "native-contracts");
                Directory.CreateDirectory(directory);
                File.Copy(paths[0], Path.Combine(directory, name), true);
            }
        }
        if (evidence is not null)
            File.WriteAllText(Path.Combine(evidence, "native-contracts", "sha256.json"),
                JsonSerializer.Serialize(fingerprints, new JsonSerializerOptions { WriteIndented = true }));
    }
}
