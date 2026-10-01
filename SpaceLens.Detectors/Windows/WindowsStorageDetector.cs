using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;
using SpaceLens.Windows.Shell;

namespace SpaceLens.Detectors.WindowsStorage;

/// <summary>
/// Windows-managed and well-known locations: component store, update cache, installer cache, temporary
/// files, Recycle Bin, page/hibernation files, crash dumps, previous Windows installations, Downloads,
/// browser caches and application caches.
/// </summary>
public sealed class WindowsStorageDetector : DetectorBase
{
    public const string RecycleBinGroup = "Recycle Bin";
    private const string StorageSettings = "ms-settings:storagesense";
    private const string StorageSettingsLabel = "Open Storage settings";

    private static readonly HashSet<string> CacheFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Caches", "cache2", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DXCache", "GLCache",
        "D3DSCache", "CachedData", "Service Worker", "INetCache", "CrashDumps", "Temp",
    };

    public override string Id => "windows";

    public override string DisplayName => "Windows storage";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var findings = new List<StorageFinding>();
        var k = context.Known;
        var tree = context.Tree;

        void Add(StorageFinding? f)
        {
            if (f is not null)
            {
                findings.Add(f);
            }
        }

        string windows = k.WindowsDirectory;
        Add(DirectoryFinding(context, Find(context, Combine(windows, "WinSxS")), "Windows", "Windows component store (WinSxS)", StorageNature.SystemFile, LocationCategory.Windows,
            "Windows components and updates. Many files are hard links shared with System32, so the size shown overstates the real usage. Managed by Windows; manual deletion is not recommended.",
            "Run Disk Cleanup > \"Clean up system files\", or \"Dism /Online /Cleanup-Image /StartComponentCleanup\" as administrator.", allowRemoval: false));
        Add(DirectoryFinding(context, Find(context, Combine(windows, "SoftwareDistribution", "Download")), "Windows Update cache", "Windows Update downloads", StorageNature.Temporary, LocationCategory.Windows,
            "Updates downloaded by Windows Update. Managed by Windows.",
            "Settings > System > Storage > Temporary files > \"Windows Update Cleanup\".", isOpportunity: true, allowRemoval: false,
            actionUri: StorageSettings, actionLabel: StorageSettingsLabel, minimumSize: 100L << 20));
        Add(DirectoryFinding(context, Find(context, Combine(windows, "Installer")), "Windows", "Windows Installer cache", StorageNature.SystemFile, LocationCategory.Windows,
            "Files Windows needs to repair, update and uninstall installed programs. Never delete these manually.",
            "Uninstalling programs you no longer need reduces this cache.", allowRemoval: false));
        Add(DirectoryFinding(context, Find(context, Combine(windows, "Temp")), "Temporary files", "Windows temporary files", StorageNature.Temporary, LocationCategory.TemporaryAndCache,
            "Temporary files created by Windows and installers.", "Settings > System > Storage > Temporary files, or turn on Storage Sense.",
            isOpportunity: true, allowRemoval: false, actionUri: StorageSettings, actionLabel: StorageSettingsLabel, minimumSize: 100L << 20));
        Add(DirectoryFinding(context, Find(context, k.TempDirectory), "Temporary files", "Your temporary files", StorageNature.Temporary, LocationCategory.TemporaryAndCache,
            "Temporary files created by applications. Programs that are running may still be using some of them.",
            "Settings > System > Storage > Temporary files, or turn on Storage Sense.", isOpportunity: true, allowRemoval: false,
            actionUri: StorageSettings, actionLabel: StorageSettingsLabel, minimumSize: 100L << 20));
        Add(DirectoryFinding(context, Find(context, Combine(windows, "Minidump")), "Crash dumps", "Windows crash dumps", StorageNature.Temporary, LocationCategory.Windows,
            "Diagnostic memory dumps written after system crashes.", "Settings > System > Storage > Temporary files.", allowRemoval: false,
            actionUri: StorageSettings, actionLabel: StorageSettingsLabel, minimumSize: 10L << 20));
        Add(DirectoryFinding(context, Find(context, Combine(k.LocalAppData, "CrashDumps")), "Crash dumps", "Application crash dumps", StorageNature.Temporary, LocationCategory.TemporaryAndCache,
            "Memory dumps written when applications crashed. Useful only for diagnosing those crashes.", minimumSize: 10L << 20));
        Add(DirectoryFinding(context, Find(context, Combine(k.ProgramData, "Microsoft", "Windows", "WER")), "Crash dumps", "Windows Error Reporting", StorageNature.Temporary, LocationCategory.Windows,
            "Error reports queued by Windows Error Reporting.", "Settings > System > Storage > Temporary files.", allowRemoval: false,
            actionUri: StorageSettings, actionLabel: StorageSettingsLabel, minimumSize: 10L << 20));

        if (tree.RootPath.Length == 3)
        {
            Add(DirectoryFinding(context, tree.FindChild(ScanTree.RootIndex, "Windows.old"), "Previous Windows installation", "Windows.old", StorageNature.SystemFile, LocationCategory.Windows,
                "Files from the Windows version you upgraded from, kept so you can go back.",
                "Settings > System > Storage > Temporary files > \"Previous Windows installation(s)\".", isOpportunity: true, allowRemoval: false,
                actionUri: StorageSettings, actionLabel: StorageSettingsLabel));
            Add(DirectoryFinding(context, tree.FindChild(ScanTree.RootIndex, "$WINDOWS.~BT"), "Previous Windows installation", "Windows upgrade files", StorageNature.Temporary, LocationCategory.Windows,
                "Temporary files from a Windows upgrade.", "Settings > System > Storage > Temporary files.", allowRemoval: false,
                actionUri: StorageSettings, actionLabel: StorageSettingsLabel));

            AddRootSystemFiles(context, findings);
            AddRecycleBin(context, findings);
        }

        int windowsIndex = Find(context, windows);
        if (windowsIndex >= 0)
        {
            foreach (int f in tree.GetFiles(windowsIndex))
            {
                if (tree.File(f).Name.Equals("MEMORY.DMP", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(FileFinding(context, f, "Crash dumps", "System memory dump", StorageNature.Temporary, LocationCategory.Windows,
                        "Full memory dump from the last system crash.", "Settings > System > Storage > Temporary files > \"System error memory dump files\".", allowRemoval: false));
                }
            }
        }

        Add(DirectoryFinding(context, Find(context, Combine(k.UserProfile, "Downloads")), "Downloads", "Downloads", StorageNature.UserFile, LocationCategory.Downloads,
            "Files you downloaded. Review the largest ones; installers and archives are often no longer needed.", isOpportunity: true, isUnit: true));

        var claimedCaches = new HashSet<int>();
        AddBrowserCaches(context, findings, claimedCaches);
        AddApplicationCaches(context, findings, claimedCaches, cancellationToken);
        return findings;
    }

    private void AddRootSystemFiles(DetectionContext context, List<StorageFinding> findings)
    {
        var tree = context.Tree;
        foreach (int f in tree.GetFiles(ScanTree.RootIndex))
        {
            string name = tree.File(f).Name;
            (string Title, string Explanation, string? Advice, string? Uri)? info = name.ToLowerInvariant() switch
            {
                "pagefile.sys" => ("Page file (pagefile.sys)", "Virtual memory used by Windows when RAM is full. Managed by Windows.",
                    "Its size can be changed in System > About > Advanced system settings > Performance > Advanced > Virtual memory.", null),
                "hiberfil.sys" => ("Hibernation file (hiberfil.sys)", "Stores memory contents for hibernation and Fast Startup. Managed by Windows.",
                    "Run \"powercfg /h /type reduced\" to shrink it, or \"powercfg /hibernate off\" (as administrator) to remove it and disable hibernation.", null),
                "swapfile.sys" => ("Swap file (swapfile.sys)", "Used by Windows to suspend Store apps. Managed by Windows.", null, null),
                _ => null,
            };

            if (info is { } i)
            {
                findings.Add(FileFinding(context, f, "Windows", i.Title, StorageNature.SystemFile, LocationCategory.Windows, i.Explanation, i.Advice, allowRemoval: false));
            }
        }
    }

    private void AddRecycleBin(DetectionContext context, List<StorageFinding> findings)
    {
        var (bytes, items) = ShellActions.QueryRecycleBin(context.Tree.RootPath);
        int index = context.Tree.FindChild(ScanTree.RootIndex, "$Recycle.Bin");
        if (bytes <= 0 && index < 0)
        {
            return;
        }

        findings.Add(new StorageFinding
        {
            DetectorId = Id,
            Group = RecycleBinGroup,
            Title = $"Recycle Bin ({items:N0} items)",
            Path = PathUtil.Combine(context.Tree.RootPath, "$Recycle.Bin"),
            Size = Math.Max(bytes, index >= 0 ? context.Tree.Dir(index).TotalSize : 0),
            DirectoryIndex = index,
            Nature = StorageNature.UserFile,
            Category = LocationCategory.RecycleBin,
            Explanation = "Items you deleted to the Recycle Bin on this drive. They still use space until the Recycle Bin is emptied.",
            Advice = "Review it in File Explorer, or empty it.",
            IsOpportunity = bytes > 0,
            AllowDirectRemoval = false,
            ActionUri = "spacelens:emptyrecyclebin",
            ActionLabel = "Empty Recycle Bin",
        });
    }

    private void AddBrowserCaches(DetectionContext context, List<StorageFinding> findings, HashSet<int> claimed)
    {
        var tree = context.Tree;
        string local = context.Known.LocalAppData;
        (string Path, string Browser)[] chromiumRoots =
        [
            (Combine(local, "Google", "Chrome", "User Data"), "Chrome"),
            (Combine(local, "Microsoft", "Edge", "User Data"), "Edge"),
            (Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), "Brave"),
            (Combine(local, "Vivaldi", "User Data"), "Vivaldi"),
            (Combine(context.Known.RoamingAppData, "Opera Software", "Opera Stable"), "Opera"),
        ];

        foreach (var (root, browser) in chromiumRoots)
        {
            int rootIndex = Find(context, root);
            if (rootIndex < 0)
            {
                continue;
            }

            // Profiles: "Default", "Profile 1", ... (Opera keeps the cache at the root).
            var profiles = tree.GetChildren(rootIndex).Where(p => tree.FindChild(p, "Cache") >= 0 || tree.FindChild(p, "Code Cache") >= 0).ToList();
            profiles.Add(rootIndex);
            foreach (int profile in profiles)
            {
                foreach (var name in new[] { "Cache", "Code Cache", "GPUCache", "Service Worker" })
                {
                    int cache = tree.FindChild(profile, name);
                    var f = DirectoryFinding(context, cache, "Browser cache", $"{browser} {name} ({tree.Dir(profile).Name})", StorageNature.Cache, LocationCategory.TemporaryAndCache,
                        $"Web content cached by {browser} to load pages faster. Rebuilt automatically as you browse.",
                        $"Clear it from {browser}'s settings (Privacy > Clear browsing data > Cached images and files), with the browser closed if removing directly.",
                        isOpportunity: true, minimumSize: 20L << 20);
                    if (f is not null && claimed.Add(cache))
                    {
                        findings.Add(f);
                    }
                }
            }
        }

        int firefoxProfiles = Find(context, Combine(local, "Mozilla", "Firefox", "Profiles"));
        if (firefoxProfiles >= 0)
        {
            foreach (int profile in tree.GetChildren(firefoxProfiles))
            {
                int cache = tree.FindChild(profile, "cache2");
                var f = DirectoryFinding(context, cache, "Browser cache", $"Firefox cache ({tree.Dir(profile).Name})", StorageNature.Cache, LocationCategory.TemporaryAndCache,
                    "Web content cached by Firefox. Rebuilt automatically as you browse.",
                    "Clear it from Firefox: Settings > Privacy & Security > Cookies and Site Data > Clear Data.", isOpportunity: true, minimumSize: 20L << 20);
                if (f is not null && claimed.Add(cache))
                {
                    findings.Add(f);
                }
            }
        }
    }

    private void AddApplicationCaches(DetectionContext context, List<StorageFinding> findings, HashSet<int> claimed, CancellationToken cancellationToken)
    {
        var tree = context.Tree;
        foreach (var root in new[] { context.Known.LocalAppData, context.Known.RoamingAppData, Path.Combine(context.Known.UserProfile, "AppData", "LocalLow") })
        {
            int rootIndex = Find(context, root);
            if (rootIndex < 0)
            {
                continue;
            }

            int tempIndex = Find(context, context.Known.TempDirectory);
            TreeWalker.Walk(tree, rootIndex, d =>
            {
                if (claimed.Contains(d) || d == tempIndex)
                {
                    return false;
                }

                if (!CacheFolderNames.Contains(tree.Dir(d).Name))
                {
                    return true;
                }

                string owner = DescribeOwner(tree, d, rootIndex);
                var f = DirectoryFinding(context, d, "Application cache", $"{owner} – {tree.Dir(d).Name}", StorageNature.Cache, LocationCategory.TemporaryAndCache,
                    Explain(tree.Dir(d).Name, owner),
                    "Close the application before removing its cache. Some applications offer a \"clear cache\" option in their settings.",
                    isOpportunity: true, minimumSize: 50L << 20);
                if (f is not null)
                {
                    claimed.Add(d);
                    findings.Add(f);
                }

                return false;
            }, cancellationToken);
        }
    }

    private static string Explain(string folder, string owner) => folder.ToLowerInvariant() switch
    {
        "dxcache" or "glcache" or "shadercache" or "d3dscache" or "grshadercache" =>
            $"Shader cache ({owner}). Compiled graphics shaders that are rebuilt automatically; games may stutter briefly while shaders recompile. Disk Cleanup's \"DirectX Shader Cache\" option also clears it.",
        "crashdumps" => $"Crash dumps written by {owner}. Only useful for diagnosing crashes.",
        "temp" => $"Temporary files of {owner}.",
        _ => $"Cached data of {owner}. Normally rebuilt automatically, but the application may be slower or need to download data again.",
    };

    private static string DescribeOwner(ScanTree tree, int dir, int appDataRoot)
    {
        // The first folder below Local/Roaming identifies the vendor or application.
        int owner = dir;
        while (tree.Dir(owner).Parent >= 0 && tree.Dir(owner).Parent != appDataRoot)
        {
            owner = tree.Dir(owner).Parent;
        }

        string name = tree.Dir(owner).Name;
        if (name is "Microsoft" or "Google" or "Packages" && owner != dir)
        {
            // Use the next level for umbrella vendor folders.
            int next = dir;
            while (tree.Dir(next).Parent != owner && tree.Dir(next).Parent >= 0)
            {
                next = tree.Dir(next).Parent;
            }

            if (next != dir)
            {
                name += " " + tree.Dir(next).Name;
            }
        }

        return name;
    }
}
