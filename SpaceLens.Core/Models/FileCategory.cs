namespace SpaceLens.Core.Models;

/// <summary>
/// Content type of a file, derived from its extension. Stored as a byte in compact records.
/// </summary>
public enum FileCategory : byte
{
    Other = 0,
    Video,
    Audio,
    Image,
    Document,
    Archive,
    DiskImage,
    VirtualMachine,
    Installer,
    Executable,
    GameData,
    Database,
    DeveloperFile,
    SystemFile,
    LogsAndDumps,
}

public static class FileCategoryInfo
{
    public const int Count = (int)FileCategory.LogsAndDumps + 1;

    public static IReadOnlyList<FileCategory> All { get; } = Enum.GetValues<FileCategory>();

    public static string DisplayName(FileCategory category) => category switch
    {
        FileCategory.Video => "Videos",
        FileCategory.Audio => "Audio",
        FileCategory.Image => "Images",
        FileCategory.Document => "Documents",
        FileCategory.Archive => "ZIP/Archives",
        FileCategory.DiskImage => "Disk images (ISO)",
        FileCategory.VirtualMachine => "Virtual machines",
        FileCategory.Installer => "Installers",
        FileCategory.Executable => "Programs & libraries",
        FileCategory.GameData => "Game archives",
        FileCategory.Database => "Databases",
        FileCategory.DeveloperFile => "Developer files",
        FileCategory.SystemFile => "System files",
        FileCategory.LogsAndDumps => "Logs & dumps",
        _ => "Other",
    };

    public static string ShortName(FileCategory category) => category switch
    {
        FileCategory.Video => "Video",
        FileCategory.Audio => "Audio",
        FileCategory.Image => "Image",
        FileCategory.Document => "Document",
        FileCategory.Archive => "Archive",
        FileCategory.DiskImage => "Disk image",
        FileCategory.VirtualMachine => "Virtual disk",
        FileCategory.Installer => "Installer",
        FileCategory.Executable => "Program",
        FileCategory.GameData => "Game data",
        FileCategory.Database => "Database",
        FileCategory.DeveloperFile => "Developer",
        FileCategory.SystemFile => "System",
        FileCategory.LogsAndDumps => "Log/dump",
        _ => "File",
    };

    /// <summary>ARGB color used for charts and treemaps.</summary>
    public static uint Color(FileCategory category) => category switch
    {
        FileCategory.Video => 0xFFE5534B,
        FileCategory.Audio => 0xFFDB61A2,
        FileCategory.Image => 0xFF57AB5A,
        FileCategory.Document => 0xFF539BF5,
        FileCategory.Archive => 0xFFC69026,
        FileCategory.DiskImage => 0xFFB083F0,
        FileCategory.VirtualMachine => 0xFF8256D0,
        FileCategory.Installer => 0xFFE0823D,
        FileCategory.Executable => 0xFF6CB6FF,
        FileCategory.GameData => 0xFF2EA043,
        FileCategory.Database => 0xFF39C5CF,
        FileCategory.DeveloperFile => 0xFF96D0FF,
        FileCategory.SystemFile => 0xFF768390,
        FileCategory.LogsAndDumps => 0xFFA2A2A2,
        _ => 0xFF909DAB,
    };
}
