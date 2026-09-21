using OpenTK.Graphics.OpenGL4;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Client;

/// <summary>Owned light texture. Delta uploads require the exact preceding pixel revision.</summary>
internal sealed class LightTexture : IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private GpuLightData? uploadedData;
    private long uploadedRevision = -1;
    private int allocatedHeight;
    private float[] upload = [];
    private bool disposed;
    public int Texture { get; private set; }
    public bool Ready { get; private set; }
    public LightFrame? PublishedFrame { get; private set; }
    public CellId Anchor { get; private set; }
    public long LastUploadBytes { get; private set; }
    public long TotalUploadBytes { get; private set; }
    public void Invalidate() { Ready = false; PublishedFrame = null; LastUploadBytes = 0; }

    public void Upload(GpuLightData data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Light upload requires its owning render thread.");
        ArgumentNullException.ThrowIfNull(data);
        LightFrame frame = data.SourceFrame ?? throw new ArgumentException("Missing evaluated light frame.", nameof(data));
        LastUploadBytes = 0;
        if (Ready && ReferenceEquals(data, uploadedData) && data.PixelRevision == uploadedRevision)
        { PublishedFrame = frame; Anchor = data.Anchor; return; }
        // A consumer can miss one or several frames. Apply a delta only from its exact predecessor;
        // otherwise upload the complete packet. Never assume every render pass ran last frame.
        bool delta = Ready && ReferenceEquals(data, uploadedData) && allocatedHeight == data.Height
            && data.PixelRevision == uploadedRevision + 1;
        int firstRow = delta ? data.ChangedRowStart : 0;
        int rows = delta ? data.ChangedRowCount : data.Height;
        Invalidate();
        if (data.Height > GL.GetInteger(GetPName.MaxTextureSize))
            throw new InvalidOperationException("Light packet exceeds GPU texture capacity; no sources were silently dropped.");
        GL.GetInteger(GetPName.TextureBinding2D, out int texture);
        GL.GetInteger(GetPName.PixelUnpackBufferBinding, out int pbo);
        GL.GetInteger(GetPName.UnpackAlignment, out int alignment);
        GL.GetInteger(GetPName.UnpackRowLength, out int rowLength);
        GL.GetInteger(GetPName.UnpackSkipRows, out int skipRows);
        GL.GetInteger(GetPName.UnpackSkipPixels, out int skipPixels);
        GL.GetInteger(GetPName.UnpackSwapBytes, out int swapBytes);
        try
        {
            ErrorCode before = GL.GetError();
            if (before != ErrorCode.NoError)
                throw new InvalidOperationException($"Pre-existing OpenGL error {before}; light frame not published.");
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1); GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0); GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
            GL.PixelStore(PixelStoreParameter.UnpackSwapBytes, 0);
            if (Texture == 0) Texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, Texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            int values = rows * GpuLightData.Width * 4;
            if (upload.Length < values) upload = new float[Math.Max(values, upload.Length * 2)];
            data.Pixels.Slice(firstRow * GpuLightData.Width * 4, values).CopyTo(upload);
            if (allocatedHeight != data.Height)
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, GpuLightData.Width,
                    data.Height, 0, PixelFormat.Rgba, PixelType.Float, upload);
            else
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, firstRow, GpuLightData.Width,
                    rows, PixelFormat.Rgba, PixelType.Float, upload);
            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError) throw new InvalidOperationException($"OpenGL light upload failed: {error}.");
            allocatedHeight = data.Height; uploadedData = data; uploadedRevision = data.PixelRevision;
            LastUploadBytes = (long)values * sizeof(float); TotalUploadBytes += LastUploadBytes;
            PublishedFrame = frame; Anchor = data.Anchor; Ready = true;
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, alignment); GL.PixelStore(PixelStoreParameter.UnpackRowLength, rowLength);
            GL.PixelStore(PixelStoreParameter.UnpackSkipRows, skipRows); GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, skipPixels);
            GL.PixelStore(PixelStoreParameter.UnpackSwapBytes, swapBytes);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pbo); GL.BindTexture(TextureTarget.Texture2D, texture);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Light disposal requires its owning render thread.");
        disposed = true; Invalidate();
        if (Texture != 0) GL.DeleteTexture(Texture);
        Texture = 0; uploadedData = null; upload = [];
    }
}
