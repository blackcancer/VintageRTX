using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace VintageRTX.Client.Tests;

[TestClass]
public sealed class CompleteNativePipelineContractTests
{
    [TestMethod]
    public void RecordActualWorldStageInterfacesWithoutInventingLiquidOrHeldLayouts()
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY")
            ?? throw new InvalidOperationException("Official client installation required.");
        string? evidence = Environment.GetEnvironmentVariable("VINTAGERTX_DIRECT_EVIDENCE");
        string[] files = Directory.GetFiles(Path.Combine(game, "assets"), "*", SearchOption.AllDirectories)
            .Where(p => p.Replace('\\', '/').Contains("/shaders/", StringComparison.Ordinal)
                || p.Replace('\\', '/').Contains("/shaderincludes/", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(files.Length > 10);
        var names = files.Select(Path.GetFileName).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Console.WriteLine("Native shader inventory: " + string.Join(", ", names));
        if (evidence is null) return;
        string directory = Path.Combine(evidence, "complete-native-contracts");
        Directory.CreateDirectory(directory);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("chunk", StringComparison.Ordinal)
                || name.StartsWith("helditem", StringComparison.Ordinal)
                || name.StartsWith("standard", StringComparison.Ordinal)
                || name.StartsWith("entity", StringComparison.Ordinal)
                || file.Replace('\\', '/').Contains("/shaderincludes/", StringComparison.Ordinal))
                File.Copy(file, Path.Combine(directory, name), true);
        }
        File.WriteAllText(Path.Combine(directory, "inventory.json"), JsonSerializer.Serialize(names));
    }
}
