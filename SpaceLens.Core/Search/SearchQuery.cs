using System.IO.Enumeration;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.Search;

/// <summary>
/// Parsed search expression.
/// <list type="bullet">
/// <item><c>steam</c> – name contains "steam"</item>
/// <item><c>.iso</c> – files with extension .iso, and folders named exactly ".iso" (so <c>.git</c> finds .git folders)</item>
/// <item><c>*.vmdk</c>, <c>backup_??.zip</c> – wildcard match on the name</item>
/// <item><c>&gt;5GB</c>, <c>&lt;100MB</c>, <c>&gt;=1g</c> – size filters</item>
/// <item><c>older:1y</c>, <c>newer:30d</c> – files not modified in the last year / modified in the last 30 days
/// (units d, w, m, y; or a date such as <c>older:2024-01-01</c>). Folder dates do not reflect changes deeper
/// inside them, so these filters only match files.</item>
/// <item><c>type:video</c> – file category (several <c>type:</c> terms match any of the categories)</item>
/// <item><c>path:steamapps</c>, <c>path:"Program Files"</c> – the full path contains the text</item>
/// <item><c>"two words"</c> – quoted text is always matched literally against the name</item>
/// </list>
/// Terms separated by spaces must all match. <c>-term</c> excludes items matching that term (any kind of
/// term: <c>-node_modules</c>, <c>-type:video</c>, <c>-path:Windows</c>). <c>OR</c> (or <c>|</c>) separates
/// alternatives, and binds weaker than the spaces: <c>.iso &gt;4GB OR .vhdx</c> is (.iso and &gt;4GB) or .vhdx.
/// </summary>
public sealed class SearchQuery
{
    private readonly List<Alternative> _alternatives = [];

    private SearchQuery()
    {
    }

    public bool IsEmpty => _alternatives.All(a => a.IsEmpty);

    /// <summary>True when no alternative can match a folder (category and date filters only match files).</summary>
    public bool FilesOnly => _alternatives.Count > 0 && _alternatives.All(a => a.Required.FilesOnly);

    /// <param name="nowUtc">The reference time for relative dates such as <c>older:1y</c> (tests pass a fixed one).</param>
    public static SearchQuery Parse(string? text, DateTime? nowUtc = null)
    {
        var query = new SearchQuery();
        if (string.IsNullOrWhiteSpace(text))
        {
            return query;
        }

        DateTime now = nowUtc ?? DateTime.UtcNow;
        var current = new Alternative();
        foreach (var token in Tokenize(text))
        {
            if (!token.Quoted && !token.Negated && token.Text is "OR" or "|")
            {
                query.Add(current);
                current = new Alternative();
                continue;
            }

            if (token.Negated)
            {
                var excluded = new Filter();
                excluded.AddTerm(token.Text, token.Quoted, now);
                current.Excluded.Add(excluded);
            }
            else
            {
                current.Required.AddTerm(token.Text, token.Quoted, now);
            }
        }

        query.Add(current);
        return query;
    }

    private void Add(Alternative alternative)
    {
        if (!alternative.IsEmpty)
        {
            _alternatives.Add(alternative);
        }
    }

