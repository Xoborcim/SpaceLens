using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.InstalledApps;
using SpaceLens.Core.Safety;
using SpaceLens.Windows.Shell;

namespace SpaceLens.App.Controls;

/// <summary>Facts, safety information and relevant actions for the selected file or folder.</summary>
public sealed partial class DetailsPanel : UserControl
{
    private EntryItem? _item;
    private InstalledApp? _app;

    public DetailsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Hide the "Show in Folders" action (used on the Folders page itself).</summary>
    public bool ShowFoldersAction { get; set; } = true;

    public EntryItem? Item => _item;

    public void Show(EntryItem? item)
    {
        _item = item;
        if (item is null || AppState.Current.Tree is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            DetailsContent.Visibility = Visibility.Collapsed;
            return;
        }

        var state = AppState.Current;
        var tree = state.Tree;
        EmptyState.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;

        IconGlyph.Glyph = item.Glyph;
        SizeValue.Text = item.SizeText;
        TypeValue.Text = item.Kind switch
        {
            EntryKind.Directory => item.TypeText,
            EntryKind.File => $"{item.TypeText} file",
            _ => "Files below the indexing threshold",
        };
        NameValue.Text = item.Kind == EntryKind.Directory && item.Index == 0 ? item.Path : item.Name;
        PathValue.Text = item.Path;

        FactsGrid.Children.Clear();
        FactsGrid.RowDefinitions.Clear();
        if (item.Kind == EntryKind.Directory)
        {
            ref var node = ref tree.Dir(item.Index);
            AddFact("Files", SizeFormatter.FormatCount(node.TotalFiles));
            AddFact("Folders", SizeFormatter.FormatCount(node.TotalDirs));
            AddFact("Location type", StorageNatureInfo.Label(state.CategoryOf(item.Index)));
        }
        else if (item.Kind == EntryKind.File)
        {
            AddFact("Folder", item.ParentPath);
        }

        if (item.ModifiedText.Length > 0)
        {
            AddFact("Modified", item.ModifiedText);
        }

        if (item.ParentItem is { } parent && item.PercentText.Length > 0)
        {
            AddFact("Share of parent", item.PercentText);
        }

        // Safety: shown prominently for anything that is not an ordinary user location.
        var safety = item.Safety;
        SafetyInfo.IsOpen = safety.Level != ProtectionLevel.None;
        if (SafetyInfo.IsOpen)
        {
            SafetyInfo.Severity = safety.Level == ProtectionLevel.Protected ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
            SafetyInfo.Title = safety.IsSystemManaged ? "Managed by Windows" : safety.Label;
            SafetyInfo.Message = safety.Explanation + (safety.Advice is { } advice ? "\n" + advice : "");
        }

        var finding = item.Finding;
        FindingInfo.IsOpen = finding is not null && (finding.Explanation is not null || finding.Advice is not null);
        if (finding is not null)
        {
            FindingInfo.Title = $"{finding.Group} · {StorageNatureInfo.Label(finding.Nature)}";
            FindingInfo.Message = string.Join("\n", new[] { finding.Explanation, finding.Advice }.Where(s => s is not null));
        }

        bool isEntry = item.Kind is EntryKind.Directory or EntryKind.File;
        OpenButton.Visibility = item.Kind == EntryKind.File ? Visibility.Visible : Visibility.Collapsed;
        RevealText.Text = item.Kind == EntryKind.File ? "Open folder" : "Open in Explorer";
        FoldersButton.Visibility = ShowFoldersAction && isEntry ? Visibility.Visible : Visibility.Collapsed;

        string? uri = finding?.ActionUri;
        bool hasAction = uri is not null && finding!.ActionLabel is not null && !uri.StartsWith("spacelens:", StringComparison.Ordinal);
        ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        ActionText.Text = finding?.ActionLabel ?? "";

        _app = item.Kind == EntryKind.Directory ? state.FindAppForPath(item.Path) : null;
        UninstallButton.Visibility = _app is not null ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = state.CanModify;
        UninstallText.Text = _app is not null ? $"Uninstall {_app.Name}…" : "";
        AutomationProperties.SetName(RevealButton, RevealText.Text);
        AutomationProperties.SetName(ActionButton, ActionText.Text);
        AutomationProperties.SetName(UninstallButton, UninstallText.Text);

        RecycleButton.Visibility = isEntry && ItemActions.IsRemovable(item) ? Visibility.Visible : Visibility.Collapsed;
        RecycleButton.IsEnabled = ItemActions.CanRecycle(item);
        ToolTipService.SetToolTip(RecycleButton, state.IsScanning ? "Available when the scan finishes" : null);
        WhyButton.Visibility = isEntry && !ItemActions.IsRemovable(item) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Updates size text after a live refresh.</summary>
    public void RefreshSize()
    {
        if (_item is not null)
        {
            SizeValue.Text = _item.SizeText;
        }
    }

    private void AddFact(string label, string value)
    {
        int row = FactsGrid.RowDefinitions.Count;
        FactsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        FactsGrid.Children.Add(l);
        FactsGrid.Children.Add(v);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            ItemActions.Open(_item);
        }
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            ItemActions.OpenInExplorer(_item);
        }
    }

    private void OnShowInFolders(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            AppState.Current.RequestNavigation("folders", _item);
        }
    }

    private void OnFindingAction(object sender, RoutedEventArgs e)
    {
        if (_item?.Finding?.ActionUri is { } uri)
        {
            ShellActions.OpenUri(uri);
        }
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_app is not null)
        {
            AppState.Current.RequestNavigation("apps", _app);
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            ItemActions.CopyPath(_item);
        }
    }

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            await ItemActions.RecycleAsync([_item]);
        }
    }

    private async void OnWhy(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            await ItemActions.ShowProtectedAsync(_item);
        }
    }
}
