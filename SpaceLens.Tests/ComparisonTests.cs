using SpaceLens.Core.Comparison;
using SpaceLens.Core.Models;

namespace SpaceLens.Tests;

public class ComparisonTests
{
    private const long MB = 1L << 20;
    private const long GB = 1L << 30;

    /// <summary>Builds a tree from (path, own size) pairs; parent folders are created as needed.</summary>
    private static ScanTree Tree(DateTime completed, params (string Path, long OwnSize)[] folders)
    {
        var tree = new ScanTree(@"C:\");
        tree.Metadata.CompletedUtc = completed;
        var own = new Dictionary<int, long>();
        foreach (var (path, size) in folders)
        {
            int current = ScanTree.RootIndex;
            foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                int child = tree.FindChild(current, segment);
                current = child >= 0 ? child : tree.AddDirectory(current, segment);
            }

            own[current] = own.GetValueOrDefault(current) + size;
        }

        for (int i = 0; i < tree.DirectoryCount; i++)
        {
            tree.CompleteDirectory(i, own.GetValueOrDefault(i), own.ContainsKey(i) ? 1 : 0, 0);
        }

        return tree;
    }

    private static readonly DateTime Earlier = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Growth_is_attributed_to_the_folder_that_grew()
    {
        var before = Tree(Earlier,
            (@"Users\me\Downloads", 2 * GB), (@"Users\me\Documents", 1 * GB), (@"Windows", 20 * GB));
        var after = Tree(Later,
            (@"Users\me\Downloads", 7 * GB), (@"Users\me\Documents", 1 * GB), (@"Windows", 20 * GB));

        var result = ScanComparer.Compare(before, after);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeKind.Grew, change.Kind);
        Assert.Equal(@"C:\Users\me\Downloads", change.Path);
        Assert.Equal(5 * GB, change.Delta);
        Assert.Equal(5 * GB, result.NetChange);
        Assert.Equal(Earlier, result.PreviousScanUtc);
        Assert.Equal(after.FindDirectory(@"C:\Users\me\Downloads"), change.CurrentIndex);
    }

    [Fact]
    public void Added_and_removed_folders_are_reported_whole()
    {
        var before = Tree(Earlier, (@"Games\Old Game\data", 30 * GB), (@"Games\Old Game", 1 * GB), (@"Data", 1 * GB));
        var after = Tree(Later, (@"Games\New Game\data", 12 * GB), (@"Data", 1 * GB));

        var result = ScanComparer.Compare(before, after);

        var removed = Assert.Single(result.Changes, c => c.Kind == ChangeKind.Removed);
        Assert.Equal(@"C:\Games\Old Game", removed.Path);
        Assert.Equal(-31 * GB, removed.Delta);
        Assert.Equal(-1, removed.CurrentIndex);

        var added = Assert.Single(result.Changes, c => c.Kind == ChangeKind.Added);
        Assert.Equal(@"C:\Games\New Game", added.Path);
        Assert.Equal(12 * GB, added.Delta);

        Assert.Equal(31 * GB, result.Freed);
        Assert.Equal(12 * GB, result.Grown);
        Assert.Equal(-19 * GB, result.NetChange);
    }

    [Fact]
    public void Moves_are_found_even_when_the_total_is_unchanged()
    {
        var before = Tree(Earlier, (@"A", 10 * GB), (@"B", 1 * GB));
        var after = Tree(Later, (@"A", 4 * GB), (@"B", 7 * GB));

        var result = ScanComparer.Compare(before, after);

        Assert.Equal(0, result.NetChange);
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(6 * GB, result.Changes.Single(c => c.Path == @"C:\B").Delta);
        Assert.Equal(-6 * GB, result.Changes.Single(c => c.Path == @"C:\A").Delta);
    }

    [Fact]
    public void Loose_files_and_spread_out_changes_are_listed_separately()
    {
        var before = Tree(Earlier, (@"Users\me", 1 * GB), (@"Users\me\Videos", 1 * GB), (@"Users\me\Music", 1 * GB));
        var after = Tree(Later, (@"Users\me", 3 * GB), (@"Users\me\Videos", 4 * GB), (@"Users\me\Music", 3 * GB));

        var result = ScanComparer.Compare(before, after);

        Assert.Equal(3, result.Changes.Count);
        Assert.Contains(result.Changes, c => c.IsLooseFiles && c.Path == @"C:\Users\me" && c.Delta == 2 * GB);
        Assert.Contains(result.Changes, c => c.Path == @"C:\Users\me\Videos" && c.Delta == 3 * GB);
        Assert.Contains(result.Changes, c => c.Path == @"C:\Users\me\Music" && c.Delta == 2 * GB);
        Assert.Equal(result.Changes.OrderByDescending(c => Math.Abs(c.Delta)), result.Changes);
    }

    [Fact]
    public void Tiny_changes_and_identical_scans_produce_nothing()
    {
        var before = Tree(Earlier, (@"A", 10 * GB), (@"B", 100 * MB));
        var after = Tree(Later, (@"A", 10 * GB), (@"B", 100 * MB + 1000));

        Assert.Empty(ScanComparer.Compare(before, after).Changes);
        Assert.Empty(ScanComparer.Compare(before, before).Changes);
    }

    [Fact]
    public void Names_are_matched_case_insensitively()
    {
        var before = Tree(Earlier, (@"Projects", 5 * GB));
        var after = Tree(Later, (@"projects", 6 * GB));

        var change = Assert.Single(ScanComparer.Compare(before, after).Changes);
        Assert.Equal(ChangeKind.Grew, change.Kind);
        Assert.Equal(1 * GB, change.Delta);
    }
}
