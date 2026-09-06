using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>Covers the entity PBR bridge paths that do not require a live OpenGL context.</summary>
[TestClass]
public sealed class PbrEntityRendererCoverageTests
{
    /// <summary>Exercises renderer metadata, waiting-atlas behavior, reload, and zero-handle disposal.</summary>
    [TestMethod]
    public void WaitingAtlasFrameRemainsSafeAndReloadable()
    {
        List<string> logs = [];
        ILogger logger = Logger(logs);
        ITextureAtlasAPI atlas = Atlas([], [], new TextureAtlasPosition());
        PbrEntityRenderer renderer = Renderer(atlas, EmptyAssets(), EmptyStore(logger), logger);

        Assert.AreEqual(0.365, renderer.RenderOrder);
        Assert.AreEqual(0, renderer.RenderRange);
        Assert.AreEqual("waiting for entity atlas", renderer.Status);

        renderer.OnRenderFrame(0.1f, EnumRenderStage.AfterFinalComposition);
        renderer.OnRenderFrame(0.1f, EnumRenderStage.Opaque);
        Assert.AreEqual("waiting for entity atlas", renderer.Status);
        renderer.OnRenderFrame(0.1f, EnumRenderStage.Opaque);
        Assert.AreEqual(0, logs.Count);

        Assert.IsTrue(renderer.ReloadShader());
        SetField(renderer, "loaded", true);
        Assert.IsTrue(renderer.ReloadShader());
        StringAssert.Contains(renderer.Status, "shader reload pending");
        SetField(renderer, "loaded", false);
        renderer.Dispose();
    }

    /// <summary>Reports unsafe multi-page ownership once and keeps all GPU state untouched.</summary>
    [TestMethod]
    public void UnsafeEntityAtlasProducesEntitySpecificStatusAndWarning()
    {
        List<string> logs = [];
        ILogger logger = Logger(logs);
        ICoreClientAPI textureApi = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>(Default);
        List<LoadedTexture> pages =
        [
            new LoadedTexture(textureApi) { TextureId = 10, Width = 16, Height = 16 },
            new LoadedTexture(textureApi) { TextureId = 11, Width = 16, Height = 16 }
        ];
        PbrEntityRenderer renderer = Renderer(
            Atlas(pages, [], new TextureAtlasPosition()),
            EmptyAssets(),
            EmptyStore(logger),
            logger);

        try
        {
            renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
            StringAssert.Contains(renderer.Status, "entity atlas exposes 2 pages");
            Assert.AreEqual(1, logs.Count);

            renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
            Assert.AreEqual(1, logs.Count);
            renderer.Dispose();
        }
        finally
        {
            // These handles model atlas pages owned by the game. They are not real test-owned
            // OpenGL allocations, so their LoadedTexture finalizers must never delete them.
            foreach (LoadedTexture page in pages)
            {
                page.TextureId = 0;
                GC.SuppressFinalize(page);
            }
        }
    }

