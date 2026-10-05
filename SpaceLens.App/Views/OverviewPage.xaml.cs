using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Visualization;
using SpaceLens.Windows.FileSystem;

namespace SpaceLens.App.Views;

public sealed partial class OverviewPage : Page
{
    private const int LargestItemCount = 10;
    private const int TreemapItemCount = 48;
    private int _refreshVersion;
    private int _colorVersion;
    private ScanTree? _mapTree;
    private List<EntryItem> _mapEntries = [];

    public OverviewPage()
    {
        InitializeComponent();
        State.TreeReplaced += (_, _) => Refresh();
        State.LiveRefresh += (_, _) => Refresh();
        State.ScanFinished += (_, _) => Refresh();
        State.AnalysisChanged += (_, _) => Refresh();
        State.TreeMutated += (_, _) => Refresh();
        State.AppsChanged += (_, _) => RefreshOpportunities();
        State.PropertyChanged += OnStateChanged;
        ItemActions.AttachListBehaviors(LargestList, RevealInFolders);
    }

    public AppState State => AppState.Current;

    public bool CanStartScan(bool isScanning) => !isScanning;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.State) or nameof(AppState.AnalysisRunning))
        {
            UpdateHeader();
        }
        else if (e.PropertyName == nameof(AppState.SelectedDrive))
        {
            UpdateDriveCard();
        }
    }

    private void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        var drive = State.SelectedDrive;
        if (drive is null)
        {
            return;
        }

        // When the user switches drives, show that drive's saved results, if any. Selection changes
        // made by the app itself (drive list refresh, a scan starting) leave the picker unfocused.
        bool userChoice = DrivePicker.FocusState != FocusState.Unfocused;
        if (userChoice && !State.IsScanning && State.Tree?.RootPath != drive.RootPath && State.Settings.RememberScans && SnapshotStore.Exists(drive.RootPath))
        {
            _ = State.LoadSnapshotAsync(drive.RootPath);
        }

        Refresh();
    }

    private bool TreeMatchesDrive(ScanTree? tree, DriveDescriptor? drive) =>
        tree is not null && (drive is null || PathUtil.IsSameOrUnder(tree.RootPath, drive.RootPath));

    private void UpdateHeader()
    {
        var drive = State.SelectedDrive;
        var tree = State.Tree;
        ScanDriveText.Text = drive is null ? "Scan drive" : TreeMatchesDrive(tree, drive) && tree!.RootPath == drive.RootPath ? $"Rescan {drive.Letter}" : $"Scan {drive.Letter}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ScanDriveButton, ScanDriveText.Text);

        SnapshotBar.IsOpen = State.State == ScanState.Snapshot && tree is not null;
        if (SnapshotBar.IsOpen)
        {
            var m = tree!.Metadata;
            SnapshotBar.Title = $"Last scanned {AppState.FormatWhen(m.CompletedUtc ?? m.StartedUtc)}";
            SnapshotBar.Message = "These are saved results. Files may have changed since then.";
        }

        AnalysisText.Visibility = State.AnalysisRunning || State.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        AnalysisText.Text = State.IsScanning ? "Available when the scan finishes." : "Analyzing…";
    }

    private void Refresh()
    {
        UpdateDriveCard();
        UpdateHeader();

        var tree = State.Tree;
        bool show = TreeMatchesDrive(tree, State.SelectedDrive);
        EmptyCard.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ResultsGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = State.SelectedDrive is { } d ? $"Scan {d.DisplayName} to see what is using space" : "Choose a drive to scan";
        if (!show || tree is null)
        {
            return;
        }

        RefreshCategories();
        RefreshOpportunities();
        _ = RefreshBreakdownAsync(tree);
    }

    private void UpdateDriveCard()
    {
        var drive = State.SelectedDrive;
        if (drive is null)
        {
            DriveName.Text = "No drive selected";
            return;
        }

        DriveName.Text = drive.DisplayName;
        DriveSubtitle.Text = $"{drive.MediaLabel}  ·  {drive.FileSystem}" + (drive.IsSystemDrive ? "  ·  Windows drive" : "");
        UsedText.Text = SizeFormatter.Format(drive.UsedBytes);
        FreeText.Text = SizeFormatter.Format(drive.FreeBytes);
        TotalText.Text = SizeFormatter.Format(drive.TotalBytes);
        UsedBar.Value = drive.UsedFraction;
    }

    private void RefreshCategories()
    {
        var totals = State.CategoryTotals.Where(c => c.Size > 0).OrderByDescending(c => c.Size).Take(9).ToList();
        long max = totals.Count > 0 ? totals[0].Size : 0;
        long total = State.Tree?.Root.TotalSize ?? 0;
        CategoryList.ItemsSource = totals.Select(c => new SummaryItem
        {
            Name = StorageNatureInfo.Label(c.Category),
            Size = c.Size,
            CountText = total > 0 ? SizeFormatter.FormatPercent((double)c.Size / total) : "",
            BarWidth = SummaryItem.Bar(c.Size, max),
            Swatch = SummaryItem.BrushFromArgb(StorageNatureInfo.Color(c.Category)),
            Tag = c.Category,
        }).ToList();
    }

    private void RefreshOpportunities()
    {
        if (State.IsScanning || State.Tree is null)
        {
            OpportunityList.ItemsSource = null;
            return;
        }

        var groups = State.Findings
            .Where(f => f.IsOpportunity && f.Size > 0)
            .GroupBy(f => f.Group)
            .Select(g => (Group: g.Key, Size: g.Sum(f => f.Size), Count: g.Count(), First: g.OrderByDescending(f => f.Size).First()))
            .OrderByDescending(g => g.Size)
            .Take(8)
            .ToList();

        var rows = groups.Select(g => new SummaryItem
        {
            Name = g.Group,
            Size = g.Size,
            Subtitle = OpportunityHint(g.First),
            CountText = g.Count == 1 ? StorageNatureInfo.Label(g.First.Nature) : $"{g.Count} locations",
            Glyph = EntryItem.GlyphFor(g.First.Category),
            Tag = g.First,
        }).ToList();

        // Only apps installed in the scanned location, and not ones that contain a detected library
        // (Steam's folder holds its games, which already appear as their own row).
        var tree = State.Tree!;
        var largeApps = State.Apps
            .Where(a => a.IsSizeMeasured && !a.IsGame && a.InstallLocation is not null)
            .Select(a => (App: a, Dir: tree.FindDirectory(a.InstallLocation!)))
            .Where(x => x.Dir > 0 && !State.UnitDirectories.Any(u => tree.IsWithin(u, x.Dir)))
            .Select(x => x.App)
            .OrderByDescending(a => a.BestSize)
            .Take(3)
            .ToList();
        if (largeApps.Count > 0)
        {
            rows.Add(new SummaryItem
            {
                Name = "Largest installed apps",
                Size = largeApps.Sum(a => a.BestSize),
                Subtitle = string.Join(", ", largeApps.Select(a => a.Name)) + " – remove with Uninstall",
                Glyph = "\uE71D",
                Tag = "apps",
            });
        }

        long max = rows.Count > 0 ? rows.Max(r => r.Size) : 0;
        OpportunityList.ItemsSource = rows.OrderByDescending(r => r.Size).Select(r => new SummaryItem
        {
            Name = r.Name,
            Size = r.Size,
            Subtitle = r.Subtitle,
            CountText = r.CountText,
            Glyph = r.Glyph,
            Tag = r.Tag,
            BarWidth = SummaryItem.Bar(r.Size, max),
        }).ToList();
        OpportunitiesCard.Visibility = rows.Count > 0 || State.AnalysisRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string OpportunityHint(StorageFinding f) => f.Nature switch
    {
        _ when f.Group == SpaceLens.Detectors.WindowsStorage.WindowsStorageDetector.RecycleBinGroup => "Deleted items; emptying the Recycle Bin frees this space",
        StorageNature.Game => "Uninstall games you no longer play from their launcher",
        StorageNature.Temporary => "Temporary data; Windows Storage Sense can clean it",
        StorageNature.Cache => "Rebuilt automatically when needed",
        StorageNature.DeveloperArtifact => "Can be restored by rebuilding or reinstalling packages",
        StorageNature.UserFile => "Your files – review before removing",
        StorageNature.VirtualDisk => "Manage with the tool that created it",
        StorageNature.SystemFile => "Reduce with Windows settings, not by deleting",
        _ => f.Advice ?? f.Explanation ?? "",
    };

    private async Task RefreshBreakdownAsync(ScanTree tree)
    {
        int version = ++_refreshVersion;
        var units = State.UnitDirectories;
        var items = await Task.Run(() => Breakdown.Build(tree, ScanTree.RootIndex, TreemapItemCount, units.Count > 0 ? units.Contains : null));
        if (version != _refreshVersion || State.Tree != tree)
        {
            return;
        }

        long rootSize = tree.Root.TotalSize;
        var entries = items.Select(i => i.Kind switch
        {
            BreakdownItemKind.Directory => EntryItem.ForDirectory(tree, i.Index, rootSize),
            BreakdownItemKind.File => EntryItem.ForFile(tree, i.Index, rootSize),
            _ => EntryItem.ForLooseFiles(tree, i.Index, rootSize),
        }).ToList();

        long max = entries.Count > 0 ? entries.Max(e => e.Size) : 0;
        foreach (var entry in entries)
        {
            entry.Subtitle = entry.Kind == EntryKind.LooseFiles ? entry.ParentPath : $"{entry.PercentText}  ·  {entry.ParentPath}";
            entry.BarWidth = SummaryItem.Bar(entry.Size, max);
        }

        LargestList.ItemsSource = entries.Take(LargestItemCount).ToList();
        _mapTree = tree;
        _mapEntries = entries;
        await ColorMapAsync();
    }

    private string MapMode => (MapColoring.SelectedItem as ComboBoxItem)?.Tag as string ?? "location";

    private async void OnMapColoringChanged(object sender, SelectionChangedEventArgs e) => await ColorMapAsync();

    /// <summary>
    /// Colours the map cells. Location uses the analysis already in memory; file type and age walk each
    /// cell's subtree, so they run on a background thread (the cells are disjoint: at most one pass over the tree).
    /// </summary>
    private async Task ColorMapAsync()
    {
        var tree = _mapTree;
        var entries = _mapEntries;
        if (tree is null || MapColoring is null)
        {
            return;
        }

        int version = ++_colorVersion;
        string mode = MapMode;
        uint[] colors;
        if (mode == "location")
        {
            colors = entries.Select(e => LocationColor(tree, e)).ToArray();
        }
        else
        {
            var now = DateTime.UtcNow;
            colors = await Task.Run(() => entries.Select(e => mode == "age" ? AgeColor(tree, e, now) : TypeColor(tree, e)).ToArray());
        }

        if (version != _colorVersion || tree != _mapTree)
        {
            return;
        }

        Treemap.SetItems(entries.Select((e, i) => (e, colors[i])).ToList());
        UpdateLegend(mode);
    }

    private uint LocationColor(ScanTree tree, EntryItem e) => e.Kind switch
    {
        EntryKind.Directory => StorageNatureInfo.Color(State.CategoryOf(e.Index)),
        EntryKind.File => FileCategoryInfo.Color(tree.File(e.Index).Category),
        _ => TreemapColoring.UnknownColor,
    };

    private static uint TypeColor(ScanTree tree, EntryItem e) => e.Kind switch
    {
        EntryKind.Directory => TreemapColoring.DominantCategory(tree, e.Index) is { } category ? FileCategoryInfo.Color(category) : TreemapColoring.UnknownColor,
        EntryKind.File => FileCategoryInfo.Color(tree.File(e.Index).Category),
        _ => TreemapColoring.UnknownColor,
    };

    private static uint AgeColor(ScanTree tree, EntryItem e, DateTime nowUtc) => e.Kind switch
    {
        EntryKind.Directory => TreemapColoring.AgeColor(TreemapColoring.NewestWriteUtc(tree, e.Index), nowUtc),
        EntryKind.File => TreemapColoring.AgeColor(tree.File(e.Index).LastWriteUtc, nowUtc),
        _ => TreemapColoring.UnknownColor,
    };

    /// <summary>Age bands need a legend; location and file type colours match the swatches in their own lists.</summary>
    private void UpdateLegend(string mode)
    {
        MapLegend.Children.Clear();
        MapLegend.Visibility = mode == "age" ? Visibility.Visible : Visibility.Collapsed;
        if (mode != "age")
        {
            return;
        }

        MapLegend.Children.Add(new TextBlock { Text = "Last change:", Style = (Style)Application.Current.Resources["SecondaryTextStyle"], VerticalAlignment = VerticalAlignment.Center });
        foreach (var (label, _, color) in TreemapColoring.AgeBands)
        {
            var swatch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            swatch.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = SummaryItem.BrushFromArgb(color), VerticalAlignment = VerticalAlignment.Center });
            swatch.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["SecondaryTextStyle"], VerticalAlignment = VerticalAlignment.Center });
            MapLegend.Children.Add(swatch);
        }
    }

    private bool RevealInFolders(EntryItem item)
    {
        State.RequestNavigation("folders", item);
        return true;
    }

    private void OnLargestClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntryItem item)
        {
            RevealInFolders(item);
        }
    }

    private void OnTreemapCellInvoked(object? sender, EntryItem item) => RevealInFolders(item);

    private void OnCategoryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SummaryItem { Tag: LocationCategory category })
        {
            State.RequestNavigation(category == LocationCategory.Developer ? "developer" : "storage", category);
        }
    }

    private void OnOpportunityClick(object sender, ItemClickEventArgs e)
    {
        switch ((e.ClickedItem as SummaryItem)?.Tag)
        {
            case StorageFinding f:
                State.RequestNavigation(f.Category == LocationCategory.Developer ? "developer" : "storage", f.Group);
                break;
            case "apps":
                State.RequestNavigation("apps");
                break;
        }
    }

    private void OnScanDrive(object sender, RoutedEventArgs e)
    {
        if (State.SelectedDrive is { } drive)
        {
            State.StartScan(drive.RootPath);
        }
    }

    private async void OnScanFolder(object sender, RoutedEventArgs e) => await MainWindow.PickAndScanFolderAsync();

    private void OnRescan(object sender, RoutedEventArgs e) => State.Rescan();
}
