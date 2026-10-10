using SpaceLens.Core.Models;

namespace SpaceLens.Core.Safety;

public enum ProtectionLevel
{
    /// <summary>Ordinary user-controlled location.</summary>
    None,

    /// <summary>Removal is possible but needs a prominent warning (application data, files in system areas).</summary>
    Caution,

    /// <summary>SpaceLens refuses to delete this. The user is pointed to a Windows-managed alternative.</summary>
    Protected,
}

/// <summary>Decides whether an item may be removed through SpaceLens, and how strongly to warn. One per platform.</summary>
public interface IItemSafetyPolicy
{
    SafetyAssessment AssessDirectory(string path, NodeFlags flags = NodeFlags.None);

    SafetyAssessment AssessFile(string path, FileAttributes attributes = 0);
}

public sealed record SafetyAssessment(ProtectionLevel Level, string Label, string? Explanation = null, string? Advice = null)
{
    public static SafetyAssessment Ordinary { get; } = new(ProtectionLevel.None, "User file");

    public bool CanDelete => Level != ProtectionLevel.Protected;

    public bool IsSystemManaged { get; init; }
}

/// <summary>Well-known locations used by <see cref="SafetyPolicy"/>. Injectable for tests.</summary>
public sealed class KnownLocations
{
    public required string SystemDrive { get; init; }
    public required string WindowsDirectory { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string ProgramData { get; init; }
    public required string UsersDirectory { get; init; }
    public required string UserProfile { get; init; }
    public required string LocalAppData { get; init; }
    public required string RoamingAppData { get; init; }
    public required string TempDirectory { get; init; }
    public IReadOnlyList<string> UserKnownFolders { get; init; } = [];

    public static KnownLocations FromEnvironment()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string systemDrive = Path.GetPathRoot(windows) is { Length: > 0 } root ? root : @"C:\";
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string users = Path.GetDirectoryName(profile) ?? Path.Combine(systemDrive, "Users");

        var known = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "OneDrive"),
            Path.Combine(profile, "Favorites"),
            Path.Combine(profile, "Saved Games"),
            Path.Combine(profile, "Contacts"),
            Path.Combine(profile, "Links"),
            Path.Combine(profile, "Searches"),
        };

        return new KnownLocations
        {
            SystemDrive = PathUtil.NormalizeDisplayPath(systemDrive),
            WindowsDirectory = windows,
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            UsersDirectory = users,
            UserProfile = profile,
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            TempDirectory = Path.GetTempPath(),
            UserKnownFolders = known.Where(p => !string.IsNullOrEmpty(p)).ToList(),
        };
    }
}

/// <summary>
/// Decides whether a path may be removed through SpaceLens and how strongly to warn.
/// The policy is deliberately conservative: it blocks whole system and application directories,
/// and only warns (never blocks) for individual files in sensitive places.
/// </summary>
public sealed class SafetyPolicy : IItemSafetyPolicy
{
    private readonly KnownLocations _known;
    private readonly string[] _essentialRoots;
    private readonly string[] _settingsFolders;

