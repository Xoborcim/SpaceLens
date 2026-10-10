namespace SpaceLens.Core.Models;

/// <summary>
/// Path helpers for the two path styles the engine handles. A path that starts with <c>/</c> is a Unix
/// path (macOS, Linux), separated by <c>/</c>. Anything else is a Windows path (<c>C:\</c>, <c>\\server\share</c>),
/// separated by <c>\</c>, which may use the <c>\\?\</c> long-path form. A scan tree uses the style of its root.
/// Comparisons ignore case: Windows and the default macOS (APFS) volume format are case-insensitive.
/// </summary>
public static class PathUtil
{
    public const string LongPathPrefix = @"\\?\";
    public const string LongUncPrefix = @"\\?\UNC\";

    public static bool IsUnixPath(string path) => path.Length > 0 && path[0] == '/';

    /// <summary>The separator used by paths in the style of <paramref name="path"/>.</summary>
    public static char SeparatorOf(string path) => IsUnixPath(path) ? '/' : '\\';

    public static bool EndsWithSeparator(string path) =>
        path.Length > 0 && (path[^1] == '\\' || path[^1] == '/');

    /// <summary>
    /// Produces a canonical display path. Windows: backslashes, no long-path prefix, upper-case drive
    /// letter, no trailing separator except for drive roots ("C:\"). Unix: no trailing separator except
    /// for the root ("/").
    /// </summary>
    public static string NormalizeDisplayPath(string path)
    {
        path = path.Trim().Trim('"');
        if (IsUnixPath(path))
        {
            string trimmed = path.TrimEnd('/');
            return trimmed.Length == 0 ? "/" : trimmed;
        }

        path = StripLongPathPrefix(path)!.Replace('/', '\\');
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
        if (IsUnixPath(displayPath) || displayPath.StartsWith(LongPathPrefix, StringComparison.Ordinal))
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

        if (IsUnixPath(path) != IsUnixPath(root))
        {
            return false;
        }

        string prefix = EndsWithSeparator(root) ? root : root + SeparatorOf(root);
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStrictlyUnder(string path, string root) =>
        IsSameOrUnder(path, root) && !NormalizeDisplayPath(path).Equals(NormalizeDisplayPath(root), StringComparison.OrdinalIgnoreCase);

    public static string GetName(string path)
    {
        path = NormalizeDisplayPath(path);
        if (IsUnixPath(path))
        {
            return path == "/" ? path : path[(path.LastIndexOf('/') + 1)..];
        }

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
        if (IsUnixPath(path))
        {
            if (path == "/")
            {
                return null;
            }

            int separator = path.LastIndexOf('/');
            return separator == 0 ? "/" : path[..separator];
        }

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

    /// <summary>
    /// Cleans a folder passed on the command line. Explorer passes a drive as <c>"C:\"</c>, and the
    /// Windows argument rules turn the backslash before the closing quote into an escaped quote, so the
    /// program receives <c>C:"</c>. Stray quotes are removed and a bare drive gets its backslash back.
    /// </summary>
    public static string? CleanCommandLinePath(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return null;
        }

        string path = argument.Trim().Trim('"').Trim();
        if (path.Length == 2 && path[1] == ':')
        {
            path += "\\";
        }

        return path.Length > 0 ? path : null;
    }

    public static string Combine(string directory, string name) =>
        EndsWithSeparator(directory) ? directory + name : string.Concat(directory, SeparatorOf(directory).ToString(), name);
}
