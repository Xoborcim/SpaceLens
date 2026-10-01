using System.Text.RegularExpressions;
using Microsoft.Win32;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;

namespace SpaceLens.Detectors.Steam;

/// <summary>
/// Finds Steam libraries and the games inside them. Library locations come from Steam's
/// <c>libraryfolders.vdf</c>; game names and app IDs from <c>appmanifest_*.acf</c>. Libraries that are
/// not registered (e.g. on a drive Steam no longer knows) are found by their <c>steamapps\common</c>
/// structure. Uninstalling is delegated to Steam via <c>steam://uninstall/&lt;appid&gt;</c>.
/// </summary>
public sealed partial class SteamDetector : DetectorBase
{
    private const string Explanation = "Steam game. Uninstalling it through Steam keeps your library consistent and frees this space.";

    [GeneratedRegex("\"path\"\\s+\"(?<p>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathPattern();

    [GeneratedRegex("\"(?<k>appid|name|installdir)\"\\s+\"(?<v>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestPattern();

    public override string Id => "steam";

    public override string DisplayName => "Steam";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? steamPath = GetSteamPath();
        if (steamPath is not null)
        {
            libraries.Add(steamPath);
            foreach (var lib in ReadLibraryFolders(Path.Combine(steamPath, "steamapps", "libraryfolders.vdf")))
            {
                libraries.Add(lib);
            }
        }

        // Unregistered libraries: any "steamapps\common" within the first few levels of the scan.
        var tree = context.Tree;
        TreeWalker.Walk(tree, ScanTree.RootIndex, d =>
        {
            if (tree.Dir(d).Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) && tree.FindChild(d, "common") >= 0)
            {
                libraries.Add(PathUtil.GetParent(tree.GetPath(d))!);
                return false;
            }

            return tree.GetDepth(d) < 4;
        }, cancellationToken);

        foreach (var library in libraries)
        {
            int steamapps = Find(context, Path.Combine(library, "steamapps"));
            if (steamapps < 0)
            {
                continue;
            }

            var manifests = ReadManifests(Path.Combine(library, "steamapps"));
            int common = tree.FindChild(steamapps, "common");
            if (common >= 0)
            {
                var libraryFinding = DirectoryFinding(context, common, "Steam", "Steam Library" + (libraries.Count > 1 ? $" ({PathUtil.GetName(library)})" : ""),
                    StorageNature.Game, LocationCategory.Games, "Games installed by Steam in this library.",
                    isOpportunity: true, allowRemoval: false, isUnit: false);
                if (libraryFinding is not null)
                {
                    yield return libraryFinding;
                }

                foreach (int game in tree.GetChildren(common))
                {
                    string folder = tree.Dir(game).Name;
                    manifests.TryGetValue(folder, out var manifest);
                    var finding = DirectoryFinding(context, game, "Steam games", manifest.Name ?? folder, StorageNature.Game, LocationCategory.Games,
                        Explanation, "Uninstall from Steam: Library > right-click the game > Manage > Uninstall.",
                        allowRemoval: false,
                        actionUri: manifest.AppId is not null ? $"steam://uninstall/{manifest.AppId}" : null,
                        actionLabel: manifest.AppId is not null ? "Uninstall in Steam" : null);
                    if (finding is not null)
                    {
                        yield return finding;
                    }
                }
            }

            var workshop = DirectoryFinding(context, Find(context, Path.Combine(library, "steamapps", "workshop")), "Steam", "Steam Workshop content",
                StorageNature.Game, LocationCategory.Games, "Mods and items you subscribed to in the Steam Workshop. Unsubscribe in Steam to remove them.",
                allowRemoval: false, minimumSize: 50L << 20);
            if (workshop is not null)
            {
                yield return workshop;
            }

            var shaders = DirectoryFinding(context, tree.FindChild(steamapps, "shadercache"), "Steam", "Steam shader cache",
                StorageNature.Cache, LocationCategory.Games, "Pre-compiled shaders downloaded by Steam. Rebuilt automatically when games run.",
                "Can be limited in Steam > Settings > Downloads > Shader Pre-Caching.", minimumSize: 50L << 20);
            if (shaders is not null)
            {
                yield return shaders;
            }

            var downloading = DirectoryFinding(context, tree.FindChild(steamapps, "downloading"), "Steam", "Steam incomplete downloads",
                StorageNature.Temporary, LocationCategory.Games, "Partially downloaded game updates. Steam removes them when the download completes or is cancelled.",
                allowRemoval: false, minimumSize: 50L << 20);
            if (downloading is not null)
            {
                yield return downloading;
            }
        }
    }

    private static string? GetSteamPath()
    {
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (user?.GetValue("SteamPath") is string path && Directory.Exists(path))
            {
                return PathUtil.NormalizeDisplayPath(path);
            }

            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (machine?.GetValue("InstallPath") is string installPath && Directory.Exists(installPath))
            {
                return PathUtil.NormalizeDisplayPath(installPath);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return null;
    }

    internal static IEnumerable<string> ReadLibraryFolders(string vdfPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(vdfPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (Match m in LibraryPathPattern().Matches(text))
        {
            yield return PathUtil.NormalizeDisplayPath(m.Groups["p"].Value.Replace(@"\\", @"\"));
        }
    }

    internal static Dictionary<string, (string? AppId, string? Name)> ReadManifests(string steamappsPath)
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(steamappsPath, "appmanifest_*.acf"))
            {
                var parsed = ParseManifest(File.ReadAllText(file));
                if (parsed.InstallDir is not null)
                {
                    result[parsed.InstallDir] = (parsed.AppId, parsed.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    internal static (string? AppId, string? Name, string? InstallDir) ParseManifest(string text)
    {
        string? appId = null, name = null, dir = null;
        foreach (Match m in ManifestPattern().Matches(text))
        {
            string value = m.Groups["v"].Value;
            switch (m.Groups["k"].Value.ToLowerInvariant())
            {
                case "appid":
                    appId ??= value;
                    break;
                case "name":
                    name ??= value;
                    break;
                case "installdir":
                    dir ??= value;
                    break;
            }
        }

        return (appId, name, dir);
    }
}
