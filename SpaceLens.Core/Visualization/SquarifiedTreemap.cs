namespace SpaceLens.Core.Visualization;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

public readonly record struct TreemapCell<T>(T Item, TreemapRect Bounds);

/// <summary>
/// Squarified treemap layout (Bruls, Huizing, van Wijk 2000): lays out weighted items into a rectangle
/// while keeping cell aspect ratios close to 1. Pure computation, safe to run on a background thread.
/// </summary>
public static class SquarifiedTreemap
{
    /// <param name="items">Items with positive weights, sorted by descending weight for best results.</param>
    public static List<TreemapCell<T>> Layout<T>(IReadOnlyList<(T Item, double Weight)> items, TreemapRect bounds)
    {
        var cells = new List<TreemapCell<T>>(items.Count);
        double total = 0;
        foreach (var (_, w) in items)
        {
            if (w > 0)
            {
                total += w;
            }
        }

        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return cells;
        }

        double scale = bounds.Area / total;
        var row = new List<(T Item, double Area)>();
        var rect = bounds;
        int i = 0;
        var areas = items.Where(x => x.Weight > 0).Select(x => (x.Item, Area: x.Weight * scale)).ToList();

        while (i < areas.Count)
        {
            double side = Math.Min(rect.Width, rect.Height);
            var candidate = areas[i];
            if (row.Count == 0 || WorstRatio(row, candidate.Area, side) <= WorstRatio(row, 0, side))
            {
                row.Add(candidate);
                i++;
                continue;
            }

            rect = PlaceRow(row, rect, cells);
            row.Clear();
        }

        if (row.Count > 0)
        {
            PlaceRow(row, rect, cells);
        }

        return cells;
    }

    private static double WorstRatio<T>(List<(T Item, double Area)> row, double extra, double side)
    {
        double sum = extra, min = extra > 0 ? extra : double.MaxValue, max = extra;
        foreach (var (_, a) in row)
        {
            sum += a;
            min = Math.Min(min, a);
            max = Math.Max(max, a);
        }

        if (sum <= 0 || min <= 0)
        {
            return double.MaxValue;
        }

        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private static TreemapRect PlaceRow<T>(List<(T Item, double Area)> row, TreemapRect rect, List<TreemapCell<T>> cells)
    {
        double rowArea = row.Sum(r => r.Area);
        if (rect.Width >= rect.Height)
        {
            // Vertical column on the left.
            double width = rect.Height > 0 ? rowArea / rect.Height : 0;
            double y = rect.Y;
            foreach (var (item, area) in row)
            {
                double h = width > 0 ? area / width : 0;
                cells.Add(new TreemapCell<T>(item, new TreemapRect(rect.X, y, width, h)));
                y += h;
            }

            return new TreemapRect(rect.X + width, rect.Y, Math.Max(0, rect.Width - width), rect.Height);
        }
        else
        {
            double height = rect.Width > 0 ? rowArea / rect.Width : 0;
            double x = rect.X;
            foreach (var (item, area) in row)
            {
                double w = height > 0 ? area / height : 0;
                cells.Add(new TreemapCell<T>(item, new TreemapRect(x, rect.Y, w, height)));
                x += w;
            }

            return new TreemapRect(rect.X, rect.Y + height, rect.Width, Math.Max(0, rect.Height - height));
        }
    }
}
