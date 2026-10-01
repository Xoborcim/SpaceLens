using System.Globalization;

namespace SpaceLens.Core.Formatting;

/// <summary>
/// Formats byte counts the way Windows Explorer does (binary multiples labelled KB/MB/GB/TB).
/// </summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }

        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 3 significant digits: 1.23 GB, 12.3 GB, 123 GB.
        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string FormatCount(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    public static string FormatPercent(double fraction) =>
        fraction <= 0 ? "0%" :
        fraction < 0.001 ? "<0.1%" :
        (fraction * 100).ToString(fraction < 0.1 ? "0.0" : "0", CultureInfo.CurrentCulture) + "%";

    /// <summary>
    /// Parses sizes such as "5GB", "1.5 gb", "500MB", "100k", "42" (bytes).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == ','))
        {
            i++;
        }

        if (i == 0 || !double.TryParse(text[..i].ToString().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return false;
        }

        var unit = text[i..].Trim().ToString().ToUpperInvariant();
        long multiplier = unit switch
        {
            "" or "B" or "BYTES" => 1,
            "K" or "KB" or "KIB" => 1L << 10,
            "M" or "MB" or "MIB" => 1L << 20,
            "G" or "GB" or "GIB" => 1L << 30,
            "T" or "TB" or "TIB" => 1L << 40,
            _ => -1,
        };

        if (multiplier < 0)
        {
            return false;
        }

        bytes = (long)(number * multiplier);
        return true;
    }
}
