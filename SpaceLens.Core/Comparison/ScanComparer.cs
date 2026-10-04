using SpaceLens.Core.Models;

namespace SpaceLens.Core.Comparison;

public enum ChangeKind
{
    Grew,
    Shrank,

    /// <summary>The folder did not exist in the previous scan.</summary>
    Added,

    /// <summary>The folder no longer exists.</summary>
    Removed,
}

/// <summary>
/// One entry of a comparison: a folder (or the loose files directly inside a folder) whose size changed.
/// </summary>
/// <param name="CurrentIndex">Directory index in the current tree, or -1 for removed folders.</param>
/// <param name="IsLooseFiles">The files directly inside <paramref name="Path"/>, not the folder as a whole.</param>
public sealed record ScanChange(ChangeKind Kind, string Path, long OldSize, long NewSize, int CurrentIndex, bool IsLooseFiles = false)
{
    public long Delta => NewSize - OldSize;
}

public sealed record ScanComparison(
    DateTime PreviousScanUtc,
    DateTime CurrentScanUtc,
    long PreviousTotal,
    long CurrentTotal,
    IReadOnlyList<ScanChange> Changes)
{
    public long NetChange => CurrentTotal - PreviousTotal;

    public long Grown => Changes.Where(c => c.Delta > 0).Sum(c => c.Delta);

    public long Freed => -Changes.Where(c => c.Delta < 0).Sum(c => c.Delta);
}

/// <summary>
/// Compares two scans of the same root and explains the difference with a short, disjoint list of the
/// folders responsible, the way <see cref="Aggregation.Breakdown"/> explains a drive's contents.
/// <para>
/// Folders are matched by name (case-insensitive) level by level, starting at the root. The largest
/// change is repeatedly taken from a queue and either reported or replaced by the changes of its
/// children. A folder is replaced by its children when one child accounts for most of its change (so
/// "C:\Users\me" growing becomes "Downloads" growing), and near the top of the tree also when its
/// children together account for most of it. New and removed folders are reported as a whole.
/// </para>
/// </summary>
public static class ScanComparer
{
    private readonly record struct Pair(int Old, int New, long OldSize, long NewSize, int Depth)
    {
        public long Delta => NewSize - OldSize;
    }

    public static ScanComparison Compare(ScanTree previous, ScanTree current, int maxItems = 50, CancellationToken cancellationToken = default)
    {
        long oldTotal = previous.Root.TotalSize;
        long newTotal = current.Root.TotalSize;
        long minimum = Math.Max(1L << 20, Math.Max(oldTotal, newTotal) / 5000);

        var changes = new List<ScanChange>();
        var queue = new PriorityQueue<Pair, long>();

        // The root is always expanded: its total can stay the same while data moves between folders.
        var root = new Pair(ScanTree.RootIndex, ScanTree.RootIndex, oldTotal, newTotal, 0);
        Expand(previous, current, root, MatchChildren(previous, current, root), queue, changes, minimum);

        while (queue.Count > 0 && changes.Count < maxItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = queue.Dequeue();
            if (Math.Abs(pair.Delta) < minimum)
            {
                // Everything left in the queue is smaller.
                break;
            }

            if (pair.Old >= 0 && pair.New >= 0)
            {
                var children = MatchChildren(previous, current, pair);
                if (ShouldExpand(pair, children, minimum))
                {
                    Expand(previous, current, pair, children, queue, changes, minimum);
                    continue;
                }
            }

            changes.Add(ToChange(previous, current, pair));
        }

        changes.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        return new ScanComparison(
            previous.Metadata.CompletedUtc ?? previous.Metadata.StartedUtc,
            current.Metadata.CompletedUtc ?? current.Metadata.StartedUtc,
            oldTotal, newTotal, changes);
    }

    /// <summary>Queues the children of <paramref name="pair"/> and reports the change of its loose files.</summary>
    private static void Expand(ScanTree previous, ScanTree current, Pair pair, List<Pair> children,
        PriorityQueue<Pair, long> queue, List<ScanChange> changes, long minimum)
    {
        foreach (var child in children)
        {
            Enqueue(queue, child);
        }

        long oldLoose = previous.Dir(pair.Old).OwnSize;
        long newLoose = current.Dir(pair.New).OwnSize;
        if (Math.Abs(newLoose - oldLoose) >= minimum)
        {
            changes.Add(new ScanChange(newLoose > oldLoose ? ChangeKind.Grew : ChangeKind.Shrank,
                current.GetPath(pair.New), oldLoose, newLoose, pair.New, IsLooseFiles: true));
        }
    }

    private static void Enqueue(PriorityQueue<Pair, long> queue, Pair pair)
    {
        if (pair.Delta != 0)
        {
            queue.Enqueue(pair, -Math.Abs(pair.Delta));
        }
    }

    private static bool ShouldExpand(Pair pair, List<Pair> children, long minimum)
    {
        long delta = pair.Delta;
        long sameSign = 0;
        long largest = 0;
        foreach (var child in children)
        {
            long d = child.Delta;
            if (Math.Sign(d) == Math.Sign(delta) && Math.Abs(d) >= minimum)
            {
                sameSign += Math.Abs(d);
                largest = Math.Max(largest, Math.Abs(d));
            }
        }

        // One child explains most of the change: the change is really in there.
        if (largest >= Math.Abs(delta) * 0.6)
        {
            return true;
        }

        // Near the top (drive root, Users, a profile) a change spread over several folders is more
        // useful as those folders than as "C:\ grew".
        return pair.Depth <= 2 && sameSign >= Math.Abs(delta) * 0.5;
    }

    private static List<Pair> MatchChildren(ScanTree previous, ScanTree current, Pair pair)
    {
        var oldChildren = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (int c in previous.GetChildren(pair.Old))
        {
            oldChildren.TryAdd(previous.Dir(c).Name, c);
        }

        var result = new List<Pair>();
        foreach (int c in current.GetChildren(pair.New))
        {
            ref var node = ref current.Dir(c);
            if (oldChildren.Remove(node.Name, out int old))
            {
                result.Add(new Pair(old, c, previous.Dir(old).TotalSize, node.TotalSize, pair.Depth + 1));
            }
            else
            {
                result.Add(new Pair(-1, c, 0, node.TotalSize, pair.Depth + 1));
            }
        }

        foreach (int old in oldChildren.Values)
        {
            result.Add(new Pair(old, -1, previous.Dir(old).TotalSize, 0, pair.Depth + 1));
        }

        return result;
    }

    private static ScanChange ToChange(ScanTree previous, ScanTree current, Pair pair)
    {
        if (pair.New < 0)
        {
            return new ScanChange(ChangeKind.Removed, previous.GetPath(pair.Old), pair.OldSize, 0, -1);
        }

        var kind = pair.Old < 0 ? ChangeKind.Added : pair.Delta > 0 ? ChangeKind.Grew : ChangeKind.Shrank;
        return new ScanChange(kind, current.GetPath(pair.New), pair.OldSize, pair.NewSize, pair.New);
    }
}
