using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;

namespace SpaceLens.App.Views;

public sealed partial class FileTypesPage : Page
{
    private const int FilesPerType = 500;
    private int _version;

    public FileTypesPage()
    {
        InitializeComponent();
        ItemActions.AttachListBehaviors(FileList);
        State.TreeReplaced += (_, _) => RefreshIfVisible();
        State.ScanFinished += (_, _) => RefreshIfVisible();
        State.TreeMutated += (_, _) => RefreshIfVisible();
        State.LiveRefresh += (_, _) => RefreshIfVisible();
    }

    public AppState State => AppState.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    private void RefreshIfVisible()
    {
        if (ReferenceEquals(Frame?.Content, this))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var tree = State.Tree;
        if (tree is null)
        {
            TypeList.ItemsSource = null;
            FileList.ItemsSource = null;
            FilesTitle.Text = "Scan a drive to see space by file type.";
            FilesNote.Text = "";
            return;
        }

        var selected = (TypeList.SelectedItem as SummaryItem)?.Tag as FileCategory?;
        var rows = FileCategoryInfo.All
            .Select(c => (Category: c, Bytes: tree.CategoryBytes[(int)c], Count: tree.CategoryCounts[(int)c]))
            .Where(r => r.Bytes > 0)
            .OrderByDescending(r => r.Bytes)
            .ToList();
        long max = rows.Count > 0 ? rows[0].Bytes : 0;
        var items = rows.Select(r => new SummaryItem
        {
            Name = FileCategoryInfo.DisplayName(r.Category),
            Size = r.Bytes,
            CountText = $"{SizeFormatter.FormatCount(r.Count)} files",
            BarWidth = SummaryItem.Bar(r.Bytes, max),
            Swatch = SummaryItem.BrushFromArgb(FileCategoryInfo.Color(r.Category)),
            Tag = r.Category,
        }).ToList();

        TypeList.ItemsSource = items;
        TypeList.SelectedItem = items.FirstOrDefault(i => (FileCategory)i.Tag! == selected) ?? items.FirstOrDefault();
    }

    private async void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        var tree = State.Tree;
        if (tree is null || TypeList.SelectedItem is not SummaryItem { Tag: FileCategory category } row)
        {
            return;
        }

        int version = ++_version;
        var indices = await Task.Run(() => Breakdown.LargeFiles(tree, tree.FileIndexThreshold, category, FilesPerType));
        if (version != _version || tree != State.Tree)
        {
            return;
        }

        var files = indices.Select(i => EntryItem.ForFile(tree, i)).ToList();
        long max = files.Count > 0 ? files[0].Size : 0;
        foreach (var file in files)
        {
            file.Subtitle = file.ParentPath;
            file.BarWidth = SummaryItem.Bar(file.Size, max);
        }

        FileList.ItemsSource = files;
        long listed = files.Sum(f => f.Size);
        FilesTitle.Text = $"Largest {row.Name.ToLowerInvariant()} files";
        FilesNote.Text = files.Count == 0
            ? $"All {row.Name.ToLowerInvariant()} files are smaller than {SizeFormatter.Format(tree.FileIndexThreshold)}; they are counted in the total but not listed individually."
            : $"Showing {SizeFormatter.FormatCount(files.Count)} files ({SizeFormatter.Format(listed)} of {row.SizeText}). Files under {SizeFormatter.Format(tree.FileIndexThreshold)} are counted but not listed.";
    }
}
