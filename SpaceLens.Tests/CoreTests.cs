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

    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>D:\old.iso (3 years old), D:\recent.mkv (10 days old), D:\unknown.bin (no date), D:\Archive folder.</summary>
    private static ScanTree DatedTree()
    {
        var tree = new ScanTree(@"D:\");
        int archive = tree.AddDirectory(ScanTree.RootIndex, "Archive", lastWriteUtc: Now.AddYears(-5).ToFileTimeUtc());
        tree.AddFile(ScanTree.RootIndex, "old.iso", 4L << 30, FileCategory.DiskImage, Now.AddYears(-3).ToFileTimeUtc());
        tree.AddFile(ScanTree.RootIndex, "recent.mkv", 2L << 30, FileCategory.Video, Now.AddDays(-10).ToFileTimeUtc());
        tree.AddFile(ScanTree.RootIndex, "unknown.bin", 1L << 30, FileCategory.Other, 0);
        tree.CompleteDirectory(archive, 5L << 30, 10, 0);
        tree.CompleteDirectory(ScanTree.RootIndex, 7L << 30, 3, 1);
        return tree;
    }

    private static List<string> Names(ScanTree tree, string query) =>
        SearchEngine.Search(tree, SearchQuery.Parse(query, Now)).Hits
            .Select(h => h.IsFile ? tree.File(h.Index).Name : tree.Dir(h.Index).Name).ToList();

    [Fact]
    public void Older_and_newer_filter_files_by_modification_date()
    {
        var tree = DatedTree();
        Assert.Equal(["old.iso"], Names(tree, "older:1y"));
        Assert.Equal(["old.iso"], Names(tree, "older:2024-01-01"));
        Assert.Equal(["recent.mkv"], Names(tree, "newer:30d"));
        Assert.Equal(["recent.mkv"], Names(tree, "newer:2w"));
        Assert.Equal(["old.iso", "recent.mkv"], Names(tree, "older:1w"));
        Assert.Empty(Names(tree, "older:1y newer:2y"));
        Assert.Equal(["old.iso"], Names(tree, "older:1y newer:4y"));
        Assert.Equal(["old.iso"], Names(tree, "older:6m >3GB"));
        Assert.Empty(Names(tree, "older:soon"));
    }

    [Theory]
    [InlineData("30d", 30.0)]
    [InlineData("45", 45.0)]
    [InlineData("1000", 1000.0)]
    [InlineData("2w", 14.0)]
    public void Ages_are_parsed_in_days_and_weeks(string text, double days)
    {
        Assert.True(SearchQuery.TryParseCutoff(text, Now, out var cutoff));
        Assert.Equal(Now.AddDays(-days), cutoff);
    }

    [Fact]
    public void Ages_in_months_and_years_and_dates_are_parsed()
    {
        Assert.True(SearchQuery.TryParseCutoff("6m", Now, out var months));
        Assert.Equal(Now.AddMonths(-6), months);
        Assert.True(SearchQuery.TryParseCutoff("2Y", Now, out var years));
        Assert.Equal(Now.AddYears(-2), years);
        Assert.True(SearchQuery.TryParseCutoff("2024", Now, out var year));
        Assert.Equal(2024, year.ToLocalTime().Year);
        Assert.False(SearchQuery.TryParseCutoff("", Now, out _));
        Assert.False(SearchQuery.TryParseCutoff("3x", Now, out _));
        Assert.False(SearchQuery.TryParseCutoff("-5d", Now, out _));
    }

    [Fact]
    public void Large_files_can_be_limited_to_files_not_modified_recently()
    {
        var tree = DatedTree();
        var old = Breakdown.LargeFiles(tree, 0, modifiedBefore: Now.AddYears(-1).ToFileTimeUtc());
        Assert.Equal(["old.iso"], old.Select(i => tree.File(i).Name));
        Assert.Equal(3, Breakdown.LargeFiles(tree, 0).Count);
    }

    [Fact]
    public void Or_separates_alternatives_and_binds_weaker_than_spaces()
    {
        var tree = ScanTreeTests.Sample();
        Assert.Equal(["ubuntu.iso", "movie.mkv"], SearchNames(tree, ".iso OR .mkv"));
        Assert.Equal(["ubuntu.iso", "movie.mkv"], SearchNames(tree, ".iso | .mkv"));
        Assert.Equal(["movie.mkv"], SearchNames(tree, ".iso >10GB OR .mkv"));
        Assert.Equal(["ubuntu.iso"], SearchNames(tree, "OR .iso OR"));
        Assert.Empty(SearchNames(tree, "\"OR\""));
    }

    [Fact]
    public void Minus_excludes_any_kind_of_term()
    {
        var tree = ScanTreeTests.Sample();
        Assert.Equal(["movie.mkv"], SearchNames(tree, ">1GB -.iso -Big -Games -Users -me -Downloads -Videos"));
        Assert.Equal(["ubuntu.iso"], SearchNames(tree, ">1GB -type:video -path:Games -Users -me -Downloads -Videos"));
        Assert.DoesNotContain("Videos", SearchNames(tree, "-path:Videos"));
        Assert.Contains("Downloads", SearchNames(tree, "-path:Videos"));

        // Only exclusions: everything else matches.
        Assert.Equal(SearchNames(tree, ">0").Count - 1, SearchNames(tree, "-Big").Count);
    }

    [Fact]
    public void Path_matches_the_full_path_and_quotes_group_spaces()
    {
        var tree = ScanTreeTests.Sample();
        int me = tree.FindDirectory(@"C:\Users\me");
        int saved = tree.AddDirectory(me, "Saved Games");
        tree.AddFile(saved, "save.dat", 2L << 20, FileCategory.Other, 0);
        tree.CompleteDirectory(saved, 2L << 20, 1, 0);

        Assert.Equal(["ubuntu.iso"], SearchNames(tree, "path:Downloads .iso"));
        Assert.Equal(["ubuntu.iso", "movie.mkv", "save.dat"], SearchNames(tree, @"path:Users\me type:other OR path:users/me .iso OR path:users\me .mkv"));
        Assert.Equal(["Saved Games", "save.dat"], SearchNames(tree, "path:\"Saved Games\""));
        Assert.Equal(["save.dat"], SearchNames(tree, "save \"path:x\" OR -\"Saved\" save"));
    }

    private static List<string> SearchNames(ScanTree tree, string query) =>
        SearchEngine.Search(tree, SearchQuery.Parse(query)).Hits
            .Select(h => h.IsFile ? tree.File(h.Index).Name : tree.Dir(h.Index).Name).ToList();

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