    /// <summary>Makes an opaque-frame dependency failure sticky until the explicit reload boundary.</summary>
    [TestMethod]
    public void OpaqueFrameFailureIsStickyUntilReload()
    {
        int errors = 0;
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
        {
            if (method.Name == nameof(ILogger.Error))
            {
                errors++;
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name switch
            {
                "get_EntityTextureAtlas" => throw new InvalidOperationException("entity atlas failure"),
                "get_Logger" => logger,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        PbrEntityRenderer renderer = new(api, EmptyStore(logger));

        renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
        Assert.AreEqual("faulted: entity atlas failure", renderer.Status);
        Assert.AreEqual(1, errors);
        renderer.OnRenderFrame(0, EnumRenderStage.Opaque);
        Assert.AreEqual(1, errors);

        Assert.IsTrue(renderer.ReloadShader());
        Assert.AreEqual("waiting for entity atlas", renderer.Status);
        renderer.Dispose();
    }

    /// <summary>Scans invalid, missing-albedo, and unmapped sidecars without attempting an upload.</summary>
    [TestMethod]
    public void SidecarScanRejectsEveryPreUploadMismatch()
    {
        ILogger logger = Logger([]);
        Dictionary<AssetLocation, IAsset> catalog = new()
        {
            [new AssetLocation("game:textures/block/stone_n.png")] = Asset(),
            [new AssetLocation("game:textures/entity/missing_n.png")] = Asset(),
            [new AssetLocation("game:textures/entity/unmapped_n.png")] = Asset(),
            [new AssetLocation("game:textures/entity/unmapped.png")] = Asset()
        };
        IAssetManager assets = AssetManager(catalog);
        PbrSidecarAssetStore store = PbrSidecarAssetStore.Capture(assets, logger);
        TextureAtlasPosition unknown = new();
        ITextureAtlasAPI atlas = Atlas([], [], unknown, _ => unknown);
        PbrEntityRenderer renderer = Renderer(atlas, assets, store, logger);
        SetField(renderer, "sourceAtlasTexture", 42);

        int applied = (int)Invoke(renderer, "ApplySidecarOverrides", atlas)!;

        Assert.AreEqual(0, applied);
        renderer.Dispose();
    }

    /// <summary>Reaches entity upload preparation and contains malformed normal-map data before OpenGL.</summary>
    [TestMethod]
    public void MappedSidecarRejectsMalformedPngBeforeGpuMutation()
    {
        ILogger logger = Logger([]);
        AssetLocation normalLocation = new("game:textures/entity/wolf/body_n.png");
        AssetLocation sourceLocation = new("game:textures/entity/wolf/body.png");
        Dictionary<AssetLocation, IAsset> catalog = new()
        {
            [normalLocation] = Asset(),
            [sourceLocation] = Asset()
        };
        IAssetManager assets = AssetManager(catalog);
        PbrSidecarAssetStore store = PbrSidecarAssetStore.Capture(assets, logger);
        TextureAtlasPosition unknown = new();
        TextureAtlasPosition mapped = new()
        {
            atlasTextureId = 42,
            atlasNumber = 0,
            x1 = 0.25f,
            y1 = 0.25f,
            x2 = 0.5f,
            y2 = 0.5f
        };
        ITextureAtlasAPI atlas = Atlas([], [], unknown, _ => mapped);
        PbrEntityRenderer renderer = Renderer(atlas, assets, store, logger);
        SetField(renderer, "sourceAtlasTexture", 42);
        SetField(renderer, "sourceAtlasWidth", 16);
        SetField(renderer, "sourceAtlasHeight", 16);

        TargetInvocationException failure = Assert.ThrowsException<TargetInvocationException>(() =>
            Invoke(renderer, "ApplySidecarOverrides", atlas));

        Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        renderer.Dispose();
    }

    /// <summary>Creates an entity renderer with shader lookup deliberately unavailable.</summary>
    /// <param name="atlas">Entity atlas exposed by the API.</param>
    /// <param name="assets">Asset manager exposed by the API.</param>
    /// <param name="store">Sidecar index supplied to the renderer.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <returns>An isolated renderer instance.</returns>
    private static PbrEntityRenderer Renderer(
        ITextureAtlasAPI atlas,
        IAssetManager assets,
        PbrSidecarAssetStore store,
        ILogger logger)
    {
        IShaderAPI shader = RuntimeCoverageDispatchProxy.Create<IShaderAPI>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name switch
            {
                "get_EntityTextureAtlas" => atlas,
                "get_Assets" => assets,
                "get_Logger" => logger,
                "get_Shader" => shader,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        return new PbrEntityRenderer(api, store);
    }

    /// <summary>Creates a deterministic entity atlas.</summary>
    /// <param name="textures">Owned texture pages.</param>
    /// <param name="positions">Indexed atlas rectangles.</param>
    /// <param name="unknown">Missing-texture sentinel.</param>
    /// <param name="lookup">Optional atlas identity lookup.</param>
    /// <returns>An isolated atlas double.</returns>
    private static ITextureAtlasAPI Atlas(
        List<LoadedTexture> textures,
        TextureAtlasPosition[] positions,
        TextureAtlasPosition unknown,
        System.Func<AssetLocation, TextureAtlasPosition>? lookup = null) =>
        RuntimeCoverageDispatchProxy.Create<ITextureAtlasAPI>((method, arguments) => method.Name switch
        {
            "get_AtlasTextures" => textures,
            "get_Positions" => positions,
            "get_UnknownTexturePosition" => unknown,
            "get_Item" => lookup?.Invoke((AssetLocation)arguments![0]!) ?? unknown,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>Creates an indexed store with no sidecars.</summary>
    /// <param name="logger">Diagnostic sink.</param>
    /// <returns>An empty immutable sidecar view.</returns>
    private static PbrSidecarAssetStore EmptyStore(ILogger logger) =>
        PbrSidecarAssetStore.Capture(EmptyAssets(), logger);

    /// <summary>Creates an empty asset manager.</summary>
    /// <returns>An asset manager with an empty shared catalog.</returns>
    private static IAssetManager EmptyAssets() => AssetManager([]);

    /// <summary>Creates an asset manager around the supplied mutable catalog.</summary>
    /// <param name="catalog">Assets visible to lookups and sidecar capture.</param>
    /// <returns>A deterministic asset manager.</returns>
    private static IAssetManager AssetManager(Dictionary<AssetLocation, IAsset> catalog) =>
        RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) => method.Name switch
        {
            "get_AllAssets" => catalog,
            "TryGet" => catalog.TryGetValue((AssetLocation)arguments![0]!, out IAsset? asset) ? asset : null,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>Creates a loaded binary asset.</summary>
    /// <returns>A deterministic loaded asset.</returns>
    private static IAsset Asset() => RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) => method.Name switch
    {
        "get_Data" => new byte[] { 1 },
        "IsLoaded" => true,
        _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
    });

    /// <summary>Creates a logger that records formatted arguments as searchable text.</summary>
    /// <param name="logs">Destination list.</param>
    /// <returns>A deterministic logger.</returns>
    private static ILogger Logger(List<string> logs) =>
        RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Warning) or nameof(ILogger.Error))
            {
                logs.Add(string.Join("|", arguments ?? []));
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

    /// <summary>Returns the CLR default for a proxy method.</summary>
    /// <param name="method">Invoked interface member.</param>
    /// <param name="arguments">Ignored arguments.</param>
    /// <returns>The member's default return value.</returns>
    private static object? Default(MethodInfo method, object?[]? arguments) =>
        RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);

    /// <summary>Invokes a non-public renderer operation.</summary>
    /// <param name="instance">Renderer under test.</param>
    /// <param name="name">Method name.</param>
    /// <param name="arguments">Method arguments.</param>
    /// <returns>The boxed method result.</returns>
    private static object? Invoke(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments);

    /// <summary>Assigns one non-public renderer field.</summary>
    /// <param name="instance">Renderer under test.</param>
    /// <param name="name">Field name.</param>
    /// <param name="value">Assigned value.</param>
    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}
