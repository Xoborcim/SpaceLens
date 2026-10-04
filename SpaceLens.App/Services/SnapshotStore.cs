using SpaceLens.Core.Models;
using SpaceLens.Core.Snapshots;

namespace SpaceLens.App.Services;

/// <summary>One compact snapshot file per scanned root under %LOCALAPPDATA%\SpaceLens\Snapshots.</summary>
public static class SnapshotStore
{
    private static string Folder => Path.Combine(SettingsService.DataDirectory, "Snapshots");

    // Saves run on background threads (after a scan and after every removal). One at a time: each save
    // reads the tree when it starts, so whichever runs last writes the newest state.
    private static readonly Lock SaveGate = new();

    public static string PathFor(string root)
    {
        string normalized = PathUtil.NormalizeDisplayPath(root).ToUpperInvariant();
        var safe = new string(normalized.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (safe.Length > 80)
        {
            safe = safe[..40] + "_" + StableHash(normalized).ToString("x8");
        }

        return Path.Combine(Folder, safe + ".slsnap");
    }

    /// <summary>The scan before the current one, kept so the Changes page can compare the two.</summary>
    public static string PreviousPathFor(string root) => Path.ChangeExtension(PathFor(root), ".prev.slsnap");

    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }

    public static bool Exists(string root) => File.Exists(PathFor(root));

    public static ScanTree? TryLoad(string root)
    {
        try
        {
            string path = PathFor(root);
            return File.Exists(path) ? SnapshotSerializer.LoadFromFile(path) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    public static ScanTree? TryLoadPrevious(string root)
    {
        try
        {
            string path = PreviousPathFor(root);
            return File.Exists(path) ? SnapshotSerializer.LoadFromFile(path) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves a newly completed scan. The snapshot it replaces becomes the previous scan. Saves after
    /// removals use <see cref="Save"/> instead, which keeps the previous scan as it is.
    /// </summary>
    public static void SaveScan(ScanTree tree)
    {
        lock (SaveGate)
        {
            try
            {
                string current = PathFor(tree.RootPath);
                if (File.Exists(current))
                {
                    File.Move(current, PreviousPathFor(tree.RootPath), overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            SaveCore(tree);
        }
    }

    public static void Save(ScanTree tree)
    {
        lock (SaveGate)
        {
            SaveCore(tree);
        }
    }

    private static void SaveCore(ScanTree tree)
    {
        try
        {
            SnapshotSerializer.SaveToFile(tree, PathFor(tree.RootPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void DeleteAll()
    {
        lock (SaveGate)
        {
            DeleteAllCore();
        }
    }

    private static void DeleteAllCore()
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static long TotalSize()
    {
        try
        {
            return Directory.Exists(Folder) ? new DirectoryInfo(Folder).EnumerateFiles().Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
