namespace SpaceLens.Core.Models;

public static class PathUtil
{
    public const string LongPathPrefix = @"\\?\";
    public const string LongUncPrefix = @"\\?\UNC\";

    public static bool EndsWithSeparator(string path) =>
        path.Length > 0 && (path[^1] == '\\' || path[^1] == '/');

    /// <summary>
    /// Produces a canonical display path: backslashes, no long-path prefix, no trailing separator
    /// except for drive roots ("C:\").
    /// </summary>
    public static string NormalizeDisplayPath(string path)
    {
        path = StripLongPathPrefix(path.Trim().Trim('"'))!.Replace('/', '\\');
        if (path.Length == 2 && path[1] == ':')
        {
            return char.ToUpperInvariant(path[0]) + @":\";
        }

        if (path.Length == 3 && path[1] == ':' && path[2] == '\\')
        {
            return char.ToUpperInvariant(path[0]) + @":\";
        }

        path = path.TrimEnd('\\');
        if (path.Length >= 2 && path[1] == ':')
        {
            path = char.ToUpperInvariant(path[0]) + path[1..];
        }

        return path;
    }

    /// <summary>Converts a display path to the <c>\\?\</c> form, which bypasses MAX_PATH and Win32 name normalization.</summary>
    public static string ToLongPath(string displayPath)
    {
        if (displayPath.StartsWith(LongPathPrefix, StringComparison.Ordinal))
        {
            return displayPath;
        }

        if (displayPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return LongUncPrefix + displayPath[2..];
        }

        return LongPathPrefix + displayPath;
    }

    public static string? StripLongPathPrefix(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (path.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[LongUncPrefix.Length..];
        }

        if (path.StartsWith(LongPathPrefix, StringComparison.Ordinal))
        {
            return path[LongPathPrefix.Length..];
        }

        return path;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or is inside it (case-insensitive).</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        path = NormalizeDisplayPath(path);
        root = NormalizeDisplayPath(root);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = EndsWithSeparator(root) ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStrictlyUnder(string path, string root) =>
        IsSameOrUnder(path, root) && !NormalizeDisplayPath(path).Equals(NormalizeDisplayPath(root), StringComparison.OrdinalIgnoreCase);

    public static string GetName(string path)
    {
        path = NormalizeDisplayPath(path);
        if (path.Length == 3 && path[1] == ':')
        {
            return path;
        }

        int slash = path.LastIndexOf('\\');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    public static string? GetParent(string path)
    {
        path = NormalizeDisplayPath(path);
        if (path.Length <= 3)
        {
            return null;
        }

        int slash = path.LastIndexOf('\\');
        if (slash < 0)
        {
            return null;
        }

        return slash == 2 && path[1] == ':' ? path[..3] : path[..slash];
    }

    public static string Combine(string directory, string name) =>
        EndsWithSeparator(directory) ? directory + name : directory + "\\" + name;
}
