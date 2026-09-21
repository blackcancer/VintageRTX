using System.Numerics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Diagnostics;

/// <summary>
/// Explicit synthetic receiver scene for developing the real image pass. Primary rays use the same
/// CPU mesh tracer, while direct visibility runs on the GPU. Not a snapshot of the player's world.
/// </summary>
public static class DirectLightLab
{
    public static DirectSurfaceFrame Create(int width = 128, int height = 96, CellId offset = default, bool blocker = true)
    {
        if (width < 1 || height < 1 || (long)width * height > 1_048_576) throw new ArgumentOutOfRangeException(nameof(width));
        var scene = new CellScene(new WorldId(Guid.NewGuid(), 0));
        for (int z = 0; z < 8; z++) for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            scene.Observe(Cell(x, y, z), CellGeometry.Empty);
        SurfaceMaterial[] materials = [
            new(SurfaceKind.Diffuse, new(.55f, .48f, .38f)),
            new(SurfaceKind.Diffuse, new(.36f, .45f, .56f)),
            // Deliberately authored test indices, not claimed measurements of a named metal.
            new(SurfaceKind.Conductor, Vector3.One, .22, new(.2f, .85f, 1.2f), new(3.2f, 2.8f, 2.5f)),
            new(SurfaceKind.Conductor, Vector3.One, .48, new(2, 2, 2), new(3, 3, 3)),
            new(SurfaceKind.Diffuse, new(.12f, .12f, .12f))];
        var wallMeshes = Enumerable.Range(0, 4).Select(material => CellGeometry.FromMesh(new BlockMesh(new Triangle[] {
            new(new(0,0,.75),new(1,0,.75),new(1,1,.75),material),
            new(new(0,0,.75),new(1,1,.75),new(0,1,.75),material)}))).ToArray();
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) scene.Observe(Cell(x, y, 0), wallMeshes[x / 2]);
        if (blocker) scene.Observe(Cell(3, 3, 2), CellGeometry.FromMesh(Box(4)));
        CellSceneFrame snapshot = scene.Capture();
        DVec3 camera = offset.Position + new DVec3(4, 4, 7);
        var receivers = new SurfaceReceiver[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            DVec3 target = offset.Position + new DVec3(.5 + 7.0 * (x + .5) / width, .5 + 7.0 * (y + .5) / height, .75);
            var ray = new Ray(camera, target - camera, .0001, 16);
            SceneTrace hit = CellSceneTracer.Trace(snapshot, ray);
            if (hit.Status != SceneTraceStatus.Hit) continue;
            DVec3 geometric = hit.Hit.GeometricNormal;
            if (DVec3.Dot(geometric, -ray.Direction) < 0) geometric = -geometric;
            DVec3 shading = geometric;
            DVec3 local = hit.Hit.Position - offset.Position;
            if (hit.Hit.Material == 1)
                shading = new DVec3(.22 * Math.Sin(local.X * 9), .22 * Math.Cos(local.Y * 11), 1).Normalized();
            float texel = ((int)Math.Floor(local.X * 4) + (int)Math.Floor(local.Y * 4)) % 2 == 0 ? 1f : .7f;
            receivers[y * width + x] = new(hit.Hit.Position, shading, geometric, new Vector3(texel), hit.Hit.Material);
        }
        return new(snapshot, offset, camera, width, height, receivers, materials);
        CellId Cell(int x, int y, int z) => new(checked(offset.X + x), checked(offset.Y + y), checked(offset.Z + z));
    }

    public static LightId WarmId => new(SourceKind.Extension, 100, 0, 0, 0, 0);
    public static LightId CoolId => new(SourceKind.Extension, 200, 0, 0, 0, 0);
    public static void SetLights(LightRegistry registry, CellId anchor, EmissionSelection warmProfile, bool lit)
    {
        if (!lit) { registry.Remove(WarmId); registry.Remove(CoolId); return; }
        LightDefinition? warm = warmProfile.CreateLight(anchor.Position + new DVec3(2.2, 3.4, 4.2), new(40, 22, 7), radius: .12);
        if (warm is null) registry.Remove(WarmId); else registry.Upsert(WarmId, warm);
        registry.Upsert(CoolId, new(anchor.Position + new DVec3(5.8, 5.8, 3.5), new(8, 11, 20), EmissionProfile.Steady, radius: .06));
    }
    private static BlockMesh Box(int material)
    {
        DVec3[] p = [new(.2,0,.2),new(.8,0,.2),new(.8,1,.2),new(.2,1,.2),
            new(.2,0,.8),new(.8,0,.8),new(.8,1,.8),new(.2,1,.8)];
        int[] indices = [0,2,1,0,3,2,4,5,6,4,6,7,0,4,7,0,7,3,1,2,6,1,6,5,0,1,5,0,5,4,3,7,6,3,6,2];
        var triangles = new Triangle[indices.Length / 3];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = new(p[indices[i * 3]], p[indices[i * 3 + 1]], p[indices[i * 3 + 2]], material);
        return new(triangles);
    }
}
