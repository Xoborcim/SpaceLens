using SpaceLens.Core.Models;

namespace SpaceLens.Core.Scanning;

/// <summary>
/// A complete scanning strategy. Implementations fill a <see cref="ScanTree"/> incrementally so that
/// callers can display live results while the scan runs.
/// </summary>
public interface IDiskScanner
{
    string Name { get; }

    /// <summary>Checks whether this scanner can scan <paramref name="rootPath"/> in the current environment.</summary>
    bool IsSupported(string rootPath, out string? reason);

    Task<ScanResult> ScanAsync(ScanTree tree, ScanOptions options, PauseGate? pause, CancellationToken cancellationToken);
}

/// <summary>
/// Lists the immediate entries of a single directory. Instances are created per worker thread
/// (see <see cref="IDirectoryEnumeratorFactory"/>) so they can own reusable native buffers.
/// </summary>
public interface IDirectoryEnumerator : IDisposable
{
    /// <summary>
    /// Invokes <paramref name="sink"/> for every entry except "." and "..".
    /// </summary>
    /// <param name="path">Full directory path, normally in <c>\\?\</c> form.</param>
    /// <returns>0 on success, otherwise a Win32 error code. Entries reported before a failure are still valid.</returns>
    int Enumerate(string path, IDirectoryEntrySink sink);
}

public interface IDirectoryEnumeratorFactory
{
    string Name { get; }

    /// <summary>True if the enumerator reports reparse tags, allowing cloud placeholders to be traversed while junctions are skipped.</summary>
    bool ReportsReparseTags { get; }

    IDirectoryEnumerator Create();
}

public interface IDirectoryEntrySink
{
    void OnEntry(in RawDirectoryEntry entry);
}

/// <summary>A directory entry as reported by the OS, valid only for the duration of the callback.</summary>
public readonly ref struct RawDirectoryEntry
{
    public RawDirectoryEntry(ReadOnlySpan<char> name, FileAttributes attributes, long length, long allocatedSize, long lastWriteUtc, uint reparseTag)
    {
        Name = name;
        Attributes = attributes;
        Length = length;
        AllocatedSize = allocatedSize;
        LastWriteUtc = lastWriteUtc;
        ReparseTag = reparseTag;
    }

    public ReadOnlySpan<char> Name { get; }

    public FileAttributes Attributes { get; }

    /// <summary>Logical file size (end of file).</summary>
    public long Length { get; }

    /// <summary>Bytes allocated on disk, or -1 if the enumerator cannot report it.</summary>
    public long AllocatedSize { get; }

    /// <summary>Last write time as FILETIME (UTC).</summary>
    public long LastWriteUtc { get; }

    public uint ReparseTag { get; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

public sealed class ScanOptions
{
    /// <summary>Number of worker threads. 0 selects a default based on the processor count.</summary>
    public int MaxParallelism { get; init; }

    /// <summary>
    /// Count allocated (on-disk) size when available. This makes OneDrive placeholders, sparse and
    /// compressed files reflect the space they really consume.
    /// </summary>
    public bool UseAllocatedSize { get; init; } = true;

    /// <summary>
    /// Folders (display paths) that are recorded but never entered. On macOS, scanning <c>/</c> must skip
    /// <c>/System/Volumes</c> (the data volume again, through firmlinks) and <c>/Volumes</c> (other disks),
    /// the way <c>du -x</c> stays on one file system.
    /// </summary>
    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = [];

    /// <summary>Run workers at below-normal priority so the UI stays responsive.</summary>
    public bool LowPriorityThreads { get; init; } = true;

    public int ResolveParallelism() =>
        MaxParallelism > 0 ? MaxParallelism : Math.Clamp(Environment.ProcessorCount, 2, 16);
}

public sealed record ScanResult(
    bool Completed,
    bool Cancelled,
    TimeSpan Duration,
    long Files,
    long Directories,
    long Bytes,
    int Errors,
    int Workers,
    string Scanner)
{
    public double FilesPerSecond => Duration.TotalSeconds > 0 ? Files / Duration.TotalSeconds : 0;

    public double DirectoriesPerSecond => Duration.TotalSeconds > 0 ? Directories / Duration.TotalSeconds : 0;

    public double BytesPerSecond => Duration.TotalSeconds > 0 ? Bytes / Duration.TotalSeconds : 0;
}

/// <summary>Cooperative pause switch checked by workers between directories.</summary>
public sealed class PauseGate
{
    private readonly ManualResetEventSlim _running = new(initialState: true);

    public bool IsPaused => !_running.IsSet;

    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    public void WaitIfPaused(CancellationToken cancellationToken)
    {
        if (!_running.IsSet)
        {
            _running.Wait(cancellationToken);
        }
    }
}
