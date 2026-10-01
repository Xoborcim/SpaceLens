using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;

namespace SpaceLens.Detectors.UserFiles;

/// <summary>
/// Large personal files worth reviewing: old installers, disk images (ISO), archives and videos.
/// Only files in user-controlled locations are reported (not inside Program Files, Windows, ProgramData
/// or application data, where such files belong to installed software).
/// </summary>
public sealed class UserFilesDetector : DetectorBase
{
    private const long MinimumSize = 100L << 20;

    public override string Id => "userfiles";

    public override string DisplayName => "Large user files";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var tree = context.Tree;
        var excluded = new HashSet<int>();
        foreach (var path in new[]
        {
            context.Known.WindowsDirectory, context.Known.ProgramFiles, context.Known.ProgramFilesX86, context.Known.ProgramData,
            Path.Combine(context.Known.UserProfile, "AppData"),
        })
        {
            int index = Find(context, path);
            if (index > ScanTree.RootIndex)
            {
                excluded.Add(index);
            }
        }

        int count = tree.FileRecordCount;
        for (int i = 0; i < count; i++)
        {
            if (!tree.IsLiveFile(i))
            {
                continue;
            }

            ref var file = ref tree.File(i);
            if (file.Size < MinimumSize)
            {
                continue;
            }

            (string Group, string Explanation)? info = file.Category switch
            {
                FileCategory.Installer => ("Old installers", "Installer package. Once the program is installed, the installer is usually no longer needed."),
                FileCategory.Executable when file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsInDownloads(context, file.Directory) =>
                    ("Old installers", "Program downloaded to your Downloads folder. Installers are usually no longer needed after installation."),
                FileCategory.DiskImage => ("ISO images", "Disk image (ISO, IMG, WIM). Often an operating system or software installer that can be downloaded again."),
                FileCategory.Archive => ("Archives", "Compressed archive. Check whether its contents were already extracted elsewhere."),
                FileCategory.Video => ("Videos", "Video file. Review whether you still need it, or move it to external storage."),
                _ => null,
            };

            if (info is null || IsExcluded(tree, file.Directory, excluded))
            {
                continue;
            }

            var category = IsInDownloads(context, file.Directory) ? LocationCategory.Downloads : file.Category == FileCategory.Video ? LocationCategory.Videos : LocationCategory.UserFiles;
            yield return FileFinding(context, i, info.Value.Group, null, StorageNature.UserFile, category, info.Value.Explanation, isOpportunity: false);
        }
    }

    private static bool IsExcluded(ScanTree tree, int dir, HashSet<int> excluded)
    {
        for (int p = dir; p >= 0; p = tree.Dir(p).Parent)
        {
            if (excluded.Contains(p))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInDownloads(DetectionContext context, int dir) =>
        PathUtil.IsSameOrUnder(context.Tree.GetPath(dir), Path.Combine(context.Known.UserProfile, "Downloads"));
}
