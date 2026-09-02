using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>
/// Guards the semantic evidence emitted by the close-range shadow laboratory.
/// These assertions distinguish authored partial geometry from a cube fallback
/// and require a complete, glass-transmitting lantern caster.
/// </summary>
[TestClass]
public sealed class ShadowGeometryScenarioTests
{
    /// <summary>
    /// Verifies that lower plate, uprights and roof are all present while
    /// transparent faces leave empty fine-caster samples.
    /// </summary>
    [TestMethod]
    public void LanternEvidenceRequiresBaseCompleteCageAndIgnoredGlass()
    {
        byte[] cage = new byte[VoxelScene.LightCasterVoxelCount];
        FillHorizontalPlate(cage, y: 1);
        FillVerticalUprights(cage);
        FillHorizontalPlate(cage, y: VoxelScene.LightCasterScale - 2);
        CachedBlockOccupancy occupancy = new(
            Mask: 0x0000_0660_0660_0000UL,
            HasDetailedMesh: true,
            OpaqueTriangles: 114,
            TransparentTriangles: 8,
            AlphaTestedTriangles: 8,
            GeometryKind: BlockGeometryKind.DynamicInstance);

        CasterGeometryEvidence evidence = VoxelScene.MeasureCasterGeometry(
            occupancy,
            isCrossedPlane: false,
            cage);

        Assert.IsTrue(evidence.DetailedNonCube);
        Assert.IsTrue(evidence.BaseIncluded);
        Assert.IsTrue(evidence.CageHeightCovered);
        Assert.IsTrue(evidence.TransparentSurfaceIgnored);
        Assert.IsTrue(evidence.FineBands.LowerOccupied > 0);
        Assert.IsTrue(evidence.FineBands.MiddleOccupied > 0);
        Assert.IsTrue(evidence.FineBands.UpperOccupied > 0);
        Assert.IsTrue(evidence.FineBands.Empty > 0);
    }

    /// <summary>
    /// Verifies that missing lower geometry, missing glass provenance and a
    /// truncated fine buffer cannot produce a passing cage verdict.
    /// </summary>
    [TestMethod]
    public void LanternEvidenceRejectsIncompleteOrUnprovenCasterData()
    {
        CachedBlockOccupancy occupancy = new(
            Mask: 1,
            HasDetailedMesh: true,
            OpaqueTriangles: 2,
            TransparentTriangles: 0,
            AlphaTestedTriangles: 0,
            GeometryKind: BlockGeometryKind.StaticComplex);
        byte[] upperOnly = new byte[VoxelScene.LightCasterVoxelCount];
        int upperIndex = (8 * VoxelScene.LightCasterScale
            + VoxelScene.LightCasterScale - 1) * VoxelScene.LightCasterScale + 8;
        upperOnly[upperIndex] = 255;

        CasterGeometryEvidence incomplete = VoxelScene.MeasureCasterGeometry(
            occupancy,
            isCrossedPlane: false,
            upperOnly);
        CasterGeometryEvidence truncated = VoxelScene.MeasureCasterGeometry(
            occupancy,
            isCrossedPlane: false,
            [255]);

        Assert.IsFalse(incomplete.BaseIncluded);
        Assert.IsFalse(incomplete.CageHeightCovered);
        Assert.IsFalse(incomplete.TransparentSurfaceIgnored);
        Assert.IsFalse(truncated.BaseIncluded);
        Assert.IsFalse(truncated.CageHeightCovered);
        Assert.IsFalse(truncated.TransparentSurfaceIgnored);
    }

