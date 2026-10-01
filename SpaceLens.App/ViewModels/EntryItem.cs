using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using SpaceLens.App.Services;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.App.ViewModels;

public enum EntryKind
{
    Directory,
    File,

    /// <summary>All files directly in a folder that are below the indexing threshold, as one row.</summary>
    LooseFiles,

    /// <summary>Placeholder for the remaining children of a very large folder.</summary>
    More,
}

/// <summary>
/// A row in a list or in the folder tree. Created only for items that are actually displayed;
/// the underlying data lives in the compact <see cref="ScanTree"/> and is referenced by index.
/// </summary>
public sealed partial class EntryItem : ObservableObject
{
    public const double BarMaxWidth = 100;

    private EntryItem(EntryKind kind, int index)
    {
        Kind = kind;
        Index = index;
    }

    public EntryKind Kind { get; }

    /// <summary>Directory index (Directory, LooseFiles) or file index (File).</summary>
    public int Index { get; }

    public bool IsDirectory => Kind == EntryKind.Directory;

    public bool IsFile => Kind == EntryKind.File;

    public string Name { get; set; } = "";

    public string Path { get; private set; } = "";

    public string ParentPath { get; private set; } = "";

    public FileAttributes Attributes { get; private set; }

    public long LastWriteUtc { get; private set; }

    public string Glyph { get; private set; } = "\uE8B7";

    public string TypeText { get; set; } = "";

    public string? Subtitle { get; set; }

    public SafetyAssessment Safety { get; private set; } = SafetyAssessment.Ordinary;

    public StorageFinding? Finding { get; set; }

