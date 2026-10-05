using SpaceLens.Core.Models;

namespace SpaceLens.Core.Aggregation;

public enum BreakdownItemKind
{
    Directory,
    File,

    /// <summary>The small (non-indexed) files directly inside a directory, shown as one item.</summary>
    LooseFiles,
}

public readonly record struct BreakdownItem(BreakdownItemKind Kind, int Index, long Size);

/// <summary>
/// Produces a short, disjoint list of the largest meaningful items on a drive.
/// <para>
/// The algorithm repeatedly takes the largest remaining item and either reports it or replaces it with
/// its children. An item is expanded when it is a known container (the drive root, "Users", a user
/// profile, "Program Files", "AppData", "steamapps\common", ...) or when it merely passes most of its
/// size through to a single child. The result covers the drive without overlaps, so the sizes add up and
/// "C:\Users" never hides "Downloads".
/// </para>
/// </summary>
public static class Breakdown
{
    private static readonly HashSet<string> ContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Users", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "Local", "LocalLow", "Roaming",
        "steamapps", "common", "SteamLibrary", "Steam", "XboxGames", "Epic Games", "GOG Games", "Games",
        "Packages", "Programs", "Microsoft", "Google", "Mozilla", "WindowsApps", "Riot Games", "EA Games",
    };

    /// <param name="isUnit">Optional predicate for directories that must be reported as a whole (e.g. a detected game).</param>
    public static List<BreakdownItem> Build(ScanTree tree, int start = ScanTree.RootIndex, int maxItems = 40, Func<int, bool>? isUnit = null)
    {
        var results = new List<BreakdownItem>(maxItems);
        long total = tree.Dir(start).TotalSize;
        if (total <= 0)
        {
            return results;
        }

        long minimum = Math.Max(1L << 20, total / 2000);
        var queue = new PriorityQueue<BreakdownItem, long>(Comparer<long>.Create(static (a, b) => b.CompareTo(a)));
        Expand(tree, start, queue, minimum);

        while (queue.Count > 0 && results.Count < maxItems)
        {
            var item = queue.Dequeue();
            if (item.Kind == BreakdownItemKind.Directory && ShouldExpand(tree, item.Index, start, isUnit))
            {
                Expand(tree, item.Index, queue, minimum);
                continue;
            }

            results.Add(item);
        }

        return results;
    }

    private static bool ShouldExpand(ScanTree tree, int index, int start, Func<int, bool>? isUnit)
    {
        ref var node = ref tree.Dir(index);
        if (node.SubdirCount == 0 || (node.Flags & NodeFlags.ReparsePoint) != 0)
        {
            return false;
        }

        if (isUnit?.Invoke(index) == true)
        {
            return false;
        }

        if (ContainerNames.Contains(node.Name))
        {
            return true;
        }

        // A user profile: direct child of "Users" at the drive root.
        if (node.Parent >= 0 && tree.Dir(node.Parent).Name.Equals("Users", StringComparison.OrdinalIgnoreCase) &&
            tree.Dir(node.Parent).Parent == ScanTree.RootIndex)
        {
            return true;
        }

        // Pass-through directory near the top (e.g. D:\Backup holding only D:\Backup\2024): one child
        // holds most of the size. Deeper down, the parent is the more meaningful unit
        // ("Some Game" rather than "Some Game\data").
        if (tree.GetDepth(index) - tree.GetDepth(start) > 1)
        {
            return false;
        }

        long largestChild = 0;
        for (int c = node.FirstChild; c >= 0; c = tree.Dir(c).NextSibling)
        {
            if (!tree.Dir(c).IsRemoved)
            {
                largestChild = Math.Max(largestChild, tree.Dir(c).TotalSize);
            }
        }

        return node.TotalSize > 0 && largestChild >= node.TotalSize * 0.8;
    }

    private static void Expand(ScanTree tree, int index, PriorityQueue<BreakdownItem, long> queue, long minimum)
    {
        ref var node = ref tree.Dir(index);
        for (int c = Volatile.Read(ref node.FirstChild); c >= 0; c = tree.Dir(c).NextSibling)
        {
            ref var child = ref tree.Dir(c);
            if (!child.IsRemoved && child.TotalSize >= minimum)
            {
                queue.Enqueue(new BreakdownItem(BreakdownItemKind.Directory, c, child.TotalSize), child.TotalSize);
            }
        }

        // Large individual files are listed on their own; the remainder is grouped.
        long loose = node.OwnSize;
        for (int f = node.FirstFile; f >= 0; f = tree.File(f).NextFile)
        {
            ref var file = ref tree.File(f);
            if (!file.Removed && file.Size >= minimum)
            {
                queue.Enqueue(new BreakdownItem(BreakdownItemKind.File, f, file.Size), file.Size);
                loose -= file.Size;
            }
        }

        if (loose >= minimum)
        {
            queue.Enqueue(new BreakdownItem(BreakdownItemKind.LooseFiles, index, loose), loose);
        }
    }

    /// <summary>Returns the children of a directory sorted by size, largest first.</summary>
    public static List<int> SortedChildren(ScanTree tree, int dirIndex)
    {
        var children = tree.GetChildren(dirIndex);
        children.Sort((a, b) => tree.Dir(b).TotalSize.CompareTo(tree.Dir(a).TotalSize));
        return children;
    }

    public static List<int> SortedFiles(ScanTree tree, int dirIndex)
    {
        var files = tree.GetFiles(dirIndex);
        files.Sort((a, b) => tree.File(b).Size.CompareTo(tree.File(a).Size));
        return files;
    }

    /// <summary>All indexed files at least <paramref name="minimumSize"/> bytes, sorted descending.</summary>
    /// <param name="modifiedBefore">Only files last modified before this FILETIME (UTC); files with an unknown date are excluded.</param>
    public static List<int> LargeFiles(ScanTree tree, long minimumSize, FileCategory? category = null, int maxResults = 5000, long modifiedBefore = long.MaxValue)
    {
        var top = new TopN<int>(maxResults);
        int count = tree.FileRecordCount;
        for (int i = 0; i < count; i++)
        {
            if (!tree.IsLiveFile(i))
            {
                continue;
            }

            ref var file = ref tree.File(i);
            if (file.Size >= minimumSize && (category is null || file.Category == category) &&
                (modifiedBefore == long.MaxValue || file.LastWriteUtc > 0 && file.LastWriteUtc < modifiedBefore))
            {
                top.Offer(file.Size, i);
            }
        }

        return top.ToSortedList().ConvertAll(static x => x.Value);
    }
}
