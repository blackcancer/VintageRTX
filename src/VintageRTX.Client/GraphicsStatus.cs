using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Client;

/// <summary>
/// Read-only driver diagnostics. Resource creation, uploads and drawing always use OpenGL directly.
/// A test provider can report loss/capacity failures without exhausting a machine's GPU memory.
/// </summary>
internal interface IGraphicsStatus
{
    int Limit(GetPName name);
    ErrorCode Error();
    FramebufferErrorCode Framebuffer(FramebufferTarget target);
}

internal sealed class NativeGraphicsStatus : IGraphicsStatus
{
    internal static readonly NativeGraphicsStatus Instance = new();
    public int Limit(GetPName name) => GL.GetInteger(name);
    public ErrorCode Error() => GL.GetError();
    public FramebufferErrorCode Framebuffer(FramebufferTarget target) => GL.CheckFramebufferStatus(target);
}
