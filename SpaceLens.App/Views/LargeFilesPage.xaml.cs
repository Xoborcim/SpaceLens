using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;

namespace SpaceLens.App.Views;

public sealed partial class LargeFilesPage : Page
{
    private const int MaxResults = 5000;
    private int _version;
    private bool _dirty = true;
    private readonly bool _initialized;

    public LargeFilesPage()
    {
        InitializeComponent();
        TypeFilter.Items.Add(new ComboBoxItem { Content = "All types", IsSelected = true });
        foreach (var category in FileCategoryInfo.All)
        {
            TypeFilter.Items.Add(new ComboBoxItem { Content = FileCategoryInfo.DisplayName(category), Tag = category });
        }

        ItemActions.AttachListBehaviors(FileList);
        State.TreeReplaced += (_, _) => MarkDirty();
        State.ScanFinished += (_, _) => MarkDirty();
        State.TreeMutated += (_, _) => MarkDirty();
        State.AnalysisChanged += (_, _) => MarkDirty();
        State.LiveRefresh += (_, _) =>
        {
            // Don't reshuffle the list under the user's selection while a scan is running.
            if (FileList.SelectedItems.Count == 0)
            {
                MarkDirty();
            }
        };
        _initialized = true;
    }

    public AppState State => AppState.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_dirty)
        {
            _ = RefreshAsync();
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        if (IsLoaded && ReferenceEquals(Frame?.Content, this))
        {
            _ = RefreshAsync();
        }
    }

    private long MinimumSize
    {
        get
        {
            var tag = (SizeFilter.SelectedItem as ComboBoxItem)?.Tag as string;
            if (tag == "custom")
            {
                double mb = double.IsNaN(CustomSize.Value) ? 500 : CustomSize.Value;
                return (long)(Math.Max(1, mb) * 1024 * 1024);
            }

            return long.TryParse(tag, out long bytes) ? bytes : 1L << 30;
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        CustomSize.Visibility = (SizeFilter.SelectedItem as ComboBoxItem)?.Tag as string == "custom" ? Visibility.Visible : Visibility.Collapsed;
        _ = RefreshAsync();
    }

    private void OnCustomChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initialized)
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        _dirty = false;
        var tree = State.Tree;
        if (tree is null)
        {
            FileList.ItemsSource = null;
            SummaryText.Text = "";
            EmptyText.Text = "Scan a drive to find its largest files.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        int version = ++_version;
        long minimum = Math.Max(MinimumSize, tree.FileIndexThreshold);
        FileCategory? category = (TypeFilter.SelectedItem as ComboBoxItem)?.Tag is FileCategory c ? c : null;
        var indices = await Task.Run(() => Breakdown.LargeFiles(tree, minimum, category, MaxResults));
        if (version != _version || tree != State.Tree)
        {
            return;
        }

        var items = indices.Select(i => EntryItem.ForFile(tree, i)).ToList();
        long max = items.Count > 0 ? items[0].Size : 0;
        foreach (var item in items)
        {
            item.Subtitle = item.ParentPath;
            item.BarWidth = SummaryItem.Bar(item.Size, max);
        }

        FileList.ItemsSource = items;
        long total = items.Sum(i => i.Size);
        SummaryText.Text = items.Count == 0 ? "" :
            $"{SizeFormatter.FormatCount(items.Count)}{(items.Count == MaxResults ? "+" : "")} files  ·  {SizeFormatter.Format(total)} in total" +
            (State.IsScanning ? "  ·  scan in progress" : "");
        EmptyText.Text = $"No files over {SizeFormatter.Format(minimum)}" + (category is { } cat ? $" of type {FileCategoryInfo.DisplayName(cat)}" : "") + ".";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Details.Show(null);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Details.Show(FileList.SelectedItems.Count == 1 ? FileList.SelectedItem as EntryItem : null);
        if (FileList.SelectedItems.Count > 1)
        {
            long total = FileList.SelectedItems.OfType<EntryItem>().Sum(i => i.Size);
            SummaryText.Text = $"{FileList.SelectedItems.Count} selected  ·  {SizeFormatter.Format(total)}";
        }
    }
}
