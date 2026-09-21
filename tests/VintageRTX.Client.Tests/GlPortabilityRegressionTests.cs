using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using VintageRTX.Client;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class GlPortabilityRegressionTests
{
    [DataTestMethod]
    [DataRow(1, 0, 0)]
    [DataRow(1, 1, 0)]
    [DataRow(1, 0, (int)PolygonMode.Point)]
    [DataRow(2, 1, 0)]
    public void SinglePolygonStateIgnoresUnusedSecondSlot(int profileMask, int flags, int secondSlot)
    {
        using var gl = PortableGlContext.Create();
        var state = new RewritePolygonModes(profileMask, flags, (int)PolygonMode.Line, secondSlot);
        Assert.IsFalse(state.SeparateFaces); Assert.AreEqual(PolygonMode.Line, state.Back);
        // A driver writing a single scalar leaves the other slot unchanged. Feed that exact
        // representation through the production decoder, even when Mesa writes two equal values.
        state.Restore(); Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        int[] actual = new int[2]; GL.GetInteger(GetPName.PolygonMode, actual);
        Assert.AreEqual((int)PolygonMode.Line, actual[0]);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        // Negative control: the old restoration's separate call really is rejected in core.
        GL.PolygonMode(TriangleFace.Front, PolygonMode.Line);
        Assert.AreEqual(ErrorCode.InvalidEnum, GL.GetError());
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [DataTestMethod]
    [DataRow((int)PolygonMode.Fill)]
    [DataRow((int)PolygonMode.Line)]
    [DataRow((int)PolygonMode.Point)]
    public void RealCoreStateRoundTripsTwiceWithoutContaminatingTheNextPass(int mode)
    {
        using var gl = PortableGlContext.Create();
        GL.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)mode);
        for (int frame = 0; frame < 2; frame++)
        {
            using (var state = new RewriteDrawState(1, 1))
            {
                state.Configure();
                int[] during = new int[2]; GL.GetInteger(GetPName.PolygonMode, during);
                Assert.AreEqual((int)PolygonMode.Fill, during[0]);
                Assert.AreEqual(ErrorCode.NoError, GL.GetError());
            }
            int[] after = new int[2]; GL.GetInteger(GetPName.PolygonMode, after);
            Assert.AreEqual(mode, after[0]); Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        }
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
    }

    [TestMethod]
    public void CompatibilityEqualFacesAndExceptionUnwindBothRestoreTheOriginalMode()
    {
        using var gl = PortableGlContext.Create(ContextProfile.Compatability);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Point);
        Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var state = new RewriteDrawState(1, 1); state.Configure();
            throw new InvalidOperationException("controlled body failure");
        });
        int[] modes = new int[2]; GL.GetInteger(GetPName.PolygonMode, modes);
        CollectionAssert.AreEqual(new[] {(int)PolygonMode.Point, (int)PolygonMode.Point}, modes);
        Assert.AreEqual(ErrorCode.NoError, GL.GetError());
    }

    [TestMethod]
    public void TheLinkFailureIsAnActualInterfaceFailureAndTheNextProgramStillLinks()
    {
        using var gl = PortableGlContext.Create();
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        CompileIndependently(ShaderType.VertexShader, ShaderLinkFailureFixture.Vertex);
        CompileIndependently(ShaderType.FragmentShader, ShaderLinkFailureFixture.Fragment);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.ThrowsException<InvalidOperationException>(() => DirectImagePass.LinkSources(
                ShaderLinkFailureFixture.Vertex, ShaderLinkFailureFixture.Fragment));
            StringAssert.Contains(error.Message, "shader link failed");
            int valid = DirectImagePass.LinkSources(ShaderLinkFailureFixture.Vertex, ShaderLinkFailureFixture.CompatibleFragment);
            try
            {
                GL.GetProgram(valid, GetProgramParameterName.LinkStatus, out int linked);
                Assert.AreEqual(1, linked); Assert.AreEqual(previous, GL.GetInteger(GetPName.CurrentProgram));
            }
            finally { GL.DeleteProgram(valid); }
            Assert.AreEqual(ErrorCode.NoError, GL.GetError());
        }
        var vertexError = Assert.ThrowsException<InvalidOperationException>(() => DirectImagePass.LinkSources(
            "#version 330 core\nnot GLSL", ShaderLinkFailureFixture.CompatibleFragment));
        StringAssert.Contains(vertexError.Message, "VertexShader compilation failed");
    }

    private static void CompileIndependently(ShaderType stage, string source)
    {
        int shader = GL.CreateShader(stage);
        try
        {
            GL.ShaderSource(shader, source); GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            Assert.AreEqual(1, compiled, GL.GetShaderInfoLog(shader));
        }
        finally { GL.DeleteShader(shader); }
    }
}
