using Avalonia.Controls;
using Avalonia.Input;
using SpaceLens.Desktop.ViewModels;

namespace SpaceLens.Desktop.Views;

/// <summary>Keyboard and mouse behaviour shared by every list of files and folders.</summary>
internal static class ItemListBehavior
{
    /// <summary>
    /// Delete (or ⌘⌫) moves the selection to the Trash after the usual confirmation; Enter or a double-click
    /// runs <paramref name="activate"/> (open a folder, open a file).
    /// </summary>
    public static void Attach(ListBox list, Func<MainViewModel?> main, Action<ItemViewModel> activate)
    {
        list.SelectionMode = SelectionMode.Multiple;
        list.KeyDown += async (_, e) =>
        {
            bool delete = e.Key == Key.Delete || e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (delete && main() is { } vm)
            {
                var selected = list.SelectedItems?.OfType<ItemViewModel>().ToList() ?? [];
                if (selected.Count > 0)
                {
                    e.Handled = true;
                    await vm.MoveToTrashAsync(selected);
                }
            }
            else if (e.Key == Key.Enter && list.SelectedItem is ItemViewModel item)
            {
                e.Handled = true;
                activate(item);
            }
        };
        list.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is ItemViewModel item)
            {
                activate(item);
            }
        };
    }
}
