using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Scene;

public enum CellState { Unknown=0, Empty=1, Mesh=2, Unsupported=3 }
public readonly record struct CellId(int X,int Y,int Z)
{
    public RegionId Region => new(FloorDiv(X,8),FloorDiv(Y,8),FloorDiv(Z,8));
    public int Index => Mod(X,8)+8*Mod(Y,8)+64*Mod(Z,8);
    public DVec3 Position => new(X,Y,Z);
    public static int FloorDiv(int value,int divisor) => (int)Math.Floor(value/(double)divisor);
    public static int Mod(int value,int divisor) => (value%divisor+divisor)%divisor;
}
public readonly record struct CellGeometry
{
    public CellState State { get; }
    public BlockMesh? Mesh { get; }
    private CellGeometry(CellState state,BlockMesh? mesh=null) { State=state;Mesh=mesh; }
    public static CellGeometry Unknown => default;
    public static CellGeometry Empty => new(CellState.Empty);
    public static CellGeometry Unsupported => new(CellState.Unsupported);
    public static CellGeometry FromMesh(BlockMesh mesh) => new(CellState.Mesh,mesh??throw new ArgumentNullException(nameof(mesh)));
}

/// <summary>A regional immutable snapshot may contain known cells AND cells not yet observed.</summary>
public sealed class CellRegion
{
    private readonly CellGeometry[] cells;
    internal CellRegion(RegionId id,long revision,CellGeometry[] cells) { Id=id;Revision=revision;this.cells=cells; }
    public RegionId Id { get; }
    public long Revision { get; }
    public ReadOnlySpan<CellGeometry> Cells => cells;
    public CellGeometry this[int index] => cells[index];
}

/// <summary>Detached frame: safe for CPU workers. No world access or mutable game mesh retained.</summary>
public sealed class CellSceneFrame
{
    private readonly Dictionary<RegionId,CellRegion> regions;
    internal CellSceneFrame(WorldId world,long revision,Dictionary<RegionId,CellRegion> regions)
    { World=world;Revision=revision;this.regions=regions; }
    public WorldId World { get; }
    public long Revision { get; }
    public IEnumerable<CellRegion> Regions => regions.Values;
    public int RegionCount => regions.Count;
    public CellGeometry At(CellId cell) => regions.TryGetValue(cell.Region,out CellRegion? r)?r[cell.Index]:CellGeometry.Unknown;
}

/// <summary>
/// Single-owner, copy-on-write cell publication. A nearby edit does not depend on completing the
/// other 511 cells, another region, rain, sun or GI. Capture reuses its object when nothing changed.
/// </summary>
public sealed class CellScene
{
    private readonly int owner=Environment.CurrentManagedThreadId;
    private readonly Dictionary<RegionId,CellRegion> published=new();
    private readonly Dictionary<RegionId,CellGeometry[]> writes=new();
    private WorldId world;
    private long revision;
    private CellSceneFrame? last;
    public CellScene(WorldId world) => this.world=world;
    private void CheckOwner()
    { if(Environment.CurrentManagedThreadId!=owner) throw new InvalidOperationException("Cell observations require the scene owner thread."); }
    public void Reset(WorldId next)
    { CheckOwner();world=next;published.Clear();writes.Clear();last=null;revision=checked(revision+1); }
    public void Observe(CellId id,CellGeometry value)
    {
        CheckOwner();RegionId region=id.Region;
        if(!writes.TryGetValue(region,out CellGeometry[]? buffer))
        {
            bool exists=published.TryGetValue(region,out CellRegion? ready);
            if((exists?ready![id.Index]:CellGeometry.Unknown)==value) return;
            buffer=exists?ready!.Cells.ToArray():new CellGeometry[512]; writes.Add(region,buffer);
        }
        if(buffer[id.Index]==value) return;
        buffer[id.Index]=value;revision=checked(revision+1);last=null;
    }
    public void Invalidate(RegionId region)
    {
        CheckOwner();
        // Drop only this region immediately. Old captured frames remain immutable.
        bool removed=published.Remove(region);removed|=writes.Remove(region);
        if(removed) { revision=checked(revision+1);last=null; }
    }
    public CellSceneFrame Capture()
    {
        CheckOwner();if(last is not null) return last;
        foreach((RegionId id,CellGeometry[] cells) in writes) published[id]=new(id,revision,cells);
        writes.Clear();
        return last=new(world,revision,new(published));
    }
}

public enum SceneTraceStatus { Clear=0, Hit=1, Unknown=2, Unsupported=3, BudgetExhausted=4 }
public readonly record struct SceneTrace(SceneTraceStatus Status,double Distance,CellId Cell,SurfaceHit Hit,int CellsVisited);

/// <summary>
/// World-cell traversal with mesh-local BVH intersections. Unknown or unsupported intervals stop
/// the query: neither is treated as visible sky nor a measured solid. Direction ties advance
/// together; zero-width edge contacts do not inspect unrelated neighbour cells.
/// </summary>
public static class CellSceneTracer
{
    public static SceneTrace Trace(CellSceneFrame scene,in Ray ray,int maximumCells=256)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if(!double.IsFinite(ray.Maximum)) throw new ArgumentException("Streaming queries require a finite requested interval.");
        DVec3 start=ray.At(ray.Minimum);
        CellId cell=new(checked((int)Math.Floor(start.X)),checked((int)Math.Floor(start.Y)),checked((int)Math.Floor(start.Z)));
        double entered=ray.Minimum;
        for(int step=0;step<maximumCells;step++)
        {
            // Subtract the cell origin in double precision BEFORE tracing unit-sized geometry.
            DVec3 local=ray.Origin-cell.Position;
            double bx=Boundary(local.X,ray.Direction.X),by=Boundary(local.Y,ray.Direction.Y),bz=Boundary(local.Z,ray.Direction.Z);
            double boundary=Math.Min(bx,Math.Min(by,bz)),exit=Math.Min(boundary,ray.Maximum);
            if(exit>entered)
            {
                CellGeometry g=scene.At(cell);
                if(g.State is CellState.Unknown or CellState.Unsupported)
                    return new(g.State==CellState.Unknown?SceneTraceStatus.Unknown:SceneTraceStatus.Unsupported,entered,cell,default,step+1);
                if(g.Mesh is not null)
                {
                    Ray localRay=new(local,ray.Direction,entered,exit);
                    if(g.Mesh.Trace(localRay,out SurfaceHit h))
                        return new(SceneTraceStatus.Hit,h.Distance,cell,h with { Position=ray.At(h.Distance) },step+1);
                }
            }
            if(exit>=ray.Maximum) return new(SceneTraceStatus.Clear,ray.Maximum,cell,default,step+1);
            cell=new(checked(cell.X+(bx<=boundary?Math.Sign(ray.Direction.X):0)),
                checked(cell.Y+(by<=boundary?Math.Sign(ray.Direction.Y):0)),
                checked(cell.Z+(bz<=boundary?Math.Sign(ray.Direction.Z):0)));
            entered=Math.Max(entered,boundary);
        }
        return new(SceneTraceStatus.BudgetExhausted,entered,cell,default,Math.Max(0,maximumCells));
    }
    private static double Boundary(double origin,double direction) => direction==0?double.PositiveInfinity:((direction>0?1:0)-origin)/direction;
}
