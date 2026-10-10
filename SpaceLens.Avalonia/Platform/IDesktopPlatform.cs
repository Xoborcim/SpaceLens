using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;
using SpaceLens.Core.Scanning;
using SpaceLens.Mac;

namespace SpaceLens.Desktop.Platform;

/// <summary>A volume (disk) the person can scan.</summary>
public sealed record VolumeInfo(string RootPath, string Name, long TotalBytes, long FreeBytes, bool IsStartup, bool IsRemovable)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
}

public sealed record TrashResult(string Path, bool Success, string? Error);

/// <summary>Everything the app needs from the operating system. One implementation per platform; tests use a fake.</summary>
public interface IDesktopPlatform
{
    string Home { get; }

    /// <summary>Folder for settings and saved scans.</summary>
    string DataDirectory { get; }

    /// <summary>How sizes are shown and typed (macOS Finder uses decimal units).</summary>
    SizeUnits Units { get; }

    /// <summary>Name of the trash for this platform ("Trash").</summary>
    string TrashName { get; }

    IItemSafetyPolicy Safety { get; }

    IReadOnlyList<VolumeInfo> GetVolumes();

    IDirectoryEnumeratorFactory CreateEnumeratorFactory();

    IReadOnlyCollection<string> ExcludedPathsFor(string root);

    List<StorageFinding> Detect(ScanTree tree, CancellationToken cancellationToken);

    Dictionary<int, LocationCategory> BuildLocationMap(ScanTree tree);

    string Label(LocationCategory category);

    IReadOnlyList<TrashResult> MoveToTrash(IReadOnlyList<(string Path, bool IsDirectory)> items);

    void Reveal(string path);

    void Open(string path);

    void OpenUrl(string url);

    /// <summary>False when the app lacks access to protected folders (macOS Full Disk Access); null when unknown.</summary>
    bool? HasFullDiskAccess();

    string? FullDiskAccessSettingsUrl { get; }

    List<MacApp> FindApps(CancellationToken cancellationToken);

    List<MacLeftover> FindLeftovers(MacApp app);

    TrashResult RemoveApp(MacApp app);
}
