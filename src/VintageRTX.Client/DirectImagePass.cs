using OpenTK.Graphics.OpenGL4;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Transport;

namespace VintageRTX.Client;

/// <summary>Owned direct HDR image targets with explicit geometry, light and driver-state validation.</summary>
internal sealed class DirectImagePass : IDisposable
{
    private const string Vertex = "#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}";
    private static readonly string[] Samplers = ["regionData", "cellData", "geometryData", "lightData",
        "receiverPosition", "receiverNormal", "receiverGeometricNormal", "receiverColor", "materialData"];
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly IGraphicsStatus status;
    private readonly int[] surfaceTextures = new int[5], inputs = new int[9];
    private readonly int[] samplerLocations = new int[9];
    private readonly int program, vao;
    private readonly int anchorUniform, cameraUniform, countUniform, samplesUniform, minimumUniform, cellsUniform, rotationUniform, exposureUniform;
    private int framebuffer, hdr, diagnostic, preview, targetWidth, targetHeight, surfaceWidth, surfaceHeight, materialHeight;
    private DirectSurfaceFrame? uploadedSurfaces;
    private WorldId? previousWorld;
    private long previousFrame = -1;
    private bool disposed;
    public bool Ready { get; private set; }
    public DirectSurfaceFrame? PublishedSurfaces { get; private set; }
    public LightFrame? PublishedLights { get; private set; }
    public int RadianceTexture => Ready ? hdr : 0;
    public int DiagnosticTexture => Ready ? diagnostic : 0;
    public int PreviewTexture => Ready ? preview : 0;
    public int Width => targetWidth;
    public int Height => targetHeight;
    public long LastSurfaceUploadBytes { get; private set; }

    public DirectImagePass(string sceneSource, string lightSource, string materialSource, string imageSource, IGraphicsStatus? status = null)
    {
        this.status = status ?? NativeGraphicsStatus.Instance;
        if (this.status.Limit(GetPName.MaxTextureImageUnits) < Samplers.Length || this.status.Limit(GetPName.MaxDrawBuffers) < 3)
            throw new NotSupportedException("The direct pass requires nine fragment samplers and three color outputs.");
        program = MakeProgram(sceneSource, lightSource, materialSource, imageSource);
        vao = GL.GenVertexArray();
        for (int i = 0; i < samplerLocations.Length; i++) samplerLocations[i] = Uniform(Samplers[i]);
        anchorUniform = Uniform("sceneAnchor"); cameraUniform = Uniform("cameraRelative"); countUniform = Uniform("lightCount");
        samplesUniform = Uniform("finiteSourceSamples"); minimumUniform = Uniform("directRayMinimum"); cellsUniform = Uniform("directMaximumCells");
        rotationUniform = Uniform("directSampleRotation"); exposureUniform = Uniform("previewExposure");
    }

