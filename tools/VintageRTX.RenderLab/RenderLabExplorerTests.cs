using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using SkiaSharp;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.RenderLab;

/// <summary>
/// Contains deterministic regression checks for render Lab Explorer.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RenderLabExplorerTests
{
    /// <summary>
    /// Gets or sets the MSTest context used to locate per-run artifacts without process-global state.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies the command Line Options Remain Deterministic regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void CommandLineOptionsRemainDeterministic()
    {
        RenderLabOptions options = RenderLabOptions.Parse(
        [
            "--width", "1",
            "--height", "9999",
            "--frames", "2",
            "--output", "."
        ]);

        Assert.AreEqual(320, options.Width);
        Assert.AreEqual(2160, options.Height);
        Assert.AreEqual(60, options.BenchmarkFrames);
        Assert.AreEqual(Path.GetFullPath("."), options.OutputDirectory);
        Assert.ThrowsException<ArgumentException>(() => RenderLabOptions.Parse(["--unknown"]));
    }

    /// <summary>
    /// Verifies the synthetic Buffers And Analyzer Guards Remain Coherent regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void SyntheticBuffersAndAnalyzerGuardsRemainCoherent()
    {
        SyntheticScene scene = new(320, 180);
        int expectedRgba = checked(scene.Width * scene.Height * 4);
        int expectedPixels = checked(scene.Width * scene.Height);

        Assert.AreEqual(expectedRgba, scene.SourceColor.Length);
        Assert.AreEqual(expectedRgba, scene.OpaqueReferenceColor.Length);
        Assert.AreEqual(expectedRgba, scene.NormalRoughness.Length);
        Assert.AreEqual(expectedRgba, scene.Material.Length);
        Assert.AreEqual(expectedPixels, scene.SurfaceIdentities.Length);
        Assert.AreEqual(expectedPixels, scene.LiquidKind.Length);
        Assert.AreEqual(10, LiquidOpticalRegistry.LookupWidth);
        Assert.AreEqual(
            LiquidOpticalRegistry.LookupWidth
                * LiquidOpticalRegistry.LookupHeight
                * LiquidOpticalRegistry.LookupChannels,
            scene.LiquidOpticalProfiles.Length);
        Assert.ThrowsException<InvalidDataException>(
            () => PbrSeparationAnalyzer.Analyze(scene, new byte[4]));
        Assert.ThrowsException<InvalidDataException>(
            () => WaterVolumeAnalyzer.Analyze(scene, new byte[4]));
    }

    /// <summary>
    /// Verifies that authored metal identities remain deterministic and produce independent PBR report metrics.
    /// </summary>
    [TestMethod]
    public void PbrAnalyzerSeparatesAnvilAndLanternCageMetal()
    {
        SyntheticScene scene = new(320, 180);
        SyntheticScene repeatedScene = new(320, 180);
        Assert.IsTrue(scene.SurfaceIdentities.SequenceEqual(repeatedScene.SurfaceIdentities));

        int anvilPixels = 0;
        int lanternCagePixels = 0;
        for (int pixel = 0; pixel < scene.SurfaceIdentities.Length; pixel++)
        {
            LabSurfaceIdentity identity = scene.SurfaceIdentities[pixel];
            if (identity == LabSurfaceIdentity.None)
            {
                continue;
            }

            int metallicBits = scene.Material[pixel * 4 + 2] & 7;
            Assert.IsTrue(metallicBits >= 4, $"Surface identity {identity} lost its metallic payload at pixel {pixel}.");
            if (identity == LabSurfaceIdentity.AnvilMetal)
            {
                anvilPixels++;
            }
            else if (identity == LabSurfaceIdentity.LanternCageMetal)
            {
                lanternCagePixels++;
            }
            else
            {
                Assert.Fail($"Unexpected synthetic surface identity '{identity}'.");
            }
        }

        Assert.IsTrue(anvilPixels >= 8);
        Assert.IsTrue(lanternCagePixels >= 8);

        byte[] responsiveFinal = (byte[])scene.SourceColor.Clone();
        for (int pixel = 0; pixel < scene.SurfaceIdentities.Length; pixel++)
        {
            int offset = pixel * 4;
            if ((scene.Material[offset + 2] & 64) == 0)
            {
                continue;
            }
            responsiveFinal[offset] = (byte)Math.Min(responsiveFinal[offset] + 32, byte.MaxValue);
            responsiveFinal[offset + 1] = (byte)Math.Min(responsiveFinal[offset + 1] + 32, byte.MaxValue);
            responsiveFinal[offset + 2] = (byte)Math.Min(responsiveFinal[offset + 2] + 32, byte.MaxValue);
        }

        PbrSeparationAnalysis analysis = PbrSeparationAnalyzer.Analyze(scene, responsiveFinal);
        Assert.IsFalse(analysis.Report.Classes.ContainsKey("metal"));
        Assert.AreEqual(anvilPixels, analysis.Report.Classes["anvil-metal"].PixelCount);
        Assert.AreEqual(lanternCagePixels, analysis.Report.Classes["lantern-cage-metal"].PixelCount);
        Assert.IsTrue(analysis.Report.Classes["anvil-metal"].MeanFinalLinearLuminance >= 0.020);
        Assert.IsTrue(analysis.Report.Classes["lantern-cage-metal"].MeanFinalLinearLuminance >= 0.020);
        Assert.IsTrue(analysis.Report.ChangedGeometryFraction >= 0.05);
    }

    /// <summary>
    /// Verifies that every synthetic liquid carries its SI transport data in the ten-texel GPU ABI.
    /// </summary>
    [TestMethod]
    public void SyntheticLiquidProfilesPackPhysicalTenTexelContract()
    {
        SyntheticScene scene = new(320, 180);

        AssertLiquidTexel(
            scene.LiquidOpticalProfiles,
            SyntheticScene.WaterOpticalProfileId,
            1,
            0.3594f,
            0.0654f,
            0.00922f,
            0.0f);
        AssertLiquidTexel(
            scene.LiquidOpticalProfiles,
            SyntheticScene.WaterOpticalProfileId,
            2,
            0.0007f,
            0.0015f,
            0.0035f,
            0.0f);
        AssertLiquidTexel(
            scene.LiquidOpticalProfiles,
            SyntheticScene.WaterOpticalProfileId,
            8,
            998.2f,
            0.001002f,
            0.07275f,
            0.020f);
        AssertLiquidTexel(
            scene.LiquidOpticalProfiles,
            SyntheticScene.HoneyOpticalProfileId,
            8,
            1496.0f,
            7.85f,
            0.0801f,
            0.003f);
        AssertLiquidTexel(
            scene.LiquidOpticalProfiles,
            SyntheticScene.LavaOpticalProfileId,
            8,
            2800.0f,
            10.0f,
            0.4f,
            0.005f);
        AssertLiquidTexel(
            scene.LiquidOpticalProfiles,
            SyntheticScene.LavaOpticalProfileId,
            9,
            1473.15f,
            0.8f,
            (float)LiquidPhysicalModel.MetresPerWorldBlock,
            0.0f);
        AssertLiquidTexel(scene.LiquidOpticalProfiles, 0, 8, 0.0f, 0.0f, 0.0f, 0.0f);
        AssertLiquidTexel(scene.LiquidOpticalProfiles, byte.MaxValue, 9, 0.0f, 0.0f, 0.0f, 0.0f);
        Assert.AreEqual(137.25f, StandaloneRenderer.DeterministicRendererTimeSeconds);
        Assert.AreEqual(0.65f, StandaloneRenderer.DeterministicLiquidWindX);
        Assert.AreEqual(-0.35f, StandaloneRenderer.DeterministicLiquidWindZ);
        Assert.AreEqual(
            8.0,
            LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit,
            0.0);
    }

    /// <summary>
    /// Verifies the production Shader Compiles And Renders Headlessly regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    [TestCategory("GPU")]
    [Timeout(120_000)]
    public void ProductionShaderCompilesAndRendersHeadlessly()
    {
        string testRunDirectory = TestContext.TestRunResultsDirectory
            ?? throw new InvalidOperationException("MSTest did not provide a results directory.");
        string output = Path.Combine(
            testRunDirectory,
            $"renderlab-{Guid.NewGuid():N}");
        Type glfwProvider = typeof(NativeWindow).Assembly.GetType(
            "OpenTK.Windowing.Desktop.GLFWProvider",
            throwOnError: true)!;
        PropertyInfo threadGuard = glfwProvider.GetProperty(
            "CheckForMainThread",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMemberException(glfwProvider.FullName, "CheckForMainThread");
        bool previousGuard = (bool)(threadGuard.GetValue(null) ?? true);
        RenderLabReport report;
        try
        {
            // Windows permits GLFW initialization on this dedicated MSTest
            // worker. OpenTK's conservative cross-platform guard is disabled
            // only for the duration of this non-parallel integration test.
            threadGuard.SetValue(null, false);
            report = RenderLabRunner.Run(
                new RenderLabOptions(640, 360, 60, output),
                ExerciseProductionGpuServices);
        }
        finally
        {
            threadGuard.SetValue(null, previousGuard);
        }

        Assert.IsTrue(File.Exists(report.FinalCapture));
        Assert.IsTrue(File.Exists(Path.Combine(output, "report.json")));
        Assert.IsTrue(report.DiagnosticCaptures.TryGetValue(
            "pbr-transport",
            out string? pbrTransportCapture));
        Assert.IsTrue(File.Exists(pbrTransportCapture));
        Assert.IsTrue(File.Exists(Path.Combine(output, "phase-timings.json")));
        Assert.AreEqual(60, report.BenchmarkFrames);
        Assert.IsTrue(report.AverageGpuMilliseconds > 0.0);
        Assert.IsTrue(report.P99GpuMilliseconds >= report.AverageGpuMilliseconds);
        Assert.IsTrue(report.OnePercentLowFps > 0.0);
        Assert.IsTrue(report.PbrSeparation.ChangedGeometryFraction >= 0.05);
        Assert.IsFalse(report.PbrSeparation.Classes.ContainsKey("metal"));
        PbrClassMetric anvilMetal = report.PbrSeparation.Classes["anvil-metal"];
        PbrClassMetric lanternCageMetal = report.PbrSeparation.Classes["lantern-cage-metal"];
        Assert.IsTrue(anvilMetal.MeanFinalLinearLuminance >= 0.020);
        Assert.IsTrue(anvilMetal.FinalToSourceLuminanceRatio >= 1.0);
        Assert.IsTrue(lanternCageMetal.MeanFinalLinearLuminance >= 0.020);
        Assert.IsTrue(lanternCageMetal.FinalToSourceLuminanceRatio >= 1.0);
        Assert.AreEqual(0, report.WaterVolumes.OverflowPixels);
        Assert.IsTrue(report.DynamicLiquidSurface.ActiveCellCount >= 128);
        Assert.IsTrue(report.DynamicLiquidSurface.DisturbedCellCount >= 24);
        Assert.IsTrue(report.DynamicLiquidSurface.MaximumAbsoluteHeightMetres > 0.0001);
        Assert.IsTrue(report.DynamicLiquidSurface.MaximumNormalSlope > 0.0001);
        Assert.IsTrue(report.DynamicLiquidSurface.MaximumTransientEmission > 0.0001);
        Assert.IsTrue(report.DynamicLiquidSurface.TotalMechanicalEnergyJoules > 0.0);
    }

    /// <summary>
    /// Compares one four-channel row element without hiding channel ordering or SI calibration drift.
    /// </summary>
    /// <param name="lookup">Complete row-major liquid profile lookup.</param>
    /// <param name="profileId">Profile row to inspect.</param>
    /// <param name="texel">Texel column within the ten-column row.</param>
    /// <param name="red">Expected red channel.</param>
    /// <param name="green">Expected green channel.</param>
    /// <param name="blue">Expected blue channel.</param>
    /// <param name="alpha">Expected alpha channel.</param>
    private static void AssertLiquidTexel(
        float[] lookup,
        byte profileId,
        int texel,
        float red,
        float green,
        float blue,
        float alpha)
    {
        int offset = checked(
            ((profileId * LiquidOpticalRegistry.LookupWidth) + texel)
            * LiquidOpticalRegistry.LookupChannels);
        Assert.AreEqual(red, lookup[offset], 0.000001f, "Red channel mismatch.");
        Assert.AreEqual(green, lookup[offset + 1], 0.000001f, "Green channel mismatch.");
        Assert.AreEqual(blue, lookup[offset + 2], 0.000001f, "Blue channel mismatch.");
        Assert.AreEqual(alpha, lookup[offset + 3], 0.000001f, "Alpha channel mismatch.");
    }

    /// <summary>
    /// Executes the exercise Production Gpu Services step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    private static void ExerciseProductionGpuServices()
    {
        ExerciseProductionGlStateAndFrameAllocation();
        ExerciseFilmicVoxelGpuUploads();
        ExercisePbrTerrainAtlas();

        byte[] singlePixel = FrameCaptureService.ReadCurrentFrame(1, 1);
        Assert.AreEqual(4, singlePixel.Length);

        using RenderPerformanceMonitor monitor = new();
        int measurements = 0;
        for (int frame = 0; frame < 48; frame++)
        {
            if (monitor.TryBeginGpuMeasurement())
            {
                GL.Clear(ClearBufferMask.ColorBufferBit);
                monitor.EndGpuMeasurement();
                measurements++;
            }
            GL.Finish();
        }

        Assert.AreEqual(6, measurements);
        Assert.IsTrue(monitor.SmoothedGpuMilliseconds > 0.0);
        Assert.IsTrue(monitor.CreateSnapshot().GpuMilliseconds == 0.0,
            "GPU time alone must not fabricate a CPU frame-history sample.");
        ExerciseGpuQueryDefensiveBranches(monitor);
        monitor.Dispose();
    }

    /// <summary>
    /// Executes the exercise Filmic Voxel Gpu Uploads step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    private static void ExerciseFilmicVoxelGpuUploads()
    {
        string captureRoot = Path.Combine(
            Path.GetTempPath(),
            $"vintagertx-renderlab-filmic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureRoot);
        FilmicDisplayRenderer? renderer = null;
        VoxelScene? scene = null;
        ProductionShaderApiHarness? shaderHarness = null;
        int lumaTexture = 0;
        int lumaFramebuffer = 0;
        try
        {
            EntityPlayer entity = new()
            {
                CameraPos = new Vec3d(0.5, 1.5, 2.5)
            };
            entity.Pos.SetPos(0, 1, 2);
            ILogger logger = Proxy<ILogger>((method, _) => Default(method));
            IClientEventAPI eventApi = Proxy<IClientEventAPI>((method, _) => method.Name switch
            {
                "RegisterGameTickListener" => 1L,
                _ => Default(method)
            });
            IClientPlayer player = Proxy<IClientPlayer>((method, _) => method.Name switch
            {
                "get_Entity" => entity,
                _ => Default(method)
            });
            IClientGameCalendar calendar = Proxy<IClientGameCalendar>((method, _) => method.Name switch
            {
                "get_SunPositionNormalized" => new Vec3f(0.4f, 0.8f, 0.2f),
                "get_SunColor" => new Vec3f(1.0f, 0.9f, 0.75f),
                "GetDayLightStrength" => 0.8f,
                _ => Default(method)
            });
            IBlockAccessor blockAccessor = Proxy<IBlockAccessor>((method, _) => method.Name switch
            {
                "GetClimateAt" => new ClimateCondition
                {
                    Rainfall = 0.75f,
                    RainCloudOverlay = 0.9f
                },
                _ => Default(method)
            });
            IClientWorldAccessor world = Proxy<IClientWorldAccessor>((method, _) => method.Name switch
            {
                "get_BlockAccessor" => blockAccessor,
                "get_Collectibles" => new List<CollectibleObject>(),
                "get_Player" => player,
                "get_Calendar" => calendar,
                _ => Default(method)
            });
            double[] identity =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1
            ];
            DefaultShaderUniforms uniforms = new();
            lumaTexture = GL.GenTexture();
            lumaFramebuffer = GL.GenFramebuffer();
            GL.BindTexture(TextureTarget.Texture2D, lumaTexture);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba16f,
                17,
                9,
                0,
                PixelFormat.Rgba,
                PixelType.HalfFloat,
                IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, lumaFramebuffer);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                lumaTexture,
                0);
            Assert.AreEqual(
                FramebufferErrorCode.FramebufferComplete,
                GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            List<FrameBufferRef> frameBuffers = Enumerable.Range(
                    0,
                    (int)EnumFrameBuffer.Luma + 1)
                .Select(static _ => new FrameBufferRef())
                .ToList();
            frameBuffers[(int)EnumFrameBuffer.Luma] = new FrameBufferRef
            {
                FboId = lumaFramebuffer,
                Width = 17,
                Height = 9,
                ColorTextureIds = [lumaTexture]
            };
            IRenderAPI renderApi = Proxy<IRenderAPI>((method, arguments) => method.Name switch
            {
                "CheckGlError" => CheckGlError(arguments),
                "get_CameraMatrixOrigin" => identity,
                "get_FrameBuffers" => frameBuffers,
                "get_FrameHeight" => 9,
                "get_FrameWidth" => 17,
                "get_PerspectiveProjectionMat" => identity,
                "get_ShaderUniforms" => uniforms,
                _ => Default(method)
            });
            IAssetManager assetManager = CreateDisplayShaderAssets();
            shaderHarness = new ProductionShaderApiHarness();
            ICoreClientAPI api = Proxy<ICoreClientAPI>((method, arguments) => method.Name switch
            {
                "get_Assets" => assetManager,
                "get_Event" => eventApi,
                "get_Logger" => logger,
                "get_Render" => renderApi,
                "get_Shader" => shaderHarness.ShaderApi,
                "get_World" => world,
                "GetOrCreateDataPath" => CreateDataPath(captureRoot, arguments),
                _ => Default(method)
            });

            scene = new VoxelScene(api);
            SetPrivateField(scene, "generation", 1);
            SetPrivateField(scene, "uploadPending", true);
            SetPrivateField(
                scene,
                "readyLights",
                new[]
                {
                    new VoxelLight(0, 0, 0, 1, 1, 1, 1, "invalid", [1]),
                    new VoxelLight(
                        1,
                        2,
                        3,
                        1,
                        0.5f,
                        0.25f,
                        2,
                        "valid",
                        new byte[VoxelScene.LightCasterVoxelCount])
                });
            renderer = new FilmicDisplayRenderer(api, () => new VintageRtxConfig(), scene);

            InvokePrivate(renderer, "EnsureResources", 32, 16, 0);
            InvokePrivate(renderer, "EnsureResources", 32, 16, 0);
            InvokePrivate(renderer, "EnsureResources", 17, 9, 2);
            Assert.IsTrue(renderer.Initialized);
            Assert.IsTrue(shaderHarness.ProgramIds.Count > 0);
            Assert.IsTrue(renderer.ReloadShader());
            foreach (string requiredUniform in new[]
            {
                "voxelVolume",
                "voxelOccupancy",
                "voxelLightCasterMasks",
                "voxelFluidSurface",
                "voxelIrradiance",
                "voxelIrradianceDirection",
                "voxelSunOccupancy",
                "voxelRainSurface",
                "voxelLightPositionIntensity",
                "voxelLightColorRadius",
                "voxelLightCasterLayer"
            })
            {
                shaderHarness.RenamedUniform = requiredUniform;
                Assert.IsFalse(renderer.ReloadShader(), requiredUniform);
                StringAssert.Contains(renderer.Status, "voxel lighting uniforms were not linked");
                renderer.ResetFault();
            }
            foreach (string requiredImpactUniform in new[]
            {
                "liquidImpactOriginAgeAmplitude",
                "liquidImpactMaterial",
                "liquidImpactMotionWavelength",
                "liquidImpactEnergyLedger",
                "liquidImpactSplash"
            })
            {
                shaderHarness.RenamedUniform = requiredImpactUniform;
                Assert.IsFalse(renderer.ReloadShader(), requiredImpactUniform);
                StringAssert.Contains(renderer.Status, "impact packet uniforms were not linked");
                renderer.ResetFault();
            }
            shaderHarness.RenamedUniform = null;
            InvokePrivate(renderer, "UpdateVoxelTexture");
            Assert.IsTrue(GetPrivateField<bool>(renderer, "voxelTextureReady"));

            GetPrivateField<List<VoxelSceneBlockUpdate>>(scene, "pendingBlockUpdates").Add(
                new VoxelSceneBlockUpdate(0, 0, 0, new byte[4], new byte[64], new byte[4], new byte[4]));
            GetPrivateField<List<VoxelSunOccupancyUpdate>>(scene, "pendingSunOccupancyUpdates").Add(
                new VoxelSunOccupancyUpdate(0, 0, 0, 1));
            GetPrivateField<List<VoxelRainSurfaceUpdate>>(scene, "pendingRainSurfaceUpdates").Add(
                new VoxelRainSurfaceUpdate(0, 0, 12.5f));
            InvokePrivate(renderer, "UpdateVoxelTexture");
            InvokePrivate(renderer, "UpdateVoxelTexture");
            SetPrivateField(renderer, "gBufferAvailable", true);
            for (int quality = 0; quality <= 2; quality++)
            {
                SetPrivateField(renderer, "adaptiveQualityLevel", quality);
                VintageRtxConfig config = new()
                {
                    DebugView = quality == 0
                        ? VintageRtxDebugView.Final
                        : VintageRtxDebugView.Reflection
                };
                InvokePrivate(
                    renderer,
                    "RenderDisplayPass",
                    config,
                    17,
                    9,
                    new GameGBuffer(0, 0, 0),
                    config.DebugView,
                    false);
            }
            VoxelSceneSnapshot twoLightSnapshot = GetPrivateField<VoxelSceneSnapshot>(
                renderer,
                "voxelSnapshot");
            int branchFramebufferTexture = GL.GenTexture();
            int branchFramebuffer = GL.GenFramebuffer();
            try
            {
                GL.BindTexture(TextureTarget.Texture2D, branchFramebufferTexture);
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba8,
                    17,
                    9,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    IntPtr.Zero);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, branchFramebuffer);
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D,
                    branchFramebufferTexture,
                    0);
                Assert.AreEqual(
                    FramebufferErrorCode.FramebufferComplete,
                    GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                SetPrivateField(renderer, "voxelTextureReady", false);
                InvokePrivate(
                    renderer,
                    "RenderDisplayPass",
                    new VintageRtxConfig
                    {
                        DebugView = VintageRtxDebugView.Final,
                        TemporalAccumulationEnabled = false
                    },
                    17,
                    9,
                    new GameGBuffer(0, 0, 0),
                    VintageRtxDebugView.Final,
                    false);
            }
            finally
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                GL.DeleteFramebuffer(branchFramebuffer);
                GL.DeleteTexture(branchFramebufferTexture);
            }

            uniforms.PointLightsCount = 2;
            uniforms.PointLights3 = [1, 0, 0, -1, 0, 0];
            uniforms.PointLightColors3 = [3, 0, 0, 0, 3, 0];
            SetPrivateField(renderer, "availableDynamicLightCount", 2);
            SetPrivateField(renderer, "voxelTextureReady", true);
            SetPrivateField(renderer, "adaptiveQualityLevel", 2);
            InvokePrivate(
                renderer,
                "RenderDisplayPass",
                new VintageRtxConfig { DebugView = VintageRtxDebugView.Final },
                17,
                9,
                new GameGBuffer(0, 0, 0),
                VintageRtxDebugView.Final,
                false);
            VoxelSceneSnapshot oneLightSnapshot = twoLightSnapshot with
            {
                Lights = [twoLightSnapshot.Lights[1]]
            };
            SetPrivateField(renderer, "voxelSnapshot", oneLightSnapshot);
            uniforms.PointLightsCount = 1;
            SetPrivateField(renderer, "availableDynamicLightCount", 1);
            SetPrivateField(renderer, "adaptiveQualityLevel", 2);
            for (int frame = 0; frame < 8; frame++)
            {
                InvokePrivate(
                    renderer,
                    "RenderDisplayPass",
                    new VintageRtxConfig { DebugView = VintageRtxDebugView.Material },
                    17,
                    9,
                    new GameGBuffer(0, 0, 0),
                    VintageRtxDebugView.Material,
                    false);
            }
            SetPrivateField(renderer, "voxelSnapshot", twoLightSnapshot);
            foreach (int captureIndex in new[] { 4, 5, 7 })
            {
                VintageRtxDebugView captureView = captureIndex switch
                {
                    4 => VintageRtxDebugView.Reflection,
                    5 => VintageRtxDebugView.VoxelReflection,
                    _ => VintageRtxDebugView.VoxelBounce
                };
                InvokePrivate(
                    renderer,
                    "RenderDisplayPass",
                    new VintageRtxConfig { DebugView = VintageRtxDebugView.Final },
                    17,
                    9,
                    new GameGBuffer(0, 0, 0),
                    captureView,
                    true);
            }
            SetPrivateField(renderer, "gBufferAvailable", true);
            renderer.OnRenderFrame(0.016f, EnumRenderStage.AfterBlit);
            Assert.IsTrue(
                GetPrivateField<bool>(renderer, "gBufferAvailable"),
                "AfterBlit must perform readback only and never rerun transport/G-buffer resolution.");
            TargetInvocationException incompleteFramebuffer = Assert.ThrowsException<TargetInvocationException>(
                () => InvokePrivate(renderer, "ResizeSceneTexture", 0, 0, 2));
            Assert.IsInstanceOfType<InvalidOperationException>(incompleteFramebuffer.InnerException);
            Assert.AreEqual(ErrorCode.NoError, GL.GetError());

            renderer.Dispose();
            renderer = null;
            Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            renderer?.Dispose();
            scene?.Dispose();
            shaderHarness?.Dispose();
            if (lumaFramebuffer != 0)
            {
                GL.DeleteFramebuffer(lumaFramebuffer);
            }
            if (lumaTexture != 0)
            {
                GL.DeleteTexture(lumaTexture);
            }
            if (Directory.Exists(captureRoot))
            {
                Directory.Delete(captureRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Creates display Shader Assets with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Display Shader Assets result consumed by the caller&apos;s assertion.</returns>
    private static IAssetManager CreateDisplayShaderAssets()
    {
        string shaderDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "vintagertx",
            "shaders");
        return Proxy<IAssetManager>((method, arguments) =>
        {
            if (method.Name != "TryGet")
            {
                return Default(method);
            }

            AssetLocation location = (AssetLocation)arguments![0]!;
            string fileName = location.Path.EndsWith("display.vert", StringComparison.Ordinal)
                ? "display.vert"
                : location.Path.EndsWith("display.frag", StringComparison.Ordinal)
                    ? "display.frag"
                    : location.Path.EndsWith("lumarepack.frag", StringComparison.Ordinal)
                        ? "lumarepack.frag"
                    : string.Empty;
            if (fileName.Length == 0)
            {
                return null;
            }

            string path = Path.Combine(shaderDirectory, fileName);
            string source = File.ReadAllText(path);
            return Proxy<IAsset>((assetMethod, _) => assetMethod.Name switch
            {
                "get_Location" => location,
                "get_Name" => fileName,
                "get_Data" => System.Text.Encoding.UTF8.GetBytes(source),
                "IsLoaded" => true,
                "ToText" => source,
                _ => Default(assetMethod)
            });
        });
    }

    /// <summary>
    /// Creates data Path with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="root">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The create Data Path result consumed by the caller&apos;s assertion.</returns>
    private static string CreateDataPath(string root, object?[]? arguments)
    {
        string path = Path.Combine(
            root,
            arguments?[0]?.ToString()?.Replace('/', Path.DirectorySeparatorChar) ?? "data");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Invokes private through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The invoke Private result consumed by the caller&apos;s assertion.</returns>
    private static object? InvokePrivate(object instance, string name, params object?[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, name);
        return method.Invoke(instance, arguments);
    }

    /// <summary>
    /// Executes the exercise Gpu Query Defensive Branches step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    /// <param name="monitor">The monitor input used to configure this deterministic test path.</param>
    private static void ExerciseGpuQueryDefensiveBranches(RenderPerformanceMonitor monitor)
    {
        Type type = typeof(RenderPerformanceMonitor);
        int[] startQueries = GetPrivateField<int[]>(monitor, "startQueries");
        int[] endQueries = GetPrivateField<int[]>(monitor, "endQueries");
        bool[] pendingQueries = GetPrivateField<bool[]>(monitor, "pendingQueries");
        MethodInfo resolve = type.GetMethod(
            "ResolveQueryIfAvailable",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "ResolveQueryIfAvailable");

        int unavailableSlot = 1;
        int originalEnd = endQueries[unavailableSlot];
        int neverIssued = GL.GenQuery();
        try
        {
            endQueries[unavailableSlot] = neverIssued;
            pendingQueries[unavailableSlot] = true;
            resolve.Invoke(monitor, [unavailableSlot]);
            Assert.IsTrue(pendingQueries[unavailableSlot],
                "an unavailable timestamp pair must remain pending");

            SetPrivateField(monitor, "queryIndex", unavailableSlot);
            SetPrivateField(monitor, "gpuQueryFrame", 47);
            Assert.IsFalse(monitor.TryBeginGpuMeasurement(),
                "a pending ring slot must not be overwritten");
        }
        finally
        {
            endQueries[unavailableSlot] = originalEnd;
            pendingQueries[unavailableSlot] = false;
            GL.DeleteQuery(neverIssued);
            while (GL.GetError() != ErrorCode.NoError)
            {
            }
        }

        int nonPositiveSlot = 2;
        int savedEnd = endQueries[nonPositiveSlot];
        endQueries[nonPositiveSlot] = startQueries[nonPositiveSlot];
        pendingQueries[nonPositiveSlot] = true;
        resolve.Invoke(monitor, [nonPositiveSlot]);
        Assert.IsFalse(pendingQueries[nonPositiveSlot]);
        endQueries[nonPositiveSlot] = savedEnd;
    }

    /// <summary>
    /// Returns private Field from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The get Private Field result consumed by the caller&apos;s assertion.</returns>
    private static T GetPrivateField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>
    /// Sets private Field on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    private static void SetPrivateField<T>(object instance, string name, T value)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        field.SetValue(instance, value);
    }

    /// <summary>
    /// Executes the exercise Production Gl State And Frame Allocation step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    private static void ExerciseProductionGlStateAndFrameAllocation()
    {
        int texture2D = GL.GenTexture();
        int texture3D = GL.GenTexture();
        int allocatedFrame = GL.GenTexture();
        int vertexArray = GL.GenVertexArray();
        try
        {
            GL.Enable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.FramebufferSrgb);
            GL.Enable(EnableCap.ScissorTest);
            GL.Viewport(2, 3, 11, 7);
            GL.Scissor(4, 5, 6, 3);
            GL.ColorMask(true, false, true, false);
            GL.BindVertexArray(vertexArray);
            for (int unit = 0; unit <= 7; unit++)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + unit);
                GL.BindTexture(
                    unit is 3 or 4 or 6 ? TextureTarget.Texture3D : TextureTarget.Texture2D,
                    unit is 3 or 4 or 6 ? texture3D : texture2D);
            }
            GL.ActiveTexture(TextureUnit.Texture5);

            Type glStateType = typeof(FilmicDisplayRenderer).GetNestedType(
                "GlState",
                BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(FilmicDisplayRenderer).FullName, "GlState");
            MethodInfo capture = glStateType.GetMethod(
                "Capture",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(glStateType.FullName, "Capture");
            MethodInfo restore = glStateType.GetMethod(
                "Restore",
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(glStateType.FullName, "Restore");
            object state = capture.Invoke(null, null)
                ?? throw new InvalidOperationException("GL state capture returned null.");

            foreach (PropertyInfo property in glStateType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                _ = property.GetValue(state);
            }

            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(true);
            GL.Enable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.FramebufferSrgb);
            GL.Disable(EnableCap.ScissorTest);
            GL.Viewport(0, 0, 17, 9);
            GL.Scissor(0, 0, 17, 9);
            GL.ColorMask(false, true, false, true);
            GL.BindVertexArray(0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            restore.Invoke(state, null);

            Assert.IsTrue(GL.IsEnabled(EnableCap.DepthTest));
            Assert.IsFalse(GL.IsEnabled(EnableCap.Blend));
            Assert.IsTrue(GL.IsEnabled(EnableCap.CullFace));
            Assert.IsTrue(GL.IsEnabled(EnableCap.FramebufferSrgb));
            Assert.IsTrue(GL.IsEnabled(EnableCap.ScissorTest));
            GL.GetBoolean(GetPName.DepthWritemask, out bool depthWrite);
            Assert.IsFalse(depthWrite);
            int[] viewport = new int[4];
            int[] scissor = new int[4];
            bool[] colorMask = new bool[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            GL.GetInteger(GetPName.ScissorBox, scissor);
            GL.GetBoolean(GetPName.ColorWritemask, colorMask);
            CollectionAssert.AreEqual(new[] { 2, 3, 11, 7 }, viewport);
            CollectionAssert.AreEqual(new[] { 4, 5, 6, 3 }, scissor);
            CollectionAssert.AreEqual(new[] { true, false, true, false }, colorMask);
            Assert.AreEqual((int)TextureUnit.Texture5, GL.GetInteger(GetPName.ActiveTexture));
            Assert.AreEqual(vertexArray, GL.GetInteger(GetPName.VertexArrayBinding));

            MethodInfo allocate = typeof(FilmicDisplayRenderer).GetMethod(
                "AllocateFrameTexture",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(
                    typeof(FilmicDisplayRenderer).FullName,
                    "AllocateFrameTexture");
            allocate.Invoke(null, [allocatedFrame, 17, 9]);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D,
                0,
                GetTextureParameter.TextureWidth,
                out int width);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D,
                0,
                GetTextureParameter.TextureHeight,
                out int height);
            Assert.AreEqual(17, width);
            Assert.AreEqual(9, height);
            Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            GL.DepthMask(true);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.FramebufferSrgb);
            GL.ColorMask(true, true, true, true);
            GL.BindVertexArray(0);
            GL.DeleteVertexArray(vertexArray);
            GL.DeleteTexture(texture2D);
            GL.DeleteTexture(texture3D);
            GL.DeleteTexture(allocatedFrame);
        }
    }

    /// <summary>
    /// Executes the exercise Pbr Terrain Atlas step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    private static void ExercisePbrTerrainAtlas()
    {
        int sourceTexture = GL.GenTexture();
        List<int> terrainPrograms = [];
        LoadedTexture? loadedTexture = null;
        PbrTerrainRenderer? renderer = null;
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, sourceTexture);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                4,
                4,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);
            byte[] generatedNormal = CreatePng(new SKColor(128, 128, 255, 255));
            byte[] authoredNormal = CreatePng(new SKColor(156, 111, 241, 255));
            byte[] roughness = CreatePng(new SKColor(91, 91, 91, 255));
            byte[] metallic = CreatePng(new SKColor(220, 220, 220, 255));
            byte[] emissive = CreatePng(new SKColor(37, 37, 37, 255));
            AssetLocation authoredOriginLocation = new("mod", "textures/block/origin_n.png");
            IAsset authoredOriginAsset = CreateAsset(authoredOriginLocation, authoredNormal);
            Dictionary<AssetLocation, IAsset> assets = new()
            {
                [new AssetLocation("mod", "textures/block/normalonly_n.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/normalonly_n.png"), generatedNormal),
                [new AssetLocation("mod", "textures/block/full_n.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/full_n.png"), generatedNormal),
                [new AssetLocation("mod", "textures/block/full_r.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/full_r.png"), roughness),
                [new AssetLocation("mod", "textures/block/full_m.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/full_m.png"), metallic),
                [new AssetLocation("mod", "textures/block/full_e.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/full_e.png"), emissive),
                [authoredOriginLocation] = CreateAsset(authoredOriginLocation, generatedNormal),
                [new AssetLocation("mod", "textures/block/authored_n.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/authored_n.png"), authoredNormal),
                [new AssetLocation("mod", "textures/block/authored_r.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/authored_r.png"), roughness),
                [new AssetLocation("mod", "textures/block/authored_m.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/authored_m.png"), metallic),
                [new AssetLocation("mod", "textures/block/authored_e.png")] =
                    CreateAsset(new AssetLocation("mod", "textures/block/authored_e.png"), emissive)
            };
            PbrManifest manifest = new()
            {
                Schema = "vintagertx.pbr-manifest",
                SchemaVersion = 3,
                Textures =
                [
                    CreateManifestEntry("normalonly", generatedNormal),
                    CreateManifestEntry("full", generatedNormal, roughness, metallic, emissive),
                    CreateManifestEntry("origin", generatedNormal)
                ]
            };
            IAsset manifestAsset = CreateAsset(
                new AssetLocation("mod", "config/vintagertx/pbr-manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest));
            IAssetOrigin authoredOrigin = Proxy<IAssetOrigin>((method, arguments) => method.Name switch
            {
                "GetAssets" when ((AssetLocation)arguments![0]!).Equals(authoredOriginLocation) =>
                    new List<IAsset> { authoredOriginAsset },
                "GetAssets" => new List<IAsset>(),
                _ => Default(method)
            });
            ILogger logger = Proxy<ILogger>((method, _) => Default(method));
            IRenderAPI renderApi = Proxy<IRenderAPI>((method, arguments) => method.Name switch
            {
                "CheckGlError" => CheckGlError(arguments),
                _ => Default(method)
            });
            IAssetManager assetManager = Proxy<IAssetManager>((method, arguments) => method.Name switch
            {
                "get_AllAssets" => assets,
                "get_Origins" => new List<IAssetOrigin> { authoredOrigin },
                "GetManyInCategory" => new List<IAsset> { manifestAsset },
                "TryGet" => assets.TryGetValue((AssetLocation)arguments![0]!, out IAsset? asset)
                    ? asset
                    : null,
                _ => Default(method)
            });
            IClientWorldAccessor world = Proxy<IClientWorldAccessor>((method, _) => method.Name switch
            {
                "get_Blocks" => new List<Block>(),
                _ => Default(method)
            });
            IShaderProgram? terrainProgram = null;
            IShaderAPI shaderApi = Proxy<IShaderAPI>((method, _) => method.Name switch
            {
                "GetProgram" => terrainProgram,
                _ => Default(method)
            });
            IBlockTextureAtlasAPI atlas = null!;
            ICoreClientAPI api = Proxy<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_BlockTextureAtlas" => atlas,
                "get_Assets" => assetManager,
                "get_Logger" => logger,
                "get_Render" => renderApi,
                "get_Shader" => shaderApi,
                "get_World" => world,
                _ => Default(method)
            });
            loadedTexture = new LoadedTexture(api)
            {
                TextureId = sourceTexture,
                Width = 4,
                Height = 4
            };
            TextureAtlasPosition fullAtlas = new()
            {
                atlasTextureId = sourceTexture,
                atlasNumber = 0,
                x1 = 0,
                y1 = 0,
                x2 = 1,
                y2 = 1
            };
            TextureAtlasPosition unknownAtlas = new()
            {
                atlasTextureId = sourceTexture,
                atlasNumber = 1,
                x1 = 0,
                y1 = 0,
                x2 = 1,
                y2 = 1
            };
            atlas = Proxy<IBlockTextureAtlasAPI>((method, _) => method.Name switch
            {
                "get_AtlasTextures" => new List<LoadedTexture> { loadedTexture },
                "get_UnknownTexturePosition" => unknownAtlas,
                "get_Positions" => Array.Empty<TextureAtlasPosition>(),
                "get_Item" => fullAtlas,
                _ => Default(method)
            });

            PbrSidecarAssetStore sidecars = PbrSidecarAssetStore.Capture(assetManager, logger);
            renderer = new PbrTerrainRenderer(api, sidecars);
            renderer.OnRenderFrame(0.0f, EnumRenderStage.Opaque);
            StringAssert.Contains(renderer.Status, "file-backed 4x4 PBR atlas");
            FieldInfo materialField = typeof(PbrTerrainRenderer).GetField(
                "materialAtlasTexture",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(PbrTerrainRenderer).FullName, "materialAtlasTexture");
            Assert.IsTrue((int)(materialField.GetValue(renderer) ?? 0) > 0);

            SetPrivateField(renderer, "sourceAtlasWidth", 0);
            SetPrivateField(renderer, "sourceAtlasHeight", 0);
            TargetInvocationException incompleteAtlas = Assert.ThrowsException<TargetInvocationException>(
                () => InvokePrivate(renderer, "CreateNeutralAtlas", new float[] { 0.5f, 0.5f, 0.5f, 0.0f }));
            Assert.IsInstanceOfType<InvalidOperationException>(incompleteAtlas.InnerException);
            SetPrivateField(renderer, "sourceAtlasWidth", 4);
            SetPrivateField(renderer, "sourceAtlasHeight", 4);
            while (GL.GetError() != ErrorCode.NoError)
            {
            }

            int enabledOnly = CreateTerrainProgram(includeSampler: false, includeEnabled: true);
            int complete = CreateTerrainProgram(includeSampler: true, includeEnabled: true);
            int samplerOnly = CreateTerrainProgram(includeSampler: true, includeEnabled: false);
            terrainPrograms.AddRange([enabledOnly, complete, samplerOnly]);

            terrainProgram = CreateShaderProgram(enabledOnly);
            InvokePrivate(renderer, "BindTerrainShader");
            StringAssert.Contains(renderer.Status, "chunk shader override unavailable");

            int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
            int previousActiveTexture = GL.GetInteger(GetPName.ActiveTexture);
            terrainProgram = CreateShaderProgram(complete);
            InvokePrivate(renderer, "BindTerrainShader");
            Assert.AreEqual(previousProgram, GL.GetInteger(GetPName.CurrentProgram));
            Assert.AreEqual(previousActiveTexture, GL.GetInteger(GetPName.ActiveTexture));

            terrainProgram = CreateShaderProgram(samplerOnly);
            InvokePrivate(renderer, "BindTerrainShader");
            Assert.AreEqual(ErrorCode.NoError, GL.GetError());
            renderer.Dispose();
            renderer = null;
            Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            renderer?.Dispose();
            if (loadedTexture is not null)
            {
                // The source atlas is borrowed. Neutralize and dispose its
                // managed wrapper while the GL context is still current so no
                // finalizer can touch a destroyed GLFW context later.
                loadedTexture.TextureId = 0;
                loadedTexture.Dispose();
            }
            foreach (int program in terrainPrograms)
            {
                GL.DeleteProgram(program);
            }
            GL.DeleteTexture(sourceTexture);
        }
    }

    /// <summary>
    /// Creates asset with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="location">The location input used to configure this deterministic test path.</param>
    /// <param name="data">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The create Asset result consumed by the caller&apos;s assertion.</returns>
    private static IAsset CreateAsset(AssetLocation location, byte[] data) =>
        Proxy<IAsset>((method, _) => method.Name switch
        {
            "get_Location" => location,
            "get_Name" => location.Path,
            "get_Data" => data,
            "IsLoaded" => true,
            _ => Default(method)
        });

    /// <summary>
    /// Creates manifest Entry with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="normal">The normal input used to configure this deterministic test path.</param>
    /// <param name="roughness">The roughness input used to configure this deterministic test path.</param>
    /// <param name="metallic">The metallic input used to configure this deterministic test path.</param>
    /// <param name="emissive">The emissive input used to configure this deterministic test path.</param>
    /// <returns>The create Manifest Entry result consumed by the caller&apos;s assertion.</returns>
    private static PbrManifestTexture CreateManifestEntry(
        string name,
        byte[] normal,
        byte[]? roughness = null,
        byte[]? metallic = null,
        byte[]? emissive = null) =>
        new()
        {
            Source = new PbrSourceReference
            {
                Domain = "mod",
                Path = $"textures/block/{name}.png"
            },
            Normal = CreateMapReference(name, "n", normal),
            Roughness = CreateMapReference(name, "r", roughness),
            Metallic = CreateMapReference(name, "m", metallic),
            Emissive = CreateMapReference(name, "e", emissive)
        };

    /// <summary>
    /// Creates map Reference with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="suffix">Coordinate component in the space defined by the tested API.</param>
    /// <param name="data">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The create Map Reference result consumed by the caller&apos;s assertion.</returns>
    private static PbrMapReference CreateMapReference(string name, string suffix, byte[]? data) =>
        data is null
            ? new PbrMapReference()
            : new PbrMapReference
            {
                Asset = $"textures/block/{name}_{suffix}.png",
                Sha256 = Convert.ToHexString(SHA256.HashData(data))
            };

    /// <summary>
    /// Creates png with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="color">The color input used to configure this deterministic test path.</param>
    /// <returns>The create Png result consumed by the caller&apos;s assertion.</returns>
    private static byte[] CreatePng(SKColor color)
    {
        using SKBitmap bitmap = new(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// Creates terrain Program with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="includeSampler">The include Sampler input used to configure this deterministic test path.</param>
    /// <param name="includeEnabled">The include Enabled input used to configure this deterministic test path.</param>
    /// <returns>The create Terrain Program result consumed by the caller&apos;s assertion.</returns>
    private static int CreateTerrainProgram(bool includeSampler, bool includeEnabled)
    {
        const string vertexSource = """
            #version 430 core
            void main() {
                vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        string sampler = includeSampler
            ? "uniform sampler2D vintagertxMaterialTex;"
            : string.Empty;
        string enabled = includeEnabled
            ? "uniform int vintagertxPbrEnabled;"
            : string.Empty;
        string sample = includeSampler
            ? "texture(vintagertxMaterialTex, vec2(0.5))"
            : "vec4(0.0)";
        string flag = includeEnabled
            ? "float(vintagertxPbrEnabled) * 0.001"
            : "0.0";
        string fragmentSource = $$"""
            #version 430 core
            {{sampler}}
            {{enabled}}
            layout(location = 0) out vec4 outColor;
            void main() { outColor = {{sample}} + vec4({{flag}}); }
            """;

        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        try
        {
            int program = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            Assert.AreEqual(1, linked, GL.GetProgramInfoLog(program));
            return program;
        }
        finally
        {
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
        }
    }

    /// <summary>
    /// Compiles shader and surfaces shader or compiler diagnostics instead of hiding a failed test setup.
    /// </summary>
    /// <param name="type">The type input used to configure this deterministic test path.</param>
    /// <param name="source">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The compile Shader result consumed by the caller&apos;s assertion.</returns>
    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        Assert.AreEqual(1, compiled, GL.GetShaderInfoLog(shader));
        return shader;
    }

    /// <summary>
    /// Creates shader Program with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="programId">The program Id input used to configure this deterministic test path.</param>
    /// <returns>The create Shader Program result consumed by the caller&apos;s assertion.</returns>
    private static IShaderProgram CreateShaderProgram(int programId) =>
        Proxy<IShaderProgram>((method, _) => method.Name switch
        {
            "get_ProgramId" => programId,
            "get_Disposed" => false,
            _ => Default(method)
        });

    /// <summary>
    /// Executes the check Gl Error step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The check Gl Error result consumed by the caller&apos;s assertion.</returns>
    private static object? CheckGlError(object?[]? arguments)
    {
        ErrorCode error = GL.GetError();
        Assert.AreEqual(ErrorCode.NoError, error, arguments?[0]?.ToString());
        return null;
    }

    /// <summary>
    /// Executes the proxy step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    /// <param name="handler">The handler input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The proxy result consumed by the caller&apos;s assertion.</returns>
    private static T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        return VintageRTX.Test.RuntimeCoverageDispatchProxy.Create<T>(handler);
    }

    /// <summary>
    /// Executes the default step used by the deterministic render Lab Explorer Tests fixture.
    /// </summary>
    /// <param name="method">The method input used to configure this deterministic test path.</param>
    /// <returns>The default result consumed by the caller&apos;s assertion.</returns>
    private static object? Default(MethodInfo method)
    {
        return method.ReturnType == typeof(void) || !method.ReturnType.IsValueType
            ? null
            : Activator.CreateInstance(method.ReturnType);
    }

    /// <summary>
    /// Coordinates the production Shader Api harness while keeping filesystem, game, and graphics state isolated.
    /// </summary>
    private sealed class ProductionShaderApiHarness : IDisposable
    {
        private readonly List<ProgramState> programs = [];

        /// <summary>
        /// Initializes a new production Shader Api Harness fixture with the dependencies required for isolated execution.
        /// </summary>
        public ProductionShaderApiHarness()
        {
            ShaderApi = Proxy<IShaderAPI>((method, arguments) => method.Name switch
            {
                "IsGLSLVersionSupported" => true,
                "NewShader" => CreateShader((EnumShaderType)arguments![0]!),
                "NewShaderProgram" => CreateProgram(),
                "RegisterMemoryShaderProgram" => 0,
                _ => Default(method)
            });
        }

        /// <summary>
        /// Gets the shader Api value exposed to the deterministic fixture.
        /// </summary>
        public IShaderAPI ShaderApi { get; }

        /// <summary>
        /// Gets the program Ids value exposed to the deterministic fixture.
        /// </summary>
        public IReadOnlyList<int> ProgramIds => programs.Select(static state => state.ProgramId).ToArray();

        /// <summary>
        /// Gets or sets the renamed Uniform value exposed to the deterministic fixture.
        /// </summary>
        public string? RenamedUniform { get; set; }

        /// <summary>
        /// Releases resources owned by production Shader Api Harness; cleanup remains safe after partial fixture initialization.
        /// </summary>
        public void Dispose()
        {
            foreach (ProgramState state in programs)
            {
                state.Dispose();
            }
            programs.Clear();
        }

        /// <summary>
        /// Creates shader with deterministic defaults suitable for isolated assertions.
        /// </summary>
        /// <param name="type">The type input used to configure this deterministic test path.</param>
        /// <returns>The create Shader result consumed by the caller&apos;s assertion.</returns>
        private static IShader CreateShader(EnumShaderType type)
        {
            string code = string.Empty;
            string prefix = string.Empty;
            return Proxy<IShader>((method, arguments) => method.Name switch
            {
                "get_Code" => code,
                "set_Code" => Set(ref code, (string)arguments![0]!),
                "get_PrefixCode" => prefix,
                "set_PrefixCode" => Set(ref prefix, (string)arguments![0]!),
                "get_Type" => type,
                _ => Default(method)
            });
        }

        /// <summary>
        /// Creates program with deterministic defaults suitable for isolated assertions.
        /// </summary>
        /// <returns>The create Program result consumed by the caller&apos;s assertion.</returns>
        private IShaderProgram CreateProgram()
        {
            ProgramState state = new(RenamedUniform);
            programs.Add(state);
            return state.Program;
        }

        /// <summary>
        /// Sets requested fixture operation on the test double without invoking unrelated production side effects.
        /// </summary>
        /// <param name="target">The target input used to configure this deterministic test path.</param>
        /// <param name="value">The value input used to configure this deterministic test path.</param>
        /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
        /// <returns>The set result consumed by the caller&apos;s assertion.</returns>
        private static object? Set<T>(ref T target, T value)
        {
            target = value;
            return null;
        }

        /// <summary>
        /// Supports program State within the deterministic VintageRTX test infrastructure.
        /// </summary>
        private sealed class ProgramState : IDisposable
        {
            private readonly string? renamedUniform;
            private IShader? vertexShader;
            private IShader? fragmentShader;
            private bool loadError;

            /// <summary>
            /// Initializes a new program State fixture with the dependencies required for isolated execution.
            /// </summary>
            /// <param name="renamedUniform">Stable identifier selecting the deterministic fixture case.</param>
            public ProgramState(string? renamedUniform)
            {
                this.renamedUniform = renamedUniform;
                Program = Proxy<IShaderProgram>(Invoke);
            }

            /// <summary>
            /// Gets the program value exposed to the deterministic fixture.
            /// </summary>
            public IShaderProgram Program { get; }

            /// <summary>
            /// Gets or sets the program Id value exposed to the deterministic fixture.
            /// </summary>
            public int ProgramId { get; private set; }

            /// <summary>
            /// Releases resources owned by program State; cleanup remains safe after partial fixture initialization.
            /// </summary>
            public void Dispose()
            {
                if (ProgramId != 0)
                {
                    GL.DeleteProgram(ProgramId);
                    ProgramId = 0;
                }
            }

            /// <summary>
            /// Invokes requested fixture operation through the fixture reflection boundary and propagates failures to the calling assertion.
            /// </summary>
            /// <param name="method">The method input used to configure this deterministic test path.</param>
            /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
            /// <returns>The invoke result consumed by the caller&apos;s assertion.</returns>
            private object? Invoke(MethodInfo method, object?[]? arguments)
            {
                return method.Name switch
                {
                    "get_VertexShader" => vertexShader,
                    "set_VertexShader" => Set(ref vertexShader, (IShader)arguments![0]!),
                    "get_FragmentShader" => fragmentShader,
                    "set_FragmentShader" => Set(ref fragmentShader, (IShader)arguments![0]!),
                    "get_ProgramId" => ProgramId,
                    "get_LoadError" => loadError,
                    "get_Disposed" => ProgramId == 0 && loadError,
                    "Compile" => Compile(),
                    "Use" => Use(),
                    "Stop" => Stop(),
                    _ => Default(method)
                };
            }

            /// <summary>
            /// Compiles requested fixture operation and surfaces shader or compiler diagnostics instead of hiding a failed test setup.
            /// </summary>
            /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
            private bool Compile()
            {
                ArgumentNullException.ThrowIfNull(vertexShader);
                ArgumentNullException.ThrowIfNull(fragmentShader);
                int vertex = CompileStage(ShaderType.VertexShader, vertexShader.Code);
                string fragmentSource = renamedUniform is null
                    ? fragmentShader.Code
                    : fragmentShader.Code.Replace(
                        renamedUniform,
                        renamedUniform + "HarnessMissing",
                        StringComparison.Ordinal);
                int fragment = CompileStage(ShaderType.FragmentShader, fragmentSource);
                try
                {
                    ProgramId = GL.CreateProgram();
                    GL.AttachShader(ProgramId, vertex);
                    GL.AttachShader(ProgramId, fragment);
                    GL.LinkProgram(ProgramId);
                    GL.GetProgram(ProgramId, GetProgramParameterName.LinkStatus, out int linked);
                    loadError = linked == 0;
                    if (loadError)
                    {
                        Assert.Fail($"Production shader link failed: {GL.GetProgramInfoLog(ProgramId)}");
                    }
                    return !loadError;
                }
                finally
                {
                    GL.DeleteShader(vertex);
                    GL.DeleteShader(fragment);
                }
            }

            /// <summary>
            /// Executes the use step used by the deterministic program State fixture.
            /// </summary>
            /// <returns>The use result consumed by the caller&apos;s assertion.</returns>
            private object? Use()
            {
                GL.UseProgram(ProgramId);
                return null;
            }

            /// <summary>
            /// Executes the stop step used by the deterministic program State fixture.
            /// </summary>
            /// <returns>The stop result consumed by the caller&apos;s assertion.</returns>
            private static object? Stop()
            {
                GL.UseProgram(0);
                return null;
            }

            /// <summary>
            /// Compiles stage and surfaces shader or compiler diagnostics instead of hiding a failed test setup.
            /// </summary>
            /// <param name="type">The type input used to configure this deterministic test path.</param>
            /// <param name="source">Caller-owned input consumed only for the duration of this test step.</param>
            /// <returns>The compile Stage result consumed by the caller&apos;s assertion.</returns>
            private static int CompileStage(ShaderType type, string source)
            {
                int shader = GL.CreateShader(type);
                GL.ShaderSource(shader, source);
                GL.CompileShader(shader);
                GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                if (compiled == 0)
                {
                    string error = GL.GetShaderInfoLog(shader);
                    GL.DeleteShader(shader);
                    Assert.Fail($"Production {type} compile failed: {error}");
                }
                return shader;
            }
        }
    }
}
