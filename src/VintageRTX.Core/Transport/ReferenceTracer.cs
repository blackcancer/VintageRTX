using System.Numerics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Transport;

/// <summary>
/// CPU path-transport reference over a complete supplied triangle scene. Not the game GPU backend.
/// All finite point sources are evaluated; diffuse and conductor paths use the same frozen frame.
/// No screenshots, raster brightness reconstruction, blur, bloom, clamping or invented ambient.
/// </summary>
public sealed class ReferenceTracer
{
    private readonly TriangleBvh scene;
    private readonly SurfaceMaterial[] materials;
    public ReferenceTracer(ReadOnlySpan<Triangle> geometry,IEnumerable<SurfaceMaterial> materials)
    {
        this.materials=materials.ToArray();
        foreach(Triangle t in geometry)
            if(t.Material>=this.materials.Length) throw new ArgumentException("Unknown triangle material.");
        scene=new(geometry);
    }
    public Vector3 Trace(Ray ray,LightFrame lights,ulong seed,int maximumBounces=4)
    {
        if(maximumBounces<1 || maximumBounces>64) throw new ArgumentOutOfRangeException(nameof(maximumBounces));
        foreach(LightSample source in lights.Samples)
            if(source.Radius>0) throw new NotSupportedException("This reference supports point sources; finite area emitters require area sampling.");
        Vector3 result=Vector3.Zero,throughput=Vector3.One;
        for(int depth=0;depth<maximumBounces;depth++)
        {
            if(!scene.Trace(ray,out SurfaceHit hit)) break; // Closed test scene, black environment.
            DVec3 n=DVec3.Dot(hit.GeometricNormal,ray.Direction)>0?-hit.GeometricNormal:hit.GeometricNormal;
            SurfaceMaterial material=materials[hit.Material];
            foreach(LightSample light in lights.Samples)
            {
                if(light.Intensity==Vector3.Zero) continue;
                DVec3 delta=light.Position-hit.Position; double r2=delta.LengthSquared;
                if(r2<=1e-16) continue;
                double distance=Math.Sqrt(r2); DVec3 wi=delta/distance;
                double cosine=DVec3.Dot(n,wi); if(cosine<=0) continue;
                Vector3 f=Bsdf.Evaluate(material,n,-ray.Direction,wi); if(f==Vector3.Zero) continue;
                // Offset only the ray segment, not its photometric distance. Reference double precision.
                DVec3 origin=Offset(hit.Position,n); DVec3 segment=light.Position-origin;
                double length=segment.Length; if(length<=1e-7) continue;
                if(scene.Trace(new Ray(origin,segment,0,length-1e-7),out _)) continue;
                result+=throughput*f*light.Intensity*(float)(cosine/r2);
            }
            seed=EmissionWaveform.Mix(seed); double u=EmissionWaveform.Unit(seed);
            seed=EmissionWaveform.Mix(seed); double v=EmissionWaveform.Unit(seed);
            if(!Bsdf.Sample(material,n,-ray.Direction,u,v,out DVec3 next,out Vector3 weight)) break;
            throughput*=weight;
            if(throughput==Vector3.Zero) break;
            ray=new(Offset(hit.Position,n),next);
        }
        return result;
    }
    private static DVec3 Offset(DVec3 position,DVec3 normal)
    {
        // An error-aware offset in double world coordinates, not a tenth-of-a-block global bias.
        double magnitude=Math.Max(Math.Abs(position.X),Math.Max(Math.Abs(position.Y),Math.Abs(position.Z)));
        return position+normal*Math.Max(1e-7,16*(Math.BitIncrement(magnitude)-magnitude));
    }
}
