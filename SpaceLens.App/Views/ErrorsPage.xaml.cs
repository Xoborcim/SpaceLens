using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;

namespace SpaceLens.App.Views;

public sealed partial class ErrorsPage : Page
{
    public ErrorsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var tree = AppState.Current.Tree;
        if (tree is null)
        {
            SummaryText.Text = "No scan results.";
            return;
        }

        var errors = tree.Errors.OrderBy(err => err.Path, StringComparer.OrdinalIgnoreCase).ToList();
        ErrorList.ItemsSource = errors;
        string stored = errors.Count < tree.ErrorCount ? $" (the first {SizeFormatter.FormatCount(errors.Count)} are listed)" : "";
        SummaryText.Text = $"{SizeFormatter.FormatCount(tree.ErrorCount)} locations could not be read during the scan of {tree.RootPath}{stored}.";
    }

    private List<ScanError> Selected() => ErrorList.SelectedItems.OfType<ScanError>().ToList();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        var paths = (selected.Count > 0 ? selected : ErrorList.ItemsSource as IEnumerable<ScanError> ?? []).Select(err => err.Path);
        ItemActions.CopyText(string.Join(Environment.NewLine, paths));
    }

    private void OnOpenParent(object sender, RoutedEventArgs e)
    {
        if (Selected().FirstOrDefault() is { } error && PathUtil.GetParent(error.Path) is { } parent)
        {
            ItemActions.OpenFolder(parent);
        }
    }
}
