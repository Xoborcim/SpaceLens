using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Search;
using SpaceLens.Core.Snapshots;
using SpaceLens.Core.Visualization;

namespace SpaceLens.Tests;

public class ScanTreeTests
{
    internal static ScanTree Sample()
    {
        // C:\
        //   Users\me\Downloads\ubuntu.iso (8 GB), notes.txt (small)
        //   Users\me\Videos\movie.mkv (3 GB)
        //   Games\Big (20 GB loose)
        var tree = new ScanTree(@"C:\");
        int users = tree.AddDirectory(ScanTree.RootIndex, "Users");
        int me = tree.AddDirectory(users, "me");
        int downloads = tree.AddDirectory(me, "Downloads");
        int videos = tree.AddDirectory(me, "Videos");
        int games = tree.AddDirectory(ScanTree.RootIndex, "Games");
        int big = tree.AddDirectory(games, "Big");

        tree.AddFile(downloads, "ubuntu.iso", 8L << 30, FileCategory.DiskImage, 0);
        tree.AddFile(videos, "movie.mkv", 3L << 30, FileCategory.Video, 0);

        tree.CompleteDirectory(ScanTree.RootIndex, 0, 0, 2);
        tree.CompleteDirectory(users, 0, 0, 1);
        tree.CompleteDirectory(me, 0, 0, 2);
        tree.CompleteDirectory(downloads, (8L << 30) + 5000, 2, 0);
        tree.CompleteDirectory(videos, 3L << 30, 1, 0);
        tree.CompleteDirectory(games, 0, 0, 1);
        tree.CompleteDirectory(big, 20L << 30, 1000, 0);
        return tree;
    }

    [Fact]
    public void Totals_propagate_to_all_ancestors()
    {
        var tree = Sample();
        Assert.Equal((31L << 30) + 5000, tree.Root.TotalSize);
        Assert.Equal(1003, tree.Root.TotalFiles);
        Assert.Equal(6, tree.Root.TotalDirs);
        Assert.Equal((11L << 30) + 5000, tree.Dir(tree.FindDirectory(@"C:\Users\me")).TotalSize);
    }

    [Fact]
    public void Paths_round_trip_through_find()
    {
        var tree = Sample();
        int downloads = tree.FindDirectory(@"c:\users\ME\Downloads\");
        Assert.True(downloads > 0);
        Assert.Equal(@"C:\Users\me\Downloads", tree.GetPath(downloads));
        Assert.Equal(-1, tree.FindDirectory(@"C:\Users\nobody"));
        Assert.Equal(-1, tree.FindDirectory(@"D:\Users"));
        Assert.Equal(ScanTree.RootIndex, tree.FindDirectory("C:"));
        int file = tree.GetFiles(downloads).Single();
        Assert.Equal(@"C:\Users\me\Downloads\ubuntu.iso", tree.GetFilePath(file));
    }

    [Fact]
    public void Folder_root_paths_join_correctly()
    {
        var tree = new ScanTree(@"D:\Projects\");
        int a = tree.AddDirectory(ScanTree.RootIndex, "a");
        Assert.Equal(@"D:\Projects", tree.RootPath);
        Assert.Equal(@"D:\Projects\a", tree.GetPath(a));
        Assert.Equal(a, tree.FindDirectory(@"D:\Projects\a"));
    }

    [Fact]
    public void Removing_a_file_updates_ancestors_and_categories()
    {
        var tree = Sample();
        int downloads = tree.FindDirectory(@"C:\Users\me\Downloads");
        int iso = tree.GetFiles(downloads).Single();
        tree.CategoryBytes[(int)FileCategory.DiskImage] = 8L << 30;
        tree.CategoryCounts[(int)FileCategory.DiskImage] = 1;

        tree.RemoveFile(iso);

        Assert.Empty(tree.GetFiles(downloads));
        Assert.Equal(5000, tree.Dir(downloads).TotalSize);
        Assert.Equal((23L << 30) + 5000, tree.Root.TotalSize);
        Assert.Equal(0, tree.CategoryBytes[(int)FileCategory.DiskImage]);
        Assert.False(tree.IsLiveFile(iso));
    }

    [Fact]
    public void Removing_a_directory_also_removes_its_small_files_from_category_totals()
    {
        // D:\a holds 2 MB of small documents, D:\b 6 MB of small images and an indexed 4 MB video.
        var tree = new ScanTree(@"D:\");
        int a = tree.AddDirectory(ScanTree.RootIndex, "a");
        int b = tree.AddDirectory(ScanTree.RootIndex, "b");
        tree.AddFile(b, "clip.mp4", 4L << 20, FileCategory.Video, 0);
        tree.CompleteDirectory(a, 2L << 20, 20, 0);
        tree.CompleteDirectory(b, 10L << 20, 61, 0);
        tree.CompleteDirectory(ScanTree.RootIndex, 0, 0, 2);
        tree.AddCategoryTotals(Totals((FileCategory.Document, 2L << 20), (FileCategory.Image, 6L << 20), (FileCategory.Video, 4L << 20)),
            Totals((FileCategory.Document, 20), (FileCategory.Image, 60), (FileCategory.Video, 1)));

        tree.RemoveDirectory(b);

        Assert.Equal(tree.Root.TotalSize, tree.CategoryBytes.Sum());
        Assert.Equal(tree.Root.TotalFiles, tree.CategoryCounts.Sum());
        Assert.Equal(0, tree.CategoryBytes[(int)FileCategory.Video]);
        Assert.All(tree.CategoryBytes, v => Assert.True(v >= 0));
    }

    private static long[] Totals(params (FileCategory Category, long Value)[] values)
    {
        var totals = new long[FileCategoryInfo.Count];
        foreach (var (category, value) in values)
        {
            totals[(int)category] = value;
        }

        return totals;
    }

    [Fact]
    public void Removing_a_directory_detaches_its_subtree()
    {
        var tree = Sample();
        int me = tree.FindDirectory(@"C:\Users\me");
        tree.RemoveDirectory(me);

        Assert.Equal(20L << 30, tree.Root.TotalSize);
        Assert.Equal(-1, tree.FindDirectory(@"C:\Users\me"));
        Assert.False(tree.IsLiveDirectory(me));
        Assert.Empty(tree.GetChildren(tree.FindDirectory(@"C:\Users")));
        Assert.Equal(3, tree.Root.TotalDirs); // Users, Games, Games\Big
        Assert.Throws<InvalidOperationException>(() => tree.RemoveDirectory(ScanTree.RootIndex));
    }

    [Fact]
    public void Snapshot_round_trip_preserves_structure_totals_and_removals()
    {
        var tree = Sample();
        tree.Metadata.ScannerName = "Test";
        tree.Metadata.VolumeTotalBytes = 1L << 40;
        tree.RecordError(@"\\?\C:\System Volume Information", 5, "Access denied");
        tree.RemoveDirectory(tree.FindDirectory(@"C:\Users\me\Videos"));

        using var stream = new MemoryStream();
        SnapshotSerializer.Save(tree, stream);
        stream.Position = 0;
        var loaded = SnapshotSerializer.Load(stream);

        Assert.True(loaded.Metadata.LoadedFromSnapshot);
        Assert.Equal("Test", loaded.Metadata.ScannerName);
        Assert.Equal(1L << 40, loaded.Metadata.VolumeTotalBytes);
        Assert.Equal(tree.Root.TotalSize, loaded.Root.TotalSize);
        Assert.Equal(tree.Root.TotalFiles, loaded.Root.TotalFiles);
        Assert.Equal(-1, loaded.FindDirectory(@"C:\Users\me\Videos"));
        Assert.True(loaded.FindDirectory(@"C:\Users\me\Downloads") > 0);
        Assert.Equal(1, loaded.ErrorCount);
        Assert.Equal(@"C:\System Volume Information", loaded.Errors.Single().Path);
        Assert.Equal(8L << 30, loaded.File(Breakdown.LargeFiles(loaded, 0)[0]).Size);
    }
}

public class SnapshotConcurrencyTests
{
    [Fact]
    public void Saving_while_items_are_removed_always_produces_a_loadable_snapshot()
    {
        for (int round = 0; round < 20; round++)
        {
            var tree = new ScanTree(@"D:\");
            var files = new List<int>();
            var dirs = new List<int>();
            for (int d = 0; d < 200; d++)
            {
                int dir = tree.AddDirectory(ScanTree.RootIndex, $"dir{d}");
                dirs.Add(dir);
                for (int f = 0; f < 50; f++)
                {
                    files.Add(tree.AddFile(dir, $"file{f}.bin", 2L << 20, FileCategory.Other, 0));
                }

                tree.CompleteDirectory(dir, 50 * (2L << 20), 50, 0);
            }

            tree.CompleteDirectory(ScanTree.RootIndex, 0, 0, dirs.Count);

            using var stream = new MemoryStream();
            var remover = Task.Run(() =>
            {
                for (int i = 0; i < files.Count; i += 7)
                {
                    tree.RemoveFile(files[i]);
                }

                for (int i = 0; i < dirs.Count; i += 5)
                {
                    tree.RemoveDirectory(dirs[i]);
                }
            });
            SnapshotSerializer.Save(tree, stream);
            remover.Wait();

            stream.Position = 0;
            var loaded = SnapshotSerializer.Load(stream);
            Assert.True(loaded.Root.TotalFiles > 0);
        }
    }
}

public class BreakdownTests
{
    [Fact]
    public void Breakdown_is_disjoint_sorted_and_expands_containers()
    {
        var tree = ScanTreeTests.Sample();
        var items = Breakdown.Build(tree);

        // Users and the profile are containers; Downloads, Videos and Games\Big are reported.
        var paths = items.Select(i => i.Kind == BreakdownItemKind.Directory ? tree.GetPath(i.Index) : i.Kind.ToString()).ToList();
        Assert.Contains(@"C:\Users\me\Downloads", paths);
        Assert.Contains(@"C:\Users\me\Videos", paths);
        Assert.DoesNotContain(@"C:\Users", paths);
        Assert.Equal(items.OrderByDescending(i => i.Size).Select(i => i.Size), items.Select(i => i.Size));
        Assert.True(items.Sum(i => i.Size) <= tree.Root.TotalSize);
    }

    [Fact]
    public void Units_are_not_expanded()
    {
        var tree = ScanTreeTests.Sample();
        int me = tree.FindDirectory(@"C:\Users\me");
        var items = Breakdown.Build(tree, isUnit: i => i == me);
        Assert.Contains(items, i => i.Index == me && i.Kind == BreakdownItemKind.Directory);
    }

    [Fact]
    public void Large_files_query_filters_by_size_and_category()
    {
        var tree = ScanTreeTests.Sample();
        Assert.Equal(2, Breakdown.LargeFiles(tree, 1L << 30).Count);
        Assert.Single(Breakdown.LargeFiles(tree, 5L << 30));
        Assert.Single(Breakdown.LargeFiles(tree, 0, FileCategory.Video));
        Assert.Empty(Breakdown.LargeFiles(tree, 10L << 30));
    }
}

public class TopNTests
{
    [Fact]
    public void Keeps_the_largest_items_in_descending_order()
    {
        var top = new TopN<int>(5);
        var random = new Random(5);
        var values = Enumerable.Range(0, 10_000).Select(_ => random.NextInt64(0, 1_000_000)).ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            top.Offer(values[i], i);
        }

        var expected = values.OrderByDescending(v => v).Take(5).ToArray();
        Assert.Equal(expected, top.ToSortedList().Select(x => x.Key).ToArray());
    }

    [Fact]
    public void Concurrent_offers_match_sequential_result()
    {
        var top = new ConcurrentTopN<int>(100);
        var values = Enumerable.Range(0, 200_000).Select(i => (long)((i * 7919L) % 1_000_003)).ToArray();
        Parallel.For(0, values.Length, i => top.Offer(values[i], i));
        var expected = values.OrderByDescending(v => v).Take(100).ToArray();
        Assert.Equal(expected, top.Snapshot().Select(x => x.Key).ToArray());
    }

    [Fact]
    public void Handles_fewer_items_than_capacity()
    {
        var top = new TopN<string>(10);
        top.Offer(3, "c");
        top.Offer(1, "a");
        Assert.Equal(["c", "a"], top.ToSortedList().Select(x => x.Value).ToArray());
    }
}

public class ClassificationTests
{
    [Theory]
    [InlineData("movie.MKV", FileCategory.Video)]
    [InlineData("ubuntu-24.04.iso", FileCategory.DiskImage)]
    [InlineData("disk.vmdk", FileCategory.VirtualMachine)]
    [InlineData("ext4.vhdx", FileCategory.VirtualMachine)]
    [InlineData("backup.tar.gz", FileCategory.Archive)]
    [InlineData("setup.msi", FileCategory.Installer)]
    [InlineData("pakchunk0-WindowsNoEditor.pak", FileCategory.GameData)]
    [InlineData("memory.dmp", FileCategory.LogsAndDumps)]
    [InlineData("README", FileCategory.Other)]
    [InlineData("trailingdot.", FileCategory.Other)]
    [InlineData(".gitignore", FileCategory.Other)]
    [InlineData("weird.verylongextensionname", FileCategory.Other)]
    [InlineData("", FileCategory.Other)]
    public void Classifies_by_extension_case_insensitively(string name, FileCategory expected) =>
        Assert.Equal(expected, ExtensionClassifier.Classify(name));

    [Fact]
    public void All_categories_have_names()
    {
        foreach (var c in FileCategoryInfo.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(FileCategoryInfo.DisplayName(c)));
        }

        Assert.Equal(FileCategoryInfo.Count, FileCategoryInfo.All.Count);
    }
}

public class SizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(800L << 20, "800 MB")]
    [InlineData(183_400_000_000L, "171 GB")]
    [InlineData(42L << 30, "42.0 GB")]
    [InlineData(1L << 40, "1.00 TB")]
    public void Formats_like_explorer(long bytes, string expected)
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal(expected, SizeFormatter.Format(bytes));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("5GB", 5L << 30)]
    [InlineData("1.5 gb", 3L << 29)]
    [InlineData("1,5 GB", 3L << 29)]
    [InlineData("1,000MB", 1000L << 20)]
    [InlineData("1,000.5 KB", 1_024_512)]
    [InlineData("2PB", 2L << 50)]
    [InlineData("500MB", 500L << 20)]
    [InlineData("100k", 100L << 10)]
    [InlineData("42", 42L)]
    [InlineData("2T", 2L << 40)]
    public void Parses_sizes(string text, long expected)
    {
        Assert.True(SizeFormatter.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("5 parsecs")]
    [InlineData("1,5.5GB")]
    [InlineData("1,000,5GB")]
    [InlineData("99999999PB")]
    public void Rejects_invalid_sizes(string text) => Assert.False(SizeFormatter.TryParse(text, out _));
}

public class SearchTests
{
    [Fact]
    public void Extension_search_matches_files_only()
    {
        var tree = ScanTreeTests.Sample();
        var results = SearchEngine.Search(tree, SearchQuery.Parse(".iso"));
        var hit = Assert.Single(results.Hits);
        Assert.True(hit.IsFile);
        Assert.Equal("ubuntu.iso", tree.File(hit.Index).Name);
    }

