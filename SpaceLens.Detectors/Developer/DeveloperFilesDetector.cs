using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;

namespace SpaceLens.Detectors.Developer;

/// <summary>
/// Finds project build artifacts, package caches, toolchains and developer virtual disks.
/// <para>
/// Generic folder names like "bin", "build" or "target" are only reported when a project marker file is
/// next to them (a *.csproj, Cargo.toml, package.json, ...), so ordinary folders that happen to share
/// these names are not flagged. Nested matches (node_modules inside node_modules) are folded into the
/// outermost one.
/// </para>
/// </summary>
public sealed class DeveloperFilesDetector : DetectorBase
{
    public override string Id => "developer";

    public override string DisplayName => "Developer files";

    public static IReadOnlyDictionary<string, string> GroupExplanations { get; } = new Dictionary<string, string>(ProjectArtifacts.GroupExplanations)
    {
        ["Package caches"] = "Downloaded packages shared by all projects. Packages are downloaded again when needed. Prefer the tool's own clean command (\"npm cache clean --force\", \"dotnet nuget locals all --clear\", \"pnpm store prune\", \"cargo cache\").",
        ["Toolchains & SDKs"] = "Installed compiler and SDK versions. Remove old versions with the tool's manager (for example \"rustup toolchain uninstall\" or \"elan toolchain uninstall\").",
        ["Docker"] = "Docker Desktop's virtual disk with images, containers and volumes. Use \"docker system prune\" or Docker Desktop > Troubleshoot > Clean / Purge data. Do not delete the disk file directly.",
        ["WSL"] = "Virtual disk of a WSL Linux distribution. It contains the whole Linux file system, including your files. Remove a distribution with \"wsl --unregister <name>\" only if you no longer need it.",
        ["Android emulators"] = "Android Virtual Devices. Delete unused devices from Android Studio's Device Manager.",
        ["Tool caches"] = "Caches written by developer tools and ML libraries (for example Hugging Face models). Usually downloaded again on demand.",
    };

