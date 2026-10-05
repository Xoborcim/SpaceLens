using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.Duplicates;

/// <summary>Files with identical contents. <see cref="Files"/> are file indices in the scan tree, sorted by path.</summary>
public sealed record DuplicateGroup(long Size, IReadOnlyList<int> Files)
{
    /// <summary>Space freed by keeping one copy and removing the others.</summary>
    public long Reclaimable => Size * (Files.Count - 1);
}

public sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, int UnreadableFiles, int CloudFilesSkipped)
{
    public long Reclaimable => Groups.Sum(g => g.Reclaimable);
}

public enum DuplicateStage
{
    Comparing,
    QuickCheck,
    FullCheck,
}

public readonly record struct DuplicateProgress(DuplicateStage Stage, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

public sealed class DuplicateOptions
{
    /// <summary>Smaller files are ignored. Only indexed files are known individually, so this is at least the index threshold.</summary>
    public long MinimumSize { get; init; } = 1L << 20;

    /// <summary>Folders whose files are never considered (for example C:\Windows, which is full of hard links).</summary>
    public IReadOnlyList<string> ExcludedFolders { get; init; } = [];

    /// <summary>Concurrent file reads. Two keeps an SSD busy without thrashing a hard disk too badly.</summary>
    public int Parallelism { get; init; } = 2;

    /// <summary>Bytes read from the start and from the end of each file for the quick check.</summary>
    public int QuickCheckBytes { get; init; } = 64 * 1024;

    /// <summary>Opens a file for reading. Replaceable for tests.</summary>
    public Func<string, Stream> OpenRead { get; init; } = DefaultOpenRead;

    /// <summary>
    /// Returns a key identifying the file on disk (volume and file ID), or null when unknown. Paths with the
    /// same key are hard links to one file: they use the space once and are not duplicates.
    /// </summary>
    public Func<string, string?>? FileIdentity { get; init; }

    public static Stream DefaultOpenRead(string path) =>
        new FileStream(PathUtil.ToLongPath(path), new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 0,
        });
}

