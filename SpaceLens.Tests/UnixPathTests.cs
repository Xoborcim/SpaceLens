using SpaceLens.Core.Models;
using SpaceLens.Core.Scanning;
using SpaceLens.Core.Search;
using SpaceLens.Core.Snapshots;

namespace SpaceLens.Tests;

/// <summary>Unix-style paths (macOS, Linux) next to the Windows style the engine started with.</summary>
public class UnixPathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/Users/me/", "/Users/me")]
    [InlineData("/Users/me//", "/Users/me")]
    [InlineData("\"/Volumes/Backup Disk\"", "/Volumes/Backup Disk")]
    [InlineData("/users/Me", "/users/Me")] // case is kept
    public void Normalizes_unix_paths(string input, string expected) =>
        Assert.Equal(expected, PathUtil.NormalizeDisplayPath(input));

    [Fact]
    public void Names_parents_and_containment()
    {
        Assert.Equal("Downloads", PathUtil.GetName("/Users/me/Downloads"));
        Assert.Equal("/", PathUtil.GetName("/"));
        Assert.Equal("/Users/me", PathUtil.GetParent("/Users/me/Downloads"));
        Assert.Equal("/", PathUtil.GetParent("/Users"));
        Assert.Null(PathUtil.GetParent("/"));
        Assert.Equal("/Users/me/x", PathUtil.Combine("/Users/me", "x"));
        Assert.Equal("/x", PathUtil.Combine("/", "x"));
        Assert.Equal("/Users/me", PathUtil.ToLongPath("/Users/me"));

        Assert.True(PathUtil.IsSameOrUnder("/Users/me/Library/Caches", "/Users/me/Library"));
        Assert.True(PathUtil.IsSameOrUnder("/users/ME/library", "/Users/me/Library")); // APFS default is case-insensitive
        Assert.True(PathUtil.IsSameOrUnder("/anything", "/"));
        Assert.False(PathUtil.IsSameOrUnder("/Users/meandyou", "/Users/me"));
        Assert.False(PathUtil.IsSameOrUnder(@"C:\Users", "/"));
        Assert.False(PathUtil.IsSameOrUnder("/Users", @"C:\"));

        // Windows paths keep their behaviour.
        Assert.Equal(@"C:\Users", PathUtil.GetParent(@"C:\Users\me"));
        Assert.Equal(@"C:\Users\me\x", PathUtil.Combine(@"C:\Users\me", "x"));
    }

    [Fact]
    public void Trees_rooted_at_a_unix_path_build_and_find_paths()
    {
        var tree = new ScanTree("/");
        int users = tree.AddDirectory(ScanTree.RootIndex, "Users");
        int me = tree.AddDirectory(users, "me");
        int file = tree.AddFile(me, "movie.mov", 2L << 30, FileCategory.Video, 0);
        tree.CompleteDirectory(me, 2L << 30, 1, 0);

        Assert.Equal("/Users/me", tree.GetPath(me));
        Assert.Equal("/Users/me/movie.mov", tree.GetFilePath(file));
        Assert.Equal(me, tree.FindDirectory("/Users/me/"));
        Assert.Equal(-1, tree.FindDirectory(@"C:\Users\me"));

        var folder = new ScanTree("/Users/me/Projects");
        int app = folder.AddDirectory(ScanTree.RootIndex, "app");
        Assert.Equal("/Users/me/Projects/app", folder.GetPath(app));
        Assert.Equal(app, folder.FindDirectory("/Users/me/Projects/app"));

        // Snapshots keep the style.
        using var stream = new MemoryStream();
        SnapshotSerializer.Save(tree, stream);
        stream.Position = 0;
        var loaded = SnapshotSerializer.Load(stream);
        Assert.Equal("/", loaded.RootPath);
        Assert.Equal("/Users/me", loaded.GetPath(loaded.FindDirectory("/Users/me")));
    }

    [Fact]
    public void Path_terms_match_either_separator()
    {
        var tree = new ScanTree("/");
        int users = tree.AddDirectory(ScanTree.RootIndex, "Users");
        int me = tree.AddDirectory(users, "me");
        tree.AddFile(me, "movie.mov", 2L << 30, FileCategory.Video, 0);
        tree.CompleteDirectory(me, 2L << 30, 1, 0);

        Assert.Single(SearchEngine.Search(tree, SearchQuery.Parse("path:Users/me .mov")).Hits);
        Assert.Single(SearchEngine.Search(tree, SearchQuery.Parse(@"path:Users\me .mov")).Hits);
        Assert.Empty(SearchEngine.Search(tree, SearchQuery.Parse("path:Library .mov")).Hits);
    }

    [Fact]
    public async Task Excluded_paths_are_recorded_but_not_entered()
    {
        using var dir = new TestDirectory();
        dir.File(@"data\keep.bin", 3000);
        dir.File(@"Volumes\External\big.bin", 9000);
        dir.File(@"System\Volumes\Data\again.bin", 7000);

        var tree = new ScanTree(dir.Root, 1024);
        var options = new ScanOptions
        {
            MaxParallelism = 2,
            UseAllocatedSize = false,
            ExcludedPaths = [dir.PathOf("Volumes"), dir.PathOf(@"System\Volumes") + Path.DirectorySeparatorChar],
        };
        var result = await new ParallelDirectoryScanner(new ManagedDirectoryEnumeratorFactory()).ScanAsync(tree, options, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(3000, tree.Root.TotalSize);
        int volumes = tree.FindDirectory(dir.PathOf("Volumes"));
        Assert.True(volumes > 0);
        Assert.True((tree.Dir(volumes).Flags & NodeFlags.Excluded) != 0);
        Assert.Equal(0, tree.Dir(volumes).TotalSize);
        Assert.True((tree.Dir(tree.FindDirectory(dir.PathOf(@"System\Volumes"))).Flags & NodeFlags.Excluded) != 0);
        Assert.Equal(0, result.Errors);
    }
}
