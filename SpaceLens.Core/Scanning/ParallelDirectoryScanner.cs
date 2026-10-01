using System.Collections.Concurrent;
using System.Diagnostics;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.Scanning;

/// <summary>
/// Recursive directory scanner with a bounded pool of dedicated worker threads.
/// <para>
/// Each worker owns a local LIFO stack of directories (depth-first keeps the frontier small and
/// caches warm). When other workers are idle, a busy worker shares the oldest (shallowest, therefore
/// usually largest) entries of its stack through a shared queue. A pending-work counter detects
/// completion. No task or closure is allocated per directory.
/// </para>
/// <para>
/// Junctions, symbolic links and mount points (name-surrogate reparse points) are recorded but never
/// followed, which prevents loops and double counting across volumes.
/// </para>
/// </summary>
public sealed class ParallelDirectoryScanner : IDiskScanner
{
    private const uint ReparseTagNameSurrogateBit = 0x20000000;

    private readonly IDirectoryEnumeratorFactory _factory;

    public ParallelDirectoryScanner(IDirectoryEnumeratorFactory factory) => _factory = factory;

    public string Name => _factory.Name;

    public bool IsSupported(string rootPath, out string? reason)
    {
        reason = null;
        return true;
    }

    public Task<ScanResult> ScanAsync(ScanTree tree, ScanOptions options, PauseGate? pause, CancellationToken cancellationToken)
    {
        var run = new ScanRun(tree, options, _factory, pause, cancellationToken);
        return run.Start();
    }

    /// <summary>True for reparse points that redirect to another location (junction, symlink, mount point, WSL link).</summary>
    public static bool IsNameSurrogate(uint reparseTag) => (reparseTag & ReparseTagNameSurrogateBit) != 0;

    private readonly record struct WorkItem(int Node, string Path);

    private sealed class ScanRun
    {
        private readonly ScanTree _tree;
        private readonly ScanOptions _options;
        private readonly IDirectoryEnumeratorFactory _factory;
        private readonly PauseGate? _pause;
        private readonly CancellationToken _ct;
        private readonly ConcurrentQueue<WorkItem> _shared = new();
        private readonly SemaphoreSlim _sharedSignal = new(0);
        private readonly TaskCompletionSource<ScanResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _workerCount;
        private int _pending;
        private int _idle;
        private int _activeWorkers;
        private volatile bool _done;
        private readonly Stopwatch _stopwatch = new();
        private Exception? _fatal;

        public ScanRun(ScanTree tree, ScanOptions options, IDirectoryEnumeratorFactory factory, PauseGate? pause, CancellationToken ct)
        {
            _tree = tree;
            _options = options;
            _factory = factory;
            _pause = pause;
            _ct = ct;
            _workerCount = options.ResolveParallelism();
        }

        public Task<ScanResult> Start()
        {
            _tree.Metadata.ScannerName = _factory.Name;
            _tree.Metadata.StartedUtc = DateTime.UtcNow;
            _stopwatch.Start();

            _pending = 1;
            _shared.Enqueue(new WorkItem(ScanTree.RootIndex, PathUtil.ToLongPath(_tree.RootPath)));
            _sharedSignal.Release();

            _activeWorkers = _workerCount;
            for (int i = 0; i < _workerCount; i++)
            {
                var thread = new Thread(WorkerMain)
                {
                    IsBackground = true,
                    Name = $"SpaceLens scan worker {i}",
                    Priority = _options.LowPriorityThreads ? ThreadPriority.BelowNormal : ThreadPriority.Normal,
                };
                thread.Start();
            }

            return _completion.Task;
        }

        private void WorkerMain()
        {
            var worker = new Worker(this);
            try
            {
                worker.Run();
            }
            catch (OperationCanceledException)
            {
                // Cancellation is reported through the result.
            }
            catch (Exception ex)
            {
                _fatal ??= ex;
                Finish();
            }
            finally
            {
                worker.Dispose();
                if (Interlocked.Decrement(ref _activeWorkers) == 0)
                {
                    Complete();
                }
            }
        }

        private void Finish()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _sharedSignal.Release(_workerCount);
        }

        private void Complete()
        {
            _stopwatch.Stop();
            bool cancelled = _ct.IsCancellationRequested;
            var meta = _tree.Metadata;
            meta.Duration = _stopwatch.Elapsed;
            meta.WasCancelled = cancelled;
            meta.CompletedUtc = DateTime.UtcNow;

            if (_fatal is not null && !cancelled)
            {
                _completion.TrySetException(_fatal);
                return;
            }

            _completion.TrySetResult(new ScanResult(
                Completed: !cancelled,
                Cancelled: cancelled,
                Duration: _stopwatch.Elapsed,
                Files: _tree.FilesScanned,
                Directories: _tree.DirectoriesScanned,
                Bytes: _tree.BytesScanned,
                Errors: _tree.ErrorCount,
                Workers: _workerCount,
                Scanner: _factory.Name));
        }

        /// <summary>Per-thread state. Implements the entry callback to avoid a closure per directory.</summary>
        private sealed class Worker : IDirectoryEntrySink, IDisposable
        {
            private readonly ScanRun _run;
            private readonly ScanTree _tree;
            private readonly IDirectoryEnumerator _enumerator;
            private readonly List<WorkItem> _local = new(256);
            private readonly long[] _categoryBytes = new long[FileCategoryInfo.Count];
            private readonly long[] _categoryCounts = new long[FileCategoryInfo.Count];
            private readonly bool _useAllocated;
            private readonly bool _reparseTagsKnown;
            private readonly long _indexThreshold;

