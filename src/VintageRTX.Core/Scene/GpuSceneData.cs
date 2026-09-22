using VintageRTX.Core.Geometry;

namespace VintageRTX.Core.Scene;

/// <summary>Versioned GPU packets for a collision-free ring of 27 world regions and shared mesh BVHs.</summary>
public sealed class GpuSceneData
{
    public const int RegionSlots=27,CellWidth=64,CellRowsPerRegion=8,GeometryWidth=256;
    public const int MaximumGeometryTexels=1048576;
    // Low 16 bits retain triangle count; bit 16 opts into the independent surface packet.
    public const int SurfacePresentBit=1<<16;
    private readonly int[] regions=new int[RegionSlots*4],cells=new int[RegionSlots*512*4];
    private readonly float[] materials = new float[RegionSlots * 512 * 8];
    public ReadOnlySpan<float> MaterialData => materials;
    private readonly long[] revisions=new long[RegionSlots];
    private readonly Dictionary<BlockMesh,(int Start,int End)> templates=new();
    private readonly List<float> geometry=new();
    private float[] packedGeometry=new float[GeometryWidth*4];
    private CellSceneFrame? previous;
    private int[] changed=Array.Empty<int>(), materialChanged=Array.Empty<int>();
    public ReadOnlySpan<int> MaterialChangedRegions=>materialChanged;
    public CellSceneFrame? SourceFrame=>previous;
    public ReadOnlySpan<int> RegionData=>regions;
    public ReadOnlySpan<int> CellData=>cells;
    public ReadOnlySpan<float> GeometryData=>packedGeometry;
    public ReadOnlySpan<int> ChangedRegions=>changed;
    public bool GeometryChanged { get; private set; }
    public bool ResetOccurred { get; private set; }
    public int GeometryHeight=>packedGeometry.Length/(GeometryWidth*4);
    public int TemplateCount=>templates.Count;
    public int GeometryUsedTexels=>geometry.Count/4;
    public static int Slot(RegionId id)=>CellId.Mod(id.X,3)+3*CellId.Mod(id.Y,3)+9*CellId.Mod(id.Z,3);

