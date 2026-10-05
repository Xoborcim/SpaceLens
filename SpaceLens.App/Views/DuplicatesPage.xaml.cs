using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Duplicates;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Windows.FileSystem;

namespace SpaceLens.App.Views;

/// <summary>
/// Groups of identical files. The search reads file contents, so it only runs when asked. Removing copies
/// goes through the usual Recycle Bin confirmation and safety checks.
/// </summary>
public sealed partial class DuplicatesPage : Page
{
    private CancellationTokenSource? _cts;
    private ScanTree? _resultTree;
    private List<DuplicateGroup> _groups = [];
    private DuplicateResult? _result;

    public DuplicatesPage()
    {
        InitializeComponent();
        ItemActions.AttachListBehaviors(FileList);
        State.TreeReplaced += (_, _) => OnTreeReplaced();
        State.TreeMutated += (_, _) => OnTreeMutated();
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.State))
            {
                UpdateButtons();
            }
        };
        ShowEmpty("Select Find duplicates to compare the files of the current scan.");
        UpdateButtons();
    }

    public AppState State => AppState.Current;

    private bool IsSearching => _cts is not null;

    private void UpdateButtons()
    {
        FindButton.IsEnabled = !IsSearching && State.Tree is not null && !State.IsScanning;
        ToolTipService.SetToolTip(FindButton, State.IsScanning ? "Available when the scan finishes" : State.Tree is null ? "Scan a drive or folder first" : null);
        CancelButton.Visibility = IsSearching ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = IsSearching ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        var tree = State.Tree;
        if (tree is null || State.IsScanning || IsSearching)
        {
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        ClearResults();
        ShowEmpty("Looking for duplicates…");
        SearchProgress.Value = 0;
        SearchProgress.IsIndeterminate = true;
        ProgressText.Text = "Grouping files by size…";
        UpdateButtons();

        var options = new DuplicateOptions
        {
            MinimumSize = tree.FileIndexThreshold,
            ExcludedFolders = [State.Known.WindowsDirectory],
            Parallelism = State.SelectedDrive?.Media == DriveMedia.Hdd ? 1 : 2,
            FileIdentity = FileIdentity.HardLinkKey,
        };
        var progress = new Progress<DuplicateProgress>(OnProgress);

        try
        {
            var result = await Task.Run(() => DuplicateFinder.Find(tree, options, progress, cts.Token), cts.Token);
            if (tree == State.Tree)
            {
                _result = result;
                _resultTree = tree;
                _groups = result.Groups.ToList();
                ShowResults();
            }
        }
        catch (OperationCanceledException)
        {
            ShowEmpty("The search was cancelled.");
        }
        finally
        {
            cts.Dispose();
            if (_cts == cts)
            {
                _cts = null;
            }

            UpdateButtons();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnProgress(DuplicateProgress p)
    {
        if (!IsSearching)
        {
            return;
        }

        switch (p.Stage)
        {
            case DuplicateStage.Comparing:
                SearchProgress.IsIndeterminate = true;
                ProgressText.Text = $"Checking for hard links ({SizeFormatter.FormatCount(p.FilesDone)} of {SizeFormatter.FormatCount(p.FilesTotal)} files)…";
                break;
            case DuplicateStage.QuickCheck:
                SearchProgress.IsIndeterminate = false;
                SearchProgress.Value = p.FilesTotal > 0 ? p.FilesDone * 100.0 / p.FilesTotal : 0;
                ProgressText.Text = $"Quick check: {SizeFormatter.FormatCount(p.FilesDone)} of {SizeFormatter.FormatCount(p.FilesTotal)} files";
                break;
            default:
                SearchProgress.IsIndeterminate = false;
                SearchProgress.Value = p.BytesTotal > 0 ? p.BytesDone * 100.0 / p.BytesTotal : 100;
                ProgressText.Text = $"Comparing contents: {SizeFormatter.Format(p.BytesDone)} of {SizeFormatter.Format(p.BytesTotal)}";
                break;
        }
    }

    private void ShowResults()
    {
        var tree = _resultTree;
        if (tree is null || _result is null)
        {
            return;
        }

        string notes = string.Join("  ·  ", new[]
        {
            _result.UnreadableFiles > 0 ? $"{SizeFormatter.FormatCount(_result.UnreadableFiles)} files could not be read" : null,
            _result.CloudFilesSkipped > 0 ? $"{SizeFormatter.FormatCount(_result.CloudFilesSkipped)} online-only cloud files were not downloaded to compare" : null,
        }.Where(n => n is not null));

        if (_groups.Count == 0)
        {
            ClearResults();
            SummaryText.Text = notes;
            ShowEmpty("No duplicate files of 1 MB or more were found.");
            return;
        }

        long reclaimable = _groups.Sum(g => g.Reclaimable);
        SummaryText.Text = $"{SizeFormatter.FormatCount(_groups.Count)} sets of identical files  ·  {SizeFormatter.Format(reclaimable)} could be freed by keeping one copy of each" +
            (notes.Length > 0 ? "  ·  " + notes : "");

        var selected = (GroupList.SelectedItem as SummaryItem)?.Tag as DuplicateGroup;
        long max = _groups.Max(g => g.Reclaimable);
        var items = _groups.Select(g => new SummaryItem
        {
            Name = tree.File(g.Files[0]).Name,
            Size = g.Reclaimable,
            CountText = $"{g.Files.Count} copies of {SizeFormatter.Format(g.Size)}",
            Subtitle = "could be freed",
            Glyph = "",
            BarWidth = SummaryItem.Bar(g.Reclaimable, max),
            Tag = g,
        }).ToList();

        GroupList.ItemsSource = items;
        GroupList.SelectedItem = items.FirstOrDefault(i => ReferenceEquals(i.Tag, selected)) ?? items[0];
        EmptyText.Visibility = Visibility.Collapsed;
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        var tree = _resultTree;
        if (tree is null || GroupList.SelectedItem is not SummaryItem { Tag: DuplicateGroup group })
        {
            FileList.ItemsSource = null;
            FilesTitle.Text = "";
            FilesNote.Text = "";
            SelectCopiesButton.Visibility = Visibility.Collapsed;
            return;
        }

        var files = group.Files.Where(tree.IsLiveFile).Select(i => EntryItem.ForFile(tree, i)).ToList();
        foreach (var file in files)
        {
            file.Subtitle = file.ModifiedText.Length > 0 ? $"{file.ParentPath}  ·  modified {file.ModifiedText}" : file.ParentPath;
            file.BarWidth = EntryItem.BarMaxWidth;
        }

        FileList.ItemsSource = files;
        FilesTitle.Text = files.Count > 0 ? files[0].Name : "";
        FilesNote.Text = $"{files.Count} identical files of {SizeFormatter.Format(group.Size)} each. Keep at least one; removing the others frees {SizeFormatter.Format(group.Reclaimable)}.";
        SelectCopiesButton.Visibility = files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectCopies(object sender, RoutedEventArgs e)
    {
        FileList.SelectedItems.Clear();
        foreach (var item in (FileList.ItemsSource as IEnumerable<EntryItem> ?? []).Skip(1))
        {
            FileList.SelectedItems.Add(item);
        }

        FileList.Focus(FocusState.Programmatic);
    }

    /// <summary>Copies were removed (Recycle Bin or permanent delete): drop them, and groups left with a single file.</summary>
    private void OnTreeMutated()
    {
        var tree = _resultTree;
        if (tree is null || tree != State.Tree)
        {
            return;
        }

        _groups = _groups
            .Select(g => g with { Files = g.Files.Where(tree.IsLiveFile).ToList() })
            .Where(g => g.Files.Count > 1)
            .OrderByDescending(g => g.Reclaimable)
            .ToList();
        ShowResults();
    }

    private void OnTreeReplaced()
    {
        _cts?.Cancel();
        _result = null;
        _resultTree = null;
        _groups = [];
        ClearResults();
        SummaryText.Text = "Finds files of 1 MB or more with identical contents. Files are compared by size first, then by reading them, so only possible duplicates are read.";
        ShowEmpty("Select Find duplicates to compare the files of the current scan.");
        UpdateButtons();
    }

    private void ClearResults()
    {
        GroupList.ItemsSource = null;
        FileList.ItemsSource = null;
        FilesTitle.Text = "";
        FilesNote.Text = "";
        SelectCopiesButton.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
    }
}
