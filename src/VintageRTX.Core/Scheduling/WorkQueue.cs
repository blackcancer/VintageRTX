using System.Diagnostics;

namespace VintageRTX.Core.Scheduling;

public interface IIncrementalWork
{
    /// <summary>One bounded nonblocking unit, true only when this task is complete.</summary>
    bool Step();
}
public enum WorkPriority { NearbyEdit, Background }
public readonly record struct WorkReport(int Steps, int Remaining, TimeSpan Elapsed);

/// <summary>Coalesced owner-thread queue, with reserved background progress to avoid starvation.</summary>
public sealed class WorkQueue<TKey> where TKey:notnull
{
    private sealed record Item(TKey Key,IIncrementalWork Work,WorkPriority Priority);
    private readonly LinkedList<Item> urgent=new(), background=new();
    private readonly Dictionary<TKey,LinkedListNode<Item>> queued=new();
    private readonly int owner=Environment.CurrentManagedThreadId;
    private int urgentSinceBackground;
    public int Count=>queued.Count;
    private void AssertOwner()
    {
        if(Environment.CurrentManagedThreadId!=owner) throw new InvalidOperationException("Marshal work to the owner thread.");
    }
    public bool Contains(TKey key) { AssertOwner(); return queued.ContainsKey(key); }
    public bool Cancel(TKey key)
    {
        AssertOwner();
        if(!queued.Remove(key,out LinkedListNode<Item>? node)) return false;
        node.List!.Remove(node); return true;
    }
    public void Enqueue(TKey key,IIncrementalWork work,WorkPriority priority)
    {
        AssertOwner(); ArgumentNullException.ThrowIfNull(work);
        if(!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        Cancel(key);
        LinkedList<Item> list=priority==WorkPriority.NearbyEdit?urgent:background;
        queued[key]=list.AddLast(new Item(key,work,priority));
    }
    public void Clear() { AssertOwner(); urgent.Clear(); background.Clear(); queued.Clear(); urgentSinceBackground=0; }
    public WorkReport Drain(int maximumSteps,TimeSpan budget)
    {
        AssertOwner();
        if(maximumSteps<0 || budget<TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumSteps));
        long start=Stopwatch.GetTimestamp(); int steps=0;
        while(steps<maximumSteps && queued.Count>0 && Stopwatch.GetElapsedTime(start)<budget)
        {
            bool useBackground=background.Count>0 && (urgent.Count==0 || urgentSinceBackground>=7);
            LinkedList<Item> list=useBackground?background:urgent;
            LinkedListNode<Item> node=list.First!; list.RemoveFirst(); queued.Remove(node.Value.Key);
            if(useBackground) urgentSinceBackground=0; else urgentSinceBackground++;
            bool complete=node.Value.Work.Step(); steps++;
            // A step may explicitly enqueue a newer revision under its key; never overwrite it.
            if(!complete && !queued.ContainsKey(node.Value.Key))
            { list.AddLast(node); queued.Add(node.Value.Key,node); }
        }
        return new(steps,queued.Count,Stopwatch.GetElapsedTime(start));
    }
}
