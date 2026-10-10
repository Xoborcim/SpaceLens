using SpaceLens.Core.Models;
using SpaceLens.Core.Snapshots;

namespace SpaceLens.Desktop.Services;

/// <summary>One snapshot per scanned root, plus the previous scan of that root (for the Changes view).</summary>
public sealed class SnapshotStore(string dataDirectory)
{
    private readonly Lock _gate = new();

    private string Folder => Path.Combine(dataDirectory, "Snapshots");

    public string PathFor(string root)
    {
        string normalized = PathUtil.NormalizeDisplayPath(root);
        string safe = new string(normalized.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (safe.Length == 0)
        {
            safe = "root";
        }

        if (safe.Length > 80)
        {
            safe = safe[..40] + "_" + ((uint)normalized.GetHashCode(StringComparison.OrdinalIgnoreCase)).ToString("x8");
        }

        return Path.Combine(Folder, safe + ".slsnap");
    }

    public ScanTree? TryLoad(string root)
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

    /// <summary>Saves a completed scan; written in full before it replaces the current snapshot.</summary>
    public void Save(ScanTree tree)
    {
        lock (_gate)
        {
            try
            {
                SnapshotSerializer.SaveToFile(tree, PathFor(tree.RootPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
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
    }
}
