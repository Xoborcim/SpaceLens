using SpaceLens.Core.Models;

namespace SpaceLens.Core.Safety;

/// <summary>Well-known macOS locations used by <see cref="MacSafetyPolicy"/>. Injectable for tests.</summary>
public sealed class MacKnownLocations
{
    /// <summary>The user's home folder, e.g. /Users/me.</summary>
    public required string Home { get; init; }

    public string UsersDirectory { get; init; } = "/Users";

    public static MacKnownLocations FromEnvironment() =>
        new() { Home = PathUtil.NormalizeDisplayPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) };
}

/// <summary>
/// The macOS counterpart of <see cref="SafetyPolicy"/>. The operating system (/System, /usr, /bin, /sbin,
/// /private), volume-level system data, application bundles and the folders that make up a home folder are
/// protected; shared and per-user application data (/Library, ~/Library) and package-manager trees need a
/// warning. Applications are removed from the Apps page, which moves the bundle to the Trash and reports
/// what the app left behind.
/// </summary>
public sealed class MacSafetyPolicy : IItemSafetyPolicy
{
    private readonly MacKnownLocations _known;

    /// <summary>Operating-system trees (sealed system volume, BSD layer, firmlinked system data).</summary>
    private static readonly string[] SystemRoots = ["/System", "/usr", "/bin", "/sbin", "/private", "/etc", "/var", "/tmp", "/dev", "/cores", "/Network"];

