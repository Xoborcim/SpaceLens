using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Windows.Shell;

namespace SpaceLens.App.Controls;

/// <summary>
/// Detector findings grouped by kind (Steam games, node_modules, browser caches, …). Each group
/// explains what the data is and the proper way to reduce it before any action is offered.
/// </summary>
public sealed partial class FindingsView : UserControl
{
    private const string EmptyRecycleBinUri = "spacelens:emptyrecyclebin";
    private string? _groupAction;
    private string? _selectGroup;
    private LocationCategory? _selectCategory;

    public FindingsView()
    {
        InitializeComponent();
        ItemActions.AttachListBehaviors(ItemList);
    }

    public Func<StorageFinding, bool> Filter { get; set; } = _ => true;

    public string NoResultsMessage { get; set; } = "Nothing found.";

    private static AppState State => AppState.Current;

    /// <summary>Selects a group by name or by location category the next time results are shown.</summary>
    public void Select(object? parameter)
    {
        _selectGroup = parameter as string;
        _selectCategory = parameter as LocationCategory?;
        Refresh();
    }

    public void Refresh()
    {
        var tree = State.Tree;
        string? previous = (GroupList.SelectedItem as SummaryItem)?.Name;
        if (tree is null || State.IsScanning || State.AnalysisRunning)
        {
            GroupList.ItemsSource = null;
            ItemList.ItemsSource = null;
            ShowGroup(null);
            EmptyText.Text = tree is null ? "Scan a drive to find these locations." : State.IsScanning ? "Available when the scan finishes." : "Analyzing…";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        var groups = State.Findings
            .Where(Filter)
            .GroupBy(f => f.Group)
            .Select(g => (Name: g.Key, Items: g.OrderByDescending(f => f.Size).ToList()))
            .Select(g => (g.Name, g.Items, Size: g.Items.Sum(f => f.Size)))
            .OrderByDescending(g => g.Size)
            .ToList();

        long max = groups.Count > 0 ? groups[0].Size : 0;
        var rows = groups.Select(g => new SummaryItem
        {
            Name = g.Name,
            Size = g.Size,
            CountText = g.Items.Count == 1 ? "1 location" : $"{g.Items.Count} locations",
            Subtitle = StorageNatureInfo.Label(g.Items[0].Nature),
            Glyph = EntryItem.GlyphFor(g.Items[0].Category),
            BarWidth = SummaryItem.Bar(g.Size, max),
            Tag = g.Items,
        }).ToList();

        GroupList.ItemsSource = rows;
        EmptyText.Text = NoResultsMessage;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SummaryItem? target = null;
        if (_selectGroup is not null)
        {
            target = rows.FirstOrDefault(r => r.Name == _selectGroup);
        }
        else if (_selectCategory is { } category)
        {
            target = rows.FirstOrDefault(r => ((List<StorageFinding>)r.Tag!).Any(f => f.Category == category));
        }

        _selectGroup = null;
        _selectCategory = null;
        GroupList.SelectedItem = target ?? rows.FirstOrDefault(r => r.Name == previous) ?? rows.FirstOrDefault();
        if (GroupList.SelectedItem is null)
        {
            ShowGroup(null);
        }
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e) => ShowGroup(GroupList.SelectedItem as SummaryItem);

    private void ShowGroup(SummaryItem? group)
    {
        var tree = State.Tree;
        Details.Show(null);
        SelectionBar.Visibility = Visibility.Collapsed;
        if (group?.Tag is not List<StorageFinding> findings || tree is null)
        {
            GroupTitle.Text = "";
            GroupExplanation.Text = "";
            GroupAdvice.IsOpen = false;
            ItemList.ItemsSource = null;
            return;
        }

        var first = findings[0];
        GroupTitle.Text = $"{group.Name}  ·  {group.SizeText}";
        GroupExplanation.Text = first.Explanation ?? "";

        // A group-level action exists when every location shares it (e.g. Storage Sense, Empty Recycle Bin).
        _groupAction = findings.All(f => f.ActionUri == first.ActionUri) ? first.ActionUri : null;
        bool allowsRemoval = findings.Any(f => f.AllowDirectRemoval);
        GroupAdvice.IsOpen = first.Advice is not null || _groupAction is not null;
        GroupAdvice.Title = allowsRemoval ? "Before removing" : "How to reduce this";
        GroupAdvice.Message = first.Advice ?? "";
        GroupActionButton.Visibility = _groupAction is not null ? Visibility.Visible : Visibility.Collapsed;
        GroupActionButton.Content = first.ActionLabel ?? "Open";
        GroupActionButton.IsEnabled = _groupAction != EmptyRecycleBinUri || State.CanModify;

        var items = new List<EntryItem>(findings.Count);
        foreach (var f in findings)
        {
            EntryItem item;
            if (f.FileIndex >= 0 && tree.IsLiveFile(f.FileIndex))
            {
                item = EntryItem.ForFile(tree, f.FileIndex);
            }
            else if (f.DirectoryIndex >= 0 && tree.IsLiveDirectory(f.DirectoryIndex))
            {
                item = EntryItem.ForDirectory(tree, f.DirectoryIndex);
            }
            else if (f.FileIndex < 0 && f.DirectoryIndex < 0)
            {
                item = EntryItem.ForExternal(f);
            }
            else
            {
                continue;
            }

            item.Finding = f;
            item.Name = f.Title;
            item.Subtitle = f.Path;
            items.Add(item);
        }

        long max = items.Count > 0 ? items.Max(i => i.Size) : 0;
        foreach (var item in items)
        {
            item.BarWidth = SummaryItem.Bar(item.Size, max);
        }

        ItemList.ItemsSource = items;
    }

    /// <summary>The details column is only shown when the item list still has room for names.</summary>
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 1180;
        DetailsColumn.Width = wide ? new GridLength(320) : new GridLength(0);
        Details.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        LayoutRoot.ColumnSpacing = wide ? 16 : 8;
    }

