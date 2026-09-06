using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;

namespace VintageRTX.Test;

/// <summary>
/// Locks the hybrid solar-shadow contract that combines Vintage Story's exact animated,
/// alpha-tested cascades with VintageRTX's independent long-range voxel clipmap.
/// </summary>
[TestClass]
public sealed class VegetationSunShadowRegressionTests
{
    /// <summary>
    /// Verifies only complete public near/far framebuffer depth attachments are borrowed and that
    /// a missing cascade degrades independently instead of disabling the other native silhouette.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadows")]
    public void NativeCascadeDepthMapsAreBorrowedIndependentlyAndFailClosed()
    {
        List<FrameBufferRef> frameBuffers = [];
        while (frameBuffers.Count <= (int)EnumFrameBuffer.ShadowmapNear)
        {
            frameBuffers.Add(new FrameBufferRef());
        }

        frameBuffers[(int)EnumFrameBuffer.ShadowmapFar] = new FrameBufferRef
        {
            FboId = 21,
            Width = 2048,
            Height = 2048,
            DepthTextureId = 71
        };
        frameBuffers[(int)EnumFrameBuffer.ShadowmapNear] = new FrameBufferRef
        {
            FboId = 22,
            Width = 1024,
            Height = 1024,
            DepthTextureId = 72
        };

        Assert.AreEqual(
            new NativeSunShadowDepthMaps(71, 72),
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(frameBuffers));

        frameBuffers[(int)EnumFrameBuffer.ShadowmapNear].Disposed = true;
        Assert.AreEqual(
            new NativeSunShadowDepthMaps(71, 0),
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(frameBuffers));

        frameBuffers[(int)EnumFrameBuffer.ShadowmapNear].Disposed = false;
        frameBuffers[(int)EnumFrameBuffer.ShadowmapFar].DepthTextureId = 0;
        Assert.AreEqual(
            new NativeSunShadowDepthMaps(0, 72),
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(frameBuffers));
        Assert.AreEqual(
            default,
            FilmicDisplayRenderer.ResolveNativeSunShadowDepthMaps(null));
    }

