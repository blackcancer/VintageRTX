using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Exercises the production optional MRT target in a real hidden OpenGL context.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RawAlbedoTargetTests
{
    /// <summary>Preserves unlit RGB under changing light and prevents unsupported output contamination.</summary>
    [TestMethod]
    [TestCategory("GPU")]
    public void RawAttachmentKeepsColourAndRestoresNativeState()
    {
        GLFWProvider.CheckForMainThread = false;
        using NativeWindow window = new(new NativeWindowSettings
        {
            ClientSize = new Vector2i(8, 8), StartVisible = false,
            API = ContextAPI.OpenGL, APIVersion = new Version(3, 3), Profile = ContextProfile.Core
        });
        window.Context.MakeCurrent();
        GL.LoadBindings(new GLFWBindingsContext());
        int framebuffer = GL.GenFramebuffer();
        int sentinelTexture = GL.GenTexture();
        int vao = GL.GenVertexArray();
        int[] textures = new int[4];
        int program = 0;
        using RawAlbedoTarget target = new();
        try
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            for (int i = 0; i < 4; i++)
            {
                textures[i] = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, textures[i]);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, 8, 8, 0,
                    PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                    (FramebufferAttachment)((int)FramebufferAttachment.ColorAttachment0 + i), TextureTarget.Texture2D, textures[i], 0);
            }
            DrawBuffersEnum[] native = [DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1,
                DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3];
            GL.DrawBuffers(native.Length, native);
            GL.ActiveTexture(TextureUnit.Texture5);
            GL.BindTexture(TextureTarget.Texture2D, sentinelTexture);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(0, 0, 1, 1);
            GL.Enable(IndexedEnableCap.Blend, 4);
            GL.ColorMask(4, false, true, false, true);
            Assert.IsTrue(target.Begin(framebuffer, 8, 8));
            Assert.IsFalse(target.Ready);
            Assert.IsTrue(GL.IsEnabled(EnableCap.ScissorTest));
            GL.GetInteger(GetPName.ActiveTexture, out int unit);
            GL.GetInteger(GetPName.TextureBinding2D, out int binding);
            Assert.AreEqual((int)TextureUnit.Texture5, unit);
            Assert.AreEqual(sentinelTexture, binding);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Viewport(0, 0, 8, 8);
            program = MakeProgram();
            GL.UseProgram(program);
            GL.BindVertexArray(vao);
            target.SetWriter(true);
            GL.Uniform1(GL.GetUniformLocation(program, "illumination"), 0.03f);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            // A second lighting value changes the raster carrier but not the material itself.
            GL.Uniform1(GL.GetUniformLocation(program, "illumination"), 12.0f);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            target.SetWriter(false);
            GL.GetInteger(GetPName.DrawBuffer4, out int route);
            Assert.AreEqual(0, route);
            target.End();
            Assert.IsTrue(target.Ready);
            Assert.IsTrue(GL.IsEnabled(IndexedEnableCap.Blend, 4));
            bool[] mask = new bool[4];
            GL.GetBoolean(GetIndexedPName.ColorWritemask, 4, mask);
            CollectionAssert.AreEqual(new[] { false, true, false, true }, mask);
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment4, FramebufferParameterName.FramebufferAttachmentObjectType, out int owner);
            Assert.AreEqual(0, owner);
            for (int i = 0; i < 4; i++)
            {
                GL.GetInteger((GetPName)((int)GetPName.DrawBuffer0 + i), out route);
                Assert.AreEqual((int)native[i], route);
            }
            GL.BindTexture(TextureTarget.Texture2D, target.TextureId);
            float[] pixels = new float[8 * 8 * 4];
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, pixels);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.AreEqual(.72f, pixels[i], .001f);
                Assert.AreEqual(.31f, pixels[i + 1], .001f);
                Assert.AreEqual(.08f, pixels[i + 2], .001f);
                Assert.AreEqual(-3.0f, pixels[i + 3], .001f);
            }
            Assert.IsTrue(target.Begin(framebuffer, 8, 8));
            target.SetWriter(false);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            target.End();
            Assert.IsFalse(target.Ready, "A frame with no supported writer must not publish stale material.");
            // Never overwrite a foreign mod's fifth attachment.
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
                TextureTarget.Texture2D, textures[0], 0);
            Assert.IsFalse(target.Begin(framebuffer, 8, 8));
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment4, FramebufferParameterName.FramebufferAttachmentObjectName, out owner);
            Assert.AreEqual(textures[0], owner);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4, TextureTarget.Texture2D, 0, 0);
            Assert.AreEqual(OpenTK.Graphics.OpenGL4.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            target.Dispose();
            if (program != 0) GL.DeleteProgram(program);
            GL.DeleteVertexArray(vao);
            GL.DeleteTexture(sentinelTexture);
            foreach (int texture in textures) if (texture != 0) GL.DeleteTexture(texture);
            GL.DeleteFramebuffer(framebuffer);
        }
    }

    /// <summary>Links a genuine opaque writer whose material output is independent of illumination.</summary>
    /// <returns>Owned linked program handle.</returns>
    private static int MakeProgram()
    {
        const string vertex = "#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0.,1.);}";
        const string fragment = "#version 330 core\nuniform float illumination;layout(location=0) out vec4 raster;layout(location=4) out vec4 vintagertxUnlitAlbedo;void main(){vec3 albedo=vec3(.72,.31,.08);raster=vec4(albedo*illumination,1.);vintagertxUnlitAlbedo=vec4(albedo,-3.);}";
        int v = GL.CreateShader(ShaderType.VertexShader);
        int f = GL.CreateShader(ShaderType.FragmentShader);
        int p = GL.CreateProgram();
        try
        {
            GL.ShaderSource(v, vertex); GL.CompileShader(v);
            GL.ShaderSource(f, fragment); GL.CompileShader(f);
            GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
            Assert.AreEqual(1, linked, GL.GetProgramInfoLog(p));
            return p;
        }
        catch { GL.DeleteProgram(p); throw; }
        finally { GL.DeleteShader(v); GL.DeleteShader(f); }
    }
}