    /// <summary>
    /// Verifies that crossed vegetation and anvil-like authored masks are
    /// classified as partial detailed geometry rather than full cubes.
    /// </summary>
    [TestMethod]
    public void CrossedPlanesAndAnvilRemainDetailedPartialGeometry()
    {
        CachedBlockOccupancy grass = new(
            Mask: 0x9009_6006_6006_9009UL,
            HasDetailedMesh: true,
            OpaqueTriangles: 4,
            TransparentTriangles: 0,
            AlphaTestedTriangles: 4,
            GeometryKind: BlockGeometryKind.StaticComplex);
        CachedBlockOccupancy anvil = new(
            Mask: 0x0ff0_0660_0ff0_0660UL,
            HasDetailedMesh: true,
            OpaqueTriangles: 172,
            TransparentTriangles: 0,
            AlphaTestedTriangles: 0,
            GeometryKind: BlockGeometryKind.DynamicInstance);
        CachedBlockOccupancy uncutGrass = grass with { AlphaTestedTriangles = 0 };

        CasterGeometryEvidence grassEvidence = VoxelScene.MeasureCasterGeometry(
            grass,
            isCrossedPlane: true,
            null);
        CasterGeometryEvidence anvilEvidence = VoxelScene.MeasureCasterGeometry(
            anvil,
            isCrossedPlane: false,
            null);
        CasterGeometryEvidence uncutGrassEvidence = VoxelScene.MeasureCasterGeometry(
            uncutGrass,
            isCrossedPlane: true,
            null);

        Assert.AreEqual(4, grass.AlphaTestedTriangles);
        Assert.IsTrue(grassEvidence.DetailedNonCube);
        Assert.IsTrue(grassEvidence.CrossedPlanePartial);
        Assert.IsFalse(
            uncutGrassEvidence.CrossedPlanePartial,
            "Crossed planes without texture-alpha evidence are still oversized opaque rectangles.");
        Assert.IsTrue(anvilEvidence.DetailedNonCube);
        Assert.IsFalse(anvilEvidence.CrossedPlanePartial);
        Assert.IsTrue(grassEvidence.CoarseOccupied is > 0 and < 64);
        Assert.IsTrue(anvilEvidence.CoarseOccupied is > 0 and < 64);
    }

    /// <summary>
    /// Verifies that the material ABI can identify a partial solid without changing its broad
    /// transmissive, dielectric, or conductor class used by the lighting shader.
    /// </summary>
    [TestMethod]
    public void PartialGeometryFlagPreservesEveryMaterialClass()
    {
        Assert.AreEqual((byte)68, VoxelScene.EncodeMaterialGeometryClass(64, true));
        Assert.AreEqual((byte)132, VoxelScene.EncodeMaterialGeometryClass(128, true));
        Assert.AreEqual((byte)196, VoxelScene.EncodeMaterialGeometryClass(192, true));
        Assert.AreEqual((byte)128, VoxelScene.EncodeMaterialGeometryClass(128, false));
        Assert.AreEqual(
            0,
            VoxelScene.EncodeMaterialGeometryClass(192, true) & 3,
            "Geometry metadata must not occupy the low material-reserved bits.");
    }

    /// <summary>
    /// Verifies that both render-laboratory campaigns reject captures lacking
    /// the authored cage, anvil, crossed-plane, multi-light or color-target evidence.
    /// </summary>
    [TestMethod]
    public void RenderLabCampaignRequiresEverySemanticGeometryToken()
    {
        string[] requiredEvidence =
        [
            "Render lab camera applied",
            "Scenario render-lab injected 1 moving point light",
            "Render lab light rig verified: sources=3",
            "Render lab reflection targets verified: count=3, emissive=none",
            "Lantern cage evidence game:lantern-large-up: verdict=PASS",
            "Geometry evidence game:anvil-iron: kind=DynamicInstance, detailed non-cube=True",
            "Geometry evidence game:tallgrass-tall-free: kind=StaticComplex, detailed non-cube=True, crossed planes=True"
        ];

        foreach (string scenarioName in new[] { "render-lab", "render-lab-performance" })
        {
            ScenarioDefinition scenario = ScenarioCatalog.Get(scenarioName);
            foreach (string token in requiredEvidence)
            {
                CollectionAssert.Contains(scenario.RequiredLogTokens, token, scenarioName);
            }
        }
    }

    /// <summary>Fills one inset opaque plate at the requested fine-grid height.</summary>
    /// <param name="mask">Fine caster receiving the plate.</param>
    /// <param name="y">Fine-grid vertical coordinate.</param>
    private static void FillHorizontalPlate(byte[] mask, int y)
    {
        for (int z = 2; z < VoxelScene.LightCasterScale - 2; z++)
        {
            for (int x = 2; x < VoxelScene.LightCasterScale - 2; x++)
            {
                mask[(z * VoxelScene.LightCasterScale + y) * VoxelScene.LightCasterScale + x] = 255;
            }
        }
    }

    /// <summary>Fills four narrow uprights while leaving the glass volume empty.</summary>
    /// <param name="mask">Fine caster receiving the uprights.</param>
    private static void FillVerticalUprights(byte[] mask)
    {
        int maximum = VoxelScene.LightCasterScale - 3;
        for (int y = 2; y < VoxelScene.LightCasterScale - 2; y++)
        {
            foreach ((int X, int Z) corner in new[]
            {
                (2, 2),
                (2, maximum),
                (maximum, 2),
                (maximum, maximum)
            })
            {
                mask[(corner.Z * VoxelScene.LightCasterScale + y)
                    * VoxelScene.LightCasterScale + corner.X] = 255;
            }
        }
    }
}
