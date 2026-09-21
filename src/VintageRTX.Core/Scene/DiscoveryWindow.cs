using VintageRTX.Core.Geometry;
using VintageRTX.Core.Scheduling;

namespace VintageRTX.Core.Scene;

/// <summary>Stable world regions; local observations are never withheld behind a complete-window scan.</summary>
public sealed class DiscoveryWindow
{
    public const int Edge = 8;
    private readonly WorkQueue<RegionId> queue = new();
    private readonly HashSet<RegionId> active = new(), repeat = new();
    private readonly Action<RegionId, int> sample;
    private readonly Action<RegionId> evict;
    private IReadOnlyCollection<RegionId> activeSnapshot = Array.Empty<RegionId>();
    private RegionId? center;
    public DiscoveryWindow(Action<RegionId, int> sample, Action<RegionId> evict)
    { this.sample = sample; this.evict = evict; }
    public int Pending => queue.Count;
    /// <summary>Immutable membership snapshot. Repeated chunk notices no longer copy all 27 tags.</summary>
    public IReadOnlyCollection<RegionId> Active => activeSnapshot;
    public bool Contains(RegionId id) => active.Contains(id);
    public static RegionId At(DVec3 position)
    {
        if (!position.IsFinite) throw new ArgumentException("Nonfinite region position.");
        return new(checked((int)Math.Floor(position.X / Edge)), checked((int)Math.Floor(position.Y / Edge)), checked((int)Math.Floor(position.Z / Edge)));
    }
    public void MoveTo(RegionId next)
    {
        if (center == next) return;
        // Validate the complete requested ring before evicting any currently valid region.
        var desired = new HashSet<RegionId>();
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            desired.Add(new(checked(next.X + x), checked(next.Y + y), checked(next.Z + z)));
        foreach (RegionId id in active.Where(id => !desired.Contains(id)).ToArray())
        { queue.Cancel(id); repeat.Remove(id); active.Remove(id); evict(id); }
        // Materialize once per move; hot readers consume the immutable snapshot, not this ordering iterator.
        RegionId[] ordered = desired.OrderBy(id => Math.Abs(id.X - next.X) + Math.Abs(id.Y - next.Y) + Math.Abs(id.Z - next.Z)).ToArray();
        foreach (RegionId id in ordered)
            if (active.Add(id)) queue.Enqueue(id, new Scan(this, id), WorkPriority.Background);
        activeSnapshot = Array.AsReadOnly(active.ToArray());
        center = next;
    }
    public void Invalidate(RegionId id)
    {
        if (!active.Contains(id)) return;
        if (queue.Contains(id)) repeat.Add(id);
        else queue.Enqueue(id, new Scan(this, id), WorkPriority.NearbyEdit);
    }
    public WorkReport Drain(int maximumSteps, TimeSpan budget) => queue.Drain(maximumSteps, budget);
    public void Clear()
    { queue.Clear(); active.Clear(); repeat.Clear(); center = null; activeSnapshot = Array.Empty<RegionId>(); }
    private sealed class Scan(DiscoveryWindow owner, RegionId region) : IIncrementalWork
    {
        private int index;
        public bool Step()
        {
            if (!owner.active.Contains(region)) return true;
            owner.sample(region, index++);
            if (index < Edge * Edge * Edge) return false;
            if (owner.repeat.Remove(region)) owner.queue.Enqueue(region, new Scan(owner, region), WorkPriority.NearbyEdit);
            return true;
        }
    }
}
