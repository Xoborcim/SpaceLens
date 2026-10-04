using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Safety;
using SpaceLens.Windows.FileSystem;
using SpaceLens.Windows.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace SpaceLens.App.Services;

/// <summary>Actions available on files and folders, shared by all pages, plus the context menu builder.</summary>
public static class ItemActions
{
    private static AppState State => AppState.Current;

    // A removal is waiting on a dialog or on the shell. A second Delete press would otherwise open a
    // second confirmation for the same items.
    private static bool _removalInProgress;

    public static void Open(EntryItem item)
    {
        if (item.Kind == EntryKind.File)
        {
            Report(ShellActions.Open(item.Path));
        }
        else
        {
            Report(ShellActions.OpenInExplorer(item.Path, select: false));
        }
    }

    public static void OpenInExplorer(EntryItem item) =>
        Report(ShellActions.OpenInExplorer(item.Path, select: item.Kind != EntryKind.LooseFiles));

    public static void OpenFolder(string path) => Report(ShellActions.OpenInExplorer(path, select: false));

    public static void CopyPath(EntryItem item) => CopyText(item.Path);

    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public static void ShowProperties(EntryItem item) => Report(ShellActions.ShowProperties(item.Path, State.WindowHandle));

    public static void ScanFolder(EntryItem item)
    {
        if (!State.IsScanning)
        {
            State.StartScan(item.Kind == EntryKind.File ? item.ParentPath : item.Path);
            State.RequestNavigation("folders");
        }
    }

    public static bool CanRecycle(EntryItem item) =>
        State.CanModify && item.Kind is EntryKind.Directory or EntryKind.File && IsRemovable(item) && !item.IsReparsePoint && !IsScanRoot(item);

    private static bool IsScanRoot(EntryItem item) => item.Kind == EntryKind.Directory && item.Index == Core.Models.ScanTree.RootIndex;

    /// <summary>Removal is refused for protected locations and for locations that must be removed by their owner.</summary>
    public static bool IsRemovable(EntryItem item) => item.Safety.CanDelete && !item.IsManagedElsewhere;

    /// <summary>Moves items to the Recycle Bin after an explicit confirmation that shows sizes and any warnings.</summary>
    public static async Task<bool> RecycleAsync(IReadOnlyList<EntryItem> items)
    {
        if (_removalInProgress)
        {
            return false;
        }

        _removalInProgress = true;
        try
        {
            return await RecycleCoreAsync(items);
        }
        finally
        {
            _removalInProgress = false;
        }
    }

