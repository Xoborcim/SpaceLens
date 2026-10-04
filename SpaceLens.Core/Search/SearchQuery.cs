using System.IO.Enumeration;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.Search;

/// <summary>
/// Parsed search expression. Whitespace-separated terms are combined with AND:
/// <list type="bullet">
/// <item><c>steam</c> – name contains "steam"</item>
/// <item><c>.iso</c> – files with extension .iso, and folders named exactly ".iso" (so <c>.git</c> finds .git folders)</item>
/// <item><c>*.vmdk</c>, <c>backup_??.zip</c> – wildcard match on the name</item>
/// <item><c>&gt;5GB</c>, <c>&lt;100MB</c>, <c>&gt;=1g</c> – size filters</item>
/// <item><c>type:video</c> – file category</item>
/// <item><c>"two words"</c> – quoted substring</item>
/// </list>
/// </summary>
public sealed class SearchQuery
{
    private readonly List<string> _contains = [];
    private readonly List<string> _wildcards = [];
    private readonly List<string> _extensions = [];
    private readonly List<FileCategory> _categories = [];

    public long MinSize { get; private set; } = long.MinValue;

    public long MaxSize { get; private set; } = long.MaxValue;

    public bool IsEmpty =>
        _contains.Count == 0 && _wildcards.Count == 0 && _extensions.Count == 0 && _categories.Count == 0 &&
        MinSize == long.MinValue && MaxSize == long.MaxValue;

    /// <summary>Category filters only match files.</summary>
    public bool FilesOnly => _categories.Count > 0;

    public static SearchQuery Parse(string? text)
    {
        var query = new SearchQuery();
        if (string.IsNullOrWhiteSpace(text))
        {
            return query;
        }

        foreach (var token in Tokenize(text))
        {
            query.AddTerm(token);
        }

        return query;
    }

    private void AddTerm(string term)
    {
        if (term.Length == 0)
        {
            return;
        }

        if (term[0] is '>' or '<')
        {
            bool greater = term[0] == '>';
            var rest = term.AsSpan(1);
            bool inclusive = rest.Length > 0 && rest[0] == '=';
            if (inclusive)
            {
                rest = rest[1..];
            }

            if (SizeFormatter.TryParse(rest, out long bytes))
            {
                if (greater)
                {
                    MinSize = Math.Max(MinSize, inclusive ? bytes : bytes + 1);
                }
                else
                {
                    MaxSize = Math.Min(MaxSize, inclusive ? bytes : bytes - 1);
                }

                return;
            }
        }

        if (term.StartsWith("type:", StringComparison.OrdinalIgnoreCase) || term.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
        {
            string wanted = term[5..];
            foreach (var category in FileCategoryInfo.All)
            {
                if (FileCategoryInfo.DisplayName(category).Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                    FileCategoryInfo.ShortName(category).Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                    category.ToString().Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    _categories.Add(category);
                }
            }

            if (_categories.Count == 0)
            {
                _contains.Add(term);
            }

            return;
        }

        if (term.Contains('*') || term.Contains('?'))
        {
            _wildcards.Add(term);
            return;
        }

        if (term.Length > 1 && term[0] == '.' && term.IndexOf('.', 1) < 0)
        {
            _extensions.Add(term);
            return;
        }

        _contains.Add(term);
    }

    public bool MatchesSize(long size) => size >= MinSize && size <= MaxSize;

    public bool MatchesName(string name, bool isFile, FileCategory category = FileCategory.Other)
    {
        if (!isFile && FilesOnly)
        {
            return false;
        }

        foreach (var c in _contains)
        {
            if (!name.Contains(c, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var w in _wildcards)
        {
            if (!FileSystemName.MatchesSimpleExpression(w, name, ignoreCase: true))
            {
                return false;
            }
        }

        // ".git" means files ending in .git, or a folder named exactly ".git".
        foreach (var e in _extensions)
        {
            if (isFile ? !name.EndsWith(e, StringComparison.OrdinalIgnoreCase) : !name.Equals(e, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (_categories.Count > 0 && !_categories.Contains(category))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                yield break;
            }

            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0)
                {
                    end = text.Length;
                }

                yield return text[(i + 1)..end];
                i = end + 1;
                continue;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            // Allow "> 5GB" (operator separated from the value by a space).
            string token = text[start..i];
            if (token is ">" or "<" or ">=" or "<=")
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                int valueStart = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                token += text[valueStart..i];
            }

            yield return token;
        }
    }
}
