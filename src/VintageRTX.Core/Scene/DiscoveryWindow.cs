using VintageRTX.Core.Geometry;
using VintageRTX.Core.Scheduling;

namespace VintageRTX.Core.Scene;

/// <summary>
/// Camera-relative set of stable WORLD regions. Moving one region reuses the overlap;
/// invalidating a running scan requests a later confirmation without restarting its progress.
/// Discoveries are reported per cell, never withheld until the window is complete.
/// </summary>
public sealed class DiscoveryWindow
{
    public const int Edge=8;
    private readonly WorkQueue<RegionId> queue=new();
    private readonly HashSet<RegionId> active=new(),repeat=new();
    private readonly Action<RegionId,int> sample;
    private readonly Action<RegionId> evict;
    private RegionId? center;
    public DiscoveryWindow(Action<RegionId,int> sample,Action<RegionId> evict)
    { this.sample=sample; this.evict=evict; }
    public int Pending=>queue.Count;
    public IReadOnlyCollection<RegionId> Active=>active.ToArray();
    public bool Contains(RegionId id)=>active.Contains(id);
    public static RegionId At(DVec3 position)
    {
        if(!position.IsFinite) throw new ArgumentException("Nonfinite region position.");
        return new(checked((int)Math.Floor(position.X/Edge)),checked((int)Math.Floor(position.Y/Edge)),checked((int)Math.Floor(position.Z/Edge)));
    }
    public void MoveTo(RegionId next)
    {
        if(center==next) return;
        var desired=new HashSet<RegionId>();
        for(int x=-1;x<=1;x++) for(int y=-1;y<=1;y++) for(int z=-1;z<=1;z++)
            desired.Add(new(checked(next.X+x),checked(next.Y+y),checked(next.Z+z)));
        foreach(RegionId id in active.Where(id=>!desired.Contains(id)).ToArray())
        { queue.Cancel(id); repeat.Remove(id); active.Remove(id); evict(id); }
        foreach(RegionId id in desired.OrderBy(id=>Math.Abs(id.X-next.X)+Math.Abs(id.Y-next.Y)+Math.Abs(id.Z-next.Z)))
            if(active.Add(id)) queue.Enqueue(id,new Scan(this,id),WorkPriority.Background);
        center=next;
    }
    public void Invalidate(RegionId id)
    {
        if(!active.Contains(id)) return;
        if(queue.Contains(id)) repeat.Add(id);
        else queue.Enqueue(id,new Scan(this,id),WorkPriority.NearbyEdit);
    }
    public WorkReport Drain(int maximumSteps,TimeSpan budget)=>queue.Drain(maximumSteps,budget);
    public void Clear()
    { queue.Clear(); active.Clear(); repeat.Clear(); center=null; }
    private sealed class Scan(DiscoveryWindow owner,RegionId region):IIncrementalWork
    {
        private int index;
        public bool Step()
        {
            if(!owner.active.Contains(region)) return true;
            owner.sample(region,index++);
            if(index<Edge*Edge*Edge) return false;
            if(owner.repeat.Remove(region)) owner.queue.Enqueue(region,new Scan(owner,region),WorkPriority.NearbyEdit);
            return true;
        }
    }
}
