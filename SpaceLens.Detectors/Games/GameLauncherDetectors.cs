using System.Text.Json;
using Microsoft.Win32;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;

namespace SpaceLens.Detectors.Games;

/// <summary>Epic Games Launcher: install manifests in <c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item</c>.</summary>
public sealed class EpicDetector : DetectorBase
{
    public override string Id => "epic";

    public override string DisplayName => "Epic Games";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        string manifests = Path.Combine(context.Known.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            yield break;
        }

        foreach (var file in SafeEnumerate(manifests, "*.item"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, location) = ParseManifest(file);
            if (location is null)
            {
                continue;
            }

            var finding = DirectoryFinding(context, Find(context, location), "Epic Games", name, StorageNature.Game, LocationCategory.Games,
                "Game installed by the Epic Games Launcher.",
                "Uninstall from the Epic Games Launcher: Library > ... > Uninstall.", isOpportunity: false, allowRemoval: false);
            if (finding is not null)
            {
                yield return finding;
            }
        }
    }

    internal static (string? Name, string? Location) ParseManifest(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string? name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
            string? location = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
            return (name, location);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    internal static IEnumerable<string> SafeEnumerate(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Xbox app / Microsoft Store PC games in <c>X:\XboxGames</c> (and the legacy ModifiableWindowsApps folder).</summary>
public sealed class XboxDetector : DetectorBase
{
    public override string Id => "xbox";

    public override string DisplayName => "Xbox";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var tree = context.Tree;
        if (tree.RootPath.Length != 3)
        {
            // Folder scans: only look for XboxGames if it is the scanned folder itself.
            int self = tree.Dir(ScanTree.RootIndex).Name.EndsWith("XboxGames", StringComparison.OrdinalIgnoreCase) ? ScanTree.RootIndex : -1;
            foreach (var f in GamesIn(context, self))
            {
                yield return f;
            }

            yield break;
        }

        foreach (var f in GamesIn(context, tree.FindChild(ScanTree.RootIndex, "XboxGames")))
        {
            yield return f;
        }

        foreach (var f in GamesIn(context, Find(context, Path.Combine(context.Known.ProgramFiles, "ModifiableWindowsApps"))))
        {
            yield return f;
        }
    }

    private IEnumerable<StorageFinding> GamesIn(DetectionContext context, int folder)
    {
        if (folder < 0)
        {
            yield break;
        }

        foreach (int game in context.Tree.GetChildren(folder))
        {
            var finding = DirectoryFinding(context, game, "Xbox games", null, StorageNature.Game, LocationCategory.Games,
                "PC game installed by the Xbox app.",
                "Uninstall from the Xbox app (right-click the game > Uninstall) or Settings > Apps > Installed apps.",
                allowRemoval: false, actionUri: "ms-settings:appsfeatures", actionLabel: "Open Installed apps");
            if (finding is not null)
            {
                yield return finding;
            }
        }
    }
}

/// <summary>GOG Galaxy / GOG installers: <c>HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\*</c>.</summary>
public sealed class GogDetector : DetectorBase
{
    public override string Id => "gog";

    public override string DisplayName => "GOG";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var games = new List<(string Name, string Path)>();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (key is not null)
            {
                foreach (var id in key.GetSubKeyNames())
                {
                    using var game = key.OpenSubKey(id);
                    if (game?.GetValue("path") is string path)
                    {
                        games.Add((game.GetValue("gameName") as string ?? PathUtil.GetName(path), path));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        foreach (var (name, path) in games)
        {
            var finding = DirectoryFinding(context, Find(context, path), "GOG games", name, StorageNature.Game, LocationCategory.Games,
                "Game installed from GOG.", "Uninstall from GOG Galaxy or the Apps page.", allowRemoval: false);
            if (finding is not null)
            {
                yield return finding;
            }
        }
    }
}

/// <summary>
/// Battle.net, Riot, EA and Ubisoft games, recognized by their standard folders. Blizzard games are
/// identified by the <c>.build.info</c> file at their root.
/// </summary>
public sealed class OtherLaunchersDetector : DetectorBase
{
    public override string Id => "launchers";

    public override string DisplayName => "Other game launchers";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var tree = context.Tree;
        var candidates = new List<int>();
        foreach (var parent in new[] { context.Known.ProgramFiles, context.Known.ProgramFilesX86 })
        {
            int index = Find(context, parent);
            if (index >= 0)
            {
                candidates.AddRange(tree.GetChildren(index));
            }
        }

        if (tree.RootPath.Length == 3)
        {
            candidates.AddRange(tree.GetChildren(ScanTree.RootIndex));
        }

        foreach (int dir in candidates)
        {
            string path = tree.GetPath(dir);
            if (tree.Dir(dir).TotalSize > 100L << 20 && context.FileExists(Path.Combine(path, ".build.info")))
            {
                var finding = DirectoryFinding(context, dir, "Battle.net games", null, StorageNature.Game, LocationCategory.Games,
                    "Blizzard game installed by Battle.net.", "Uninstall from the Battle.net app (game settings > Uninstall).", allowRemoval: false);
                if (finding is not null)
                {
                    yield return finding;
                }
            }
        }

        (string Path, string Group, string Advice)[] folders =
        [
            (Path.Combine(tree.RootPath.Length == 3 ? tree.RootPath : context.Known.SystemDrive, "Riot Games"), "Riot games", "Uninstall from Settings > Apps > Installed apps."),
            (Path.Combine(context.Known.ProgramFiles, "EA Games"), "EA games", "Uninstall from the EA app."),
            (Path.Combine(context.Known.ProgramFilesX86, "Ubisoft", "Ubisoft Game Launcher", "games"), "Ubisoft games", "Uninstall from Ubisoft Connect."),
        ];

        foreach (var (folder, group, advice) in folders)
        {
            int index = Find(context, folder);
            if (index < 0)
            {
                continue;
            }

            foreach (int game in tree.GetChildren(index))
            {
                if (tree.Dir(game).Name.Equals("Riot Client", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var finding = DirectoryFinding(context, game, group, null, StorageNature.Game, LocationCategory.Games, $"Game installed in {folder}.", advice,
                    allowRemoval: false, minimumSize: 100L << 20);
                if (finding is not null)
                {
                    yield return finding;
                }
            }
        }
    }
}
