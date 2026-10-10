using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SpaceLens.Core.Visualization;
using SpaceLens.Desktop.ViewModels;

namespace SpaceLens.Desktop.Controls;

/// <summary>Squarified treemap of the largest items; clicking a cell raises <see cref="CellInvoked"/>.</summary>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<MapCell>?> CellsProperty =
        AvaloniaProperty.Register<TreemapControl, IReadOnlyList<MapCell>?>(nameof(Cells));

    private List<TreemapCell<MapCell>> _layout = [];

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(CellsProperty);
        AffectsArrange<TreemapControl>(CellsProperty);
    }

    public IReadOnlyList<MapCell>? Cells
    {
        get => GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    public event EventHandler<MapCell>? CellInvoked;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = (Cells ?? []).Where(c => c.Size > 0).Select(c => (c, (double)c.Size)).ToList();
        _layout = SquarifiedTreemap.Layout<MapCell>(items, new TreemapRect(0, 0, finalSize.Width, finalSize.Height));
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        var border = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1);
        foreach (var cell in _layout)
        {
            var rect = new Rect(cell.Bounds.X, cell.Bounds.Y, cell.Bounds.Width, cell.Bounds.Height);
            uint argb = cell.Item.Color;
            var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            context.DrawRectangle(new SolidColorBrush(color), border, rect);
            if (rect.Width < 60 || rect.Height < 30)
            {
                continue;
            }

            bool light = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 150;
            var text = new FormattedText($"{cell.Item.Label}\n{cell.Item.Item.SizeText}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, light ? Brushes.Black : Brushes.White)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 12),
                MaxTextHeight = Math.Max(1, rect.Height - 8),
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(text, new Point(rect.X + 6, rect.Y + 4));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var hit = _layout.FirstOrDefault(c => point.X >= c.Bounds.X && point.X < c.Bounds.X + c.Bounds.Width && point.Y >= c.Bounds.Y && point.Y < c.Bounds.Y + c.Bounds.Height);
        if (hit.Item is not null)
        {
            CellInvoked?.Invoke(this, hit.Item);
        }
    }
}