    private void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = ItemList.SelectedItems.OfType<EntryItem>().ToList();
        Details.Show(selected.Count == 1 ? selected[0] : null);

        var removable = selected.Where(ItemActions.CanRecycle).ToList();
        SelectionBar.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = selected.Count == 1
            ? $"{selected[0].Name}  ·  {selected[0].SizeText}"
            : $"{selected.Count} selected  ·  {SizeFormatter.Format(selected.Sum(i => i.Size))}";
        RevealSelectedButton.Visibility = selected.Count == 1 ? Visibility.Visible : Visibility.Collapsed;

        var finding = selected.Count == 1 ? selected[0].Finding : null;
        bool hasAction = finding is { ActionUri: { } uri, ActionLabel: not null } && !uri.StartsWith("spacelens:", StringComparison.Ordinal) && uri != _groupAction;
        ItemActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        ItemActionButton.Content = finding?.ActionLabel;

        RecycleSelectedButton.Visibility = removable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecycleSelectedButton.IsEnabled = removable.Count == selected.Count;
    }

    private void OnRevealSelected(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is EntryItem item)
        {
            ItemActions.OpenInExplorer(item);
        }
    }

    private async void OnItemAction(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is EntryItem { Finding.ActionUri: { } uri })
        {
            var result = ShellActions.OpenUri(uri);
            if (!result.Success && result.Error is not null)
            {
                await ItemActions.ShowMessageAsync("Could not open", result.Error);
            }
        }
    }

    private async void OnRecycleSelected(object sender, RoutedEventArgs e)
    {
        var selected = ItemList.SelectedItems.OfType<EntryItem>().ToList();
        if (selected.Count > 0)
        {
            await ItemActions.RecycleAsync(selected);
        }
    }

    private async void OnGroupAction(object sender, RoutedEventArgs e)
    {
        if (_groupAction is null)
        {
            return;
        }

        if (_groupAction == EmptyRecycleBinUri)
        {
            await EmptyRecycleBinAsync();
            return;
        }

        var result = ShellActions.OpenUri(_groupAction);
        if (!result.Success && result.Error is not null)
        {
            await ItemActions.ShowMessageAsync("Could not open", result.Error);
        }
    }

    private static async Task EmptyRecycleBinAsync()
    {
        var tree = State.Tree;
        if (tree is null || !State.CanModify)
        {
            return;
        }

        string driveRoot = Path.GetPathRoot(tree.RootPath) ?? tree.RootPath;
        var (bytes, count) = ShellActions.QueryRecycleBin(driveRoot);
        var dialog = new ContentDialog
        {
            Title = "Empty the Recycle Bin?",
            Content = new TextBlock
            {
                Text = $"{SizeFormatter.FormatCount(count)} items ({SizeFormatter.Format(bytes)}) on {driveRoot.TrimEnd('\\')} will be permanently deleted. Items in the Recycle Bin cannot be restored afterwards.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = "Empty Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = State.XamlRoot,
        };

        if (await ItemActions.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var result = ShellActions.EmptyRecycleBin(driveRoot, State.WindowHandle);
        if (!result.Success)
        {
            if (!result.Cancelled)
            {
                await ItemActions.ShowMessageAsync("The Recycle Bin was not emptied", result.Error ?? "Unknown error.");
            }

            return;
        }

        // The per-user folders under $Recycle.Bin are now empty; reflect that in the results.
        int bin = tree.FindDirectory(Path.Combine(driveRoot, "$Recycle.Bin"));
        if (bin > 0)
        {
            var emptied = new List<(bool, int)>();
            foreach (int child in tree.GetChildren(bin))
            {
                emptied.AddRange(tree.GetChildren(child).Select(d => (false, d)));
                emptied.AddRange(tree.GetFiles(child).Select(f => (true, f)));
            }

            State.RemoveFromTree(tree, emptied);
        }
    }
}
