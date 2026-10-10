using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using SpaceLens.Desktop.Services;

namespace SpaceLens.Desktop.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public async Task<bool> ConfirmAsync(ConfirmRequest request) =>
        await new ConfirmDialog(request).ShowDialog<bool>(this);

    public Task ShowMessageAsync(string title, string message) =>
        new ConfirmDialog(new ConfirmRequest(title, message, "", [])).ShowDialog<bool>(this);

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to scan", AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
