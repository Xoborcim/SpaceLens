namespace SpaceLens.Core.Models;

[Flags]
public enum NodeFlags : ushort
{
    None = 0,

    /// <summary>The directory's entries were enumerated.</summary>
    Scanned = 1 << 0,

    /// <summary>The directory could not be opened because access was denied.</summary>
    AccessDenied = 1 << 1,

    /// <summary>Enumeration failed or was incomplete for another reason.</summary>
    Error = 1 << 2,

    /// <summary>Junction, symbolic link or mount point. Not followed, to avoid loops and double counting.</summary>
    ReparsePoint = 1 << 3,

    /// <summary>Removed by the user after the scan (moved to Recycle Bin or deleted).</summary>
    Removed = 1 << 4,

    Hidden = 1 << 5,
    System = 1 << 6,

    /// <summary>
    /// Deliberately not entered (<see cref="Scanning.ScanOptions.ExcludedPaths"/>): another volume mounted
    /// inside the scanned one, or a second view of data counted elsewhere (macOS firmlinks).
    /// </summary>
    Excluded = 1 << 7,
}

/// <summary>
/// One directory in the scan tree. Children and indexed files are singly linked lists threaded
/// through the record arrays, so there are no per-directory collection objects.
/// </summary>
public struct DirNode
{
    public string Name;
    public int Parent;
    public int FirstChild;
    public int NextSibling;
    public int FirstFile;
    public int SubdirCount;
    public int OwnFileCount;

    /// <summary>Total size of the files directly inside this directory.</summary>
    public long OwnSize;

    /// <summary>Size of this directory including all descendants. Updated atomically during the scan.</summary>
    public long TotalSize;

    /// <summary>Number of files in this directory and all descendants.</summary>
    public long TotalFiles;

    /// <summary>Number of descendant directories.</summary>
    public int TotalDirs;

    public NodeFlags Flags;

    /// <summary>Last write time as a Windows FILETIME (UTC).</summary>
    public long LastWriteUtc;

    public readonly bool IsRemoved => (Flags & NodeFlags.Removed) != 0;
}

/// <summary>
/// A file that is large enough to be indexed individually (see <see cref="ScanTree.FileIndexThreshold"/>).
/// Smaller files are only counted in their directory's aggregates.
/// </summary>
public struct FileRecord
{
    public string Name;
    public int Directory;
    public int NextFile;
    public long Size;
    public long LastWriteUtc;
    public FileCategory Category;
    public bool Removed;
    public FileAttributes Attributes;
}

public readonly record struct ScanError(string Path, int ErrorCode, string Message);