    public void Render(DirectSurfaceFrame surfaces, SceneTextureSet geometry, LightTexture lights,
        int finiteSamples = 8, float rayMinimum = .0001f, int maximumCells = 256, float previewExposure = 0)
    {
        CheckOwner(); Ready = false; PublishedSurfaces = null; PublishedLights = null; LastSurfaceUploadBytes = 0;
        ArgumentNullException.ThrowIfNull(surfaces); ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(lights);
        LightFrame frame = lights.PublishedFrame ?? throw new InvalidOperationException("No current GPU light frame.");
        if (!geometry.Ready || !ReferenceEquals(geometry.PublishedFrame, surfaces.Scene))
            throw new InvalidOperationException("Receiver surfaces and GPU geometry do not share one snapshot.");
        if (!lights.Ready || frame.World != surfaces.World || lights.Anchor != surfaces.Anchor)
            throw new InvalidOperationException("Receiver and light world/anchor mismatch.");
        if (previousWorld == frame.World && frame.Frame < previousFrame)
            throw new InvalidOperationException("An old light frame cannot replace a newer rendered frame.");
        if (finiteSamples is < 1 or > 64 || !float.IsFinite(rayMinimum) || rayMinimum < 0
            || maximumCells is < 1 or > 256 || !float.IsFinite(previewExposure) || Math.Abs(previewExposure) > 16)
            throw new ArgumentOutOfRangeException(nameof(finiteSamples));
        int maximumSize = status.Limit(GetPName.MaxTextureSize);
        if (surfaces.Width > maximumSize || surfaces.Height > maximumSize || surfaces.MaterialCount > maximumSize)
            throw new NotSupportedException("Surface packet exceeds the GPU texture extent.");
        CheckError("before direct pass");
        using var state = new RewriteDrawState(Samplers.Length, 3);
        state.Configure();
        UploadSurfaces(surfaces); AllocateTargets(surfaces.Width, surfaces.Height);
        inputs[0] = geometry.RegionTexture; inputs[1] = geometry.CellTexture; inputs[2] = geometry.GeometryTexture; inputs[3] = lights.Texture;
        for (int i = 0; i < surfaceTextures.Length; i++) inputs[i + 4] = surfaceTextures[i];
        GL.UseProgram(program);
        for (int i = 0; i < inputs.Length; i++)
        {
            if (inputs[i] == 0 || inputs[i] == hdr || inputs[i] == diagnostic || inputs[i] == preview)
                throw new InvalidOperationException("Missing input or framebuffer feedback in direct pass.");
            GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + i));
            GL.BindTexture(TextureTarget.Texture2D, inputs[i]); GL.Uniform1(samplerLocations[i], i);
        }
        var camera = DirectSurfaceFrame.Relative(surfaces.Camera, surfaces.Anchor);
        GL.Uniform3(anchorUniform, surfaces.Anchor.X, surfaces.Anchor.Y, surfaces.Anchor.Z);
        GL.Uniform3(cameraUniform, camera.X, camera.Y, camera.Z); GL.Uniform1(countUniform, frame.Samples.Length);
        GL.Uniform1(samplesUniform, finiteSamples); GL.Uniform1(minimumUniform, rayMinimum); GL.Uniform1(cellsUniform, maximumCells);
        GL.Uniform2(rotationUniform, 0f, 0f); GL.Uniform1(exposureUniform, previewExposure);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer); GL.Viewport(0, 0, surfaces.Width, surfaces.Height);
        GL.BindVertexArray(vao); GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        CheckError("drawing direct image");
        previousWorld = frame.World; previousFrame = frame.Frame;
        PublishedSurfaces = surfaces; PublishedLights = frame; Ready = true;
    }

    private void UploadSurfaces(DirectSurfaceFrame surfaces)
    {
        if (ReferenceEquals(uploadedSurfaces, surfaces)) return;
        uploadedSurfaces = null;
        using var unpack = new RewriteUnpackState();
        for (int i = 0; i < surfaceTextures.Length; i++) if (surfaceTextures[i] == 0) surfaceTextures[i] = GL.GenTexture();
        bool sameExtent = surfaces.Width == surfaceWidth && surfaces.Height == surfaceHeight;
        Write(surfaceTextures[0], surfaces.Width, surfaces.Height, surfaces.Positions, sameExtent);
        Write(surfaceTextures[1], surfaces.Width, surfaces.Height, surfaces.Normals, sameExtent);
        Write(surfaceTextures[2], surfaces.Width, surfaces.Height, surfaces.GeometricNormals, sameExtent);
        Write(surfaceTextures[3], surfaces.Width, surfaces.Height, surfaces.Colors, sameExtent);
        Write(surfaceTextures[4], DirectSurfaceFrame.MaterialWidth, surfaces.MaterialCount, surfaces.Materials, materialHeight == surfaces.MaterialCount);
        CheckError("uploading receiver data");
        surfaceWidth = surfaces.Width; surfaceHeight = surfaces.Height; materialHeight = surfaces.MaterialCount;
        uploadedSurfaces = surfaces;
        LastSurfaceUploadBytes = ((long)surfaces.Width * surfaces.Height * 4 * 4 + surfaces.Materials.Length) * sizeof(float);
    }
    private static void Write(int texture, int width, int height, ReadOnlySpan<float> data, bool allocated)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture); Parameters();
        if (allocated) GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.Float, data.ToArray());
        else GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, width, height, 0, PixelFormat.Rgba, PixelType.Float, data.ToArray());
    }
    private void AllocateTargets(int width, int height)
    {
        if (framebuffer != 0 && targetWidth == width && targetHeight == height) return;
        using var unpack = new RewriteUnpackState();
        if (framebuffer == 0) framebuffer = GL.GenFramebuffer();
        if (hdr == 0) hdr = GL.GenTexture(); if (diagnostic == 0) diagnostic = GL.GenTexture(); if (preview == 0) preview = GL.GenTexture();
        Allocate(hdr, PixelInternalFormat.Rgba32f); Allocate(diagnostic, PixelInternalFormat.Rgba32f); Allocate(preview, PixelInternalFormat.Rgba8);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
        Attach(FramebufferAttachment.ColorAttachment0, hdr); Attach(FramebufferAttachment.ColorAttachment1, diagnostic); Attach(FramebufferAttachment.ColorAttachment2, preview);
        GL.DrawBuffers(3, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2 });
        if (status.Framebuffer(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("Direct image framebuffer is incomplete.");
        CheckError("allocating direct targets"); targetWidth = width; targetHeight = height;
        void Allocate(int texture, PixelInternalFormat format)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture); Parameters();
            GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        }
        static void Attach(FramebufferAttachment slot, int texture) =>
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, slot, TextureTarget.Texture2D, texture, 0);
    }
    private static void Parameters()
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
    }
    private int Uniform(string name) => GL.GetUniformLocation(program, name);
    private void CheckOwner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Direct image operations require their owning render thread.");
    }
    private void CheckError(string operation)
    {
        ErrorCode error = status.Error();
        if (error != ErrorCode.NoError) throw new InvalidOperationException($"OpenGL error {error} {operation}; image not published.");
    }
    private static int MakeProgram(params string[] parts)
    {
        // Compile owns failure cleanup. After it succeeds, this scope unconditionally owns vertex.
        int vertex = Compile(ShaderType.VertexShader, Vertex);
        int fragment = 0, result = 0;
        try
        {
            fragment = Compile(ShaderType.FragmentShader, "#version 330 core\n" + string.Join("\n", parts));
            result = GL.CreateProgram(); GL.AttachShader(result, vertex); GL.AttachShader(result, fragment); GL.LinkProgram(result);
            GL.GetProgram(result, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(result));
            return result;
        }
        catch { if (result != 0) GL.DeleteProgram(result); throw; }
        finally { GL.DeleteShader(vertex); if (fragment != 0) GL.DeleteShader(fragment); }
    }
    private static int Compile(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok != 0) return shader;
        string error = GL.GetShaderInfoLog(shader); GL.DeleteShader(shader); throw new InvalidOperationException(error);
    }
    public void Dispose()
    {
        if (disposed) return; CheckOwner(); disposed = true; Ready = false; PublishedSurfaces = null; PublishedLights = null;
        GL.DeleteProgram(program); GL.DeleteVertexArray(vao);
        if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer);
        if (hdr != 0) GL.DeleteTexture(hdr); if (diagnostic != 0) GL.DeleteTexture(diagnostic); if (preview != 0) GL.DeleteTexture(preview);
        foreach (int texture in surfaceTextures) if (texture != 0) GL.DeleteTexture(texture);
    }
}
