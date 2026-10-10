using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Search;

namespace SpaceLens.Desktop.ViewModels;

/// <summary>Searches the scan in memory (names, wildcards, sizes, dates, path:, -term, OR).</summary>
public sealed partial class SearchViewModel : PageViewModel
{
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;

    public SearchViewModel(MainViewModel main)
        : base(main, "Search", "⌕")
    {
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Refresh();
        });
        _debounce.Stop();
    }

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "Examples: .dmg   *.mov   node_modules   >5GB   older:1y   path:Library -Caches   .iso OR .dmg";

    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Runs the current query right away (Enter).</summary>
    public Task RunNowAsync()
    {
        _debounce.Stop();
        return SearchAsync();
    }

    protected override void OnRefresh() => _ = SearchAsync();

    private async Task SearchAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var tree = Main.Tree;
        if (tree is null || string.IsNullOrWhiteSpace(Query))
        {
            Items.Clear();
            Summary = tree is null ? "Scan a disk first; search runs over the scan results." : "Type to search the scan results.";
            return;
        }

        var query = SearchQuery.Parse(Query, units: Main.Platform.Units);
        SearchResults results;
        try
        {
            results = await Task.Run(() => SearchEngine.Search(tree, query, 1000, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested || Main.Tree != tree)
        {
            return;
        }

        Items.Clear();
        long max = results.Hits.Count > 0 ? results.Hits.Max(h => h.Size) : 1;
        foreach (var hit in results.Hits)
        {
            var item = hit.IsFile ? ItemViewModel.ForFile(Main, tree, hit.Index) : ItemViewModel.ForFolder(Main, tree, hit.Index);
            item.Subtitle = Core.Models.PathUtil.GetParent(item.Path) ?? item.Path;
            item.Fraction = (double)item.Size / Math.Max(1, max);
            Items.Add(item);
        }

        string shown = results.TotalMatches > Items.Count ? $" (showing the largest {SizeFormatter.FormatCount(Items.Count)})" : "";
        Summary = $"{SizeFormatter.FormatCount(results.TotalMatches)} matches{shown}  ·  {results.Elapsed.TotalMilliseconds:0} ms";
    }
}
