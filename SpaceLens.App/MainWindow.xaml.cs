using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SpaceLens.App.Services;
using SpaceLens.App.Views;

namespace SpaceLens.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["overview"] = typeof(OverviewPage),
        ["apps"] = typeof(AppsPage),
        ["folders"] = typeof(FoldersPage),
        ["largefiles"] = typeof(LargeFilesPage),
        ["changes"] = typeof(ChangesPage),
        ["duplicates"] = typeof(DuplicatesPage),
        ["basket"] = typeof(BasketPage),
        ["filetypes"] = typeof(FileTypesPage),
        ["storage"] = typeof(StoragePage),
        ["developer"] = typeof(DevFilesPage),
        ["errors"] = typeof(ErrorsPage),
        ["search"] = typeof(SearchPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly string? _startupFolder;
    private bool _suppressNavSelection;

    public MainWindow(string? startupFolder = null)
    {
        _startupFolder = startupFolder;
        State = new AppState(DispatcherQueue.GetForCurrentThread());
        InitializeComponent();

        State.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "SpaceLens.ico"));

        double scale = GetDpiForWindow(State.WindowHandle) / 96.0;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(1320 * scale), (int)(860 * scale)));

        ApplyTheme(State.Settings.Theme);
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtons();

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(180);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => RunSearch(SearchBox.Text);

        State.PropertyChanged += OnStatePropertyChanged;
        State.NavigationRequested += (_, request) =>
        {
            // A saved search was run: show its text in the search box too.
            if (request.Page == "search" && request.Parameter is string query)
            {
                SearchBox.Text = query;
            }

            Navigate(request.Page, request.Parameter);
        };
        State.TreeReplaced += (_, _) => UpdateChrome();
        State.ScanFinished += (_, _) => UpdateChrome();
        State.BasketChanged += (_, _) => UpdateChrome();

        RootGrid.Loaded += OnLoaded;
    }

    public AppState State { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        State.XamlRoot = RootGrid.XamlRoot;
        Navigate("overview");
        State.StartupWindowMs = AppState.MillisecondsSinceProcessStart();

        // Everything else is deferred until after the first frame is on screen.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (_startupFolder is not null)
            {
                State.StartScan(_startupFolder);
                Navigate("folders");
            }

            await State.InitializeAsync();
        });
    }

    public void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionButtons();
    }

    private void UpdateCaptionButtons()
    {
        var titleBar = AppWindow.TitleBar;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? global::Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : global::Windows.UI.Color.FromArgb(0x20, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.State) or nameof(AppState.ErrorCount))
        {
            UpdateChrome();
        }
    }

    private void UpdateChrome()
    {
        RescanButton.Visibility = State.HasResults && !State.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = RescanButton.Visibility;
        IssuesNavItem.Visibility = State.ErrorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        IssuesBadge.Value = State.ErrorCount;
        BasketBadge.Value = State.Basket.Count;
        BasketBadge.Visibility = State.Basket.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------------------------------------
    // Navigation
    // ---------------------------------------------------------------------------------------------

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection)
        {
            return;
        }

        string? tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag is not null)
        {
            Navigate(tag, null, fromNavView: true);
        }
    }

    public void Navigate(string tag, object? parameter = null, bool fromNavView = false)
    {
        if (!Pages.TryGetValue(tag, out var pageType))
        {
            return;
        }

        if (!fromNavView)
        {
            _suppressNavSelection = true;
            Nav.SelectedItem = tag == "settings" ? Nav.SettingsItem :
                Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
            _suppressNavSelection = false;
        }

        if (ContentFrame.CurrentSourcePageType == pageType && parameter is null && tag != "search")
        {
            return;
        }

        ContentFrame.Navigate(pageType, parameter, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
    }

    // ---------------------------------------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------------------------------------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce.Stop();
        RunSearch(args.QueryText);
    }

    private void RunSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            if (ContentFrame.Content is SearchPage)
            {
                Navigate("overview");
            }

            return;
        }

        if (ContentFrame.Content is SearchPage page)
        {
            page.Search(query);
        }
        else
        {
            Nav.SelectedItem = null;
            ContentFrame.Navigate(typeof(SearchPage), query, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------------------------------

    private void OnPauseClick(object sender, RoutedEventArgs e) => State.TogglePause();

    private void OnCancelClick(object sender, RoutedEventArgs e) => State.CancelScan();

    private void OnRescanClick(object sender, RoutedEventArgs e) => State.Rescan();

    private void OnRescanAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!State.IsScanning)
        {
            State.Rescan();
        }
    }

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!Nav.IsPaneOpen)
        {
            Nav.IsPaneOpen = true;
        }

        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void OnScanFolderAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndScanFolderAsync();
    }

    public static async Task PickAndScanFolderAsync()
    {
        var state = AppState.Current;
        if (state.IsScanning)
        {
            return;
        }

        var picker = new global::Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, state.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            state.StartScan(folder.Path);
            state.RequestNavigation("folders");
        }
    }

    /// <summary>Saves folders (CSV), large files (CSV) or a full report (JSON) to a file the user picks.</summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var tree = State.Tree;
        if (tree is null || State.IsScanning || (sender as FrameworkElement)?.Tag is not string kind)
        {
            return;
        }

        string root = new string(tree.RootPath.Where(char.IsLetterOrDigit).ToArray());
        var picker = new global::Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"SpaceLens {root} {kind} {DateTime.Now:yyyy-MM-dd}",
        };
        if (kind == "json")
        {
            picker.FileTypeChoices.Add("JSON", [".json"]);
        }
        else
        {
            picker.FileTypeChoices.Add("CSV (spreadsheet)", [".csv"]);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, State.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        string path = file.Path;
        try
        {
            await Task.Run(() =>
            {
                // Removals on the UI thread take this lock; the export reads a consistent tree.
                lock (tree.SyncRoot)
                {
                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    if (kind == "json")
                    {
                        Core.Export.ReportExporter.WriteJson(tree, stream);
                        return;
                    }

                    // UTF-8 with a byte order mark so Excel detects the encoding of non-ASCII names.
                    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    if (kind == "folders")
                    {
                        Core.Export.ReportExporter.WriteFoldersCsv(tree, writer);
                    }
                    else
                    {
                        Core.Export.ReportExporter.WriteFilesCsv(tree, writer);
                    }
                }
            });
            State.StatusDetail = $"Exported to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ItemActions.ShowMessageAsync("The export could not be saved", ex.Message);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
