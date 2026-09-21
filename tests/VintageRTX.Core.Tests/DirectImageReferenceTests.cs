using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Diagnostics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class DirectImageReferenceTests
{
    private static CellScene Scene(CellId anchor = default)
    {
        var scene = new CellScene(new(Guid.NewGuid(), 0));
        for (int z = 0; z < 8; z++) for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            scene.Observe(new(anchor.X + x, anchor.Y + y, anchor.Z + z), CellGeometry.Empty);
        return scene;
    }
    private static SurfaceReceiver Receiver(int material = 0) => new(new(1.25, 2.5, 3.75), new(0,0,1), new(0,0,1), Vector3.One, material);
    private static DirectSurfaceFrame Surfaces(CellScene scene, SurfaceMaterial material) =>
        new(scene.Capture(), default, new(1.25,2.5,7), 1, 1, new[] { Receiver() }, new[] { material });
    private static LightFrame Light(DirectSurfaceFrame surfaces, double radius = 0)
    {
        var registry = new LightRegistry(surfaces.World);
        registry.Upsert(default, new(new(1.25,2.5,5.75), new(8,4,2), EmissionProfile.Steady, radius: radius));
        return registry.Capture(1, 0);
    }
    [TestMethod]
    public void SurfacePacketIsDetachedUnlitAndKeepsAbsentPixelsAbsent()
    {
        CellScene scene = Scene(); SurfaceReceiver[] source = [Receiver(), default];
        SurfaceMaterial[] materials = [new(SurfaceKind.Diffuse, new(.5f,.25f,.125f))];
        var surfaces = new DirectSurfaceFrame(scene.Capture(), default, new(1,2,7), 2, 1, source, materials);
        source[0] = default; materials[0] = new(SurfaceKind.Conductor, Vector3.One, .4, Vector3.One, Vector3.One);
        Assert.IsTrue(surfaces.Receivers[0].Present);
        CollectionAssert.AreEqual(new float[] {.5f,.25f,.125f,1}, surfaces.Colors[..4].ToArray());
        Assert.IsTrue(surfaces.Positions[4..].ToArray().All(x => x == 0));
        Assert.AreEqual(SurfaceKind.Diffuse, surfaces.Material(0).Kind);
        Assert.AreSame(scene.Capture(), surfaces.Scene);
    }
    [TestMethod]
    public void LargeCoordinatesAreSubtractedInDoubleBeforePacking()
    {
        CellId anchor = new(10_000_000,-4_000_000,8_000_000); var origin = Receiver();
        SurfaceReceiver shifted = origin with { Position = origin.Position + anchor.Position };
        var material = new SurfaceMaterial(SurfaceKind.Diffuse, Vector3.One);
        var local = new DirectSurfaceFrame(Scene().Capture(), default, new(1.25,2.5,7), 1, 1, new[] {origin}, new[] {material});
        var far = new DirectSurfaceFrame(Scene(anchor).Capture(), anchor, new DVec3(1.25,2.5,7) + anchor.Position,
            1, 1, new[] {shifted}, new[] {material});
        CollectionAssert.AreEqual(local.Positions.ToArray(), far.Positions.ToArray());
        CollectionAssert.AreEqual(local.Normals.ToArray(), far.Normals.ToArray());
    }
    [DataTestMethod]
    [DataRow(0.0)] [DataRow(0.01)] [DataRow(0.5)] [DataRow(1.9)]
    public void AlignedDiffuseMatchesProjectedSolidAngleWithoutChangingPower(double radius)
    {
        var surfaces = Surfaces(Scene(), new(SurfaceKind.Diffuse, new(.5f,.7f,.9f)));
        DirectLightingResult actual = DirectLightingReference.Evaluate(surfaces, 0, Light(surfaces, radius), 64);
        Vector3 expected = new Vector3(8,4,2) * new Vector3(.5f,.7f,.9f) / (4 * MathF.PI);
        Assert.IsTrue(Vector3.Distance(actual.Radiance, expected) < 2e-6f, $"{actual.Radiance} != {expected}");
        Assert.AreEqual(0, actual.Unresolved); Assert.AreEqual(0, actual.Blocked);
        Assert.AreEqual(radius > 0 ? 64 : 1, actual.Traced);
    }
    [TestMethod]
    public void ConductorUsesComplexFresnelNotDiffuseColorAndPreservesHdr()
    {
        Vector3 eta = new(.2f,.85f,1.2f), k = new(3.2f,2.8f,2.5f);
        var material = new SurfaceMaterial(SurfaceKind.Conductor, Vector3.Zero, .2, eta, k);
        var surfaces = Surfaces(Scene(), material);
        var result = DirectLightingReference.Evaluate(surfaces, 0, Light(surfaces));
        Vector3 numerator = (eta - Vector3.One) * (eta - Vector3.One) + k * k;
        Vector3 denominator = (eta + Vector3.One) * (eta + Vector3.One) + k * k;
        Vector3 expected = numerator / denominator * new Vector3(8,4,2) / (float)(16 * Math.PI * Math.Pow(.2, 4));
        Assert.IsTrue(Vector3.Distance(expected, result.Radiance) < .001f);
        Assert.IsTrue(result.Radiance.X > 1 && result.Radiance.Y > 1);
    }
    [TestMethod]
    public void BlockedUnknownUnsupportedAndBudgetAreNotTheSameAsClear()
    {
        CellScene scene = Scene();
        var material = new SurfaceMaterial(SurfaceKind.Diffuse, Vector3.One);
        var plane = new BlockMesh(new Triangle[] { new(new(0,0,.5),new(1,0,.5),new(1,1,.5),0),
            new(new(0,0,.5),new(1,1,.5),new(0,1,.5),0) });
        foreach (CellGeometry geometry in new[] {CellGeometry.FromMesh(plane), CellGeometry.Unknown, CellGeometry.Unsupported})
        {
            scene.Observe(new(1,2,4), geometry); DirectSurfaceFrame surfaces = Surfaces(scene, material);
            DirectLightingResult result = DirectLightingReference.Evaluate(surfaces, 0, Light(surfaces));
            Assert.AreEqual(Vector3.Zero, result.Radiance);
            Assert.AreEqual(geometry.State == CellState.Mesh ? 1 : 0, result.Blocked);
            Assert.AreEqual(geometry.State == CellState.Mesh ? 0 : 1, result.Unresolved);
        }
        scene.Observe(new(1,2,4), CellGeometry.Empty); var clear = Surfaces(scene, material);
        var exhausted = DirectLightingReference.Evaluate(clear, 0, Light(clear), maximumCells: 1);
        Assert.AreEqual(Vector3.Zero, exhausted.Radiance); Assert.AreEqual(1, exhausted.Unresolved);
    }
    [TestMethod]
    public void NoLightMeansNoStoredHistoryEnergyAndNoInventedAmbient()
    {
        var surfaces = Surfaces(Scene(), new(SurfaceKind.Diffuse, Vector3.One));
        var registry = new LightRegistry(surfaces.World);
        registry.Upsert(default, new(new(1.25,2.5,5.75), Vector3.One, EmissionProfile.Candle));
        var first = DirectLightingReference.Evaluate(surfaces, 0, registry.Capture(1,1));
        Assert.IsTrue(first.Radiance.X > 0);
        registry.Remove(default);
        var off = DirectLightingReference.Evaluate(surfaces, 0, registry.Capture(2,1.01));
        Assert.AreEqual(Vector3.Zero, off.Radiance); Assert.AreEqual(0, off.Traced);
    }
    [TestMethod]
    public void IdealMirrorIsExplicitlyUnresolvedByTheDirectOnlyPass()
    {
        var surfaces = Surfaces(Scene(), new(SurfaceKind.Conductor, Vector3.One, 0, Vector3.One, Vector3.One));
        var result = DirectLightingReference.Evaluate(surfaces, 0, Light(surfaces));
        Assert.AreEqual(Vector3.Zero, result.Radiance); Assert.AreEqual(1, result.Unresolved); Assert.AreEqual(0, result.Traced);
    }
    [TestMethod]
    public void WrongWorldAndInvalidReceiverInputsAreRejected()
    {
        CellScene scene = Scene(); var material = new SurfaceMaterial(SurfaceKind.Diffuse, Vector3.One);
        var surfaces = Surfaces(scene, material);
        Assert.ThrowsException<ArgumentException>(() => DirectLightingReference.Evaluate(surfaces, 0, new LightRegistry(new(Guid.NewGuid(),0)).Capture(1,0)));
        foreach (SurfaceReceiver bad in new[] {Receiver(-1), Receiver() with {BaseColor = new(2,1,1)},
            Receiver() with {ShadingNormal = new(0,0,2)}, Receiver() with {Position = new(double.NaN,0,0)},
            Receiver() with {ShadingNormal = new(0,0,-1)}})
            Assert.ThrowsException<ArgumentException>(() => new DirectSurfaceFrame(scene.Capture(),default,new(0,0,7),1,1,new[]{bad},new[]{material}));
    }
    [TestMethod]
    public void LabUsesRealPrimaryMeshIntersectionsAndDistinctMaterials()
    {
        DirectSurfaceFrame lab = DirectLightLab.Create(32,24);
        Assert.AreEqual(1, lab.Scene.RegionCount);
        Assert.IsTrue(lab.Receivers.ToArray().All(r => r.Present));
        Assert.AreEqual(5, lab.Receivers.ToArray().Select(r => r.MaterialIndex).Distinct().Count());
        Assert.IsTrue(lab.Receivers.ToArray().Any(r => r.ShadingNormal != r.GeometricNormal));
        var registry = new LightRegistry(lab.World);
        DirectLightLab.SetLights(registry,lab.Anchor,new(null,null,EmissionProfile.Candle,ComponentCount:3),true);
        var light = registry.Capture(1,3.25); int blocked = 0; double total = 0;
        for(int i=0;i<lab.Receivers.Length;i++)
        {
            var result=DirectLightingReference.Evaluate(lab,i,light);
            Assert.AreEqual(0,result.Unresolved);blocked+=result.Blocked;total+=result.Radiance.Length();
        }
        Assert.IsTrue(blocked>0); Assert.IsTrue(total>0);
    }
}