    [Fact]
    public void Wildcards_substrings_and_size_filters_combine()
    {
        var tree = ScanTreeTests.Sample();
        Assert.Single(SearchEngine.Search(tree, SearchQuery.Parse("*.mkv")).Hits);
        Assert.Equal(2, SearchEngine.Search(tree, SearchQuery.Parse(">2GB .iso")).Hits.Count + SearchEngine.Search(tree, SearchQuery.Parse(">2GB *.mkv")).Hits.Count);
        Assert.Empty(SearchEngine.Search(tree, SearchQuery.Parse(">10GB .iso")).Hits);

        var downloads = SearchEngine.Search(tree, SearchQuery.Parse("download"));
        Assert.False(Assert.Single(downloads.Hits).IsFile);

        var big = SearchEngine.Search(tree, SearchQuery.Parse("> 5GB"));
        Assert.All(big.Hits, h => Assert.True(h.Size > 5L << 30));
        Assert.Equal(big.Hits.OrderByDescending(h => h.Size), big.Hits);
    }

    [Fact]
    public void Dot_names_find_folders_with_that_exact_name()
    {
        var tree = ScanTreeTests.Sample();
        int me = tree.FindDirectory(@"C:\Users\me");
        int git = tree.AddDirectory(me, ".git");
        tree.AddDirectory(me, "my.git.backup");
        tree.CompleteDirectory(git, 10L << 20, 3, 0);

        var hit = Assert.Single(SearchEngine.Search(tree, SearchQuery.Parse(".git")).Hits);
        Assert.False(hit.IsFile);
        Assert.Equal(git, hit.Index);
        Assert.Empty(SearchEngine.Search(tree, SearchQuery.Parse(".git type:video")).Hits);
    }

