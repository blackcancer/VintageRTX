using OpenTK.Graphics.OpenGL4;
using VintageRTX.Core.Scene;

namespace VintageRTX.Client;

/// <summary>Owned scene textures; a matching immutable frame is published only after all writes succeed.</summary>
internal sealed class SceneTextureSet : IDisposable
{
    public int RegionTexture { get; private set; }
    public int CellTexture { get; private set; }
    public int GeometryTexture { get; private set; }
    public int MaterialTexture { get; private set; }
    public bool Ready { get; private set; }
    public CellSceneFrame? PublishedFrame { get; private set; }
    public long LastUploadBytes { get; private set; }
    public long TotalUploadBytes { get; private set; }
    private readonly IGraphicsStatus status;
    private int geometryHeight, uploadedGeometryTexels;
    private readonly int[] regionScratch = new int[2048], tagScratch = new int[4];
    private readonly float[] materialScratch = new float[4096];
    private readonly int owner = Environment.CurrentManagedThreadId;
    private bool disposed;
    public SceneTextureSet(IGraphicsStatus? status = null) => this.status = status ?? NativeGraphicsStatus.Instance;
    internal void NoUpload() => LastUploadBytes = 0;
    public void Upload(GpuSceneData data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("GPU scene uploads require their owning render thread.");
        Ready = false; PublishedFrame = null; LastUploadBytes = 0;
        ArgumentNullException.ThrowIfNull(data);
        CellSceneFrame frame = data.SourceFrame ?? throw new ArgumentException("Missing geometry source frame.");
        ErrorCode before = status.Error();
        if (before != ErrorCode.NoError) throw new InvalidOperationException($"Pre-existing OpenGL error {before}; geometry not published.");
        if (data.GeometryHeight > status.Limit(GetPName.MaxTextureSize)) throw new NotSupportedException("Scene texture capacity exceeded.");
        using var unpack = new RewriteUnpackState();
        bool first = RegionTexture == 0;
        if (first)
        {
            RegionTexture = Allocate(PixelInternalFormat.Rgba32i, 27, 1, PixelFormat.RgbaInteger, PixelType.Int);
            CellTexture = Allocate(PixelInternalFormat.Rgba32i, 64, 216, PixelFormat.RgbaInteger, PixelType.Int);
            GeometryTexture = GL.GenTexture();
            MaterialTexture = Allocate(PixelInternalFormat.Rgba32f, 128, 216, PixelFormat.Rgba, PixelType.Float);
        }
        if (first || data.GeometryChanged)
        {
            GL.BindTexture(TextureTarget.Texture2D, GeometryTexture); Parameters();
            if (geometryHeight != data.GeometryHeight || first)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 256, data.GeometryHeight, 0, PixelFormat.Rgba, PixelType.Float, data.GeometryData.ToArray());
                geometryHeight = data.GeometryHeight; LastUploadBytes += data.GeometryData.Length * 4L;
            }
            else
            {
                int firstRow = data.ResetOccurred ? 0 : uploadedGeometryTexels / 256;
                int lastRow = Math.Max(firstRow + 1, (data.GeometryUsedTexels + 255) / 256);
                int count = (lastRow - firstRow) * 256 * 4;
                float[] append = data.GeometryData.Slice(firstRow * 256 * 4, count).ToArray();
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, firstRow, 256, lastRow - firstRow, PixelFormat.Rgba, PixelType.Float, append);
                LastUploadBytes += count * 4L;
            }
            uploadedGeometryTexels = data.GeometryUsedTexels;
        }
        for (int slot = 0; slot < 27; slot++)
        {
            if (!first && !data.ChangedRegions.Contains(slot)) continue;
            Array.Clear(tagScratch);
            GL.BindTexture(TextureTarget.Texture2D, RegionTexture);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, slot, 0, 1, 1, PixelFormat.RgbaInteger, PixelType.Int, tagScratch);
            LastUploadBytes += 16;
            data.CellData.Slice(slot * 2048, 2048).CopyTo(regionScratch);
            GL.BindTexture(TextureTarget.Texture2D, CellTexture);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, slot * 8, 64, 8, PixelFormat.RgbaInteger, PixelType.Int, regionScratch);
            LastUploadBytes += 8192;
            // Publish each region's material with the SAME cell revision, before the ready tag.
            if(first || data.ResetOccurred || data.MaterialChangedRegions.Contains(slot))
            {
                data.MaterialData.Slice(slot * 4096, 4096).CopyTo(materialScratch);
                GL.BindTexture(TextureTarget.Texture2D, MaterialTexture);
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, slot * 8, 128, 8, PixelFormat.Rgba, PixelType.Float, materialScratch);
                LastUploadBytes += 16384;
            }
            data.RegionData.Slice(slot * 4, 4).CopyTo(tagScratch);
            GL.BindTexture(TextureTarget.Texture2D, RegionTexture);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, slot, 0, 1, 1, PixelFormat.RgbaInteger, PixelType.Int, tagScratch);
            LastUploadBytes += 16;
        }
        ErrorCode error = status.Error();
        if (error != ErrorCode.NoError) throw new InvalidOperationException($"OpenGL scene upload reported {error}; cache not published.");
        PublishedFrame = frame; Ready = true; TotalUploadBytes += LastUploadBytes;
    }
    private static int Allocate(PixelInternalFormat format, int width, int height, PixelFormat layout, PixelType type)
    {
        int texture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, texture); Parameters();
        GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, layout, type, IntPtr.Zero); return texture;
    }
    private static void Parameters()
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }
    public void Dispose()
    {
        if (disposed) return;
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("GPU disposal requires its render thread.");
        disposed = true; Ready = false; PublishedFrame = null;
        if (RegionTexture != 0) GL.DeleteTexture(RegionTexture); if (CellTexture != 0) GL.DeleteTexture(CellTexture); if (GeometryTexture != 0) GL.DeleteTexture(GeometryTexture);
        if (MaterialTexture != 0) GL.DeleteTexture(MaterialTexture);
        RegionTexture = CellTexture = GeometryTexture = MaterialTexture = 0;
    }
}
