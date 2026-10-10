namespace SpaceLens.Desktop.Services;

/// <summary>A warning shown in a confirmation (title and text).</summary>
public sealed record DialogWarning(string Title, string Message);

public sealed record ConfirmRequest(
    string Title,
    string Message,
    string PrimaryText,
    IReadOnlyList<DialogWarning> Warnings,
    string? Footnote = null,
    string? AcknowledgementText = null);

/// <summary>What the view models need from the window. The real one shows dialogs; tests answer directly.</summary>
public interface IDialogService
{
    /// <summary>True when the person chose the primary action (Cancel is the default).</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    Task ShowMessageAsync(string title, string message);

    Task<string?> PickFolderAsync();

    Task CopyTextAsync(string text);
}
