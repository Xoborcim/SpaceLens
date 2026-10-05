using SpaceLens.Core.Models;

namespace SpaceLens.Core.Visualization;

/// <summary>Per-folder statistics used to colour treemap cells by file type or by age.</summary>
public static class TreemapColoring
{
    /// <summary>Age bands from newest to oldest, with a sequential palette (dark = recent, light = old).</summary>
    public static IReadOnlyList<(string Label, int MaxAgeDays, uint Color)> AgeBands { get; } =
    [
        ("Last month", 31, 0xFF0B5394),
        ("Last 6 months", 183, 0xFF3D85C6),
        ("Last year", 366, 0xFF6FA8DC),
        ("Last 3 years", 1096, 0xFF9FC5E8),
        ("Older", int.MaxValue, 0xFFCFE2F3),
    ];

    public const uint UnknownColor = 0xFF8A8A8A;

    /// <summary>The colour of the age band for a FILETIME (UTC); <see cref="UnknownColor"/> when unknown.</summary>
    public static uint AgeColor(long lastWriteUtc, DateTime nowUtc)
    {
        if (lastWriteUtc <= 0)
        {
            return UnknownColor;
        }

        double days = (nowUtc - DateTime.FromFileTimeUtc(lastWriteUtc)).TotalDays;
        foreach (var band in AgeBands)
        {
            if (days <= band.MaxAgeDays)
            {
                return band.Color;
            }
        }

        return AgeBands[^1].Color;
    }

    /// <summary>
    /// The most recent change anywhere in a folder: the newest last-write time of the folder, its subfolders
    /// (a folder's time changes when entries are added, removed or renamed) and its indexed files.
    /// </summary>
    public static long NewestWriteUtc(ScanTree tree, int dirIndex)
    {
        long newest = Math.Max(tree.Dir(dirIndex).LastWriteUtc, NewestFile(tree, dirIndex));
        TreeWalker.Walk(tree, dirIndex, d =>
        {
            newest = Math.Max(newest, Math.Max(tree.Dir(d).LastWriteUtc, NewestFile(tree, d)));
            return true;
        });
        return newest;
    }

    /// <summary>
    /// The file category holding the most bytes among a folder's indexed files (files of at least the
    /// index threshold), or null when the folder has none.
    /// </summary>
    public static FileCategory? DominantCategory(ScanTree tree, int dirIndex)
    {
        var bytes = new long[FileCategoryInfo.Count];
        AddFiles(tree, dirIndex, bytes);
        TreeWalker.Walk(tree, dirIndex, d =>
        {
            AddFiles(tree, d, bytes);
            return true;
        });

        int best = -1;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] > 0 && (best < 0 || bytes[i] > bytes[best]))
            {
                best = i;
            }
        }

        return best < 0 ? null : (FileCategory)best;
    }

    private static long NewestFile(ScanTree tree, int dir)
    {
        long newest = 0;
        for (int f = tree.Dir(dir).FirstFile; f >= 0; f = tree.File(f).NextFile)
        {
            if (!tree.File(f).Removed)
            {
                newest = Math.Max(newest, tree.File(f).LastWriteUtc);
            }
        }

        return newest;
    }

    private static void AddFiles(ScanTree tree, int dir, long[] bytes)
    {
        for (int f = tree.Dir(dir).FirstFile; f >= 0; f = tree.File(f).NextFile)
        {
            ref var file = ref tree.File(f);
            if (!file.Removed)
            {
                bytes[(int)file.Category] += file.Size;
            }
        }
    }
}
