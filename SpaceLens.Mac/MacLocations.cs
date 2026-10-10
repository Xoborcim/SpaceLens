using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.Mac;

/// <summary>Where things live on a Mac: the location categories behind the Overview's "space by location".</summary>
public static class MacLocations
{
    /// <summary>Labels for the categories whose shared names are Windows words.</summary>
    public static string Label(LocationCategory category) => category switch
    {
        LocationCategory.Windows => "macOS & system",
        LocationCategory.RecycleBin => "Trash",
        _ => StorageNatureInfo.Label(category),
    };

    /// <summary>Category of the well-known folders of a scan (the nearest mapped ancestor applies to everything below).</summary>
    public static Dictionary<int, LocationCategory> BuildBaseMap(ScanTree tree, MacKnownLocations known)
    {
        var map = new Dictionary<int, LocationCategory>();
        string home = known.Home;

        void Map(string path, LocationCategory category)
        {
            int index = tree.FindDirectory(path);
            if (index > ScanTree.RootIndex || index == ScanTree.RootIndex && tree.RootPath != "/")
            {
                map[index] = category;
            }
        }

        foreach (var system in new[] { "/System", "/usr", "/bin", "/sbin", "/private", "/Library/Apple" })
        {
            Map(system, LocationCategory.Windows);
        }

        Map("/Library", LocationCategory.AppData);
        Map("/Library/Caches", LocationCategory.TemporaryAndCache);
        Map("/private/var/folders", LocationCategory.TemporaryAndCache);
        Map("/private/var/vm", LocationCategory.Windows);
        Map("/Applications", LocationCategory.Apps);
        Map("/opt/homebrew", LocationCategory.Developer);
        Map("/usr/local", LocationCategory.Developer);
        Map(known.UsersDirectory, LocationCategory.UserFiles);
        Map(PathUtil.Combine(home, "Applications"), LocationCategory.Apps);
        Map(PathUtil.Combine(home, "Library"), LocationCategory.AppData);
        Map(PathUtil.Combine(home, "Library/Caches"), LocationCategory.TemporaryAndCache);
        Map(PathUtil.Combine(home, "Library/Developer"), LocationCategory.Developer);
        Map(PathUtil.Combine(home, "Library/Mobile Documents"), LocationCategory.Documents);
        Map(PathUtil.Combine(home, "Downloads"), LocationCategory.Downloads);
        Map(PathUtil.Combine(home, "Documents"), LocationCategory.Documents);
        Map(PathUtil.Combine(home, "Pictures"), LocationCategory.Pictures);
        Map(PathUtil.Combine(home, "Movies"), LocationCategory.Videos);
        Map(PathUtil.Combine(home, "Music"), LocationCategory.Music);
        Map(PathUtil.Combine(home, "Desktop"), LocationCategory.Desktop);
        Map(PathUtil.Combine(home, ".Trash"), LocationCategory.RecycleBin);
        Map("/.Trashes", LocationCategory.RecycleBin);
        return map;
    }
}
