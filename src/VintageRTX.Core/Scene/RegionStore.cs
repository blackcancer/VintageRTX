using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Scene;

public readonly record struct RegionId(int X, int Y, int Z);
public readonly record struct RegionTicket(WorldId World, RegionId Region, long Revision);
public enum RegionState { Unknown, Building, CpuReady, Ready }

/// <summary>Immutable copied geometry. Empty KNOWN geometry differs from a missing region.</summary>
public sealed class RegionGeometry
{
    private readonly Triangle[] triangles;
    public RegionGeometry(ReadOnlySpan<Triangle> triangles) => this.triangles=triangles.ToArray();
    public ReadOnlySpan<Triangle> Triangles => triangles;
}

/// <summary>
/// Independent region transactions. Completion and GPU acknowledgement are revision checked.
/// No global settled flag. No ICoreAPI, worker world access or GL calls in this store.
/// </summary>
public sealed class RegionStore
{
    private sealed class Entry(RegionTicket ticket)
    {
        public RegionTicket Ticket= ticket;
        public RegionState State=RegionState.Building;
        public RegionGeometry? Geometry;
    }
    private readonly object sync=new();
    private readonly Dictionary<RegionId,Entry> regions=new();
    private long nextRevision;
    private WorldId world;
    public RegionStore(WorldId world) => this.world=world;
    public void Reset(WorldId id) { lock(sync) { world=id; regions.Clear(); } }
    public RegionTicket Begin(RegionId id)
    {
        lock(sync)
        {
            RegionTicket ticket=new(world,id,checked(++nextRevision));
            regions[id]=new(ticket); return ticket;
        }
    }
    public void Evict(RegionId id) { lock(sync) regions.Remove(id); }
    public bool Complete(RegionTicket ticket,RegionGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        lock(sync)
        {
            if(!Match(ticket,out Entry? entry) || entry!.State!=RegionState.Building) return false;
            entry.Geometry=geometry; entry.State=RegionState.CpuReady; return true;
        }
    }
    public bool TryGetUpload(RegionTicket ticket,out RegionGeometry? geometry)
    {
        lock(sync)
        {
            geometry=null;
            if(!Match(ticket,out Entry? entry) || entry!.State!=RegionState.CpuReady) return false;
            geometry=entry.Geometry; return true;
        }
    }
    public bool AcknowledgeUpload(RegionTicket ticket)
    {
        lock(sync)
        {
            if(!Match(ticket,out Entry? entry) || entry!.State!=RegionState.CpuReady) return false;
            entry.State=RegionState.Ready; return true;
        }
    }
    public RegionState StateOf(RegionId id)
    { lock(sync) return regions.TryGetValue(id,out Entry? entry)?entry.State:RegionState.Unknown; }
    public bool TryGetReady(RegionId id,out RegionGeometry? geometry)
    {
        lock(sync)
        {
            geometry=null;
            if(!regions.TryGetValue(id,out Entry? entry) || entry.State!=RegionState.Ready) return false;
            geometry=entry.Geometry; return true;
        }
    }
    private bool Match(RegionTicket ticket,out Entry? entry)
    {
        entry=null;
        return ticket.World==world && regions.TryGetValue(ticket.Region,out entry) && entry.Ticket==ticket;
    }
}
