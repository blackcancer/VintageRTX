using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Geometry;

/// <summary>
/// Detached, immutable, block-local render triangles. Neither collision boxes nor inventory meshes
/// are substitutes. This first backend accepts geometry inside one unit cell only; overhanging,
/// alpha-tested, animated and procedural instances need an explicit provider.
/// </summary>
public sealed class BlockMesh
{
    private readonly Triangle[] triangles;
    private readonly TriangleBvh hierarchy;
    public ReadOnlySpan<Triangle> Triangles => triangles;
    public int TriangleCount => triangles.Length;
    public BlockMesh(ReadOnlySpan<Triangle> geometry)
    {
        if(geometry.IsEmpty) throw new ArgumentException("Use a known empty cell, not an empty mesh.");
        if(geometry.Length>4096) throw new ArgumentException("Block mesh exceeds this backend's explicit 4096-triangle budget.");
        triangles=geometry.ToArray();
        foreach(Triangle t in triangles)
        {
            Validate(t.A); Validate(t.B); Validate(t.C);
            if(DVec3.Cross(t.B-t.A,t.C-t.A).LengthSquared<=1e-24)
                throw new ArgumentException("Degenerate render triangle.");
        }
        hierarchy=new(triangles);
    }
    private static void Validate(DVec3 v)
    {
        if(!v.IsFinite || v.X<0 || v.Y<0 || v.Z<0 || v.X>1 || v.Y>1 || v.Z>1)
            throw new ArgumentException("Mesh crosses its owning cell; an instanced geometry provider is required.");
    }
    public bool Trace(in Ray localRay,out SurfaceHit hit) => hierarchy.Trace(localRay,out hit);

    /// <summary>Copies only active buffer prefixes. Capacity tails may contain arbitrary old data.</summary>
    public static BlockMesh CopyIndexed(ReadOnlySpan<float> xyz,int vertexCount,
        ReadOnlySpan<int> indices,int indexCount)
    {
        if(vertexCount<=0 || vertexCount>xyz.Length/3 || indexCount<=0 || indexCount>indices.Length
            || indexCount%3!=0 || indexCount/3>4096) throw new ArgumentException("Malformed active mesh counts.");
        var geometry=new Triangle[indexCount/3];
        for(int t=0;t<geometry.Length;t++)
        {
            int ia=indices[t*3],ib=indices[t*3+1],ic=indices[t*3+2];
            if((uint)ia>=(uint)vertexCount || (uint)ib>=(uint)vertexCount || (uint)ic>=(uint)vertexCount)
                throw new ArgumentException("Index lies outside the active vertex prefix.");
            geometry[t]=new(Read(xyz,ia),Read(xyz,ib),Read(xyz,ic),0);
        }
        return new(geometry);
    }
    private static DVec3 Read(ReadOnlySpan<float> xyz,int i) => new(xyz[i*3],xyz[i*3+1],xyz[i*3+2]);
}
