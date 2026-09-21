namespace VintageRTX.Core.Geometry;

/// <summary>Immutable balanced triangle hierarchy for the CPU reference and geometry qualification.</summary>
public sealed class TriangleBvh
{
    private readonly Triangle[] triangles;
    private readonly int[] indices;
    private readonly Node[] nodes;
    private readonly record struct Node(Bounds Bounds, int Start, int Count, int Left, int Right);
    public int TriangleCount => triangles.Length;
    public TriangleBvh(ReadOnlySpan<Triangle> geometry)
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
    private readonly record struct PendingNode(int Index, double Entry);

    public bool Trace(in Ray ray, out SurfaceHit hit) => Trace(ray, out hit, out _);

    /// <summary>
    /// Retain the slab entry computed when a child was queued. Once a nearer triangle is found,
    /// compare this entry with the shortened interval rather than repeating three slab divisions.
    /// Exact-distance ties remain eligible so primitive ordering is unchanged.
    /// </summary>
    public bool Trace(in Ray ray, out SurfaceHit hit, out BvhTraceStatistics statistics)
    {
        hit = default; statistics = default;
        if (nodes.Length == 0) return false;
        int boundsTests = 1, triangleTests = 0;
        if (!nodes[0].Bounds.Intersect(ray, ray.Maximum, out double rootEntry))
        { statistics = new(boundsTests, triangleTests); return false; }
        // A median split over a CLR array has depth <= 31. At most one sibling per level is
        // pending; this fixed stack has room for the root plus all siblings without heap work.
        Span<PendingNode> stack = stackalloc PendingNode[64];
        int size = 1; stack[0] = new(0, rootEntry);
        double closest = ray.Maximum; bool found = false;
        while (size > 0)
        {
            PendingNode pending = stack[--size];
            if (pending.Entry > closest) continue;
            Node node = nodes[pending.Index];
            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int primitive = indices[i]; triangleTests++;
                    if (triangles[primitive].Intersect(ray, closest, primitive, out SurfaceHit candidate)
                        && (!found || candidate.Distance < closest || candidate.Distance == closest && candidate.Primitive < hit.Primitive))
                    { hit = candidate; closest = candidate.Distance; found = true; }
                }
            }
            else
            {
                boundsTests += 2;
                bool left = nodes[node.Left].Bounds.Intersect(ray, closest, out double dl);
                bool right = nodes[node.Right].Bounds.Intersect(ray, closest, out double dr);
                if (left && right)
                {
                    stack[size++] = dl < dr ? new(node.Right, dr) : new(node.Left, dl);
                    stack[size++] = dl < dr ? new(node.Left, dl) : new(node.Right, dr);
                }
                else if (left) stack[size++] = new(node.Left, dl);
                else if (right) stack[size++] = new(node.Right, dr);
            }
        }
        statistics = new(boundsTests, triangleTests); return found;
    }
}

/// <summary>Operation counts, not a GPU timer or an estimated frame rate.</summary>
public readonly record struct BvhTraceStatistics(int BoundsTests, int TriangleTests);
