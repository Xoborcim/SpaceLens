using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;
using SpaceLens.Core.Scanning;
using SpaceLens.Mac;

namespace SpaceLens.Desktop.Platform;

/// <summary>macOS (and, for development, other Unix systems) through SpaceLens.Mac.</summary>
public sealed class MacDesktopPlatform : IDesktopPlatform
{
    private readonly MacKnownLocations _known;
    private readonly MacShell _shell;

    public MacDesktopPlatform()
        : this(MacKnownLocations.FromEnvironment())
    {
    }

    /// <param name="dataDirectory">Where settings and saved scans go; the platform default when null.</param>
    public MacDesktopPlatform(MacKnownLocations known, string? dataDirectory = null)
    {
        _known = known;
        var policy = new MacSafetyPolicy(known);
        Safety = policy;
        _shell = new MacShell(policy);
        DataDirectory = dataDirectory ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(known.Home, "Library", "Application Support", "SpaceLens")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens"));
    }

    public string Home => _known.Home;

    public string DataDirectory { get; }

    public SizeUnits Units => OperatingSystem.IsMacOS() ? SizeUnits.Decimal : SizeUnits.Binary;

    public string TrashName => "Trash";

    public IItemSafetyPolicy Safety { get; }

    public string? FullDiskAccessSettingsUrl => OperatingSystem.IsMacOS() ? MacShell.FullDiskAccessSettingsUrl : null;

    public IReadOnlyList<VolumeInfo> GetVolumes() =>
        MacVolumes.GetVolumes().Select(v => new VolumeInfo(v.RootPath, v.Name, v.TotalBytes, v.FreeBytes, v.IsStartup, v.IsRemovable)).ToList();

    public IDirectoryEnumeratorFactory CreateEnumeratorFactory() => new UnixDirectoryEnumeratorFactory();

    public IReadOnlyCollection<string> ExcludedPathsFor(string root) => MacVolumes.ExcludedPathsFor(root);

    public List<StorageFinding> Detect(ScanTree tree, CancellationToken cancellationToken) =>
        MacStorageDetector.Detect(tree, _known, File.Exists, cancellationToken);

    public Dictionary<int, LocationCategory> BuildLocationMap(ScanTree tree) => MacLocations.BuildBaseMap(tree, _known);

    public string Label(LocationCategory category) => MacLocations.Label(category);

    public IReadOnlyList<TrashResult> MoveToTrash(IReadOnlyList<(string Path, bool IsDirectory)> items) =>
        _shell.MoveToTrash(items).Select(r => new TrashResult(r.Path, r.Result.Success, r.Result.Error)).ToList();

    public void Reveal(string path) => MacShell.Reveal(path);

    public void Open(string path) => MacShell.Open(path);

    public void OpenUrl(string url) => MacShell.OpenUrl(url);

    public bool? HasFullDiskAccess() => MacShell.HasFullDiskAccess(_known.Home);

    public List<MacApp> FindApps(CancellationToken cancellationToken) => MacApps.Find(_known, cancellationToken);

    public List<MacLeftover> FindLeftovers(MacApp app) => MacApps.FindLeftovers(app, _known.Home);

    public TrashResult RemoveApp(MacApp app)
    {
        var result = _shell.RemoveApplication(app);
        return new TrashResult(app.BundlePath, result.Success, result.Error);
    }
}
