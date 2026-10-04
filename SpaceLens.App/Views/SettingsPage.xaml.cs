using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SpaceLens.App.Services;
using SpaceLens.Core.Formatting;
using SpaceLens.Windows.FileSystem;
using SpaceLens.Windows.Shell;

namespace SpaceLens.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private static AppState State => AppState.Current;

    private static AppSettings Settings => State.Settings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;
        ThemeChoice.SelectedIndex = (int)Settings.Theme;
        DevToggle.IsOn = Settings.DetectDeveloperFiles;
        StoreToggle.IsOn = Settings.IncludeStoreApps;
        RememberToggle.IsOn = Settings.RememberScans;
        EngineChoice.SelectedItem = EngineChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Settings.Engine.ToString());
        WorkersBox.Value = Settings.Workers;
        ExplorerToggle.IsOn = Environment.ProcessPath is { } exe && ExplorerIntegration.IsRegistered(exe);
        _loading = false;

        UpdateSnapshotSize();
        ShowDiagnostics();
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"SpaceLens {version?.ToString(3)}  ·  .NET {Environment.Version}  ·  {(DriveService.IsAdministrator() ? "running as administrator" : "standard user")}";
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeChoice.SelectedItem is not RadioButton { Tag: string tag } || !Enum.TryParse<AppTheme>(tag, out var theme))
        {
            return;
        }

        Settings.Theme = theme;
        State.ApplySettings();
        App.Window?.ApplyTheme(theme);
    }

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool devChanged = Settings.DetectDeveloperFiles != DevToggle.IsOn;
        bool storeChanged = Settings.IncludeStoreApps != StoreToggle.IsOn;
        Settings.DetectDeveloperFiles = DevToggle.IsOn;
        Settings.IncludeStoreApps = StoreToggle.IsOn;
        Settings.RememberScans = RememberToggle.IsOn;
        State.ApplySettings();

        if (devChanged && State.Tree is { } tree && !State.IsScanning)
        {
            _ = State.AnalyzeAsync(tree);
        }

        if (storeChanged)
        {
            _ = State.LoadAppsAsync(force: true, includeStore: StoreToggle.IsOn);
        }
    }

    private async void OnExplorerToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            if (ExplorerToggle.IsOn)
            {
                ExplorerIntegration.Register(exe);
            }
            else
            {
                ExplorerIntegration.Unregister();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _loading = true;
            ExplorerToggle.IsOn = ExplorerIntegration.IsRegistered(exe);
            _loading = false;
            await ItemActions.ShowMessageAsync("File Explorer was not changed", ex.Message);
        }
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && EngineChoice.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<ScanEngine>(tag, out var engine))
        {
            Settings.Engine = engine;
            State.ApplySettings();
        }
    }

    private void OnWorkersChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && !double.IsNaN(args.NewValue))
        {
            Settings.Workers = (int)Math.Clamp(args.NewValue, 0, 64);
            State.ApplySettings();
        }
    }

    private async void OnClearSnapshots(object sender, RoutedEventArgs e)
    {
        await Task.Run(SnapshotStore.DeleteAll);
        UpdateSnapshotSize();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsService.DataDirectory);
        ItemActions.OpenFolder(SettingsService.DataDirectory);
    }

    private void UpdateSnapshotSize()
    {
        long size = SnapshotStore.TotalSize();
        SnapshotSizeText.Text = size > 0 ? $"{SizeFormatter.Format(size)} used" : "No saved scans";
        ClearSnapshotsButton.IsEnabled = size > 0;
    }

    private IEnumerable<(string Label, string Value)> DiagnosticRows()
    {
        yield return ("Startup", $"window ready after {State.StartupWindowMs:N0} ms" +
            (State.StartupSnapshotMs > 0 ? $", saved results shown after {State.StartupSnapshotMs:N0} ms (reading the snapshot took {State.SnapshotLoadTime.TotalMilliseconds:N0} ms)" : ""));
        yield return ("Current working set", SizeFormatter.Format(Environment.WorkingSet));
        var gc = GC.GetGCMemoryInfo();
        yield return ("Managed heap", $"{SizeFormatter.Format(gc.HeapSizeBytes)} in use, {SizeFormatter.Format(gc.TotalCommittedBytes)} committed");

        var d = State.LastDiagnostics;
        if (d is null)
        {
            yield return ("Last scan", State.Tree?.Metadata.LoadedFromSnapshot == true ? "Loaded from a saved scan (run a scan to measure)" : "No scan in this session");
            yield break;
        }

        yield return ("Scanner", $"{d.Engine}, {d.Workers} workers");
        yield return ("Duration", $"{d.Duration.TotalSeconds:0.00} s");
        yield return ("Files", $"{SizeFormatter.FormatCount(d.Files)}  ({d.FilesPerSecond:N0} per second)");
        yield return ("Folders", $"{SizeFormatter.FormatCount(d.Folders)}  ({d.FoldersPerSecond:N0} per second)");
        yield return ("Data analyzed", $"{SizeFormatter.Format(d.Bytes)}  ({SizeFormatter.Format((long)d.BytesPerSecond)} per second)");
        yield return ("Indexed files", $"{SizeFormatter.FormatCount(d.IndexedFiles)} (files of 1 MB or more)");
        yield return ("Tree records", SizeFormatter.Format(d.RecordBytes));
        yield return ("Peak working set", SizeFormatter.Format(d.PeakWorkingSet));
        yield return ("Allocated during scan", SizeFormatter.Format(d.AllocatedBytes));
        yield return ("Garbage collections", $"gen0 {d.Gen0}, gen1 {d.Gen1}, gen2 {d.Gen2}");
        yield return ("UI progress updates", $"{d.UiRefreshRate:0.0} per second");
    }

    private void ShowDiagnostics()
    {
        DiagnosticsGrid.Children.Clear();
        DiagnosticsGrid.RowDefinitions.Clear();
        foreach (var (label, value) in DiagnosticRows())
        {
            int row = DiagnosticsGrid.RowDefinitions.Count;
            DiagnosticsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };
            var v = new TextBlock { Text = value, IsTextSelectionEnabled = true };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            DiagnosticsGrid.Children.Add(l);
            DiagnosticsGrid.Children.Add(v);
        }
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var (label, value) in DiagnosticRows())
        {
            text.Append(label).Append(": ").AppendLine(value);
        }

        ItemActions.CopyText(text.ToString());
    }
}
