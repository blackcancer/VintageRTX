using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Rendering;

/// <summary>
/// Stable shader-side names for the dynamic RGBA32F liquid height field. The
/// renderer can bind <see cref="LiquidSurfaceGpuBinding.TextureId"/> to any free
/// sampler unit and publish the remaining values without knowing uploader internals.
/// </summary>
internal static class LiquidSurfaceGpuContract
{
    /// <summary>Sampler2D containing height, normal X, normal Z, and transient emission.</summary>
    public const string SamplerUniformName = "dynamicLiquidSurface";
    /// <summary>Vec3 containing world origin X/Z and cell edge length in blocks.</summary>
    public const string OriginCellUniformName = "dynamicLiquidOriginCell";
    /// <summary>IVec2 containing texture width and depth in cells.</summary>
    public const string GridSizeUniformName = "dynamicLiquidGridSize";
}

/// <summary>
/// Immutable result of one successful upload. Origin and cell size map world
/// X/Z to texture coordinates while revision distinguishes successive frames.
/// </summary>
/// <param name="TextureId">Owned OpenGL 2D texture handle.</param>
/// <param name="OriginWorldX">Grid minimum world X in blocks.</param>
/// <param name="OriginWorldZ">Grid minimum world Z in blocks.</param>
/// <param name="Width">Texture width in cells.</param>
/// <param name="Depth">Texture height in cells along world Z.</param>
/// <param name="CellSize">Cell edge length in world blocks.</param>
/// <param name="Revision">Monotonic successful-upload revision.</param>
internal readonly record struct LiquidSurfaceGpuBinding(
    int TextureId,
    int OriginWorldX,
    int OriginWorldZ,
    int Width,
    int Depth,
    float CellSize,
    long Revision);

/// <summary>
/// Minimal texture operations required by the uploader. The abstraction keeps
/// packing and ownership unit-testable without an OpenGL context.
/// </summary>
internal interface ILiquidSurfaceTextureApi
{
    /// <summary>Creates one texture name owned by the caller.</summary>
    /// <returns>Positive texture handle.</returns>
    int CreateTexture();

    /// <summary>Allocates/replaces RGBA32F storage and uploads every texel.</summary>
    /// <param name="textureId">Positive owned texture handle.</param>
    /// <param name="width">Positive X cell count.</param>
    /// <param name="depth">Positive Z cell count.</param>
    /// <param name="pixels">Row-major RGBA float payload.</param>
    void AllocateRgba32Float(int textureId, int width, int depth, float[] pixels);

    /// <summary>Updates existing same-size RGBA32F storage without reallocating it.</summary>
    /// <param name="textureId">Positive owned texture handle.</param>
    /// <param name="width">Current X cell count.</param>
    /// <param name="depth">Current Z cell count.</param>
    /// <param name="pixels">Row-major RGBA float payload.</param>
    void UpdateRgba32Float(int textureId, int width, int depth, float[] pixels);

    /// <summary>Deletes one previously created texture handle.</summary>
    /// <param name="textureId">Positive owned texture handle.</param>
    void DeleteTexture(int textureId);
}

/// <summary>
/// OpenGL 4 implementation preserving the caller's 2D binding and unpack
/// alignment. Allocation uses linear-filtered clamp-to-edge RGBA32F storage;
/// subsequent same-size frames use <c>TexSubImage2D</c>.
/// </summary>
internal sealed class OpenGlLiquidSurfaceTextureApi : ILiquidSurfaceTextureApi
{
    /// <inheritdoc />
    public int CreateTexture()
    {
        return GL.GenTexture();
    }

    /// <inheritdoc />
    public void AllocateRgba32Float(int textureId, int width, int depth, float[] pixels)
    {
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTexture);
        GL.GetInteger(GetPName.UnpackAlignment, out int previousUnpackAlignment);
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, textureId);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba32f,
                width,
                depth,
                0,
                PixelFormat.Rgba,
                PixelType.Float,
                pixels);
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousUnpackAlignment);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        }
    }

    /// <inheritdoc />
    public void UpdateRgba32Float(int textureId, int width, int depth, float[] pixels)
    {
        GL.GetInteger(GetPName.TextureBinding2D, out int previousTexture);
        GL.GetInteger(GetPName.UnpackAlignment, out int previousUnpackAlignment);
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, textureId);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                0,
                0,
                width,
                depth,
                PixelFormat.Rgba,
                PixelType.Float,
                pixels);
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousUnpackAlignment);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        }
    }

    /// <inheritdoc />
    public void DeleteTexture(int textureId)
    {
        GL.DeleteTexture(textureId);
    }
}

/// <summary>
/// Reuses one CPU staging array and one GPU texture for dynamic liquid-surface
/// frames. Packing comes directly from <see cref="LiquidSurfaceSimulation"/>;
/// ownership remains local and every failed allocation is rolled back.
/// </summary>
internal sealed class LiquidSurfaceGpuUploader : IDisposable
{
    private readonly ILiquidSurfaceTextureApi textureApi;
    private float[] stagingPixels = [];
    private int textureId;
    private int allocatedWidth;
    private int allocatedDepth;
    private long revision;
    private bool disposed;

