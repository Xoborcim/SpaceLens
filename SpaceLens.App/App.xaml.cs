using Microsoft.UI.Xaml;
using SpaceLens.App.Services;

namespace SpaceLens.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static MainWindow? Window { get; private set; }

    /// <summary><c>SpaceLens.exe [folder]</c> — a folder argument is scanned immediately.</summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        string? argument = commandLine.Length > 1 ? Core.Models.PathUtil.CleanCommandLinePath(commandLine[1]) : null;
        string? startupFolder = argument is not null && Directory.Exists(argument) ? argument : null;
        Window = new MainWindow(startupFolder);
        Window.Activate();
    }

    /// <summary>
    /// Errors are written to a local log only. SpaceLens never uploads crash data; file system paths
    /// are not included in the message shown to the user.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDirectory);
            File.AppendAllText(Path.Combine(SettingsService.DataDirectory, "errors.log"), $"{DateTime.Now:O} {e.Exception}\n\n");
        }
        catch (IOException)
        {
        }

        e.Handled = true;
        _ = ItemActions.ShowMessageAsync("Something went wrong", "SpaceLens hit an unexpected error but kept running. Details were written to errors.log in %LOCALAPPDATA%\\SpaceLens.");
    }
}
