using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Formatting;

namespace SpaceLens.Desktop.ViewModels;

public sealed record SizeChoice(string Label, long Bytes);

public sealed partial class LargeFilesViewModel : PageViewModel
{
    private int _version;

    public LargeFilesViewModel(MainViewModel main)
        : base(main, "Large Files", "▤")
    {
        bool decimalUnits = main.Platform.Units == SizeUnits.Decimal;
        long m = decimalUnits ? 1_000_000 : 1L << 20;
        long g = decimalUnits ? 1_000_000_000 : 1L << 30;
        SizeChoices = [new("Over 10 MB", 10 * m), new("Over 100 MB", 100 * m), new("Over 1 GB", g), new("Over 5 GB", 5 * g), new("Over 10 GB", 10 * g)];
        SelectedSize = SizeChoices[2];
    }

    public IReadOnlyList<SizeChoice> SizeChoices { get; }

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial SizeChoice SelectedSize { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    partial void OnSelectedSizeChanged(SizeChoice value) => Refresh();

    protected override async void OnRefresh()
    {
        int version = ++_version;
        var tree = Main.Tree;
        Items.Clear();
        if (tree is null)
        {
            Summary = "Scan a disk to find its largest files.";
            return;
        }

        long minimum = Math.Max(SelectedSize.Bytes, tree.FileIndexThreshold);
        var indices = await Task.Run(() => Breakdown.LargeFiles(tree, minimum, maxResults: 2000));
        if (version != _version || Main.Tree != tree)
        {
            return;
        }

        long max = indices.Count > 0 ? tree.File(indices[0]).Size : 1;
        long total = 0;
        foreach (int index in indices)
        {
            var item = ItemViewModel.ForFile(Main, tree, index);
            item.Subtitle = $"{Core.Models.PathUtil.GetParent(item.Path)}";
            item.Fraction = (double)item.Size / max;
            total += item.Size;
            Items.Add(item);
        }

        Summary = indices.Count == 0
            ? $"No files {SelectedSize.Label.ToLowerInvariant()}."
            : $"{SizeFormatter.FormatCount(indices.Count)} files  ·  {Main.FormatSize(total)} in total";
    }
}
