using System.Diagnostics;

namespace VintageRTX.Core.Scheduling;

public interface IIncrementalWork
{
    /// <summary>One bounded nonblocking unit, true only when this task is complete.</summary>
    bool Step();
}
public enum WorkPriority { NearbyEdit, Background }
public readonly record struct WorkReport(int Steps, int Remaining, TimeSpan Elapsed);

/// <summary>Coalesced owner-thread queue with cancellable in-flight work and reserved background progress.</summary>
public sealed class WorkQueue<TKey> where TKey:notnull
{
    private sealed record Item(TKey Key, IIncrementalWork Work, WorkPriority Priority)
    { internal bool Cancelled; }
    private readonly LinkedList<Item> urgent = new(), background = new();
    private readonly Dictionary<TKey, LinkedListNode<Item>> queued = new();
    private readonly int owner = Environment.CurrentManagedThreadId;
    private LinkedListNode<Item>? executing;
    private bool draining;
    private int urgentSinceBackground;
    public int Count => queued.Count;
    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Marshal work to the owner thread.");
    }
    public bool Contains(TKey key)
    {
        AssertOwner();
        return queued.ContainsKey(key) || executing is not null && !executing.Value.Cancelled
            && EqualityComparer<TKey>.Default.Equals(executing.Value.Key, key);
    }
    public bool Cancel(TKey key)
    {
        AssertOwner();
        bool removed = queued.Remove(key, out LinkedListNode<Item>? node);
        if (removed) node!.List!.Remove(node);
        if (executing is not null && !executing.Value.Cancelled
            && EqualityComparer<TKey>.Default.Equals(executing.Value.Key, key))
        { executing.Value.Cancelled = true; removed = true; }
        return removed;
    }
    public void Enqueue(TKey key, IIncrementalWork work, WorkPriority priority)
    {
        AssertOwner(); ArgumentNullException.ThrowIfNull(work);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        Cancel(key);
        LinkedList<Item> list = priority == WorkPriority.NearbyEdit ? urgent : background;
        queued[key] = list.AddLast(new Item(key, work, priority));
    }
    public void Clear()
    {
        AssertOwner();
        if (executing is not null) executing.Value.Cancelled = true;
        urgent.Clear(); background.Clear(); queued.Clear(); urgentSinceBackground = 0;
    }
    public WorkReport Drain(int maximumSteps, TimeSpan budget)
    {
        AssertOwner();
        if (maximumSteps < 0 || budget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumSteps));
        if (draining) throw new InvalidOperationException("A work step cannot recursively drain its owner queue.");
        long start = Stopwatch.GetTimestamp(); int steps = 0; draining = true;
        try
        {
            while (steps < maximumSteps && queued.Count > 0 && Stopwatch.GetElapsedTime(start) < budget)
            {
                bool useBackground = background.Count > 0 && (urgent.Count == 0 || urgentSinceBackground >= 7);
                LinkedList<Item> list = useBackground ? background : urgent;
                LinkedListNode<Item> node = list.First!; list.RemoveFirst(); queued.Remove(node.Value.Key);
                urgentSinceBackground = useBackground ? 0 : Math.Min(urgentSinceBackground + 1, 7);
                executing = node;
                bool complete;
                try { complete = node.Value.Work.Step(); }
                finally { executing = null; }
                steps++;
                // Cancellation, Clear, or replacement during Step must not resurrect the old
                // world/revision after the callback returns. Newer queued work is untouched.
                if (!complete && !node.Value.Cancelled && !queued.ContainsKey(node.Value.Key))
                { list.AddLast(node); queued.Add(node.Value.Key, node); }
            }
            return new(steps, queued.Count, Stopwatch.GetElapsedTime(start));
        }
        finally { draining = false; }
    }
}
