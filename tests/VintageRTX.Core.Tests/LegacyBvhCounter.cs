// Reference frozen from 8f58774f; changes limited to namespace/type name and bounds-call counting.
// Original source SHA256: db10713e1cc0303b05281f199ae687d624f9b68c19134cdbd6f2556c4c6aee3c
using VintageRTX.Core.Geometry;

namespace VintageRTX.Core.Tests;

public sealed class LegacyBvhCounter
{
    private readonly Triangle[] triangles;
    private readonly int[] indices;
    private readonly Node[] nodes;
    private readonly record struct Node(Bounds Bounds, int Start, int Count, int Left, int Right);
    public long BoundsTests { get; private set; }
    public LegacyBvhCounter(ReadOnlySpan<Triangle> geometry)
    {
        triangles=geometry.ToArray(); indices=Enumerable.Range(0,triangles.Length).ToArray();
        var build=new List<Node>();
        if(triangles.Length>0) Build(0,triangles.Length,build);
        nodes=build.ToArray();
    }
    private int Build(int start,int count,List<Node> target)
    {
        Bounds b=triangles[indices[start]].Bounds;
        Bounds centroids=new(triangles[indices[start]].Centroid,triangles[indices[start]].Centroid);
        for(int i=start+1;i<start+count;i++)
        {
            Triangle t=triangles[indices[i]]; b=Bounds.Union(b,t.Bounds);
            centroids=Bounds.Union(centroids,new(t.Centroid,t.Centroid));
        }
        int index=target.Count; target.Add(default);
        if(count<=4) { target[index]=new(b,start,count,-1,-1); return index; }
        DVec3 extent=centroids.Maximum-centroids.Minimum;
        int axis=extent.X>=extent.Y && extent.X>=extent.Z ? 0 : extent.Y>=extent.Z ? 1 : 2;
        Array.Sort(indices,start,count,Comparer<int>.Create((a,c)=>
        {
            int order=triangles[a].Centroid[axis].CompareTo(triangles[c].Centroid[axis]);
            return order!=0 ? order : a.CompareTo(c);
        }));
        int half=count/2, left=Build(start,half,target), right=Build(start+half,count-half,target);
        target[index]=new(b,0,0,left,right); return index;
    }
    private bool Intersect(Bounds b, in Ray ray, double limit, out double entry)
    { BoundsTests++; return b.Intersect(ray, limit, out entry); }
    public bool Trace(in Ray ray,out SurfaceHit hit)
    {
        BoundsTests=0; hit=default; if(nodes.Length==0) return false;
        Span<int> stack=stackalloc int[64]; int size=1; stack[0]=0;
        double closest=ray.Maximum; bool found=false;
        while(size>0)
        {
            Node node=nodes[stack[--size]];
            if(!Intersect(node.Bounds,ray,closest,out _)) continue;
            if(node.Count>0)
            {
                for(int i=node.Start;i<node.Start+node.Count;i++)
                {
                    int primitive=indices[i];
                    if(triangles[primitive].Intersect(ray,closest,primitive,out SurfaceHit candidate)
                        && (!found || candidate.Distance<closest || candidate.Distance==closest && candidate.Primitive<hit.Primitive))
                    { hit=candidate; closest=candidate.Distance; found=true; }
                }
            }
            else
            {
                if(size+2>stack.Length) throw new InvalidOperationException("BVH stack contract violated.");
                bool l=Intersect(nodes[node.Left].Bounds,ray,closest,out double dl);
                bool r=Intersect(nodes[node.Right].Bounds,ray,closest,out double dr);
                if(l && r)
                {
                    stack[size++]=dl<dr?node.Right:node.Left;
                    stack[size++]=dl<dr?node.Left:node.Right;
                }
                else if(l) stack[size++]=node.Left;
                else if(r) stack[size++]=node.Right;
            }
        }
        return found;
    }
}
