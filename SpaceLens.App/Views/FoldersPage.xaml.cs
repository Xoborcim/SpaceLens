using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Models;

namespace SpaceLens.App.Views;

/// <summary>
/// Folder tree shown as a flat, virtualized list. The set of expanded folders is the only UI state;
/// the visible rows are recomputed from the scan tree on a background thread and reconciled into
/// the existing collection so scroll position and selection survive live updates.
/// </summary>
public sealed partial class FoldersPage : Page
{
    /// <summary>Rows shown per folder; the remainder is summarized in a single "more" row.</summary>
    private const int MaxChildrenPerFolder = 400;

    private readonly ObservableCollection<EntryItem> _rows = [];
    private readonly HashSet<int> _expanded = [ScanTree.RootIndex];
    private ScanTree? _tree;
    private int _version;
    private bool _rebuilding;
    private bool _rebuildPending;
    private (EntryKind Kind, int Index)? _pendingReveal;

    public FoldersPage()
    {
        InitializeComponent();
        TreeList.ItemsSource = _rows;
        ItemActions.AttachListBehaviors(TreeList, OnInvoke);

        State.TreeReplaced += (_, _) => ResetTree();
        State.LiveRefresh += (_, _) => RequestRebuild();
        State.ScanFinished += (_, _) => RequestRebuild();
        State.TreeMutated += (_, _) => RequestRebuild();
        State.AnalysisChanged += (_, _) => RecreateRows();
    }

    public AppState State => AppState.Current;

