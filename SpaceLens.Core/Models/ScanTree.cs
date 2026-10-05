using System.Collections.Concurrent;

namespace SpaceLens.Core.Models;

/// <summary>
/// Compact in-memory representation of a scanned directory hierarchy.
/// <para>
/// Every directory is a <see cref="DirNode"/> struct; files are only stored individually when they are at
/// least <see cref="FileIndexThreshold"/> bytes. Smaller files are folded into per-directory and
/// per-category counters. This keeps memory roughly proportional to the number of directories rather
/// than the number of files.
/// </para>
/// <para>
/// Threading model: during a scan, each directory is written by exactly one worker (the one that
/// enumerates its parent creates it, the one that enumerates it fills in its contents). Aggregates on
/// ancestors are updated with interlocked adds, so readers on other threads (the UI) can observe live,
/// monotonically growing totals at any time. Removal APIs must only be called after the scan finished.
/// </para>
/// </summary>
public sealed class ScanTree
{
    public const int RootIndex = 0;
    public const long DefaultFileIndexThreshold = 1L << 20; // 1 MB

    private readonly ChunkedArray<DirNode> _dirs = new();
    private readonly ChunkedArray<FileRecord> _files = new();
    private readonly ConcurrentQueue<ScanError> _errors = new();
    private const int MaxStoredErrors = 20_000;

    private long _filesScanned;
    private long _directoriesScanned;
    private long _bytesScanned;
    private int _errorCount;
    private string? _currentPath;

    public ScanTree(string rootPath, long fileIndexThreshold = DefaultFileIndexThreshold)
    {
        RootPath = PathUtil.NormalizeDisplayPath(rootPath);
        FileIndexThreshold = fileIndexThreshold;
        int root = _dirs.Allocate();
        ref var node = ref _dirs[root];
        node.Name = RootPath;
        node.Parent = -1;
        node.FirstChild = -1;
        node.NextSibling = -1;
        node.FirstFile = -1;
    }

    /// <summary>Display path of the scanned root, e.g. <c>C:\</c> or <c>D:\Projects</c>.</summary>
    public string RootPath { get; }

    public long FileIndexThreshold { get; }

    public ScanMetadata Metadata { get; } = new();

    public long[] CategoryBytes { get; } = new long[FileCategoryInfo.Count];

    public long[] CategoryCounts { get; } = new long[FileCategoryInfo.Count];

    public int DirectoryCount => _dirs.Count;

    public int FileRecordCount => _files.Count;

    public long FilesScanned => Interlocked.Read(ref _filesScanned);

    public long DirectoriesScanned => Interlocked.Read(ref _directoriesScanned);

    public long BytesScanned => Interlocked.Read(ref _bytesScanned);

    public int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>
    /// Taken by removals and by readers that need a consistent view of a finished tree (snapshot saves),
    /// so a save never sees a half-applied removal.
    /// </summary>
    public Lock SyncRoot { get; } = new();

    public IReadOnlyCollection<ScanError> Errors => _errors;

    /// <summary>The directory most recently started by a worker (for progress display).</summary>
    public string? CurrentPath
    {
        get => PathUtil.StripLongPathPrefix(Volatile.Read(ref _currentPath));
        set => Volatile.Write(ref _currentPath, value);
    }

    public ref DirNode Dir(int index) => ref _dirs[index];

    public ref FileRecord File(int index) => ref _files[index];

    public ref DirNode Root => ref _dirs[RootIndex];

    /// <summary>True when the record is fully published and not removed (safe to read during a scan).</summary>
    public bool IsLiveDirectory(int index) =>
        index >= 0 && index < _dirs.Count && _dirs.IsMaterialized(index) && _dirs[index].Name is not null && !_dirs[index].IsRemoved;

    public bool IsLiveFile(int index) =>
        index >= 0 && index < _files.Count && _files.IsMaterialized(index) && _files[index].Name is not null && !_files[index].Removed;

