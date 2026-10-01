using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.Core.Classification;

/// <summary>
/// What a location is, in cautious terms. SpaceLens never labels anything "safe to delete".
/// </summary>
public enum StorageNature
{
    Unknown,
    Temporary,
    Cache,
    UserFile,
    ApplicationData,
    SystemFile,
    Game,
    Application,
    DeveloperArtifact,
    VirtualDisk,
}

/// <summary>Coarse buckets used for the Overview "Largest categories" summary.</summary>
public enum LocationCategory
{
    Other,
    Apps,
    Games,
    Downloads,
    Documents,
    Pictures,
    Videos,
    Music,
    Desktop,
    UserFiles,
    AppData,
    Developer,
    TemporaryAndCache,
    RecycleBin,
    Windows,
    VirtualMachines,
}

public static class StorageNatureInfo
{
    public static string Label(StorageNature nature) => nature switch
    {
        StorageNature.Temporary => "Temporary",
        StorageNature.Cache => "Cache",
        StorageNature.UserFile => "User file",
        StorageNature.ApplicationData => "Application data",
        StorageNature.SystemFile => "System file",
        StorageNature.Game => "Game",
        StorageNature.Application => "Application",
        StorageNature.DeveloperArtifact => "Developer files",
        StorageNature.VirtualDisk => "Virtual disk",
        _ => "Unknown",
    };

    public static string Label(LocationCategory category) => category switch
    {
        LocationCategory.Apps => "Apps",
        LocationCategory.Games => "Games",
        LocationCategory.Downloads => "Downloads",
        LocationCategory.Documents => "Documents",
        LocationCategory.Pictures => "Pictures",
        LocationCategory.Videos => "Videos",
        LocationCategory.Music => "Music",
        LocationCategory.Desktop => "Desktop",
        LocationCategory.UserFiles => "Other user files",
        LocationCategory.AppData => "App data",
        LocationCategory.Developer => "Developer files",
        LocationCategory.TemporaryAndCache => "Temporary & cache",
        LocationCategory.RecycleBin => "Recycle Bin",
        LocationCategory.Windows => "Windows & system",
        LocationCategory.VirtualMachines => "Virtual machines",
        _ => "Other",
    };

    public static uint Color(LocationCategory category) => category switch
    {
        LocationCategory.Apps => 0xFF539BF5,
        LocationCategory.Games => 0xFF57AB5A,
        LocationCategory.Downloads => 0xFFE0823D,
        LocationCategory.Documents => 0xFF6CB6FF,
        LocationCategory.Pictures => 0xFFDB61A2,
        LocationCategory.Videos => 0xFFE5534B,
        LocationCategory.Music => 0xFFB083F0,
        LocationCategory.Desktop => 0xFFC69026,
        LocationCategory.UserFiles => 0xFF96D0FF,
        LocationCategory.AppData => 0xFF39C5CF,
        LocationCategory.Developer => 0xFF8256D0,
        LocationCategory.TemporaryAndCache => 0xFFA2A2A2,
        LocationCategory.RecycleBin => 0xFF768390,
        LocationCategory.Windows => 0xFF4E5A65,
        LocationCategory.VirtualMachines => 0xFF2EA043,
        _ => 0xFF909DAB,
    };
}

/// <summary>
/// A notable location found by an <see cref="IStorageDetector"/>, such as a Steam game, a node_modules
/// folder or the Windows Update cache.
/// </summary>
public sealed record StorageFinding
{
    public required string DetectorId { get; init; }

    /// <summary>Grouping shown in the UI, e.g. "Steam", "node_modules", "Browser cache".</summary>
    public required string Group { get; init; }

    public required string Title { get; init; }

    public required string Path { get; init; }

    public long Size { get; init; }

    /// <summary>Index of the directory in the scan tree, or -1.</summary>
    public int DirectoryIndex { get; init; } = -1;

    /// <summary>Index of the file in the scan tree, or -1.</summary>
    public int FileIndex { get; init; } = -1;

    public StorageNature Nature { get; init; }

    public LocationCategory Category { get; init; }

    /// <summary>What this location contains, in plain language.</summary>
    public string? Explanation { get; init; }

    /// <summary>A legitimate way to reduce the size (e.g. a Windows setting), when one exists.</summary>
    public string? Advice { get; init; }

    /// <summary>Should be reported as one unit rather than broken into subfolders.</summary>
    public bool IsUnit { get; init; } = true;

    /// <summary>Whether the location is worth showing as a space-recovery opportunity on the overview.</summary>
    public bool IsOpportunity { get; init; }

    /// <summary>
    /// False when the location must not be removed by deleting files (system-managed data, virtual disks
    /// that need their own tools, games managed by a launcher). The UI then only shows <see cref="Advice"/>.
    /// </summary>
    public bool AllowDirectRemoval { get; init; } = true;

    /// <summary>Official removal or management action, e.g. <c>steam://uninstall/570</c> or <c>ms-settings:storagesense</c>.</summary>
    public string? ActionUri { get; init; }

    public string? ActionLabel { get; init; }

    public bool IsFile => FileIndex >= 0;
}

public sealed class DetectionContext
{
    public required ScanTree Tree { get; init; }

    public required KnownLocations Known { get; init; }

    /// <summary>Optional probe for files that the scan did not index (small marker files like Cargo.toml).</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    public Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;
}

/// <summary>
/// Recognizes a family of storage locations in a completed scan. Detectors are independent of the
/// UI; new ones can be added without touching view code.
/// </summary>
public interface IStorageDetector
{
    string Id { get; }

    string DisplayName { get; }

    IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken);
}