    /// <summary>Managed by macOS at the root of every volume.</summary>
    private static readonly Dictionary<string, (string Label, string Explanation, string? Advice)> VolumeSystemFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".Spotlight-V100"] = ("Spotlight index", "The search index Spotlight keeps for this volume.", "Spotlight rebuilds it; exclude folders in System Settings > Siri & Spotlight > Spotlight Privacy."),
            [".fseventsd"] = ("File system events", "The log macOS keeps of changes on this volume.", null),
            [".DocumentRevisions-V100"] = ("Document versions", "Earlier versions of documents saved by apps (File > Revert To).", null),
            [".Trashes"] = ("Trash", "Items moved to the Trash from this volume.", "Empty the Trash to free this space."),
            [".TemporaryItems"] = ("Temporary items", "Temporary files macOS keeps for this volume.", null),
            [".MobileBackups"] = ("Local snapshots", "Local Time Machine data.", "Time Machine manages it."),
            ["Backups.backupdb"] = ("Time Machine backups", "Backups made by Time Machine.", "Delete backups from Time Machine, not in the file system."),
            [".vol"] = ("System folder", "Used by macOS.", null),
        };

    /// <summary>Folders macOS creates in every home folder.</summary>
    private static readonly HashSet<string> HomeFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Desktop", "Documents", "Downloads", "Movies", "Music", "Pictures", "Public", "Library", "Applications", "Sites",
    };

    public MacSafetyPolicy(MacKnownLocations known) => _known = known;

    public MacKnownLocations Known => _known;

    public SafetyAssessment AssessDirectory(string path, NodeFlags flags = NodeFlags.None)
    {
        path = PathUtil.NormalizeDisplayPath(path);
        string name = PathUtil.GetName(path);
        string? parent = PathUtil.GetParent(path);

        if (path == "/" || parent == "/Volumes")
        {
            return Protected("Volume", "The top level of a disk cannot be removed.");
        }

        if (VolumeRootOf(path) is { } volumeRoot && SystemFolderBelow(path, volumeRoot) is { } systemFolder)
        {
            return systemFolder;
        }

        foreach (var root in SystemRoots)
        {
            if (PathUtil.IsSameOrUnder(path, root))
            {
                return PathUtil.IsSameOrUnder(path, "/private/var/vm")
                    ? Protected("Virtual memory", "Swap files and the sleep image used by macOS.") with { IsSystemManaged = true }
                    : PathUtil.IsStrictlyUnder(path, "/usr/local")
                        ? PackageManager(path)
                        : Protected("Part of macOS", "Files of the operating system. macOS protects them and manages them itself.",
                            "To reclaim space used by macOS, see System Settings > General > Storage.") with { IsSystemManaged = true };
            }
        }

        if (PathUtil.IsSameOrUnder(path, "/opt/homebrew") || PathUtil.IsSameOrUnder(path, "/opt/local"))
        {
            return path is "/opt/homebrew" or "/opt/local" ? Protected("Package manager", "Software installed by a package manager such as Homebrew.") : PackageManager(path);
        }

        if (IsInsideBundle(path))
        {
            return Protected("Part of an application", "Files inside an application bundle. Changing them breaks the application and its signature.",
                "Remove the whole application from the Apps page.");
        }

        if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && parent is not null && IsApplicationsFolder(parent))
        {
            return Protected("Application", "Applications are removed from the Apps page, which moves them to the Trash and shows what they left behind.",
                "Open the Apps page to remove it.");
        }

        if (path is "/Applications" or "/Library" or "/Users" or "/opt" || path == "/Users/Shared" || IsApplicationsFolder(path))
        {
            return Protected("Essential folder", "This folder is part of macOS. Its contents can be reviewed individually.");
        }

        if (PathUtil.IsStrictlyUnder(path, "/Library"))
        {
            return new SafetyAssessment(ProtectionLevel.Caution, "Shared application data",
                "Data, settings and support files of applications for all users. Applications may stop working if it is removed.");
        }

        if (AssessHome(path, name) is { } home)
        {
            return home;
        }

        if (PathUtil.IsStrictlyUnder(path, _known.UsersDirectory) && OtherUser(path) is { } other)
        {
            return other;
        }

        return SafetyAssessment.Ordinary;
    }

    public SafetyAssessment AssessFile(string path, FileAttributes attributes = 0)
    {
        path = PathUtil.NormalizeDisplayPath(path);
        string? parent = PathUtil.GetParent(path);
        if (parent is null)
        {
            return SafetyAssessment.Ordinary;
        }

        var parentAssessment = AssessDirectory(parent);
        if (parentAssessment.IsSystemManaged || IsInsideBundle(path))
        {
            return Protected(parentAssessment.Label, parentAssessment.Explanation ?? "Managed by macOS.", parentAssessment.Advice) with { IsSystemManaged = parentAssessment.IsSystemManaged };
        }

        if (parentAssessment.Level == ProtectionLevel.Caution)
        {
            return parentAssessment;
        }

        // Loose files in folders that hold settings rather than documents.
        if (parent == _known.Home || parent == "/Library" || parent == PathUtil.Combine(_known.Home, "Library") || parent == "/")
        {
            return new SafetyAssessment(ProtectionLevel.Caution, "Settings or system file",
                $"This file is directly inside {PathUtil.GetName(parent)}, where macOS and applications keep settings. Deleting it can reset or break something.");
        }

        return SafetyAssessment.Ordinary;
    }

    private SafetyAssessment? AssessHome(string path, string name)
    {
        string home = _known.Home;
        if (path.Equals(home, StringComparison.OrdinalIgnoreCase))
        {
            return Protected("Home folder", "Your home folder. Its contents can be reviewed individually.");
        }

        if (!PathUtil.IsStrictlyUnder(path, home))
        {
            return null;
        }

        string[] segments = path[(home.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        string first = segments[0];
        if (segments.Length == 1 && HomeFolders.Contains(first))
        {
            return Protected("Essential folder", "This folder is part of your home folder. Its contents can be reviewed individually.");
        }

        if (first.Equals(".Trash", StringComparison.OrdinalIgnoreCase))
        {
            return Protected("Trash", "Items you moved to the Trash.", "Empty the Trash to free this space.") with { IsSystemManaged = true };
        }

        if (first.Equals("Library", StringComparison.OrdinalIgnoreCase))
        {
            string second = segments.Length > 1 ? segments[1] : "";
            if (second.Equals("Mobile Documents", StringComparison.OrdinalIgnoreCase))
            {
                return new SafetyAssessment(ProtectionLevel.Caution, "iCloud Drive",
                    "Files synced with iCloud Drive. Deleting them here deletes them from iCloud and your other devices.",
                    "To keep them in iCloud but free space on this Mac, use Remove Download in Finder.");
            }

            return second.Equals("Caches", StringComparison.OrdinalIgnoreCase)
                ? new SafetyAssessment(ProtectionLevel.Caution, "Cache", "Data applications keep to work faster. They usually recreate it, but quit an application before removing its cache.")
                : new SafetyAssessment(ProtectionLevel.Caution, "Application data", "Settings, caches or data of an application. Removing it may reset the application or lose data.");
        }

        if (segments.Length == 1 && first.StartsWith('.'))
        {
            return new SafetyAssessment(ProtectionLevel.Caution, "Settings", "A hidden folder where a program keeps its settings or data (for example .ssh holds your SSH keys).");
        }

        return null;
    }

    /// <summary>Another account's home folder (or the Shared folder): protected at the top, like your own.</summary>
    private SafetyAssessment? OtherUser(string path)
    {
        string[] segments = path[(_known.UsersDirectory.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0].Equals("Shared", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (segments.Length == 1)
        {
            return Protected("User folder", "The home folder of another account on this Mac. Its contents can be reviewed individually.",
                "To remove an account and its files, use System Settings > Users & Groups.");
        }

        if (segments.Length == 2 && HomeFolders.Contains(segments[1]))
        {
            return Protected("Essential folder", "This folder is part of a home folder. Its contents can be reviewed individually.");
        }

        return segments[1].Equals("Library", StringComparison.OrdinalIgnoreCase)
            ? new SafetyAssessment(ProtectionLevel.Caution, "Application data", "Settings, caches or data of an application used by another account.")
            : null;
    }

    private static SafetyAssessment PackageManager(string path) =>
        new(ProtectionLevel.Caution, "Package manager", "Software installed by a package manager such as Homebrew or MacPorts. Deleting it by hand leaves the package manager confused.",
            "Remove packages with the package manager, for example \"brew uninstall\" or \"brew cleanup\".");

    /// <summary>"/" for the startup volume, "/Volumes/Name" for other volumes.</summary>
    private static string? VolumeRootOf(string path)
    {
        if (PathUtil.IsStrictlyUnder(path, "/Volumes"))
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return "/Volumes/" + segments[1];
        }

        return "/";
    }

    private static SafetyAssessment? SystemFolderBelow(string path, string volumeRoot)
    {
        string relative = volumeRoot == "/" ? path[1..] : path.Length > volumeRoot.Length ? path[(volumeRoot.Length + 1)..] : "";
        string first = relative.Split('/', 2)[0];
        return first.Length > 0 && VolumeSystemFolders.TryGetValue(first, out var info)
            ? Protected(info.Label, info.Explanation, info.Advice) with { IsSystemManaged = true }
            : null;
    }

    private bool IsApplicationsFolder(string path) =>
        path.Equals("/Applications", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(PathUtil.Combine(_known.Home, "Applications"), StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/Applications/Utilities", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for anything inside an application (or other) bundle: a parent folder ends in .app, .framework, .bundle...</summary>
    private static bool IsInsideBundle(string path)
    {
        for (string? p = PathUtil.GetParent(path); p is not null && p != "/"; p = PathUtil.GetParent(p))
        {
            string name = PathUtil.GetName(p);
            if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".framework", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".plugin", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".kext", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".appex", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static SafetyAssessment Protected(string label, string explanation, string? advice = null) =>
        new(ProtectionLevel.Protected, label, explanation, advice);
}
