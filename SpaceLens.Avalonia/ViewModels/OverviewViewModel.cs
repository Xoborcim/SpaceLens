using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;

namespace SpaceLens.Desktop.ViewModels;

/// <summary>A location category (Downloads, App data...) with its share of the scan.</summary>
public sealed record CategoryRow(string Name, string SizeText, double BarWidth, IBrush Swatch, LocationCategory Category);

/// <summary>Where space can be recovered: a detector finding with its explanation.</summary>
public sealed record OpportunityRow(string Title, string Group, string SizeText, string Explanation, string? Advice, string Path, int DirectoryIndex);

/// <summary>A cell of the map.</summary>
public sealed record MapCell(string Label, long Size, uint Color, ItemViewModel Item);

public sealed partial class OverviewViewModel : PageViewModel
{
    private int _version;

    public OverviewViewModel(MainViewModel main)
        : base(main, "Overview", "◔")
    {
    }

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    public ObservableCollection<ItemViewModel> Largest { get; } = [];

    public ObservableCollection<OpportunityRow> Opportunities { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<MapCell> Map { get; set; } = [];

    [ObservableProperty]
    public partial string Heading { get; set; } = "Choose a disk to scan";

    [ObservableProperty]
    public partial string UsageText { get; set; } = "";

    [ObservableProperty]
    public partial double UsedFraction { get; set; }

    [ObservableProperty]
    public partial bool HasTree { get; set; }

    protected override async void OnRefresh()
    {
        int version = ++_version;
        var main = Main;
        if (main.SelectedVolume is { } volume)
        {
            Heading = volume.Name;
            UsageText = $"{main.FormatSize(volume.UsedBytes)} used of {main.FormatSize(volume.TotalBytes)}  ·  {main.FormatSize(volume.FreeBytes)} free";
            UsedFraction = volume.UsedFraction;
        }

        var tree = main.Tree;
        HasTree = tree is not null;
        if (tree is null)
        {
            Categories.Clear();
            Largest.Clear();
            Opportunities.Clear();
            Map = [];
            return;
        }

        if (tree.RootPath != main.SelectedVolume?.RootPath)
        {
            Heading = tree.RootPath;
            UsageText = $"{main.FormatSize(tree.Root.TotalSize)} in {ItemViewModel.FileCount(tree.Root.TotalFiles)}";
            UsedFraction = 0;
        }

        // Space by location.
        long categoryMax = main.CategoryTotals.Count > 0 ? main.CategoryTotals.Max(c => c.Size) : 0;
        Categories.Clear();
        foreach (var total in main.CategoryTotals.Where(c => c.Size > 0).OrderByDescending(c => c.Size))
        {
            Categories.Add(new CategoryRow(main.LocationLabel(total.Category), main.FormatSize(total.Size),
                categoryMax > 0 ? 160.0 * total.Size / categoryMax : 0, Brush(StorageNatureInfo.Color(total.Category)), total.Category));
        }

        // Largest items, as a disjoint breakdown (the same items fill the map).
        var units = main.Findings.Where(f => f.IsUnit && f.DirectoryIndex > 0).Select(f => f.DirectoryIndex).ToHashSet();
        var items = await Task.Run(() => Breakdown.Build(tree, ScanTree.RootIndex, 40, units.Count > 0 ? units.Contains : null));
        if (version != _version || main.Tree != tree)
        {
            return;
        }

        long rootSize = Math.Max(1, tree.Root.TotalSize);
        var rows = items.Select(i => i.Kind switch
        {
            BreakdownItemKind.Directory => ItemViewModel.ForFolder(main, tree, i.Index),
            BreakdownItemKind.File => ItemViewModel.ForFile(main, tree, i.Index),
            _ => ItemViewModel.ForLooseFiles(main, tree, i.Index),
        }).ToList();
        long largest = rows.Count > 0 ? rows.Max(r => r.Size) : 1;
        Largest.Clear();
        foreach (var row in rows.Take(12))
        {
            row.Subtitle = $"{SizeFormatter.FormatPercent((double)row.Size / rootSize)}  ·  {PathUtil.GetParent(row.Path) ?? row.Path}";
            row.Fraction = (double)row.Size / largest;
            Largest.Add(row);
        }

        Map = rows.Select(r => new MapCell(r.Name, r.Size, ColorFor(tree, r), r)).ToList();

        Opportunities.Clear();
        foreach (var f in main.Findings.Where(f => f.IsOpportunity).OrderByDescending(f => f.Size).Take(8))
        {
            Opportunities.Add(new OpportunityRow(f.Title, f.Group, main.FormatSize(f.Size), f.Explanation ?? "", f.Advice, f.Path, f.DirectoryIndex));
        }
    }

    private uint ColorFor(ScanTree tree, ItemViewModel item) => item.Kind switch
    {
        ItemKind.Folder => StorageNatureInfo.Color(Main.CategoryOf(item.Index)),
        ItemKind.File => FileCategoryInfo.Color(tree.File(item.Index).Category),
        _ => 0xFF8A8A8A,
    };

    internal static IBrush Brush(uint argb) =>
        new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
