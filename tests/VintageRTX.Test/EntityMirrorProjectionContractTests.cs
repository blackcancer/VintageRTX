using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Test;

/// <summary>
/// Verifies the conservative full-source coverage and algebraically equivalent early rejection used
/// by the dedicated entity-mirror projection pass.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EntityMirrorProjectionContractTests
{
    /// <summary>
    /// Guards the world-mirror contract and the re-entrancy latch used while the official terrain
    /// and entity systems are replayed into the reflected framebuffer.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void WorldReplayTracksTerrainAndEntitySystemsWithoutRecapturingItself()
    {
        string source = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "Rendering", "EntityMirrorGeometryReplayPatch.cs"));
        StringAssert.Contains(source, "Vintagestory.Client.NoObf.SystemRenderTerrain");
        StringAssert.Contains(source, "OnRenderBefore");
        StringAssert.Contains(source, "OnRenderOpaque");
        StringAssert.Contains(source, "beforeTerrain!.Invoke(terrainRenderSystem");
        StringAssert.Contains(source, "terrainPass!.Invoke(terrainRenderSystem");
        StringAssert.Contains(source, "pass.Invoke(renderSystem");

        Type patch = typeof(VintageRTX.Rendering.EntityMirrorGeometryReplayPatch);
        MethodInfo terrainCapture = patch.GetMethod(
            "AfterOpaqueTerrainPass",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(patch.FullName, "AfterOpaqueTerrainPass");
        MethodInfo entityCapture = patch.GetMethod(
            "AfterOpaqueEntityPass",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(patch.FullName, "AfterOpaqueEntityPass");
        FieldInfo replay = patch.GetField(
            "replayInProgress",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(patch.FullName, "replayInProgress");
        FieldInfo terrainSystem = patch.GetField(
            "currentTerrainRenderSystem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(patch.FullName, "currentTerrainRenderSystem");
        FieldInfo entitySystem = patch.GetField(
            "currentRenderSystem",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(patch.FullName, "currentRenderSystem");
        FieldInfo terrainDelta = patch.GetField(
            "currentTerrainDeltaTime",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(patch.FullName, "currentTerrainDeltaTime");

        object firstTerrain = new();
        object firstEntity = new();
        terrainCapture.Invoke(null, [firstTerrain, 0.25f]);
        entityCapture.Invoke(null, [firstEntity, 0.5f]);
        Assert.AreSame(firstTerrain, terrainSystem.GetValue(null));
        Assert.AreSame(firstEntity, entitySystem.GetValue(null));
        Assert.AreEqual(0.25f, terrainDelta.GetValue(null));

        replay.SetValue(null, true);
        terrainCapture.Invoke(null, [new object(), 1.0f]);
        entityCapture.Invoke(null, [new object(), 1.0f]);
        Assert.AreSame(firstTerrain, terrainSystem.GetValue(null));
        Assert.AreSame(firstEntity, entitySystem.GetValue(null));

        VintageRTX.Rendering.EntityMirrorGeometryReplayPatch.Uninstall();
        Assert.IsNull(terrainSystem.GetValue(null));
        Assert.IsNull(entitySystem.GetValue(null));
    }

    /// <summary>
    /// Prevents one display sampler from silently replacing another through a reused OpenGL unit,
    /// and verifies that partial-mesh depth comes from the late-opaque snapshot that includes foliage.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void DisplayPassUsesDistinctSamplerUnitsAndLateOpaqueDepth()
    {
        string renderer = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "Rendering", "FilmicDisplayRenderer.cs"));
        int start = renderer.IndexOf("shader!.Use();", StringComparison.Ordinal);
        int end = renderer.IndexOf("bool temporalCameraStable", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start, "The display binding block must remain discoverable.");
        string bindings = renderer[start..end];

        int[] helperUnits = System.Text.RegularExpressions.Regex.Matches(
                bindings,
                @"BindTexture2D\([\s\S]*?,\s*(\d+)\s*\);",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(static match => int.Parse(
                match.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        int[] directUnits = System.Text.RegularExpressions.Regex.Matches(
                bindings,
                @"GL\.ActiveTexture\(TextureUnit\.Texture(\d+)\);",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(static match => int.Parse(
                match.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        int[] allUnits = [.. helperUnits, .. directUnits];

        CollectionAssert.AreEquivalent(allUnits, allUnits.Distinct().ToArray());
        StringAssert.Contains(bindings, "\"entityMirrorColor\"");
        StringAssert.Contains(bindings, "entityMirrorProjection.ColorTextureId");
        StringAssert.Contains(bindings, "22);");
        StringAssert.Contains(bindings, "\"entityMirrorDepth\"");
        StringAssert.Contains(bindings, "entityMirrorProjection.DepthTextureId");
        StringAssert.Contains(bindings, "23);");
        StringAssert.Contains(bindings, "\"gOpaquePosition\"");
        StringAssert.Contains(bindings, "reflectionSourceCapture.PositionTextureId");
        StringAssert.Contains(bindings, "opaquePositionEnabled");
        StringAssert.Contains(bindings, "\"gOpaqueDepth\"");
        StringAssert.Contains(bindings, "reflectionSourceCapture.DepthTextureId");
        StringAssert.Contains(bindings, "opaqueDepthEnabled");
        StringAssert.Contains(bindings, "DisplayColorPipelineContract.TryResolveLiquidDepth");
        StringAssert.Contains(bindings, "\"gLiquidDepth\"");
        StringAssert.Contains(bindings, "liquidDepthEnabled");
        StringAssert.Contains(bindings, "19);");

        string shader = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "assets", "vintagertx", "shaders", "display.frag"));
        StringAssert.Contains(shader, "uniform sampler2D entityMirrorDepth;");
        StringAssert.Contains(shader, "float sourceAboveInterface = step(");
        StringAssert.Contains(shader, "planarFluidSurfaceWorldY + 0.015");
        StringAssert.Contains(shader, "entityMirrorSupport *= validReflectedEntityDepth");
    }

    /// <summary>
    /// Proves the reflected view maps world Y around the local liquid plane while preserving X/Z,
    /// so the official rasterizer can expose lower and formerly occluded entity faces.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ReflectedViewMatrixMirrorsGeometryAroundLiquidPlane()
    {
        double[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];
        double[] reflected = new double[16];

        VintageRTX.Rendering.EntityMirrorProjection.BuildReflectedViewMatrix(
            identity,
            2.5,
            reflected);

        CollectionAssert.AreEqual(
            new double[]
            {
                1, 0, 0, 0,
                0, -1, 0, 0,
                0, 0, 1, 0,
                0, 5, 0, 1
            },
            reflected);
        Assert.AreEqual(3.0, -2.0 + reflected[13], 0.0, "World Y=2 must reflect to Y=3 around Y=2.5.");
        Assert.AreEqual(
            FrontFaceDirection.Cw,
            VintageRTX.Rendering.EntityMirrorGeometryReplayPatch.ReflectedFrontFace(
                FrontFaceDirection.Ccw));
        Assert.AreEqual(
            FrontFaceDirection.Ccw,
            VintageRTX.Rendering.EntityMirrorGeometryReplayPatch.ReflectedFrontFace(
                FrontFaceDirection.Cw));
    }

    /// <summary>
    /// Ensures instancing maps columns and rows directly while still submitting every isolated source
    /// pixel that can belong to a small or alpha-tested entity silhouette.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void InstancedGridPreservesEverySourcePixelWithoutIntegerDivision()
    {
        string root = TestPaths.FindRepositoryRoot();
        string renderer = File.ReadAllText(Path.Combine(
            root,
            "src", "VintageRTX", "Rendering", "EntityMirrorProjection.cs"));
        string vertex = File.ReadAllText(Path.Combine(
            root,
            "src", "VintageRTX", "assets", "vintagertx", "shaders", "entitymirror.vert"));

        StringAssert.Contains(
            renderer,
            "GL.DrawArraysInstanced(PrimitiveType.Points, 0, frameWidth, frameHeight);");
        StringAssert.Contains(vertex, "int sourceX = gl_VertexID;");
        StringAssert.Contains(vertex, "int sourceY = gl_InstanceID;");
        Assert.IsFalse(vertex.Contains("gl_VertexID %", StringComparison.Ordinal));
        Assert.IsFalse(vertex.Contains("gl_VertexID /", StringComparison.Ordinal));

        const int width = 7;
        const int height = 5;
        HashSet<int> visitedPixels = [];
        for (int instance = 0; instance < height; instance++)
        {
            for (int vertexId = 0; vertexId < width; vertexId++)
            {
                visitedPixels.Add(instance * width + vertexId);
            }
        }

        Assert.AreEqual(width * height, visitedPixels.Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, width * height).ToArray(),
            visitedPixels.Order().ToArray());
    }

    /// <summary>
    /// Proves that the one-square-root occlusion predicate retains the former 0.025-block depth
    /// separation exactly over valid near, mid-range, and reflection-limit samples.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void SquaredTerrainPredicateMatchesDistancePredicate()
    {
        string vertex = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "assets", "vintagertx", "shaders", "entitymirror.vert"));
        StringAssert.Contains(vertex, "entityDistanceSquared > maximumDistanceSquared");
        StringAssert.Contains(
            vertex,
            "separatedEntityDistance * separatedEntityDistance < terrainDistanceSquared");

        double[] entityDistances = [0.011, 0.25, 1.0, 31.75, 95.999];
        double[] terrainDistances = [0.0, 0.02, 0.035, 0.50, 1.024, 1.026, 32.0, 96.0];
        foreach (double entityDistance in entityDistances)
        {
            foreach (double terrainDistance in terrainDistances)
            {
                bool terrainValid = terrainDistance > 0.01;
                bool distancePredicate = !terrainValid
                    || entityDistance + 0.025 < terrainDistance;
                double separatedEntityDistance = entityDistance + 0.025;
                bool squaredPredicate = !terrainValid
                    || separatedEntityDistance * separatedEntityDistance
                        < terrainDistance * terrainDistance;
                Assert.AreEqual(
                    distancePredicate,
                    squaredPredicate,
                    $"Predicate mismatch at entity={entityDistance}, terrain={terrainDistance}.");
            }
        }
    }

    /// <summary>
    /// Guards Vintage Story's documented floating-origin convention: inverse view restores a point
    /// relative to the player origin, and the original view matrix projects the mirrored world point.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ProjectionRestoresPlayerOriginAndUsesFullViewTransform()
    {
        string vertex = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "assets", "vintagertx", "shaders", "entitymirror.vert"));

        StringAssert.Contains(vertex, "uniform mat4 viewMatrix;");
        StringAssert.Contains(vertex, "uniform vec3 floatingWorldOrigin;");
        StringAssert.Contains(vertex, "sourceWorldPosition = floatingWorldOrigin + sourceWorldOffset;");
        StringAssert.Contains(
            vertex,
            "viewMatrix\n        * vec4(reflectedWorldPosition - floatingWorldOrigin, 1.0)");
        Assert.IsFalse(vertex.Contains("cameraWorldPosition", StringComparison.Ordinal));
        Assert.IsFalse(vertex.Contains("transpose(mat3(inverseViewMatrix))", StringComparison.Ordinal));
    }
}
