using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.Mac;

/// <summary>Something an application leaves in the Library after it is removed (settings, caches, data).</summary>
public sealed record MacLeftover(string Path, string Kind, bool IsFile);

/// <summary>An application bundle found in /Applications or ~/Applications.</summary>
public sealed class MacApp
{
    public required string Name { get; init; }

    public required string BundlePath { get; init; }

    public string? BundleId { get; init; }

    public string? Version { get; init; }

    /// <summary>Installed from the Mac App Store (the bundle carries a store receipt).</summary>
    public bool IsAppStore { get; init; }

    /// <summary>Part of macOS (an Apple bundle identifier in a system-protected place): cannot be removed.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Size of the bundle on disk, once measured.</summary>
    public long? Size { get; set; }

    /// <summary>Settings, caches and data in the Library that belong to this app.</summary>
    public List<MacLeftover> Leftovers { get; set; } = [];

    public long LeftoverSize { get; set; }
}

/// <summary>
/// Finds installed applications and what they keep in the Library. Removing an application on a Mac means
/// moving its bundle to the Trash; the data it leaves behind is reported, never removed automatically.
/// </summary>
public static class MacApps
{
    public static List<MacApp> Find(MacKnownLocations known, CancellationToken cancellationToken = default)
    {
        var apps = new List<MacApp>();
        foreach (var root in new[] { "/Applications", "/Applications/Utilities", PathUtil.Combine(known.Home, "Applications") })
        {
            foreach (var bundle in EnumerateBundles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadBundle(bundle) is { } app)
                {
                    apps.Add(app);
                }
            }
        }

        return apps
            .GroupBy(a => a.BundlePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Bundles directly in a folder, and one level down in ordinary folders ("/Applications/Adobe X/X.app").</summary>
    private static IEnumerable<string> EnumerateBundles(string root)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
            else if (!Path.GetFileName(entry).Equals("Utilities", StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<string> inner;
                try
                {
                    inner = Directory.EnumerateDirectories(entry, "*.app").ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var bundle in inner)
                {
                    yield return bundle;
                }
            }
        }
    }

    /// <summary>Reads a bundle's Info.plist; null for something that is not an application bundle.</summary>
    public static MacApp? ReadBundle(string bundlePath)
    {
        string plist = Path.Combine(bundlePath, "Contents", "Info.plist");
        var info = PropertyList.ReadDictionary(plist);
        if (info is null)
        {
            return null;
        }

        string? bundleId = info.GetString("CFBundleIdentifier");
        string name = info.GetString("CFBundleDisplayName") ?? info.GetString("CFBundleName") ?? Path.GetFileNameWithoutExtension(bundlePath);
        return new MacApp
        {
            Name = name,
            BundlePath = PathUtil.NormalizeDisplayPath(bundlePath),
            BundleId = bundleId,
            Version = info.GetString("CFBundleShortVersionString") ?? info.GetString("CFBundleVersion"),
            IsAppStore = File.Exists(Path.Combine(bundlePath, "Contents", "_MASReceipt", "receipt")),
            IsSystem = IsSystemApp(bundlePath, bundleId),
        };
    }

    /// <summary>Apple's own applications are part of macOS (protected by System Integrity Protection).</summary>
    public static bool IsSystemApp(string bundlePath, string? bundleId) =>
        PathUtil.IsSameOrUnder(bundlePath, "/System") ||
        bundleId is not null && bundleId.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase) &&
        PathUtil.IsSameOrUnder(bundlePath, "/Applications");

    /// <summary>The Library locations where applications commonly keep data, for an app's bundle identifier and name.</summary>
    public static List<MacLeftover> LeftoverCandidates(string? bundleId, string name, string home)
    {
        string library = PathUtil.Combine(home, "Library");
        var candidates = new List<MacLeftover>();
        void Add(string relative, string kind, bool isFile = false) => candidates.Add(new MacLeftover(PathUtil.Combine(library, relative), kind, isFile));

        if (!string.IsNullOrWhiteSpace(bundleId))
        {
            Add($"Application Support/{bundleId}", "Application Support");
            Add($"Caches/{bundleId}", "Caches");
            Add($"Containers/{bundleId}", "Container (sandboxed data)");
            Add($"Preferences/{bundleId}.plist", "Preferences", isFile: true);
            Add($"Saved Application State/{bundleId}.savedState", "Saved window state");
            Add($"HTTPStorages/{bundleId}", "Web storage");
            Add($"WebKit/{bundleId}", "Web data");
            Add($"Logs/{bundleId}", "Logs");
        }

        if (!string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', ':']) < 0)
        {
            Add($"Application Support/{name}", "Application Support");
            Add($"Caches/{name}", "Caches");
            Add($"Logs/{name}", "Logs");
        }

        return candidates;
    }

    /// <summary>The candidates that exist on disk.</summary>
    public static List<MacLeftover> FindLeftovers(MacApp app, string home) =>
        LeftoverCandidates(app.BundleId, app.Name, home)
            .Where(l => l.IsFile ? File.Exists(l.Path) : Directory.Exists(l.Path))
            .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
