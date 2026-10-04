using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Comparison;
using SpaceLens.Core.Models;

namespace SpaceLens.App.Views;

/// <summary>
/// What changed since the previous scan of the same folder or drive. The previous scan is read from
/// its saved snapshot only while the comparison runs, so it costs no memory afterwards.
/// </summary>
public sealed partial class ChangesPage : Page
{
    private int _version;
    private bool _dirty = true;

    public ChangesPage()
    {
        InitializeComponent();
        State.TreeReplaced += (_, _) => MarkDirty();
        State.ScanFinished += (_, _) => MarkDirty();
        State.TreeMutated += (_, _) => MarkDirty();
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

    private async Task RefreshAsync()
    {
        _dirty = false;
        int version = ++_version;
        var tree = State.Tree;
        ChangeList.ItemsSource = null;
        TotalsPanel.Visibility = Visibility.Collapsed;

        if (tree is null)
        {
            ShowMessage("", "Scan a drive or folder. After the next scan of the same location, this page shows what grew and what was freed in between.");
            return;
        }

        if (State.IsScanning)
        {
            ShowMessage($"Scanning {tree.RootPath}", "Changes are shown when the scan finishes.");
            return;
        }

        if (tree.Metadata.WasCancelled)
        {
            ShowMessage(tree.RootPath, "The last scan was cancelled. Its partial results can't be compared with the previous scan; run a full scan to see changes.");
            return;
        }

        ShowMessage($"Comparing {tree.RootPath} with the previous scan…", "", busy: true);
        ScanComparison? comparison;
        try
        {
            comparison = await Task.Run(() =>
            {
                var previous = SnapshotStore.TryLoadPrevious(tree.RootPath);
                if (previous is null)
                {
                    return null;
                }

                // Removals on the UI thread take the same lock; the comparison only touches a few folders.
                lock (tree.SyncRoot)
                {
                    return ScanComparer.Compare(previous, tree);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            comparison = null;
        }

        if (version != _version || tree != State.Tree)
        {
            return;
        }

        if (comparison is null)
        {
            ShowMessage(tree.RootPath, $"There is no earlier scan of {tree.RootPath} to compare with yet. Changes appear here after the next full scan.");
            return;
        }

        string between = $"Between {AppState.FormatWhen(comparison.PreviousScanUtc)} and {AppState.FormatWhen(comparison.CurrentScanUtc)}";
        SummaryText.Text = $"{between}  ·  {tree.RootPath}";
        NetText.Text = ChangeItem.FormatDelta(comparison.NetChange);
        GrownText.Text = ChangeItem.FormatDelta(comparison.Grown);
        FreedText.Text = ChangeItem.FormatDelta(-comparison.Freed);
        TotalsPanel.Visibility = Visibility.Visible;

        long maxDelta = comparison.Changes.Count > 0 ? comparison.Changes.Max(c => Math.Abs(c.Delta)) : 0;
        var items = comparison.Changes.Select(c => new ChangeItem(c, maxDelta)).ToList();
        ChangeList.ItemsSource = items;
        if (items.Count == 0)
        {
            ShowMessage(SummaryText.Text, "No folder changed noticeably between the two scans.");
        }
        else
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowMessage(string summary, string message, bool busy = false)
    {
        SummaryText.Text = summary;
        EmptyText.Text = message;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private void ShowInFolders(ChangeItem item)
    {
        if (item.ExistsNow)
        {
            State.RequestNavigation("folders", item.Path);
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ChangeItem item)
        {
            ShowInFolders(item);
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter && ChangeList.SelectedItem is ChangeItem item)
        {
            ShowInFolders(item);
            e.Handled = true;
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement;
        if ((element?.DataContext ?? (element as ListViewItem)?.Content) is not ChangeItem item)
        {
            return;
        }

        ChangeList.SelectedItem = item;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var menuItem = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            menuItem.Click += (_, _) => action();
            menu.Items.Add(menuItem);
        }

        Add("Show in Folders", "", () => ShowInFolders(item), enabled: item.ExistsNow);
        Add("Open in Explorer", "", () => ItemActions.OpenFolder(item.Path), enabled: item.ExistsNow);
        Add("Copy path", "", () => ItemActions.CopyText(item.Path));

        if (e.TryGetPosition(ChangeList, out var point))
        {
            menu.ShowAt(ChangeList, point);
        }
        else
        {
            menu.ShowAt(element ?? ChangeList);
        }

        e.Handled = true;
    }
}