    /// <summary>True when the item satisfies at least one alternative.</summary>
    public bool Matches(SearchCandidate item)
    {
        foreach (var alternative in _alternatives)
        {
            if (alternative.Matches(item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns an age ("30d", "2w", "6m", "1y", a bare number of days) or a date ("2024-01-01", "2024-01",
    /// "2024", local time) into the corresponding moment in UTC.
    /// </summary>
    public static bool TryParseCutoff(string text, DateTime nowUtc, out DateTime cutoffUtc)
    {
        cutoffUtc = default;
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        string[] dateFormats = ["yyyy-MM-dd", "yyyy-MM", "yyyy"];
        if (text.Length >= 4 && DateTime.TryParseExact(text, dateFormats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date) &&
            date.Year >= 1970)
        {
            cutoffUtc = date;
            return true;
        }

        char unit = char.ToLowerInvariant(text[^1]);
        string digits = char.IsLetter(unit) ? text[..^1] : text;
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int amount) || amount > 10_000)
        {
            return false;
        }

        try
        {
            cutoffUtc = unit switch
            {
                'd' or >= '0' and <= '9' => nowUtc.AddDays(-amount),
                'w' => nowUtc.AddDays(-7.0 * amount),
                'm' => nowUtc.AddMonths(-amount),
                'y' => nowUtc.AddYears(-amount),
                _ => default,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return cutoffUtc != default;
    }

    private sealed class Alternative
    {
        public Filter Required { get; } = new();

        public List<Filter> Excluded { get; } = [];

        public bool IsEmpty => Required.IsEmpty && Excluded.Count == 0;

        public bool Matches(SearchCandidate item)
        {
            if (!Required.Matches(item))
            {
                return false;
            }

            foreach (var excluded in Excluded)
            {
                if (excluded.Matches(item))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A set of terms that must all match.</summary>
    private sealed class Filter
    {
        private readonly List<string> _contains = [];
        private readonly List<string> _wildcards = [];
        private readonly List<string> _extensions = [];
        private readonly List<string> _paths = [];
        private readonly List<FileCategory> _categories = [];
        private long _minSize = long.MinValue;
        private long _maxSize = long.MaxValue;
        private long _modifiedBefore = long.MaxValue;
        private long _modifiedAfter = long.MinValue;

        private bool HasDateFilter => _modifiedBefore != long.MaxValue || _modifiedAfter != long.MinValue;

        public bool IsEmpty =>
            _contains.Count == 0 && _wildcards.Count == 0 && _extensions.Count == 0 && _paths.Count == 0 && _categories.Count == 0 &&
            _minSize == long.MinValue && _maxSize == long.MaxValue && !HasDateFilter;

        public bool FilesOnly => _categories.Count > 0 || HasDateFilter;

        public void AddTerm(string term, bool quoted, DateTime nowUtc)
        {
            if (term.Length == 0)
            {
                return;
            }

            if (quoted)
            {
                _contains.Add(term);
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
                        _minSize = Math.Max(_minSize, inclusive ? bytes : bytes + 1);
                    }
                    else
                    {
                        _maxSize = Math.Min(_maxSize, inclusive ? bytes : bytes - 1);
                    }

                    return;
                }
            }

            bool older = term.StartsWith("older:", StringComparison.OrdinalIgnoreCase);
            if (older || term.StartsWith("newer:", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseCutoff(term[6..], nowUtc, out var cutoff))
                {
                    long fileTime = cutoff.ToFileTimeUtc();
                    if (older)
                    {
                        _modifiedBefore = Math.Min(_modifiedBefore, fileTime);
                    }
                    else
                    {
                        _modifiedAfter = Math.Max(_modifiedAfter, fileTime);
                    }
                }
                else
                {
                    // Not a valid age or date: match nothing rather than silently ignoring the term.
                    _contains.Add(term);
                }

                return;
            }

            if (term.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                string wanted = term[5..].Replace('/', '\\');
                if (wanted.Length > 0)
                {
                    _paths.Add(wanted);
                }

                return;
            }

            if (term.StartsWith("type:", StringComparison.OrdinalIgnoreCase) || term.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
            {
                string wanted = term[5..];
                bool matched = false;
                foreach (var category in FileCategoryInfo.All)
                {
                    if (FileCategoryInfo.DisplayName(category).Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                        FileCategoryInfo.ShortName(category).Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                        category.ToString().Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        _categories.Add(category);
                        matched = true;
                    }
                }

                // An unknown type matches nothing (the literal "type:xyz" is not part of any file name),
                // even when another type: term in the same query was recognized.
                if (!matched)
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

        /// <summary>Cheap checks first; the path is only built when a path term needs it.</summary>
        public bool Matches(SearchCandidate item)
        {
            if (!item.IsFile && FilesOnly)
            {
                return false;
            }

            if (item.Size < _minSize || item.Size > _maxSize)
            {
                return false;
            }

            if (HasDateFilter && (item.LastWriteUtc <= 0 || item.LastWriteUtc >= _modifiedBefore || item.LastWriteUtc < _modifiedAfter))
            {
                return false;
            }

            if (_categories.Count > 0 && !_categories.Contains(item.Category))
            {
                return false;
            }

            string name = item.Name;
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
                if (item.IsFile ? !name.EndsWith(e, StringComparison.OrdinalIgnoreCase) : !name.Equals(e, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            foreach (var p in _paths)
            {
                if (!item.Path.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly record struct Token(string Text, bool Quoted, bool Negated);

    /// <summary>
    /// Splits on whitespace. A token that is entirely quoted is literal; quotes inside a token group spaces
    /// (<c>path:"Program Files"</c>). A leading <c>-</c> negates a token. <c>&gt; 5GB</c> is joined to one token.
    /// </summary>
    private static IEnumerable<Token> Tokenize(string text)
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

            bool negated = false;
            if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                negated = true;
                i++;
            }

            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0)
                {
                    end = text.Length;
                }

                yield return new Token(text[(i + 1)..end], Quoted: true, negated);
                i = end + 1;
                continue;
            }

            var token = new System.Text.StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                if (text[i] == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        end = text.Length;
                    }

                    token.Append(text, i + 1, end - i - 1);
                    i = Math.Min(text.Length, end + 1);
                    continue;
                }

                token.Append(text[i]);
                i++;
            }

            // Allow "> 5GB" (operator separated from the value by a space).
            string value = token.ToString();
            if (value is ">" or "<" or ">=" or "<=")
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

                value += text[valueStart..i];
            }

            yield return new Token(value, Quoted: false, negated);
        }
    }
}

/// <summary>
/// The item a query is tested against. <see cref="SearchEngine"/> reuses one instance for every item,
/// and the path is only built when a <c>path:</c> term asks for it.
/// </summary>
public sealed class SearchCandidate
{
    private readonly Func<SearchCandidate, string> _resolvePath;
    private string? _path;

    public SearchCandidate(Func<SearchCandidate, string> resolvePath) => _resolvePath = resolvePath;

    public bool IsFile { get; private set; }

    public int Index { get; private set; }

    public string Name { get; private set; } = "";

    public long Size { get; private set; }

    public FileCategory Category { get; private set; }

    /// <summary>FILETIME (UTC), or 0 when unknown.</summary>
    public long LastWriteUtc { get; private set; }

    public string Path => _path ??= _resolvePath(this);

    public SearchCandidate Set(bool isFile, int index, string name, long size, FileCategory category = FileCategory.Other, long lastWriteUtc = 0)
    {
        IsFile = isFile;
        Index = index;
        Name = name;
        Size = size;
        Category = category;
        LastWriteUtc = lastWriteUtc;
        _path = null;
        return this;
    }
}