    private static async Task<bool> RecycleCoreAsync(IReadOnlyList<EntryItem> items)
    {
        var tree = State.Tree;
        items = items.Where(i => i.Kind is EntryKind.Directory or EntryKind.File && i.Tree == tree && !IsScanRoot(i) && !i.IsReparsePoint).ToList();
        var selectedFolders = items.Where(i => i.IsDirectory).Select(i => i.Path).ToList();
        items = items.Where(i => !selectedFolders.Any(f => Core.Models.PathUtil.IsStrictlyUnder(i.Path, f))).ToList();
        if (tree is null || items.Count == 0 || !State.CanModify)
        {
            return false;
        }

        // Removable and network drives normally have no Recycle Bin: the shell deletes permanently there.
        // A selection can mix both kinds of drive; the dialog says which items are deleted permanently.
        var noRecycleBin = items.Select(i => i.Path).Where(p => !DriveService.HasRecycleBin(p)).ToList();
        bool anyPermanent = noRecycleBin.Count > 0;
        bool allPermanent = noRecycleBin.Count == items.Count;

        var blocked = items.Where(i => !IsRemovable(i)).ToList();
        if (blocked.Count > 0)
        {
            await ShowProtectedAsync(blocked[0]);
            return false;
        }

        long total = items.Sum(i => i.Size);
        var content = new StackPanel { Spacing = 12, MaxWidth = 520 };
        content.Children.Add(new TextBlock
        {
            Text = items.Count == 1
                ? $"{items[0].Name}\n{SizeFormatter.Format(items[0].Size)}"
                : $"{items.Count} items, {SizeFormatter.Format(total)}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = string.Join("\n", items.Take(5).Select(i => i.Path)) + (items.Count > 5 ? $"\n… and {items.Count - 5} more" : ""),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            IsTextSelectionEnabled = true,
        });

        foreach (var warning in items.Where(i => i.Safety.Level == ProtectionLevel.Caution).Take(3))
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = warning.Safety.Label,
                Message = warning.Safety.Explanation + (warning.Safety.Advice is { } advice ? " " + advice : ""),
            });
        }

        var finding = items.Count == 1 ? items[0].Finding : null;
        if (finding?.Explanation is { } explanation)
        {
            content.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap });
        }

        CheckBox? acknowledge = null;
        if (anyPermanent)
        {
            var drives = noRecycleBin.Select(p => Path.GetPathRoot(p)?.TrimEnd('\\') ?? p).Distinct(StringComparer.OrdinalIgnoreCase);
            string which = allPermanent
                ? $"Items on {string.Join(", ", drives)} will be deleted permanently and cannot be restored."
                : $"{noRecycleBin.Count} of the {items.Count} items are on {string.Join(", ", drives)} and will be deleted permanently:\n" +
                  string.Join("\n", noRecycleBin.Take(5)) + (noRecycleBin.Count > 5 ? $"\n… and {noRecycleBin.Count - 5} more" : "") +
                  "\nThe other items go to the Recycle Bin.";
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = allPermanent ? "This drive has no Recycle Bin" : "Some items cannot go to the Recycle Bin",
                Message = which,
            });
            acknowledge = new CheckBox
            {
                Content = allPermanent ? "I understand these items cannot be recovered." : "I understand the items listed above cannot be recovered.",
            };
            content.Children.Add(acknowledge);
        }

        if (!allPermanent)
        {
            content.Children.Add(new TextBlock
            {
                Text = anyPermanent
                    ? "Items moved to the Recycle Bin can be restored until it is emptied."
                    : "You can restore items from the Recycle Bin until it is emptied.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
        }

        var dialog = new ContentDialog
        {
            Title = allPermanent
                ? (items.Count == 1 ? "Delete permanently?" : $"Delete {items.Count} items permanently?")
                : (items.Count == 1 ? "Move to Recycle Bin?" : $"Move {items.Count} items to the Recycle Bin?"),
            Content = new ScrollViewer { Content = content, MaxHeight = 420 },
            PrimaryButtonText = allPermanent ? "Delete permanently" : anyPermanent ? "Remove" : "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = !anyPermanent,
            XamlRoot = State.XamlRoot,
        };

        if (acknowledge is not null)
        {
            acknowledge.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            acknowledge.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        }

        if (await ItemActions.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        long freeBefore = State.SelectedDrive?.FreeBytes ?? -1;
        var result = await State.Shell.MoveToRecycleBinAsync(items.Select(i => (i.Path, i.IsDirectory)).ToList(), State.WindowHandle);
        State.RemoveFromTree(tree, items
            .Where(i => i.IsDirectory ? !Directory.Exists(i.Path) : !File.Exists(i.Path))
            .Select(i => (i.IsFile, i.Index))
            .ToList());
        if (result.Success && !anyPermanent && freeBefore >= 0 && State.SelectedDrive is { } drive && drive.FreeBytes <= freeBefore)
        {
            State.StatusDetail += "  ·  Free space is unchanged until the Recycle Bin is emptied";
        }

        if (!result.Success && !result.Cancelled)
        {
            await ShowMessageAsync("Some items were not moved", result.Error ?? "Windows could not move every item to the Recycle Bin.");
        }

        return result.Success;
    }

    /// <summary>Permanent deletion of a single file, behind a second explicit acknowledgement.</summary>
    public static async Task<bool> DeletePermanentlyAsync(EntryItem item)
    {
        if (_removalInProgress)
        {
            return false;
        }

        _removalInProgress = true;
        try
        {
            return await DeletePermanentlyCoreAsync(item);
        }
        finally
        {
            _removalInProgress = false;
        }
    }

    private static async Task<bool> DeletePermanentlyCoreAsync(EntryItem item)
    {
        if (item.Kind != EntryKind.File || item.Tree is not { } tree || tree != State.Tree || !State.CanModify)
        {
            return false;
        }

        if (!IsRemovable(item))
        {
            await ShowProtectedAsync(item);
            return false;
        }

        var acknowledge = new CheckBox { Content = "I understand this file cannot be recovered from the Recycle Bin." };
        var content = new StackPanel { Spacing = 12, MaxWidth = 520 };
        content.Children.Add(new TextBlock { Text = $"{item.Name}\n{SizeFormatter.Format(item.Size)}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = item.Path, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        if (item.Safety.Level == ProtectionLevel.Caution)
        {
            content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = item.Safety.Label, Message = item.Safety.Explanation });
        }

        content.Children.Add(acknowledge);
        var dialog = new ContentDialog
        {
            Title = "Delete permanently?",
            Content = content,
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = State.XamlRoot,
        };
        acknowledge.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        acknowledge.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;

        if (await ItemActions.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        var result = State.Shell.DeleteFilePermanently(item.Path);
        if (result.Success)
        {
            State.RemoveFromTree(tree, true, item.Index);
            return true;
        }

        await ShowMessageAsync("The file was not deleted", result.Error ?? "Unknown error.");
        return false;
    }

    /// <summary>
    /// Marks a cloud-synced file or folder online-only (File Explorer's "Free up space"). Nothing is deleted:
    /// the provider removes the local copy in the background and downloads it again when it is opened.
    /// Afterwards the folder is rescanned so the freed space shows up.
    /// </summary>
    public static async Task FreeUpSpaceAsync(EntryItem item)
    {
        if (item.Kind is not (EntryKind.Directory or EntryKind.File) || item.Tree is not { } tree || tree != State.Tree || !State.CanModify)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Free up space?",
            Content = new TextBlock
            {
                Text = $"{item.Path}\n\nThe {(item.IsDirectory ? "files in this folder stay" : "file stays")} in the cloud and {(item.IsDirectory ? "are" : "is")} downloaded again when opened, " +
                       "but will not be available without an internet connection. This is the same as \"Free up space\" in File Explorer; nothing is deleted.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480,
            },
            PrimaryButtonText = "Free up space",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = State.XamlRoot,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string path = item.Path;
        FreeUpResult result;
        try
        {
            result = await Task.Run(() => CloudFiles.FreeUpSpace(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Space was not freed", ex.Message);
            return;
        }

        State.StatusDetail = $"{SizeFormatter.FormatCount(result.Changed)} files set to online-only; the cloud provider frees the space in the background" +
            (result.Failed > 0 ? $"  ·  {SizeFormatter.FormatCount(result.Failed)} items could not be changed" : "");

        // Give the provider a moment, then measure again.
        int folder = item.IsDirectory ? item.Index : tree.File(item.Index).Directory;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (folder != Core.Models.ScanTree.RootIndex && tree == State.Tree && tree.IsLiveDirectory(folder))
        {
            await State.RescanFolderAsync(tree, folder);
        }
    }

    /// <summary>Explains why SpaceLens will not delete an item and offers the legitimate alternative, if any.</summary>
    public static async Task ShowProtectedAsync(EntryItem item)
    {
        var app = item.IsDirectory ? State.FindAppForPath(item.Path) : null;
        string title;
        string message;
        string? actionUri = null;
        string? actionLabel = null;

        if (!item.Safety.CanDelete || item.Finding is null)
        {
            title = item.Safety.IsSystemManaged ? "Managed by Windows" : item.Safety.Label;
            message = (item.Safety.Explanation ?? "This location is protected.") + (item.Safety.Advice is { } a ? "\n\n" + a : "");
            if (item.Finding?.ActionUri is { } uri && !uri.StartsWith("spacelens:", StringComparison.Ordinal))
            {
                (actionUri, actionLabel) = (uri, item.Finding.ActionLabel);
            }
        }
        else
        {
            var f = item.Finding;
            title = f.Nature == StorageNature.Game ? "Managed by a game launcher" : "Remove this with its own tool";
            message = (f.Explanation ?? "") + (f.Advice is { } a ? "\n\n" + a : "") +
                "\n\nSpaceLens does not delete these files directly, because doing so can leave the owning program in a broken state.";
            if (f.ActionUri is { } uri && !uri.StartsWith("spacelens:", StringComparison.Ordinal))
            {
                (actionUri, actionLabel) = (uri, f.ActionLabel);
            }
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = new TextBlock { Text = message.Trim(), TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, MaxHeight = 400 },
            CloseButtonText = "OK",
            XamlRoot = State.XamlRoot,
        };

        if (actionUri is not null)
        {
            dialog.PrimaryButtonText = actionLabel ?? "Open";
        }
        else if (app is not null)
        {
            dialog.PrimaryButtonText = $"Uninstall {app.Name}…";
        }

        if (await ItemActions.ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            if (actionUri is not null)
            {
                Report(ShellActions.OpenUri(actionUri));
            }
            else if (app is not null)
            {
                State.RequestNavigation("apps", app);
            }
        }
    }

    /// <summary>
    /// Shows a dialog in the app's current theme. Dialogs are hosted in a popup layer that does not
    /// inherit RequestedTheme from the page, so it is applied explicitly.
    /// </summary>
    public static IAsyncOperation<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot ??= State.XamlRoot;
        dialog.Style ??= (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        if (dialog.XamlRoot?.Content is FrameworkElement root)
        {
            dialog.RequestedTheme = root.ActualTheme;
        }

        return dialog.ShowAsync();
    }

    public static async Task ShowMessageAsync(string title, string message)
    {
        if (State.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            CloseButtonText = "OK",
            XamlRoot = State.XamlRoot,
        };
        await ItemActions.ShowDialogAsync(dialog);
    }

    private static void Report(ShellResult result)
    {
        if (!result.Success && result.Error is not null)
        {
            _ = ShowMessageAsync("Could not complete the action", result.Error);
        }
    }

    /// <summary>Builds a context menu containing only the actions that make sense for the item.</summary>
    public static MenuFlyout BuildContextMenu(EntryItem item, IReadOnlyList<EntryItem>? selection = null)
    {
        var menu = new MenuFlyout();
        var targets = selection is { Count: > 1 } && selection.Contains(item) ? selection : [item];

        void Add(string text, string glyph, Action action, bool enabled = true, VirtualKeyAccel? accel = null)
        {
            var menuItem = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            if (accel is { } k)
            {
                menuItem.KeyboardAcceleratorTextOverride = k.Text;
            }

            menuItem.Click += (_, _) => action();
            menu.Items.Add(menuItem);
        }

        if (targets.Count == 1)
        {
            if (item.Kind == EntryKind.File)
            {
                Add("Open", "\uE8E5", () => Open(item), accel: new("Enter"));
            }

            Add(item.Kind == EntryKind.File ? "Open folder" : "Open in Explorer", "\uE838", () => OpenInExplorer(item),
                accel: item.Kind == EntryKind.File ? null : new("Enter"));
            Add("Copy path", "\uE8C8", () => CopyPath(item));
            if (item.Kind != EntryKind.LooseFiles)
            {
                Add("Properties", "\uE946", () => ShowProperties(item));
            }

            if (item.Kind == EntryKind.Directory)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                Add("Show in Folders", "\uE8B7", () => State.RequestNavigation("folders", item));
                Add("Scan this folder", "\uE721", () => ScanFolder(item), enabled: !State.IsScanning);
                if (item.Index != Core.Models.ScanTree.RootIndex && item.Tree is { } tree)
                {
                    Add("Rescan this folder", "\uE72C", () => _ = State.RescanFolderAsync(tree, item.Index),
                        enabled: !State.IsScanning && !State.IsRescanningFolder && tree == State.Tree);
                }
            }

            var app = item.Kind == EntryKind.Directory ? State.FindAppForPath(item.Path) : null;
            if (app is not null)
            {
                Add($"Uninstall {app.Name}…", "\uE74D", () => State.RequestNavigation("apps", app), enabled: State.CanModify);
            }

            if (item.Kind is EntryKind.Directory or EntryKind.File && !item.IsReparsePoint && CloudFiles.IsInSyncRoot(item.Path))
            {
                Add("Free up space (online-only)", "\uE753", () => _ = FreeUpSpaceAsync(item), enabled: State.CanModify);
            }

            if (item.Finding?.ActionUri is { } uri && item.Finding.ActionLabel is { } label && !uri.StartsWith("spacelens:", StringComparison.Ordinal))
            {
                Add(label, "\uE8A7", () => Report(ShellActions.OpenUri(uri)));
            }
        }

        if (item.Kind is EntryKind.Directory or EntryKind.File)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            bool canRecycle = targets.All(CanRecycle);
            Add(targets.Count == 1 ? "Move to Recycle Bin" : $"Move {targets.Count} items to Recycle Bin", "\uE74D",
                () => _ = RecycleAsync(targets), enabled: canRecycle, accel: new("Del"));
            if (!targets.All(IsRemovable))
            {
                Add("Why can't this be removed?", "\uE897", () => _ = ShowProtectedAsync(targets.First(t => !IsRemovable(t))));
            }

            if (targets.Count == 1 && item.Kind == EntryKind.File)
            {
                Add("Delete permanently…", "\uE711", () => _ = DeletePermanentlyAsync(item), enabled: CanRecycle(item));
            }
        }

        return menu;
    }

    /// <summary>Wires the standard context menu, Enter and Delete keys and double-click into a list.</summary>
    public static void AttachListBehaviors(ListViewBase list, Func<EntryItem, bool>? onInvoke = null)
    {
        list.ContextRequested += (sender, e) =>
        {
            var element = e.OriginalSource as FrameworkElement;
            var item = element?.DataContext as EntryItem ?? (element as ListViewItem)?.Content as EntryItem;
            if (item is null)
            {
                return;
            }

            if (!list.SelectedItems.Contains(item))
            {
                list.SelectedItem = item;
            }

            var selection = list.SelectedItems.OfType<EntryItem>().ToList();
            var menu = BuildContextMenu(item, selection);
            if (e.TryGetPosition(list, out var point))
            {
                menu.ShowAt(list, point);
            }
            else
            {
                menu.ShowAt(element ?? list);
            }

            e.Handled = true;
        };

        list.KeyDown += (sender, e) =>
        {
            if (list.SelectedItem is not EntryItem item)
            {
                return;
            }

            if (e.Key == global::Windows.System.VirtualKey.Enter)
            {
                if (onInvoke?.Invoke(item) != true)
                {
                    Open(item);
                }

                e.Handled = true;
            }
            else if (e.Key == global::Windows.System.VirtualKey.Delete)
            {
                var selection = list.SelectedItems.OfType<EntryItem>().Where(i => i.Kind is EntryKind.Directory or EntryKind.File).ToList();
                if (selection.Count == 0)
                {
                    return;
                }

                if (selection.Any(i => !IsRemovable(i)))
                {
                    _ = ShowProtectedAsync(selection.First(i => !IsRemovable(i)));
                }
                else if (State.CanModify)
                {
                    _ = RecycleAsync(selection);
                }

                e.Handled = true;
            }
        };

        list.DoubleTapped += (sender, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is EntryItem item && onInvoke?.Invoke(item) != true)
            {
                Open(item);
            }
        };
    }

    public readonly record struct VirtualKeyAccel(string Text);
}
