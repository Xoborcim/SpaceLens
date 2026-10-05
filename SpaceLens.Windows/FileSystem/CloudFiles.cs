using SpaceLens.Core.Models;
using SpaceLens.Windows.Native;

namespace SpaceLens.Windows.FileSystem;

public sealed record FreeUpResult(int Changed, int Failed);

/// <summary>
/// Files synced by a cloud provider that uses the Windows Cloud Files API (OneDrive and others).
/// "Free up space" marks them online-only, exactly like the command of the same name in File Explorer:
/// the provider then removes the local copy in the background and keeps the file in the cloud, where it
/// is downloaded again when opened. Nothing is deleted.
/// </summary>
public static class CloudFiles
{
    private const uint IoReparseTagCloud = 0x9000001A;
    private const uint IoReparseTagCloudMask = 0x0000F000;

    /// <summary>FILE_ATTRIBUTE_PINNED: always keep on this device.</summary>
    public const FileAttributes Pinned = (FileAttributes)0x00080000;

    /// <summary>FILE_ATTRIBUTE_UNPINNED: online-only; the provider may remove the local copy.</summary>
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;

    /// <summary>True for the IO_REPARSE_TAG_CLOUD family (0x9000001A, 0x9000101A, ... 0x9000F01A).</summary>
    public static bool IsCloudTag(uint reparseTag) => (reparseTag & ~IoReparseTagCloudMask) == IoReparseTagCloud;

    /// <summary>The attributes "Free up space" sets: unpinned, not pinned.</summary>
    public static FileAttributes OnlineOnly(FileAttributes attributes) => (attributes | Unpinned) & ~Pinned;

    /// <summary>True when the item or one of its folders is managed by a cloud sync provider.</summary>
    public static bool IsInSyncRoot(string path)
    {
        for (string? p = PathUtil.NormalizeDisplayPath(path); p is not null; p = PathUtil.GetParent(p))
        {
            if (TryGetReparseTag(p, out uint tag) && IsCloudTag(tag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Marks <paramref name="path"/> and, for a folder, everything below it online-only. Links to other
    /// places (junctions, symbolic links, mount points) are neither marked nor entered.
    /// </summary>
    public static FreeUpResult FreeUpSpace(string path, CancellationToken cancellationToken = default)
    {
        int changed = 0, failed = 0;
        var pending = new Stack<string>();
        pending.Push(PathUtil.NormalizeDisplayPath(path));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();
            string longPath = PathUtil.ToLongPath(current);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(longPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 && !(TryGetReparseTag(current, out uint tag) && IsCloudTag(tag)))
            {
                continue;
            }

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            var wanted = OnlineOnly(attributes);
            try
            {
                if (wanted != attributes)
                {
                    File.SetAttributes(longPath, wanted);
                    if (!isDirectory)
                    {
                        changed++;
                    }
                }

                if (isDirectory)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(longPath))
                    {
                        pending.Push(PathUtil.StripLongPathPrefix(entry)!);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return new FreeUpResult(changed, failed);
    }

    private static unsafe bool TryGetReparseTag(string path, out uint tag)
    {
        tag = 0;
        using var handle = NativeMethods.CreateFile(
            PathUtil.ToLongPath(path), NativeMethods.FILE_READ_ATTRIBUTES, NativeMethods.FILE_SHARE_ALL, 0, NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (handle.IsInvalid)
        {
            return false;
        }

        NativeMethods.FileAttributeTagInformation info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileAttributeTagInfo, &info, (uint)sizeof(NativeMethods.FileAttributeTagInformation)))
        {
            return false;
        }

        tag = (info.FileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? info.ReparseTag : 0;
        return true;
    }
}
