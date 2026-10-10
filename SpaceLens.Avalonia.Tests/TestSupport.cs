using Avalonia;
using Avalonia.Headless;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;
using SpaceLens.Core.Scanning;
using SpaceLens.Desktop;
using SpaceLens.Desktop.Platform;
using SpaceLens.Desktop.Services;
using SpaceLens.Mac;

[assembly: AvaloniaTestApplication(typeof(SpaceLens.Avalonia.Tests.TestAppBuilder))]

namespace SpaceLens.Avalonia.Tests;

public static class TestAppBuilder
{
    /// <summary>Headless platform with real Skia rendering, so windows can be captured as images.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>A home folder laid out like a Mac's, in a temporary directory.</summary>
public sealed class FakeMacHome : IDisposable
{
    public FakeMacHome()
    {
        // Not under the temp folder: macOS keeps /tmp and /private to itself, and the safety policy says so.
        Root = Path.Combine(AppContext.BaseDirectory, "fake-disks", Guid.NewGuid().ToString("N")[..10]);
        Home = Path.Combine(Root, "Users", "me");
        File("Library/Developer/Xcode/DerivedData/App-abc/Build/Intermediates.bin", 12_000_000);
        File("Library/Caches/com.example.browser/cache.db", 6_000_000);
        File("Library/Application Support/MobileSync/Backup/0001/Manifest.db", 11_000_000);
        File("Downloads/Installer.dmg", 25_000_000);
        File("Downloads/notes.txt", 2_000);
        File("Movies/Holiday.mov", 40_000_000);
        File("Projects/web/package.json", 200);
        File("Projects/web/node_modules/react/index.js", 3_000_000);
        File("Projects/web/src/app.js", 5_000);
        File("Documents/Report.pdf", 1_500_000);
        AppFile("Applications/Editor.app/Contents/MacOS/Editor", 4_000_000);
        DataDirectory = Path.Combine(Root, "data");
    }

    public string Root { get; }

    public string Home { get; }

    public string DataDirectory { get; }

    public string File(string relative, long size) => Write(Path.Combine(Home, relative), size);

    /// <summary>A file relative to the fake disk's root rather than the home folder.</summary>
    public string AppFile(string relative, long size) => Write(Path.Combine(Root, relative), size);

    private static string Write(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create);
        var buffer = new byte[Math.Min(size, 1 << 20)];
        Random.Shared.NextBytes(buffer);
        for (long left = size; left > 0; left -= buffer.Length)
        {
            stream.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// The macOS platform pointed at a fake home folder, with a fake disk, and a Trash that really deletes
/// (so removals can be tested on any OS). Everything else is the real Mac implementation.
/// </summary>
public sealed class TestPlatform(FakeMacHome home) : IDesktopPlatform
{
    private readonly MacDesktopPlatform _mac = new(new MacKnownLocations { Home = home.Home, UsersDirectory = Path.Combine(home.Root, "Users") }, home.DataDirectory);

    public List<string> Trashed { get; } = [];

    public string Home => _mac.Home;

    public string DataDirectory => _mac.DataDirectory;

    public SizeUnits Units => SizeUnits.Decimal;

    public string TrashName => "Trash";

    public IItemSafetyPolicy Safety => _mac.Safety;

    public IReadOnlyList<VolumeInfo> GetVolumes() => [new VolumeInfo(home.Root, "Macintosh HD", 500_000_000_000, 180_000_000_000, true, false)];

    public IDirectoryEnumeratorFactory CreateEnumeratorFactory() => _mac.CreateEnumeratorFactory();

    public IReadOnlyCollection<string> ExcludedPathsFor(string root) => [];

    public List<StorageFinding> Detect(ScanTree tree, CancellationToken cancellationToken) => _mac.Detect(tree, cancellationToken);

    public Dictionary<int, LocationCategory> BuildLocationMap(ScanTree tree) => _mac.BuildLocationMap(tree);

    public string Label(LocationCategory category) => _mac.Label(category);

    public IReadOnlyList<TrashResult> MoveToTrash(IReadOnlyList<(string Path, bool IsDirectory)> items)
    {
        var results = new List<TrashResult>();
        foreach (var (path, isDirectory) in items)
        {
            var assessment = isDirectory ? Safety.AssessDirectory(path) : Safety.AssessFile(path);
            if (!assessment.CanDelete)
            {
                results.Add(new TrashResult(path, false, "protected"));
                continue;
            }

            if (isDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                System.IO.File.Delete(path);
            }

            Trashed.Add(path);
            results.Add(new TrashResult(path, true, null));
        }

        return results;
    }

    public void Reveal(string path)
    {
    }

    public void Open(string path)
    {
    }

    public void OpenUrl(string url)
    {
    }

    public bool? HasFullDiskAccess() => false;

    public string? FullDiskAccessSettingsUrl => MacShell.FullDiskAccessSettingsUrl;

    public List<MacApp> FindApps(CancellationToken cancellationToken) =>
    [
        new MacApp { Name = "Example Editor", BundlePath = Path.Combine(home.Root, "Applications", "Editor.app"), BundleId = "com.example.Editor", Version = "2.4.1", IsAppStore = true },
        new MacApp { Name = "Safari", BundlePath = "/Applications/Safari.app", BundleId = "com.apple.Safari", Version = "18.0", IsSystem = true },
    ];

    public List<MacLeftover> FindLeftovers(MacApp app) => [];

    public TrashResult RemoveApp(MacApp app) => new(app.BundlePath, !app.IsSystem, app.IsSystem ? "part of macOS" : null);
}

/// <summary>Answers dialogs without showing them and records what was asked.</summary>
public sealed class TestDialogs : IDialogService
{
    public bool Answer { get; set; } = true;

    public List<ConfirmRequest> Confirmations { get; } = [];

    public List<(string Title, string Message)> Messages { get; } = [];

    public string? Clipboard { get; private set; }

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Confirmations.Add(request);
        return Task.FromResult(Answer);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public Task CopyTextAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }
}
