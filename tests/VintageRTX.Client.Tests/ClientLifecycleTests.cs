using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Client;
using VintageRTX.Core.Lighting;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Client.Tests;

/// <summary>Public API events drive the real observer. Only the world provider and host draw are controlled.</summary>
[TestClass, DoNotParallelize]
public sealed class ClientLifecycleTests
{
    public static void Initialize(TestContext? _) { }
    private static NativeWindow Context()
    {
        GLFWProvider.CheckForMainThread = false;
        var w = new NativeWindow(new NativeWindowSettings {ClientSize = new(8, 8), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new(3, 3), Profile = ContextProfile.Core});
        w.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext()); return w;
    }
    [TestMethod]
    public void ObserverPublishesNearbyEditsBeforeDiscoveryCompletesAndExtinguishesOnTheNextFrame()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(ClientLifecycleTests), nameof(ObserverPublishesNearbyEditsBeforeDiscoveryCompletesAndExtinguishesOnTheNextFrame))) return;
        using var gl = Context(); var host = new LifecycleHost();
        var assets = new EmissionAssetCatalog(host.Assets, host.Logger); Assert.IsTrue(assets.LoadPatched());
        using var observer = new ClientSourceObserver(host.Api, assets);
        Assert.AreEqual(0d, observer.RenderOrder); Assert.AreEqual(0, observer.RenderRange);
        Assert.IsNull(observer.CurrentFrame); observer.OnRenderFrame(0, EnumRenderStage.Before);
        host.HasWorld = false; host.Tick(); host.HasWorld = true; host.HasPlayer = false; host.Tick(); host.HasPlayer = true;
        host.Tick();
        BlockPos pos = new(4, 4, 5, 0);
        var candle = new SourceBlock {Code = new("game:bunchocandles-3"), BlockId = 42, Emission = [5, 7, 16]};
        host.Set(pos, candle); host.Raise("BlockChanged", pos, host.Air); host.Tick();
        observer.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.IsNull(observer.CurrentFrame);
        observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        Assert.IsTrue(observer.CurrentFrame.Samples[0].Intensity.X > 0);
        StringAssert.Contains(observer.Describe(), "watched blocks=1");
        LightFrame frozen = observer.CurrentFrame;
        candle.Emission = [5, 7, 0]; host.Raise("BlockChanged", pos, candle); host.Tick(); host.Milliseconds++;
        observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        Assert.AreEqual(1, frozen.Samples.Length);
        candle.Emission = [5, 7, 16]; host.Tick(); host.Milliseconds++;
        observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        candle.Code = new("game:bunchocandles-8"); host.Raise("BlockChanged", pos, candle); host.Tick();
        host.Set(pos, host.Air); host.Raise("BlockChanged", pos, candle); host.Tick(); host.Milliseconds++;
        observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [TestMethod]
    public void EntityMovementDropsAndRuntimeFailuresCannotLeaveGhostSources()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(ClientLifecycleTests), nameof(EntityMovementDropsAndRuntimeFailuresCannotLeaveGhostSources))) return;
        using var gl = Context(); var host = new LifecycleHost(); var assets = new EmissionAssetCatalog(host.Assets, host.Logger);
        assets.LoadPatched(); using var observer = new ClientSourceObserver(host.Api, assets); host.Tick();
        var entity = new SourceEntity {Code = new("game:locust-bronze"), EntityId = 10, Emission = [4, 7, 16]};
        entity.Pos.SetPos(5, 4, 4); host.Entities![10] = entity;
        host.Tick(); observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        entity.Pos.X = 100; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        entity.Pos.X = 5; entity.Pos.Dimension = 1; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        entity.Pos.Dimension = 0; entity.Fail = true; host.Tick(); Assert.IsTrue(host.Messages.Any(s => s.Contains("source-failure")));
        entity.Fail = false; host.Entities[10] = null!; host.Tick();
        var item = new SourceItem {Code = new("game:candle"), ItemId = 43, Emission = [5, 7, 16]};
        var drop = new EntityItem {EntityId = 11, Itemstack = new ItemStack(item, 3)};
        drop.Pos.SetPos(5, 4, 4); host.Entities[11] = drop;
        host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        drop.Itemstack.StackSize = 0; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
        drop.Itemstack = null!; host.Tick(); host.Entities = null; host.Tick();
        host.Entities = new(); host.Entities[10] = entity; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(1, observer.CurrentFrame!.Samples.Length);
        host.Entities = null; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreEqual(0, observer.CurrentFrame!.Samples.Length);
    }

    [TestMethod]
    public void WorldTransitionsAssetReloadAndDisposalDetachCallbacksAndInvalidateOldFrames()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(ClientLifecycleTests), nameof(WorldTransitionsAssetReloadAndDisposalDetachCallbacksAndInvalidateOldFrames))) return;
        using var gl = Context(); var host = new LifecycleHost(); var assets = new EmissionAssetCatalog(host.Assets, host.Logger);
        assets.LoadPatched(); var observer = new ClientSourceObserver(host.Api, assets); host.Tick();
        Delegate staleBlockCallback = host.Events["BlockChanged"]!;
        Delegate staleChunkCallback = host.Events["ChunkDirty"]!;
        observer.OnRenderFrame(0, EnumRenderStage.Before); var first = observer.CurrentFrame!;
        host.Raise("ChunkDirty", new Vec3i(0, 0, 0), host.Chunk, default(EnumChunkDirtyReason));
        host.Raise("BlockChanged", new BlockPos(4, 4, 4, 1), host.Air); host.Tick();
        host.Raise("ReloadTextures"); host.Tick(); host.Raise("ReloadShapes"); host.Tick();
        host.Player.Pos.Dimension = 1; host.Tick(); host.Milliseconds++; observer.OnRenderFrame(0, EnumRenderStage.Before);
        Assert.AreNotEqual(first.World, observer.CurrentFrame!.World);
        host.Player.Pos.X = 24; host.Tick();
        host.Raise("LeaveWorld"); Assert.IsNull(observer.CurrentFrame);
        StringAssert.Contains(observer.Describe(), "Sources=0"); host.Tick();
        observer.Dispose(); observer.Dispose();
        Assert.IsTrue(host.Events.Values.All(d => d is null)); Assert.AreEqual(0, host.Renderers.Count);
        staleBlockCallback.DynamicInvoke(new BlockPos(1, 1, 1, 0), host.Air);
        staleChunkCallback.DynamicInvoke(new Vec3i(), host.Chunk, default(EnumChunkDirtyReason));
        host.Tick(); observer.OnRenderFrame(0, EnumRenderStage.Before); Assert.IsNull(observer.CurrentFrame);
    }

    [TestMethod]
    public void LabRemainsOptInAndCanRecoverFromShaderFailureWithoutAWorldImageWrite()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(ClientLifecycleTests), nameof(LabRemainsOptInAndCanRecoverFromShaderFailureWithoutAWorldImageWrite))) return;
        using var gl = Context(); var host = new LifecycleHost(); var assets = new EmissionAssetCatalog(host.Assets, host.Logger); assets.LoadPatched();
        var lab = new DirectLightLabRenderer(host.Api, assets);
        Assert.AreEqual(.98, lab.RenderOrder); Assert.AreEqual(0, lab.RenderRange);
        lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.AreEqual(0, host.Draws);
        StringAssert.Contains(lab.Configure("unknown"), "Utilisation");
        lab.Configure("ON"); host.HasWorld = false; lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.AreEqual(0, host.Draws);
        host.HasWorld = true; lab.OnRenderFrame(0, EnumRenderStage.Before); Assert.AreEqual(0, host.Draws);
        host.BadShader = true; lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.IsNotNull(lab.LastError); Assert.AreEqual(0, host.Draws);
        host.BadShader = false; lab.Configure("lit"); host.Milliseconds = 2000; lab.OnRenderFrame(0, EnumRenderStage.Ortho);
        Assert.IsNull(lab.LastError, lab.LastError); Assert.AreEqual(1, host.Draws);
        lab.Configure("dark"); host.Milliseconds++; lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.AreEqual(2, host.Draws);
        lab.Configure("off"); lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.AreEqual(2, host.Draws);
        lab.Configure("on"); host.Raise("LeaveWorld"); lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.AreEqual(2, host.Draws);
        lab.Configure("on"); host.Milliseconds++; lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.AreEqual(3, host.Draws);
        lab.Dispose(); lab.Dispose(); StringAssert.Contains(lab.Configure("on"), "arrêté");
        lab.OnRenderFrame(0, EnumRenderStage.Ortho); Assert.AreEqual(3, host.Draws); Assert.AreEqual(0, host.Renderers.Count);
    }

    private sealed class SourceBlock : Block
    {
        public byte[]? Emission;
        public override byte[] GetLightHsv(IBlockAccessor accessor, BlockPos pos, ItemStack stack = null!) => Emission!;
    }
    private sealed class SourceItem : Item
    {
        public byte[]? Emission;
        public override byte[] GetLightHsv(IBlockAccessor accessor, BlockPos pos, ItemStack stack = null!) => Emission!;
    }
    private sealed class SourceEntity : EntityPlayer
    {
        public byte[]? Emission;
        public bool Fail;
        public override byte[] LightHsv => Fail ? throw new InvalidOperationException("source-failure") : Emission!;
    }
}

