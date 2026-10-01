using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;

namespace SpaceLens.Detectors;

/// <summary>Helpers shared by detectors.</summary>
public abstract class DetectorBase : IStorageDetector
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken);

    protected StorageFinding? DirectoryFinding(
        DetectionContext ctx,
        int index,
        string group,
        string? title,
        StorageNature nature,
        LocationCategory category,
        string? explanation,
        string? advice = null,
        bool isOpportunity = false,
        bool allowRemoval = true,
        bool isUnit = true,
        string? actionUri = null,
        string? actionLabel = null,
        long minimumSize = 1)
    {
        if (index < 0 || !ctx.Tree.IsLiveDirectory(index))
        {
            return null;
        }

        long size = ctx.Tree.Dir(index).TotalSize;
        if (size < minimumSize)
        {
            return null;
        }

        return new StorageFinding
        {
            DetectorId = Id,
            Group = group,
            Title = title ?? ctx.Tree.Dir(index).Name,
            Path = ctx.Tree.GetPath(index),
            Size = size,
            DirectoryIndex = index,
            Nature = nature,
            Category = category,
            Explanation = explanation,
            Advice = advice,
            IsOpportunity = isOpportunity,
            AllowDirectRemoval = allowRemoval,
            IsUnit = isUnit,
            ActionUri = actionUri,
            ActionLabel = actionLabel,
        };
    }

    protected StorageFinding FileFinding(
        DetectionContext ctx,
        int fileIndex,
        string group,
        string? title,
        StorageNature nature,
        LocationCategory category,
        string? explanation,
        string? advice = null,
        bool isOpportunity = false,
        bool allowRemoval = true)
    {
        ref var file = ref ctx.Tree.File(fileIndex);
        return new StorageFinding
        {
            DetectorId = Id,
            Group = group,
            Title = title ?? file.Name,
            Path = ctx.Tree.GetFilePath(fileIndex),
            Size = file.Size,
            FileIndex = fileIndex,
            Nature = nature,
            Category = category,
            Explanation = explanation,
            Advice = advice,
            IsOpportunity = isOpportunity,
            AllowDirectRemoval = allowRemoval,
        };
    }

    protected static int Find(DetectionContext ctx, string? path) => TreeWalker.Resolve(ctx.Tree, path);

    protected static string Combine(params string[] parts) => Path.Combine(parts);
}

/// <summary>Runs detectors in isolation: one failing detector never hides the results of the others.</summary>
public static class DetectionRunner
{
    public static IReadOnlyList<IStorageDetector> CreateDefault(bool includeDeveloper = true)
    {
        var list = new List<IStorageDetector>
        {
            new WindowsStorage.WindowsStorageDetector(),
            new Steam.SteamDetector(),
            new Games.EpicDetector(),
            new Games.XboxDetector(),
            new Games.GogDetector(),
            new Games.OtherLaunchersDetector(),
            new VirtualMachines.VirtualMachineDetector(),
            new UserFiles.UserFilesDetector(),
        };

        if (includeDeveloper)
        {
            list.Insert(1, new Developer.DeveloperFilesDetector());
        }

        return list;
    }

    public static List<StorageFinding> Run(DetectionContext context, IEnumerable<IStorageDetector> detectors, CancellationToken cancellationToken, Action<string, Exception>? onError = null)
    {
        var findings = new List<StorageFinding>();
        foreach (var detector in detectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                findings.AddRange(detector.Detect(context, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                onError?.Invoke(detector.Id, ex);
            }
        }

        // A location claimed by two detectors keeps the first (more specific detectors run first).
        var seen = new HashSet<(int, int, string)>();
        return findings.Where(f => seen.Add((f.DirectoryIndex, f.FileIndex, f.Group))).ToList();
    }
}
