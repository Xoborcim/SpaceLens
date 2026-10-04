using System.Security.AccessControl;
using System.Security.Principal;
using SpaceLens.Core.Models;
using SpaceLens.Core.Scanning;
using SpaceLens.Windows.FileSystem;

namespace SpaceLens.Tests;

public class ScannerTests
{
    public static TheoryData<ScanEngine> Engines => new() { ScanEngine.Native, ScanEngine.FindFirstFile, ScanEngine.Managed };

    private static async Task<(ScanTree Tree, ScanResult Result)> Scan(string root, ScanEngine engine, int workers = 4, long threshold = 1024)
    {
        var tree = new ScanTree(root, threshold);
        var result = await ScannerFactory.Create(engine).ScanAsync(tree, new ScanOptions { MaxParallelism = workers, UseAllocatedSize = false }, null, CancellationToken.None);
        return (tree, result);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Aggregates_sizes_and_counts_up_the_tree(ScanEngine engine)
    {
        using var dir = new TestDirectory();
        dir.File(@"a\one.bin", 1000);
        dir.File(@"a\b\two.bin", 2000);
        dir.File(@"a\b\c\three.mp4", 3000);
        dir.File(@"d\four.txt", 4000);
        dir.File("root.txt", 500);
        dir.Dir("empty");

        var (tree, result) = await Scan(dir.Root, engine);

        Assert.True(result.Completed);
        Assert.Equal(5, result.Files);
        Assert.Equal(10_500, tree.Root.TotalSize);
        Assert.Equal(5, tree.Root.TotalFiles);
        Assert.Equal(5, tree.Root.TotalDirs); // a, a\b, a\b\c, d, empty
        Assert.Equal(6_000, tree.Dir(tree.FindDirectory(Path.Combine(dir.Root, "a"))).TotalSize);
        Assert.Equal(5_000, tree.Dir(tree.FindDirectory(Path.Combine(dir.Root, @"a\b"))).TotalSize);
        Assert.Equal(0, tree.Dir(tree.FindDirectory(Path.Combine(dir.Root, "empty"))).TotalSize);
        Assert.Equal(500, tree.Root.OwnSize);
        Assert.Equal(0, result.Errors);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Indexes_only_files_at_or_above_threshold_and_tracks_categories(ScanEngine engine)
    {
        using var dir = new TestDirectory();
        dir.File("small.mp4", 100);
        dir.File("large.mp4", 5000);
        dir.File("disk.iso", 3000);

        var (tree, _) = await Scan(dir.Root, engine, threshold: 2000);

        Assert.Equal(2, tree.FileRecordCount);
        var names = tree.GetFiles(ScanTree.RootIndex).Select(f => tree.File(f).Name).OrderBy(n => n).ToArray();
        Assert.Equal(["disk.iso", "large.mp4"], names);
        Assert.Equal(5100, tree.CategoryBytes[(int)FileCategory.Video]);
        Assert.Equal(2, tree.CategoryCounts[(int)FileCategory.Video]);
        Assert.Equal(3000, tree.CategoryBytes[(int)FileCategory.DiskImage]);
        Assert.Equal(100, tree.GetUnindexedOwnSize(ScanTree.RootIndex, out int unindexed));
        Assert.Equal(1, unindexed);

        var largest = SpaceLens.Core.Aggregation.Breakdown.LargeFiles(tree, 0);
        Assert.Equal("large.mp4", tree.File(largest[0]).Name);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Junction_loops_are_recorded_but_not_followed(ScanEngine engine)
    {
        using var dir = new TestDirectory();
        string inner = dir.Dir("loop", "inner");
        dir.File(@"loop\inner\data.bin", 4096);
        Assert.True(TestDirectory.TryCreateJunction(Path.Combine(inner, "back-to-root"), dir.Root), "mklink /J failed");

        var (tree, result) = await Scan(dir.Root, engine);

        Assert.True(result.Completed);
        Assert.Equal(1, result.Files);
        Assert.Equal(4096, tree.Root.TotalSize);
        int link = tree.FindDirectory(Path.Combine(inner, "back-to-root"));
        Assert.True(link > 0);
        Assert.True((tree.Dir(link).Flags & NodeFlags.ReparsePoint) != 0);
        Assert.Equal(0, tree.Dir(link).TotalSize);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Symbolic_links_are_not_followed(ScanEngine engine)
    {
        using var dir = new TestDirectory();
        string target = dir.Dir("target");
        dir.File(@"target\payload.bin", 8192);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(dir.Root, "symlink"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating symbolic links needs Developer Mode or administrator rights; nothing to test.
            return;
        }

        var (tree, _) = await Scan(dir.Root, engine);

        Assert.Equal(8192, tree.Root.TotalSize); // counted once, through the real folder
        int link = tree.FindDirectory(Path.Combine(dir.Root, "symlink"));
        Assert.True((tree.Dir(link).Flags & NodeFlags.ReparsePoint) != 0);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Inaccessible_directories_are_recorded_and_scan_continues(ScanEngine engine)
    {
        using var dir = new TestDirectory();
        string locked = dir.Dir("locked");
        dir.File(@"locked\secret.bin", 1000);
        dir.File(@"open\visible.bin", 2000);

        var info = new DirectoryInfo(locked);
        var sid = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(sid, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            var (tree, result) = await Scan(dir.Root, engine);

            Assert.True(result.Completed);
            Assert.Equal(2000, tree.Root.TotalSize);
            Assert.Equal(1, result.Errors);
            int lockedIndex = tree.FindDirectory(locked);
            Assert.True((tree.Dir(lockedIndex).Flags & NodeFlags.AccessDenied) != 0);
            var error = Assert.Single(tree.Errors);
            Assert.Equal(5, error.ErrorCode);
            Assert.Equal(PathUtil.NormalizeDisplayPath(locked), error.Path, ignoreCase: true);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Handles_long_and_unusual_paths(ScanEngine engine)
    {
        using var dir = new TestDirectory();
        // > 260 characters.
        string deep = dir.Root;
        for (int i = 0; i < 12; i++)
        {
            deep = Path.Combine(deep, new string((char)('a' + i), 30));
        }

        Directory.CreateDirectory(deep);
        System.IO.File.WriteAllBytes(Path.Combine(deep, "deep.bin"), new byte[3000]);

        // Unicode, spaces, dots, brackets, percent and semicolons.
        dir.File(@"Ünïcødé 文件 🚀\naïve file;name%20[1].txt", 1500);
        dir.File(@"has.dots.in.name\file.with.many.dots.tar.gz", 700);

        // A trailing space and trailing dot are illegal through Win32 normalization but valid on NTFS.
        string odd = @"\\?\" + Path.Combine(dir.Root, "trailing space ");
        Directory.CreateDirectory(odd);
        System.IO.File.WriteAllBytes(odd + @"\dot.", new byte[1200]);

        var (tree, result) = await Scan(dir.Root, engine);

        Assert.True(result.Completed);
        Assert.Equal(0, result.Errors);
        Assert.Equal(3000 + 1500 + 700 + 1200, tree.Root.TotalSize);
        Assert.True(tree.FindDirectory(deep) > 0);
        Assert.True(tree.GetPath(tree.FindDirectory(deep)).Length > 260);
        Assert.True(tree.FindChild(ScanTree.RootIndex, "trailing space ") > 0);
        Assert.True(tree.FindChild(ScanTree.RootIndex, "Ünïcødé 文件 🚀") > 0);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan_promptly_and_reports_cancelled()
    {
        using var dir = new TestDirectory();
        for (int i = 0; i < 300; i++)
        {
            dir.Dir($"d{i}", "x", "y");
        }

        var tree = new ScanTree(dir.Root);
        using var cts = new CancellationTokenSource();
        var gate = new PauseGate();
        gate.Pause();
        var task = ScannerFactory.Create(ScanEngine.Native).ScanAsync(tree, new ScanOptions { MaxParallelism = 2 }, gate, cts.Token);

        await Task.Delay(50);
        Assert.False(task.IsCompleted); // paused
        cts.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Cancelled);
        Assert.False(result.Completed);
        Assert.True(tree.Metadata.WasCancelled);
    }

    [Fact]
    public async Task Pause_and_resume_complete_the_scan()
    {
        using var dir = new TestDirectory();
        for (int i = 0; i < 50; i++)
        {
            dir.File($@"d{i}\f.bin", 10);
        }

        var tree = new ScanTree(dir.Root);
        var gate = new PauseGate();
        gate.Pause();
        var task = ScannerFactory.Create(ScanEngine.Native).ScanAsync(tree, new ScanOptions { MaxParallelism = 3, UseAllocatedSize = false }, gate, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);
        gate.Resume();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Completed);
        Assert.Equal(50, result.Files);
        Assert.Equal(500, tree.Root.TotalSize);
    }

    [Fact]
    public async Task Enumeration_errors_and_exceptions_do_not_abort_the_scan()
    {
        var factory = new FakeEnumeratorFactory(new Dictionary<string, Func<IDirectoryEntrySink, int>>
        {
            [@"\\?\X:\"] = sink =>
            {
                sink.OnEntry(new RawDirectoryEntry("good", FileAttributes.Directory, 0, 0, 0, 0));
                sink.OnEntry(new RawDirectoryEntry("vanished", FileAttributes.Directory, 0, 0, 0, 0));
                sink.OnEntry(new RawDirectoryEntry("throws", FileAttributes.Directory, 0, 0, 0, 0));
                sink.OnEntry(new RawDirectoryEntry("file.txt", FileAttributes.Normal, 10, 10, 0, 0));
                return 0;
            },
            [@"\\?\X:\good"] = sink =>
            {
                sink.OnEntry(new RawDirectoryEntry("a.bin", FileAttributes.Normal, 100, 100, 0, 0));
                return 0;
            },
            [@"\\?\X:\vanished"] = _ => 3,
            [@"\\?\X:\throws"] = _ => throw new IOException("Corrupt directory"),
        });

        var tree = new ScanTree(@"X:\");
        var result = await new ParallelDirectoryScanner(factory).ScanAsync(tree, new ScanOptions { MaxParallelism = 2 }, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(110, tree.Root.TotalSize);
        Assert.Equal(2, result.Errors);
        Assert.Contains(tree.Errors, e => e.Path == @"X:\vanished" && e.ErrorCode == 3);
        Assert.Contains(tree.Errors, e => e.Path == @"X:\throws");
    }

    [Fact]
    public async Task Parallel_scan_of_wide_and_deep_tree_matches_single_threaded()
    {
        var factory = new FakeEnumeratorFactory(path =>
        {
            int depth = path.Split('\\').Count(s => s.Length == 2 && s[0] == 'd');
            return sink =>
            {
                if (depth < 4)
                {
                    for (int i = 0; i < 6; i++)
                    {
                        sink.OnEntry(new RawDirectoryEntry($"d{i}", FileAttributes.Directory, 0, 0, 0, 0));
                    }
                }

                for (int i = 0; i < 5; i++)
                {
                    sink.OnEntry(new RawDirectoryEntry($"f{i}.dat", FileAttributes.Normal, 1000 + i, 1000 + i, 0, 0));
                }

                return 0;
            };
        });

        var single = new ScanTree(@"X:\");
        var parallel = new ScanTree(@"X:\");
        var r1 = await new ParallelDirectoryScanner(factory).ScanAsync(single, new ScanOptions { MaxParallelism = 1 }, null, CancellationToken.None);
        var r2 = await new ParallelDirectoryScanner(factory).ScanAsync(parallel, new ScanOptions { MaxParallelism = 16 }, null, CancellationToken.None);

        int expectedDirs = 1 + 6 + 36 + 216 + 1296;
        Assert.Equal(expectedDirs, r1.Directories);
        Assert.Equal(expectedDirs, r2.Directories);
        Assert.Equal(single.Root.TotalSize, parallel.Root.TotalSize);
        Assert.Equal(expectedDirs * 5L, parallel.Root.TotalFiles);
        Assert.Equal(expectedDirs - 1, parallel.Root.TotalDirs);
    }

    private sealed class FakeEnumeratorFactory : IDirectoryEnumeratorFactory, IDirectoryEnumerator
    {
        private readonly Func<string, Func<IDirectoryEntrySink, int>?> _lookup;

        public FakeEnumeratorFactory(Dictionary<string, Func<IDirectoryEntrySink, int>> map) =>
            _lookup = p => map.TryGetValue(p, out var f) ? f : null;

        public FakeEnumeratorFactory(Func<string, Func<IDirectoryEntrySink, int>?> lookup) => _lookup = lookup;

        public string Name => "Fake";

        public bool ReportsReparseTags => true;

        public IDirectoryEnumerator Create() => this;

        public int Enumerate(string path, IDirectoryEntrySink sink) => _lookup(path)?.Invoke(sink) ?? 0;

        public void Dispose()
        {
        }
    }
}
