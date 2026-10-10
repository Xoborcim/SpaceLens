using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SpaceLens.Core.Models;
using SpaceLens.Desktop.Platform;
using SpaceLens.Desktop.ViewModels;
using SpaceLens.Desktop.Views;

namespace SpaceLens.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var main = new MainViewModel(new MacDesktopPlatform(), window);
            window.DataContext = main;
            desktop.MainWindow = window;

            string? folder = desktop.Args is { Length: > 0 } args ? PathUtil.CleanCommandLinePath(args[0]) : null;
            window.Opened += async (_, _) => await main.InitializeAsync(folder);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