    /// <summary>Approximate managed memory used by the record arrays (excluding strings).</summary>
    public long ApproximateRecordBytes =>
        _dirs.ApproximateBytes(System.Runtime.CompilerServices.Unsafe.SizeOf<DirNode>()) +
        _files.ApproximateBytes(System.Runtime.CompilerServices.Unsafe.SizeOf<FileRecord>());

    // ----------------------------------------------------------------------------------------------
    // Construction (scanner side)
    // ----------------------------------------------------------------------------------------------

    /// <summary>Adds a child directory. Must be called by the single writer of <paramref name="parent"/>.</summary>
    public int AddDirectory(int parent, string name, NodeFlags flags = NodeFlags.None, long lastWriteUtc = 0)
    {
        int index = _dirs.Allocate();
        ref var node = ref _dirs[index];
        node.Parent = parent;
        node.FirstChild = -1;
        node.FirstFile = -1;
        node.Flags = flags;
        node.LastWriteUtc = lastWriteUtc;

        ref var parentNode = ref _dirs[parent];
        node.NextSibling = parentNode.FirstChild;
        Volatile.Write(ref node.Name, name);
        Volatile.Write(ref parentNode.FirstChild, index);
        parentNode.SubdirCount++;
        return index;
    }

    /// <summary>Adds an indexed file. Must be called by the single writer of <paramref name="directory"/>.</summary>
    public int AddFile(int directory, string name, long size, FileCategory category, long lastWriteUtc, FileAttributes attributes = 0)
    {
        int index = _files.Allocate();
        ref var file = ref _files[index];
        file.Directory = directory;
        file.Size = size;
        file.Category = category;
        file.LastWriteUtc = lastWriteUtc;
        file.Attributes = attributes;

        ref var dir = ref _dirs[directory];
        file.NextFile = dir.FirstFile;
        Volatile.Write(ref file.Name, name);
        Volatile.Write(ref dir.FirstFile, index);
        return index;
    }

    /// <summary>
    /// Records the direct contents of a directory and propagates its totals to all ancestors.
    /// </summary>
    public void CompleteDirectory(int index, long ownSize, int ownFiles, int newSubdirs, NodeFlags extraFlags = NodeFlags.None)
    {
        ref var node = ref _dirs[index];
        node.OwnSize = ownSize;
        node.OwnFileCount = ownFiles;
        node.Flags |= NodeFlags.Scanned | extraFlags;

        if (ownSize != 0 || ownFiles != 0 || newSubdirs != 0)
        {
            for (int p = index; p >= 0; p = _dirs[p].Parent)
            {
                ref var ancestor = ref _dirs[p];
                if (ownSize != 0)
                {
                    Interlocked.Add(ref ancestor.TotalSize, ownSize);
                }

                if (ownFiles != 0)
                {
                    Interlocked.Add(ref ancestor.TotalFiles, ownFiles);
                }

                if (newSubdirs != 0)
                {
                    Interlocked.Add(ref ancestor.TotalDirs, newSubdirs);
                }
            }
        }

        Interlocked.Add(ref _filesScanned, ownFiles);
        Interlocked.Add(ref _bytesScanned, ownSize);
        Interlocked.Increment(ref _directoriesScanned);
    }

