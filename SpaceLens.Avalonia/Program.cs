using Avalonia;

namespace SpaceLens.Desktop;

internal static class Program
{
    /// <summary><c>SpaceLens [folder]</c>: a folder argument is scanned right away.</summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
