using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VintageRTX.Client;
using VintageRTX.Core.Lighting;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageRTX.Client.Tests;

/// <summary>Memory-backed world/assets, but the patcher and API assemblies are the actual game binaries.</summary>
[TestClass]
[DoNotParallelize]
public sealed class EmissionAssetTests
{
    private static string game = null!;
    private static string Asset() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "emission.json"));
    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        game = Environment.GetEnvironmentVariable("VINTAGE_STORY")
            ?? throw new InvalidOperationException("Client integration tests require the official VINTAGE_STORY references.");
        AssemblyLoadContext.Default.Resolving += ResolveGameAssembly;
    }
    [ClassCleanup]
    public static void Cleanup() => AssemblyLoadContext.Default.Resolving -= ResolveGameAssembly;
    private static Assembly? ResolveGameAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        foreach (string folder in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
        {
            string file = Path.Combine(folder, name.Name + ".dll");
            if (File.Exists(file)) return context.LoadFromAssemblyPath(Path.GetFullPath(file));
        }
        return null;
    }
    private static ModSystem NativePatcher()
    {
        string file = Directory.GetFiles(game, "VSEssentials.dll", SearchOption.AllDirectories).Single();
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(file));
        Type type = assembly.GetTypes().Single(candidate => candidate.Name == "ModJsonPatchLoader");
        return (ModSystem)Activator.CreateInstance(type)!;
    }

    [TestMethod]
    public void ActualNativePatcherAppliesShippedExampleBeforeCatalogConsumption()
    {
        var world = new MemoryContext(Asset());
        world.Add("vrtxemissionexample:patches/emission.json",
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "example-patch.json")));
        ModSystem patcher = NativePatcher();
        Assert.IsTrue(patcher.ExecuteOrder() < new VintageRTXModSystem().ExecuteOrder());
        patcher.AssetsLoaded(world.Api);
        Assert.IsTrue(world.Catalog.IsPatched, string.Join("\n", world.Errors));
        Assert.AreEqual(0, world.Errors.Count, string.Join("\n", world.Errors));
        var settings = new EmissionAssetCatalog(world.Assets, world.Logger);
        Assert.IsTrue(settings.LoadPatched(), settings.LastError);
        EmissionSelection candle = settings.Resolve("game:candle", EmissionTarget.Item, null);
        Assert.AreEqual(.08, candle.Profile.Amplitude);
        Assert.AreEqual("vintagertx:candle", settings.Resolve("game:chandelier-brass-candle8", EmissionTarget.Block, null).ProfileId);
        Assert.AreEqual(EmissionKind.Steady, settings.Resolve("example:crystal-lantern-blue", EmissionTarget.Item, null).Profile.Kind);
        Assert.IsNull(candle.CreateLight(default, Vector3.Zero));
        Assert.AreEqual(10, settings.Catalog.ProfileCount);
        Assert.IsNotNull(settings.Fingerprint);
    }

    [TestMethod]
    public void ActualAttributePatchPreservesUnrelatedFieldsAndOverridesIntrinsicDefault()
    {
        var world = new MemoryContext(Asset());
        MemoryAsset entity = world.Add("example:entities/torchbot.json", """{"code":"torchbot","attributes":{"unrelated":42}}""");
        world.Add("example:patches/torchbot.json", """
            [{"file":"example:entities/torchbot.json","side":"client","op":"add",
              "path":"/attributes/vintageRtxEmission","value":{"profile":"vintagertx:candle","intensityScale":0.5}}]
            """);
        NativePatcher().AssetsLoaded(world.Api);
        Assert.IsTrue(entity.IsPatched, string.Join("\n", world.Errors));
        JObject document = JObject.Parse(entity.Text);
        Assert.AreEqual(42, document["attributes"]!["unrelated"]!.Value<int>());
        var settings = new EmissionAssetCatalog(world.Assets, world.Logger);
        Assert.IsTrue(settings.LoadPatched());
        var attributes = new JsonObject(document["attributes"]!);
        EmissionSelection result = settings.Resolve("example:torchbot", EmissionTarget.Entity, attributes);
        Assert.AreEqual("vintagertx:candle", result.ProfileId);
        Assert.AreEqual(new Vector3(.5f), result.CreateLight(default, Vector3.One)!.Intensity);
        Assert.IsNull(result.CreateLight(default, Vector3.Zero));
    }

    [TestMethod]
    public void ModSystemLoadsOnlyAfterAssetsStageAndRespectsClientSide()
    {
        var world = new MemoryContext(Asset());
        var system = new VintageRTXModSystem();
        Assert.IsTrue(system.ShouldLoad(EnumAppSide.Client)); Assert.IsFalse(system.ShouldLoad(EnumAppSide.Server));
        system.Start(world.Api);
        Assert.AreEqual(0, world.Reads);
        system.AssetsLoaded(world.Api);
        Assert.AreEqual(1, world.Reads);
        system.Dispose(); system.Dispose();
    }

    [TestMethod]
    public void InvalidReloadPreservesCatalogAndAlreadyResolvedTypes()
    {
        var world = new MemoryContext(Asset());
        var settings = new EmissionAssetCatalog(world.Assets, world.Logger);
        Assert.IsTrue(settings.LoadPatched());
        EmissionSelection before = settings.Resolve("game:candle", EmissionTarget.Item, null);
        long revision = settings.Revision; string? hash = settings.Fingerprint;
        world.Catalog.Text = "{broken";
        Assert.IsFalse(settings.LoadPatched());
        Assert.AreEqual(revision, settings.Revision); Assert.AreEqual(hash, settings.Fingerprint);
        Assert.AreSame(before, settings.Resolve("game:candle", EmissionTarget.Item, null));
        Assert.IsNotNull(settings.LastError);
    }

    [TestMethod]
    public void SettingsAreNotParsedAgainForEverySourceOrFrame()
    {
        var world = new MemoryContext(Asset()); var settings = new EmissionAssetCatalog(world.Assets, world.Logger);
        Assert.IsTrue(settings.LoadPatched());
        var attributes = new JsonObject(JObject.Parse("""{"vintageRtxEmission":{"profile":"vintagertx:candle"}}"""));
        EmissionSelection selection = settings.Resolve("other:lamp", EmissionTarget.Item, attributes);
        for (int i = 0; i < 10000; i++) Assert.AreSame(selection, settings.Resolve("other:lamp", EmissionTarget.Item, attributes));
        Assert.AreEqual(1, settings.ResolutionCount);
        EmissionSelection plain = settings.Resolve("game:candle", EmissionTarget.Item, null);
        for (int i = 0; i < 1000; i++) Assert.AreSame(plain, settings.Resolve("game:candle", EmissionTarget.Item, null));
        Assert.AreEqual(2, settings.ResolutionCount);
    }

    [TestMethod]
    public void BadAttributeIsRejectedOnceWithoutAFlameFallback()
    {
        var world = new MemoryContext(Asset()); var settings = new EmissionAssetCatalog(world.Assets, world.Logger);
        Assert.IsTrue(settings.LoadPatched());
        var attributes = new JsonObject(JObject.Parse("""{"vintageRtxEmission":{"profiel":"vintagertx:candle"}}"""));
        for (int i = 0; i < 100; i++) Assert.IsNull(settings.Resolve("game:candle", EmissionTarget.Block, attributes)
            .CreateLight(default, Vector3.One));
        Assert.AreEqual(1, settings.RejectedCount); Assert.AreEqual(1, settings.ResolutionCount);
    }

    [TestMethod]
    public void SuccessfulReloadInvalidatesTypeCacheButUnchangedBytesDoNot()
    {
        var world = new MemoryContext(Asset()); var settings = new EmissionAssetCatalog(world.Assets, world.Logger);
        Assert.IsTrue(settings.LoadPatched());
        EmissionSelection before = settings.Resolve("game:candle", EmissionTarget.Block, null);
        Assert.IsTrue(settings.LoadPatched());
        Assert.AreSame(before, settings.Resolve("game:candle", EmissionTarget.Block, null));
        JObject json = JObject.Parse(world.Catalog.Text);
        json["profiles"]!["vintagertx:candle"]!["amplitude"] = .01;
        world.Catalog.Text = json.ToString();
        Assert.IsTrue(settings.LoadPatched());
        Assert.AreEqual(2L, settings.Revision);
        Assert.AreEqual(.01, settings.Resolve("game:candle", EmissionTarget.Block, null).Profile.Amplitude);
        Assert.AreEqual(.12, before.Profile.Amplitude);
    }

    [TestMethod]
    public void OfficialCandleAndChandelierDefinitionsHaveMappedRuntimeFamilies()
    {
        EmissionCatalog catalog = EmissionCatalog.Parse(Asset());
        bool candle = false, chandelier = false;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(game, "assets"), "*.json", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (!normalized.Contains("/blocktypes/") && !normalized.Contains("/itemtypes/")) continue;
            string filename = Path.GetFileName(file);
            if (!filename.Contains("candle", StringComparison.OrdinalIgnoreCase)
                && !filename.Contains("chandelier", StringComparison.OrdinalIgnoreCase)) continue;
            JObject json = JObject.Parse(File.ReadAllText(file));
            string? code = json["code"]?.Value<string>();
            if (code is null) continue;
            if (code is "candle" or "candles" or "bunchocandles") candle = true;
            else if (code == "chandelier") chandelier = true;
            else continue;
            EmissionSelection selection = catalog.Resolve("game:" + code,
                normalized.Contains("/blocktypes/") ? EmissionTarget.Block : EmissionTarget.Item);
            Assert.AreEqual(EmissionKind.Flame, selection.Profile.Kind, file);
            Console.WriteLine("Verified family {0} in {1}", code, normalized[(normalized.IndexOf("/assets/") + 1)..]);
        }
        Assert.IsTrue(candle, "No official candle asset was examined.");
        Assert.IsTrue(chandelier, "No official chandelier asset was examined.");
    }

    private sealed class MemoryContext
    {
        private readonly Dictionary<string, MemoryAsset> entries = new(StringComparer.Ordinal);
        internal MemoryAsset Catalog { get; }
        internal ICoreClientAPI Api { get; }
        internal IAssetManager Assets { get; }
        internal ILogger Logger { get; }
        internal List<string> Errors { get; } = new();
        internal int Reads { get; private set; }
        internal MemoryContext(string json)
        {
            Logger = Stub.Create<ILogger>((method, args) =>
            {
                if (method.Name == "Error") Errors.Add(string.Join(" | ", args.Select(value => value?.ToString())));
                return Default(method.ReturnType);
            });
            Assets = Stub.Create<IAssetManager>((method, args) => method.Name switch
            {
                "TryGet" => Read((AssetLocation)args[0]!),
                "GetMany" => entries.Values.Where(value => value.Location.Path.StartsWith((string)args[0]!, StringComparison.Ordinal))
                    .Select(value => value.Asset).ToList(),
                _ => throw new NotSupportedException("Asset stub: " + method.Name)
            });
            IClientWorldAccessor world = Stub.Create<IClientWorldAccessor>((method, _) => method.Name switch
            {
                "get_Config" => new TreeAttribute(), "get_Logger" => Logger,
                _ => throw new NotSupportedException("World stub: " + method.Name)
            });
            Api = Stub.Create<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_Assets" => Assets, "get_Logger" => Logger, "get_World" => world,
                "get_Side" => EnumAppSide.Client,
                "get_ModLoader" => Stub.Create(method.ReturnType, (member, _) => member.Name == "get_Mods"
                    ? Array.CreateInstance(member.ReturnType.IsArray ? member.ReturnType.GetElementType()! : member.ReturnType.GetGenericArguments()[0], 0)
                    : throw new NotSupportedException("ModLoader stub: " + member.Name)),
                _ => throw new NotSupportedException("API stub: " + method.Name)
            });
            Catalog = Add(EmissionAssetCatalog.AssetName, json);
        }
        internal MemoryAsset Add(string location, string text)
        { var asset = new MemoryAsset(location, text); entries.Add(asset.Location.ToString(), asset); return asset; }
        private IAsset? Read(AssetLocation location)
        { Reads++; return entries.TryGetValue(location.ToString(), out MemoryAsset? asset) ? asset.Asset : null; }
    }
    private sealed class MemoryAsset
    {
        private byte[] data;
        internal AssetLocation Location { get; }
        internal IAsset Asset { get; }
        internal bool IsPatched { get; private set; }
        internal string Text { get => Encoding.UTF8.GetString(data); set => data = Encoding.UTF8.GetBytes(value); }
        internal MemoryAsset(string location, string text)
        {
            Location = new(location); data = Encoding.UTF8.GetBytes(text);
            Asset = Stub.Create<IAsset>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_Location": return Location;
                    case "get_Data": return data;
                    case "set_Data": data = (byte[])args[0]!; return null;
                    case "get_IsPatched": return IsPatched;
                    case "set_IsPatched": IsPatched = (bool)args[0]!; return null;
                    case "ToText": return Text;
                    case "ToObject": return JsonConvert.DeserializeObject(Text, method.GetGenericArguments()[0],
                        args.FirstOrDefault() as JsonSerializerSettings);
                    default: throw new NotSupportedException("IAsset stub: " + method.Name);
                }
            });
        }
    }
    private static object? Default(Type type) => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
}

/// <summary>Explicitly routed interfaces; unexpected world reads fail the test rather than being hidden.</summary>
public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Route { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Route(targetMethod!, args ?? []);
    public static T Create<T>(Func<MethodInfo, object?[], object?> route) where T : class => (T)Create(typeof(T), route);
    public static object Create(Type type, Func<MethodInfo, object?[], object?> route)
    { object value = DispatchProxy.Create(type, typeof(Stub)); ((Stub)value).Route = route; return value; }
}