    public void AddCategoryTotals(ReadOnlySpan<long> bytes, ReadOnlySpan<long> counts)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (counts[i] != 0)
            {
                Interlocked.Add(ref CategoryBytes[i], bytes[i]);
                Interlocked.Add(ref CategoryCounts[i], counts[i]);
            }
        }
    }

    public void MarkDirectory(int index, NodeFlags flags)
    {
        ref var node = ref _dirs[index];
        node.Flags |= flags;
    }

    public void RecordError(string path, int errorCode, string message)
    {
        Interlocked.Increment(ref _errorCount);
        if (_errors.Count < MaxStoredErrors)
        {
            _errors.Enqueue(new ScanError(PathUtil.StripLongPathPrefix(path) ?? path, errorCode, message));
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Queries
    // ----------------------------------------------------------------------------------------------

    public string GetPath(int dirIndex)
    {
        if (dirIndex == RootIndex)
        {
            return RootPath;
        }

        Span<int> chain = stackalloc int[256];
        int depth = 0;
        int totalLength = RootPath.Length;
        List<int>? overflow = null;
        for (int p = dirIndex; p != RootIndex && p >= 0; p = _dirs[p].Parent)
        {
            if (depth < chain.Length)
            {
                chain[depth] = p;
            }
            else
            {
                (overflow ??= []).Add(p);
            }

            depth++;
            totalLength += _dirs[p].Name.Length + 1;
        }

        var builder = new System.Text.StringBuilder(totalLength);
        builder.Append(RootPath);
        bool needsSeparator = !PathUtil.EndsWithSeparator(RootPath);
        for (int i = depth - 1; i >= 0; i--)
        {
            int node = i < chain.Length ? chain[i] : overflow![i - chain.Length];
            if (needsSeparator)
            {
                builder.Append('\\');
            }

            builder.Append(_dirs[node].Name);
            needsSeparator = true;
        }

        return builder.ToString();
    }

    public string GetFilePath(int fileIndex)
    {
        ref var file = ref _files[fileIndex];
        string dir = GetPath(file.Directory);
        return PathUtil.EndsWithSeparator(dir) ? dir + file.Name : dir + "\\" + file.Name;
    }

    public int GetDepth(int dirIndex)
    {
        int depth = 0;
        for (int p = dirIndex; p > RootIndex; p = _dirs[p].Parent)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>Returns true if <paramref name="ancestor"/> is <paramref name="dirIndex"/> or one of its ancestors.</summary>
    public bool IsWithin(int dirIndex, int ancestor)
    {
        for (int p = dirIndex; p >= 0; p = _dirs[p].Parent)
        {
            if (p == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the directory with the given display path, or -1.</summary>
    public int FindDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return -1;
        }

        string normalized = PathUtil.NormalizeDisplayPath(path);
        if (normalized.Equals(RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return RootIndex;
        }

        string rootWithSeparator = PathUtil.EndsWithSeparator(RootPath) ? RootPath : RootPath + "\\";
        if (!normalized.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        int current = RootIndex;
        foreach (var segment in normalized[rootWithSeparator.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = FindChild(current, segment);
            if (current < 0)
            {
                return -1;
            }
        }

        return current;
    }

    public int FindChild(int dirIndex, string name)
    {
        for (int c = Volatile.Read(ref _dirs[dirIndex].FirstChild); c >= 0; c = _dirs[c].NextSibling)
        {
            if (!_dirs[c].IsRemoved && string.Equals(_dirs[c].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return -1;
    }

    public List<int> GetChildren(int dirIndex)
    {
        var list = new List<int>(Math.Max(0, _dirs[dirIndex].SubdirCount));
        for (int c = Volatile.Read(ref _dirs[dirIndex].FirstChild); c >= 0; c = _dirs[c].NextSibling)
        {
            if (!_dirs[c].IsRemoved)
            {
                list.Add(c);
            }
        }

        return list;
    }

    public List<int> GetFiles(int dirIndex)
    {
        var list = new List<int>();
        for (int f = Volatile.Read(ref _dirs[dirIndex].FirstFile); f >= 0; f = _files[f].NextFile)
        {
            if (!_files[f].Removed)
            {
                list.Add(f);
            }
        }

        return list;
    }

    /// <summary>Size of the files directly in a directory that were too small to be indexed.</summary>
    public long GetUnindexedOwnSize(int dirIndex, out int unindexedCount)
    {
        long size = _dirs[dirIndex].OwnSize;
        int count = _dirs[dirIndex].OwnFileCount;
        for (int f = _dirs[dirIndex].FirstFile; f >= 0; f = _files[f].NextFile)
        {
            if (!_files[f].Removed)
            {
                size -= _files[f].Size;
                count--;
            }
        }

        unindexedCount = Math.Max(0, count);
        return Math.Max(0, size);
    }

    // ----------------------------------------------------------------------------------------------
    // Mutation after the scan (deletions performed through SpaceLens)
    // ----------------------------------------------------------------------------------------------

    public void RemoveFile(int fileIndex)
    {
        lock (SyncRoot)
        {
            RemoveFileCore(fileIndex);
        }
    }

    private void RemoveFileCore(int fileIndex)
    {
        ref var file = ref _files[fileIndex];
        if (file.Removed)
        {
            return;
        }

        file.Removed = true;
        ref var dir = ref _dirs[file.Directory];
        dir.OwnSize -= file.Size;
        dir.OwnFileCount--;
        for (int p = file.Directory; p >= 0; p = _dirs[p].Parent)
        {
            _dirs[p].TotalSize -= file.Size;
            _dirs[p].TotalFiles--;
        }

        CategoryBytes[(int)file.Category] -= file.Size;
        CategoryCounts[(int)file.Category]--;
        Unlink(ref dir.FirstFile, fileIndex, static (tree, i) => ref tree._files[i].NextFile);
    }

    public void RemoveDirectory(int dirIndex)
    {
        if (dirIndex == RootIndex)
        {
            throw new InvalidOperationException("The scan root cannot be removed.");
        }

        lock (SyncRoot)
        {
            RemoveDirectoryCore(dirIndex);
        }
    }

    private void RemoveDirectoryCore(int dirIndex)
    {
        ref var node = ref _dirs[dirIndex];
        if (node.IsRemoved)
        {
            return;
        }

        long size = node.TotalSize;
        long files = node.TotalFiles;
        int dirs = node.TotalDirs + 1;

        // Mark the subtree removed so flat iteration (search, detectors) skips it, and subtract the
        // known category contribution of indexed files.
        long indexedBytes = 0;
        long indexedFiles = 0;
        var stack = new Stack<int>();
        stack.Push(dirIndex);
        while (stack.Count > 0)
        {
            int d = stack.Pop();
            _dirs[d].Flags |= NodeFlags.Removed;
            for (int f = _dirs[d].FirstFile; f >= 0; f = _files[f].NextFile)
            {
                ref var file = ref _files[f];
                if (!file.Removed)
                {
                    file.Removed = true;
                    CategoryBytes[(int)file.Category] -= file.Size;
                    CategoryCounts[(int)file.Category]--;
                    indexedBytes += file.Size;
                    indexedFiles++;
                }
            }

            for (int c = _dirs[d].FirstChild; c >= 0; c = _dirs[c].NextSibling)
            {
                stack.Push(c);
            }
        }

        SubtractUnindexed(size - indexedBytes, files - indexedFiles);

        int parent = node.Parent;
        _dirs[parent].SubdirCount--;
        for (int p = parent; p >= 0; p = _dirs[p].Parent)
        {
            _dirs[p].TotalSize -= size;
            _dirs[p].TotalFiles -= files;
            _dirs[p].TotalDirs -= dirs;
        }

        Unlink(ref _dirs[parent].FirstChild, dirIndex, static (tree, i) => ref tree._dirs[i].NextSibling);
    }

    /// <summary>
    /// Removes the small (unindexed) files of a removed folder from the category totals. Their types
    /// were never recorded, so the amount is spread over the categories in proportion to the small
    /// files that remain in the tree. Per category this is an estimate, but the category totals keep
    /// adding up to the size of the tree instead of overstating it after every removal.
    /// </summary>
    private void SubtractUnindexed(long bytes, long files)
    {
        if (bytes <= 0 && files <= 0)
        {
            return;
        }

        // What each category holds in small files: its total minus its live indexed files.
        var smallBytes = (long[])CategoryBytes.Clone();
        var smallCounts = (long[])CategoryCounts.Clone();
        int fileCount = _files.Count;
        for (int i = 0; i < fileCount; i++)
        {
            ref var f = ref _files[i];
            if (!f.Removed)
            {
                smallBytes[(int)f.Category] -= f.Size;
                smallCounts[(int)f.Category]--;
            }
        }

        SubtractProportionally(CategoryBytes, smallBytes, bytes);
        SubtractProportionally(CategoryCounts, smallCounts, files);
    }

    private static void SubtractProportionally(long[] totals, long[] weights, long amount)
    {
        double weightSum = 0;
        int largest = -1;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] > 0)
            {
                weightSum += weights[i];
                if (largest < 0 || weights[i] > weights[largest])
                {
                    largest = i;
                }
            }
        }

        if (amount <= 0 || largest < 0)
        {
            return;
        }

        long remaining = Math.Min(amount, (long)weightSum);
        long target = remaining;
        for (int i = 0; i < weights.Length && remaining > 0; i++)
        {
            if (weights[i] > 0 && i != largest)
            {
                long share = Math.Min(weights[i], Math.Min(remaining, (long)(target * (weights[i] / weightSum))));
                totals[i] -= share;
                remaining -= share;
            }
        }

        // Rounding leftovers go to the largest category, which can absorb them.
        totals[largest] -= Math.Min(remaining, weights[largest]);
    }

    /// <summary>
    /// Replaces a folder's contents with a fresh scan of that folder (a tree rooted at the folder's path),
    /// for "rescan this folder" without rescanning the drive. The old subtree is removed, the fresh one is
    /// copied in under a new index, and the totals of every ancestor are corrected.
    /// </summary>
    /// <returns>The folder's new directory index.</returns>
    public int ReplaceDirectory(int dirIndex, ScanTree fresh)
    {
        if (dirIndex == RootIndex)
        {
            throw new InvalidOperationException("The scan root is replaced by a full rescan.");
        }

        if (fresh.FileIndexThreshold != FileIndexThreshold)
        {
            throw new ArgumentException("The fresh scan must use the same file index threshold.", nameof(fresh));
        }

        lock (SyncRoot)
        {
            ref var old = ref _dirs[dirIndex];
            if (old.IsRemoved)
            {
                throw new InvalidOperationException("The folder was removed.");
            }

            int parent = old.Parent;
            string name = old.Name;
            var keptFlags = old.Flags & (NodeFlags.Hidden | NodeFlags.System);
            RemoveDirectoryCore(dirIndex);

            // Fresh directories were allocated parent first, so one pass in index order rebuilds the links.
            var map = new int[fresh.DirectoryCount];
            Array.Fill(map, -1);
            ref var freshRoot = ref fresh.Dir(RootIndex);
            map[RootIndex] = AddDirectory(parent, name, keptFlags | (freshRoot.Flags & ~(NodeFlags.Removed | NodeFlags.Hidden | NodeFlags.System)), freshRoot.LastWriteUtc);
            for (int i = 1; i < map.Length; i++)
            {
                if (!fresh.IsLiveDirectory(i) || map[fresh.Dir(i).Parent] < 0)
                {
                    continue;
                }

                ref var node = ref fresh.Dir(i);
                map[i] = AddDirectory(map[node.Parent], node.Name, node.Flags, node.LastWriteUtc);
            }

            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] >= 0)
                {
                    ref var target = ref _dirs[map[i]];
                    target.OwnSize = fresh.Dir(i).OwnSize;
                    target.OwnFileCount = fresh.Dir(i).OwnFileCount;
                    target.TotalSize = target.OwnSize;
                    target.TotalFiles = target.OwnFileCount;
                    target.TotalDirs = 0;
                }
            }

            int fileCount = fresh.FileRecordCount;
            for (int f = 0; f < fileCount; f++)
            {
                if (fresh.IsLiveFile(f) && map[fresh.File(f).Directory] >= 0)
                {
                    ref var file = ref fresh.File(f);
                    AddFile(map[file.Directory], file.Name, file.Size, file.Category, file.LastWriteUtc, file.Attributes);
                }
            }

            // Totals inside the new subtree, bottom up (children have larger fresh indices than parents).
            for (int i = map.Length - 1; i > RootIndex; i--)
            {
                if (map[i] >= 0)
                {
                    ref var child = ref _dirs[map[i]];
                    ref var up = ref _dirs[child.Parent];
                    up.TotalSize += child.TotalSize;
                    up.TotalFiles += child.TotalFiles;
                    up.TotalDirs += child.TotalDirs + 1;
                }
            }

            ref var replaced = ref _dirs[map[RootIndex]];
            for (int p = parent; p >= 0; p = _dirs[p].Parent)
            {
                _dirs[p].TotalSize += replaced.TotalSize;
                _dirs[p].TotalFiles += replaced.TotalFiles;
                _dirs[p].TotalDirs += replaced.TotalDirs + 1;
            }

            for (int c = 0; c < CategoryBytes.Length; c++)
            {
                CategoryBytes[c] += fresh.CategoryBytes[c];
                CategoryCounts[c] += fresh.CategoryCounts[c];
            }

            foreach (var error in fresh.Errors)
            {
                RecordError(error.Path, error.ErrorCode, error.Message);
            }

            return map[RootIndex];
        }
    }

    private delegate ref int NextAccessor(ScanTree tree, int index);

    private void Unlink(ref int head, int target, NextAccessor next)
    {
        if (head == target)
        {
            head = next(this, target);
            return;
        }

        for (int i = head; i >= 0; i = next(this, i))
        {
            ref int link = ref next(this, i);
            if (link == target)
            {
                link = next(this, target);
                return;
            }
        }
    }

    /// <summary>Recomputes all aggregate totals from per-directory values (used after loading a snapshot).</summary>
    internal void RecomputeTotals()
    {
        int count = _dirs.Count;
        for (int i = 0; i < count; i++)
        {
            ref var n = ref _dirs[i];
            n.TotalSize = n.IsRemoved ? 0 : n.OwnSize;
            n.TotalFiles = n.IsRemoved ? 0 : n.OwnFileCount;
            n.TotalDirs = 0;
        }

        // Parents always have smaller indices than their children, so a reverse pass aggregates bottom-up.
        for (int i = count - 1; i > RootIndex; i--)
        {
            ref var n = ref _dirs[i];
            if (n.IsRemoved)
            {
                continue;
            }

            ref var parent = ref _dirs[n.Parent];
            parent.TotalSize += n.TotalSize;
            parent.TotalFiles += n.TotalFiles;
            parent.TotalDirs += n.TotalDirs + 1;
        }

        _filesScanned = _dirs[RootIndex].TotalFiles;
        _bytesScanned = _dirs[RootIndex].TotalSize;
        _directoriesScanned = count;
    }

    internal void SetErrorCount(int count) => _errorCount = count;

    internal void AddStoredError(ScanError error) => _errors.Enqueue(error);
}

/// <summary>Information about how and when a tree was produced. Persisted in snapshots.</summary>
public sealed class ScanMetadata
{
    public string ScannerName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public TimeSpan Duration { get; set; }
    public bool WasCancelled { get; set; }
    public bool LoadedFromSnapshot { get; set; }

    public long VolumeTotalBytes { get; set; }
    public long VolumeFreeBytes { get; set; }
    public uint VolumeSerialNumber { get; set; }
    public string? VolumeLabel { get; set; }
    public string? FileSystem { get; set; }

    /// <summary>
    /// NTFS USN journal position at scan start (0 when unknown). Stored so a future version can apply
    /// incremental updates from the change journal instead of rescanning.
    /// </summary>
    public ulong UsnJournalId { get; set; }
    public long UsnNextUsn { get; set; }
}