/// <summary>
/// Finds files with identical contents among the indexed files of a scan.
/// <para>
/// Work is done in rounds that each discard as many candidates as possible before reading more data:
/// files are grouped by size; hard links are collapsed; the first and last
/// <see cref="DuplicateOptions.QuickCheckBytes"/> of each remaining file are hashed; only files that still
/// match are hashed completely (SHA-256). Files that cloud providers keep online only are never read,
/// because reading them would download them.
/// </para>
/// </summary>
public static class DuplicateFinder
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;
    private const int ReadBufferSize = 1 << 20;

    private readonly record struct Candidate(int Index, string Path, long Size);

    public static DuplicateResult Find(ScanTree tree, DuplicateOptions options, IProgress<DuplicateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var (sizeGroups, cloudSkipped) = CollectBySize(tree, options);
        int unreadable = 0;

        // Hard links: several paths, one file. Keep one path per file.
        if (options.FileIdentity is { } identity)
        {
            var collapsed = new List<List<Candidate>>();
            int done = 0, total = sizeGroups.Sum(g => g.Count);
            foreach (var group in sizeGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var kept = new List<Candidate>(group.Count);
                foreach (var candidate in group)
                {
                    string? key = identity(candidate.Path);
                    if (key is null || seen.Add(key))
                    {
                        kept.Add(candidate);
                    }
                }

                done += group.Count;
                progress?.Report(new DuplicateProgress(DuplicateStage.Comparing, 0, 0, done, total));
                if (kept.Count > 1)
                {
                    collapsed.Add(kept);
                }
            }

            sizeGroups = collapsed;
        }

        // Quick check: start and end of each file.
        var quick = HashAll(sizeGroups, options, DuplicateStage.QuickCheck, full: false, progress, ref unreadable, cancellationToken);

        // Full check for files whose quick hashes still match.
        var full = HashAll(quick, options, DuplicateStage.FullCheck, full: true, progress, ref unreadable, cancellationToken);

        var groups = full
            .Select(g => new DuplicateGroup(g[0].Size, g.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(c => c.Index).ToList()))
            .OrderByDescending(g => g.Reclaimable)
            .ToList();
        return new DuplicateResult(groups, unreadable, cloudSkipped);
    }

    private static (List<List<Candidate>> Groups, int CloudSkipped) CollectBySize(ScanTree tree, DuplicateOptions options)
    {
        long minimum = Math.Max(1, options.MinimumSize);
        var excluded = options.ExcludedFolders.Select(tree.FindDirectory).Where(i => i >= 0).ToList();
        var excludedCache = new Dictionary<int, bool>();
        var bySize = new Dictionary<long, List<Candidate>>();
        int cloudSkipped = 0;

        // Removals happen on the UI thread under this lock; copy what is needed and work on paths afterwards.
        lock (tree.SyncRoot)
        {
            int count = tree.FileRecordCount;
            for (int i = 0; i < count; i++)
            {
                if (!tree.IsLiveFile(i))
                {
                    continue;
                }

                ref var file = ref tree.File(i);
                if (file.Size < minimum || IsExcluded(tree, file.Directory, excluded, excludedCache))
                {
                    continue;
                }

                if ((file.Attributes & CloudOnly) != 0)
                {
                    cloudSkipped++;
                    continue;
                }

                if (!bySize.TryGetValue(file.Size, out var list))
                {
                    bySize[file.Size] = list = [];
                }

                list.Add(new Candidate(i, null!, file.Size));
            }

            var groups = bySize.Values.Where(g => g.Count > 1).ToList();
            foreach (var group in groups)
            {
                for (int k = 0; k < group.Count; k++)
                {
                    group[k] = group[k] with { Path = tree.GetFilePath(group[k].Index) };
                }
            }

            return (groups, cloudSkipped);
        }
    }

    private static bool IsExcluded(ScanTree tree, int dir, List<int> excluded, Dictionary<int, bool> cache)
    {
        if (excluded.Count == 0)
        {
            return false;
        }

        if (!cache.TryGetValue(dir, out bool result))
        {
            result = excluded.Any(e => tree.IsWithin(dir, e));
            cache[dir] = result;
        }

        return result;
    }

    /// <summary>Hashes every candidate and splits each group by hash, keeping only groups that still have two or more files.</summary>
    private static List<List<Candidate>> HashAll(List<List<Candidate>> groups, DuplicateOptions options, DuplicateStage stage, bool full,
        IProgress<DuplicateProgress>? progress, ref int unreadable, CancellationToken cancellationToken)
    {
        var candidates = groups.SelectMany(g => g).ToList();
        long bytesTotal = full ? candidates.Sum(c => c.Size) : candidates.Sum(c => Math.Min(c.Size, 2L * options.QuickCheckBytes));
        long bytesDone = 0;
        int filesDone = 0;
        int failed = 0;
        var hashes = new ConcurrentDictionary<int, string>();
        var clock = Stopwatch.StartNew();
        long lastReport = 0;

        void Report(bool force = false)
        {
            long now = clock.ElapsedMilliseconds;
            long last = Interlocked.Read(ref lastReport);
            if (progress is not null && (force || now - last >= 100) && Interlocked.CompareExchange(ref lastReport, now, last) == last)
            {
                progress.Report(new DuplicateProgress(stage, Interlocked.Read(ref bytesDone), bytesTotal, Volatile.Read(ref filesDone), candidates.Count));
            }
        }

        Report(force: true);
        Parallel.ForEach(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = cancellationToken },
            candidate =>
            {
                try
                {
                    string hash = full
                        ? HashFull(candidate.Path, options, read => { Interlocked.Add(ref bytesDone, read); Report(); }, cancellationToken)
                        : HashQuick(candidate.Path, options);
                    hashes[candidate.Index] = hash;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked, deleted since the scan, or access denied: it cannot be compared.
                    Interlocked.Increment(ref failed);
                }

                if (!full)
                {
                    Interlocked.Add(ref bytesDone, Math.Min(candidate.Size, 2L * options.QuickCheckBytes));
                }

                Interlocked.Increment(ref filesDone);
                Report();
            });
        Report(force: true);
        unreadable += failed;

        var result = new List<List<Candidate>>();
        foreach (var group in groups)
        {
            foreach (var same in group.Where(c => hashes.ContainsKey(c.Index)).GroupBy(c => hashes[c.Index], StringComparer.Ordinal))
            {
                var list = same.ToList();
                if (list.Count > 1)
                {
                    result.Add(list);
                }
            }
        }

        return result;
    }

    /// <summary>Hash of the length plus the first and last bytes of the file.</summary>
    private static string HashQuick(string path, DuplicateOptions options)
    {
        using var stream = options.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = stream.Length;
        sha.AppendData(BitConverter.GetBytes(length));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(options.QuickCheckBytes);
        try
        {
            AppendRange(stream, sha, buffer, 0, Math.Min(length, options.QuickCheckBytes));
            if (length > 2L * options.QuickCheckBytes)
            {
                AppendRange(stream, sha, buffer, length - options.QuickCheckBytes, options.QuickCheckBytes);
            }
            else if (length > options.QuickCheckBytes)
            {
                AppendRange(stream, sha, buffer, options.QuickCheckBytes, length - options.QuickCheckBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void AppendRange(Stream stream, IncrementalHash sha, byte[] buffer, long offset, long count)
    {
        stream.Position = offset;
        while (count > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
            {
                break;
            }

            sha.AppendData(buffer, 0, read);
            count -= read;
        }
    }

    private static string HashFull(string path, DuplicateOptions options, Action<int> onRead, CancellationToken cancellationToken)
    {
        using var stream = options.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(stream.Length));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, ReadBufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
                onRead(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