    [Fact]
    public void Type_filter_and_quoted_terms()
    {
        var tree = ScanTreeTests.Sample();
        Assert.Equal("movie.mkv", tree.File(SearchEngine.Search(tree, SearchQuery.Parse("type:video")).Hits.Single().Index).Name);
        Assert.True(SearchQuery.Parse("").IsEmpty);
        Assert.Empty(SearchEngine.Search(tree, SearchQuery.Parse("\"not here\"")).Hits);
        Assert.Empty(SearchEngine.Search(tree, SearchQuery.Parse("type:video type:nonsense")).Hits);
        Assert.Single(SearchEngine.Search(tree, SearchQuery.Parse("type:video type:diskimage")).Hits, h => h.IsFile && tree.File(h.Index).Name == "movie.mkv");
    }
}

public class TreemapTests
{
    [Fact]
    public void Layout_fills_bounds_without_overlap_and_preserves_proportions()
    {
        var items = new List<(int, double)> { (0, 60), (1, 25), (2, 10), (3, 4), (4, 1) };
        var bounds = new TreemapRect(0, 0, 400, 300);
        var cells = SquarifiedTreemap.Layout<int>(items, bounds);

        Assert.Equal(5, cells.Count);
        Assert.Equal(bounds.Area, cells.Sum(c => c.Bounds.Area), 3);
        foreach (var cell in cells)
        {
            Assert.True(cell.Bounds.X >= -0.001 && cell.Bounds.Y >= -0.001);
            Assert.True(cell.Bounds.X + cell.Bounds.Width <= 400.001 && cell.Bounds.Y + cell.Bounds.Height <= 300.001);
            double expected = items[cell.Item].Item2 / 100.0 * bounds.Area;
            Assert.Equal(expected, cell.Bounds.Area, 3);
        }
    }

