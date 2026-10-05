using SpaceLens.Core.Models;
using SpaceLens.Windows.Native;

namespace SpaceLens.Windows.FileSystem;

public static class FileIdentity
{
    /// <summary>
    /// "volume serial:file index" for files with more than one hard link, otherwise null. Paths with the
    /// same key are the same file on disk. Opening for attributes only never reads or recalls file data.
    /// </summary>
    public static string? HardLinkKey(string path)
    {
        using var handle = NativeMethods.CreateFile(
            PathUtil.ToLongPath(path), NativeMethods.FILE_READ_ATTRIBUTES, NativeMethods.FILE_SHARE_ALL, 0,
            NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid || !NativeMethods.GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks < 2)
        {
            return null;
        }

        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }
}
