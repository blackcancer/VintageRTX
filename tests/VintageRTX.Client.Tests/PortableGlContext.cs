using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace VintageRTX.Client.Tests;

/// <summary>Only for isolated test windows. Never changes the game's context or its global hints.</summary>
internal static class PortableGlContext
{
    internal static NativeWindow Create(ContextProfile profile = ContextProfile.Core)
    {
        GLFWProvider.CheckForMainThread = false;
        GLFWProvider.EnsureInitialized();
        // GLFW hints survive previous window creations. NativeWindow sets true flags, but some
        // supported OpenTK builds do not reset their false counterparts. Start an explicit request.
        GLFW.DefaultWindowHints();
        bool forward = profile == ContextProfile.Core;
        GLFW.WindowHint(WindowHintBool.OpenGLForwardCompat, forward);
        var window = new NativeWindow(new NativeWindowSettings
        {
            ClientSize = new(8, 8), StartVisible = false, API = ContextAPI.OpenGL,
            APIVersion = new(3, 3), Profile = profile,
            Flags = forward ? ContextFlags.ForwardCompatible : ContextFlags.Default
        });
        try
        {
            window.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext());
            int mask = GL.GetInteger(GetPName.ContextProfileMask), flags = GL.GetInteger(GetPName.ContextFlags);
            Console.WriteLine("OpenGL version={0}; vendor={1}; renderer={2}; profile=0x{3:X}; flags=0x{4:X}",
                GL.GetString(StringName.Version), GL.GetString(StringName.Vendor), GL.GetString(StringName.Renderer), mask, flags);
            Assert.AreNotEqual(0, mask & (forward ? 1 : 2), "The driver did not create the requested profile.");
            Assert.AreEqual(forward ? 1 : 0, flags & 1, "The context retained an unexpected forward-compatible flag.");
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, GL.GetError());
            return window;
        }
        catch { window.Dispose(); throw; }
    }
}

internal static class ShaderLinkFailureFixture
{
    // Both stages compile independently. A statically consumed interface member with a different
    // type in the two stages is a required GLSL link error, not a driver resource-budget guess.
    internal const string Vertex = """
        #version 330 core
        out vec3 vrtxLinkWitness;
        void main() {
            vrtxLinkWitness = vec3(float(gl_VertexID), 0.25, 0.75);
            gl_Position = vec4(0, 0, 0, 1);
        }
        """;
    internal const string Fragment = """
        #version 330 core
        in vec2 vrtxLinkWitness;
        out vec4 outputColor;
        void main() { outputColor = vec4(vrtxLinkWitness, 0, 1); }
        """;
    internal const string CompatibleFragment = """
        #version 330 core
        in vec3 vrtxLinkWitness;
        out vec4 outputColor;
        void main() { outputColor = vec4(vrtxLinkWitness, 1); }
        """;
}