    [ObservableProperty]
    public partial long Size { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial double BarWidth { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; } = "";

    [ObservableProperty]
    public partial string ItemsText { get; set; } = "";

    // Tree presentation ---------------------------------------------------------------------------

    public int Depth { get; init; }

    /// <summary>Row of the containing folder in the folder tree (percentages are relative to it).</summary>
    public EntryItem? ParentItem { get; init; }

    public (EntryKind Kind, int Index) Key => (Kind, Index);

    public Thickness Indent => new(Depth * 20, 0, 0, 0);

    public bool CanExpand { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    public partial bool IsExpanded { get; set; }

    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public Visibility ChevronVisibility => CanExpand ? Visibility.Visible : Visibility.Collapsed;

    // Badges --------------------------------------------------------------------------------------

    public string BadgeText => Safety.Level switch
    {
        ProtectionLevel.Protected when Safety.IsSystemManaged => "Managed by Windows",
        ProtectionLevel.Protected => "Protected",
        ProtectionLevel.Caution when Safety.IsSystemManaged => "System",
        _ when IsManagedElsewhere => Finding!.Nature == StorageNature.Game ? "Managed by launcher" : "Remove with its own tool",
        _ => "",
    };

    public Visibility BadgeVisibility => BadgeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsReparsePoint { get; private set; }

    public bool HasError { get; private set; }

    public string ModifiedText => LastWriteUtc > 0 ? DateTime.FromFileTimeUtc(LastWriteUtc).ToLocalTime().ToString("d") : "";

    public string AutomationName =>
        $"{Name}, {SizeText}{(TypeText.Length > 0 ? ", " + TypeText : "")}{(BadgeText.Length > 0 ? ", " + BadgeText : "")}" +
        (Kind == EntryKind.Directory && CanExpand ? (IsExpanded ? ", expanded" : ", collapsed") : "");

    /// <summary>List items use ToString() as their name for screen readers.</summary>
    public override string ToString() => AutomationName;

    // Factories -----------------------------------------------------------------------------------

    public static EntryItem ForDirectory(ScanTree tree, int index, long parentSize = 0, int depth = 0, EntryItem? parent = null)
    {
        var item = new EntryItem(EntryKind.Directory, index) { Depth = depth, ParentItem = parent };
        ref var node = ref tree.Dir(index);
        item.Name = index == ScanTree.RootIndex ? tree.RootPath : node.Name;
        item.Path = tree.GetPath(index);
        item.ParentPath = PathUtil.GetParent(item.Path) ?? "";
        item.LastWriteUtc = node.LastWriteUtc;
        item.IsReparsePoint = (node.Flags & NodeFlags.ReparsePoint) != 0;
        item.HasError = (node.Flags & (NodeFlags.AccessDenied | NodeFlags.Error)) != 0;
        item.Glyph = item.IsReparsePoint ? "\uE71B" : item.HasError ? "\uE72E" : "\uE8B7";
        item.CanExpand = !item.IsReparsePoint && (node.SubdirCount > 0 || node.FirstFile >= 0 || node.OwnFileCount > 0);
        item.Safety = AppState.Current.Safety.AssessDirectory(item.Path, node.Flags);
        item.TypeText = item.IsReparsePoint ? "Link (not followed)" : item.HasError ? "Inaccessible" : "Folder";
        if (AppState.Current.FindingByDirectory.TryGetValue(index, out var finding))
        {
            item.Finding = finding;
            item.TypeText = finding.Group;
        }

        item.Refresh(tree, parentSize);
        return item;
    }

    public static EntryItem ForFile(ScanTree tree, int index, long parentSize = 0, int depth = 0, EntryItem? parent = null)
    {
        var item = new EntryItem(EntryKind.File, index) { Depth = depth, ParentItem = parent };
        ref var file = ref tree.File(index);
        item.Name = file.Name;
        item.ParentPath = tree.GetPath(file.Directory);
        item.Path = PathUtil.Combine(item.ParentPath, file.Name);
        item.LastWriteUtc = file.LastWriteUtc;
        item.Attributes = file.Attributes;
        item.Glyph = GlyphFor(file.Category);
        item.TypeText = FileCategoryInfo.ShortName(file.Category);
        item.Safety = AppState.Current.Safety.AssessFile(item.Path, file.Attributes);
        if (AppState.Current.FindingByFile.TryGetValue(index, out var finding))
        {
            item.Finding = finding;
        }

        item.Refresh(tree, parentSize);
        return item;
    }

    public static EntryItem ForLooseFiles(ScanTree tree, int dirIndex, long parentSize = 0, int depth = 0, EntryItem? parent = null)
    {
        var item = new EntryItem(EntryKind.LooseFiles, dirIndex) { Depth = depth, ParentItem = parent };
        item.ParentPath = tree.GetPath(dirIndex);
        item.Path = item.ParentPath;
        item.Glyph = "\uE8B9";
        item.TypeText = "Files";
        item.Refresh(tree, parentSize);
        return item;
    }

    /// <summary>"N more items" row shown when a folder has more children than the tree displays.</summary>
    public static EntryItem ForMore(int dirIndex, int count, long size, long parentSize, int depth, EntryItem? parent)
    {
        var item = new EntryItem(EntryKind.More, dirIndex) { Depth = depth, ParentItem = parent };
        item.Glyph = "\uE712";
        item.Name = $"{SizeFormatter.FormatCount(count)} more items";
        item.ItemsText = "Open the folder in Explorer or scan it to see everything";
        item.UpdateMore(size, parentSize);
        return item;
    }

    /// <summary>A location that is not part of the scan tree (e.g. reported by a Windows API). Read-only actions only.</summary>
    public static EntryItem ForExternal(StorageFinding finding)
    {
        var item = new EntryItem(EntryKind.More, -1)
        {
            Name = finding.Title,
            Path = finding.Path,
            ParentPath = PathUtil.GetParent(finding.Path) ?? "",
            Glyph = GlyphFor(finding.Category),
            TypeText = finding.Group,
            Finding = finding,
            Safety = AppState.Current.Safety.AssessDirectory(finding.Path),
        };
        item.UpdateMore(finding.Size, 0);
        return item;
    }

    public void UpdateMore(long size, long parentSize)
    {
        Size = size;
        SizeText = SizeFormatter.Format(size);
        double fraction = parentSize > 0 ? Math.Clamp((double)size / parentSize, 0, 1) : 0;
        BarWidth = fraction * BarMaxWidth;
        PercentText = parentSize > 0 ? SizeFormatter.FormatPercent(fraction) : "";
    }

    /// <summary>True when a detector says this location must be managed by its owner (launcher, Windows, a tool) rather than deleted.</summary>
    public bool IsManagedElsewhere => Finding is { AllowDirectRemoval: false };

    /// <summary>Re-reads sizes from the tree (used for live updates during a scan).</summary>
    public void Refresh(ScanTree tree, long parentSize)
    {
        switch (Kind)
        {
            case EntryKind.Directory:
                ref var node = ref tree.Dir(Index);
                Size = node.TotalSize;
                ItemsText = node.TotalFiles == 0 && node.TotalDirs == 0 ? "" :
                    $"{SizeFormatter.FormatCount(node.TotalFiles)} files" + (node.TotalDirs > 0 ? $", {SizeFormatter.FormatCount(node.TotalDirs)} folders" : "");
                break;
            case EntryKind.File:
                Size = tree.File(Index).Size;
                ItemsText = "";
                break;
            case EntryKind.LooseFiles:
                Size = tree.GetUnindexedOwnSize(Index, out int count);
                Name = $"{SizeFormatter.FormatCount(count)} smaller file{(count == 1 ? "" : "s")}";
                ItemsText = $"each under {SizeFormatter.Format(tree.FileIndexThreshold)}";
                break;
        }

        SizeText = SizeFormatter.Format(Size);
        double fraction = parentSize > 0 ? Math.Clamp((double)Size / parentSize, 0, 1) : 0;
        BarWidth = fraction * BarMaxWidth;
        PercentText = parentSize > 0 ? SizeFormatter.FormatPercent(fraction) : "";
    }

    public static string GlyphFor(FileCategory category) => category switch
    {
        FileCategory.Video => "\uE714",
        FileCategory.Audio => "\uE8D6",
        FileCategory.Image => "\uEB9F",
        FileCategory.Document => "\uE8A5",
        FileCategory.Archive => "\uF012",
        FileCategory.DiskImage => "\uE958",
        FileCategory.VirtualMachine => "\uE7F4",
        FileCategory.Installer => "\uE896",
        FileCategory.Executable => "\uE756",
        FileCategory.GameData => "\uE7FC",
        FileCategory.Database => "\uE1D3",
        FileCategory.DeveloperFile => "\uE943",
        FileCategory.SystemFile => "\uE770",
        FileCategory.LogsAndDumps => "\uE9F9",
        _ => "\uE7C3",
    };

    public static string GlyphFor(LocationCategory category) => category switch
    {
        LocationCategory.Apps => "\uE71D",
        LocationCategory.Games => "\uE7FC",
        LocationCategory.Downloads => "\uE896",
        LocationCategory.Documents => "\uE8A5",
        LocationCategory.Pictures => "\uEB9F",
        LocationCategory.Videos => "\uE714",
        LocationCategory.Music => "\uE8D6",
        LocationCategory.Desktop => "\uE7F4",
        LocationCategory.AppData => "\uE74C",
        LocationCategory.Developer => "\uE943",
        LocationCategory.TemporaryAndCache => "\uE81C",
        LocationCategory.RecycleBin => "\uE74D",
        LocationCategory.Windows => "\uE770",
        LocationCategory.VirtualMachines => "\uE7F4",
        LocationCategory.UserFiles => "\uE77B",
        _ => "\uE8B7",
    };
}

/// <summary>A labelled size row for summaries (categories, groups, contents).</summary>
public sealed class SummaryItem
{
    public required string Name { get; init; }

    public string? Subtitle { get; init; }

    public long Size { get; init; }

    public string SizeText => SizeFormatter.Format(Size);

    public double BarWidth { get; init; }

    public string Glyph { get; init; } = "\uE8B7";

    public Microsoft.UI.Xaml.Media.Brush? Swatch { get; init; }

    public object? Tag { get; init; }

    public string CountText { get; init; } = "";

    public string SubtitleText => string.Join("  ·  ", new[] { CountText, Subtitle }.Where(s => !string.IsNullOrEmpty(s)));

    public Visibility HasNoSwatch => Swatch is null ? Visibility.Visible : Visibility.Collapsed;

    public Microsoft.UI.Xaml.Media.Brush BarBrush =>
        Swatch ?? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    public string AutomationName => $"{Name}, {SizeText}" + (SubtitleText.Length > 0 ? ", " + SubtitleText : "");

    public override string ToString() => AutomationName;

    public static Microsoft.UI.Xaml.Media.SolidColorBrush BrushFromArgb(uint argb) =>
        new(global::Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    /// <summary>Bar width relative to the largest item in a list.</summary>
    public static double Bar(long size, long max) => max > 0 ? Math.Clamp((double)size / max, 0, 1) * EntryItem.BarMaxWidth : 0;
}
