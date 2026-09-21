using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Client;

/// <summary>Polygon state interpreted using the real context, never guessed from driver output slots.</summary>
internal readonly struct RewritePolygonModes
{
    internal PolygonMode Front { get; }
    internal PolygonMode Back { get; }
    internal bool SeparateFaces { get; }

    internal RewritePolygonModes(int profileMask, int contextFlags, int reportedFront, int reportedBack)
    {
        // GL_CONTEXT_COMPATIBILITY_PROFILE_BIT = 2; GL_CONTEXT_FLAG_FORWARD_COMPATIBLE_BIT = 1.
        // Our minimum version is 3.3, where querying these flags is defined.
        SeparateFaces = (profileMask & 2) != 0 && (contextFlags & 1) == 0;
        Front = (PolygonMode)reportedFront;
        Back = SeparateFaces ? (PolygonMode)reportedBack : Front;
    }

    internal void Restore()
    {
        if (!SeparateFaces || Front == Back)
            GL.PolygonMode(TriangleFace.FrontAndBack, Front);
        else
        {
            GL.PolygonMode(TriangleFace.Front, Front);
            GL.PolygonMode(TriangleFace.Back, Back);
        }
    }
}
