using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Datastructures;

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
        StringAssert.Contains(
            source,
            "|| replayInProgress",
            "A nested official callback must not overwrite the outer replay's camera snapshots.");

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
        int end = renderer.IndexOf(
            "shader.UniformMatrix(\"projection\"",
            start,
            StringComparison.Ordinal);
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
    /// Proves the reflected replay's OpenGL near plane accepts geometry above the liquid interface
    /// and rejects submerged geometry before either point can participate in depth testing.
    /// </summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ObliqueMirrorProjectionClipsSubmergedGeometryForSeveralCameraPitches()
    {
        const double surfaceY = 0.35;
        const double clipBias = 0.015;
        double[] projection = CreatePerspectiveProjection(
            62.0 * Math.PI / 180.0,
            16.0 / 9.0,
            0.05,
            160.0);

        foreach (double pitch in new[] { -0.31, 0.0, 0.27 })
        {
            double[] ordinaryView = CreatePitchedView(pitch, cameraY: 2.4);
            double[] mirroredView = new double[16];
            double[] obliqueProjection = new double[16];
            VintageRTX.Rendering.EntityMirrorProjection.BuildReflectedViewMatrix(
                ordinaryView,
                surfaceY,
                mirroredView);

            Assert.IsTrue(
                VintageRTX.Rendering.EntityMirrorProjection.BuildObliqueMirrorProjection(
                    projection,
                    mirroredView,
                    surfaceY,
                    clipBias,
                    obliqueProjection),
                $"The oblique projection must remain finite at pitch {pitch:0.000}.");

            double retainedNearDistance = EvaluateOpenGlNearPlane(
                obliqueProjection,
                mirroredView,
                [0.0, surfaceY + clipBias + 0.20, -8.0, 1.0]);
            double submergedNearDistance = EvaluateOpenGlNearPlane(
                obliqueProjection,
                mirroredView,
                [0.0, surfaceY + clipBias - 0.20, -8.0, 1.0]);

            Assert.IsTrue(
                retainedNearDistance > 1e-6,
                $"Above-water geometry was clipped at pitch {pitch:0.000}: {retainedNearDistance}.");
            Assert.IsTrue(
                submergedNearDistance < -1e-6,
                $"Submerged geometry survived at pitch {pitch:0.000}: {submergedNearDistance}.");
            CollectionAssert.AreEqual(
                new[]
                {
                    projection[0], projection[1], projection[3],
                    projection[4], projection[5], projection[7],
                    projection[8], projection[9], projection[11],
                    projection[12], projection[13], projection[15]
                },
                new[]
                {
                    obliqueProjection[0], obliqueProjection[1], obliqueProjection[3],
                    obliqueProjection[4], obliqueProjection[5], obliqueProjection[7],
                    obliqueProjection[8], obliqueProjection[9], obliqueProjection[11],
                    obliqueProjection[12], obliqueProjection[13], obliqueProjection[15]
                },
                "Only the projection's third row may be replaced by the clip plane.");
        }
    }

    /// <summary>Ensures invalid projection inputs suppress official replay instead of exposing stale depth.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ObliqueMirrorProjectionFailsClosedForSingularOrNonFiniteInputs()
    {
        double[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];
        double[] destination = Enumerable.Repeat(7.0, 16).ToArray();

        Assert.IsFalse(
            VintageRTX.Rendering.EntityMirrorProjection.BuildObliqueMirrorProjection(
                new double[16],
                identity,
                0.0,
                0.015,
                destination));
        CollectionAssert.AreEqual(new double[16], destination);

        double[] projection = CreatePerspectiveProjection(
            60.0 * Math.PI / 180.0,
            1.0,
            0.1,
            64.0);
        identity[0] = double.NaN;
        Array.Fill(destination, 3.0);
        Assert.IsFalse(
            VintageRTX.Rendering.EntityMirrorProjection.BuildObliqueMirrorProjection(
                projection,
                identity,
                0.0,
                0.015,
                destination));
        CollectionAssert.AreEqual(new double[16], destination);

        double[] underwaterView = CreatePitchedView(0.0, cameraY: -1.0);
        double[] reflectedUnderwaterView = new double[16];
        VintageRTX.Rendering.EntityMirrorProjection.BuildReflectedViewMatrix(
            underwaterView,
            0.0,
            reflectedUnderwaterView);
        Array.Fill(destination, 5.0);
        Assert.IsFalse(
            VintageRTX.Rendering.EntityMirrorProjection.BuildObliqueMirrorProjection(
                projection,
                reflectedUnderwaterView,
                0.0,
                0.015,
                destination),
            "An underwater source camera needs an internal-reflection model, not a reversed above-water clip plane.");
        CollectionAssert.AreEqual(new double[16], destination);
    }

    /// <summary>Proves the scoped replay removes its pushed projection and restores the exact top.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void ProjectionStackRestorationIsExactAndRepairsUnexpectedUnderflow()
    {
        double[] savedTop =
        [
            2, 0, 0, 0,
            0, 3, 0, 0,
            0, 0, -1, -1,
            0, 0, -0.2, 0
        ];
        double[] mirror = (double[])savedTop.Clone();
        mirror[2] = 0.25;
        mirror[6] = -0.5;

        StackMatrix4 ordinary = new(4);
        ordinary.Push(savedTop);
        ordinary.Push(mirror);
        VintageRTX.Rendering.EntityMirrorGeometryReplayPatch.RestoreProjectionStack(
            ordinary,
            savedCount: 1,
            projectionPushed: true,
            savedTop: savedTop);
        Assert.AreEqual(1, ordinary.Count);
        CollectionAssert.AreEqual(savedTop, ordinary.Top);

        StackMatrix4 underflow = new(4);
        VintageRTX.Rendering.EntityMirrorGeometryReplayPatch.RestoreProjectionStack(
            underflow,
            savedCount: 1,
            projectionPushed: false,
            savedTop: savedTop);
        Assert.AreEqual(1, underflow.Count);
        CollectionAssert.AreEqual(savedTop, underflow.Top);
    }

    /// <summary>Proves the outer replay guard restores all engine-visible camera carriers together.</summary>
    [TestMethod]
    [TestCategory("Rendering")]
    [TestCategory("Reflection")]
    public void MutableCameraCarrierRestorationRepairsViewProjectionAndStackTogether()
    {
        double[] savedCamera = Enumerable.Range(1, 16).Select(static value => (double)value).ToArray();
        float[] savedCameraFloat = savedCamera.Select(static value => (float)value).ToArray();
        double[] savedProjection = Enumerable.Range(21, 16).Select(static value => (double)value).ToArray();
        float[] savedCurrentProjection = savedProjection.Select(static value => (float)value).ToArray();
        double[] camera = Enumerable.Repeat(-1.0, 16).ToArray();
        float[] cameraFloat = Enumerable.Repeat(-2.0f, 16).ToArray();
        double[] projection = Enumerable.Repeat(-3.0, 16).ToArray();
        float[] currentProjection = Enumerable.Repeat(-4.0f, 16).ToArray();
        StackMatrix4 stack = new(4);
        stack.Push(savedProjection);
        stack.Push(Enumerable.Repeat(99.0, 16).ToArray());

        VintageRTX.Rendering.EntityMirrorGeometryReplayPatch.RestoreMutableCameraCarriers(
            camera,
            savedCamera,
            cameraFloat,
            savedCameraFloat,
            projection,
            savedProjection,
            currentProjection,
            savedCurrentProjection,
            stack,
            savedProjectionStackCount: 1,
            savedProjectionStackTop: savedProjection);

        CollectionAssert.AreEqual(savedCamera, camera);
        CollectionAssert.AreEqual(savedCameraFloat, cameraFloat);
        CollectionAssert.AreEqual(savedProjection, projection);
        CollectionAssert.AreEqual(savedCurrentProjection, currentProjection);
        Assert.AreEqual(1, stack.Count);
        CollectionAssert.AreEqual(savedProjection, stack.Top);
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

    /// <summary>Builds a conventional column-major OpenGL perspective matrix.</summary>
    private static double[] CreatePerspectiveProjection(
        double verticalFieldOfView,
        double aspectRatio,
        double near,
        double far)
    {
        double focal = 1.0 / Math.Tan(verticalFieldOfView * 0.5);
        return
        [
            focal / aspectRatio, 0, 0, 0,
            0, focal, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, (2.0 * far * near) / (near - far), 0
        ];
    }

    /// <summary>Builds a pitched view for a camera translated above the local origin.</summary>
    private static double[] CreatePitchedView(double pitch, double cameraY)
    {
        double cosine = Math.Cos(pitch);
        double sine = Math.Sin(pitch);
        return
        [
            1, 0, 0, 0,
            0, cosine, sine, 0,
            0, -sine, cosine, 0,
            0, -cameraY * cosine, -cameraY * sine, 1
        ];
    }

    /// <summary>Evaluates z plus w after reflected view and oblique projection.</summary>
    private static double EvaluateOpenGlNearPlane(
        double[] projection,
        double[] view,
        double[] worldPoint)
    {
        double[] viewPoint = Multiply(view, worldPoint);
        double[] clipPoint = Multiply(projection, viewPoint);
        return clipPoint[2] + clipPoint[3];
    }

    /// <summary>Multiplies one column-major matrix by a homogeneous column vector.</summary>
    private static double[] Multiply(double[] matrix, double[] point) =>
    [
        matrix[0] * point[0] + matrix[4] * point[1] + matrix[8] * point[2] + matrix[12] * point[3],
        matrix[1] * point[0] + matrix[5] * point[1] + matrix[9] * point[2] + matrix[13] * point[3],
        matrix[2] * point[0] + matrix[6] * point[1] + matrix[10] * point[2] + matrix[14] * point[3],
        matrix[3] * point[0] + matrix[7] * point[1] + matrix[11] * point[2] + matrix[15] * point[3]
    ];
}
