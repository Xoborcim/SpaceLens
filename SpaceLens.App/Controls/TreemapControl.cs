using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpaceLens.App.ViewModels;
using SpaceLens.Core.Visualization;

namespace SpaceLens.App.Controls;

/// <summary>
/// Squarified treemap of a small set of pre-aggregated items (the largest-items breakdown). The
/// expensive part — deciding what to show — runs on a background thread elsewhere; this control
/// only positions a few dozen rectangles.
/// </summary>
public sealed partial class TreemapControl : Canvas
{
    private IReadOnlyList<(EntryItem Item, uint Color)> _items = [];

    public TreemapControl()
    {
        SizeChanged += (_, _) => Render();
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(this, "Treemap of the largest items");
    }

    public event EventHandler<EntryItem>? CellInvoked;

    public void SetItems(IReadOnlyList<(EntryItem Item, uint Color)> items)
    {
        _items = items;
        Render();
    }

    private void Render()
    {
        Children.Clear();
        double width = ActualWidth, height = ActualHeight;
        if (_items.Count == 0 || width < 10 || height < 10)
        {
            return;
        }

        var weighted = _items.Where(i => i.Item.Size > 0).Select(i => (i, (double)i.Item.Size)).ToList();
        var cells = SquarifiedTreemap.Layout<(EntryItem Item, uint Color)>(weighted, new TreemapRect(0, 0, width, height));
        foreach (var cell in cells)
        {
            var bounds = cell.Bounds;
            if (bounds.Width < 2 || bounds.Height < 2)
            {
                continue;
            }

            var (item, color) = cell.Item;
            var border = new Border
            {
                Width = Math.Max(0, bounds.Width - 2),
                Height = Math.Max(0, bounds.Height - 2),
                CornerRadius = new CornerRadius(4),
                Background = SummaryItem.BrushFromArgb(color),
                Padding = new Thickness(6, 4, 6, 4),
            };
            AutomationProperties.SetName(border, item.AutomationName);
            ToolTipService.SetToolTip(border, $"{item.Path}\n{item.SizeText}");

            if (bounds.Width > 70 && bounds.Height > 34)
            {
                var text = new StackPanel();
                var foreground = new SolidColorBrush(IsLight(color) ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
                text.Children.Add(new TextBlock { Text = item.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = item.SizeText, FontSize = 11, Foreground = foreground, Opacity = 0.85 });
                border.Child = text;
            }

            border.Tapped += (_, _) => CellInvoked?.Invoke(this, item);
            SetLeft(border, bounds.X + 1);
            SetTop(border, bounds.Y + 1);
            Children.Add(border);
        }
    }

    private static bool IsLight(uint argb)
    {
        double r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        return (0.299 * r) + (0.587 * g) + (0.114 * b) > 150;
    }
}