    public bool CanStartScan(bool isScanning) => !isScanning;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_tree != State.Tree)
        {
            ResetTree();
        }

        switch (e.Parameter)
        {
            case EntryItem item:
                Reveal(item.Kind, item.Index);
                break;
            case string path when State.Tree is { } tree && tree.FindDirectory(path) is >= 0 and var index:
                Reveal(EntryKind.Directory, index);
                break;
            default:
                RequestRebuild();
                break;
        }
    }

    private void ResetTree()
    {
        _tree = State.Tree;
        _version++;
        _rows.Clear();
        _expanded.Clear();
        _expanded.Add(ScanTree.RootIndex);
        Details.Show(null);
        RootText.Text = _tree?.RootPath ?? "";
        EmptyText.Visibility = _tree is null ? Visibility.Visible : Visibility.Collapsed;
        RequestRebuild();
    }

    /// <summary>Findings changed (labels, badges): rows are recreated so they pick up the new information.</summary>
    private void RecreateRows()
    {
        var selected = TreeList.SelectedItem is EntryItem s ? s.Key : ((EntryKind, int)?)null;
        _rows.Clear();
        _pendingReveal = selected;
        RequestRebuild();
    }

    // ---------------------------------------------------------------------------------------------
    // Expansion
    // ---------------------------------------------------------------------------------------------

    private void Toggle(EntryItem item)
    {
        if (item.Kind != EntryKind.Directory || !item.CanExpand)
        {
            return;
        }

        if (!_expanded.Remove(item.Index))
        {
            _expanded.Add(item.Index);
        }

        item.IsExpanded = _expanded.Contains(item.Index);
        RequestRebuild();
    }

    private void Reveal(EntryKind kind, int index)
    {
        var tree = State.Tree;
        if (tree is null)
        {
            return;
        }

        int dir = kind == EntryKind.File ? tree.File(index).Directory : index;
        if (kind is EntryKind.LooseFiles or EntryKind.File)
        {
            _expanded.Add(dir);
        }

        for (int p = tree.Dir(dir).Parent; p >= 0; p = tree.Dir(p).Parent)
        {
            _expanded.Add(p);
        }

        _pendingReveal = (kind, index);
        RequestRebuild();
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EntryItem item)
        {
            Toggle(item);
        }
    }

    private bool OnInvoke(EntryItem item)
    {
        if (item.Kind == EntryKind.Directory && item.CanExpand)
        {
            Toggle(item);
            return true;
        }

        return item.Kind is EntryKind.More or EntryKind.LooseFiles && OpenFolder(item);
    }

    private static bool OpenFolder(EntryItem item)
    {
        ItemActions.OpenFolder(item.Path);
        return true;
    }

    private void OnTreeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TreeList.SelectedItem is not EntryItem item)
        {
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Right && item.Kind == EntryKind.Directory && item.CanExpand && !_expanded.Contains(item.Index))
        {
            Toggle(item);
            e.Handled = true;
        }
        else if (e.Key == global::Windows.System.VirtualKey.Left)
        {
            if (item.Kind == EntryKind.Directory && _expanded.Contains(item.Index) && item.Index != ScanTree.RootIndex)
            {
                Toggle(item);
            }
            else if (item.ParentItem is { } parent)
            {
                TreeList.SelectedItem = parent;
                TreeList.ScrollIntoView(parent);
            }

            e.Handled = true;
        }
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        _expanded.Clear();
        _expanded.Add(ScanTree.RootIndex);
        RequestRebuild();
    }

    private async void OnScanFolder(object sender, RoutedEventArgs e) => await MainWindow.PickAndScanFolderAsync();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Details.Show(TreeList.SelectedItems.Count == 1 ? TreeList.SelectedItem as EntryItem : null);

    // ---------------------------------------------------------------------------------------------
    // Row computation and reconciliation
    // ---------------------------------------------------------------------------------------------

    private readonly record struct RowSpec(EntryKind Kind, int Index, int Depth, int ParentDir, int MoreCount = 0, long MoreSize = 0);

    private async void RequestRebuild()
    {
        if (_rebuilding)
        {
            _rebuildPending = true;
            return;
        }

        var tree = State.Tree;
        if (tree is null || tree != _tree)
        {
            return;
        }

        _rebuilding = true;
        try
        {
            do
            {
                _rebuildPending = false;
                int version = _version;
                var expanded = _expanded.ToHashSet();
                var specs = await Task.Run(() => BuildSpecs(tree, expanded));
                if (version != _version || tree != State.Tree)
                {
                    return;
                }

                Reconcile(tree, specs);
            }
            while (_rebuildPending);
        }
        finally
        {
            _rebuilding = false;
        }

        ApplyPendingReveal();
    }

    private static List<RowSpec> BuildSpecs(ScanTree tree, HashSet<int> expanded)
    {
        var specs = new List<RowSpec> { new(EntryKind.Directory, ScanTree.RootIndex, 0, -1) };
        if (expanded.Contains(ScanTree.RootIndex))
        {
            AddChildren(tree, ScanTree.RootIndex, 1, expanded, specs);
        }

        return specs;
    }

    private static void AddChildren(ScanTree tree, int dir, int depth, HashSet<int> expanded, List<RowSpec> specs)
    {
        var entries = new List<(EntryKind Kind, int Index, long Size)>();
        foreach (int child in Breakdown.SortedChildren(tree, dir))
        {
            entries.Add((EntryKind.Directory, child, tree.Dir(child).TotalSize));
        }

        foreach (int file in Breakdown.SortedFiles(tree, dir))
        {
            entries.Add((EntryKind.File, file, tree.File(file).Size));
        }

        long loose = tree.GetUnindexedOwnSize(dir, out int looseCount);
        if (looseCount > 0)
        {
            entries.Add((EntryKind.LooseFiles, dir, loose));
        }

        entries.Sort((a, b) => b.Size.CompareTo(a.Size));
        int shown = Math.Min(entries.Count, MaxChildrenPerFolder);
        for (int i = 0; i < shown; i++)
        {
            var (kind, index, _) = entries[i];
            specs.Add(new RowSpec(kind, index, depth, dir));
            if (kind == EntryKind.Directory && expanded.Contains(index))
            {
                AddChildren(tree, index, depth + 1, expanded, specs);
            }
        }

        if (entries.Count > shown)
        {
            long rest = 0;
            for (int i = shown; i < entries.Count; i++)
            {
                rest += entries[i].Size;
            }

            specs.Add(new RowSpec(EntryKind.More, dir, depth, dir, entries.Count - shown, rest));
        }
    }

    private void Reconcile(ScanTree tree, List<RowSpec> specs)
    {
        var existing = new Dictionary<(EntryKind, int), EntryItem>(_rows.Count);
        foreach (var row in _rows)
        {
            existing.TryAdd(row.Key, row);
        }

        var placed = new Dictionary<int, EntryItem>();
        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var key = (spec.Kind, spec.Index);
            placed.TryGetValue(spec.ParentDir, out var parent);
            long parentSize = parent?.Size ?? 0;

            EntryItem item;
            if (i < _rows.Count && _rows[i].Key == key)
            {
                item = _rows[i];
            }
            else if (existing.TryGetValue(key, out var found) && found.ParentItem == parent)
            {
                item = found;
                int old = _rows.IndexOf(found);
                _rows.Move(old, i);
            }
            else
            {
                item = spec.Kind switch
                {
                    EntryKind.Directory => EntryItem.ForDirectory(tree, spec.Index, parentSize, spec.Depth, parent),
                    EntryKind.File => EntryItem.ForFile(tree, spec.Index, parentSize, spec.Depth, parent),
                    EntryKind.LooseFiles => EntryItem.ForLooseFiles(tree, spec.Index, parentSize, spec.Depth, parent),
                    _ => EntryItem.ForMore(spec.Index, spec.MoreCount, spec.MoreSize, parentSize, spec.Depth, parent),
                };
                _rows.Insert(i, item);
            }

            if (spec.Kind == EntryKind.More)
            {
                item.UpdateMore(spec.MoreSize, parentSize);
            }
            else
            {
                item.Refresh(tree, spec.Kind == EntryKind.Directory && spec.Index == ScanTree.RootIndex ? tree.Root.TotalSize : parentSize);
            }

            if (spec.Kind == EntryKind.Directory)
            {
                item.IsExpanded = _expanded.Contains(spec.Index);
                placed[spec.Index] = item;
            }
        }

        while (_rows.Count > specs.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Details.RefreshSize();
    }

    private void ApplyPendingReveal()
    {
        if (_pendingReveal is not { } key)
        {
            return;
        }

        _pendingReveal = null;
        var row = _rows.FirstOrDefault(r => r.Key == key);
        if (row is not null)
        {
            TreeList.SelectedItem = row;
            TreeList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        }
    }
}