            // State of the directory currently being enumerated.
            private int _node;
            private string _path = "";
            private long _ownSize;
            private int _ownFiles;
            private int _newSubdirs;

            public Worker(ScanRun run)
            {
                _run = run;
                _tree = run._tree;
                _enumerator = run._factory.Create();
                _useAllocated = run._options.UseAllocatedSize;
                _reparseTagsKnown = run._factory.ReportsReparseTags;
                _indexThreshold = _tree.FileIndexThreshold;
            }

            public void Run()
            {
                var ct = _run._ct;
                while (true)
                {
                    if (!TryGetWork(out var item))
                    {
                        return;
                    }

                    ct.ThrowIfCancellationRequested();
                    _run._pause?.WaitIfPaused(ct);

                    ProcessDirectory(item);

                    int pendingAfter = Interlocked.Decrement(ref _run._pending);
                    if (pendingAfter == 0)
                    {
                        _run.Finish();
                        return;
                    }

                    ShareWorkIfOthersIdle();
                }
            }

            private bool TryGetWork(out WorkItem item)
            {
                if (_local.Count > 0)
                {
                    item = _local[^1];
                    _local.RemoveAt(_local.Count - 1);
                    return true;
                }

                // The semaphore count mirrors the number of shared items, so a successful wait
                // guarantees an item unless the scan finished.
                if (!_run._sharedSignal.Wait(0))
                {
                    Interlocked.Increment(ref _run._idle);
                    try
                    {
                        _run._sharedSignal.Wait(_run._ct);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _run._idle);
                    }
                }

                if (_run._done)
                {
                    item = default;
                    return false;
                }

                return _run._shared.TryDequeue(out item) || TryGetWork(out item);
            }

            private void ShareWorkIfOthersIdle()
            {
                int idle = Volatile.Read(ref _run._idle);
                if (idle == 0 || _local.Count < 2)
                {
                    return;
                }

                // Give away the bottom of the stack: those directories are the shallowest.
                int share = Math.Min(idle, _local.Count - 1);
                for (int i = 0; i < share; i++)
                {
                    _run._shared.Enqueue(_local[i]);
                }

                _local.RemoveRange(0, share);
                _run._sharedSignal.Release(share);
            }

            private void ProcessDirectory(WorkItem item)
            {
                _node = item.Node;
                _path = item.Path;
                _ownSize = 0;
                _ownFiles = 0;
                _newSubdirs = 0;
                Array.Clear(_categoryBytes);
                Array.Clear(_categoryCounts);
                _tree.CurrentPath = item.Path;

                int error;
                try
                {
                    error = _enumerator.Enumerate(item.Path, this);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.HResult & 0xFFFF;
                    if (error == 0)
                    {
                        error = 1;
                    }
                }

                NodeFlags extra = NodeFlags.None;
                if (error != 0)
                {
                    extra = error == 5 ? NodeFlags.AccessDenied : NodeFlags.Error;
                    _tree.RecordError(item.Path, error, DescribeError(error));
                }

                _tree.AddCategoryTotals(_categoryBytes, _categoryCounts);
                _tree.CompleteDirectory(_node, _ownSize, _ownFiles, _newSubdirs, extra);
            }

            public void OnEntry(in RawDirectoryEntry entry)
            {
                if (entry.IsDirectory)
                {
                    OnDirectory(entry);
                    return;
                }

                long size = _useAllocated && entry.AllocatedSize >= 0 ? entry.AllocatedSize : entry.Length;
                var category = ExtensionClassifier.Classify(entry.Name);
                _ownSize += size;
                _ownFiles++;
                _categoryBytes[(int)category] += size;
                _categoryCounts[(int)category]++;

                if (size >= _indexThreshold)
                {
                    _tree.AddFile(_node, entry.Name.ToString(), size, category, entry.LastWriteUtc, entry.Attributes);
                }
            }

            private void OnDirectory(in RawDirectoryEntry entry)
            {
                var flags = NodeFlags.None;
                if ((entry.Attributes & FileAttributes.Hidden) != 0)
                {
                    flags |= NodeFlags.Hidden;
                }

                if ((entry.Attributes & FileAttributes.System) != 0)
                {
                    flags |= NodeFlags.System;
                }

                bool follow = true;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Without tag information every reparse directory is treated as a link.
                    follow = _reparseTagsKnown && !IsNameSurrogate(entry.ReparseTag);
                    if (!follow)
                    {
                        flags |= NodeFlags.ReparsePoint | NodeFlags.Scanned;
                    }
                }

                string name = entry.Name.ToString();
                int child = _tree.AddDirectory(_node, name, flags, entry.LastWriteUtc);
                _newSubdirs++;
                if (follow)
                {
                    Interlocked.Increment(ref _run._pending);
                    _local.Add(new WorkItem(child, _path.EndsWith('\\') ? string.Concat(_path, name) : string.Concat(_path, "\\", name)));
                }
            }

            public void Dispose() => _enumerator.Dispose();
        }
    }

    public static string DescribeError(int win32Error) => win32Error switch
    {
        2 => "File not found (removed during scan)",
        3 => "Path not found (removed during scan)",
        5 => "Access denied",
        21 => "Device not ready",
        32 => "In use by another process",
        123 => "Invalid name",
        206 => "Path too long",
        1920 => "File cannot be accessed by the system",
        4390 => "Not a reparse point / unsupported link",
        _ => $"Error {win32Error}",
    };
}
