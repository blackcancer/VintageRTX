using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>
/// Completes the PBR atlas renderer contracts against a real hidden OpenGL context. Vintage Story
/// atlas, shader-registry, asset, world, and logging services remain deterministic interface proxies.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CoveragePbrRendererGpuTests
{
    /// <summary>Binding flags used for private instance seams exercised by the coverage fixture.</summary>
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    /// <summary>Binding flags used for private static seams exercised by the coverage fixture.</summary>
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    /// <summary>Forced SKBitmap decode result: zero keeps production decoding, one returns null, two an empty bitmap.</summary>
    private static int forcedDecodeResult;

    /// <summary>Forces all suppressed native-resource finalizers to complete between fixture methods.</summary>
    [TestCleanup]
    public void CollectPendingNativeFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Builds, binds, reuses, rebuilds, invalidates, and deletes the entity material atlas while
    /// uploading both complete and normal-only sidecar suites through real OpenGL texture storage.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("PBR")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void EntityRendererExercisesRealAtlasUploadsReloadsAndShaderStates()
    {
        RunWithContext("VintageRTX.PbrEntity.Coverage", () =>
        {
            List<int> textures = [];
            List<int> programs = [];
            List<string> logs = [];
            List<LoadedTexture> loadedTextures = [];
            TextureAtlasPosition unknown = new();
            int sourceTexture = CreateTexture(8, 8, textures);
            TextureAtlasPosition wolf = Position(sourceTexture, 0, 0f, 0f, 0.5f, 1f);
            TextureAtlasPosition hare = Position(sourceTexture, 0, 0.5f, 0f, 1f, 1f);
            List<LoadedTexture> pages = [Loaded(sourceTexture, 8, 8, loadedTextures)];
            Dictionary<AssetLocation, TextureAtlasPosition> placements = new()
            {
                [new AssetLocation("game:entity/wolf")] = wolf,
                [new AssetLocation("game:entity/hare")] = hare
            };
            ITextureAtlasAPI atlas = EntityAtlas(pages, [wolf, hare], unknown, placements);
            byte[] normal = Png(new SKColor(128, 128, 255, 255));
            byte[] scalar = Png(new SKColor(90, 90, 90, 255));
            Dictionary<AssetLocation, IAsset> catalog = new()
            {
                [new AssetLocation("game:textures/entity/wolf.png")] = Asset("game:textures/entity/wolf.png", normal),
                [new AssetLocation("game:textures/entity/wolf_n.png")] = Asset("game:textures/entity/wolf_n.png", normal),
                [new AssetLocation("game:textures/entity/wolf_r.png")] = Asset("game:textures/entity/wolf_r.png", scalar),
                [new AssetLocation("game:textures/entity/wolf_m.png")] = Asset("game:textures/entity/wolf_m.png", scalar),
                [new AssetLocation("game:textures/entity/wolf_e.png")] = Asset("game:textures/entity/wolf_e.png", scalar),
                [new AssetLocation("game:textures/entity/hare.png")] = Asset("game:textures/entity/hare.png", normal),
                [new AssetLocation("game:textures/entity/hare_n.png")] = Asset("game:textures/entity/hare_n.png", normal)
            };
            IAssetManager assets = Assets(catalog);
            ILogger logger = Logger(logs);
            PbrSidecarAssetStore store = PbrSidecarAssetStore.Capture(assets, logger);
            ShaderHolder shaders = new()
            {
                Program = ShaderProgram(CreateProgram(true, true, true, true, programs))
            };
            ICoreClientAPI api = ClientApi(atlas, null, assets, logger, shaders);
            PbrEntityRenderer renderer = new(api, store);
            try
            {
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                StringAssert.Contains(renderer.Status, "sidecars=2");
                int materialTexture = GetField<int>(renderer, "materialAtlasTexture");
                Assert.IsTrue(GL.IsTexture(materialTexture));

                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.AreEqual(materialTexture, GetField<int>(renderer, "materialAtlasTexture"));

                ulong entityRevision = GetField<ulong>(renderer, "sourceAtlasRevision");
                wolf.reloadIteration++;
                for (int frame = 0; frame < PbrTerrainRenderer.AtlasRevisionStabilityFrames; frame++)
                {
                    renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                }
                int rebuiltTexture = GetField<int>(renderer, "materialAtlasTexture");
                Assert.IsTrue(GL.IsTexture(rebuiltTexture));
                Assert.AreNotEqual(entityRevision, GetField<ulong>(renderer, "sourceAtlasRevision"));

                int secondSource = CreateTexture(8, 8, textures);
                LoadedTexture secondPage = Loaded(secondSource, 8, 8, loadedTextures);
                pages.Add(secondPage);
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.AreEqual(0, GetField<int>(renderer, "materialAtlasTexture"));
                StringAssert.Contains(renderer.Status, "entity atlas exposes 2 pages");

                pages.Remove(secondPage);
                wolf.reloadIteration++;
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.IsTrue(GL.IsTexture(GetField<int>(renderer, "materialAtlasTexture")));
                Assert.IsTrue(renderer.ReloadShader());
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);

                ExerciseEntityShaderVariants(renderer, shaders, programs, logs);

                renderer.Dispose();
                renderer.Dispose();
                Assert.AreEqual(0, GetField<int>(renderer, "materialAtlasTexture"));

                SetField(renderer, "sourceAtlasWidth", 0);
                SetField(renderer, "sourceAtlasHeight", 8);
                TargetInvocationException incomplete = Assert.ThrowsException<TargetInvocationException>(() =>
                    Invoke(renderer, "CreateNeutralAtlas"));
                Assert.IsInstanceOfType<InvalidOperationException>(incomplete.InnerException);
            }
            finally
            {
                renderer.Dispose();
                ReleaseLoadedTextures(loadedTextures);
                DeletePrograms(programs);
                DeleteTextures(textures);
                DrainGlErrors();
            }
        });
    }

    /// <summary>
    /// Drives terrain allocation, shader enable/disable, atlas reload safety, suffix sidecar upload,
    /// incomplete-framebuffer cleanup, and non-zero/zero disposal on the real driver.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("PBR")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void TerrainRendererExercisesRealAtlasUploadsReloadsAndShaderStates()
    {
        RunWithContext("VintageRTX.PbrTerrain.Coverage", () =>
        {
            List<int> textures = [];
            List<int> programs = [];
            List<string> logs = [];
            List<LoadedTexture> loadedTextures = [];
            TextureAtlasPosition unknown = new();
            int sourceTexture = CreateTexture(8, 8, textures);
            TextureAtlasPosition stone = Position(sourceTexture, 0, 0f, 0f, 0.5f, 1f);
            TextureAtlasPosition soil = Position(sourceTexture, 0, 0.5f, 0f, 1f, 1f);
            List<LoadedTexture> pages = [Loaded(sourceTexture, 8, 8, loadedTextures)];
            IBlockTextureAtlasAPI atlas = BlockAtlas(pages, [stone, soil], unknown);
            byte[] normal = Png(new SKColor(128, 128, 255, 255));
            byte[] scalar = Png(new SKColor(170, 170, 170, 255));
            Dictionary<AssetLocation, IAsset> catalog = new()
            {
                [new AssetLocation("mod:textures/block/stone_n.png")] = Asset("mod:textures/block/stone_n.png", normal),
                [new AssetLocation("mod:textures/block/stone_r.png")] = Asset("mod:textures/block/stone_r.png", scalar),
                [new AssetLocation("mod:textures/block/stone_m.png")] = Asset("mod:textures/block/stone_m.png", scalar),
                [new AssetLocation("mod:textures/block/stone_e.png")] = Asset("mod:textures/block/stone_e.png", scalar),
                [new AssetLocation("mod:textures/block/soil_n.png")] = Asset("mod:textures/block/soil_n.png", normal)
            };
            IAssetManager assets = Assets(catalog);
            ILogger logger = Logger(logs);
            PbrSidecarAssetStore store = PbrSidecarAssetStore.Capture(assets, logger);
            ShaderHolder shaders = new()
            {
                Program = ShaderProgram(CreateProgram(true, true, true, true, programs))
            };
            ICoreClientAPI api = ClientApi(null, atlas, assets, logger, shaders);
            PbrTerrainRenderer renderer = new(api, store);
            try
            {
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                int materialTexture = GetField<int>(renderer, "materialAtlasTexture");
                Assert.IsTrue(GL.IsTexture(materialTexture));

                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                ulong terrainRevision = GetField<ulong>(renderer, "sourceAtlasRevision");
                stone.reloadIteration++;
                for (int frame = 0; frame < PbrTerrainRenderer.AtlasRevisionStabilityFrames; frame++)
                {
                    renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                }
                Assert.IsTrue(GL.IsTexture(GetField<int>(renderer, "materialAtlasTexture")));
                Assert.AreNotEqual(terrainRevision, GetField<ulong>(renderer, "sourceAtlasRevision"));

                Dictionary<AssetLocation, TextureAtlasPosition[]> lookup = new()
                {
                    [new AssetLocation("mod:textures/block/stone.png")] = [stone],
                    [new AssetLocation("mod:textures/block/soil.png")] = [soil]
                };
                SetField(renderer, "sourceAtlasPositions", lookup);
                SetField(renderer, "ambiguousAtlasSources", new HashSet<AssetLocation>());
                Assert.AreEqual(2, Invoke(renderer, "ApplySidecarOverrides",
                    new HashSet<AssetLocation>(),
                    new Dictionary<AssetLocation, HashSet<string>>(),
                    new HashSet<AssetLocation>()));
                Assert.AreEqual(2, Invoke(renderer, "ApplySidecarOverrides",
                    new HashSet<AssetLocation>(),
                    new Dictionary<AssetLocation, HashSet<string>>(),
                    new HashSet<AssetLocation>(lookup.Keys)));

                int secondSource = CreateTexture(8, 8, textures);
                LoadedTexture secondPage = Loaded(secondSource, 8, 8, loadedTextures);
                pages.Add(secondPage);
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.AreEqual(0, GetField<int>(renderer, "materialAtlasTexture"));
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.AreEqual(0, GetField<int>(renderer, "materialAtlasTexture"));
                pages.Remove(secondPage);
                pages.Clear();
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.AreEqual("waiting for terrain atlas", renderer.Status);
                pages.Add(Loaded(sourceTexture, 8, 8, loadedTextures));
                stone.reloadIteration++;
                renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.IsTrue(GL.IsTexture(GetField<int>(renderer, "materialAtlasTexture")));

                ExerciseTerrainShaderVariants(renderer, shaders, programs, logs);

                renderer.Dispose();
                renderer.Dispose();
                SetField(renderer, "sourceAtlasWidth", 0);
                SetField(renderer, "sourceAtlasHeight", 8);
                TargetInvocationException incomplete = Assert.ThrowsException<TargetInvocationException>(() =>
                    Invoke(renderer, "CreateNeutralAtlas", new[] { 0.5f, 0.5f, 0.5f, 0f }));
                Assert.IsInstanceOfType<InvalidOperationException>(incomplete.InnerException);
            }
            finally
            {
                renderer.Dispose();
                ReleaseLoadedTextures(loadedTextures);
                DeletePrograms(programs);
                DeleteTextures(textures);
                DrainGlErrors();
            }
        });
    }

    /// <summary>
    /// Uploads generated and authored manifest suites, including optional maps and a deterministic
    /// duplicate claim, into a real material texture.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("PBR")]
    [TestCategory("GPU")]
    [Timeout(60_000)]
    public void TerrainManifestUploadsGeneratedAuthoredAndRejectsDuplicateClaims()
    {
        RunWithContext("VintageRTX.PbrManifest.Coverage", () =>
        {
            List<int> textures = [];
            List<string> logs = [];
            byte[] normal = Png(new SKColor(128, 128, 255, 255));
            byte[] scalar = Png(new SKColor(210, 210, 210, 255));
            byte[] albedo = Png(new SKColor(90, 80, 70, 255));
            string normalHash = Sha(normal);
            string scalarHash = Sha(scalar);
            string albedoHash = Sha(albedo);
            PbrManifestTexture generated = ManifestEntry("textures/block/generated.png", "textures/block/generated_n.png");
            PbrManifestTexture missingSource = ManifestEntry(
                "textures/block/missing-source.png",
                "textures/block/generated_n.png");
            PbrManifestTexture noAtlas = ManifestEntry(
                "textures/block/no-atlas.png",
                "textures/block/generated_n.png");
            PbrManifestTexture transparent = ManifestEntry(
                "textures/block/transparent.png",
                "textures/block/generated_n.png");
            PbrManifest generatedManifest = new()
            {
                Schema = "vintagertx.pbr-manifest",
                SchemaVersion = 2,
                Textures = [generated, generated, missingSource, noAtlas, transparent]
            };
            PbrManifestTexture authored = ManifestEntry(
                "textures/block/authored.png",
                "textures/block/authored_n.png",
                normalHash,
                "textures/block/authored_r.png",
                scalarHash,
                "textures/block/authored_m.png",
                scalarHash,
                "textures/block/authored_e.png",
                scalarHash);
            authored.Source.Sha256 = albedoHash;
            PbrManifest authoredManifest = new()
            {
                Schema = "vintagertx.pbr-manifest",
                SchemaVersion = 4,
                DefaultProvenance = "authored",
                Textures = [authored]
            };
            Dictionary<AssetLocation, IAsset> catalog = new()
            {
                [new AssetLocation("mod:textures/block/generated.png")] = Asset("mod:textures/block/generated.png", albedo),
                [new AssetLocation("mod:textures/block/generated_n.png")] = Asset("mod:textures/block/generated_n.png", normal),
                [new AssetLocation("mod:textures/block/no-atlas.png")] = Asset("mod:textures/block/no-atlas.png", albedo),
                [new AssetLocation("mod:textures/block/transparent.png")] = Asset(
                    "mod:textures/block/transparent.png",
                    Png(new SKColor(90, 80, 70, 0))),
                [new AssetLocation("mod:textures/block/authored.png")] = Asset("mod:textures/block/authored.png", albedo),
                [new AssetLocation("mod:textures/block/authored_n.png")] = Asset("mod:textures/block/authored_n.png", normal),
                [new AssetLocation("mod:textures/block/authored_r.png")] = Asset("mod:textures/block/authored_r.png", scalar),
                [new AssetLocation("mod:textures/block/authored_m.png")] = Asset("mod:textures/block/authored_m.png", scalar),
                [new AssetLocation("mod:textures/block/authored_e.png")] = Asset("mod:textures/block/authored_e.png", scalar)
            };
            List<IAsset> manifests =
            [
                Asset("mod:config/vintagertx/pbr-manifest.json", JsonSerializer.SerializeToUtf8Bytes(generatedManifest)),
                Asset("mod:config/vintagertx/pbr-manifest.json", JsonSerializer.SerializeToUtf8Bytes(authoredManifest))
            ];
            IAssetManager assets = Assets(catalog, manifests);
            ILogger logger = Logger(logs);
            PbrTerrainRenderer renderer = new(
                ClientApi(null, BlockAtlas([], [], new TextureAtlasPosition()), assets, logger, new ShaderHolder()),
                PbrSidecarAssetStore.Capture(assets, logger));
            try
            {
                int material = CreateTexture(8, 8, textures);
                SetField(renderer, "materialAtlasTexture", material);
                SetField(renderer, "sourceAtlasWidth", 8);
                SetField(renderer, "sourceAtlasHeight", 8);
                SetField(renderer, "sourceAtlasPositions", new Dictionary<AssetLocation, TextureAtlasPosition[]>
                {
                    [new AssetLocation("mod:textures/block/generated.png")] = [Position(1, 0, 0f, 0f, 0.5f, 1f)],
                    [new AssetLocation("mod:textures/block/authored.png")] = [Position(1, 0, 0.5f, 0f, 1f, 1f)],
                    [new AssetLocation("mod:textures/block/transparent.png")] = [Position(1, 0, 0f, 0f, 0.25f, 0.25f)]
                });
                SetField(renderer, "ambiguousAtlasSources", new HashSet<AssetLocation>());
                HashSet<AssetLocation> applied = [];
                Dictionary<AssetLocation, HashSet<string>> hashes = [];
                HashSet<AssetLocation> rejected = [];

                Assert.AreEqual(2, Invoke(renderer, "ApplyManifestOverrides", applied, hashes, rejected));
                Assert.AreEqual(3, applied.Count);
                Assert.AreEqual(1, rejected.Count);
                Assert.IsTrue(logs.Any(entry => entry.Contains("duplicate", StringComparison.OrdinalIgnoreCase)));
                Assert.IsFalse(PbrTerrainRenderer.IsOpaqueGeneratedFallbackSource([]));

                AssetLocation flakySourceLocation = new("mod:textures/block/flaky.png");
                AssetLocation flakyNormalLocation = new("mod:textures/block/flaky_n.png");
                IAsset flakySource = Asset(flakySourceLocation.ToString(), albedo);
                IAsset flakyNormal = Asset(flakyNormalLocation.ToString(), normal);
                PbrManifest flakyManifestValue = new()
                {
                    Schema = "vintagertx.pbr-manifest",
                    SchemaVersion = 2,
                    Textures = [ManifestEntry(flakySourceLocation.Path, flakyNormalLocation.Path)]
                };
                IAsset flakyManifest = Asset(
                    "mod:config/vintagertx/pbr-manifest.json",
                    JsonSerializer.SerializeToUtf8Bytes(flakyManifestValue));
                Dictionary<AssetLocation, IAsset> flakyCatalog = new()
                {
                    [flakySourceLocation] = flakySource,
                    [flakyNormalLocation] = flakyNormal
                };
                int flakySourceLookups = 0;
                IAssetManager flakyAssets = RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) =>
                    method.Name switch
                    {
                        "get_AllAssets" => flakyCatalog,
                        "get_Origins" => new List<IAssetOrigin>(),
                        "GetManyInCategory" => new List<IAsset> { flakyManifest },
                        "TryGet" when ((AssetLocation)arguments![0]!).Equals(flakySourceLocation) =>
                            ++flakySourceLookups == 1 ? flakySource : null,
                        "TryGet" => flakyCatalog.TryGetValue(
                            (AssetLocation)arguments![0]!,
                            out IAsset? value) ? value : null,
                        _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
                    });
                PbrTerrainRenderer flakyRenderer = new(
                    ClientApi(
                        null,
                        BlockAtlas([], [], new TextureAtlasPosition()),
                        flakyAssets,
                        logger,
                        new ShaderHolder()),
                    PbrSidecarAssetStore.Capture(flakyAssets, logger));
                SetField(flakyRenderer, "sourceAtlasPositions", new Dictionary<AssetLocation, TextureAtlasPosition[]>
                {
                    [flakySourceLocation] = [Position(1, 0, 0f, 0f, 1f, 1f)]
                });
                SetField(flakyRenderer, "ambiguousAtlasSources", new HashSet<AssetLocation>());
                Assert.AreEqual(0, Invoke(
                    flakyRenderer,
                    "ApplyManifestOverrides",
                    new HashSet<AssetLocation>(),
                    new Dictionary<AssetLocation, HashSet<string>>(),
                    new HashSet<AssetLocation>()));
                Assert.AreEqual(2, flakySourceLookups);
                flakyRenderer.Dispose();
            }
            finally
            {
                SetField(renderer, "materialAtlasTexture", 0);
                renderer.Dispose();
                DeleteTextures(textures);
                DrainGlErrors();
            }
        });
    }

    /// <summary>
    /// Closes the remaining CPU decision edges for manifest identity, authored sidecar preference,
    /// baked sub-ID bounds, and corrupt/empty decoded albedo handling.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("PBR")]
    public void TerrainResidualCpuContractsExerciseEveryBoundary()
    {
        PbrManifestTexture missingDomain = ManifestEntry("textures/block/a.png", "textures/block/a_n.png");
        missingDomain.Source.Domain = " ";
        Assert.IsFalse(PbrTerrainRenderer.HasExactManifestSidecarReferences(
            2,
            "mod",
            missingDomain,
            out _));

        byte[] authoredBytes = [1, 2, 3, 4];
        AssetLocation authoredLocation = new("mod:textures/block/authored_n.png");
        Dictionary<AssetLocation, IAsset> catalog = new()
        {
            [authoredLocation] = Asset(authoredLocation.ToString(), authoredBytes)
        };
        IAssetManager assets = Assets(catalog);
        ILogger logger = Logger([]);
        PbrTerrainRenderer renderer = new(
            ClientApi(null, BlockAtlas([], [], new TextureAtlasPosition()), assets, logger, new ShaderHolder()),
            PbrSidecarAssetStore.Capture(assets, logger));
        Dictionary<AssetLocation, HashSet<string>> fallbackHashes = new()
        {
            [authoredLocation] = new(StringComparer.OrdinalIgnoreCase) { new string('0', 64) }
        };
        object?[] resolutionArguments = [authoredLocation, fallbackHashes, false, false];
        Assert.AreSame(
            catalog[authoredLocation],
            Invoke(renderer, "ResolvePreferredSidecarCore", resolutionArguments));
        Assert.AreEqual(true, resolutionArguments[3]);
        AssetLocation absentLocation = new("mod:textures/block/absent_n.png");
        object?[] absentResolutionArguments =
        [
            absentLocation,
            new Dictionary<AssetLocation, HashSet<string>>
            {
                [absentLocation] = new(StringComparer.OrdinalIgnoreCase) { new string('0', 64) }
            },
            false,
            true
        ];
        Assert.IsNull(Invoke(renderer, "ResolvePreferredSidecarCore", absentResolutionArguments));
        Assert.AreEqual(false, absentResolutionArguments[3]);

        TextureAtlasPosition usable = Position(7, 0, 0f, 0f, 1f, 1f);
        Dictionary<AssetLocation, List<TextureAtlasPosition>> lookup = [];
        int skipped = 0;
        object?[] negativeArguments =
        [
            new AssetLocation("mod:textures/block/base.png"),
            new BakedCompositeTexture { TextureSubId = -1 },
            new[] { usable },
            lookup,
            new System.Func<TextureAtlasPosition, bool>(_ => true),
            skipped
        ];
        InvokeStatic("AddBakedTexture", negativeArguments);
        object?[] nullPositionArguments =
        [
            new AssetLocation("mod:textures/block/base.png"),
            new BakedCompositeTexture { TextureSubId = 0 },
            new TextureAtlasPosition[] { null! },
            lookup,
            new System.Func<TextureAtlasPosition, bool>(_ => true),
            skipped
        ];
        InvokeStatic("AddBakedTexture", nullPositionArguments);

        Harmony harmony = new($"vintagertx.test.pbrdecode.{Guid.NewGuid():N}");
        MethodInfo decode = typeof(SKBitmap).GetMethod(
            nameof(SKBitmap.Decode),
            BindingFlags.Public | BindingFlags.Static,
            null,
            [typeof(byte[])],
            null)!;
        harmony.Patch(decode, prefix: new HarmonyMethod(
            typeof(CoveragePbrRendererGpuTests).GetMethod(nameof(ForceDecode), PrivateStatic)!));
        try
        {
            forcedDecodeResult = 1;
            Assert.IsFalse(PbrTerrainRenderer.IsOpaqueGeneratedFallbackSource([1]));
            forcedDecodeResult = 2;
            Assert.IsFalse(PbrTerrainRenderer.IsOpaqueGeneratedFallbackSource([1]));
        }
        finally
        {
            forcedDecodeResult = 0;
            harmony.UnpatchAll(harmony.Id);
            renderer.Dispose();
        }
    }

    /// <summary>Exercises null, disposed, zero-id, missing-uniform, enabled, and disabled entity shaders.</summary>
    /// <param name="renderer">Renderer whose private state transition is invoked.</param>
    /// <param name="shaders">Mutable shader registry.</param>
    /// <param name="programs">Owned real program handles.</param>
    /// <param name="logs">Captured diagnostics.</param>
    private static void ExerciseEntityShaderVariants(
        PbrEntityRenderer renderer,
        ShaderHolder shaders,
        List<int> programs,
        List<string> logs)
    {
        shaders.Program = null;
        Invoke(renderer, "SetEntityShaderState", true);
        shaders.Program = ShaderProgram(1, disposed: true);
        Invoke(renderer, "SetEntityShaderState", true);
        shaders.Program = ShaderProgram(0);
        Invoke(renderer, "SetEntityShaderState", true);

        int absent = CreateProgram(false, false, false, false, programs);
        shaders.Program = ShaderProgram(absent);
        SetField(renderer, "overrideAvailable", false);
        Invoke(renderer, "SetEntityShaderState", true);
        int warningCount = logs.Count(entry => entry.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        SetField(renderer, "overrideAvailable", true);
        Invoke(renderer, "SetEntityShaderState", true);
        Invoke(renderer, "SetEntityShaderState", false);
        Assert.AreEqual(warningCount, logs.Count(entry => entry.Contains("unavailable", StringComparison.OrdinalIgnoreCase)));

        shaders.Program = ShaderProgram(CreateProgram(false, false, true, false, programs));
        Invoke(renderer, "SetEntityShaderState", true);
        shaders.Program = ShaderProgram(CreateProgram(false, false, true, true, programs));
        Invoke(renderer, "SetEntityShaderState", true);
        Invoke(renderer, "SetEntityShaderState", false);
    }

    /// <summary>Exercises null, disposed, zero-id, missing-uniform, enabled, and disabled terrain shaders.</summary>
    /// <param name="renderer">Renderer whose private state transition is invoked.</param>
    /// <param name="shaders">Mutable shader registry.</param>
    /// <param name="programs">Owned real program handles.</param>
    /// <param name="logs">Captured diagnostics.</param>
    private static void ExerciseTerrainShaderVariants(
        PbrTerrainRenderer renderer,
        ShaderHolder shaders,
        List<int> programs,
        List<string> logs)
    {
        shaders.Program = null;
        Invoke(renderer, "SetTerrainShaderState", true);
        shaders.Program = ShaderProgram(1, disposed: true);
        Invoke(renderer, "SetTerrainShaderState", true);
        shaders.Program = ShaderProgram(0);
        Invoke(renderer, "SetTerrainShaderState", true);

        shaders.Program = ShaderProgram(CreateProgram(false, false, false, false, programs));
        SetField(renderer, "overrideAvailable", false);
        Invoke(renderer, "SetTerrainShaderState", true);
        int warningCount = logs.Count(entry => entry.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        SetField(renderer, "overrideAvailable", true);
        Invoke(renderer, "SetTerrainShaderState", true);
        Invoke(renderer, "SetTerrainShaderState", false);
        Assert.AreEqual(warningCount, logs.Count(entry => entry.Contains("unavailable", StringComparison.OrdinalIgnoreCase)));

        shaders.Program = ShaderProgram(CreateProgram(true, false, false, false, programs));
        Invoke(renderer, "SetTerrainShaderState", true);
        shaders.Program = ShaderProgram(CreateProgram(true, true, false, false, programs));
        Invoke(renderer, "SetTerrainShaderState", true);
        Invoke(renderer, "SetTerrainShaderState", false);
    }

    /// <summary>Forces deterministic null or empty bitmap results around Skia's managed decode wrapper.</summary>
    /// <param name="__result">Harmony return carrier.</param>
    /// <returns>Whether the original decoder should execute.</returns>
    private static bool ForceDecode(ref SKBitmap? __result)
    {
        if (forcedDecodeResult == 0)
        {
            return true;
        }

        __result = forcedDecodeResult == 1 ? null : new SKBitmap();
        return false;
    }

    /// <summary>Runs assertions inside an invisible OpenGL 4.3 core context.</summary>
    /// <param name="title">Native window diagnostic title.</param>
    /// <param name="assertions">Assertions requiring current OpenGL bindings.</param>
    private static void RunWithContext(string title, Action assertions)
    {
        Type provider = typeof(NativeWindow).Assembly.GetType(
            "OpenTK.Windowing.Desktop.GLFWProvider",
            throwOnError: true)!;
        PropertyInfo guard = provider.GetProperty(
            "CheckForMainThread",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        bool previous = (bool)(guard.GetValue(null) ?? true);
        try
        {
            guard.SetValue(null, false);
            NativeWindowSettings settings = new()
            {
                API = ContextAPI.OpenGL,
                APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                ClientSize = new Vector2i(16, 16),
                StartVisible = false,
                StartFocused = false,
                AutoLoadBindings = false,
                NumberOfSamples = 0,
                Title = title
            };
            using NativeWindow window = new(settings);
            window.MakeCurrent();
            GL.LoadBindings(new GLFWBindingsContext());
            assertions();
        }
        finally
        {
            guard.SetValue(null, previous);
        }
    }

    /// <summary>Creates one allocated RGBA8 texture and records its ownership.</summary>
    /// <param name="width">Storage width.</param>
    /// <param name="height">Storage height.</param>
    /// <param name="textures">Owned-handle sink.</param>
    /// <returns>Real texture handle.</returns>
    private static int CreateTexture(int width, int height, List<int> textures)
    {
        int texture = GL.GenTexture();
        textures.Add(texture);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba8,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            IntPtr.Zero);
        return texture;
    }

    /// <summary>Compiles one real shader program exposing a selected subset of renderer uniforms.</summary>
    /// <param name="terrainSampler">Whether the terrain sampler remains active.</param>
    /// <param name="terrainEnabled">Whether the terrain enable uniform remains active.</param>
    /// <param name="entitySampler">Whether the entity sampler remains active.</param>
    /// <param name="entityEnabled">Whether the entity enable uniform remains active.</param>
    /// <param name="programs">Owned-program sink.</param>
    /// <returns>Linked real program handle.</returns>
    private static int CreateProgram(
        bool terrainSampler,
        bool terrainEnabled,
        bool entitySampler,
        bool entityEnabled,
        List<int> programs)
    {
        string declarations = string.Empty;
        string expression = "vec4(0.125)";
        if (terrainSampler)
        {
            declarations += "uniform sampler2D vintagertxMaterialTex;\n";
            expression += " + texture(vintagertxMaterialTex, vec2(0.5))";
        }
        if (terrainEnabled)
        {
            declarations += "uniform int vintagertxPbrEnabled;\n";
            expression += " + vec4(float(vintagertxPbrEnabled))";
        }
        if (entitySampler)
        {
            declarations += "uniform sampler2D vintagertxEntityMaterialTex;\n";
            expression += " + texture(vintagertxEntityMaterialTex, vec2(0.5))";
        }
        if (entityEnabled)
        {
            declarations += "uniform int vintagertxEntityPbrEnabled;\n";
            expression += " + vec4(float(vintagertxEntityPbrEnabled))";
        }

        int vertex = CompileShader(ShaderType.VertexShader, """
            #version 330 core
            void main()
            {
                vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """);
        int fragment = CompileShader(
            ShaderType.FragmentShader,
            $"#version 330 core\n{declarations}out vec4 color;\nvoid main() {{ color = {expression}; }}");
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        string log = GL.GetProgramInfoLog(program);
        GL.DetachShader(program, vertex);
        GL.DetachShader(program, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        Assert.AreEqual(1, linked, log);
        programs.Add(program);
        return program;
    }

    /// <summary>Compiles one shader stage and asserts driver acceptance.</summary>
    /// <param name="type">OpenGL shader stage.</param>
    /// <param name="source">GLSL source.</param>
    /// <returns>Compiled shader handle.</returns>
    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        Assert.AreEqual(1, compiled, GL.GetShaderInfoLog(shader));
        return shader;
    }

    /// <summary>Creates a Vintage Story shader proxy around one driver program.</summary>
    /// <param name="programId">Driver program name.</param>
    /// <param name="disposed">Whether the engine wrapper reports disposal.</param>
    /// <returns>Shader-program proxy.</returns>
    private static IShaderProgram ShaderProgram(int programId, bool disposed = false) =>
        RuntimeCoverageDispatchProxy.Create<IShaderProgram>((method, _) => method.Name switch
        {
            "get_ProgramId" => programId,
            "get_Disposed" => disposed,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>Creates a client API graph shared by entity and terrain renderer fixtures.</summary>
    /// <param name="entityAtlas">Optional entity atlas.</param>
    /// <param name="blockAtlas">Optional terrain atlas.</param>
    /// <param name="assets">Asset registry.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="shaders">Mutable shader registry.</param>
    /// <returns>Client API proxy.</returns>
    private static ICoreClientAPI ClientApi(
        ITextureAtlasAPI? entityAtlas,
        IBlockTextureAtlasAPI? blockAtlas,
        IAssetManager assets,
        ILogger logger,
        ShaderHolder shaders)
    {
        IShaderAPI shaderApi = RuntimeCoverageDispatchProxy.Create<IShaderAPI>((method, _) =>
            method.Name == nameof(IShaderAPI.GetProgram)
                ? shaders.Program
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>((method, _) =>
            method.Name == "get_Blocks" ? new List<Block>() : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_EntityTextureAtlas" => entityAtlas,
            "get_BlockTextureAtlas" => blockAtlas,
            "get_Assets" => assets,
            "get_Logger" => logger,
            "get_Shader" => shaderApi,
            "get_Render" => render,
            "get_World" => world,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Creates a deterministic entity atlas with mutable page ownership.</summary>
    /// <param name="textures">Mutable atlas pages.</param>
    /// <param name="positions">Indexed rectangles.</param>
    /// <param name="unknown">Missing-texture sentinel.</param>
    /// <param name="lookup">Atlas key mappings.</param>
    /// <returns>Entity atlas proxy.</returns>
    private static ITextureAtlasAPI EntityAtlas(
        List<LoadedTexture> textures,
        TextureAtlasPosition[] positions,
        TextureAtlasPosition unknown,
        IReadOnlyDictionary<AssetLocation, TextureAtlasPosition> lookup) =>
        RuntimeCoverageDispatchProxy.Create<ITextureAtlasAPI>((method, arguments) => method.Name switch
        {
            "get_AtlasTextures" => textures,
            "get_Positions" => positions,
            "get_UnknownTexturePosition" => unknown,
            "get_Item" => lookup.TryGetValue((AssetLocation)arguments![0]!, out TextureAtlasPosition? value) ? value : unknown,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>Creates a deterministic block atlas with mutable page ownership.</summary>
    /// <param name="textures">Mutable atlas pages.</param>
    /// <param name="positions">Indexed rectangles.</param>
    /// <param name="unknown">Missing-texture sentinel.</param>
    /// <returns>Block atlas proxy.</returns>
    private static IBlockTextureAtlasAPI BlockAtlas(
        List<LoadedTexture> textures,
        TextureAtlasPosition[] positions,
        TextureAtlasPosition unknown) =>
        RuntimeCoverageDispatchProxy.Create<IBlockTextureAtlasAPI>((method, _) => method.Name switch
        {
            "get_AtlasTextures" => textures,
            "get_Positions" => positions,
            "get_UnknownTexturePosition" => unknown,
            "get_Item" => unknown,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>Creates one loaded atlas page wrapper.</summary>
    /// <param name="textureId">Borrowed real texture handle.</param>
    /// <param name="width">Atlas width.</param>
    /// <param name="height">Atlas height.</param>
    /// <param name="wrappers">All wrappers tracked for unconditional finalizer suppression.</param>
    /// <returns>Mutable loaded texture metadata.</returns>
    private static LoadedTexture Loaded(
        int textureId,
        int width,
        int height,
        ICollection<LoadedTexture> wrappers)
    {
        IRenderAPI render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_Render" ? render : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        LoadedTexture loaded = new(api)
        {
            TextureId = textureId,
            Width = width,
            Height = height
        };
        wrappers.Add(loaded);
        return loaded;
    }

    /// <summary>Creates one normalized atlas rectangle.</summary>
    /// <param name="textureId">Owning texture.</param>
    /// <param name="atlasNumber">Owning atlas page.</param>
    /// <param name="x1">Left coordinate.</param>
    /// <param name="y1">Top coordinate.</param>
    /// <param name="x2">Right coordinate.</param>
    /// <param name="y2">Bottom coordinate.</param>
    /// <returns>Atlas rectangle metadata.</returns>
    private static TextureAtlasPosition Position(
        int textureId,
        byte atlasNumber,
        float x1,
        float y1,
        float x2,
        float y2) => new()
        {
            atlasTextureId = textureId,
            atlasNumber = atlasNumber,
            x1 = x1,
            y1 = y1,
            x2 = x2,
            y2 = y2
        };

    /// <summary>Creates an asset manager over mutable assets and ordered manifests.</summary>
    /// <param name="catalog">Visible asset catalog.</param>
    /// <param name="manifests">Optional manifest enumeration.</param>
    /// <returns>Asset manager proxy.</returns>
    private static IAssetManager Assets(
        Dictionary<AssetLocation, IAsset> catalog,
        List<IAsset>? manifests = null) =>
        RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) => method.Name switch
        {
            "get_AllAssets" => catalog,
            "get_Origins" => new List<IAssetOrigin>(),
            "TryGet" => catalog.TryGetValue((AssetLocation)arguments![0]!, out IAsset? asset) ? asset : null,
            "GetManyInCategory" => manifests ?? [],
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

    /// <summary>Creates one loaded binary asset.</summary>
    /// <param name="code">Canonical asset code.</param>
    /// <param name="data">Loaded bytes.</param>
    /// <returns>Asset proxy.</returns>
    private static IAsset Asset(string code, byte[] data)
    {
        AssetLocation location = new(code);
        return RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) => method.Name switch
        {
            "get_Location" => location,
            "get_Data" => data,
            "IsLoaded" => true,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>Creates a logger that records all formatted calls.</summary>
    /// <param name="logs">Destination strings.</param>
    /// <returns>Logger proxy.</returns>
    private static ILogger Logger(List<string> logs) =>
        RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Warning) or nameof(ILogger.Error) or nameof(ILogger.Notification))
            {
                logs.Add($"{method.Name}:{string.Join('|', arguments ?? [])}");
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

    /// <summary>Creates a one-pixel PNG with exact unpremultiplied channels.</summary>
    /// <param name="color">Pixel color.</param>
    /// <returns>Encoded PNG bytes.</returns>
    private static byte[] Png(SKColor color)
    {
        using SKBitmap bitmap = new(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.SetPixel(0, 0, color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Returns the uppercase SHA-256 expected by manifest validation.</summary>
    /// <param name="data">Bytes to hash.</param>
    /// <returns>Hexadecimal digest.</returns>
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>Creates one manifest entry with optional map identities and hashes.</summary>
    /// <param name="sourcePath">Canonical source path.</param>
    /// <param name="normalAsset">Normal-map reference.</param>
    /// <param name="normalSha256">Normal digest.</param>
    /// <param name="roughnessAsset">Roughness-map reference.</param>
    /// <param name="roughnessSha256">Roughness digest.</param>
    /// <param name="metallicAsset">Metallic-map reference.</param>
    /// <param name="metallicSha256">Metallic digest.</param>
    /// <param name="emissiveAsset">Emissive-map reference.</param>
    /// <param name="emissiveSha256">Emissive digest.</param>
    /// <returns>Manifest entry.</returns>
    private static PbrManifestTexture ManifestEntry(
        string sourcePath,
        string normalAsset,
        string normalSha256 = "",
        string roughnessAsset = "",
        string roughnessSha256 = "",
        string metallicAsset = "",
        string metallicSha256 = "",
        string emissiveAsset = "",
        string emissiveSha256 = "") => new()
        {
            Source = new PbrSourceReference { Domain = "mod", Path = sourcePath },
            Normal = new PbrMapReference { Asset = normalAsset, Sha256 = normalSha256 },
            Roughness = new PbrMapReference { Asset = roughnessAsset, Sha256 = roughnessSha256 },
            Metallic = new PbrMapReference { Asset = metallicAsset, Sha256 = metallicSha256 },
            Emissive = new PbrMapReference { Asset = emissiveAsset, Sha256 = emissiveSha256 }
        };

    /// <summary>Invokes one private instance method.</summary>
    /// <param name="instance">Target object.</param>
    /// <param name="name">Method name.</param>
    /// <param name="arguments">Arguments.</param>
    /// <returns>Boxed return value, or null for void.</returns>
    private static object? Invoke(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethods(PrivateInstance)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(instance, arguments);

    /// <summary>Invokes one private static renderer helper, including ref-argument carriers.</summary>
    /// <param name="name">Method name.</param>
    /// <param name="arguments">Arguments.</param>
    /// <returns>Boxed return value, or null for void.</returns>
    private static object? InvokeStatic(string name, params object?[] arguments) =>
        typeof(PbrTerrainRenderer).GetMethods(PrivateStatic)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(null, arguments);

    /// <summary>Reads one private instance field.</summary>
    /// <typeparam name="T">Expected field type.</typeparam>
    /// <param name="instance">Field owner.</param>
    /// <param name="name">Field name.</param>
    /// <returns>Current field value.</returns>
    private static T GetField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;

    /// <summary>Assigns one private instance field.</summary>
    /// <param name="instance">Field owner.</param>
    /// <param name="name">Field name.</param>
    /// <param name="value">New value.</param>
    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);

    /// <summary>Prevents borrowed LoadedTexture finalizers from deleting test-owned GL handles.</summary>
    /// <param name="textures">Wrappers to neutralize.</param>
    private static void ReleaseLoadedTextures(IEnumerable<LoadedTexture> textures)
    {
        foreach (LoadedTexture texture in textures)
        {
            texture.TextureId = 0;
            GC.SuppressFinalize(texture);
        }
    }

    /// <summary>Deletes all distinct real texture handles.</summary>
    /// <param name="textures">Owned handles.</param>
    private static void DeleteTextures(IEnumerable<int> textures)
    {
        foreach (int texture in textures.Distinct())
        {
            if (GL.IsTexture(texture))
            {
                GL.DeleteTexture(texture);
            }
        }
    }

    /// <summary>Deletes all distinct real program handles.</summary>
    /// <param name="programs">Owned programs.</param>
    private static void DeletePrograms(IEnumerable<int> programs)
    {
        GL.UseProgram(0);
        foreach (int program in programs.Distinct())
        {
            if (GL.IsProgram(program))
            {
                GL.DeleteProgram(program);
            }
        }
    }

    /// <summary>Consumes benign errors emitted by deliberately incomplete synthetic resources.</summary>
    private static void DrainGlErrors()
    {
        while (GL.GetError() != OpenTK.Graphics.OpenGL4.ErrorCode.NoError)
        {
        }
    }

    /// <summary>Mutable shader registry state captured by the API proxy.</summary>
    private sealed class ShaderHolder
    {
        /// <summary>Gets or sets the program returned by every renderer lookup.</summary>
        internal IShaderProgram? Program { get; set; }
    }
}
