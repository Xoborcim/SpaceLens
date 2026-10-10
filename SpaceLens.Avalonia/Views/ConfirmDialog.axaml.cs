using Avalonia.Controls;
using Avalonia.Interactivity;
using SpaceLens.Desktop.Services;

namespace SpaceLens.Desktop.Views;

/// <summary>
/// Confirmation for a destructive action: Cancel is the default button and Escape cancels. When the request
/// has an acknowledgement text, the primary button stays disabled until the box is ticked. With no primary
/// text it is a plain message with one button.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
        : this(new ConfirmRequest("", "", "", []))
    {
    }

    public ConfirmDialog(ConfirmRequest request)
    {
        InitializeComponent();
        DataContext = request;
        bool message = string.IsNullOrEmpty(request.PrimaryText);
        PrimaryButton.IsVisible = !message;
        CancelButton.Content = message ? "OK" : "Cancel";
        Acknowledge.IsVisible = request.AcknowledgementText is not null;
        PrimaryButton.IsEnabled = request.AcknowledgementText is null;
    }

    private void OnAcknowledgeChanged(object? sender, RoutedEventArgs e) => PrimaryButton.IsEnabled = Acknowledge.IsChecked == true;

    private void OnPrimary(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
