using SpaceLens.Core.Classification;
using SpaceLens.Core.Duplicates;
using SpaceLens.Core.Models;

namespace SpaceLens.Tests;

public class DuplicateTests
{
    private const long MB = 1L << 20;

    /// <summary>A tree whose file contents live in memory, keyed by the tree's paths.</summary>
    private sealed class Fixture
    {
        public ScanTree Tree { get; } = new(@"D:\");
        public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Identities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Opened { get; } = [];

        public int File(string folder, string name, byte[] content, FileAttributes attributes = 0)
        {
            int dir = ScanTree.RootIndex;
            foreach (var segment in folder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                int child = Tree.FindChild(dir, segment);
                dir = child >= 0 ? child : Tree.AddDirectory(dir, segment);
            }

            int index = Tree.AddFile(dir, name, content.Length, FileCategory.Other, 0, attributes);
            Contents[Tree.GetFilePath(index)] = content;
            return index;
        }

        public DuplicateOptions Options(params string[] excluded) => new()
        {
            ExcludedFolders = excluded,
            QuickCheckBytes = 4096,
            OpenRead = path =>
            {
                lock (Opened)
                {
                    Opened.Add(path);
                }

                return Locked.Contains(path) ? throw new IOException("locked") : new MemoryStream(Contents[path], writable: false);
            },
            FileIdentity = path => Identities.GetValueOrDefault(path),
        };
    }

    private static byte[] Content(long size, int seed, int changeAt = -1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        if (changeAt >= 0)
        {
            bytes[changeAt] ^= 0xFF;
        }

        return bytes;
    }

    [Fact]
    public void Finds_identical_files_and_ignores_same_sized_different_files()
    {
        var f = new Fixture();
        var movie = Content(3 * MB, 1);
        int a = f.File(@"Videos", "movie.mkv", movie);
        int b = f.File(@"Backup\Videos", "movie (copy).mkv", movie);
        int c = f.File(@"Old", "movie.mkv", movie);
        f.File(@"Other", "same-size.bin", Content(3 * MB, 2));                          // differs everywhere
        f.File(@"Other", "middle-differs.bin", Content(3 * MB, 1, changeAt: (int)(MB + 7))); // passes the quick check
        f.File(@"Other", "unique.bin", Content(2 * MB, 3));

        var result = DuplicateFinder.Find(f.Tree, f.Options());

        var group = Assert.Single(result.Groups);
        Assert.Equal(3 * MB, group.Size);
        Assert.Equal(6 * MB, group.Reclaimable);
        Assert.Equal(new[] { b, c, a }, group.Files); // sorted by path: D:\Backup..., D:\Old..., D:\Videos...
        Assert.Equal(6 * MB, result.Reclaimable);
    }

    [Fact]
    public void Full_check_reads_only_files_that_pass_the_quick_check()
    {
        var f = new Fixture();
        var data = Content(2 * MB, 5);
        f.File(@"A", "x.bin", data);
        f.File(@"B", "x.bin", data);
        f.File(@"C", "different-start.bin", Content(2 * MB, 5, changeAt: 10));
        f.File(@"C", "different-size.bin", Content(2 * MB + 1, 5));

        DuplicateFinder.Find(f.Tree, f.Options());

        // Quick check opens the three same-sized files; the full check only the two that still match.
        Assert.Equal(4, f.Opened.Count(p => p.EndsWith("x.bin", StringComparison.Ordinal)));
        Assert.Equal(1, f.Opened.Count(p => p.EndsWith("different-start.bin", StringComparison.Ordinal)));
        Assert.DoesNotContain(f.Opened, p => p.EndsWith("different-size.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void Hard_links_cloud_files_excluded_folders_and_small_files_are_not_duplicates()
    {
        var f = new Fixture();
        var data = Content(2 * MB, 7);
        int linkA = f.File(@"Tools", "tool.exe", data);
        int linkB = f.File(@"Tools\Alias", "tool.exe", data);
        f.Identities[f.Tree.GetFilePath(linkA)] = "vol1:42";
        f.Identities[f.Tree.GetFilePath(linkB)] = "vol1:42";
        f.File(@"OneDrive", "cloud.bin", data, FileAttributes.Offline);
        f.File(@"Windows\WinSxS", "component.dll", data);
        var small = Content(MB / 2, 8);
        f.File(@"A", "small.txt", small);
        f.File(@"B", "small.txt", small);

        var result = DuplicateFinder.Find(f.Tree, f.Options(@"D:\Windows"));

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.CloudFilesSkipped);
        Assert.DoesNotContain(f.Opened, p => p.Contains("OneDrive", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_files_are_counted_and_left_out()
    {
        var f = new Fixture();
        var data = Content(2 * MB, 9);
        f.File(@"A", "x.bin", data);
        f.File(@"B", "x.bin", data);
        int locked = f.File(@"C", "x.bin", data);
        f.Locked.Add(f.Tree.GetFilePath(locked));

        var result = DuplicateFinder.Find(f.Tree, f.Options());

        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
        Assert.Equal(1, result.UnreadableFiles);
    }

    [Fact]
    public void Removed_files_are_ignored_and_progress_is_reported()
    {
        var f = new Fixture();
        var data = Content(2 * MB, 11);
        f.File(@"A", "x.bin", data);
        int removed = f.File(@"B", "x.bin", data);
        f.Tree.RemoveFile(removed);

        var reports = new List<DuplicateProgress>();
        var result = DuplicateFinder.Find(f.Tree, f.Options(), new SyncProgress(reports));

        Assert.Empty(result.Groups);
        Assert.Empty(f.Opened);

        var g = new Fixture();
        g.File(@"A", "y.bin", data);
        g.File(@"B", "y.bin", data);
        DuplicateFinder.Find(g.Tree, g.Options(), new SyncProgress(reports));
        var last = reports[^1];
        Assert.Equal(DuplicateStage.FullCheck, last.Stage);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(2, last.FilesDone);
    }

    [Fact]
    public void Cancellation_stops_the_search()
    {
        var f = new Fixture();
        var data = Content(2 * MB, 13);
        f.File(@"A", "x.bin", data);
        f.File(@"B", "x.bin", data);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DuplicateFinder.Find(f.Tree, f.Options(), null, cts.Token));
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need the reports in order and immediately.</summary>
    private sealed class SyncProgress(List<DuplicateProgress> reports) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}

public class NativeLayoutTests
{
    [Fact]
    public void By_handle_file_information_matches_the_native_layout() =>
        Assert.Equal(52, System.Runtime.InteropServices.Marshal.SizeOf<SpaceLens.Windows.Native.NativeMethods.ByHandleFileInformation>());
}
