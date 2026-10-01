using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Controls;
using SpaceLens.App.Services;
using SpaceLens.Core.Classification;

namespace SpaceLens.App.Views;

public sealed partial class StoragePage : Page
{
    public StoragePage()
    {
        InitializeComponent();
        Findings.Filter = f => f.Category != LocationCategory.Developer;
        FindingsPageHelper.Hook(this, Findings);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Findings.Select(e.Parameter);
    }
}

internal static class FindingsPageHelper
{
    /// <summary>Keeps a findings view current while its page is visible.</summary>
    public static void Hook(Page page, FindingsView view)
    {
        var state = AppState.Current;
        void RefreshIfVisible()
        {
            if (ReferenceEquals(page.Frame?.Content, page))
            {
                view.Refresh();
            }
        }

        state.AnalysisChanged += (_, _) => RefreshIfVisible();
        state.TreeReplaced += (_, _) => RefreshIfVisible();
        state.ScanFinished += (_, _) => RefreshIfVisible();
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.AnalysisRunning))
            {
                RefreshIfVisible();
            }
        };
    }
}