    [Fact]
    public void Empty_or_degenerate_input_produces_no_cells()
    {
        Assert.Empty(SquarifiedTreemap.Layout<int>([], new TreemapRect(0, 0, 10, 10)));
        Assert.Empty(SquarifiedTreemap.Layout<int>([(1, 5)], new TreemapRect(0, 0, 0, 10)));
    }
}

public class PathUtilTests
{
    [Theory]
    [InlineData(@"c:", @"C:\")]
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"C:\Users\", @"C:\Users")]
    [InlineData(@"\\?\C:\Users", @"C:\Users")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"""C:\Program Files\App""", @"C:\Program Files\App")]
    [InlineData(@"C:/Users/me", @"C:\Users\me")]
    public void Normalizes_paths(string input, string expected) => Assert.Equal(expected, PathUtil.NormalizeDisplayPath(input));

    [Fact]
    public void Containment_is_component_aware()
    {
        Assert.True(PathUtil.IsSameOrUnder(@"C:\Program Files\App", @"C:\Program Files"));
        Assert.False(PathUtil.IsSameOrUnder(@"C:\Program Files (x86)\App", @"C:\Program Files"));
        Assert.True(PathUtil.IsStrictlyUnder(@"C:\Windows\System32", @"c:\windows"));
        Assert.False(PathUtil.IsStrictlyUnder(@"C:\Windows", @"C:\Windows"));
        Assert.Equal(@"C:\", PathUtil.GetParent(@"C:\Windows"));
        Assert.Null(PathUtil.GetParent(@"C:\"));
    }
}

public class LocationClassifierTests
{
    [Fact]
    public void Nested_categories_are_not_double_counted()
    {
        var tree = ScanTreeTests.Sample();
        int users = tree.FindDirectory(@"C:\Users");
        int downloads = tree.FindDirectory(@"C:\Users\me\Downloads");
        int games = tree.FindDirectory(@"C:\Games");
        var map = new Dictionary<int, LocationCategory>
        {
            [users] = LocationCategory.UserFiles,
            [downloads] = LocationCategory.Downloads,
            [games] = LocationCategory.Games,
        };

        var totals = LocationClassifier.Summarize(tree, map).ToDictionary(t => t.Category, t => t.Size);

        Assert.Equal(20L << 30, totals[LocationCategory.Games]);
        Assert.Equal((8L << 30) + 5000, totals[LocationCategory.Downloads]);
        Assert.Equal(3L << 30, totals[LocationCategory.UserFiles]);
        Assert.False(totals.ContainsKey(LocationCategory.Other));
        Assert.Equal(tree.Root.TotalSize, totals.Values.Sum());
    }
}
