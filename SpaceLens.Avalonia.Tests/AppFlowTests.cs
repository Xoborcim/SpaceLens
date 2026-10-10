using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SpaceLens.Core.Models;
using SpaceLens.Desktop.ViewModels;
using SpaceLens.Desktop.Views;

namespace SpaceLens.Avalonia.Tests;

public class AppFlowTests
{
    private static async Task<(MainViewModel Main, TestPlatform Platform, TestDialogs Dialogs)> ScanAsync(FakeMacHome home)
    {
        var platform = new TestPlatform(home);
        var dialogs = new TestDialogs();
        var main = new MainViewModel(platform, dialogs);
        await main.InitializeAsync(null);
        main.StartScan(home.Root);
        await main.CurrentScan!;
        return (main, platform, dialogs);
    }

    /// <summary>Pages refresh asynchronously; wait (pumping the UI thread) until a condition holds.</summary>
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (int i = 0; i < 250 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.True(condition(), $"Timed out waiting for {what}");
    }

    [AvaloniaFact]
    public async Task A_scan_fills_the_overview_folders_large_files_search_and_findings()
    {
        using var home = new FakeMacHome();
        var (main, _, _) = await ScanAsync(home);

        Assert.Equal(ScanState.Completed, main.State);
        Assert.Equal(0, main.ErrorCount);
        Assert.True(main.Tree!.Root.TotalSize > 95_000_000);

        // Detectors: Xcode build data, device backups and the project's node_modules.
        Assert.Contains(main.Findings, f => f.Title == "Xcode build data (DerivedData)" && f.AllowDirectRemoval);
        Assert.Contains(main.Findings, f => f.Title == "iPhone and iPad backups" && !f.AllowDirectRemoval);
        Assert.Contains(main.Findings, f => f.Group == "node_modules");

        // Overview: locations, largest items, opportunities; sizes in Finder's decimal units.
        await WaitFor(() => main.Overview.Largest.Count > 0, "the largest items");
        Assert.Contains(main.Overview.Categories, c => c.Name == "Videos" && c.SizeText == "40.0 MB");
        Assert.Contains(main.Overview.Categories, c => c.Name == "Developer files");
        Assert.Contains(main.Overview.Opportunities, o => o.Title == "Xcode build data (DerivedData)");
        Assert.NotEmpty(main.Overview.Map);

        // Folders: drill into the home folder.
        main.ShowInFolders(main.Tree.FindDirectory(home.Home));
        Assert.Same(main.Folders, main.CurrentPage);
        Assert.Equal(["Movies", "Library", "Downloads", "Projects", "Documents"], main.Folders.Items.Take(5).Select(i => i.Name));
        Assert.Equal("Library", main.Folders.Items.Single(i => i.Name == "Library").Name);
        Assert.False(main.Folders.Items.Single(i => i.Name == "Library").CanTrash); // ~/Library is protected
        Assert.True(main.Folders.Items.Single(i => i.Name == "Projects").CanTrash);
        main.Folders.UpCommand.Execute(null);
        Assert.Equal(main.Tree.FindDirectory(Path.Combine(home.Root, "Users")), main.Folders.CurrentIndex);

        // Large files, with the smallest minimum (10 MB).
        main.CurrentPage = main.LargeFiles;
        main.LargeFiles.SelectedSize = main.LargeFiles.SizeChoices[0];
        await WaitFor(() => main.LargeFiles.Items.Count > 0, "large files");
        Assert.Equal("Holiday.mov", main.LargeFiles.Items[0].Name);
        Assert.Contains(main.LargeFiles.Items, i => i.Name == "Installer.dmg" && i.SizeText == "25.0 MB");

        // Search, including the newer syntax.
        main.CurrentPage = main.Search;
        main.Search.Query = ".dmg OR .mov";
        await main.Search.RunNowAsync();
        Assert.Equal(["Holiday.mov", "Installer.dmg"], main.Search.Items.Select(i => i.Name));
        main.Search.Query = ">20MB -path:Movies";
        await main.Search.RunNowAsync();
        Assert.DoesNotContain(main.Search.Items, i => i.Name == "Holiday.mov");
    }

    [AvaloniaFact]
    public async Task Moving_to_the_trash_confirms_first_and_updates_the_results()
    {
        using var home = new FakeMacHome();
        var (main, platform, dialogs) = await ScanAsync(home);
        main.CurrentPage = main.Search;
        main.Search.Query = "Installer.dmg";
        await main.Search.RunNowAsync();
        var dmg = Assert.Single(main.Search.Items);
        long before = main.Tree!.Root.TotalSize;

        // Cancel: nothing happens.
        dialogs.Answer = false;
        Assert.False(await main.MoveToTrashAsync([dmg]));
        Assert.True(File.Exists(dmg.Path));

        // Confirm: the file goes and the totals follow.
        dialogs.Answer = true;
        Assert.True(await main.MoveToTrashAsync([dmg]));
        Assert.False(File.Exists(dmg.Path));
        Assert.Equal([dmg.Path], platform.Trashed);
        var confirmation = dialogs.Confirmations[^1];
        Assert.Contains("Installer.dmg", confirmation.Title);
        Assert.Contains("25.0 MB", confirmation.Message);
        Assert.Equal(before - dmg.Size, main.Tree.Root.TotalSize);
        // The page refreshes itself (possibly more than once, as the analysis catches up).
        await WaitFor(() => main.Search.Items.Count == 0, "the search to drop the trashed file");
    }