    /// <summary>
    /// Folders whose contents are installed applications or games, on any drive. Bundled node_modules,
    /// bin or Python folders in there belong to the application and cannot be recreated by a build.
    /// </summary>
    private static readonly HashSet<string> InstalledContentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamapps", "WindowsApps", "XboxGames", "Epic Games", "GOG Games",
    };

    /// <summary>The same, but only directly at a drive root (D:\Program Files and so on).</summary>
    private static readonly HashSet<string> InstalledContentRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "ProgramData", "Windows",
    };

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var findings = new List<StorageFinding>();
        var tree = context.Tree;
        var claimed = new HashSet<int>();

        AddKnownLocations(context, findings, claimed);

        // Installed applications bundle their own node_modules, bin folders, Python environments...
        // Those belong to the application, so these areas are never searched for project artifacts.
        var excluded = new HashSet<int>();
        foreach (var path in new[]
        {
            context.Known.WindowsDirectory, context.Known.ProgramFiles, context.Known.ProgramFilesX86, context.Known.ProgramData,
            Path.Combine(context.Known.UserProfile, "AppData"),
        })
        {
            int index = Find(context, path);
            if (index > ScanTree.RootIndex)
            {
                excluded.Add(index);
            }
        }

        int profileIndex = Find(context, context.Known.UserProfile);
        bool rootIsDrive = tree.RootPath.Length == 3;

        // A scan of C:\Program Files, D:\SteamLibrary\steamapps\... or AppData itself: everything in it is installed content.
        bool rootIsInstalledContent = IsInstalledContentPath(tree.RootPath) ||
            new[] { context.Known.WindowsDirectory, context.Known.ProgramFiles, context.Known.ProgramFilesX86, context.Known.ProgramData, Path.Combine(context.Known.UserProfile, "AppData") }
                .Any(p => !string.IsNullOrEmpty(p) && PathUtil.IsSameOrUnder(tree.RootPath, p));

        bool Skip(int d)
        {
            if (rootIsInstalledContent || claimed.Contains(d) || excluded.Contains(d))
            {
                return true;
            }

            // Tool folders in the profile root (.vscode, .cursor, .dotnet, ...) hold installed tools and extensions.
            if (tree.Dir(d).Parent == profileIndex && tree.Dir(d).Name.StartsWith('.'))
            {
                return true;
            }

            string name = tree.Dir(d).Name;
            return name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || InstalledContentNames.Contains(name) ||
                rootIsDrive && tree.Dir(d).Parent == ScanTree.RootIndex && InstalledContentRootNames.Contains(name);
        }

        foreach (var artifact in ProjectArtifacts.Find(tree, context.FileExists, Skip, cancellationToken))
        {
            var finding = DirectoryFinding(context, artifact.DirectoryIndex, artifact.Group, artifact.Title, StorageNature.DeveloperArtifact,
                LocationCategory.Developer, GroupExplanations[artifact.Group], minimumSize: 1L << 20);
            if (finding is not null)
            {
                findings.Add(finding);
            }
        }

        // Developer virtual disks among indexed files.
        int count = tree.FileRecordCount;
        for (int i = 0; i < count; i++)
        {
            if (!tree.IsLiveFile(i))
            {
                continue;
            }

            string name = tree.File(i).Name;
            if (name.Equals("ext4.vhdx", StringComparison.OrdinalIgnoreCase))
            {
                string distro = DescribeWslDisk(tree.GetPath(tree.File(i).Directory));
                findings.Add(FileFinding(context, i, "WSL", $"WSL disk ({distro})", StorageNature.VirtualDisk, LocationCategory.Developer,
                    GroupExplanations["WSL"], "Compact with \"wsl --manage <name> --set-sparse true\", or unregister unused distributions.", isOpportunity: true, allowRemoval: false));
            }
            else if (name.StartsWith("docker_data", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(FileFinding(context, i, "Docker", "Docker Desktop data", StorageNature.VirtualDisk, LocationCategory.Developer,
                    GroupExplanations["Docker"], "Run \"docker system prune\" to remove unused images and containers.", isOpportunity: true, allowRemoval: false));
            }
        }

        return findings;
    }

    private void AddKnownLocations(DetectionContext context, List<StorageFinding> findings, HashSet<int> claimed)
    {
        string profile = context.Known.UserProfile;
        string local = context.Known.LocalAppData;
        string roaming = context.Known.RoamingAppData;

        (string Path, string Group, string Title, StorageNature Nature)[] locations =
        [
            (Combine(profile, ".nuget", "packages"), "Package caches", "NuGet packages", StorageNature.Cache),
            (Combine(local, "NuGet", "v3-cache"), "Package caches", "NuGet HTTP cache", StorageNature.Cache),
            (Combine(local, "npm-cache"), "Package caches", "npm cache", StorageNature.Cache),
            (Combine(roaming, "npm-cache"), "Package caches", "npm cache", StorageNature.Cache),
            (Combine(local, "pnpm", "store"), "Package caches", "pnpm store", StorageNature.Cache),
            (Combine(local, "pnpm-store"), "Package caches", "pnpm store", StorageNature.Cache),
            (Combine(local, "Yarn", "Cache"), "Package caches", "Yarn cache", StorageNature.Cache),
            (Combine(local, "Yarn", "Berry", "cache"), "Package caches", "Yarn cache", StorageNature.Cache),
            (Combine(profile, ".cargo", "registry"), "Package caches", "Cargo registry", StorageNature.Cache),
            (Combine(profile, ".cargo", "git"), "Package caches", "Cargo git checkouts", StorageNature.Cache),
            (Combine(profile, ".gradle", "caches"), "Package caches", "Gradle caches", StorageNature.Cache),
            (Combine(profile, ".m2", "repository"), "Package caches", "Maven repository", StorageNature.Cache),
            (Combine(local, "pip", "cache"), "Package caches", "pip cache", StorageNature.Cache),
            (Combine(local, "uv", "cache"), "Package caches", "uv cache", StorageNature.Cache),
            (Combine(profile, "go", "pkg", "mod"), "Package caches", "Go module cache", StorageNature.Cache),
            (Combine(local, "go-build"), "Package caches", "Go build cache", StorageNature.Cache),
            (Combine(profile, "anaconda3", "pkgs"), "Package caches", "Anaconda packages", StorageNature.Cache),
            (Combine(profile, "miniconda3", "pkgs"), "Package caches", "Miniconda packages", StorageNature.Cache),
            (Combine(profile, ".conda", "pkgs"), "Package caches", "Conda packages", StorageNature.Cache),
            (Combine(profile, ".rustup", "toolchains"), "Toolchains & SDKs", "Rust toolchains", StorageNature.ApplicationData),
            (Combine(profile, ".elan", "toolchains"), "Toolchains & SDKs", "Lean toolchains", StorageNature.ApplicationData),
            (Combine(profile, ".gradle", "wrapper", "dists"), "Toolchains & SDKs", "Gradle distributions", StorageNature.ApplicationData),
            (Combine(local, "Android", "Sdk"), "Toolchains & SDKs", "Android SDK", StorageNature.ApplicationData),
            (Combine(profile, ".android", "avd"), "Android emulators", "Android Virtual Devices", StorageNature.VirtualDisk),
            (Combine(profile, ".cache"), "Tool caches", ".cache (tool & model caches)", StorageNature.Cache),
        ];

        foreach (var (path, group, title, nature) in locations)
        {
            int index = Find(context, path);
            if (index < 0 || claimed.Contains(index) || IsInsideClaimed(context.Tree, index, claimed))
            {
                continue;
            }

            bool allowRemoval = nature == StorageNature.Cache;
            var finding = DirectoryFinding(context, index, group, title, nature, LocationCategory.Developer, GroupExplanations[group],
                allowRemoval: allowRemoval, isOpportunity: true, minimumSize: 10L << 20);
            if (finding is not null)
            {
                findings.Add(finding);
                claimed.Add(index);
            }
        }
    }

    private static bool IsInsideClaimed(ScanTree tree, int index, HashSet<int> claimed)
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

    /// <summary>True when a path lies inside a folder that holds installed applications or games, on any drive.</summary>
    private static bool IsInstalledContentPath(string path)
    {
        string[] segments = PathUtil.NormalizeDisplayPath(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        bool hasDrive = segments.Length > 0 && segments[0].Length == 2 && segments[0][1] == ':';
        if (hasDrive && segments.Length > 1 && InstalledContentRootNames.Contains(segments[1]))
        {
            return true;
        }

        return segments.Skip(hasDrive ? 1 : 0).Any(InstalledContentNames.Contains);
    }

    private static string DescribeWslDisk(string folder)
    {
        // %LOCALAPPDATA%\Packages\CanonicalGroupLimited.Ubuntu_...\LocalState or %LOCALAPPDATA%\wsl\{guid}
        foreach (var segment in folder.Split('\\'))
        {
            if (segment.Contains("Canonical", StringComparison.OrdinalIgnoreCase) || segment.Contains("Debian", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains("SUSE", StringComparison.OrdinalIgnoreCase) || segment.Contains("Kali", StringComparison.OrdinalIgnoreCase))
            {
                int underscore = segment.IndexOf('_');
                return underscore > 0 ? segment[..underscore] : segment;
            }
        }

        return PathUtil.GetName(folder);
    }
}
