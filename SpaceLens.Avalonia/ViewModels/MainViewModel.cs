using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;
using SpaceLens.Core.Scanning;
using SpaceLens.Desktop.Platform;
using SpaceLens.Desktop.Services;

namespace SpaceLens.Desktop.ViewModels;

public enum ScanState
{
    Idle,
    Scanning,
    Paused,
    Completed,
    Cancelled,
    Snapshot,
    Failed,
}

/// <summary>
/// Application state: the current scan, its analysis, the pages and the actions on items. Everything here is
/// used from the UI thread; scanning and analysis run in the background and publish results back.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _analysisCts;
    private PauseGate? _pause;
    private long _scanUsedBytes;
    private int _tick;
    private Dictionary<int, LocationCategory> _locationMap = new();
    private Dictionary<int, StorageFinding> _findingByDirectory = new();
    private Dictionary<int, StorageFinding> _findingByFile = new();

    public MainViewModel(IDesktopPlatform platform, IDialogService dialogs)
    {
        Platform = platform;
        Dialogs = dialogs;
        Snapshots = new SnapshotStore(platform.DataDirectory);
        Overview = new OverviewViewModel(this);
        Folders = new FoldersViewModel(this);
        LargeFiles = new LargeFilesViewModel(this);
        Search = new SearchViewModel(this);
        Apps = new AppsViewModel(this);
        Pages = [Overview, Folders, LargeFiles, Search, Apps];
        CurrentPage = Overview;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => OnTick());
    }

    public IDesktopPlatform Platform { get; }

    public IDialogService Dialogs { get; }

    public SnapshotStore Snapshots { get; }

    public OverviewViewModel Overview { get; }

    public FoldersViewModel Folders { get; }

    public LargeFilesViewModel LargeFiles { get; }

    public SearchViewModel Search { get; }

    public AppsViewModel Apps { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public ObservableCollection<VolumeInfo> Volumes { get; } = [];

    public ScanTree? Tree { get; private set; }

    public IReadOnlyList<StorageFinding> Findings { get; private set; } = [];

    public IReadOnlyList<CategoryTotal> CategoryTotals { get; private set; } = [];

    /// <summary>Raised when the tree is replaced, changes (removals, folder rescans) or grows during a scan (about once a second).</summary>
    public event EventHandler? TreeChanged;

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    [ObservableProperty]
    public partial VolumeInfo? SelectedVolume { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning), nameof(IsPaused), nameof(CanModify), nameof(HasResults), nameof(PauseText), nameof(CanScan))]
    public partial ScanState State { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "Ready";

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = "Choose a disk or folder to scan.";

    [ObservableProperty]
    public partial string StatusCurrent { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool AnalysisRunning { get; set; }

    [ObservableProperty]
    public partial int ErrorCount { get; set; }

    /// <summary>Shown when protected folders could not be read (macOS Full Disk Access).</summary>
    [ObservableProperty]
    public partial bool ShowFullDiskAccessHint { get; set; }

    public bool IsScanning => State is ScanState.Scanning or ScanState.Paused;

    public bool IsPaused => State == ScanState.Paused;

    public bool CanScan => !IsScanning;

    /// <summary>Removal actions are disabled while a scan is writing the tree.</summary>
    public bool CanModify => !IsScanning && Tree is not null;

    public bool HasResults => Tree is not null;

    public string PauseText => IsPaused ? "Resume" : "Pause";

    public string FormatSize(long bytes) => SizeFormatter.Format(bytes, Platform.Units);

    public string LocationLabel(LocationCategory category) => Platform.Label(category);

    public StorageFinding? FindingFor(bool isFile, int index) =>
        (isFile ? _findingByFile : _findingByDirectory).GetValueOrDefault(index);

    public LocationCategory CategoryOf(int dirIndex) =>
        Tree is null ? LocationCategory.Other : LocationClassifier.NearestCategory(Tree, _locationMap, dirIndex);

    // ---------------------------------------------------------------------------------------------
    // Startup
    // ---------------------------------------------------------------------------------------------

    /// <summary>Lists the volumes, then scans the folder given on the command line or shows the last saved scan.</summary>
    public async Task InitializeAsync(string? startupFolder)
    {
        RefreshVolumes();
        if (startupFolder is not null && Directory.Exists(startupFolder))
        {
            StartScan(startupFolder);
            CurrentPage = Folders;
            return;
        }

        if (SelectedVolume is { } volume)
        {
            var tree = await Task.Run(() => Snapshots.TryLoad(volume.RootPath));
            if (tree is not null && Tree is null && !IsScanning)
            {
                ReplaceTree(tree, ScanState.Snapshot);
                await AnalyzeAsync(tree);
            }
        }

        ShowFullDiskAccessHint = Platform.HasFullDiskAccess() == false;
    }

    public void RefreshVolumes()
    {
        string? selected = SelectedVolume?.RootPath;
        Volumes.Clear();
        foreach (var volume in Platform.GetVolumes())
        {
            Volumes.Add(volume);
        }

        SelectedVolume = Volumes.FirstOrDefault(v => v.RootPath == selected) ?? Volumes.FirstOrDefault(v => v.IsStartup) ?? Volumes.FirstOrDefault();
    }

    // ---------------------------------------------------------------------------------------------
    // Scanning
    // ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ScanVolume()
    {
        if (SelectedVolume is { } volume)
        {
            StartScan(volume.RootPath);
        }
    }

    [RelayCommand]
    private async Task ScanFolder()
    {
        if (!IsScanning && await Dialogs.PickFolderAsync() is { } folder)
        {
            StartScan(folder);
            CurrentPage = Folders;
        }
    }

    [RelayCommand]
    private void Rescan()
    {
        if ((Tree?.RootPath ?? SelectedVolume?.RootPath) is { } root)
        {
            StartScan(root);
        }
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (_pause is null || !IsScanning)
        {
            return;
        }

        if (_pause.IsPaused)
        {
            _pause.Resume();
            _clock.Start();
            State = ScanState.Scanning;
        }
        else
        {
            _pause.Pause();
            _clock.Stop();
            State = ScanState.Paused;
        }

        UpdateProgress();
    }

    [RelayCommand]
    private void CancelScan()
    {
        _pause?.Resume();
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private void OpenFullDiskAccessSettings()
    {
        if (Platform.FullDiskAccessSettingsUrl is { } url)
        {
            Platform.OpenUrl(url);
        }
    }

    [RelayCommand]
    private void ShowPage(PageViewModel page) => CurrentPage = page;

    public void StartScan(string root)
    {
        if (IsScanning)
        {
            return;
        }

        root = PathUtil.NormalizeDisplayPath(root);
        if (!Directory.Exists(root))
        {
            StatusTitle = "Folder not found";
            StatusDetail = root;
            return;
        }

        RefreshVolumes();
        var volume = Volumes.FirstOrDefault(v => v.RootPath == root);
        _scanUsedBytes = volume?.UsedBytes ?? 0;
        ProgressIndeterminate = _scanUsedBytes == 0;
        Progress = 0;

        var tree = new ScanTree(root);
        tree.Metadata.VolumeTotalBytes = volume?.TotalBytes ?? 0;
        tree.Metadata.VolumeFreeBytes = volume?.FreeBytes ?? 0;
        _scanCts = new CancellationTokenSource();
        _pause = new PauseGate();
        _tick = 0;
        _clock.Restart();
        ReplaceTree(tree, ScanState.Scanning);
        StatusTitle = $"Scanning {root}";
        UpdateProgress();
        _timer.Start();

        var options = new ScanOptions { ExcludedPaths = Platform.ExcludedPathsFor(root) };
        var task = new ParallelDirectoryScanner(Platform.CreateEnumeratorFactory()).ScanAsync(tree, options, _pause, _scanCts.Token);
        _ = CompleteScanAsync(task, tree);
    }

    /// <summary>Waits for the scan; exposed so tests can wait for a scan they started.</summary>
    public Task? CurrentScan { get; private set; }

    private Task CompleteScanAsync(Task<ScanResult> task, ScanTree tree) => CurrentScan = CompleteCoreAsync(task, tree);

    private async Task CompleteCoreAsync(Task<ScanResult> task, ScanTree tree)
    {
        ScanResult result;
        try
        {
            result = await task;
        }
        catch (Exception ex)
        {
            _timer.Stop();
            State = ScanState.Failed;
            StatusTitle = "Scan failed";
            StatusDetail = ex.Message;
            return;
        }

        _timer.Stop();
        _clock.Stop();
        if (Tree != tree)
        {
            return;
        }

        ErrorCount = tree.ErrorCount;
        State = result.Cancelled ? ScanState.Cancelled : ScanState.Completed;
        UpdateStatusForFinishedTree();
        RaiseTreeChanged();
        await AnalyzeAsync(tree);
        if (!result.Cancelled)
        {
            await Task.Run(() => Snapshots.Save(tree));
        }

        // Protected folders that could not be read point to Full Disk Access.
        ShowFullDiskAccessHint = Platform.HasFullDiskAccess() == false ||
            tree.ErrorCount > 0 && tree.Errors.Any(e => e.ErrorCode == 5 && PathUtil.IsSameOrUnder(e.Path, PathUtil.Combine(Platform.Home, "Library")));
    }

    private void ReplaceTree(ScanTree tree, ScanState state)
    {
        _analysisCts?.Cancel();
        Tree = tree;
        Findings = [];
        CategoryTotals = [];
        _locationMap = new();
        _findingByDirectory = new();
        _findingByFile = new();
        ErrorCount = tree.ErrorCount;
        State = state;
        if (state == ScanState.Snapshot)
        {
            UpdateStatusForFinishedTree();
        }

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanModify));
        RaiseTreeChanged();
    }

    private void OnTick()
    {
        _tick++;
        UpdateProgress();
        if (_tick % 5 == 0 && Tree is { } tree)
        {
            // Quick location categories while scanning; detectors run when the scan is done.
            _locationMap = Platform.BuildLocationMap(tree);
            CategoryTotals = LocationClassifier.Summarize(tree, _locationMap);
            RaiseTreeChanged();
        }
    }

    private void UpdateProgress()
    {
        if (Tree is not { } tree)
        {
            return;
        }

        long bytes = tree.BytesScanned;
        StatusTitle = IsPaused ? $"Paused – {tree.RootPath}" : $"Scanning {tree.RootPath}";
        StatusDetail = $"{SizeFormatter.FormatCount(tree.FilesScanned)} files  ·  {SizeFormatter.FormatCount(tree.DirectoriesScanned)} folders  ·  {FormatSize(bytes)}  ·  {_clock.Elapsed:m\\:ss}";
        StatusCurrent = IsPaused ? "" : tree.CurrentPath ?? "";
        if (_scanUsedBytes > 0)
        {
            Progress = Math.Min(99, bytes * 100.0 / _scanUsedBytes);
        }

        ErrorCount = tree.ErrorCount;
    }

    private void UpdateStatusForFinishedTree()
    {
        var tree = Tree!;
        var m = tree.Metadata;
        Progress = 100;
        ProgressIndeterminate = false;
        StatusCurrent = "";
        StatusTitle = State switch
        {
            ScanState.Cancelled => $"Scan cancelled – partial results for {tree.RootPath}",
            ScanState.Snapshot => $"Last scanned {(m.CompletedUtc ?? m.StartedUtc).ToLocalTime():g} – {tree.RootPath}",
            _ => $"Scan complete – {tree.RootPath}",
        };
        StatusDetail = $"{SizeFormatter.FormatCount(tree.Root.TotalFiles)} files  ·  {FormatSize(tree.Root.TotalSize)}" +
            (m.Duration > TimeSpan.Zero ? $"  ·  {m.Duration.TotalSeconds:0.0} s" : "") +
            (tree.ErrorCount > 0 ? $"  ·  {SizeFormatter.FormatCount(tree.ErrorCount)} folders could not be read" : "");
    }

    private void RaiseTreeChanged() => TreeChanged?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------------------------------------
    // Analysis
    // ---------------------------------------------------------------------------------------------

    public async Task AnalyzeAsync(ScanTree tree)
    {
        _analysisCts?.Cancel();
        var cts = _analysisCts = new CancellationTokenSource();
        AnalysisRunning = true;
        try
        {
            var (findings, map, totals) = await Task.Run(() =>
            {
                var found = Platform.Detect(tree, cts.Token);
                var locations = Platform.BuildLocationMap(tree);
                LocationClassifier.ApplyFindings(locations, found.Where(f => f.FileIndex < 0));
                return (found, locations, LocationClassifier.Summarize(tree, locations, found.Where(f => f.FileIndex >= 0)));
            }, cts.Token);

            if (Tree != tree || cts.IsCancellationRequested)
            {
                return;
            }

            Findings = findings;
            _locationMap = map;
            CategoryTotals = totals;
            _findingByDirectory = findings.Where(f => f.DirectoryIndex > 0).GroupBy(f => f.DirectoryIndex).ToDictionary(g => g.Key, g => g.First());
            _findingByFile = findings.Where(f => f.FileIndex >= 0).GroupBy(f => f.FileIndex).ToDictionary(g => g.Key, g => g.First());
            RaiseTreeChanged();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_analysisCts == cts)
            {
                AnalysisRunning = false;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------------------------------

    public void ShowInFolders(int dirIndex)
    {
        CurrentPage = Folders;
        Folders.Navigate(dirIndex);
    }

    private bool _removalInProgress;

    /// <summary>Moves items to the Trash after a confirmation that lists them with every warning.</summary>
    public async Task<bool> MoveToTrashAsync(IReadOnlyList<ItemViewModel> items)
    {
        var tree = Tree;
        if (_removalInProgress || tree is null || !CanModify)
        {
            return false;
        }

        items = items.Where(i => i.Tree == tree && i.Kind is ItemKind.Folder or ItemKind.File).ToList();
        var folders = items.Where(i => i.IsFolder).Select(i => i.Path).ToList();
        items = items.Where(i => !folders.Any(f => PathUtil.IsStrictlyUnder(i.Path, f))).ToList();
        if (items.Count == 0)
        {
            return false;
        }

        var blocked = items.FirstOrDefault(i => !i.CanTrash);
        if (blocked is not null)
        {
            var reason = blocked.Finding is { AllowDirectRemoval: false } f
                ? $"{f.Explanation}\n\n{f.Advice}".Trim()
                : $"{blocked.Safety.Explanation}\n\n{blocked.Safety.Advice}".Trim();
            await Dialogs.ShowMessageAsync(blocked.Safety.Level == ProtectionLevel.Protected ? blocked.Safety.Label : $"{blocked.Name} is managed elsewhere", reason);
            return false;
        }

        long total = items.Sum(i => i.Size);
        var warnings = items.Where(i => i.Safety.Level == ProtectionLevel.Caution).Take(3)
            .Select(i => new DialogWarning(i.Safety.Label, $"{i.Safety.Explanation} {i.Safety.Advice}".Trim())).ToList();
        string list = string.Join("\n", items.Take(5).Select(i => i.Path)) + (items.Count > 5 ? $"\n… and {items.Count - 5} more" : "");
        var request = new ConfirmRequest(
            items.Count == 1 ? $"Move “{items[0].Name}” to the {Platform.TrashName}?" : $"Move {items.Count} items to the {Platform.TrashName}?",
            $"{FormatSize(total)}\n\n{list}",
            $"Move to {Platform.TrashName}",
            warnings,
            $"You can put items back from the {Platform.TrashName} until it is emptied.");

        _removalInProgress = true;
        try
        {
            if (!await Dialogs.ConfirmAsync(request))
            {
                return false;
            }

            var results = await Task.Run(() => Platform.MoveToTrash(items.Select(i => (i.Path, i.IsFolder)).ToList()));
            RemoveFromTree(tree, items.Where(i => i.IsFolder ? !Directory.Exists(i.Path) : !File.Exists(i.Path)).ToList());
            var failed = results.Where(r => !r.Success).ToList();
            if (failed.Count > 0)
            {
                await Dialogs.ShowMessageAsync("Some items were not moved", string.Join("\n", failed.Take(5).Select(r => r.Error ?? r.Path)));
            }

            return failed.Count == 0;
        }
        finally
        {
            _removalInProgress = false;
        }
    }

    /// <summary>Removes items that no longer exist from the results; ignored if the tree was replaced or is being scanned.</summary>
    public void RemoveFromTree(ScanTree tree, IReadOnlyList<ItemViewModel> items)
    {
        if (tree != Tree || IsScanning)
        {
            return;
        }

        int removed = 0;
        foreach (var item in items)
        {
            if (item.IsFile && tree.IsLiveFile(item.Index))
            {
                tree.RemoveFile(item.Index);
                removed++;
            }
            else if (item.IsFolder && item.Index != ScanTree.RootIndex && tree.IsLiveDirectory(item.Index))
            {
                tree.RemoveDirectory(item.Index);
                removed++;
            }
        }

        if (removed > 0)
        {
            AfterTreeChanged(tree);
        }
    }

    /// <summary>Scans one folder again and replaces it in the results.</summary>
    public async Task RescanFolderAsync(ItemViewModel item)
    {
        var tree = item.Tree;
        if (tree != Tree || IsScanning || !item.IsFolder || item.Index == ScanTree.RootIndex || !tree.IsLiveDirectory(item.Index))
        {
            return;
        }

        StatusDetail = $"Rescanning {item.Path}…";
        var fresh = new ScanTree(item.Path, tree.FileIndexThreshold);
        await new ParallelDirectoryScanner(Platform.CreateEnumeratorFactory())
            .ScanAsync(fresh, new ScanOptions { ExcludedPaths = Platform.ExcludedPathsFor(tree.RootPath) }, null, CancellationToken.None);
        if (tree != Tree || IsScanning || !tree.IsLiveDirectory(item.Index))
        {
            return;
        }

        int replaced = tree.ReplaceDirectory(item.Index, fresh);
        AfterTreeChanged(tree);
        StatusDetail += $"  ·  Rescanned {item.Path}: {FormatSize(item.Size)} → {FormatSize(tree.Dir(replaced).TotalSize)}";
    }

    private void AfterTreeChanged(ScanTree tree)
    {
        RefreshVolumes();
        UpdateStatusForFinishedTree();
        RaiseTreeChanged();
        _ = AnalyzeAsync(tree);
        _ = Task.Run(() => Snapshots.Save(tree));
    }
}