    [AvaloniaFact]
    public async Task Protected_and_app_owned_items_are_never_moved()
    {
        using var home = new FakeMacHome();
        var (main, platform, dialogs) = await ScanAsync(home);
        var tree = main.Tree!;

        var library = ItemViewModel.ForFolder(main, tree, tree.FindDirectory(Path.Combine(home.Home, "Library")));
        Assert.False(await main.MoveToTrashAsync([library]));
        Assert.Contains(dialogs.Messages, m => m.Message.Contains("home folder", StringComparison.Ordinal));

        var backup = ItemViewModel.ForFolder(main, tree, tree.FindDirectory(Path.Combine(home.Home, "Library/Application Support/MobileSync/Backup")));
        Assert.False(backup.CanTrash);
        Assert.False(await main.MoveToTrashAsync([backup]));
        Assert.Contains(dialogs.Messages, m => m.Message.Contains("Manage Backups", StringComparison.Ordinal));

        Assert.Empty(platform.Trashed);
        Assert.Empty(dialogs.Confirmations);
    }

    [AvaloniaFact]
    public async Task Rescanning_a_folder_picks_up_changes()
    {
        using var home = new FakeMacHome();
        var (main, _, _) = await ScanAsync(home);
        var tree = main.Tree!;
        home.File("Downloads/Another.zip", 8_000_000);

        var downloads = ItemViewModel.ForFolder(main, tree, tree.FindDirectory(Path.Combine(home.Home, "Downloads")));
        await main.RescanFolderAsync(downloads);

        // Files of 1 MB or more count by the space they use on disk, which rounds up to whole blocks.
        int index = tree.FindDirectory(Path.Combine(home.Home, "Downloads"));
        Assert.InRange(tree.Dir(index).TotalSize, 25_000_000 + 2_000 + 8_000_000, 25_000_000 + 2_000 + 8_000_000 + 64 * 1024);
    }

    [AvaloniaFact]
    public async Task Apps_list_system_apps_as_not_removable()
    {
        using var home = new FakeMacHome();
        var (main, _, dialogs) = await ScanAsync(home);
        main.CurrentPage = main.Apps;
        await WaitFor(() => main.Apps.Items.Count == 2, "the apps");
        var safari = main.Apps.Items.Single(a => a.Name == "Safari");
        Assert.False(safari.CanRemove);
        var editor = main.Apps.Items.Single(a => a.Name == "Example Editor");
        Assert.True(editor.CanRemove);
        Assert.Contains("App Store", editor.Details);
        await WaitFor(() => editor.SizeText != "…" && safari.SizeText != "…", "the app sizes");
        Assert.Equal("4.00 MB", editor.SizeText);
        Assert.Equal("—", safari.SizeText); // not on this disk

        await main.Apps.RemoveAsync(editor);
        Assert.Contains(dialogs.Confirmations, c => c.Title == "Move Example Editor to the Trash?");
        Assert.DoesNotContain(main.Apps.Items, a => a.Name == "Example Editor");
    }

    [AvaloniaFact]
    public async Task Saved_scans_come_back_at_startup()
    {
        using var home = new FakeMacHome();
        var (first, platform, _) = await ScanAsync(home);
        long size = first.Tree!.Root.TotalSize;

        var second = new MainViewModel(platform, new TestDialogs());
        await second.InitializeAsync(null);
        Assert.Equal(ScanState.Snapshot, second.State);
        Assert.Equal(size, second.Tree!.Root.TotalSize);
        Assert.StartsWith("Last scanned", second.StatusTitle);
    }

    [AvaloniaFact]
    public async Task Every_page_renders()
    {
        using var home = new FakeMacHome();
        var (main, _, _) = await ScanAsync(home);
        var window = new MainWindow { DataContext = main, Width = 1240, Height = 820 };
        window.Show();

        string folder = Environment.GetEnvironmentVariable("SPACELENS_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "SpaceLensScreenshots");
        Directory.CreateDirectory(folder);

        main.ShowInFolders(main.Tree!.FindDirectory(home.Home));
        main.LargeFiles.SelectedSize = main.LargeFiles.SizeChoices[0];
        main.Search.Query = ".dmg OR .mov OR node_modules";
        await main.Search.RunNowAsync();

        foreach (var page in main.Pages)
        {
            main.CurrentPage = page;
            await WaitFor(() => page switch
            {
                OverviewViewModel o => o.Largest.Count > 0,
                LargeFilesViewModel l => l.Items.Count > 0,
                AppsViewModel a => a.Items.Count > 0,
                _ => true,
            }, page.Title);
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(1240, frame!.PixelSize.Width);
            frame.Save(Path.Combine(folder, $"{page.Title.Replace(' ', '-')}.png"), new global::Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        window.Close();
    }
}