internal sealed class LifecycleHost
{
    internal readonly Dictionary<string, Delegate?> Events = new();
    internal readonly List<IRenderer> Renderers = new();
    internal readonly List<string> Messages = new();
    internal readonly Dictionary<(int, int, int, int), Block> Blocks = new();
    internal readonly Block Air = new() {BlockId = 0, Code = new("game:air")};
    internal readonly EntityPlayer Player = new();
    internal Dictionary<long, Entity>? Entities = new();
    internal bool HasWorld = true, HasPlayer = true, Loaded = true, BadShader;
    internal long Milliseconds = 1000;
    internal int Draws;
    internal ICoreClientAPI Api {get;}
    internal IClientEventAPI Event {get;}
    internal IAssetManager Assets {get;}
    internal ILogger Logger {get;}
    internal IWorldChunk Chunk {get;}
    private Action<float>? tick;
    internal LifecycleHost()
    {
        Player.Pos.SetPos(4, 4, 4);
        Logger = Stub.Create<ILogger>((m, a) => {Messages.Add(string.Join(" ", a.Select(x => x is object[] v ? string.Join(" ", v.Select(z => z?.ToString())) : x?.ToString()))); return Default(m.ReturnType);});
        Chunk = Stub.Create<IWorldChunk>((m, _) => Default(m.ReturnType));
        Event = Stub.Create<IClientEventAPI>((m, a) => {
            if(m.Name.StartsWith("add_", StringComparison.Ordinal)) {string key = m.Name[4..]; Events.TryGetValue(key, out var old); Events[key] = Delegate.Combine(old, (Delegate)a[0]!); return null;}
            if(m.Name.StartsWith("remove_", StringComparison.Ordinal)) {string key = m.Name[7..]; Events.TryGetValue(key, out var old); Events[key] = Delegate.Remove(old, (Delegate)a[0]!); return null;}
            if(m.Name == "RegisterGameTickListener") {tick = (Action<float>)a[0]!; return 42L;}
            if(m.Name == "RegisterRenderer") {Renderers.Add((IRenderer)a[0]!); return null;}
            if(m.Name == "UnregisterRenderer") {Renderers.Remove((IRenderer)a[0]!); return null;}
            return Default(m.ReturnType);
        });
        Assets = Stub.Create<IAssetManager>((m, a) => {
            if(m.Name is not ("Get" or "TryGet")) return Default(m.ReturnType);
            var location = (AssetLocation)a[0]!;
            string file = location.Path == "config/emission.json" ? Path.Combine(AppContext.BaseDirectory, "Fixtures", "emission.json")
                : Path.Combine(AppContext.BaseDirectory, Path.GetFileName(location.Path));
            string text = BadShader && location.Path.EndsWith("direct-image.glsl", StringComparison.Ordinal) ? "this is not a shader" : File.ReadAllText(file);
            return Stub.Create<IAsset>((member, _) => member.Name switch {"ToText" => text, "get_Data" => Encoding.UTF8.GetBytes(text), "get_Location" => location, _ => Default(member.ReturnType)});
        });
        IBlockAccessor accessor = Stub.Create<IBlockAccessor>((m, a) => {
            if(m.Name == "GetChunkAtBlockPos") return Loaded ? Chunk : null;
            if(m.Name == "GetBlock") {BlockPos p = (BlockPos)a[0]!; int layer = a.Length > 1 ? (int)a[1]! : BlockLayersAccess.Solid;
                return layer == BlockLayersAccess.Fluid ? Air : Blocks.GetValueOrDefault((p.dimension, p.X, p.Y, p.Z), Air);}
            return Default(m.ReturnType);
        });
        IClientPlayer clientPlayer = OfficialPlayerFixture.Create(Player);
        IClientWorldAccessor world = Stub.Create<IClientWorldAccessor>((m, _) => m.Name switch {
            "get_Player" => HasPlayer ? clientPlayer : null, "get_LoadedEntities" => Entities,
            "get_BlockAccessor" => accessor, "get_Logger" => Logger, "get_Config" => new TreeAttribute(), _ => Default(m.ReturnType)});
        var render = Stub.Create<IRenderAPI>((m, _) => {if(m.Name == "RenderTexture") {Draws++; return null;} return m.Name == "get_FrameWidth" ? 640 : Default(m.ReturnType);});
        Api = Stub.Create<ICoreClientAPI>((m, _) => m.Name switch {"get_Event" => Event, "get_World" => HasWorld ? world : null,
            "get_Assets" => Assets, "get_Logger" => Logger, "get_Side" => EnumAppSide.Client,
            "get_InWorldEllapsedMilliseconds" => Milliseconds, "get_Render" => render, _ => Default(m.ReturnType)});
    }
    internal void Set(BlockPos p, Block b) => Blocks[(p.dimension, p.X, p.Y, p.Z)] = b;
    internal void Tick() => tick?.Invoke(.02f);
    internal void Raise(string name, params object?[] args)
    {
        if(!Events.TryGetValue(name, out var callback) || callback is null) return;
        try {callback.DynamicInvoke(args);} catch(TargetInvocationException e) when(e.InnerException is not null) {ExceptionDispatchInfo.Capture(e.InnerException).Throw();}
    }
    private static object? Default(Type type) => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
}
