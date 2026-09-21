using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using VintageRTX.Core.Lighting;
using Vintagestory.API.Common;

namespace VintageRTX.Client.Tests;

/// <summary>Checks multiplicity against the actual supported game DLL and its supplied asset variants.</summary>
[TestClass, DoNotParallelize]
public sealed class CandleRuntimeCountTests
{
    private static string game = null!;
    [ClassInitialize]
    public static void Initialize(TestContext context) => game = Environment.GetEnvironmentVariable("VINTAGE_STORY")
        ?? throw new InvalidOperationException("Official client references are required.");
    private static EmissionCatalog Catalog() => EmissionCatalog.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "emission.json")));

    [TestMethod]
    public void OfficialChandelierCountTracksTheCurrentExchangedBlockCode()
    {
        if (GameTestIsolation.InvokeIfDefault(typeof(CandleRuntimeCountTests), nameof(OfficialChandelierCountTracksTheCurrentExchangedBlockCode))) return;
        var context = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!;
        string file = Directory.GetFiles(game, "VSSurvivalMod.dll", SearchOption.AllDirectories).Single();
        Assembly survival = context.LoadFromAssemblyPath(Path.GetFullPath(file));
        Type type = survival.GetType("Vintagestory.GameContent.BlockChandelier", throwOnError: true)!;
        Block block = (Block)Activator.CreateInstance(type)!;
        PropertyInfo countGetter = type.GetProperty("CandleCount", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.IsNotNull(countGetter);
        EmissionCatalog catalog = Catalog();
        foreach (int count in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 3, 1, 0 })
        {
            block.Code = new AssetLocation("game:chandelier-brass-candle" + count);
            int nativeCount = (int)countGetter.GetValue(block)!;
            EmissionSelection result = catalog.Resolve(block.Code.ToString(), EmissionTarget.Block);
            Assert.AreEqual(nativeCount, result.ComponentCount);
            if (nativeCount == 0) Assert.IsNull(result.CreateLight(default, Vector3.Zero));
            else Assert.AreEqual(nativeCount, result.CreateLight(default, Vector3.One)!.ComponentCount);
        }
    }

    [TestMethod]
    public void EveryOfficialGroundCandleQuantityIsRepresentedInTheAssetCatalog()
    {
        if (GameTestIsolation.InvokeIfDefault(typeof(CandleRuntimeCountTests), nameof(EveryOfficialGroundCandleQuantityIsRepresentedInTheAssetCatalog))) return;
        EmissionCatalog catalog = Catalog(); int examined = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(game, "assets"), "*.json", SearchOption.AllDirectories))
        {
            if (!file.Replace('\\', '/').Contains("/blocktypes/", StringComparison.Ordinal)) continue;
            if (!Path.GetFileName(file).Contains("candle", StringComparison.OrdinalIgnoreCase)) continue;
            JObject json = JObject.Parse(File.ReadAllText(file));
            string? code = json["code"]?.Value<string>();
            if (code is not ("bunchocandles" or "candles")) continue;
            JArray? groups = json.GetValue("variantgroups", StringComparison.OrdinalIgnoreCase) as JArray;
            if (groups is null) continue;
            JObject? quantity = groups.OfType<JObject>().SingleOrDefault(g => g["code"]?.Value<string>() == "quantity");
            if (quantity?["states"] is not JArray states) continue;
            foreach (JToken state in states)
            {
                string value = state.Value<string>()!;
                Assert.IsTrue(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count), file);
                string runtimeCode = "game:" + code + "-" + value;
                Assert.AreEqual(count, catalog.Resolve(runtimeCode, EmissionTarget.Block).ComponentCount, runtimeCode);
                examined++;
            }
        }
        Assert.IsTrue(examined >= 2, "No variable-quantity ground candle asset was verified.");
        Console.WriteLine("Official ground candle quantities checked: {0}", examined);
    }
}
