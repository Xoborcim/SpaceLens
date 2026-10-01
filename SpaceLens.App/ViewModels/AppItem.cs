using Microsoft.UI.Xaml;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.InstalledApps;

namespace SpaceLens.App.ViewModels;

/// <summary>Row for an installed program. Sizes distinguish what was measured on disk from what the program reported.</summary>
public sealed class AppItem
{
    public AppItem(InstalledApp app, long maxSize, int nestedApps = 0)
    {
        App = app;
        NestedApps = nestedApps;
        BarWidth = SummaryItem.Bar(app.BestSize, maxSize);
    }

    public InstalledApp App { get; }

    /// <summary>Other installed apps whose folders are inside this app's folder (e.g. games inside Steam).</summary>
    public int NestedApps { get; }

    public string Name => App.Name;

    public string Subtitle => string.Join("  ·  ", new[]
    {
        App.Publisher,
        App.Version,
        App.IsGame ? "Game" : null,
        NestedApps > 0 ? $"includes {NestedApps} other app{(NestedApps == 1 ? "" : "s")}" : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string SizeText => App.BestSize > 0 ? SizeFormatter.Format(App.BestSize) : "—";

    public string SizeNote => App.Measurement switch
    {
        MeasurementState.Measured when App.IsSizeMeasured => "on disk",
        MeasurementState.Measuring => "measuring…",
        _ when App.ReportedSize is > 0 => "reported",
        _ => "unknown",
    };

    public string InstallDateText => App.InstallDate is { } d ? d.ToString("d") : "";

    public double BarWidth { get; }

    public string Glyph => App.Source == AppSource.Msix ? "\uE719" : App.IsGame ? "\uE7FC" : "\uE71D";

    public Visibility BarVisibility => App.BestSize > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string AutomationName => $"{Name}, {SizeText} {SizeNote}";

    /// <summary>List items use ToString() as their name for screen readers.</summary>
    public override string ToString() => AutomationName;
}
