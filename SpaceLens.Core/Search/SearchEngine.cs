using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.Search;

public readonly record struct SearchHit(bool IsFile, int Index, long Size);

public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, int TotalMatches, TimeSpan Elapsed);

/// <summary>
/// Searches the in-memory scan index (all directories plus indexed files). Never touches the disk.
/// </summary>
public static class SearchEngine
{
    public static SearchResults Search(ScanTree tree, SearchQuery query, int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var top = new TopN<SearchHit>(maxResults);
        int matches = 0;

        if (!query.IsEmpty)
        {
            var paths = new PathCache(tree);
            var item = new SearchCandidate(c => c.IsFile ? paths.FilePath(c.Index) : paths.DirectoryPath(c.Index));

            if (!query.FilesOnly)
            {
                int dirCount = tree.DirectoryCount;
                for (int i = 1; i < dirCount; i++)
                {
                    if ((i & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (!tree.IsLiveDirectory(i))
                    {
                        continue;
                    }

                    ref var node = ref tree.Dir(i);
                    if (query.Matches(item.Set(isFile: false, i, node.Name, node.TotalSize, lastWriteUtc: node.LastWriteUtc)))
                    {
                        matches++;
                        top.Offer(node.TotalSize, new SearchHit(false, i, node.TotalSize));
                    }
                }
            }

            int fileCount = tree.FileRecordCount;
            for (int i = 0; i < fileCount; i++)
            {
                if ((i & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!tree.IsLiveFile(i))
                {
                    continue;
                }

                ref var file = ref tree.File(i);
                if (query.Matches(item.Set(isFile: true, i, file.Name, file.Size, file.Category, file.LastWriteUtc)))
                {
                    matches++;
                    top.Offer(file.Size, new SearchHit(true, i, file.Size));
                }
            }
        }

        var hits = top.ToSortedList().ConvertAll(static x => x.Value);
        return new SearchResults(hits, matches, System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Directory paths built from the parent's cached path, so a <c>path:</c> or <c>-path:</c> term over a
    /// million items costs one string per directory rather than one walk to the root per item.
    /// </summary>
    private sealed class PathCache(ScanTree tree)
    {
        private readonly string?[] _paths = new string?[tree.DirectoryCount];

        public string DirectoryPath(int index)
        {
            if (index >= _paths.Length)
            {
                return tree.GetPath(index); // created after the search started (live scan)
            }

            if (_paths[index] is { } cached)
            {
                return cached;
            }

            // Collect the uncached ancestors, then fill them in from the top down.
            var pending = new Stack<int>();
            int d = index;
            while (d > ScanTree.RootIndex && _paths[d] is null)
            {
                pending.Push(d);
                d = tree.Dir(d).Parent;
            }

            string path = d <= ScanTree.RootIndex ? tree.RootPath : _paths[d]!;
            while (pending.Count > 0)
            {
                int next = pending.Pop();
                path = PathUtil.Combine(path, tree.Dir(next).Name);
                _paths[next] = path;
            }

            return path;
        }

        public string FilePath(int fileIndex) => PathUtil.Combine(DirectoryPath(tree.File(fileIndex).Directory), tree.File(fileIndex).Name);
    }
}
