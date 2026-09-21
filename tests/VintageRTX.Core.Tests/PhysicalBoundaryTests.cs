using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Diagnostics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Scheduling;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class PhysicalBoundaryTests
{
    private static readonly DVec3 N = new(0, 0, 1);
    private static readonly DVec3 Bad = new(double.NaN, 0, 0);
    private static readonly SurfaceMaterial Matte = new(SurfaceKind.Diffuse, Vector3.One);
    private static LightSample Lamp(DVec3 p, double radius = 0) => new(default, p, Vector3.One, radius);

    [TestMethod]
    public void TemporalProfilesRejectEveryInvalidPhysicalParameter()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionProfile((EmissionKind)99));
        foreach (double value in new[] {double.NaN, -1, .81})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionProfile(EmissionKind.Flame, amplitude: value));
        foreach (double value in new[] {double.NaN, 0, 101})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionProfile(EmissionKind.Flame, frequencyHz: value));
        foreach (double value in new[] {double.NaN, -1, 1.01})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionProfile(EmissionKind.Flame, windSensitivity: value));
        foreach (double value in new[] {double.NaN, 0, 61})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new EmissionProfile(EmissionKind.Flame, durationSeconds: value));
        Assert.ThrowsException<ArgumentNullException>(() => EmissionWaveform.Evaluate(null!, 0, 0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => EmissionWaveform.Evaluate(EmissionProfile.Fire, 0, 0, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => EmissionWaveform.Evaluate(EmissionProfile.Fire, 0, 0, 0, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => EmissionWaveform.Evaluate(EmissionProfile.Fire, 0, 1e13, 0));
        Assert.AreSame(EmissionProfile.Steady, EmissionProfiles.ForCode(EmissionCatalog.Empty, "test:lamp"));
        Assert.AreSame(EmissionProfile.Engine, EmissionProfiles.ForCode(EmissionCatalog.Empty, "test:animal", true));
        Assert.AreEqual(0f, ColorSpace.Encode(0)); Assert.AreEqual(1f, ColorSpace.Encode(1), 1e-6f);
        foreach (int count in new[] {0, 65})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => EmissionGroupWaveform.Evaluate(EmissionProfile.Candle, 0, 0, 0, count));
        foreach (int ordinal in new[] {-1, 64})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => EmissionGroupWaveform.ComponentSeed(0, ordinal));
    }

    [TestMethod]
    public void InvalidOpticalMediaAndReflectanceCannotEnterTheTransport()
    {
        foreach (SurfaceKind kind in new[] {(SurfaceKind)(-1), (SurfaceKind)99})
            Assert.ThrowsException<ArgumentException>(() => new SurfaceMaterial(kind, Vector3.One));
        foreach (Vector3 c in new[] {new Vector3(float.NaN, 0, 0), new(1.1f, 1, 1), new(1, 1.1f, 1), new(1, 1, 1.1f)})
            Assert.ThrowsException<ArgumentException>(() => new SurfaceMaterial(SurfaceKind.Diffuse, c));
        foreach (double r in new[] {double.NaN, -.1, 1.1})
            Assert.ThrowsException<ArgumentException>(() => new SurfaceMaterial(SurfaceKind.Diffuse, Vector3.One, r));
        foreach (Vector3 eta in new[] {new Vector3(float.NaN), new(0, 1, 1), new(1, 0, 1), new(1, 1, 0)})
            Assert.ThrowsException<ArgumentException>(() => new SurfaceMaterial(SurfaceKind.Conductor, Vector3.One, eta: eta));
        Assert.ThrowsException<ArgumentException>(() => new SurfaceMaterial(SurfaceKind.Conductor, Vector3.One, eta: Vector3.One, k: new(-1, 0, 0)));
        foreach ((double a, double b) in new[] {(double.NaN, 1d), (1d, double.NaN), (0d, 1d), (1d, 0d), (double.MaxValue, double.Epsilon), (double.Epsilon, double.MaxValue)})
        {
            Assert.ThrowsException<ArgumentException>(() => Bsdf.DielectricFresnel(.5, a, b));
            Assert.ThrowsException<ArgumentException>(() => OpticalInterface.Evaluate(-N, N, a, b));
        }
        Assert.ThrowsException<ArgumentException>(() => Bsdf.DielectricFresnel(double.NaN, 1, 1.5));
        Assert.ThrowsException<ArgumentException>(() => Bsdf.ConductorFresnel(.5, 1, double.NaN));
        Assert.ThrowsException<ArgumentException>(() => Bsdf.ConductorFresnel(.5, 1, -1));
        Assert.AreEqual(1d, Bsdf.ConductorFresnel(0, 1, 1));
        Assert.ThrowsException<ArgumentException>(() => OpticalInterface.ReflectPoint(Bad, default, N));
        Assert.ThrowsException<ArgumentException>(() => OpticalInterface.ReflectPoint(default, Bad, N));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => OpticalInterface.Transmittance(Vector3.One, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => OpticalInterface.Transmittance(Vector3.One, -1));
        Assert.AreEqual(0d, Bsdf.Distribution(0, .5));
        foreach ((double u, double v) in new[] {(-1d, .5), (1d, .5), (.5, -1d), (.5, 1d), (double.NaN, .5)})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => Bsdf.Sample(Matte, N, N, u, v, out _, out _));
        Assert.IsFalse(Bsdf.Sample(Matte, N, -N, .5, .5, out _, out _));
        Assert.IsTrue(Bsdf.Sample(Matte, new(0, 1, 0), new(0, 1, 0), .5, .5, out DVec3 sampled, out Vector3 weight));
        Assert.IsTrue(sampled.Y > 0); Assert.IsTrue(Vector3.Distance(weight, Vector3.One) < 1e-6);
    }

    [TestMethod]
    public void DegenerateGeometryAndActiveBufferTailsAreRejectedAtTheirBoundary()
    {
        Assert.ThrowsException<IndexOutOfRangeException>(() => _ = N[3]);
        Assert.ThrowsException<ArgumentException>(() => Bad.Normalized());
        Assert.ThrowsException<ArgumentException>(() => DVec3.Zero.Normalized());
        foreach (Action invalid in new Action[] {
            () => new Ray(Bad, N), () => new Ray(default, Bad), () => new Ray(default, default),
            () => new Ray(default, N, double.NaN), () => new Ray(default, N, -1),
            () => new Ray(default, N, 0, double.NaN), () => new Ray(default, N, 1, 1),
            () => new Triangle(Bad, N, new(1, 0, 0), 0), () => new Triangle(N, Bad, new(1, 0, 0), 0),
            () => new Triangle(N, new(1, 0, 0), Bad, 0), () => new Triangle(default, N, new(1, 0, 0), -1),
            () => new Triangle(default, N, N, 0) })
            Assert.ThrowsException<ArgumentException>(invalid);
        var t = new Triangle(default, new(1, 0, 0), new(0, 1, 0), 0);
        Assert.IsFalse(t.Intersect(new(new(.1, .1, 1), new(1, 0, 0)), 10, 0, out _));
        Assert.ThrowsException<ArgumentException>(() => new BlockMesh([]));
        Assert.ThrowsException<ArgumentException>(() => new BlockMesh(new Triangle[1]));
        Assert.ThrowsException<ArgumentException>(() => new BlockMesh(Enumerable.Repeat(t, 4097).ToArray()));
        foreach (DVec3 outside in new[] {new DVec3(-.1, 0, 0), new(0, -.1, 0), new(0, 0, -.1), new(1.1, 0, 0), new(0, 1.1, 0), new(0, 0, 1.1)})
            Assert.ThrowsException<ArgumentException>(() => new BlockMesh(new[] {new Triangle(outside, new(.2, .8, .4), new(.8, .2, .6), 0)}));
        float[] xyz = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        foreach ((int vc, int ic) in new[] {(0, 3), (4, 3), (3, 0), (3, 4), (3, 2)})
            Assert.ThrowsException<ArgumentException>(() => BlockMesh.CopyIndexed(xyz, vc, [0, 1, 2], ic));
        foreach (int[] ids in new[] {new[] {-1, 1, 2}, new[] {0, 3, 2}, new[] {0, 1, 3}})
            Assert.ThrowsException<ArgumentException>(() => BlockMesh.CopyIndexed(xyz, 3, ids, 3));
        Assert.ThrowsException<ArgumentException>(() => BlockMesh.CopyIndexed(xyz, 3, new int[4097 * 3], 4097 * 3));
        var empty = new TriangleBvh([]); Assert.AreEqual(0, empty.TriangleCount);
        Assert.IsFalse(empty.Trace(new(default, N), out _));
    }

    [TestMethod]
    public void FiniteEmitterRejectsNonfiniteGeometryAndDoesNotOverflowRadianceSilently()
    {
        LightSample valid = Lamp(new(0, 2, 0), .1);
        foreach (LightSample invalid in new[] {valid with {Position = Bad}, valid with {Intensity = new(float.NaN)}, valid with {Radius = double.NaN}, valid with {Radius = -.1}})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => FiniteEmitterSampling.TrySample(invalid, default, .5, .5, out _));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => FiniteEmitterSampling.TrySample(valid, Bad, .5, .5, out _));
        Assert.IsFalse(FiniteEmitterSampling.TrySample(valid with {Position = new(double.MaxValue, 0, 0)}, default, .5, .5, out _));
        Assert.IsTrue(FiniteEmitterSampling.TrySample(valid, default, .3, .7, out EmitterDirectionSample sample));
        Assert.IsTrue(sample.Direction.Y > 0); Assert.IsTrue(sample.Distance < 2);
        Assert.ThrowsException<OverflowException>(() => FiniteEmitterSampling.TrySample(Lamp(new(0, 0, 1e-22)), default, .5, .5, out _));
    }

    [TestMethod]
    public void ReceiverValidationCoversEveryPayloadFieldBeforeAllocation()
    {
        var scene = new CellScene(new(Guid.NewGuid(), 0)); var snapshot = scene.Capture();
        SurfaceReceiver valid = new(default, N, N, Vector3.One, 0);
        foreach ((int w, int h) in new[] {(0, 1), (1, 0), (8193, 1), (1, 8193), (8192, 8192), (2, 1)})
            Assert.ThrowsException<ArgumentException>(() => new DirectSurfaceFrame(snapshot, default, N, w, h, new[] {valid}, new[] {Matte}));
        Assert.ThrowsException<ArgumentException>(() => new DirectSurfaceFrame(snapshot, default, Bad, 1, 1, new[] {valid}, new[] {Matte}));
        Assert.ThrowsException<ArgumentException>(() => new DirectSurfaceFrame(snapshot, default, N, 1, 1, new[] {valid}, Array.Empty<SurfaceMaterial>()));
        Assert.ThrowsException<ArgumentException>(() => new DirectSurfaceFrame(snapshot, default, N, 1, 1, new[] {valid}, Enumerable.Repeat(Matte, 16385).ToArray()));
        Assert.ThrowsException<ArgumentNullException>(() => new DirectSurfaceFrame(snapshot, default, N, 1, 1, new[] {valid}, new SurfaceMaterial[] {null!}));
        foreach (SurfaceReceiver invalid in new[] {
            valid with {Position = Bad}, valid with {ShadingNormal = Bad}, valid with {GeometricNormal = Bad},
            valid with {ShadingNormal = new(0, 0, 2)}, valid with {GeometricNormal = -N},
            valid with {BaseColor = new(float.NaN)}, valid with {BaseColor = new(1.1f, 1, 1)},
            valid with {BaseColor = new(1, 1.1f, 1)}, valid with {BaseColor = new(1, 1, 1.1f)}, valid with {MaterialIndex = -1} })
            Assert.ThrowsException<ArgumentException>(() => DirectSurfaceFrame.ValidateReceiver(invalid, 1));
        DirectSurfaceFrame.ValidateReceiver(valid with {Present = false, Position = Bad}, 0);
        Assert.ThrowsException<ArgumentException>(() => DirectSurfaceFrame.Relative(Bad, default));
        Assert.ThrowsException<ArgumentException>(() => DirectSurfaceFrame.Relative(new(1_048_577, 0, 0), default));
        var frame = new DirectSurfaceFrame(snapshot, default, N, 1, 1, new[] {valid}, new[] {Matte});
        Assert.AreEqual(snapshot.Revision, frame.GeometryRevision);
    }

    [TestMethod]
    public void DirectTransportRejectsInvalidSamplingAndKeepsUnresolvedSeparateFromBlack()
    {
        var scene = new CellScene(new(Guid.NewGuid(), 0));
        for (int z = 0; z < 4; z++) for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) scene.Observe(new(x, y, z), CellGeometry.Empty);
        CellSceneFrame snapshot = scene.Capture(); SurfaceReceiver receiver = new(new(.5, .5, .5), N, N, Vector3.One, 0);
        DirectSurfaceFrame Surfaces(DVec3 camera, SurfaceReceiver r, SurfaceMaterial? material = null) => new(snapshot, default, camera, 1, 1, new[] {r}, new[] {material ?? Matte});
        var sf = Surfaces(new(.5, .5, 3), receiver);
        LightFrame Lights(params LightSample[] samples) => new(snapshot.World, 1, 0, default, samples);
        LightFrame lights = Lights(Lamp(new(.5, .5, 2)));
        foreach (int count in new[] {0, 65}) Assert.ThrowsException<ArgumentOutOfRangeException>(() => DirectLightingReference.Evaluate(sf, 0, lights, finiteSamples: count));
        foreach (double minimum in new[] {double.NaN, -.1}) Assert.ThrowsException<ArgumentOutOfRangeException>(() => DirectLightingReference.Evaluate(sf, 0, lights, rayMinimum: minimum));
        foreach (int count in new[] {0, 257}) Assert.ThrowsException<ArgumentOutOfRangeException>(() => DirectLightingReference.Evaluate(sf, 0, lights, maximumCells: count));
        foreach (Vector2 rotation in new[] {new Vector2(float.NaN, 0), new(0, float.NaN)})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => DirectLightingReference.Evaluate(sf, 0, lights, rotation: rotation));
        foreach ((int i, int n, Vector2 v) in new[] {(0, 0, Vector2.Zero), (-1, 1, Vector2.Zero), (1, 1, Vector2.Zero), (0, 1, new Vector2(float.NaN, 0)), (0, 1, new Vector2(0, float.NaN))})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => DirectLightingReference.Quadrature(i, n, v));
        Assert.AreEqual(default(DirectLightingResult), DirectLightingReference.Evaluate(Surfaces(N, receiver with {Present = false}), 0, lights));
        Assert.AreEqual(1, DirectLightingReference.Evaluate(Surfaces(receiver.Position, receiver), 0, lights).Unresolved);
        Assert.AreEqual(1, DirectLightingReference.Evaluate(Surfaces(default, receiver), 0, lights).Unresolved);
        SurfaceReceiver tilted = receiver with {ShadingNormal = new DVec3(1, 0, .1).Normalized()};
        Assert.AreEqual(1, DirectLightingReference.Evaluate(Surfaces(new(-1, .5, .6), tilted), 0, lights).Unresolved);
        Assert.AreEqual(0, DirectLightingReference.Evaluate(sf, 0, Lights(Lamp(new(.5, .5, 2)) with {Intensity = Vector3.Zero})).Traced);
        Assert.AreEqual(0, DirectLightingReference.Evaluate(Surfaces(new(.5, .5, 3), tilted), 0, Lights(Lamp(new(-1, .5, .6)))).Traced);
        Assert.AreEqual(8, DirectLightingReference.Evaluate(sf, 0, Lights(Lamp(new(.5, .5, 1), 1))).Unresolved);
        Assert.AreEqual(1, DirectLightingReference.Evaluate(sf, 0, lights, rayMinimum: 2).Unresolved);
        Assert.AreEqual(0, DirectLightingReference.Evaluate(Surfaces(new(.5, .5, 3), receiver with {BaseColor = Vector3.Zero}), 0, lights).Traced);
        var huge = Lamp(new(.5, .5, 1.5)) with {Intensity = new(float.MaxValue)};
        Assert.ThrowsException<ArithmeticException>(() => DirectLightingReference.Evaluate(sf, 0, Lights(Enumerable.Repeat(huge, 4).ToArray())));
    }

    [TestMethod]
    public void SceneAndSchedulerOwnerBoundariesLeavePreviouslyCapturedDataIntact()
    {
        var scene = new CellScene(new(Guid.NewGuid(), 0));
        scene.Observe(default, CellGeometry.Empty); CellSceneFrame first = scene.Capture();
        scene.Observe(default, CellGeometry.Empty); Assert.AreSame(first, scene.Capture());
        scene.Invalidate(new(5, 5, 5)); Assert.AreSame(first, scene.Capture());
        scene.Observe(default, CellGeometry.Unknown); scene.Observe(default, CellGeometry.Unknown);
        Assert.AreEqual(CellState.Empty, first.At(default).State);
        Assert.AreEqual(1, first.RegionCount); Assert.IsTrue(first.Regions.Single().Cells.Length == 512);
        Assert.ThrowsException<ArgumentNullException>(() => CellGeometry.FromMesh(null!));
        Assert.ThrowsException<ArgumentException>(() => CellSceneTracer.Trace(first, new(default, N)));
        Exception? error = null; var thread = new Thread(() => { try {scene.Capture();} catch(Exception ex) {error = ex;} });
        thread.Start(); thread.Join(); Assert.IsInstanceOfType<InvalidOperationException>(error);
        var queue = new WorkQueue<int>();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => queue.Drain(-1, TimeSpan.Zero));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => queue.Drain(1, TimeSpan.FromTicks(-1)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => queue.Enqueue(0, new Once(), (WorkPriority)99));
        error = null; thread = new Thread(() => {try {queue.Clear();} catch(Exception ex) {error = ex;}});
        thread.Start(); thread.Join(); Assert.IsInstanceOfType<InvalidOperationException>(error);
        queue.Enqueue(0, new Once(), WorkPriority.Background); queue.Clear(); Assert.AreEqual(0, queue.Count);
        var store = new RegionStore(new(Guid.NewGuid(), 0)); RegionTicket ticket = store.Begin(default);
        Assert.IsFalse(store.AcknowledgeUpload(ticket));
        Assert.IsFalse(store.AcknowledgeUpload(ticket with {Region = new(1, 0, 0)}));
        Assert.IsTrue(store.Complete(ticket, new([]))); Assert.IsTrue(store.AcknowledgeUpload(ticket));
        Assert.IsFalse(store.AcknowledgeUpload(ticket));
    }
    private sealed class Once : IIncrementalWork {public bool Step() => true;}

    [TestMethod]
    public void DiscoveryRevisitsCompletedEditsAndEvictsOnlyOutsideTheNewWindow()
    {
        int samples = 0, evictions = 0; var window = new DiscoveryWindow((_, _) => samples++, _ => evictions++);
        Assert.ThrowsException<ArgumentException>(() => DiscoveryWindow.At(Bad));
        window.Invalidate(default); window.MoveTo(default); window.MoveTo(default);
        Assert.AreEqual(27, window.Active.Count); Assert.IsTrue(window.Contains(default));
        window.Drain(27 * 512, TimeSpan.FromSeconds(30)); Assert.AreEqual(27 * 512, samples); Assert.AreEqual(0, window.Pending);
        window.Invalidate(default); Assert.AreEqual(1, window.Pending);
        window.Drain(512, TimeSpan.FromSeconds(30)); Assert.AreEqual(28 * 512, samples);
        window.MoveTo(new(1, 0, 0)); Assert.AreEqual(9, evictions); window.Clear(); Assert.AreEqual(0, window.Active.Count);
    }

    [TestMethod]
    public void LabLightSwitchesUseTheConfiguredGroupAndRespectExplicitDisable()
    {
        foreach ((int width, int height) in new[] {(0, 1), (1, 0), (1025, 1025)})
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => DirectLightLab.Create(width, height));
        var surfaces = DirectLightLab.Create(4, 4, blocker: false);
        var registry = new LightRegistry(surfaces.World);
        var enabled = new EmissionSelection(null, null, EmissionProfile.Candle, ComponentCount: 3);
        DirectLightLab.SetLights(registry, default, enabled, true); Assert.AreEqual(2, registry.Count);
        DirectLightLab.SetLights(registry, default, enabled with {Enabled = false}, true); Assert.AreEqual(1, registry.Count);
        DirectLightLab.SetLights(registry, default, enabled, false); Assert.AreEqual(0, registry.Count);
    }
}
