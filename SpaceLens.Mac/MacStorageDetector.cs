using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.Mac;

/// <summary>
/// Recognizes macOS storage that people ask about: Xcode and simulator data, device backups, Docker,
/// package-manager caches, libraries managed by Apple apps, games, and project build artifacts. Each finding
/// explains what the data is and how to reclaim it; data owned by an application points to that application
/// instead of offering direct removal.
/// </summary>
public static class MacStorageDetector
{
    private const string DetectorId = "macos";

    private sealed record Location(string RelativeToHome, string Group, string Title, StorageNature Nature, LocationCategory Category,
        string Explanation, string? Advice, bool AllowRemoval, bool IsOpportunity = true);

    private static readonly Location[] HomeLocations =
    [
        new("Library/Developer/Xcode/DerivedData", "Xcode", "Xcode build data (DerivedData)", StorageNature.Cache, LocationCategory.Developer,
            "Intermediate build products and indexes of Xcode projects. Xcode recreates them on the next build.", "Quit Xcode before removing it.", AllowRemoval: true),
        new("Library/Developer/Xcode/iOS DeviceSupport", "Xcode", "iOS device support files", StorageNature.Cache, LocationCategory.Developer,
            "Debug symbols copied from every iPhone or iPad version you connected. Xcode copies them again when a device needs them.", null, AllowRemoval: true),
        new("Library/Developer/Xcode/watchOS DeviceSupport", "Xcode", "watchOS device support files", StorageNature.Cache, LocationCategory.Developer,
            "Debug symbols copied from connected Apple Watch versions. Xcode copies them again when needed.", null, AllowRemoval: true),
        new("Library/Developer/Xcode/Archives", "Xcode", "Xcode archives", StorageNature.UserFile, LocationCategory.Developer,
            "Archived builds of your apps, including the debug symbols needed to read crash reports from released versions.",
            "Delete old archives from Xcode's Organizer window, keeping the ones of versions still in use.", AllowRemoval: false),
        new("Library/Developer/CoreSimulator/Devices", "Simulators", "iOS simulators", StorageNature.ApplicationData, LocationCategory.Developer,
            "Simulated devices with their installed apps and data.",
            "Run \"xcrun simctl delete unavailable\" to remove simulators of runtimes no longer installed, or delete devices in Xcode > Window > Devices and Simulators.", AllowRemoval: false),
        new("Library/Developer/CoreSimulator/Caches", "Simulators", "Simulator caches", StorageNature.Cache, LocationCategory.Developer,
            "Caches of the iOS simulators. Recreated when needed.", null, AllowRemoval: true),
        new("Library/Application Support/MobileSync/Backup", "Device backups", "iPhone and iPad backups", StorageNature.UserFile, LocationCategory.Documents,
            "Full local backups of your iPhone or iPad. They may be the only copy of messages, photos and settings on those devices.",
            "Manage backups in Finder: select the device, then Manage Backups. Delete only backups of devices you no longer need to restore.", AllowRemoval: false),
        new("Library/Caches/Homebrew", "Package caches", "Homebrew downloads", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded Homebrew packages, kept to reinstall without downloading again.", "Run \"brew cleanup --prune=all\".", AllowRemoval: true),
        new("Library/Caches/pip", "Package caches", "pip cache", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded Python packages shared by all projects. Downloaded again when needed.", "Run \"pip cache purge\".", AllowRemoval: true),
        new("Library/Caches/Yarn", "Package caches", "Yarn cache", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded JavaScript packages shared by all projects.", "Run \"yarn cache clean\".", AllowRemoval: true),
        new("Library/Caches/go-build", "Package caches", "Go build cache", StorageNature.Cache, LocationCategory.Developer,
            "Compiled Go packages, recreated on the next build.", "Run \"go clean -cache\".", AllowRemoval: true),
        new("Library/Caches/CocoaPods", "Package caches", "CocoaPods cache", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded CocoaPods dependencies.", "Run \"pod cache clean --all\".", AllowRemoval: true),
        new("Library/pnpm/store", "Package caches", "pnpm store", StorageNature.Cache, LocationCategory.Developer,
            "Packages shared by pnpm projects.", "Run \"pnpm store prune\".", AllowRemoval: false),
        new(".npm", "Package caches", "npm cache", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded JavaScript packages shared by all projects.", "Run \"npm cache clean --force\".", AllowRemoval: true),
        new(".gradle/caches", "Package caches", "Gradle caches", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded Java dependencies and build caches.", null, AllowRemoval: true),
        new(".m2/repository", "Package caches", "Maven repository", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded Java dependencies.", null, AllowRemoval: true),
        new(".cargo/registry", "Package caches", "Cargo registry", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded Rust crates.", null, AllowRemoval: true),
        new("go/pkg/mod", "Package caches", "Go module cache", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded Go modules.", "Run \"go clean -modcache\".", AllowRemoval: false),
        new(".nuget/packages", "Package caches", "NuGet packages", StorageNature.Cache, LocationCategory.Developer,
            "Downloaded .NET packages.", "Run \"dotnet nuget locals all --clear\".", AllowRemoval: true),
        new(".cache", "Tool caches", ".cache (tool & model caches)", StorageNature.Cache, LocationCategory.Developer,
            "Caches written by developer tools and machine-learning libraries (for example Hugging Face models). Usually downloaded again on demand.", null, AllowRemoval: true),
        new(".rustup/toolchains", "Toolchains & SDKs", "Rust toolchains", StorageNature.ApplicationData, LocationCategory.Developer,
            "Installed Rust compiler versions.", "Remove old ones with \"rustup toolchain uninstall\".", AllowRemoval: false),
        new("Library/Android/sdk", "Toolchains & SDKs", "Android SDK", StorageNature.ApplicationData, LocationCategory.Developer,
            "Android SDK, emulator images and build tools.", "Remove unused components in Android Studio's SDK Manager.", AllowRemoval: false),
        new(".android/avd", "Android emulators", "Android Virtual Devices", StorageNature.VirtualDisk, LocationCategory.Developer,
            "Android emulator devices.", "Delete unused devices in Android Studio's Device Manager.", AllowRemoval: false),
        new("Library/Messages/Attachments", "Apple apps", "Messages attachments", StorageNature.UserFile, LocationCategory.Documents,
            "Photos, videos and files received in Messages.",
            "In Messages > Settings, set Keep Messages to 30 days or a year, or delete large attachments from a conversation's details.", AllowRemoval: false),
        new("Library/Containers/com.apple.mail/Data/Library/Mail Downloads", "Apple apps", "Mail downloads", StorageNature.Cache, LocationCategory.TemporaryAndCache,
            "Attachments you opened from Mail.", null, AllowRemoval: true),
        new("Library/Application Support/Steam/steamapps/common", "Games", "Steam games", StorageNature.Game, LocationCategory.Games,
            "Games installed by Steam.", "Uninstall games from your Steam library (right-click > Manage > Uninstall).", AllowRemoval: false, IsOpportunity: false),
        new(".Trash", "Trash", "Trash", StorageNature.Temporary, LocationCategory.RecycleBin,
            "Items you moved to the Trash. They still use space until the Trash is emptied.", "Empty the Trash in Finder (Finder > Empty Trash).", AllowRemoval: false),
    ];

    /// <summary>Libraries managed by an Apple app (found anywhere in the scan by their bundle extension).</summary>
    private static readonly (string Extension, string Title, string Explanation, string Advice)[] ManagedLibraries =
    [
        (".photoslibrary", "Photos library", "Your photos and videos, managed by the Photos app.",
            "To free space, turn on iCloud Photos with Optimize Mac Storage (Photos > Settings > iCloud), or delete items in Photos and empty Recently Deleted."),
        (".musiclibrary", "Music library", "The library database of the Music app.", "Manage it in the Music app."),
        (".tvlibrary", "TV library", "The library database of the TV app.", "Manage it in the TV app."),
        (".fcpbundle", "Final Cut Pro library", "A Final Cut Pro library with media and render files.",
            "In Final Cut Pro, File > Delete Generated Library Files removes render and proxy files safely."),
    ];

    public static List<StorageFinding> Detect(ScanTree tree, MacKnownLocations known, Func<string, bool> fileExists, CancellationToken cancellationToken = default)
    {
        var findings = new List<StorageFinding>();
        var claimed = new HashSet<int>();

        foreach (var location in HomeLocations)
        {
            int index = tree.FindDirectory(PathUtil.Combine(known.Home, location.RelativeToHome));
            if (index <= ScanTree.RootIndex || !tree.IsLiveDirectory(index) || tree.Dir(index).TotalSize < 10L << 20 || IsInsideAny(tree, index, claimed))
            {
                continue;
            }

            findings.Add(Directory(tree, index, location.Group, location.Title, location.Nature, location.Category,
                location.Explanation, location.Advice, location.AllowRemoval, location.IsOpportunity));
            claimed.Add(index);
        }

        // Apple-managed libraries and project build artifacts, anywhere outside system areas and app bundles.
        int libraryIndex = tree.FindDirectory(PathUtil.Combine(known.Home, "Library"));
        var skipped = new HashSet<int>(new[] { "/System", "/usr", "/bin", "/sbin", "/private", "/Library", "/Applications", "/opt", "/Volumes" }
            .Select(tree.FindDirectory).Where(i => i > ScanTree.RootIndex));
        if (libraryIndex > ScanTree.RootIndex)
        {
            skipped.Add(libraryIndex);
        }

        int homeIndex = tree.FindDirectory(known.Home);
        bool Skip(int d)
        {
            ref var node = ref tree.Dir(d);
            if (claimed.Contains(d) || skipped.Contains(d) || (node.Flags & NodeFlags.Excluded) != 0)
            {
                return true;
            }

            // Tool folders in the home folder (.vscode, .cargo, .rustup...) hold installed tools.
            if (node.Parent == homeIndex && node.Name.StartsWith('.'))
            {
                return true;
            }

            foreach (var (extension, title, explanation, advice) in ManagedLibraries)
            {
                if (node.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    if (node.TotalSize >= 10L << 20)
                    {
                        findings.Add(Directory(tree, d, "Apple apps", $"{title} ({node.Name})", StorageNature.UserFile, ClassifyLibrary(extension),
                            explanation, advice, allowRemoval: false, isOpportunity: true));
                    }

                    return true;
                }
            }

            // Application bundles contain their own frameworks, node_modules and Python environments.
            return IsBundle(node.Name);
        }

        foreach (var artifact in ProjectArtifacts.Find(tree, fileExists, Skip, cancellationToken))
        {
            if (tree.Dir(artifact.DirectoryIndex).TotalSize >= 1L << 20)
            {
                findings.Add(Directory(tree, artifact.DirectoryIndex, artifact.Group, artifact.Title ?? tree.Dir(artifact.DirectoryIndex).Name,
                    StorageNature.DeveloperArtifact, LocationCategory.Developer, ProjectArtifacts.GroupExplanations[artifact.Group], null,
                    allowRemoval: true, isOpportunity: false, isUnit: true));
            }
        }

        // Virtual disks among indexed files.
        int count = tree.FileRecordCount;
        for (int i = 0; i < count; i++)
        {
            if (!tree.IsLiveFile(i))
            {
                continue;
            }

            ref var file = ref tree.File(i);
            if (file.Name.Equals("Docker.raw", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(File(tree, i, "Docker", "Docker Desktop disk", StorageNature.VirtualDisk, LocationCategory.Developer,
                    "Docker Desktop's virtual disk with images, containers and volumes. Do not delete it directly.",
                    "Run \"docker system prune\", or lower the disk limit in Docker Desktop > Settings > Resources."));
            }
            else if (file.Name.EndsWith(".ipsw", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(File(tree, i, "Device backups", $"Device software update ({file.Name})", StorageNature.Cache, LocationCategory.TemporaryAndCache,
                    "A downloaded iPhone or iPad software update. It is downloaded again when needed.", null, allowRemoval: true));
            }
        }

        return findings;
    }

    private static LocationCategory ClassifyLibrary(string extension) => extension switch
    {
        ".photoslibrary" => LocationCategory.Pictures,
        ".musiclibrary" => LocationCategory.Music,
        _ => LocationCategory.Videos,
    };

    private static bool IsBundle(string name) =>
        name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".framework", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xcarchive", StringComparison.OrdinalIgnoreCase);

    private static bool IsInsideAny(ScanTree tree, int index, HashSet<int> claimed)
    {
        for (int p = tree.Dir(index).Parent; p >= 0; p = tree.Dir(p).Parent)
        {
            if (claimed.Contains(p))
            {
                return true;
            }
        }

        return false;
    }

    private static StorageFinding Directory(ScanTree tree, int index, string group, string title, StorageNature nature, LocationCategory category,
        string explanation, string? advice, bool allowRemoval, bool isOpportunity, bool isUnit = true) => new()
    {
        DetectorId = DetectorId,
        Group = group,
        Title = title,
        Path = tree.GetPath(index),
        Size = tree.Dir(index).TotalSize,
        DirectoryIndex = index,
        Nature = nature,
        Category = category,
        Explanation = explanation,
        Advice = advice,
        AllowDirectRemoval = allowRemoval,
        IsOpportunity = isOpportunity,
        IsUnit = isUnit,
    };

    private static StorageFinding File(ScanTree tree, int index, string group, string title, StorageNature nature, LocationCategory category,
        string explanation, string? advice, bool allowRemoval = false) => new()
    {
        DetectorId = DetectorId,
        Group = group,
        Title = title,
        Path = tree.GetFilePath(index),
        Size = tree.File(index).Size,
        FileIndex = index,
        Nature = nature,
        Category = category,
        Explanation = explanation,
        Advice = advice,
        AllowDirectRemoval = allowRemoval,
        IsOpportunity = true,
    };
}
