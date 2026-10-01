using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Search;

namespace SpaceLens.App.Views;

public sealed partial class SearchPage : Page
{
    private const int MaxResults = 2000;
    private CancellationTokenSource? _cts;
    private string _query = "";

    public SearchPage()
    {
        InitializeComponent();
        ItemActions.AttachListBehaviors(ResultList);
        AppState.Current.TreeMutated += (_, _) =>
        {
            if (ReferenceEquals(Frame?.Content, this))
            {
                Search(_query);
            }
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string query)
        {
            Search(query);
        }
    }

    /// <summary>Runs a search over the in-memory index on a background thread; newer queries cancel older ones.</summary>
    public async void Search(string text)
    {
        _query = text;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var tree = AppState.Current.Tree;
        TitleText.Text = string.IsNullOrWhiteSpace(text) ? "Search" : $"Results for \u201c{text.Trim()}\u201d";

        if (tree is null)
        {
            SummaryText.Text = "Scan a drive first; search runs over the scan results.";
            ResultList.ItemsSource = null;
            HelpPanel.Visibility = Visibility.Visible;
            return;
        }

        var query = SearchQuery.Parse(text);
        SearchResults results;
        try
        {
            results = await Task.Run(() => SearchEngine.Search(tree, query, MaxResults, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        var items = results.Hits.Select(h => h.IsFile ? EntryItem.ForFile(tree, h.Index) : EntryItem.ForDirectory(tree, h.Index)).ToList();
        long max = items.Count > 0 ? items.Max(i => i.Size) : 0;
        foreach (var item in items)
        {
            item.Subtitle = item.ParentPath;
            item.BarWidth = SummaryItem.Bar(item.Size, max);
        }

        ResultList.ItemsSource = items;
        HelpPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        string shown = results.TotalMatches > items.Count ? $" (showing the largest {SizeFormatter.FormatCount(items.Count)})" : "";
        SummaryText.Text = $"{SizeFormatter.FormatCount(results.TotalMatches)} matches{shown}  ·  {results.Elapsed.TotalMilliseconds:0} ms" +
            (AppState.Current.IsScanning ? "  ·  scan in progress" : "");
        Details.Show(null);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Details.Show(ResultList.SelectedItems.Count == 1 ? ResultList.SelectedItem as EntryItem : null);
}
