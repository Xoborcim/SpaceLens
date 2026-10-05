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
            return "-" + Format(bytes == long.MinValue ? long.MaxValue : -bytes);
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
    /// Parses sizes such as "5GB", "1.5 gb", "1,5 GB", "1,000MB", "500MB", "100k", "42" (bytes).
    /// A comma followed by exactly three digits is a thousands separator; otherwise it is a decimal point.
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

        if (i == 0 || !double.TryParse(NormalizeNumber(text[..i].ToString()), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
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
            "P" or "PB" or "PIB" => 1L << 50,
            _ => -1,
        };

        double value = number * multiplier;
        if (multiplier < 0 || value >= long.MaxValue)
        {
            return false;
        }

        bytes = (long)value;
        return true;
    }

    /// <summary>Turns "1,000.5", "1,000" and "1,5" into invariant-culture numbers ("1000.5", "1000", "1.5").</summary>
    private static string NormalizeNumber(string number)
    {
        string[] groups = number.Split(',');
        if (groups.Length == 1)
        {
            return number;
        }

        // Every group after a comma has three digits (before any decimal point): thousands separators.
        bool thousands = true;
        for (int g = 1; g < groups.Length; g++)
        {
            string digits = g == groups.Length - 1 && groups[g].IndexOf('.') is int dot and >= 0 ? groups[g][..dot] : groups[g];
            if (digits.Length != 3 || digits.Contains('.'))
            {
                thousands = false;
                break;
            }
        }

        if (thousands)
        {
            return string.Concat(groups);
        }

        // Otherwise a single comma is a decimal separator ("1,5"); anything else is not a number.
        return groups.Length == 2 && !number.Contains('.') ? groups[0] + "." + groups[1] : "invalid";
    }
}
