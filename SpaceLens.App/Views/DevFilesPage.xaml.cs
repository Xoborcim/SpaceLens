using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.Core.Classification;

namespace SpaceLens.App.Views;

public sealed partial class DevFilesPage : Page
{
    public DevFilesPage()
    {
        InitializeComponent();
        Findings.Filter = f => f.Category == LocationCategory.Developer;
        FindingsPageHelper.Hook(this, Findings);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Findings.Select(e.Parameter);
    }
}