    /// <summary>Folders Windows creates in every profile. In another user's profile they are protected like our own.</summary>
    private static readonly HashSet<string> ProfileFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive", "Favorites",
        "Saved Games", "Contacts", "Links", "Searches", "3D Objects",
    };

    /// <summary>Profiles Windows itself maintains: the template for new accounts and legacy compatibility links.</summary>
    private static readonly HashSet<string> SystemProfileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default", "Default User", "All Users",
    };

    private static readonly Dictionary<string, (string Label, string Explanation, string? Advice)> SystemRootFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pagefile.sys"] = ("Page file", "Virtual memory used by Windows when RAM is full.",
                "To change its size: Settings > System > About > Advanced system settings > Performance > Advanced > Virtual memory."),
            ["hiberfil.sys"] = ("Hibernation file", "Stores memory contents for hibernation and Fast Startup.",
                "Run \"powercfg /h /type reduced\" to shrink it, or \"powercfg /hibernate off\" (as administrator) to remove it and disable hibernation."),
            ["swapfile.sys"] = ("Swap file", "Used by Windows for suspending Store apps. Usually small.", null),
            ["DumpStack.log.tmp"] = ("Crash dump log", "Created by Windows during boot for crash dump support.", null),
            ["DumpStack.log"] = ("Crash dump log", "Created by Windows during boot for crash dump support.", null),
            ["bootmgr"] = ("Boot manager", "Required to start Windows.", null),
            ["BOOTNXT"] = ("Boot file", "Required to start Windows.", null),
            ["BOOTSECT.BAK"] = ("Boot sector backup", "Boot sector backup created by Windows Setup.", null),
        };

    private static readonly Dictionary<string, (string Label, string Explanation, string? Advice)> SpecialRootFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["System Volume Information"] = ("System Restore & shadow copies", "Restore points and volume shadow copies managed by Windows.",
                "Adjust in Control Panel > System > System Protection > Configure (max usage), or delete restore points there."),
            ["$Recycle.Bin"] = ("Recycle Bin", "Items deleted to the Recycle Bin.", "Empty the Recycle Bin to free this space."),
            ["Recovery"] = ("Recovery environment", "Windows recovery files.", null),
            ["Boot"] = ("Boot files", "Required to start Windows.", null),
            ["EFI"] = ("EFI boot files", "Required to start the computer.", null),
            ["$WinREAgent"] = ("Windows Update recovery data", "Temporary data used by Windows Update.", "Removed automatically by Windows."),
            ["$WINDOWS.~BT"] = ("Windows upgrade files", "Temporary files from a Windows upgrade.", "Settings > System > Storage > Temporary files."),
            ["$WINDOWS.~WS"] = ("Windows upgrade files", "Temporary files from a Windows upgrade.", "Settings > System > Storage > Temporary files."),
            ["$SysReset"] = ("Reset logs", "Logs from a Windows reset.", null),
            ["Config.Msi"] = ("Windows Installer rollback", "Rollback data used during software installation.", null),
            ["Documents and Settings"] = ("Compatibility link", "Legacy link to the Users folder.", null),
            ["Windows.old"] = ("Previous Windows installation", "Files from the Windows version you upgraded from.",
                "Remove it with Settings > System > Storage > Temporary files > \"Previous Windows installation(s)\"."),
        };

    private static readonly Dictionary<string, (string Label, string Explanation, string? Advice)> WindowsSubfolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["WinSxS"] = ("Windows component store", "Holds Windows components and updates. Many files are hard links shared with System32, so its apparent size overstates the real usage.",
                "Run Disk Cleanup > \"Clean up system files\", or \"Dism /Online /Cleanup-Image /StartComponentCleanup\" as administrator."),
            ["Installer"] = ("Windows Installer cache", "Files needed to repair, update and uninstall installed programs.",
                "Never delete manually. Uninstalling programs you no longer need reduces it."),
            ["SoftwareDistribution"] = ("Windows Update cache", "Downloaded Windows updates.",
                "Settings > System > Storage > Temporary files > \"Windows Update Cleanup\"."),
            ["System32"] = ("System32", "Core Windows system files.", null),
            ["SysWOW64"] = ("SysWOW64", "32-bit Windows system files.", null),
            ["Temp"] = ("Windows temporary files", "Temporary files created by Windows and installers.",
                "Settings > System > Storage > Temporary files, or Storage Sense."),
            ["Logs"] = ("Windows logs", "Diagnostic logs written by Windows.", null),
            ["Prefetch"] = ("Prefetch", "Application launch optimization data.", null),
            ["assembly"] = ("Global assembly cache", ".NET Framework shared assemblies.", null),
            ["Microsoft.NET"] = (".NET Framework", "Installed .NET Framework runtimes.", null),
        };

    public SafetyPolicy(KnownLocations known)
    {
        _known = known;
        _essentialRoots = new[]
        {
            known.WindowsDirectory, known.ProgramFiles, known.ProgramFilesX86, known.ProgramData, known.UsersDirectory,
            known.UserProfile, known.LocalAppData, known.RoamingAppData, known.TempDirectory,
            Path.Combine(known.UserProfile, "AppData"),
            Path.Combine(known.LocalAppData, "Packages"),
            Path.Combine(known.ProgramFiles, "WindowsApps"),
        }
        .Concat(known.UserKnownFolders)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(PathUtil.NormalizeDisplayPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        // Protected folders whose loose files are settings or program data rather than user documents.
        _settingsFolders = new[]
        {
            known.UsersDirectory, known.UserProfile, Path.Combine(known.UserProfile, "AppData"), known.LocalAppData,
            known.RoamingAppData, known.ProgramData, Path.Combine(known.LocalAppData, "Packages"),
        }
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(PathUtil.NormalizeDisplayPath)
        .ToArray();
    }

    public KnownLocations Known => _known;

    public SafetyAssessment AssessDirectory(string path, NodeFlags flags = NodeFlags.None)
    {
        path = PathUtil.NormalizeDisplayPath(path);

        if (path.Length <= 3 || path.StartsWith(@"\\", StringComparison.Ordinal) && path.Count(c => c == '\\') <= 3)
        {
            return Protected("Drive root", "The top level of a drive cannot be removed.");
        }

        foreach (var root in _essentialRoots)
        {
            // The essential folder itself, or any folder that contains one (e.g. C:\Users).
            if (PathUtil.IsSameOrUnder(root, path))
            {
                return Protected("Essential folder", "This folder is required by Windows or by your user profile. Its contents can be reviewed individually.");
            }
        }

        if (AssessOtherProfile(path) is { } profileAssessment)
        {
            return profileAssessment;
        }

        string name = PathUtil.GetName(path);
        string? parent = PathUtil.GetParent(path);
        bool atDriveRoot = parent is { Length: 3 };

        if (atDriveRoot && SpecialRootFolders.TryGetValue(name, out var special))
        {
            return Protected(special.Label, special.Explanation, special.Advice) with { IsSystemManaged = true };
        }

        foreach (var (rootName, info) in SpecialRootFolders)
        {
            string? driveRoot = Path.GetPathRoot(path);
            if (driveRoot is not null && PathUtil.IsStrictlyUnder(path, Path.Combine(driveRoot, rootName)))
            {
                return Protected(info.Label, info.Explanation, info.Advice) with { IsSystemManaged = true };
            }
        }

        if (PathUtil.IsStrictlyUnder(path, _known.WindowsDirectory))
        {
            string relative = path[(PathUtil.NormalizeDisplayPath(_known.WindowsDirectory).Length + 1)..];
            string first = relative.Split('\\')[0];
            if (WindowsSubfolders.TryGetValue(first, out var info))
            {
                return Protected(info.Label, info.Explanation + " Managed by Windows. Manual deletion is not recommended.", info.Advice) with { IsSystemManaged = true };
            }

            return Protected("Managed by Windows", "Part of the Windows installation. Manual deletion can break Windows.",
                "Use Settings > System > Storage > Temporary files or Disk Cleanup to reclaim Windows space.") with { IsSystemManaged = true };
        }

        if (PathUtil.IsStrictlyUnder(path, _known.ProgramFiles) || PathUtil.IsStrictlyUnder(path, _known.ProgramFilesX86))
        {
            return Protected("Installed application", "Folders of installed programs must be removed with the program's uninstaller, not deleted.",
                "Use the Apps page to run the official uninstaller.");
        }

        if (PathUtil.IsStrictlyUnder(path, _known.ProgramData))
        {
            return new SafetyAssessment(ProtectionLevel.Caution, "Application data",
                "Shared data used by installed programs for all users. Programs may stop working or lose data if it is removed.");
        }

        if (PathUtil.IsStrictlyUnder(path, Path.Combine(_known.UserProfile, "AppData")) ||
            PathUtil.IsStrictlyUnder(path, _known.LocalAppData) || PathUtil.IsStrictlyUnder(path, _known.RoamingAppData))
        {
            if (PathUtil.IsSameOrUnder(path, _known.TempDirectory))
            {
                return new SafetyAssessment(ProtectionLevel.Caution, "Temporary",
                    "Temporary files. Programs that are running may still be using some of them.",
                    "Settings > System > Storage > Temporary files can remove these safely.");
            }

            return new SafetyAssessment(ProtectionLevel.Caution, "Application data",
                "Settings, caches or data of an application. Removing it may reset the application or lose data.");
        }

        if ((flags & NodeFlags.System) != 0)
        {
            return new SafetyAssessment(ProtectionLevel.Caution, "System folder", "This folder is marked as a system folder.");
        }

        return SafetyAssessment.Ordinary;
    }

    public SafetyAssessment AssessFile(string path, FileAttributes attributes = 0)
    {
        path = PathUtil.NormalizeDisplayPath(path);
        string name = PathUtil.GetName(path);
        string? parent = PathUtil.GetParent(path);

        if (parent is { Length: 3 } && SystemRootFiles.TryGetValue(name, out var info))
        {
            return Protected(info.Label, info.Explanation + " Managed by Windows.", info.Advice) with { IsSystemManaged = true };
        }

        if (parent is not null)
        {
            var parentAssessment = AssessDirectory(parent);
            if (parentAssessment.IsSystemManaged || PathUtil.IsSameOrUnder(path, _known.WindowsDirectory))
            {
                // Individual files in Windows-managed areas: allowed only with a prominent warning.
                return new SafetyAssessment(ProtectionLevel.Caution, "Managed by Windows",
                    $"This file is inside a Windows-managed area ({parentAssessment.Label}). Deleting it can break Windows or installed programs.",
                    parentAssessment.Advice) { IsSystemManaged = true };
            }

            if (PathUtil.IsStrictlyUnder(path, _known.ProgramFiles) || PathUtil.IsStrictlyUnder(path, _known.ProgramFilesX86))
            {
                return new SafetyAssessment(ProtectionLevel.Caution, "Application file",
                    "This file belongs to an installed program. Deleting it can break the program; uninstall the program instead.");
            }

            if (parentAssessment.Level == ProtectionLevel.Caution)
            {
                return parentAssessment;
            }

            if (PathUtil.IsSameOrUnder(parent, _known.TempDirectory))
            {
                return new SafetyAssessment(ProtectionLevel.Caution, "Temporary",
                    "Temporary files. Programs that are running may still be using some of them.",
                    "Settings > System > Storage > Temporary files can remove these safely.");
            }

            if (IsSettingsFolder(parent))
            {
                return new SafetyAssessment(ProtectionLevel.Caution, "Settings or program data",
                    $"This file is directly inside {PathUtil.GetName(parent)}, where Windows and programs keep settings and data. Deleting it can reset or break a program.");
            }
        }

        if ((attributes & FileAttributes.System) != 0)
        {
            return new SafetyAssessment(ProtectionLevel.Caution, "System file", "This file is marked as a system file.") { IsSystemManaged = true };
        }

        return SafetyAssessment.Ordinary;
    }

    /// <summary>
    /// Rules for profiles other than the current user's: other accounts, Public, and the Default template.
    /// Returns null for paths outside those profiles, or for ordinary content inside them.
    /// </summary>
    private SafetyAssessment? AssessOtherProfile(string path)
    {
        if (!PathUtil.IsStrictlyUnder(path, _known.UsersDirectory) || PathUtil.IsSameOrUnder(path, _known.UserProfile))
        {
            return null;
        }

        string users = PathUtil.NormalizeDisplayPath(_known.UsersDirectory);
        string[] segments = path[(users.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        string profile = segments[0];
        if (SystemProfileNames.Contains(profile))
        {
            return Protected("Default user profile", "Template Windows copies when a new user account is created.") with { IsSystemManaged = true };
        }

        bool isPublic = profile.Equals("Public", StringComparison.OrdinalIgnoreCase);
        if (segments.Length == 1)
        {
            return isPublic
                ? Protected("Public folder", "Shared folder that Windows provides for all users of this PC. Its contents can be reviewed individually.")
                : Protected("User profile", "The profile of another user account on this PC. Its contents can be reviewed individually.",
                    "To remove an account and its files, use Settings > Accounts > Other users.");
        }

        if (segments.Length == 2 && (isPublic || ProfileFolderNames.Contains(segments[1])))
        {
            return Protected("Essential folder", "This folder is part of a user profile. Its contents can be reviewed individually.");
        }

        if (segments[1].Equals("AppData", StringComparison.OrdinalIgnoreCase))
        {
            return new SafetyAssessment(ProtectionLevel.Caution, "Application data",
                "Settings, caches or data of an application used by another account. Removing it may reset the application or lose data.");
        }

        return null;
    }

    private bool IsSettingsFolder(string folder)
    {
        folder = PathUtil.NormalizeDisplayPath(folder);
        foreach (var settings in _settingsFolders)
        {
            if (folder.Equals(settings, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // The root or the AppData folder of another profile.
        if (PathUtil.IsStrictlyUnder(folder, _known.UsersDirectory))
        {
            string users = PathUtil.NormalizeDisplayPath(_known.UsersDirectory);
            string[] segments = folder[(users.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 1 && !segments[0].Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                   segments.Length == 2 && segments[1].Equals("AppData", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static SafetyAssessment Protected(string label, string explanation, string? advice = null) =>
        new(ProtectionLevel.Protected, label, explanation, advice);
}
