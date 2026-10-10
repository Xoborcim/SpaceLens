using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Models;

namespace SpaceLens.Desktop.ViewModels;

/// <summary>One step of the breadcrumb.</summary>
public sealed record Crumb(string Name, int Index);

/// <summary>Browse the scan folder by folder, largest first.</summary>
public sealed partial class FoldersViewModel : PageViewModel
{
    private const int MaxRows = 500;
    private ScanTree? _tree;

    public FoldersViewModel(MainViewModel main)
        : base(main, "Folders", "📁")
    {
    }

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    public ObservableCollection<Crumb> Breadcrumb { get; } = [];

    [ObservableProperty]
    public partial int CurrentIndex { get; set; } = ScanTree.RootIndex;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial ItemViewModel? SelectedItem { get; set; }

    public bool CanGoUp => _tree is not null && CurrentIndex != ScanTree.RootIndex;

    public void Navigate(int dirIndex)
    {
        CurrentIndex = dirIndex;
        Refresh();
    }

    [RelayCommand]
    private void Up()
    {
        if (_tree is { } tree && CurrentIndex != ScanTree.RootIndex)
        {
            Navigate(tree.Dir(CurrentIndex).Parent);
        }
    }

    [RelayCommand]
    private void GoTo(Crumb crumb) => Navigate(crumb.Index);

    /// <summary>Opens a folder row (double-click, Enter).</summary>
    public void Activate(ItemViewModel item)
    {
        if (item.IsFolder && !item.IsLink && item.Tree == _tree)
        {
            Navigate(item.Index);
        }
        else if (item.IsFile)
        {
            Main.Platform.Open(item.Path);
        }
    }

    protected override void OnRefresh()
    {
        var tree = Main.Tree;
        if (tree != _tree)
        {
            _tree = tree;
            CurrentIndex = ScanTree.RootIndex;
        }

        Items.Clear();
        Breadcrumb.Clear();
        if (tree is null)
        {
            Summary = "Scan a disk or folder to browse it here.";
            OnPropertyChanged(nameof(CanGoUp));
            return;
        }

        if (!tree.IsLiveDirectory(CurrentIndex))
        {
            CurrentIndex = ScanTree.RootIndex;
        }

        for (int d = CurrentIndex; d >= 0; d = tree.Dir(d).Parent)
        {
            Breadcrumb.Insert(0, new Crumb(d == ScanTree.RootIndex ? tree.RootPath : tree.Dir(d).Name, d));
        }

        ref var node = ref tree.Dir(CurrentIndex);
        long parentSize = Math.Max(1, node.TotalSize);
        var rows = new List<ItemViewModel>();
        rows.AddRange(Breakdown.SortedChildren(tree, CurrentIndex).Take(MaxRows).Select(c => ItemViewModel.ForFolder(Main, tree, c)));
        rows.AddRange(Breakdown.SortedFiles(tree, CurrentIndex).Take(MaxRows).Select(f => ItemViewModel.ForFile(Main, tree, f)));
        var loose = ItemViewModel.ForLooseFiles(Main, tree, CurrentIndex);
        if (loose.Size > 0)
        {
            rows.Add(loose);
        }

        foreach (var row in rows.OrderByDescending(r => r.Size))
        {
            row.Fraction = (double)row.Size / parentSize;
            row.BarBrush = OverviewViewModel.Brush(Core.Classification.StorageNatureInfo.Color(row.IsFolder ? Main.CategoryOf(row.Index) : Main.CategoryOf(CurrentIndex)));
            Items.Add(row);
        }

        Summary = $"{tree.GetPath(CurrentIndex)}  ·  {Main.FormatSize(node.TotalSize)}  ·  {ItemViewModel.FileCount(node.TotalFiles)}" +
            ((node.Flags & NodeFlags.AccessDenied) != 0 ? "  ·  could not be read completely" : "");
        OnPropertyChanged(nameof(CanGoUp));
    }
}
