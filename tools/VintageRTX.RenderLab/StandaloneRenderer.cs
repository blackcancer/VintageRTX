using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using SkiaSharp;
using VintageRTX.Configuration;
using VintageRTX.Rendering;

namespace VintageRTX.RenderLab;

/// <summary>
/// Carries render Lab Report measurements between the renderer and its assertions; property units follow their tested API contracts.
/// </summary>
internal sealed record RenderLabReport(
    string FinalCapture,
    IReadOnlyDictionary<string, string> DiagnosticCaptures,
    int Width,
    int Height,
    int BenchmarkFrames,
    double AverageGpuMilliseconds,
    double P99GpuMilliseconds,
    double OnePercentLowFps,
    string Renderer,
    string OpenGlVersion,
    PbrSeparationReport PbrSeparation,
    WaterVolumeReport WaterVolumes,
    DynamicLiquidSurfaceReport DynamicLiquidSurface);

/// <summary>Measured SI-solver state actually sampled by the production fragment shader.</summary>
internal sealed record DynamicLiquidSurfaceReport(
    int ActiveCellCount,
    int DisturbedCellCount,
    double MaximumAbsoluteHeightMetres,
    double MaximumNormalSlope,
    double MaximumTransientEmission,
    double TotalMechanicalEnergyJoules);

/// <summary>
/// Supports standalone Renderer within the deterministic VintageRTX test infrastructure.
/// </summary>
internal sealed partial class StandaloneRenderer : IDisposable
{
    /// <summary>
    /// Fixed shader clock in seconds. It keeps wind-driven waves and lava bursts identical across captures.
    /// </summary>
    internal const float DeterministicRendererTimeSeconds = 137.25f;

    /// <summary>
    /// Fixed horizontal world-space wind sample on the renderer X axis.
    /// </summary>
    internal const float DeterministicLiquidWindX = 0.65f;

    /// <summary>
    /// Fixed horizontal world-space wind sample on the renderer Z axis.
    /// </summary>
    internal const float DeterministicLiquidWindZ = -0.35f;

    private readonly RenderLabOptions options;
    private readonly SyntheticScene scene;
    private readonly List<int> textures = [];
    private int program;
    private int framebuffer;
    private int outputTexture;
    private int vertexArray;
    private DynamicLiquidSurfaceReport dynamicLiquidSurfaceReport = new(0, 0, 0, 0, 0, 0);
    private bool disposed;

    /// <summary>
    /// Initializes a new standalone Renderer fixture with the dependencies required for isolated execution.
    /// </summary>
    /// <param name="options">The options input used to configure this deterministic test path.</param>
    public StandaloneRenderer(RenderLabOptions options)
    {
        this.options = options;
        Directory.CreateDirectory(options.OutputDirectory);
        Stopwatch phase = Stopwatch.StartNew();
        scene = new SyntheticScene(options.Width, options.Height);
        RecordPhase("synthetic-scene", phase);
        phase.Restart();
        try { InitializeGlResources(); }
        catch { Dispose(); throw; }
        RecordPhase("GL-initialization", phase);
    }

