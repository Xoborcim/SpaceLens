using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;

namespace SpaceLens.Desktop.ViewModels;

public enum ItemKind
{
    Folder,
    File,

    /// <summary>The small (unindexed) files directly inside a folder, as one row.</summary>
    LooseFiles,
}

/// <summary>
/// A file or folder row. Created only for rows on screen; the data lives in the compact scan tree and is
/// referenced by index, together with the tree it belongs to.
/// </summary>
public sealed partial class ItemViewModel
{
    private readonly MainViewModel _main;

    private ItemViewModel(MainViewModel main, ScanTree tree, ItemKind kind, int index)
    {
        _main = main;
        Tree = tree;
        Kind = kind;
        Index = index;
    }

    public ScanTree Tree { get; }

    public ItemKind Kind { get; }

    /// <summary>Directory index (Folder, LooseFiles) or file index (File).</summary>
    public int Index { get; }

    public string Name { get; private init; } = "";

    public string Path { get; private init; } = "";

    public long Size { get; private init; }

    public string SizeText => _main.FormatSize(Size);

    public string Icon => Kind switch
    {
        ItemKind.Folder when IsLink => "↪︎",
        ItemKind.Folder => "📁",
        ItemKind.LooseFiles => "🗂",
        _ => "📄",
    };

    public string Subtitle { get; set; } = "";

    /// <summary>Share of the reference size (parent or largest row), 0..1, for the bar.</summary>
    public double Fraction { get; set; }

    public double BarWidth => Math.Clamp(Fraction, 0, 1) * 120;

    public IBrush BarBrush { get; set; } = Brushes.SteelBlue;

    public SafetyAssessment Safety { get; private init; } = SafetyAssessment.Ordinary;

    public StorageFinding? Finding { get; private init; }

    public bool IsLink { get; private init; }

    public bool IsFolder => Kind == ItemKind.Folder;

    public bool IsFile => Kind == ItemKind.File;

    /// <summary>A short label next to the name: why the item is special.</summary>
    public string Badge =>
        Safety.Level == ProtectionLevel.Protected ? (Safety.IsSystemManaged ? "macOS" : "Protected") :
        Finding is { AllowDirectRemoval: false } f ? f.Group :
        Safety.Level == ProtectionLevel.Caution ? Safety.Label :
        IsLink ? "Link" : "";

    public bool HasBadge => Badge.Length > 0;

    public bool CanTrash =>
        Kind is ItemKind.Folder or ItemKind.File && Safety.CanDelete && Finding is not { AllowDirectRemoval: false } &&
        !IsLink && !(Kind == ItemKind.Folder && Index == ScanTree.RootIndex);

    public static ItemViewModel ForFolder(MainViewModel main, ScanTree tree, int index)
    {
        ref var node = ref tree.Dir(index);
        string path = tree.GetPath(index);
        return new ItemViewModel(main, tree, ItemKind.Folder, index)
        {
            Name = index == ScanTree.RootIndex ? tree.RootPath : node.Name,
            Path = path,
            Size = node.TotalSize,
            IsLink = (node.Flags & (NodeFlags.ReparsePoint | NodeFlags.Excluded)) != 0,
            Safety = main.Platform.Safety.AssessDirectory(path, node.Flags),
            Finding = main.FindingFor(false, index),
            Subtitle = FileCount(node.TotalFiles),
        };
    }

    public static ItemViewModel ForFile(MainViewModel main, ScanTree tree, int index)
    {
        ref var file = ref tree.File(index);
        string path = tree.GetFilePath(index);
        return new ItemViewModel(main, tree, ItemKind.File, index)
        {
            Name = file.Name,
            Path = path,
            Size = file.Size,
            Safety = main.Platform.Safety.AssessFile(path, file.Attributes),
            Finding = main.FindingFor(true, index),
            Subtitle = FileCategoryInfo.ShortName(file.Category),
        };
    }

    public static string FileCount(long count) => count == 1 ? "1 file" : $"{SizeFormatter.FormatCount(count)} files";

    public static ItemViewModel ForLooseFiles(MainViewModel main, ScanTree tree, int dirIndex)
    {
        long size = tree.GetUnindexedOwnSize(dirIndex, out int count);
        return new ItemViewModel(main, tree, ItemKind.LooseFiles, dirIndex)
        {
            Name = $"{SizeFormatter.FormatCount(count)} smaller files",
            Path = tree.GetPath(dirIndex),
            Size = size,
            Subtitle = "Files under 1 MB directly in this folder",
        };
    }

    [RelayCommand]
    private void Reveal() => _main.Platform.Reveal(Kind == ItemKind.LooseFiles ? Path : Path);

    [RelayCommand]
    private void Open() => _main.Platform.Open(Path);

    [RelayCommand]
    private Task CopyPath() => _main.Dialogs.CopyTextAsync(Path);

    [RelayCommand]
    private void ShowInFolders() => _main.ShowInFolders(Kind == ItemKind.File ? Tree.File(Index).Directory : Index);

    [RelayCommand]
    private Task MoveToTrash() => _main.MoveToTrashAsync([this]);

    [RelayCommand]
    private Task RescanFolder() => _main.RescanFolderAsync(this);

    public override string ToString() => $"{Name}, {SizeText}";
}
