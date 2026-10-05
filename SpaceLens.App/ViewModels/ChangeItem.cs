using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SpaceLens.Core.Comparison;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;

namespace SpaceLens.App.ViewModels;

/// <summary>A row on the Changes page: one folder (or a folder's loose files) whose size changed between two scans.</summary>
public sealed class ChangeItem
{
    public ChangeItem(ScanChange change, long maxDelta)
    {
        Change = change;
        string name = PathUtil.GetName(change.Path);
        Name = change.IsLooseFiles ? $"Files in {name}" : name;
        Path = change.Path;
        BarWidth = maxDelta > 0 ? Math.Clamp((double)Math.Abs(change.Delta) / maxDelta, 0, 1) * EntryItem.BarMaxWidth : 0;
        (KindText, Glyph) = change.Kind switch
        {
            ChangeKind.Added => ("New", ""),
            ChangeKind.Removed => ("Removed", ""),
            ChangeKind.Grew => ("Grew", change.IsLooseFiles ? "" : ""),
            _ => ("Shrank", change.IsLooseFiles ? "" : ""),
        };
        Subtitle = change.Kind switch
        {
            ChangeKind.Added => $"{change.Path}  ·  new, {SizeFormatter.Format(change.NewSize)}",
            ChangeKind.Removed => $"{change.Path}  ·  was {SizeFormatter.Format(change.OldSize)}",
            _ => $"{change.Path}  ·  {SizeFormatter.Format(change.OldSize)} → {SizeFormatter.Format(change.NewSize)}",
        };
    }

    public ScanChange Change { get; }

    public string Name { get; }

    public string Path { get; }

    public string Subtitle { get; }

    public string KindText { get; }

    public string Glyph { get; }

    public double BarWidth { get; }

    /// <summary>The folder still exists in the current scan, so it can be shown in Folders.</summary>
    public bool ExistsNow => Change.CurrentIndex >= 0;

    public string DeltaText => FormatDelta(Change.Delta);

    /// <summary>Space used is shown in the caution color, space freed in the success color.</summary>
    public Brush DeltaBrush => (Brush)Application.Current.Resources[Change.Delta > 0 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush"];

    public string AutomationName => $"{Name}, {KindText}, {DeltaText}";

    public override string ToString() => AutomationName;

    public static string FormatDelta(long delta) =>
        delta > 0 ? "+" + SizeFormatter.Format(delta) : delta < 0 ? "−" + SizeFormatter.Format(-delta) : "0 bytes";
}