    /// <summary>Creates an uploader backed by the production OpenGL implementation.</summary>
    public LiquidSurfaceGpuUploader()
        : this(new OpenGlLiquidSurfaceTextureApi())
    {
    }

    /// <summary>Creates an uploader with injected texture operations for deterministic tests.</summary>
    /// <param name="textureApi">Texture allocation, update, and deletion operations.</param>
    internal LiquidSurfaceGpuUploader(ILiquidSurfaceTextureApi textureApi)
    {
        this.textureApi = textureApi ?? throw new ArgumentNullException(nameof(textureApi));
    }

    /// <summary>Gets metadata from the last successful upload, or default before the first upload.</summary>
    public LiquidSurfaceGpuBinding Current { get; private set; }

    /// <summary>Gets CPU timestamp ticks spent packing the most recent simulation payload.</summary>
    internal long LastPackingElapsedTicks { get; private set; }

    /// <summary>Gets CPU timestamp ticks spent in texture creation/allocation/update for the latest upload.</summary>
    internal long LastDriverUploadElapsedTicks { get; private set; }

    /// <summary>
    /// Packs and uploads the current simulation state. Same-size frames update
    /// existing storage; a grid-size change reallocates the same texture name.
    /// </summary>
    /// <param name="simulation">Dynamic liquid grid whose state is detached during this call.</param>
    /// <returns>Texture handle plus world-to-grid metadata for shader uniforms.</returns>
    public LiquidSurfaceGpuBinding Upload(LiquidSurfaceSimulation simulation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(simulation);

        long packingStarted = Stopwatch.GetTimestamp();
        try
        {
            int requiredFloatCount = RequiredFloatCount(simulation.Width, simulation.Depth);
            if (stagingPixels.Length != requiredFloatCount)
            {
                stagingPixels = new float[requiredFloatCount];
            }
            simulation.WriteGpuTexture(stagingPixels);
        }
        finally
        {
            LastPackingElapsedTicks = Stopwatch.GetTimestamp() - packingStarted;
        }

        long uploadStarted = Stopwatch.GetTimestamp();
        LastDriverUploadElapsedTicks = 0;
        try
        {
            bool created = textureId == 0;
            int candidateTexture = created ? textureApi.CreateTexture() : textureId;
            if (candidateTexture <= 0)
            {
                throw new InvalidOperationException(
                    "Dynamic liquid texture creation returned an invalid handle.");
            }

            bool requiresAllocation = created
                || allocatedWidth != simulation.Width
                || allocatedDepth != simulation.Depth;
            try
            {
                if (requiresAllocation)
                {
                    textureApi.AllocateRgba32Float(
                        candidateTexture,
                        simulation.Width,
                        simulation.Depth,
                        stagingPixels);
                }
                else
                {
                    textureApi.UpdateRgba32Float(
                        candidateTexture,
                        simulation.Width,
                        simulation.Depth,
                        stagingPixels);
                }
            }
            catch
            {
                if (requiresAllocation)
                {
                    try
                    {
                        textureApi.DeleteTexture(candidateTexture);
                    }
                    catch
                    {
                        // Preserve the upload exception; the handle is invalidated
                        // locally even when driver cleanup also reports a failure.
                    }

                    textureId = 0;
                    allocatedWidth = 0;
                    allocatedDepth = 0;
                    Current = default;
                }
                throw;
            }

            textureId = candidateTexture;
            allocatedWidth = simulation.Width;
            allocatedDepth = simulation.Depth;
            revision = checked(revision + 1);
            Current = new LiquidSurfaceGpuBinding(
                textureId,
                simulation.OriginWorldX,
                simulation.OriginWorldZ,
                simulation.Width,
                simulation.Depth,
                simulation.CellSize,
                revision);
            return Current;
        }
        finally
        {
            LastDriverUploadElapsedTicks = Stopwatch.GetTimestamp() - uploadStarted;
        }
    }

    /// <summary>Computes checked RGBA float cardinality for one grid.</summary>
    /// <param name="width">Positive cell count along X.</param>
    /// <param name="depth">Positive cell count along Z.</param>
    /// <returns>Width times depth times four.</returns>
    internal static int RequiredFloatCount(int width, int depth)
    {
        if (width <= 0 || depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        return checked(width * depth * LiquidSurfaceSimulation.GpuChannels);
    }

    /// <summary>Deletes the owned texture exactly once and invalidates published metadata.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        int ownedTexture = textureId;
        textureId = 0;
        allocatedWidth = 0;
        allocatedDepth = 0;
        Current = default;
        if (ownedTexture != 0)
        {
            textureApi.DeleteTexture(ownedTexture);
        }
    }
}
