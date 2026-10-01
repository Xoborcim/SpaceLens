using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.Core.Classification;

public sealed record CategoryTotal(LocationCategory Category, long Size);

/// <summary>
/// Splits the scanned size into coarse categories (Apps, Games, Downloads, ...). Each directory belongs to
/// the category of its nearest classified ancestor, so nested classifications (a Steam library inside
/// Program Files) take precedence without double counting.
/// </summary>
public static class LocationClassifier
{
    public static Dictionary<int, LocationCategory> BuildBaseMap(ScanTree tree, KnownLocations known)
    {
        var map = new Dictionary<int, LocationCategory>();

        void Map(string? path, LocationCategory category)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            int index = tree.FindDirectory(path);
            if (index > ScanTree.RootIndex || (index == ScanTree.RootIndex && category != LocationCategory.Other && tree.RootPath.Length > 3))
            {
                map[index] = category;
            }
        }

        Map(known.ProgramFiles, LocationCategory.Apps);
        Map(known.ProgramFilesX86, LocationCategory.Apps);
        Map(known.ProgramData, LocationCategory.AppData);
        Map(known.WindowsDirectory, LocationCategory.Windows);
        Map(known.UsersDirectory, LocationCategory.UserFiles);
        Map(Path.Combine(known.UserProfile, "AppData"), LocationCategory.AppData);
        Map(known.TempDirectory, LocationCategory.TemporaryAndCache);
        Map(Path.Combine(known.UserProfile, "Downloads"), LocationCategory.Downloads);
        Map(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), LocationCategory.Documents);
        Map(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), LocationCategory.Pictures);
        Map(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), LocationCategory.Videos);
        Map(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), LocationCategory.Music);
        Map(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), LocationCategory.Desktop);

        // Drive-root system folders on any drive.
        foreach (var name in new[] { "System Volume Information", "Recovery", "Windows.old", "$WinREAgent", "$WINDOWS.~BT" })
        {
            int index = tree.FindChild(ScanTree.RootIndex, name);
            if (index > 0 && tree.RootPath.Length == 3)
            {
                map[index] = LocationCategory.Windows;
            }
        }

        int recycle = tree.RootPath.Length == 3 ? tree.FindChild(ScanTree.RootIndex, "$Recycle.Bin") : -1;
        if (recycle > 0)
        {
            map[recycle] = LocationCategory.RecycleBin;
        }

        return map;
    }

    /// <summary>Applies detector findings on top of the base map (deeper and more specific wins).</summary>
    public static void ApplyFindings(Dictionary<int, LocationCategory> map, IEnumerable<StorageFinding> findings)
    {
        foreach (var f in findings)
        {
            if (f.DirectoryIndex > 0 && f.Category != LocationCategory.Other)
            {
                map[f.DirectoryIndex] = f.Category;
            }
        }
    }

    public static List<CategoryTotal> Summarize(ScanTree tree, IReadOnlyDictionary<int, LocationCategory> map, IEnumerable<StorageFinding>? fileFindings = null)
    {
        var totals = new long[Enum.GetValues<LocationCategory>().Length];
        totals[(int)LocationCategory.Other] = tree.Root.TotalSize;

        foreach (var (index, category) in map)
        {
            if (!tree.IsLiveDirectory(index))
            {
                continue;
            }

            long size = tree.Dir(index).TotalSize;
            totals[(int)category] += size;
            totals[(int)NearestCategory(tree, map, tree.Dir(index).Parent)] -= size;
        }

        // Individually classified files (e.g. pagefile.sys, VM disks outside classified folders).
        if (fileFindings is not null)
        {
            foreach (var f in fileFindings)
            {
                if (f.FileIndex >= 0 && tree.IsLiveFile(f.FileIndex) && f.Category != LocationCategory.Other)
                {
                    var container = NearestCategory(tree, map, tree.File(f.FileIndex).Directory);
                    if (container is LocationCategory.Other or LocationCategory.UserFiles or LocationCategory.Downloads or LocationCategory.Documents)
                    {
                        long size = tree.File(f.FileIndex).Size;
                        totals[(int)f.Category] += size;
                        totals[(int)container] -= size;
                    }
                }
            }
        }

        return totals
            .Select((size, i) => new CategoryTotal((LocationCategory)i, Math.Max(0, size)))
            .Where(t => t.Size > 0)
            .OrderByDescending(t => t.Size)
            .ToList();
    }

    public static LocationCategory NearestCategory(ScanTree tree, IReadOnlyDictionary<int, LocationCategory> map, int dirIndex)
    {
        for (int p = dirIndex; p >= 0; p = tree.Dir(p).Parent)
        {
            if (map.TryGetValue(p, out var category))
            {
                return category;
            }
        }

        return LocationCategory.Other;
    }
}
