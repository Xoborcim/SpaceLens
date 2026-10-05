using System.Runtime.InteropServices;
using SpaceLens.Core.Models;
using SpaceLens.Windows.Native;

namespace SpaceLens.Windows.FileSystem;

public enum DriveMedia
{
    Unknown,
    Ssd,
    Hdd,
    Usb,
    Removable,
    Network,
}

public sealed record DriveDescriptor(
    string RootPath,
    string Label,
    string FileSystem,
    DriveType DriveType,
    DriveMedia Media,
    long TotalBytes,
    long FreeBytes,
    bool IsSystemDrive,
    uint SerialNumber)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;

    public string Letter => RootPath.TrimEnd('\\');

    public string MediaLabel => Media switch
    {
        DriveMedia.Ssd => "SSD",
        DriveMedia.Hdd => "HDD",
        DriveMedia.Usb => "USB drive",
        DriveMedia.Removable => "Removable",
        DriveMedia.Network => "Network",
        _ => "Drive",
    };

    /// <summary>"C: Windows SSD", "D: Games", "E: Backup Drive".</summary>
    public string DisplayName => $"{Letter} {Label}".Trim();

    /// <summary>
    /// Worker count suited to the device: SSDs benefit from many concurrent metadata reads,
    /// rotational disks and USB sticks thrash with too many.
    /// </summary>
    public int RecommendedParallelism => Media switch
    {
        DriveMedia.Hdd => 3,
        DriveMedia.Usb or DriveMedia.Removable => 4,
        DriveMedia.Network => 8,
        _ => Math.Clamp(Environment.ProcessorCount, 4, 16),
    };
}

public static class DriveService
{
    /// <summary>Lists ready local drives (fixed, removable). Network and optical drives are excluded.</summary>
    public static List<DriveDescriptor> GetDrives()
    {
        string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var drives = new List<DriveDescriptor>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                drives.Add(Describe(drive, systemRoot));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return drives;
    }

    public static DriveDescriptor? GetDrive(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var info = new DriveInfo(root);
            return info.IsReady ? Describe(info, Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\") : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when deleting with FOF_ALLOWUNDO on this path's drive really goes to the Recycle Bin. Windows
    /// keeps Recycle Bins on fixed drives only; on removable, network and other drives the shell deletes
    /// permanently. Anything that cannot be identified is treated as having no Recycle Bin.
    /// </summary>
    public static bool HasRecycleBin(string path)
    {
        string? root = Path.GetPathRoot(PathUtil.NormalizeDisplayPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DriveDescriptor Describe(DriveInfo drive, string systemRoot)
    {
        string root = PathUtil.NormalizeDisplayPath(drive.RootDirectory.FullName);
        bool isSystem = root.Equals(PathUtil.NormalizeDisplayPath(systemRoot), StringComparison.OrdinalIgnoreCase);
        var media = drive.DriveType == DriveType.Removable ? DriveMedia.Removable : DetectMedia(root[0]);
        string label = drive.VolumeLabel;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = isSystem ? "Windows" : "Local Disk";
        }

        if (media is DriveMedia.Ssd or DriveMedia.Hdd)
        {
            label += " " + (media == DriveMedia.Ssd ? "SSD" : "HDD");
        }

        GetVolumeSerial(root, out uint serial);
        return new DriveDescriptor(root, label, drive.DriveFormat, drive.DriveType, media, drive.TotalSize, drive.AvailableFreeSpace, isSystem, serial);
    }

    public static unsafe bool GetVolumeSerial(string root, out uint serial)
    {
        char* fsName = stackalloc char[64];
        return NativeMethods.GetVolumeInformation(root, null, 0, out serial, out _, out _, fsName, 64);
    }

    /// <summary>
    /// Queries the storage stack for bus type and seek penalty. Opening "\\.\X:" with zero access
    /// rights is permitted for standard users and is sufficient for IOCTL_STORAGE_QUERY_PROPERTY.
    /// </summary>
    private static unsafe DriveMedia DetectMedia(char letter)
    {
        try
        {
            using var handle = NativeMethods.CreateFile($@"\\.\{letter}:", 0, NativeMethods.FILE_SHARE_ALL, 0, NativeMethods.OPEN_EXISTING, 0, 0);
            if (handle.IsInvalid)
            {
                return DriveMedia.Unknown;
            }

            // StorageDeviceProperty -> STORAGE_DEVICE_DESCRIPTOR; BusType is at offset 28.
            var query = new NativeMethods.StoragePropertyQuery { PropertyId = 0, QueryType = 0 };
            byte* descriptor = stackalloc byte[1024];
            if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)sizeof(NativeMethods.StoragePropertyQuery), descriptor, 1024, out uint returned, 0) && returned >= 32)
            {
                int busType = *(int*)(descriptor + 28);
                const int BusTypeUsb = 7;
                if (busType == BusTypeUsb)
                {
                    return DriveMedia.Usb;
                }
            }

            // StorageDeviceSeekPenaltyProperty -> DEVICE_SEEK_PENALTY_DESCRIPTOR { Version, Size, IncursSeekPenalty }.
            query.PropertyId = 7;
            byte* penalty = stackalloc byte[16];
            if (NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)sizeof(NativeMethods.StoragePropertyQuery), penalty, 16, out returned, 0) && returned >= 9)
            {
                return penalty[8] != 0 ? DriveMedia.Hdd : DriveMedia.Ssd;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return DriveMedia.Unknown;
    }

    /// <summary>
    /// Reads the NTFS change journal position, stored with snapshots for future incremental updates.
    /// Requires administrator rights; returns false otherwise.
    /// </summary>
    public static unsafe bool TryQueryUsnJournal(string root, out ulong journalId, out long nextUsn)
    {
        journalId = 0;
        nextUsn = 0;
        if (root.Length < 2 || root[1] != ':')
        {
            return false;
        }

        using var handle = NativeMethods.CreateFile($@"\\.\{root[0]}:", 0x80000000 /* GENERIC_READ */, NativeMethods.FILE_SHARE_ALL, 0, NativeMethods.OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
        {
            return false;
        }

        NativeMethods.UsnJournalData data;
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_QUERY_USN_JOURNAL, null, 0, &data, (uint)sizeof(NativeMethods.UsnJournalData), out _, 0))
        {
            return false;
        }

        journalId = data.UsnJournalID;
        nextUsn = data.NextUsn;
        return true;
    }

    public static void FillVolumeMetadata(ScanTree tree)
    {
        var drive = GetDrive(tree.RootPath);
        if (drive is null)
        {
            return;
        }

        var m = tree.Metadata;
        m.VolumeTotalBytes = drive.TotalBytes;
        m.VolumeFreeBytes = drive.FreeBytes;
        m.VolumeLabel = drive.Label;
        m.FileSystem = drive.FileSystem;
        m.VolumeSerialNumber = drive.SerialNumber;
        if (TryQueryUsnJournal(drive.RootPath, out var id, out var usn))
        {
            m.UsnJournalId = id;
            m.UsnNextUsn = usn;
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
