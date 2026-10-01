using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.InstalledApps;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;
using SpaceLens.Core.Scanning;
using SpaceLens.Detectors;
using SpaceLens.Windows.FileSystem;
using SpaceLens.Windows.InstalledApps;
using SpaceLens.Windows.Shell;

namespace SpaceLens.App.Services;

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

public sealed record ScanDiagnostics(
    long Files,
    long Folders,
    long Bytes,
    TimeSpan Duration,
    double FilesPerSecond,
    double FoldersPerSecond,
    double BytesPerSecond,
    long PeakWorkingSet,
    long AllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    double UiRefreshRate,
    int Workers,
    string Engine,
    long IndexedFiles,
    long RecordBytes);

/// <summary>
/// Application-wide state: the current scan tree, scan lifecycle, derived analysis (findings,
/// categories) and installed apps. All public members are used from the UI thread; heavy work runs
/// on background threads and results are applied on the dispatcher.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private const int TimerIntervalMs = 200;     // progress counters: 5 Hz
    private const int LiveRefreshEveryTicks = 5;  // result lists: 1 Hz

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _analysisCts;
    private PauseGate? _pause;
    private readonly Stopwatch _scanClock = new();
    private long _scanUsedBytes;
    private int _tick;
    private long _allocatedAtStart;
    private int _gen0AtStart, _gen1AtStart, _gen2AtStart;
    private Dictionary<int, LocationCategory> _locationMap = new();
    private bool _storeAppsLoaded;
    private bool _reloadAppsWhenDone;

    public AppState(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Settings = SettingsService.Load();
        Known = KnownLocations.FromEnvironment();
        Safety = new SafetyPolicy(Known);
        Shell = new ShellActions(Safety);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(TimerIntervalMs);
        _timer.Tick += OnTimerTick;
        Current = this;
    }

    public static AppState Current { get; private set; } = null!;

    public AppSettings Settings { get; }

    public KnownLocations Known { get; }

    public SafetyPolicy Safety { get; }

    public ShellActions Shell { get; }

    public nint WindowHandle { get; set; }

    public XamlRoot? XamlRoot { get; set; }

    public ObservableCollection<DriveDescriptor> Drives { get; } = [];

    public ScanTree? Tree { get; private set; }

    public ScanResult? LastResult { get; private set; }

    public ScanDiagnostics? LastDiagnostics { get; private set; }

    public IReadOnlyList<StorageFinding> Findings { get; private set; } = [];

    public IReadOnlyList<CategoryTotal> CategoryTotals { get; private set; } = [];

    /// <summary>Directories reported as a whole (games, detected locations) in breakdowns.</summary>
    public HashSet<int> UnitDirectories { get; private set; } = [];

    public Dictionary<int, StorageFinding> FindingByDirectory { get; private set; } = new();

    public Dictionary<int, StorageFinding> FindingByFile { get; private set; } = new();

    public List<InstalledApp> Apps { get; private set; } = [];

    public double UiRefreshRate { get; private set; }

    /// <summary>Milliseconds from process start until the first frame was laid out.</summary>
    public double StartupWindowMs { get; set; }

    /// <summary>Milliseconds from process start until saved results were on screen (0 when none were loaded).</summary>
    public double StartupSnapshotMs { get; private set; }

    /// <summary>Time spent reading and decompressing the saved scan.</summary>
    public TimeSpan SnapshotLoadTime { get; private set; }

    public static double MillisecondsSinceProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return (DateTime.Now - process.StartTime).TotalMilliseconds;
    }

    [ObservableProperty]
    public partial DriveDescriptor? SelectedDrive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning), nameof(IsPaused), nameof(CanModify), nameof(HasResults), nameof(PauseLabel), nameof(PauseGlyph))]
    public partial ScanState State { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "Ready";

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = "";

    [ObservableProperty]
    public partial string StatusCurrent { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool AppsLoading { get; set; }

    [ObservableProperty]
    public partial bool AnalysisRunning { get; set; }

    [ObservableProperty]
    public partial int ErrorCount { get; set; }

    public bool IsScanning => State is ScanState.Scanning or ScanState.Paused;

    public bool IsPaused => State == ScanState.Paused;

    /// <summary>Deletion and uninstall actions are disabled while a scan is writing the tree.</summary>
    public bool CanModify => !IsScanning && Tree is not null;

    public bool HasResults => Tree is not null;

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    public string PauseGlyph => IsPaused ? "\uE768" : "\uE769";

    public bool AppsLoaded { get; private set; }

    /// <summary>A new tree was created (scan started) or loaded (snapshot).</summary>
    public event EventHandler? TreeReplaced;

    /// <summary>Raised about once per second during a scan.</summary>
    public event EventHandler? LiveRefresh;

    public event EventHandler? ScanFinished;

    public event EventHandler? AnalysisChanged;

    public event EventHandler? AppsChanged;

    /// <summary>Items were removed from the tree (Recycle Bin, delete, uninstall).</summary>
    public event EventHandler? TreeMutated;

    public event EventHandler<NavigationRequest>? NavigationRequested;

    public void RequestNavigation(string page, object? parameter = null) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(page, parameter));

    // ---------------------------------------------------------------------------------------------
    // Startup
    // ---------------------------------------------------------------------------------------------

    /// <summary>Deferred startup work, run after the window is visible.</summary>
    public async Task InitializeAsync()
    {
        await RefreshDrivesAsync();
        string? root = Settings.LastRoot ?? SelectedDrive?.RootPath;
        if (root is not null && Settings.RememberScans && Tree is null)
        {
            await LoadSnapshotAsync(root);
        }
    }

    public async Task RefreshDrivesAsync()
    {
        var drives = await Task.Run(DriveService.GetDrives);
        string? selected = SelectedDrive?.RootPath ?? (Settings.LastRoot is { Length: 3 } r ? r : null);
        Drives.Clear();
        foreach (var d in drives)
        {
            Drives.Add(d);
        }

        SelectedDrive = drives.FirstOrDefault(d => d.RootPath.Equals(selected, StringComparison.OrdinalIgnoreCase))
            ?? drives.FirstOrDefault(d => d.IsSystemDrive) ?? drives.FirstOrDefault();
    }

    public async Task LoadSnapshotAsync(string root)
    {
        var clock = Stopwatch.StartNew();
        var previous = Tree;
        var tree = await Task.Run(() => SnapshotStore.TryLoad(root));
        if (tree is null || IsScanning || Tree != previous)
        {
            return;
        }

        SnapshotLoadTime = clock.Elapsed;

        Tree = tree;
        LastResult = null;
        ErrorCount = tree.ErrorCount;
        State = ScanState.Snapshot;
        SelectDriveFor(tree.RootPath);
        UpdateStatusForFinishedTree();
        ResetAnalysis();
        TreeReplaced?.Invoke(this, EventArgs.Empty);
        if (StartupSnapshotMs == 0)
        {
            StartupSnapshotMs = MillisecondsSinceProcessStart();
        }

        await AnalyzeAsync(tree);
        if (Tree == tree && !IsScanning)
        {
            ReleaseScanGarbage();
        }

        _ = LoadAppsAsync();
    }

    // ---------------------------------------------------------------------------------------------
    // Scanning
    // ---------------------------------------------------------------------------------------------

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

        var drive = DriveService.GetDrive(root);
        var tree = new ScanTree(root);
        if (drive is not null)
        {
            tree.Metadata.VolumeTotalBytes = drive.TotalBytes;
            tree.Metadata.VolumeFreeBytes = drive.FreeBytes;
            tree.Metadata.VolumeLabel = drive.Label;
            tree.Metadata.FileSystem = drive.FileSystem;
            tree.Metadata.VolumeSerialNumber = drive.SerialNumber;
        }

        // Progress is relative to used space only when scanning a whole drive.
        _scanUsedBytes = root.Length == 3 && drive is not null ? drive.UsedBytes : 0;
        ProgressIndeterminate = _scanUsedBytes == 0;
        ProgressPercent = 0;

        Tree = tree;
        LastResult = null;
        ErrorCount = 0;
        ResetAnalysis();
        SelectDriveFor(root);

        _scanCts = new CancellationTokenSource();
        _pause = new PauseGate();
        int workers = Settings.Workers > 0 ? Settings.Workers : drive?.RecommendedParallelism ?? 8;
        var scanner = ScannerFactory.Create(Settings.Engine);

        GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
        _allocatedAtStart = GC.GetTotalAllocatedBytes();
        _gen0AtStart = GC.CollectionCount(0);
        _gen1AtStart = GC.CollectionCount(1);
        _gen2AtStart = GC.CollectionCount(2);
        _tick = 0;
        _scanClock.Restart();

        State = ScanState.Scanning;
        StatusTitle = $"Scanning {root}";
        UpdateProgress();
        TreeReplaced?.Invoke(this, EventArgs.Empty);
        _timer.Start();

        var task = scanner.ScanAsync(tree, new ScanOptions { MaxParallelism = workers }, _pause, _scanCts.Token);
        _ = CompleteScanAsync(task, tree, workers);
    }

    private async Task CompleteScanAsync(Task<ScanResult> task, ScanTree tree, int workers)
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
        _scanClock.Stop();
        if (Tree != tree)
        {
            return;
        }

        LastResult = result;
        ErrorCount = tree.ErrorCount;
        UiRefreshRate = _tick / Math.Max(0.001, _scanClock.Elapsed.TotalSeconds);
        using (var process = Process.GetCurrentProcess())
        {
            LastDiagnostics = new ScanDiagnostics(
                result.Files, result.Directories, result.Bytes, result.Duration,
                result.FilesPerSecond, result.DirectoriesPerSecond, result.BytesPerSecond,
                process.PeakWorkingSet64,
                GC.GetTotalAllocatedBytes() - _allocatedAtStart,
                GC.CollectionCount(0) - _gen0AtStart, GC.CollectionCount(1) - _gen1AtStart, GC.CollectionCount(2) - _gen2AtStart,
                UiRefreshRate, workers, result.Scanner, tree.FileRecordCount, tree.ApproximateRecordBytes);
        }

        State = result.Cancelled ? ScanState.Cancelled : ScanState.Completed;
        UpdateStatusForFinishedTree();
        ScanFinished?.Invoke(this, EventArgs.Empty);

        await AnalyzeAsync(tree);

        if (!result.Cancelled && Settings.RememberScans)
        {
            Settings.LastRoot = tree.RootPath;
            SettingsService.Save(Settings);
            await Task.Run(() =>
            {
                DriveService.FillVolumeMetadata(tree);
                SnapshotStore.Save(tree);
            });
        }

        if (Tree == tree && !IsScanning)
        {
            ReleaseScanGarbage();
        }

        _ = LoadAppsAsync();
    }

    public void TogglePause()
    {
        if (_pause is null || !IsScanning)
        {
            return;
        }

        if (_pause.IsPaused)
        {
            _pause.Resume();
            _scanClock.Start();
            State = ScanState.Scanning;
        }
        else
        {
            _pause.Pause();
            _scanClock.Stop();
            State = ScanState.Paused;
        }

        UpdateProgress();
    }

    public void CancelScan()
    {
        _pause?.Resume();
        _scanCts?.Cancel();
    }

    public void Rescan()
    {
        string? root = Tree?.RootPath ?? SelectedDrive?.RootPath;
        if (root is not null)
        {
            StartScan(root);
        }
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        _tick++;
        UpdateProgress();
        if (_tick % LiveRefreshEveryTicks == 0 && Tree is not null)
        {
            UpdateLiveCategories(Tree);
            LiveRefresh?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateProgress()
    {
        var tree = Tree;
        if (tree is null)
        {
            return;
        }

        long bytes = tree.BytesScanned;
        StatusDetail = $"Files {SizeFormatter.FormatCount(tree.FilesScanned)}  ·  Folders {SizeFormatter.FormatCount(tree.DirectoriesScanned)}  ·  Analyzed {SizeFormatter.Format(bytes)}  ·  {FormatElapsed(_scanClock.Elapsed)}";
        StatusCurrent = IsPaused ? "Paused" : tree.CurrentPath ?? "";
        StatusTitle = IsPaused ? $"Paused – {tree.RootPath}" : $"Scanning {tree.RootPath}";
        if (_scanUsedBytes > 0)
        {
            ProgressPercent = Math.Min(99, bytes * 100.0 / _scanUsedBytes);
        }

        ErrorCount = tree.ErrorCount;
    }

    private void UpdateStatusForFinishedTree()
    {
        var tree = Tree!;
        var m = tree.Metadata;
        ProgressPercent = 100;
        ProgressIndeterminate = false;
        StatusCurrent = "";
        string files = SizeFormatter.FormatCount(tree.Root.TotalFiles);
        string errors = tree.ErrorCount > 0 ? $"  ·  {SizeFormatter.FormatCount(tree.ErrorCount)} inaccessible locations" : "";
        StatusTitle = State switch
        {
            ScanState.Cancelled => $"Scan cancelled – partial results for {tree.RootPath}",
            ScanState.Snapshot => $"Last scanned {FormatWhen(m.CompletedUtc ?? m.StartedUtc)} – {tree.RootPath}",
            _ => $"Scan complete – {tree.RootPath}",
        };
        StatusDetail = $"{files} files analyzed  ·  {SizeFormatter.Format(tree.Root.TotalSize)}  ·  {SizeFormatter.FormatCount(tree.DirectoryCount)} folders" +
            (m.Duration > TimeSpan.Zero ? $"  ·  {m.Duration.TotalSeconds:0.0} s" : "") + errors;
    }

    public static string FormatWhen(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Now.Date;
        string day = local.Date == today ? "today" : local.Date == today.AddDays(-1) ? "yesterday" : local.ToString("d");
        return $"{day}, {local:t}";
    }

    private static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void SelectDriveFor(string root)
    {
        var match = Drives.FirstOrDefault(d => PathUtil.IsSameOrUnder(root, d.RootPath));
        if (match is not null && match != SelectedDrive)
        {
            SelectedDrive = match;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Analysis
    // ---------------------------------------------------------------------------------------------

    private void ResetAnalysis()
    {
        _analysisCts?.Cancel();
        Findings = [];
        CategoryTotals = [];
        UnitDirectories = [];
        FindingByDirectory = new();
        FindingByFile = new();
        _locationMap = new();
        AnalysisChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Quick location-based categories while the scan runs (detectors run after it finishes).</summary>
    private void UpdateLiveCategories(ScanTree tree)
    {
        var map = LocationClassifier.BuildBaseMap(tree, Known);
        CategoryTotals = LocationClassifier.Summarize(tree, map);
        _locationMap = map;
    }

    public async Task AnalyzeAsync(ScanTree tree)
    {
        _analysisCts?.Cancel();
        var cts = _analysisCts = new CancellationTokenSource();
        AnalysisRunning = true;
        bool includeDev = Settings.DetectDeveloperFiles;
        try
        {
            var analysis = await Task.Run(() =>
            {
                var context = new DetectionContext { Tree = tree, Known = Known };
                var findings = DetectionRunner.Run(context, DetectionRunner.CreateDefault(includeDev), cts.Token,
                    (id, ex) => Debug.WriteLine($"Detector {id} failed: {ex}"));
                var map = LocationClassifier.BuildBaseMap(tree, Known);
                LocationClassifier.ApplyFindings(map, findings.Where(f => !f.IsFile));
                var totals = LocationClassifier.Summarize(tree, map, findings.Where(f => f.IsFile));
                return (findings, map, totals);
            }, cts.Token);

            if (Tree != tree || cts.IsCancellationRequested)
            {
                return;
            }

            Findings = analysis.findings;
            _locationMap = analysis.map;
            CategoryTotals = analysis.totals;
            UnitDirectories = analysis.findings.Where(f => f.IsUnit && f.DirectoryIndex > 0).Select(f => f.DirectoryIndex).ToHashSet();
            FindingByDirectory = new();
            FindingByFile = new();
            foreach (var f in analysis.findings)
            {
                if (f.DirectoryIndex > 0)
                {
                    FindingByDirectory.TryAdd(f.DirectoryIndex, f);
                }
                else if (f.FileIndex >= 0)
                {
                    FindingByFile.TryAdd(f.FileIndex, f);
                }
            }

            MarkGames();
            AnalysisChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// A scan or snapshot load leaves behind a large amount of short-lived garbage (path strings,
    /// enumeration buffers). One compacting collection returns that memory to Windows instead of
    /// keeping it committed for the rest of the session.
    /// </summary>
    private static void ReleaseScanGarbage() =>
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    public LocationCategory CategoryOf(int dirIndex) =>
        Tree is null ? LocationCategory.Other : LocationClassifier.NearestCategory(Tree, _locationMap, dirIndex);

    // ---------------------------------------------------------------------------------------------
    // Installed apps
    // ---------------------------------------------------------------------------------------------

    /// <param name="includeStore">
    /// Store (MSIX) packages are only enumerated when the Apps page asks for them: the Windows
    /// package API costs tens of megabytes of memory, which most sessions never need.
    /// </param>
    public async Task LoadAppsAsync(bool force = false, bool includeStore = false)
    {
        bool wantStore = includeStore && Settings.IncludeStoreApps;
        bool needReload = force || !AppsLoaded || (wantStore && !_storeAppsLoaded);
        if (AppsLoading)
        {
            _reloadAppsWhenDone |= needReload && (force || wantStore);
            return;
        }

        if (!needReload)
        {
            MeasureAppsFromTree();
            return;
        }

        wantStore |= _storeAppsLoaded && Settings.IncludeStoreApps;
        AppsLoading = true;
        try
        {
            Apps = await Task.Run(() => InstalledAppService.LoadAll(wantStore));
            AppsLoaded = true;
            _storeAppsLoaded = wantStore;
            MeasureAppsFromTree();
            AppsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            AppsLoading = false;
        }

        if (_reloadAppsWhenDone)
        {
            _reloadAppsWhenDone = false;
            await LoadAppsAsync(force: true, includeStore: true);
            return;
        }

        await MeasureRemainingAppsAsync();
    }

    private void MeasureAppsFromTree()
    {
        var tree = Tree;
        if (tree is null || IsScanning)
        {
            return;
        }

        foreach (var app in Apps)
        {
            InstalledAppService.TryMeasureFromTree(app, tree);
        }

        MarkGames();
        AppsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkGames()
    {
        if (Tree is null)
        {
            return;
        }

        foreach (var app in Apps)
        {
            if (app.InstallLocation is null)
            {
                continue;
            }

            int index = Tree.FindDirectory(app.InstallLocation);
            app.IsGame = index > 0 && CategoryOf(index) == LocationCategory.Games;
        }
    }

    /// <summary>Measures install folders that the current scan does not cover, two at a time, in the background.</summary>
    private async Task MeasureRemainingAppsAsync()
    {
        var pending = Apps.Where(a => a.Measurement == MeasurementState.NotMeasured && a.InstallLocation is not null).ToList();
        int sinceNotify = 0;
        foreach (var app in pending)
        {
            if (IsScanning)
            {
                return;
            }

            await InstalledAppService.MeasureOnDiskAsync(app, CancellationToken.None);
            if (++sinceNotify >= 5)
            {
                sinceNotify = 0;
                AppsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        if (pending.Count > 0)
        {
            AppsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveApp(InstalledApp app)
    {
        Apps.Remove(app);
        AppsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finds the installed app whose install folder contains <paramref name="path"/>.</summary>
    public InstalledApp? FindAppForPath(string path) =>
        Apps.Where(a => a.InstallLocation is not null && !a.InstallLocationInferred && PathUtil.IsSameOrUnder(path, a.InstallLocation))
            .OrderByDescending(a => a.InstallLocation!.Length)
            .FirstOrDefault()
        ?? Apps.Where(a => a.InstallLocation is not null && PathUtil.IsSameOrUnder(path, a.InstallLocation))
            .OrderByDescending(a => a.InstallLocation!.Length)
            .FirstOrDefault();

    // ---------------------------------------------------------------------------------------------
    // Mutation
    // ---------------------------------------------------------------------------------------------

    public void RemoveFromTree(bool isFile, int index) => RemoveFromTree([(isFile, index)]);

    /// <summary>Removes items that no longer exist on disk, then re-analyzes and re-saves once.</summary>
    public void RemoveFromTree(IEnumerable<(bool IsFile, int Index)> items)
    {
        var tree = Tree;
        if (tree is null)
        {
            return;
        }

        int removed = 0;
        foreach (var (isFile, index) in items)
        {
            if (isFile ? !tree.IsLiveFile(index) : !tree.IsLiveDirectory(index) || index == ScanTree.RootIndex)
            {
                continue;
            }

            if (isFile)
            {
                tree.RemoveFile(index);
            }
            else
            {
                tree.RemoveDirectory(index);
            }

            removed++;
        }

        if (removed == 0)
        {
            return;
        }

        UpdateStatusForFinishedTree();
        TreeMutated?.Invoke(this, EventArgs.Empty);
        _ = AnalyzeAsync(tree);
        if (Settings.RememberScans)
        {
            _ = Task.Run(() => SnapshotStore.Save(tree));
        }
    }

    public void ApplySettings()
    {
        SettingsService.Save(Settings);
    }

    public void RunOnUi(Action action) => _dispatcher.TryEnqueue(() => action());
}

public sealed record NavigationRequest(string Page, object? Parameter);