    /// <summary>
    /// Executes requested fixture operation as an isolated test step and propagates failures to the owning suite.
    /// </summary>
    /// <returns>The run result consumed by the caller&apos;s assertion.</returns>
    public RenderLabReport Run()
    {
        Stopwatch phase = Stopwatch.StartNew();
        Dictionary<string, string> captures = new(StringComparer.OrdinalIgnoreCase);
        string sourcePath = Path.Combine(options.OutputDirectory, "source-raster.png");
        SavePixels(sourcePath, scene.SourceColor);
        captures["source-raster"] = sourcePath;
        string opaqueReferencePath = Path.Combine(options.OutputDirectory, "opaque-reference.png");
        SavePixels(opaqueReferencePath, scene.OpaqueReferenceColor);
        captures["opaque-reference"] = opaqueReferencePath;
        (string Label, VintageRtxDebugView View)[] views =
        [
            ("final", VintageRtxDebugView.Final),
            ("normal", VintageRtxDebugView.Normal),
            ("material", VintageRtxDebugView.Material),
            ("reflection", VintageRtxDebugView.Reflection),
            ("voxel-reflection", VintageRtxDebugView.VoxelReflection),
            ("water", VintageRtxDebugView.Water),
            ("voxel-shadow", VintageRtxDebugView.VoxelShadow)
        ];
        string finalCapture = string.Empty;
        byte[]? finalPixels = null;
        foreach ((string label, VintageRtxDebugView view) in views)
        {
            RenderFrame(view, 0);
            GL.Finish();
            string path = Path.Combine(options.OutputDirectory, $"{label}.png");
            byte[] pixels = SaveCurrentFrame(path);
            captures[label] = path;
            if (view == VintageRtxDebugView.Final)
            {
                finalCapture = path;
                finalPixels = pixels;
            }
        }

        byte[] capturedFinal = finalPixels
            ?? throw new InvalidOperationException("Standalone final pixels were not captured.");
        WaterVolumeAnalysis liquids = WaterVolumeAnalyzer.Analyze(scene, capturedFinal);
        string liquidVolumePath = Path.Combine(options.OutputDirectory, "liquid-volumes.png");
        SavePixels(liquidVolumePath, liquids.DiagnosticPixels);
        captures["liquid-volumes"] = liquidVolumePath;
        WaterVolumeAnalyzer.Print(liquids.Report);

        // PBR energy must be compared in the same scene-linear domain as the
        // decoded source texture. The ordinary final capture includes the
        // standalone filmic curve, display grade and vignette, so comparing it
        // directly to the linear source measures presentation as if it were
        // BRDF loss. Capture the production Luma-domain branch separately;
        // RGBA8 stores its linear transport through one sRGB encoding, which
        // the analyzer decodes before accumulating luminance.
        RenderFrame(VintageRtxDebugView.Final, 0, outputColorDomain: 1);
        GL.Finish();
        string pbrTransportPath = Path.Combine(
            options.OutputDirectory,
            "pbr-transport.png");
        byte[] pbrTransportPixels = SaveCurrentFrame(pbrTransportPath);
        captures["pbr-transport"] = pbrTransportPath;
        PbrSeparationAnalysis pbr = PbrSeparationAnalyzer.Analyze(
            scene,
            pbrTransportPixels);
        string responsePath = Path.Combine(options.OutputDirectory, "pbr-response.png");
        SavePixels(responsePath, pbr.ResponsePixels);
        captures["pbr-response"] = responsePath;
        string classesPath = Path.Combine(options.OutputDirectory, "pbr-classes.png");
        SavePixels(classesPath, pbr.ClassPixels);
        captures["pbr-classes"] = classesPath;
        PbrSeparationAnalyzer.Print(pbr.Report);

        RecordPhase("diagnostic-draws-readback-analysis", phase);
        phase.Restart();
        // Diagnostic readback and PNG compression are outside the timing
        // window. Warm the exact final shader before allocating timer queries.
        for (int index = 0; index < 30; index++)
        {
            RenderFrame(VintageRtxDebugView.Final, index + 1);
        }
        GL.Finish();

        RecordPhase("warmup-30-frames", phase);
        phase.Restart();
        double[] gpuMilliseconds = MeasureGpuFrames(options.BenchmarkFrames);
        RecordPhase("benchmark-all-passes", phase);
        Console.WriteLine($"Transport draw count: {transportDrawCount} (three per frame, included in GPU queries).");
        Array.Sort(gpuMilliseconds);
        double average = gpuMilliseconds.Average();
        int p99Index = Math.Clamp((int)Math.Ceiling(gpuMilliseconds.Length * 0.99) - 1, 0, gpuMilliseconds.Length - 1);
        double p99 = gpuMilliseconds[p99Index];
        double onePercentLow = p99 > 0.000001 ? 1000.0 / p99 : 0.0;
        RenderLabReport report = new(
            finalCapture,
            captures,
            options.Width,
            options.Height,
            options.BenchmarkFrames,
            average,
            p99,
            onePercentLow,
            GL.GetString(StringName.Renderer) ?? "unknown",
            GL.GetString(StringName.Version) ?? "unknown",
            pbr.Report,
            liquids.Report,
            dynamicLiquidSurfaceReport);
        File.WriteAllText(
            Path.Combine(options.OutputDirectory, "report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    /// <summary>
    /// Executes the measure Gpu Frames step used by the deterministic standalone Renderer fixture.
    /// </summary>
    /// <param name="count">Bounded fixture count, index, or offset used to select the exercised case.</param>
    /// <returns>The measure Gpu Frames result consumed by the caller&apos;s assertion.</returns>
    private double[] MeasureGpuFrames(int count)
    {
        int[] queries = new int[count];
        double[] milliseconds = new double[count];
        GL.GenQueries(count, queries);
        Stopwatch cpu = Stopwatch.StartNew();
        for (int index = 0; index < count; index++)
        {
            GL.BeginQuery(QueryTarget.TimeElapsed, queries[index]);
            RenderFrame(VintageRtxDebugView.Final, index + 31);
            GL.EndQuery(QueryTarget.TimeElapsed);
        }
        GL.Finish();
        cpu.Stop();
        for (int index = 0; index < count; index++)
        {
            GL.GetQueryObject(queries[index], GetQueryObjectParam.QueryResult, out long nanoseconds);
            milliseconds[index] = nanoseconds / 1_000_000.0;
        }
        GL.DeleteQueries(count, queries);
        Console.WriteLine(
            $"Standalone dispatch: {count} frames in {cpu.Elapsed.TotalMilliseconds:0.0}ms CPU/GPU synchronized.");
        return milliseconds;
    }

    /// <summary>
    /// Executes the initialize Gl Resources step used by the deterministic standalone Renderer fixture.
    /// </summary>
    private void InitializeGlResources()
    {
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.FramebufferSrgb);
        DisplayShaderProgramSource shaderSource = DisplayShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory);
        program = LinkProgram(shaderSource.Vertex, shaderSource.Fragment);
        vertexArray = GL.GenVertexArray();

        int source = CreateTexture2D(0, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte,
            options.Width, options.Height, scene.SourceColor, TextureMinFilter.Linear, TextureMagFilter.Linear);
        CreateTexture2D(1, PixelInternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float,
            options.Width, options.Height, scene.NormalRoughness, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture2D(2, PixelInternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float,
            options.Width, options.Height, scene.ViewPosition, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture3D(3, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte,
            SyntheticScene.VoxelWidth, SyntheticScene.VoxelHeight, SyntheticScene.VoxelDepth,
            scene.Voxels, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture3D(4, PixelInternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte,
            SyntheticScene.VoxelWidth * SyntheticScene.OccupancyScale,
            SyntheticScene.VoxelHeight * SyntheticScene.OccupancyScale,
            SyntheticScene.VoxelDepth * SyntheticScene.OccupancyScale,
            scene.Occupancy, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        BindTextureUnit(5, TextureTarget.Texture2D, source);
        CreateTexture3D(6, PixelInternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte,
            SyntheticScene.LightCasterScale,
            SyntheticScene.LightCasterScale,
            SyntheticScene.LightCasterScale * SyntheticScene.MaximumLights,
            scene.LightCasterMasks, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture2D(7, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte,
            SyntheticScene.VoxelWidth, SyntheticScene.VoxelDepth, scene.FluidSurface,
            TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture2D(8, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte,
            options.Width, options.Height, scene.Material, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture3D(9, PixelInternalFormat.Rgb8, PixelFormat.Rgb, PixelType.UnsignedByte,
            SyntheticScene.VoxelWidth, SyntheticScene.VoxelHeight, SyntheticScene.VoxelDepth,
            scene.Irradiance, TextureMinFilter.Linear, TextureMagFilter.Linear);
        CreateTexture3D(10, PixelInternalFormat.Rgb8, PixelFormat.Rgb, PixelType.UnsignedByte,
            SyntheticScene.VoxelWidth, SyntheticScene.VoxelHeight, SyntheticScene.VoxelDepth,
            scene.IrradianceDirection, TextureMinFilter.Linear, TextureMagFilter.Linear);
        CreateTexture3D(11, PixelInternalFormat.Rg32ui, PixelFormat.RgInteger, PixelType.UnsignedInt,
            SyntheticScene.SunWidth, SyntheticScene.SunHeight, SyntheticScene.SunDepth,
            scene.SunOccupancy, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture2D(12, PixelInternalFormat.R32f, PixelFormat.Red, PixelType.Float,
            SyntheticScene.VoxelWidth, SyntheticScene.VoxelDepth, scene.RainSurface,
            TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture3D(13, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte,
            SyntheticScene.VoxelWidth, SyntheticScene.VoxelHeight, SyntheticScene.VoxelDepth,
            scene.LiquidMetadata, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        CreateTexture2D(14, PixelInternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float,
            LiquidOpticalRegistry.LookupWidth, LiquidOpticalRegistry.LookupHeight,
            scene.LiquidOpticalProfiles, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        float[] dynamicLiquidState = BuildDynamicLiquidState();
        dynamicLiquidTexture = CreateTexture2D(15, PixelInternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float,
            SyntheticScene.VoxelWidth * LiquidSurfaceSimulation.RecommendedCellsPerBlock,
            SyntheticScene.VoxelDepth * LiquidSurfaceSimulation.RecommendedCellsPerBlock,
            dynamicLiquidState,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear);

        // Allocation uses the active unit. Preserve the just-uploaded liquid texture instead
        // of making the shader sample its own final render target on unit fifteen.
        int allocationBinding = GL.GetInteger(GetPName.TextureBinding2D);
        outputTexture = GL.GenTexture();
        textures.Add(outputTexture);
        GL.BindTexture(TextureTarget.Texture2D, outputTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            options.Width, options.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, outputTexture, 0);
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"Standalone framebuffer incomplete: {status}.");
        }

        GL.BindTexture(TextureTarget.Texture2D, allocationBinding);
        GL.UseProgram(program);
        string[] samplerNames =
        [
            "sourceColor", "gNormal", "gPosition", "voxelVolume", "voxelOccupancy",
            "historyColor", "voxelLightCasterMasks", "voxelFluidSurface", "gMaterial",
            "voxelIrradiance", "voxelIrradianceDirection", "voxelSunOccupancy", "voxelRainSurface",
            "voxelLiquidMetadata", "liquidOpticalProfiles", "dynamicLiquidSurface"
        ];
        for (int unit = 0; unit < samplerNames.Length; unit++)
        {
            Set(samplerNames[unit], unit);
        }
        // The production renderer exposes both the clean opaque snapshot and
        // the live deferred position buffer. RenderLab has no first-person
        // pass, so both views intentionally reference the same position
        // texture. Leaving this sampler at OpenGL's default unit zero makes
        // source RGB look like near-camera geometry and bypasses every final
        // PBR transport branch as a false held-item overlay.
        SetRequired("gDirectPosition", 2);
        SetRequired("reflectionSourceColor", 0);
        BindStaticUniforms();
        InitializeTransportPipeline();
        ValidateTextureOwnership(outputTexture);
        CheckGl("standalone initialization");
    }

    /// <summary>
    /// Executes the bind Static Uniforms step used by the deterministic standalone Renderer fixture.
    /// </summary>
    private void BindStaticUniforms()
    {
        float near = 0.1f;
        float far = 100.0f;
        float f = 1.0f / MathF.Tan(MathF.PI / 6.0f);
        float aspect = options.Width / (float)options.Height;
        float[] projection =
        [
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, (2 * far * near) / (near - far), 0
        ];
        Vector3 right = scene.CameraRight;
        Vector3 up = scene.CameraUp;
        Vector3 backward = -scene.CameraForward;
        float[] inverseView =
        [
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            backward.X, backward.Y, backward.Z, 0,
            0, 0, 0, 1
        ];
        SetMatrix("projection", projection);
        SetMatrix("inverseViewMatrix", inverseView);
        float[] view =
        [
            right.X, up.X, backward.X, 0,
            right.Y, up.Y, backward.Y, 0,
            right.Z, up.Z, backward.Z, 0,
            0, 0, 0, 1
        ];
        SetMatrix("viewMatrix", view);
        Matrix4x4 p = new(projection[0], projection[1], projection[2], projection[3],
            projection[4], projection[5], projection[6], projection[7],
            projection[8], projection[9], projection[10], projection[11],
            projection[12], projection[13], projection[14], projection[15]);
        if (!Matrix4x4.Invert(p, out Matrix4x4 inverse))
            throw new InvalidOperationException("Synthetic perspective projection is singular.");
        SetMatrix("inverseProjection", [inverse.M11, inverse.M12, inverse.M13, inverse.M14,
            inverse.M21, inverse.M22, inverse.M23, inverse.M24,
            inverse.M31, inverse.M32, inverse.M33, inverse.M34,
            inverse.M41, inverse.M42, inverse.M43, inverse.M44]);
        Set("inverseFrameSize", 1.0f / options.Width, 1.0f / options.Height);
        Set("cameraWorldPosition", SyntheticScene.CameraPosition);
        // Runtime view positions are relative to the player's floating origin.
        // The authored lab camera is that origin, so supply it explicitly and
        // reconstruct the same absolute coordinates consumed by voxel fields.
        Set("floatingWorldOrigin", SyntheticScene.CameraPosition);
        Set("voxelOrigin", Vector3.Zero);
        Set("voxelSize", new Vector3(SyntheticScene.VoxelWidth, SyntheticScene.VoxelHeight, SyntheticScene.VoxelDepth));
        Set("sunVoxelOrigin", Vector3.Zero);
        Set("sunVoxelSize", new Vector3(SyntheticScene.SunWidth, SyntheticScene.SunHeight, SyntheticScene.SunDepth));
        Set("sunOccupancyScale", 2.0f);
        Set("rainSurfaceOrigin", 0.0f, 0.0f);
        Set("rainSurfaceSize", SyntheticScene.VoxelWidth, SyntheticScene.VoxelDepth);
        SetRequired("rendererTimeSeconds", DeterministicRendererTimeSeconds);
        SetRequired("liquidWindVector", DeterministicLiquidWindX, DeterministicLiquidWindZ);
        SetRequired("liquidWaveModeLimit", 5);
        SetRequired(
            "liquidWindMetresPerSecondPerEngineUnit",
            (float)LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit);
        SetRequired(
            "dynamicLiquidOriginCell",
            0.0f,
            0.0f,
            LiquidSurfaceSimulation.RecommendedCellSize);
        SetRequired(
            "dynamicLiquidGridSize",
            SyntheticScene.VoxelWidth * LiquidSurfaceSimulation.RecommendedCellsPerBlock,
            SyntheticScene.VoxelDepth * LiquidSurfaceSimulation.RecommendedCellsPerBlock);
        SetRequired("dynamicLiquidSurfaceEnabled", 1);
        for (int index = 0; index < SyntheticScene.MaximumLights; index++)
        {
            bool primary = index == 0;
            Vector3 lightPosition = primary ? SyntheticScene.LightPosition : Vector3.Zero;
            Vector3 lightColor = primary ? SyntheticScene.LightColor : Vector3.Zero;
            Set($"voxelLightPositionIntensity[{index}]", lightPosition.X, lightPosition.Y, lightPosition.Z, primary ? 1.25f : 0.0f);
            Set($"voxelLightColorRadius[{index}]", lightColor.X, lightColor.Y, lightColor.Z, primary ? 18.0f : 0.0f);
            Set($"voxelLightCasterLayer[{index}]", primary ? 0.0f : -1.0f);
        }
        Set("voxelLightCount", 1);
        Set("exposure", 0.0f);
        Set("contrast", 1.0f);
        Set("saturation", 1.0f);
        Set("vibrance", 0.0f);
        Set("vignette", 0.035f);
        Set("indirectLightStrength", 0.82f);
        Set("relightingStrength", 0.88f);
        Set("skyLightStrength", 0.78f);
        Set("emissiveLightStrength", 1.25f);
        Set("contactShadowStrength", 0.38f);
        Set("reflectionStrength", 0.72f);
        Set("reflectionDistance", 10.0f);
        Set("rainWetness", 0.0f);
        Set("voxelReflectionsEnabled", 1);
        Set("pointLightShadowStrength", 0.72f);
        Set("pointLightBounceStrength", 0.90f);
        Set("voxelBounceDistance", 6.0f);
        Set("pointLightRadius", 18.0f);
        Set("pointLightSourceRadius", 0.10f);
        Set("pointLightShadowSamples", 3);
        Set("denseDynamicLightCluster", 0);
        Set("voxelBounceRayCount", 1);
        Set("voxelBounceSteps", DiffuseTransportBudget.TraversalSteps(6f));
        Set("voxelBounceShadowSteps", DiffuseTransportBudget.TraversalSteps(6f));
        Set("skyRayCount", 1);
        Set("skyTraceSteps", 10);
        Set("albedoDetailSamples", 1);
        Set("temporalDenoiseSamples", 0);
        Set("transportInterlace", 0);
        Set("sunDirection", SyntheticScene.SunDirection);
        Set("sunColorStrength", 1.0f, 0.95f, 0.85f, 1.0f);
        Set("sunLightStrength", 1.10f);
        Set("sunShadowDistance", 64.0f);
        Set("sunFineShadowDistance", 8.0f);
        Set("occupancyScale", (float)SyntheticScene.OccupancyScale);
        Set("rayDistance", 2.4f);
        Set("rayCount", 2);
        Set("raySteps", 6);
        Set("screenSpaceLightingEnabled", 1);
        Set("screenSpaceReflectionsEnabled", 1);
        Set("reflectionSteps", 6);
        Set("voxelReflectionSteps", 18);
        Set("voxelLightingEnabled", 1);
        Set("temporalBlend", 0.0f);
    }

    /// <summary>
    /// Builds the exact RGBA32F runtime ABI from measured profile properties, calibrated wind,
    /// 12 mm/h rain, three finite-energy impacts, and the authored lava-bubble population.
    /// </summary>
    /// <returns>Row-major height, normal X, normal Z, and transient emission texels.</returns>
    private float[] BuildDynamicLiquidState()
    {
        int cellsPerBlock = LiquidSurfaceSimulation.RecommendedCellsPerBlock;
        int width = SyntheticScene.VoxelWidth * cellsPerBlock;
        int depth = SyntheticScene.VoxelDepth * cellsPerBlock;
        LiquidSurfaceSimulation simulation = new(
            0,
            0,
            width,
            depth,
            LiquidSurfaceSimulation.RecommendedCellSize,
            eventBudget: 256,
            bubbleBudget: 128,
            trackedEntityBudget: 32,
            deterministicSeed: 0x5EED1234u);
        int activeCellCount = 0;
        for (int worldZ = 0; worldZ < SyntheticScene.VoxelDepth; worldZ++)
        {
            for (int worldX = 0; worldX < SyntheticScene.VoxelWidth; worldX++)
            {
                int columnOffset = (worldZ * SyntheticScene.VoxelWidth + worldX)
                    * VoxelScene.FluidSurfaceChannels;
                byte encodedSurface = scene.FluidSurface[columnOffset];
                byte profileId = scene.FluidSurface[columnOffset + 1];
                if (encodedSurface == 0
                    || !LiquidSurfaceRuntime.TryDecodeProfile(
                        scene.LiquidOpticalProfiles,
                        profileId,
                        out LiquidSurfaceDynamics dynamics,
                        out LiquidSurfacePhysicalProperties physics))
                {
                    continue;
                }

                float surfaceWorldY = encodedSurface;
                bool rainExposed = profileId == SyntheticScene.WaterOpticalProfileId;
                for (int subZ = 0; subZ < cellsPerBlock; subZ++)
                {
                    for (int subX = 0; subX < cellsPerBlock; subX++)
                    {
                        simulation.SetSurfaceCell(
                            worldX * cellsPerBlock + subX,
                            worldZ * cellsPerBlock + subZ,
                            surfaceWorldY,
                            profileId,
                            in dynamics,
                            in physics,
                            rainExposed);
                        activeCellCount++;
                    }
                }
            }
        }

        RequireImpact(simulation, 16.25, 8.75, 0.18f, 3.2f, 0.8f, -0.4f);
        RequireImpact(simulation, 11.25, 8.25, 0.06f, 2.0f, -0.3f, 0.2f);
        RequireImpact(simulation, 5.25, 9.25, 0.12f, 2.6f, 0.2f, 0.5f);
        LiquidSurfaceForcing forcing = new(
            DeterministicLiquidWindX
                * (float)LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit,
            DeterministicLiquidWindZ
                * (float)LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit,
            0.012f / 3_600.0f);
        // 2.8 s crosses the authored 2.6 s lava rise time so the captured state contains
        // both a resolved bubble burst and its physically localized emission transient.
        for (int step = 0; step < 28; step++)
        {
            simulation.Advance(0.10f, in forcing);
        }
        const float resolvedBubbleRadiusMetres = 0.15f;
        const float basalticSurfaceTensionNewtonsPerMetre = 0.4f;
        float resolvedBubbleSurfaceEnergyJoules = basalticSurfaceTensionNewtonsPerMetre
            * 4.0f
            * MathF.PI
            * resolvedBubbleRadiusMetres
            * resolvedBubbleRadiusMetres;
        if (!simulation.QueueImpulse(
                5.50,
                9.50,
                resolvedBubbleSurfaceEnergyJoules,
                kind: LiquidSurfaceImpulseKind.BubbleBurst))
        {
            throw new InvalidOperationException("Resolved lava bubble burst missed the fixture surface.");
        }
        simulation.Advance(LiquidSurfaceSimulation.FixedStepSeconds, in forcing);

        float[] state = new float[width * depth * LiquidSurfaceSimulation.GpuChannels];
        simulation.WriteGpuTexture(state);
        int disturbedCellCount = 0;
        double maximumHeight = 0.0;
        double maximumNormalSlope = 0.0;
        double maximumEmission = 0.0;
        for (int offset = 0; offset < state.Length; offset += LiquidSurfaceSimulation.GpuChannels)
        {
            double height = Math.Abs(state[offset]);
            double normalSlope = Math.Sqrt(
                state[offset + 1] * state[offset + 1]
                + state[offset + 2] * state[offset + 2]);
            double emission = Math.Max(0.0, state[offset + 3]);
            maximumHeight = Math.Max(maximumHeight, height);
            maximumNormalSlope = Math.Max(maximumNormalSlope, normalSlope);
            maximumEmission = Math.Max(maximumEmission, emission);
            if (height > 1.0e-6 || normalSlope > 1.0e-6 || emission > 1.0e-6)
            {
                disturbedCellCount++;
            }
        }

        dynamicLiquidSurfaceReport = new DynamicLiquidSurfaceReport(
            activeCellCount,
            disturbedCellCount,
            maximumHeight,
            maximumNormalSlope,
            maximumEmission,
            simulation.ComputeTotalEnergy());
        return state;
    }

    /// <summary>Queues one physically dimensioned impact and fails the fixture if it misses liquid.</summary>
    /// <param name="simulation">Configured deterministic surface solver.</param>
    /// <param name="worldX">Impact X in metres/world blocks.</param>
    /// <param name="worldZ">Impact Z in metres/world blocks.</param>
    /// <param name="massKilograms">Impacting mass in kilograms.</param>
    /// <param name="speedMetresPerSecond">Impact speed in metres per second.</param>
    /// <param name="directionX">Horizontal X velocity in metres per second.</param>
    /// <param name="directionZ">Horizontal Z velocity in metres per second.</param>
    private static void RequireImpact(
        LiquidSurfaceSimulation simulation,
        double worldX,
        double worldZ,
        float massKilograms,
        float speedMetresPerSecond,
        float directionX,
        float directionZ)
    {
        if (!simulation.QueueImpact(
                worldX,
                worldZ,
                massKilograms,
                speedMetresPerSecond,
                directionX,
                directionZ))
        {
            throw new InvalidOperationException(
                $"Physical liquid impact at ({worldX:R}, {worldZ:R}) missed the fixture surface.");
        }
    }

    /// <summary>
    /// Executes the render Frame step used by the deterministic standalone Renderer fixture.
    /// </summary>
    /// <param name="view">The view input used to configure this deterministic test path.</param>
    /// <param name="frameIndex">Coordinate component in the space defined by the tested API.</param>
    /// <param name="outputColorDomain">
    /// Zero for the standalone filmic display preview; one for the production scene-transport domain.
    /// </param>
    internal void RenderFrame(
        VintageRtxDebugView view,
        int frameIndex,
        int outputColorDomain = 0)
    {
        RenderTransportFrame(view, frameIndex, outputColorDomain);
    }

    /// <summary>
    /// Executes the save Current Frame step used by the deterministic standalone Renderer fixture.
    /// </summary>
    /// <param name="path">Filesystem location constrained to the isolated test sandbox.</param>
    /// <returns>The save Current Frame result consumed by the caller&apos;s assertion.</returns>
    private byte[] SaveCurrentFrame(string path)
    {
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(options.Width * options.Height * 4));
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.ReadPixels(0, 0, options.Width, options.Height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        SavePixels(path, pixels);
        return pixels;
    }

    /// <summary>
    /// Executes the save Pixels step used by the deterministic standalone Renderer fixture.
    /// </summary>
    /// <param name="path">Filesystem location constrained to the isolated test sandbox.</param>
    /// <param name="bottomUpPixels">Caller-owned input consumed only for the duration of this test step.</param>
    private void SavePixels(string path, byte[] bottomUpPixels)
    {
        int stride = options.Width * 4;
        byte[] topDown = GC.AllocateUninitializedArray<byte>(bottomUpPixels.Length);
        for (int row = 0; row < options.Height; row++)
        {
            System.Buffer.BlockCopy(bottomUpPixels, row * stride, topDown, (options.Height - row - 1) * stride, stride);
        }
        using SKBitmap bitmap = new(options.Width, options.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        Marshal.Copy(topDown, 0, bitmap.GetPixels(), topDown.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        png.SaveTo(stream);
    }

    /// <summary>
    /// Creates texture2 D with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="unit">The unit input used to configure this deterministic test path.</param>
    /// <param name="internalFormat">The internal Format input used to configure this deterministic test path.</param>
    /// <param name="format">The format input used to configure this deterministic test path.</param>
    /// <param name="type">The type input used to configure this deterministic test path.</param>
    /// <param name="width">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="height">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="data">Caller-owned input consumed only for the duration of this test step.</param>
    /// <param name="minFilter">The min Filter input used to configure this deterministic test path.</param>
    /// <param name="magFilter">The mag Filter input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The create Texture2 D result consumed by the caller&apos;s assertion.</returns>
    private int CreateTexture2D<T>(int unit, PixelInternalFormat internalFormat, PixelFormat format,
        PixelType type, int width, int height, T[] data, TextureMinFilter minFilter, TextureMagFilter magFilter)
        where T : struct
    {
        int texture = GL.GenTexture();
        textures.Add(texture);
        BindTextureUnit(unit, TextureTarget.Texture2D, texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, width, height, 0, format, type, data);
        return texture;
    }

    /// <summary>
    /// Creates texture3 D with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="unit">The unit input used to configure this deterministic test path.</param>
    /// <param name="internalFormat">The internal Format input used to configure this deterministic test path.</param>
    /// <param name="format">The format input used to configure this deterministic test path.</param>
    /// <param name="type">The type input used to configure this deterministic test path.</param>
    /// <param name="width">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="height">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="depth">Fixture extent in cells or pixels, as defined by the tested API.</param>
    /// <param name="data">Caller-owned input consumed only for the duration of this test step.</param>
    /// <param name="minFilter">The min Filter input used to configure this deterministic test path.</param>
    /// <param name="magFilter">The mag Filter input used to configure this deterministic test path.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The create Texture3 D result consumed by the caller&apos;s assertion.</returns>
    private int CreateTexture3D<T>(int unit, PixelInternalFormat internalFormat, PixelFormat format,
        PixelType type, int width, int height, int depth, T[] data,
        TextureMinFilter minFilter, TextureMagFilter magFilter) where T : struct
    {
        int texture = GL.GenTexture();
        textures.Add(texture);
        BindTextureUnit(unit, TextureTarget.Texture3D, texture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)minFilter);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)magFilter);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(TextureTarget.Texture3D, 0, internalFormat, width, height, depth, 0, format, type, data);
        return texture;
    }

    /// <summary>
    /// Executes the bind Texture Unit step used by the deterministic standalone Renderer fixture.
    /// </summary>
    /// <param name="unit">The unit input used to configure this deterministic test path.</param>
    /// <param name="target">The target input used to configure this deterministic test path.</param>
    /// <param name="texture">The texture input used to configure this deterministic test path.</param>
    private static void BindTextureUnit(int unit, TextureTarget target, int texture)
    {
        GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
        GL.BindTexture(target, texture);
    }

    /// <summary>
    /// Executes the link Program step used by the deterministic standalone Renderer fixture.
    /// </summary>
    /// <param name="vertexSource">Caller-owned input consumed only for the duration of this test step.</param>
    /// <param name="fragmentSource">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The link Program result consumed by the caller&apos;s assertion.</returns>
    private static int LinkProgram(string vertexSource, string fragmentSource)
    {
        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        int linked = GL.CreateProgram();
        GL.AttachShader(linked, vertex);
        GL.AttachShader(linked, fragment);
        GL.LinkProgram(linked);
        GL.GetProgram(linked, GetProgramParameterName.LinkStatus, out int success);
        string log = GL.GetProgramInfoLog(linked);
        GL.DetachShader(linked, vertex);
        GL.DetachShader(linked, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        if (success == 0)
        {
            GL.DeleteProgram(linked);
            throw new InvalidOperationException($"Display shader link failed: {log}");
        }
        if (!string.IsNullOrWhiteSpace(log))
        {
            Console.WriteLine(log.Trim());
        }
        return linked;
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
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
        string log = GL.GetShaderInfoLog(shader);
        if (success == 0)
        {
            GL.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compilation failed: {log}");
        }
        return shader;
    }

    /// <summary>
    /// Sets requested fixture operation on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private void Set(string name, int value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0) GL.Uniform1(location, value);
    }

    /// <summary>
    /// Sets requested fixture operation on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private void Set(string name, float value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0) GL.Uniform1(location, value);
    }

    /// <summary>
    /// Sets requested fixture operation on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    private void Set(string name, float x, float y)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0) GL.Uniform2(location, x, y);
    }

    /// <summary>
    /// Binds and reads back a scalar required by the liquid shader ABI, catching renames and non-deterministic fixture state.
    /// </summary>
    /// <param name="name">Exact GLSL uniform identifier.</param>
    /// <param name="value">Deterministic value expressed in the uniform's documented unit.</param>
    private void SetRequired(string name, float value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location < 0)
        {
            throw new InvalidOperationException($"Required display-shader uniform '{name}' is not active.");
        }

        GL.Uniform1(location, value);
        GL.GetUniform(program, location, out float actual);
        if (actual != value)
        {
            throw new InvalidOperationException(
                $"Display-shader uniform '{name}' read back {actual:R}, expected {value:R}.");
        }
    }

    /// <summary>Binds and reads back a required integer uniform used as a deterministic feature gate.</summary>
    /// <param name="name">Exact GLSL uniform identifier.</param>
    /// <param name="value">Integer value expected after the driver round trip.</param>
    private void SetRequired(string name, int value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location < 0)
        {
            throw new InvalidOperationException($"Required display-shader uniform '{name}' is not active.");
        }

        GL.Uniform1(location, value);
        GL.GetUniform(program, location, out int actual);
        if (actual != value)
        {
            throw new InvalidOperationException(
                $"Display-shader uniform '{name}' read back {actual}, expected {value}.");
        }
    }

    /// <summary>
    /// Binds and reads back a required horizontal vector in world X/Z order so captures cannot inherit driver state.
    /// </summary>
    /// <param name="name">Exact GLSL uniform identifier.</param>
    /// <param name="x">World-space X component.</param>
    /// <param name="z">World-space Z component.</param>
    private void SetRequired(string name, float x, float z)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location < 0)
        {
            throw new InvalidOperationException($"Required display-shader uniform '{name}' is not active.");
        }

        GL.Uniform2(location, x, z);
        float[] actual = new float[2];
        GL.GetUniform(program, location, actual);
        if (actual[0] != x || actual[1] != z)
        {
            throw new InvalidOperationException(
                $"Display-shader uniform '{name}' read back ({actual[0]:R}, {actual[1]:R}), expected ({x:R}, {z:R}).");
        }
    }

    /// <summary>Binds and reads back a required three-component renderer contract vector.</summary>
    /// <param name="name">Exact GLSL uniform identifier.</param>
    /// <param name="x">First component.</param>
    /// <param name="y">Second component.</param>
    /// <param name="z">Third component.</param>
    private void SetRequired(string name, float x, float y, float z)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location < 0)
        {
            throw new InvalidOperationException($"Required display-shader uniform '{name}' is not active.");
        }

        GL.Uniform3(location, x, y, z);
        float[] actual = new float[3];
        GL.GetUniform(program, location, actual);
        if (actual[0] != x || actual[1] != y || actual[2] != z)
        {
            throw new InvalidOperationException(
                $"Display-shader uniform '{name}' read back ({actual[0]:R}, {actual[1]:R}, {actual[2]:R}), expected ({x:R}, {y:R}, {z:R}).");
        }
    }

    /// <summary>
    /// Sets requested fixture operation on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="w">The w input used to configure this deterministic test path.</param>
    private void Set(string name, float x, float y, float z, float w)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0) GL.Uniform4(location, x, y, z, w);
    }

    /// <summary>
    /// Sets requested fixture operation on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private void Set(string name, Vector3 value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0) GL.Uniform3(location, value.X, value.Y, value.Z);
    }

    /// <summary>
    /// Sets matrix on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="name">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private void SetMatrix(string name, float[] value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0) GL.UniformMatrix4(location, 1, false, value);
    }

    /// <summary>
    /// Executes the check Gl step used by the deterministic standalone Renderer fixture.
    /// </summary>
    /// <param name="operation">The operation input used to configure this deterministic test path.</param>
    private static void CheckGl(string operation)
    {
        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            throw new InvalidOperationException($"OpenGL error after {operation}: {error}.");
        }
    }

    /// <summary>
    /// Releases resources owned by standalone Renderer; cleanup remains safe after partial fixture initialization.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        foreach (int target in transportFramebuffers)
            if (target != 0) GL.DeleteFramebuffer(target);
        if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer);
        if (vertexArray != 0) GL.DeleteVertexArray(vertexArray);
        if (program != 0) GL.DeleteProgram(program);
        foreach (int texture in textures)
        {
            GL.DeleteTexture(texture);
        }
    }
}
