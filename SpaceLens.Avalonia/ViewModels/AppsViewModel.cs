using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceLens.Core.Models;
using SpaceLens.Core.Scanning;
using SpaceLens.Desktop.Services;
using SpaceLens.Mac;

namespace SpaceLens.Desktop.ViewModels;

/// <summary>An installed application row.</summary>
public sealed partial class AppRow(AppsViewModel page, MacApp app) : ObservableObject
{
    public MacApp App { get; } = app;

    public string Name => App.Name;

    public string Details => string.Join("  ·  ", new[] { App.Version is { } v ? $"Version {v}" : null, App.IsAppStore ? "App Store" : null, App.IsSystem ? "Part of macOS" : null, App.BundlePath }
        .Where(s => s is not null));

    [ObservableProperty]
    public partial string SizeText { get; set; } = "…";

    [ObservableProperty]
    public partial string LeftoverText { get; set; } = "";

    public bool CanRemove => !App.IsSystem;

    [RelayCommand]
    private Task Remove() => page.RemoveAsync(this);

    [RelayCommand]
    private void Reveal() => page.Main.Platform.Reveal(App.BundlePath);
}

/// <summary>
/// Installed applications. Removing one moves its bundle to the Trash (the macOS way); what it kept in the
/// Library is listed afterwards and only moved to the Trash if the person asks for it.
/// </summary>
public sealed partial class AppsViewModel : PageViewModel
{
    private bool _loaded;
    private int _version;

    public AppsViewModel(MainViewModel main)
        : base(main, "Apps", "▦")
    {
    }

    public ObservableCollection<AppRow> Items { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = "Loading applications…";

    [RelayCommand]
    private void Reload()
    {
        _loaded = false;
        Refresh();
    }

    protected override async void OnRefresh()
    {
        if (_loaded)
        {
            MeasureFromTree();
            return;
        }

        _loaded = true;
        int version = ++_version;
        var apps = await Task.Run(() => Main.Platform.FindApps(CancellationToken.None));
        if (version != _version)
        {
            return;
        }

        Items.Clear();
        foreach (var app in apps)
        {
            Items.Add(new AppRow(this, app));
        }

        Summary = $"{apps.Count} applications";
        MeasureFromTree();
        await MeasureRemainingAsync(version);
    }

    /// <summary>Uses the scan for bundles it covers.</summary>
    private void MeasureFromTree()
    {
        if (Main.Tree is not { } tree)
        {
            return;
        }

        foreach (var row in Items.Where(r => r.App.Size is null))
        {
            int index = tree.FindDirectory(row.App.BundlePath);
            if (index > 0)
            {
                row.App.Size = tree.Dir(index).TotalSize;
                row.SizeText = Main.FormatSize(row.App.Size.Value);
            }
        }
    }

    /// <summary>Measures the other bundles (outside the scanned folder) with a small scan each.</summary>
    private async Task MeasureRemainingAsync(int version)
    {
        foreach (var row in Items.ToList())
        {
            if (version != _version)
            {
                return;
            }

            if (row.App.Size is null && Directory.Exists(row.App.BundlePath))
            {
                var tree = new ScanTree(row.App.BundlePath, long.MaxValue);
                var result = await new ParallelDirectoryScanner(Main.Platform.CreateEnumeratorFactory())
                    .ScanAsync(tree, new ScanOptions { MaxParallelism = 2 }, null, CancellationToken.None);
                row.App.Size = result.Bytes;
            }

            row.SizeText = row.App.Size is { } size ? Main.FormatSize(size) : "—";
            row.App.Leftovers = await Task.Run(() => Main.Platform.FindLeftovers(row.App));
            row.LeftoverText = row.App.Leftovers.Count == 0 ? "" : $"{row.App.Leftovers.Count} items in your Library";
        }
    }

    public async Task RemoveAsync(AppRow row)
    {
        var app = row.App;
        if (app.IsSystem || Main.IsScanning)
        {
            return;
        }

        var request = new ConfirmRequest(
            $"Move {app.Name} to the Trash?",
            $"{app.BundlePath}" + (app.Size is { } size ? $"\n{Main.FormatSize(size)}" : ""),
            "Move to Trash",
            app.IsAppStore ? [new DialogWarning("App Store app", "You can download it again from the App Store at any time.")] : [],
            "Quit the application first. Its settings and data stay in your Library; SpaceLens lists them next.");
        if (!await Main.Dialogs.ConfirmAsync(request))
        {
            return;
        }

        var result = await Task.Run(() => Main.Platform.RemoveApp(app));
        if (!result.Success)
        {
            await Main.Dialogs.ShowMessageAsync($"{app.Name} was not removed", result.Error ?? "Unknown error.");
            return;
        }

        Items.Remove(row);
        Summary = $"{Items.Count} applications  ·  {app.Name} moved to the Trash";
        if (Main.Tree is { } tree && tree.FindDirectory(app.BundlePath) is > 0 and var index)
        {
            Main.RemoveFromTree(tree, [ItemViewModel.ForFolder(Main, tree, index)]);
        }

        var leftovers = await Task.Run(() => Main.Platform.FindLeftovers(app));
        if (leftovers.Count > 0)
        {
            await OfferLeftoversAsync(app, leftovers);
        }
    }

    /// <summary>Lists what the app left behind and moves it to the Trash only when the person confirms.</summary>
    private async Task OfferLeftoversAsync(MacApp app, List<MacLeftover> leftovers)
    {
        var request = new ConfirmRequest(
            $"{app.Name} left data in your Library",
            string.Join("\n", leftovers.Select(l => $"{l.Kind}: {l.Path}")),
            "Move these to Trash",
            [new DialogWarning("Settings and data", "This includes the app's settings and any data it kept. Keep them if you might install the app again.")],
            "Nothing is removed unless you choose to.");
        if (!await Main.Dialogs.ConfirmAsync(request))
        {
            return;
        }

        var results = await Task.Run(() => Main.Platform.MoveToTrash(leftovers.Select(l => (l.Path, !l.IsFile)).ToList()));
        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
        {
            await Main.Dialogs.ShowMessageAsync("Some items were not moved", string.Join("\n", failed.Select(f => f.Error ?? f.Path)));
        }
    }
}