    public bool Update(CellSceneFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if(ReferenceEquals(previous,frame)) { changed=Array.Empty<int>();materialChanged=Array.Empty<int>();GeometryChanged=false;ResetOccurred=false;return false; }
        CellRegion?[] next=new CellRegion?[RegionSlots];
        foreach(CellRegion region in frame.Regions)
        {
            int slot=Slot(region.Id);
            if(next[slot] is not null) throw new ArgumentException("Working set aliases the three-region ring; do not silently overwrite coverage.");
            next[slot]=region;
        }
        ResetOccurred=previous is null || previous.World!=frame.World;
        // From the first mutation until successful completion, no old identity may describe this
        // packet. Capacity/allocation failure leaves it unpublished; the next attempt rebuilds it.
        previous=null;
        GeometryChanged=ResetOccurred;
        if(ResetOccurred)
        {
            Array.Clear(regions);Array.Clear(cells);Array.Clear(materials);Array.Clear(revisions);templates.Clear();geometry.Clear();
        }
        var writes=new List<int>();var surfaceWrites=new List<int>();
        Span<float> surfaceValue=stackalloc float[8];
        for(int slot=0;slot<RegionSlots;slot++)
        {
            int tag=slot*4;CellRegion? r=next[slot];
            if(r is null)
            {
                if(ResetOccurred || regions[tag+3]!=0) { Array.Clear(regions,tag,4);writes.Add(slot); }
                continue;
            }
            if(!ResetOccurred && regions[tag+3]!=0 && regions[tag]==r.Id.X && regions[tag+1]==r.Id.Y
                && regions[tag+2]==r.Id.Z && revisions[slot]==r.Revision) continue;
            int offset=slot*512*4;bool surfaceChanged=false;
            for(int i=0;i<512;i++)
            {
                CellGeometry cell=r[i];int index=offset+i*4;
                int surface=(slot*512+i)*8;
                surfaceValue.Clear();
                if(cell.Surface is { Material: null }) surfaceValue[3]=-1;
                if(cell.Surface?.Material is { } material)
                {
                    surfaceValue[0]=material.Eta.X;surfaceValue[1]=material.Eta.Y;surfaceValue[2]=material.Eta.Z;
                    surfaceValue[3]=material.Kind==Transport.SurfaceKind.Conductor?2:1;
                    surfaceValue[4]=material.K.X;surfaceValue[5]=material.K.Y;surfaceValue[6]=material.K.Z;
                    surfaceValue[7]=(float)material.Roughness;
                }
                Span<float> destination=materials.AsSpan(surface,8);
                if(!surfaceValue.SequenceEqual(destination))
                { surfaceValue.CopyTo(destination);surfaceChanged=true; }
                cells[index]=(int)cell.State;cells[index+1]=0;cells[index+2]=0;cells[index+3]=0;
                if(cell.Mesh is not null)
                {
                    if(!templates.TryGetValue(cell.Mesh,out var address))
                    { address=Append(cell.Mesh);templates.Add(cell.Mesh,address);GeometryChanged=true; }
                    cells[index+1]=address.Start;cells[index+2]=address.End;cells[index+3]=cell.Mesh.TriangleCount;
                }
                if(cell.Surface is not null)cells[index+3]|=SurfacePresentBit;
            }
            regions[tag]=r.Id.X;regions[tag+1]=r.Id.Y;regions[tag+2]=r.Id.Z;regions[tag+3]=1;
            revisions[slot]=r.Revision;writes.Add(slot);
            if(surfaceChanged)surfaceWrites.Add(slot);
        }
        if(GeometryChanged)
        {
            int requiredRows=Math.Max(1,(geometry.Count+GeometryWidth*4-1)/(GeometryWidth*4));
            int rows=1;while(rows<requiredRows) rows=checked(rows*2);
            packedGeometry=new float[rows*GeometryWidth*4];geometry.CopyTo(packedGeometry);
        }
        changed=writes.ToArray();materialChanged=surfaceWrites.ToArray();previous=frame;
        // Every geometry append belongs to a rewritten region; a reset rewrites all tags.
        // GeometryChanged therefore cannot require an upload without a changed region.
        return changed.Length>0;
    }
    private readonly record struct Node(Bounds Bounds,int First,int Count,int Escape);
    private (int Start,int End) Append(BlockMesh mesh)
    {
        Triangle[] triangles=mesh.Triangles.ToArray();int[] order=Enumerable.Range(0,triangles.Length).ToArray();
        var nodes=new List<Node>();Build(0,order.Length);
        int start=geometry.Count/4,nodeEnd=checked(start+nodes.Count*3);
        if(nodeEnd+triangles.Length*3>MaximumGeometryTexels) throw new InvalidOperationException("Geometry cache capacity exceeded; coverage is NOT a miss.");
        foreach(Node node in nodes)
        {
            Add(MathF.BitDecrement((float)node.Bounds.Minimum.X),MathF.BitDecrement((float)node.Bounds.Minimum.Y),MathF.BitDecrement((float)node.Bounds.Minimum.Z),0);
            Add(MathF.BitIncrement((float)node.Bounds.Maximum.X),MathF.BitIncrement((float)node.Bounds.Maximum.Y),MathF.BitIncrement((float)node.Bounds.Maximum.Z),0);
            Add(nodeEnd+node.First*3,node.Count,start+node.Escape*3,0);
        }
        foreach(int primitive in order)
        {
            Triangle t=triangles[primitive];Add((float)t.A.X,(float)t.A.Y,(float)t.A.Z,primitive);
            Add((float)t.B.X,(float)t.B.Y,(float)t.B.Z,t.Material);Add((float)t.C.X,(float)t.C.Y,(float)t.C.Z,0);
        }
        return(start,nodeEnd);
        void Add(float x,float y,float z,float w) { geometry.Add(x);geometry.Add(y);geometry.Add(z);geometry.Add(w); }
        void Build(int first,int count)
        {
            int index=nodes.Count;nodes.Add(default);Bounds bounds=triangles[order[first]].Bounds;
            Bounds centroids=new(triangles[order[first]].Centroid,triangles[order[first]].Centroid);
            for(int i=first+1;i<first+count;i++)
            { Triangle t=triangles[order[i]];bounds=Bounds.Union(bounds,t.Bounds);centroids=Bounds.Union(centroids,new(t.Centroid,t.Centroid)); }
            if(count<=4) { nodes[index]=new(bounds,first,count,index+1);return; }
            DVec3 e=centroids.Maximum-centroids.Minimum;int axis=e.X>=e.Y && e.X>=e.Z?0:e.Y>=e.Z?1:2;
            Array.Sort(order,first,count,Comparer<int>.Create((a,b)=>
            { int c=triangles[a].Centroid[axis].CompareTo(triangles[b].Centroid[axis]);return c==0?a.CompareTo(b):c; }));
            int half=count/2;Build(first,half);Build(first+half,count-half);nodes[index]=new(bounds,0,0,nodes.Count);
        }
    }
}
