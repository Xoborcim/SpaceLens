using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.InstalledApps;
using SpaceLens.Core.Models;
using SpaceLens.Windows.InstalledApps;

namespace SpaceLens.App.Views;

public sealed partial class AppsPage : Page
{
    private static readonly TimeSpan RemovalWatchTime = TimeSpan.FromMinutes(3);

    private readonly bool _initialized;
    private InstalledApp? _selected;
    private InstalledApp? _pendingSelection;

    public AppsPage()
    {
        InitializeComponent();
        State.AppsChanged += (_, _) =>
        {
            if (ReferenceEquals(Frame?.Content, this))
            {
                Populate();
            }
        };
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.State) && _selected is not null)
            {
                ShowDetails(_selected);
            }
        };
        _initialized = true;
    }

    public AppState State => AppState.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingSelection = e.Parameter as InstalledApp;
        if (_pendingSelection is not null)
        {
            FilterBox.Text = "";
            KindBox.SelectedIndex = 0;
        }

        _ = State.LoadAppsAsync(includeStore: true);

        Populate();
    }

    private void Populate()
    {
        if (!_initialized)
        {
            return;
        }

        string filter = FilterBox.Text.Trim();
        string kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        string sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "size";

        IEnumerable<InstalledApp> apps = State.Apps;
        apps = kind switch
        {
            "desktop" => apps.Where(a => a.Source != AppSource.Msix),
            "store" => apps.Where(a => a.Source == AppSource.Msix),
            "games" => apps.Where(a => a.IsGame),
            _ => apps,
        };

        if (filter.Length > 0)
        {
            apps = apps.Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (a.Publisher?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        apps = sort switch
        {
            "name" => apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => apps.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue),
            _ => apps.OrderByDescending(a => a.BestSize),
        };

        var list = apps.ToList();
        var nesting = CountNestedApps(State.Apps);
        long max = list.Count > 0 ? list.Max(a => a.BestSize) : 0;
        var items = list.Select(a => new AppItem(a, max, nesting.Nested.GetValueOrDefault(a))).ToList();
        var keep = _pendingSelection ?? _selected;
        AppList.ItemsSource = items;

        int measured = State.Apps.Count(a => a.IsSizeMeasured);
        long total = State.Apps.Where(a => !nesting.Contained.Contains(a)).Sum(a => a.BestSize);
        SummaryText.Text = State.Apps.Count == 0 && State.AppsLoading ? "Reading installed apps…" :
            $"{SizeFormatter.FormatCount(State.Apps.Count)} apps  ·  {SizeFormatter.FormatCount(measured)} measured on disk  ·  {SizeFormatter.Format(total)} in total";

        var match = items.FirstOrDefault(i => i.App == keep);
        if (match is not null)
        {
            AppList.SelectedItem = match;
            AppList.ScrollIntoView(match);
            _pendingSelection = null;
        }
    }

    /// <summary>
    /// Finds apps installed inside another app's folder (games inside a launcher's library), so the
    /// container can be labelled and the total counts each folder once.
    /// </summary>
    private static (Dictionary<InstalledApp, int> Nested, HashSet<InstalledApp> Contained) CountNestedApps(IReadOnlyList<InstalledApp> apps)
    {
        var nested = new Dictionary<InstalledApp, int>();
        var contained = new HashSet<InstalledApp>();
        var located = apps.Where(a => a.IsSizeMeasured && a.InstallLocation is not null).ToList();
        foreach (var inner in located)
        {
            foreach (var outer in located)
            {
                if (outer != inner && PathUtil.IsStrictlyUnder(inner.InstallLocation!, outer.InstallLocation!))
                {
                    nested[outer] = nested.GetValueOrDefault(outer) + 1;
                    contained.Add(inner);
                }
            }
        }

        return (nested, contained);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Populate();

    private void OnSortChanged(object sender, SelectionChangedEventArgs e) => Populate();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await State.LoadAppsAsync(force: true, includeStore: true);

    private void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is AppItem item)
        {
            ShowDetails(item.App);
        }
    }

    private void ShowDetails(InstalledApp app)
    {
        _selected = app;
        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailsScroller.Visibility = Visibility.Visible;

        AppName.Text = app.Name;
        AppPublisher.Text = string.Join("  ·  ", new[] { app.Publisher, app.Version }.Where(s => !string.IsNullOrWhiteSpace(s)));

        MeasuredSize.Text = app.IsSizeMeasured ? SizeFormatter.Format(app.MeasuredSize!.Value + (app.MeasuredDataSize ?? 0)) : "—";
        MeasuredNote.Text = app.Measurement switch
        {
            MeasurementState.Measuring => "Measuring…",
            MeasurementState.Unavailable => "Install folder unknown or not readable",
            MeasurementState.NotMeasured => "Not measured yet",
            _ when app.MeasuredDataSize is > 0 => $"Includes {SizeFormatter.Format(app.MeasuredDataSize.Value)} of app data",
            _ => app.InstallLocationInferred ? "Folder inferred from the uninstaller location" : "",
        };
        ReportedSize.Text = app.ReportedSize is > 0 ? SizeFormatter.Format(app.ReportedSize.Value) : "Not reported";

        FactsGrid.Children.Clear();
        FactsGrid.RowDefinitions.Clear();
        AddFact("Source", app.SourceLabel);
        if (app.InstallDate is { } date)
        {
            AddFact("Installed", date.ToString("d"));
        }

        if (app.InstallLocation is not null)
        {
            AddFact("Location", app.InstallLocation);
        }

        if (app.DataLocation is not null)
        {
            AddFact("Data", app.DataLocation);
        }

        var command = UninstallCommand.ForApp(app, quiet: false, File.Exists);
        if (app.Source == AppSource.Msix)
        {
            AddFact("Removal", "Windows package manager");
        }
        else if (command is not null)
        {
            AddFact("Uninstaller", $"{command.FileName} {command.Arguments}".Trim());
        }

        UninstallButton.IsEnabled = app.CanUninstall && !State.IsScanning;
        ToolTipService.SetToolTip(UninstallButton, State.IsScanning ? "Available when the scan finishes" : app.CanUninstall ? null : "This app did not register an uninstaller");
        QuietUninstallButton.Visibility = app.CanQuietUninstall ? Visibility.Visible : Visibility.Collapsed;
        QuietUninstallButton.IsEnabled = !State.IsScanning;
        OpenFolderButton.Visibility = app.InstallLocation is not null && Directory.Exists(app.InstallLocation) ? Visibility.Visible : Visibility.Collapsed;
        OpenDataButton.Visibility = app.DataLocation is not null && Directory.Exists(app.DataLocation) ? Visibility.Visible : Visibility.Collapsed;
        ShowInFoldersButton.Visibility = FindInTree(app) > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private int FindInTree(InstalledApp app) =>
        app.InstallLocation is not null && State.Tree is { } tree ? tree.FindDirectory(app.InstallLocation) : -1;

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

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_selected?.InstallLocation is { } path)
        {
            ItemActions.OpenFolder(path);
        }
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        if (_selected?.DataLocation is { } path)
        {
            ItemActions.OpenFolder(path);
        }
    }

    private void OnShowInFolders(object sender, RoutedEventArgs e)
    {
        if (_selected?.InstallLocation is { } path)
        {
            State.RequestNavigation("folders", path);
        }
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_selected is { } app)
        {
            await UninstallAsync(app, quiet: false);
        }
    }

    private async void OnQuietUninstall(object sender, RoutedEventArgs e)
    {
        if (_selected is { } app)
        {
            await UninstallAsync(app, quiet: true);
        }
    }

    private async Task UninstallAsync(InstalledApp app, bool quiet)
    {
        if (State.IsScanning)
        {
            return;
        }

        // The uninstaller can run for minutes, and a rescan may replace the tree meanwhile.
        var tree = State.Tree;
        string how = app.Source == AppSource.Msix
            ? "Windows will remove this app package and its local data."
            : quiet
                ? "SpaceLens will run the program's quiet uninstaller. It runs without asking questions and uses the program's default removal options."
                : "SpaceLens will start the program's own uninstaller. Follow its instructions to finish.";

        var dialog = new ContentDialog
        {
            Title = $"Uninstall {app.Name}?",
            Content = new TextBlock
            {
                Text = how + (app.BestSize > 0 ? $"\n\nSize: {SizeFormatter.Format(app.BestSize)}" : ""),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = quiet ? "Run quiet uninstall" : "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await ItemActions.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        SetStatus(InfoBarSeverity.Informational, "Uninstalling", app.Source == AppSource.Msix ? "Removing the app package…" : "Waiting for the uninstaller to finish…");

        if (app.Source == AppSource.Msix)
        {
            var (success, error) = await MsixAppSource.RemoveAsync(app.PackageFullName!);
            if (!success)
            {
                SetStatus(InfoBarSeverity.Error, "The app was not removed", error ?? "Unknown error.");
                return;
            }
        }
        else
        {
            var launch = InstalledAppService.LaunchUninstaller(app, quiet);
            if (launch.Cancelled)
            {
                UninstallStatus.IsOpen = false;
                return;
            }

            if (!launch.Started)
            {
                SetStatus(InfoBarSeverity.Error, "The uninstaller could not be started", launch.Error ?? "Unknown error.");
                return;
            }

            if (launch.Process is { } process)
            {
                using (process)
                {
                    await process.WaitForExitAsync();
                }
            }
        }

        // Many uninstallers hand off to a copy of themselves and exit immediately, so keep checking for a while.
        var deadline = DateTime.UtcNow + RemovalWatchTime;
        bool stillInstalled = await Task.Run(() => InstalledAppService.IsStillInstalled(app));
        while (stillInstalled && app.Source != AppSource.Msix && DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000);
            stillInstalled = await Task.Run(() => InstalledAppService.IsStillInstalled(app));
        }

        if (stillInstalled)
        {
            SetStatus(InfoBarSeverity.Warning, "Still installed", "The app is still registered. If the uninstaller is still open, finish it and then select Refresh.");
            return;
        }

        OnUninstalled(app, tree);
    }

    private void OnUninstalled(InstalledApp app, ScanTree? tree)
    {
        int dir = app.InstallLocation is not null && tree is not null ? tree.FindDirectory(app.InstallLocation) : -1;
        string message = $"{app.Name} was uninstalled.";
        if (app.InstallLocation is { } location && Directory.Exists(location))
        {
            message += $" A folder remains at {location}. It may contain settings or data the program kept; review it in Folders if you want to remove it.";
        }
        else if (dir > 0)
        {
            State.RemoveFromTree(tree!, false, dir);
        }

        State.RemoveApp(app);
        _selected = null;
        NoSelectionText.Visibility = Visibility.Visible;
        DetailsScroller.Visibility = Visibility.Collapsed;
        _ = ItemActions.ShowMessageAsync("Uninstall complete", message);
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        UninstallStatus.Severity = severity;
        UninstallStatus.Title = title;
        UninstallStatus.Message = message;
        UninstallStatus.IsOpen = true;
    }
}
