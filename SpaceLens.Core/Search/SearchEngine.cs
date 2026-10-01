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
                    if (query.MatchesSize(node.TotalSize) && query.MatchesName(node.Name, isFile: false))
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
                if (query.MatchesSize(file.Size) && query.MatchesName(file.Name, isFile: true, file.Category))
                {
                    matches++;
                    top.Offer(file.Size, new SearchHit(true, i, file.Size));
                }
            }
        }

        var hits = top.ToSortedList().ConvertAll(static x => x.Value);
        return new SearchResults(hits, matches, System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }
}
