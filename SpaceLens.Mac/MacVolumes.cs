using SpaceLens.Core.Models;

namespace SpaceLens.Mac;

public sealed record MacVolume(string RootPath, string Name, string FileSystem, long TotalBytes, long FreeBytes, bool IsStartup, bool IsRemovable)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
}

/// <summary>Volumes a person would scan: the startup disk ("/") and the disks mounted under /Volumes.</summary>
public static class MacVolumes
{
    public static List<MacVolume> GetVolumes()
    {
        var mounts = new List<(string Root, string Format, long Total, long Free, DriveType Type)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    mounts.Add((drive.RootDirectory.FullName, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace, drive.DriveType));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return Select(mounts);
    }

    /// <summary>
    /// Keeps "/" and /Volumes/* and drops what macOS mounts for itself (/System/Volumes/*, /dev, /private/var/vm,
    /// disk images of the system). On APFS the startup volume's free space is shared with its data volume,
    /// which is what <see cref="DriveInfo"/> reports for "/".
    /// </summary>
    public static List<MacVolume> Select(IEnumerable<(string Root, string Format, long Total, long Free, DriveType Type)> mounts)
    {
        var volumes = new List<MacVolume>();
        foreach (var (root, format, total, free, type) in mounts)
        {
            string path = PathUtil.NormalizeDisplayPath(root);
            bool startup = path == "/";
            bool mountedVolume = PathUtil.GetParent(path) == "/Volumes";
            if (!startup && !mountedVolume || total <= 0 || format is "devfs" or "autofs" or "nullfs")
            {
                continue;
            }

            string name = startup ? "Macintosh HD" : PathUtil.GetName(path);
            volumes.Add(new MacVolume(path, name, format, total, free, startup, type is DriveType.Removable or DriveType.Network));
        }

        return volumes.OrderByDescending(v => v.IsStartup).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Folders a scan of <paramref name="root"/> must not enter. Scanning "/" would otherwise count the data
    /// volume twice (it is firmlinked under /System/Volumes/Data) and descend into every other disk under
    /// /Volumes. Like "du -x", a scan stays on the volume it started on.
    /// </summary>
    public static IReadOnlyList<string> ExcludedPathsFor(string root) =>
        PathUtil.NormalizeDisplayPath(root) == "/"
            ? ["/System/Volumes", "/Volumes", "/dev", "/net", "/home", "/Network/Servers"]
            : [];
}