    /// <summary>
    /// Guards registration and matrix capture at both official solar stages, plus the later binding
    /// of those exact matrices and borrowed depth names into distinct shadow-comparison samplers.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadows")]
    public void RendererCapturesBothOfficialCascadeStagesAndBindsTheirMatrices()
    {
        string source = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "Rendering",
            "FilmicDisplayRenderer.cs")).ReplaceLineEndings("\n");

        StringAssert.Contains(source, "EnumRenderStage.ShadowFar,");
        StringAssert.Contains(source, "\"vintagertx-native-shadow-far\"");
        StringAssert.Contains(source, "EnumRenderStage.ShadowNear,");
        StringAssert.Contains(source, "\"vintagertx-native-shadow-near\"");
        StringAssert.Contains(
            source,
            "if (stage is EnumRenderStage.ShadowFar or EnumRenderStage.ShadowNear)");
        StringAssert.Contains(source, "CaptureNativeSunShadowMatrix(stage);");
        StringAssert.Contains(source, "uniforms.ToShadowMapSpaceMatrixFar");
        StringAssert.Contains(source, "uniforms.ToShadowMapSpaceMatrixNear");
        StringAssert.Contains(source, "uniforms.playerReferencePos");
        StringAssert.Contains(source, "uniforms.ShadowRangeFar");
        StringAssert.Contains(source, "uniforms.ShadowRangeNear");
        StringAssert.Contains(
            source,
            "stage == EnumRenderStage.ShadowNear\n            ? nativeShadowMatrixNear\n            : nativeShadowMatrixFar");
        StringAssert.Contains(source, "ResolveNativeSunShadowDepthMaps(\n                api.Render.FrameBuffers)");
        StringAssert.Contains(source, "EnumFrameBuffer.ShadowmapFar");
        StringAssert.Contains(source, "EnumFrameBuffer.ShadowmapNear");
        StringAssert.Contains(source, "\"nativeShadowMapFar\"");
        StringAssert.Contains(source, "\"nativeShadowMapNear\"");
        StringAssert.Contains(source, "shader.UniformMatrix(\"nativeShadowMatrixFar\", nativeShadowMatrixFar);");
        StringAssert.Contains(source, "shader.UniformMatrix(\"nativeShadowMatrixNear\", nativeShadowMatrixNear);");
        StringAssert.Contains(source, "\"nativeShadowReferenceOffsetFar\"");
        StringAssert.Contains(source, "\"nativeShadowReferenceOffsetNear\"");
        StringAssert.Contains(source, "ResolveRenderFloatingOrigin(");
        StringAssert.Contains(source, "nativeShadowReferenceFar.X - nativeShadowFloatingOriginX");
        StringAssert.Contains(source, "nativeShadowReferenceNear.X - nativeShadowFloatingOriginX");
        StringAssert.Contains(source, "shader.Uniform(\"nativeShadowRangeFar\", nativeShadowRangeFar);");
        StringAssert.Contains(source, "shader.Uniform(\"nativeShadowRangeNear\", nativeShadowRangeNear);");
        StringAssert.Contains(source, "api.Event.UnregisterRenderer(this, EnumRenderStage.ShadowFar);");
        StringAssert.Contains(source, "api.Event.UnregisterRenderer(this, EnumRenderStage.ShadowNear);");
    }

    /// <summary>
    /// Ensures native comparison depth is authoritative inside its alpha/wind-aware cascades while
    /// a conservative voxel tail preserves the independent 96-m reach after the cascade exit.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Shadows")]
    public void ShaderUsesNativeVegetationLocallyAndVoxelVisibilityBeyondCascadeExit()
    {
        string shader = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "shaders",
            "display.frag"));

        StringAssert.Contains(shader, "uniform sampler2DShadow nativeShadowMapFar;");
        StringAssert.Contains(shader, "uniform sampler2DShadow nativeShadowMapNear;");
        StringAssert.Contains(shader, "uniform mat4 nativeShadowMatrixFar;");
        StringAssert.Contains(shader, "uniform mat4 nativeShadowMatrixNear;");
        StringAssert.Contains(shader, "uniform vec3 nativeShadowReferenceOffsetFar;");
        StringAssert.Contains(shader, "uniform vec3 nativeShadowReferenceOffsetNear;");
        StringAssert.Contains(shader, "uniform float nativeShadowRangeFar;");
        StringAssert.Contains(shader, "uniform float nativeShadowRangeNear;");
        StringAssert.Contains(shader, "float traceVoxelSunVisibility(vec3 rayOrigin)");
        StringAssert.Contains(shader, "float remainingDistance = sunShadowDistance - fineDistance;");
        StringAssert.Contains(
            shader,
            "return traceCoarseSunVisibility(coarseOrigin, sunDirection, remainingDistance + 0.45);");
        StringAssert.Contains(shader, "void traceNativeSunShadowVisibility(");
        StringAssert.Contains(shader, "receiverRelativeWorldPosition\n            - nativeShadowReferenceOffsetNear");
        StringAssert.Contains(shader, "receiverRelativeWorldPosition\n            - nativeShadowReferenceOffsetFar");
        StringAssert.Contains(shader, "float nativeShadowNearWeight(");
        StringAssert.Contains(shader, "float nativeShadowFarWeight(");
        StringAssert.Contains(shader, "textureSize(nativeShadowMapNear, 0)");
        StringAssert.Contains(shader, "textureSize(nativeShadowMapFar, 0)");
        StringAssert.Contains(shader, "for (int x = -1; x <= 1; x++)");
        StringAssert.Contains(shader, "return visibility / 9.0;");
        StringAssert.Contains(shader, "float traceVoxelSunTail(vec3 rayOrigin, float coveredDistance)");
        StringAssert.Contains(shader, "traceVoxelSunTail(voxelRayOrigin, nativeCoveredDistance)");
        StringAssert.Contains(shader, "if (nativeSupport >= 0.9999)");
        StringAssert.Contains(shader, "return nativeAndTailVisibility;");
        StringAssert.Contains(shader, "return mix(");
        StringAssert.Contains(shader, "traceSunVisibility(relativeWorldPosition, sunRayOrigin)");
        StringAssert.Contains(shader, "vec3 rawRelativeWorldPosition = (");
        StringAssert.Contains(shader, "vec3 earlyRelativeWorldPosition = (");
        StringAssert.Contains(shader, "return clamp(coverage, 0.0, sunShadowDistance);");
        StringAssert.Contains(shader, "R=full voxel solar occlusion, G=native alpha-tested occlusion");
        StringAssert.Contains(shader, "if (debugView == 17)");
    }
}
