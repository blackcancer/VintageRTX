using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Numerics;
using VintageRTX.Rendering;

namespace VintageRTX.RenderLab;

/// <summary>
/// Verifies the deterministic screen-space contract shared by generic opaque entities,
/// alpha-tested silhouettes, liquid-interface rejection, and first-person isolation.
/// </summary>
[TestClass]
public sealed class GenericEntityReflectionProjectionTests
{
    /// <summary>
    /// Proves that the physical LiquidLevel plane produces the expected reflected area and
    /// centroid for generic emerged geometry while submerged and direct-only geometry cannot leak.
    /// </summary>
    [TestMethod]
    [TestCategory("RenderLab")]
    [TestCategory("Reflection")]
    [TestCategory("Liquid")]
    public void GenericEntityProjectionUsesPhysicalLiquidHeightAndIsolationMasks()
    {
        const int width = 640;
        const int height = 360;
        const float blockWorldY = 12.0f;
        const byte liquidLevel = 3;
        const string item = "item";
        const string humanoid = "humanoid-alpha-tested";
        const string submerged = "submerged";
        const string firstPerson = "first-person-direct-only";

        float surfaceWorldY = blockWorldY
            + LiquidSurfaceRuntime.DecodeFluidFillHeightMetres(liquidLevel);
        Assert.AreEqual(12.375f, surfaceWorldY, 1.0e-6f);

        string shader = DisplayShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory).Fragment;
        StringAssert.Contains(
            shader,
            "return voxelOrigin.y + topLocalY + liquidLevel / 8.0;");
        StringAssert.Contains(shader, "screenBelowReflectingSurfaceEvidence(");
        StringAssert.Contains(shader, "uniform sampler2D entityMirrorColor;");
        StringAssert.Contains(shader, "texture(entityMirrorColor, entityMirrorUv)");
        Assert.IsFalse(shader.Contains("tracePlanarLiquidScreenSample", StringComparison.Ordinal));
        string mirrorVertex = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "vintagertx",
            "shaders",
            "entitymirror.vert"));
        StringAssert.Contains(mirrorVertex, "int sourceX = gl_VertexID;");
        StringAssert.Contains(mirrorVertex, "int sourceY = gl_InstanceID;");
        Assert.IsFalse(mirrorVertex.Contains("gl_VertexID %", StringComparison.Ordinal));
        Assert.IsFalse(mirrorVertex.Contains("gl_VertexID /", StringComparison.Ordinal));
        StringAssert.Contains(mirrorVertex, "entityDistanceSquared > maximumDistanceSquared");
        StringAssert.Contains(
            mirrorVertex,
            "separatedEntityDistance * separatedEntityDistance < terrainDistanceSquared");
        StringAssert.Contains(mirrorVertex, "sourceWorldPosition.y <= surfaceWorldY + 0.015");
        StringAssert.Contains(mirrorVertex, "2.0 * surfaceWorldY - sourceWorldPosition.y");

        Vector3 cameraPosition = new(0.0f, 15.5f, 0.0f);
        Vector3 cameraTarget = new(0.0f, 12.5f, 18.0f);
        Vector3 cameraForward = Vector3.Normalize(cameraTarget - cameraPosition);
        Vector3 cameraRight = Vector3.Normalize(Vector3.Cross(
            cameraForward,
            Vector3.UnitY));
        Vector3 cameraUp = Vector3.Normalize(Vector3.Cross(
            cameraRight,
            cameraForward));
        float aspect = width / (float)height;
        float tangent = MathF.Tan(MathF.PI / 6.0f);

        Dictionary<int, (string Kind, Vector3 WorldPosition)> cleanReflectionSource = [];
        Dictionary<int, (string Kind, Vector3 WorldPosition)> directSource = [];
        Dictionary<string, HashSet<int>> analyticMirrors = new(StringComparer.Ordinal)
        {
            [item] = [],
            [humanoid] = [],
            [submerged] = [],
            [firstPerson] = []
        };
        Dictionary<string, HashSet<int>> maskedMirrors = new(StringComparer.Ordinal)
        {
            [item] = [],
            [humanoid] = [],
            [submerged] = [],
            [firstPerson] = []
        };
        HashSet<int> solidHumanoidEnvelope = [];

        int? Project(Vector3 worldPosition)
        {
            Vector3 relative = worldPosition - cameraPosition;
            float viewDepth = Vector3.Dot(relative, cameraForward);
            if (viewDepth <= 0.0001f)
            {
                return null;
            }

            float ndcX = Vector3.Dot(relative, cameraRight)
                / (viewDepth * tangent * aspect);
            float ndcY = Vector3.Dot(relative, cameraUp)
                / (viewDepth * tangent);
            int pixelX = (int)MathF.Floor((ndcX * 0.5f + 0.5f) * width);
            int pixelY = (int)MathF.Floor((ndcY * 0.5f + 0.5f) * height);
            return pixelX >= 0 && pixelY >= 0 && pixelX < width && pixelY < height
                ? pixelY * width + pixelX
                : null;
        }

        Vector3 Mirror(Vector3 worldPosition, float planeWorldY) => new(
            worldPosition.X,
            2.0f * planeWorldY - worldPosition.Y,
            worldPosition.Z);

        void AddWorldSilhouette(
            string kind,
            float minimumX,
            float maximumX,
            float minimumY,
            float maximumY,
            float worldZ,
            Func<float, float, bool> alphaTest,
            bool recordSolidEnvelope = false)
        {
            const float sampleStep = 0.025f;
            for (float worldY = minimumY; worldY <= maximumY; worldY += sampleStep)
            {
                for (float worldX = minimumX; worldX <= maximumX; worldX += sampleStep)
                {
                    Vector3 worldPosition = new(worldX, worldY, worldZ);
                    int? reflectedPixel = Project(Mirror(worldPosition, surfaceWorldY));
                    if (recordSolidEnvelope && reflectedPixel.HasValue)
                    {
                        solidHumanoidEnvelope.Add(reflectedPixel.Value);
                    }

                    if (!alphaTest(worldX, worldY))
                    {
                        continue;
                    }

                    int? sourcePixel = Project(worldPosition);
                    if (!sourcePixel.HasValue)
                    {
                        continue;
                    }

                    cleanReflectionSource[sourcePixel.Value] = (kind, worldPosition);
                    directSource[sourcePixel.Value] = (kind, worldPosition);
                }
            }
        }

        AddWorldSilhouette(
            item,
            -2.42f,
            -1.78f,
            12.68f,
            13.32f,
            17.0f,
            static (worldX, worldY) =>
            {
                float localX = (worldX + 2.10f) / 0.32f;
                float localY = (worldY - 13.00f) / 0.32f;
                return localX * localX + localY * localY <= 1.0f;
            });

        AddWorldSilhouette(
            humanoid,
            0.78f,
            2.22f,
            12.42f,
            14.92f,
            18.0f,
            static (worldX, worldY) =>
            {
                float localX = worldX - 1.50f;
                float localY = worldY - 12.42f;
                bool head = localX * localX / (0.34f * 0.34f)
                    + (localY - 2.17f) * (localY - 2.17f) / (0.34f * 0.34f)
                    <= 1.0f;
                bool torso = MathF.Abs(localX) <= 0.43f
                    && localY is >= 0.78f and <= 1.88f;
                bool arms = MathF.Abs(localX) <= 0.69f
                    && localY is >= 1.02f and <= 1.66f;
                bool leftLeg = localX is >= -0.40f and <= -0.10f
                    && localY is >= 0.0f and <= 0.82f;
                bool rightLeg = localX is >= 0.10f and <= 0.40f
                    && localY is >= 0.0f and <= 0.82f;
                bool torsoAlphaHole = localX * localX
                    + (localY - 1.34f) * (localY - 1.34f)
                    < 0.15f * 0.15f;
                return (head || torso || arms || leftLeg || rightLeg)
                    && !torsoAlphaHole;
            },
            recordSolidEnvelope: true);

        AddWorldSilhouette(
            submerged,
            -0.60f,
            0.30f,
            11.35f,
            12.05f,
            16.5f,
            static (worldX, worldY) =>
            {
                float localX = (worldX + 0.15f) / 0.45f;
                float localY = (worldY - 11.70f) / 0.35f;
                return localX * localX + localY * localY <= 1.0f;
            });

        // This rectangle deliberately exists only in the live/direct buffers. It models the
        // late first-person arm/held-item draw without inventing clean world geometry behind it.
        for (int pixelY = 18; pixelY < 104; pixelY++)
        {
            for (int pixelX = 508; pixelX < 640; pixelX++)
            {
                directSource[pixelY * width + pixelX] = (
                    firstPerson,
                    new Vector3(1.0f));
            }
        }

        foreach (KeyValuePair<int, (string Kind, Vector3 WorldPosition)> entry
            in cleanReflectionSource)
        {
            string kind = entry.Value.Kind;
            Vector3 worldPosition = entry.Value.WorldPosition;
            int? reflectedPixel = Project(Mirror(worldPosition, surfaceWorldY));
            if (!reflectedPixel.HasValue)
            {
                continue;
            }

            analyticMirrors[kind].Add(reflectedPixel.Value);
            bool sourceAboveInterface = worldPosition.Y >= surfaceWorldY - 0.02f;
            if (sourceAboveInterface)
            {
                maskedMirrors[kind].Add(reflectedPixel.Value);
            }
        }

        (double X, double Y) Centroid(HashSet<int> mask)
        {
            Assert.IsTrue(mask.Count > 0, "A reflected silhouette cannot have an empty centroid.");
            double sumX = 0.0;
            double sumY = 0.0;
            foreach (int pixel in mask)
            {
                sumX += pixel % width + 0.5;
                sumY += pixel / width + 0.5;
            }
            return (sumX / mask.Count, sumY / mask.Count);
        }

        void AssertMatchesAnalyticProjection(string kind, int minimumArea)
        {
            HashSet<int> expected = analyticMirrors[kind];
            HashSet<int> actual = maskedMirrors[kind];
            Assert.IsTrue(expected.Count >= minimumArea, $"{kind} analytic area is under-sampled.");
            Assert.AreEqual(expected.Count, actual.Count, $"{kind} reflected area drifted.");
            (double expectedX, double expectedY) = Centroid(expected);
            (double actualX, double actualY) = Centroid(actual);
            Assert.AreEqual(expectedX, actualX, 0.01, $"{kind} reflected X centroid drifted.");
            Assert.AreEqual(expectedY, actualY, 0.01, $"{kind} reflected Y centroid drifted.");
        }

        AssertMatchesAnalyticProjection(item, minimumArea: 40);
        AssertMatchesAnalyticProjection(humanoid, minimumArea: 240);
        Assert.IsTrue(
            analyticMirrors[humanoid].Count < solidHumanoidEnvelope.Count * 0.78,
            "The humanoid alpha gaps were filled by the projection.");
        Assert.IsTrue(
            analyticMirrors[humanoid].Count > solidHumanoidEnvelope.Count * 0.25,
            "The humanoid alpha-tested silhouette became implausibly sparse.");

        Assert.IsTrue(
            analyticMirrors[submerged].Count >= 40,
            "The negative case must contain visible clean-source geometry before masking.");
        Assert.AreEqual(
            0,
            maskedMirrors[submerged].Count,
            "Submerged geometry belongs to transmission/refraction, not incident reflection.");
        Assert.IsTrue(directSource.Values.Any(sample => sample.Kind == firstPerson));
        Assert.IsFalse(cleanReflectionSource.Values.Any(sample => sample.Kind == firstPerson));
        Assert.AreEqual(
            0,
            maskedMirrors[firstPerson].Count,
            "Direct-only first-person geometry leaked into the clean reflection carrier.");

        HashSet<int> oldIntegerPlaneHumanoid = [];
        const float oldIntegerSurfaceWorldY = blockWorldY + 1.0f;
        foreach (KeyValuePair<int, (string Kind, Vector3 WorldPosition)> entry
            in cleanReflectionSource)
        {
            string kind = entry.Value.Kind;
            Vector3 worldPosition = entry.Value.WorldPosition;
            if (kind != humanoid)
            {
                continue;
            }

            int? reflectedPixel = Project(Mirror(
                worldPosition,
                oldIntegerSurfaceWorldY));
            if (reflectedPixel.HasValue)
            {
                oldIntegerPlaneHumanoid.Add(reflectedPixel.Value);
            }
        }

        (double physicalX, double physicalY) = Centroid(maskedMirrors[humanoid]);
        (double oldX, double oldY) = Centroid(oldIntegerPlaneHumanoid);
        double oldPlaneCentroidError = Math.Sqrt(
            (physicalX - oldX) * (physicalX - oldX)
            + (physicalY - oldY) * (physicalY - oldY));
        Assert.IsTrue(
            oldPlaneCentroidError >= 8.0,
            $"The fixture no longer distinguishes LiquidLevel/8 from the old integer plane ({oldPlaneCentroidError:0.00}px).");
    }
}
