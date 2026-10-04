using System.Globalization;
using System.Text;
using System.Text.Json;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.Export;

/// <summary>
/// Writes scan results to CSV (for spreadsheets) or JSON (for scripts). Sizes are in bytes and times are
/// UTC in ISO 8601, so the output does not depend on the current culture.
/// </summary>
public static class ReportExporter
{
    /// <summary>
    /// Folders of at least <paramref name="minimumSize"/> bytes, depth first with the largest folder first
    /// at every level, so the file reads like the folder tree.
    /// </summary>
    public static void WriteFoldersCsv(ScanTree tree, TextWriter writer, long minimumSize = 1L << 20, CancellationToken cancellationToken = default)
    {
        writer.WriteLine("Path,Size (bytes),Files,Folders,Last modified (UTC)");
        foreach (int dir in FoldersInTreeOrder(tree, minimumSize, cancellationToken))
        {
            ref var node = ref tree.Dir(dir);
            WriteCsvRow(writer, tree.GetPath(dir), node.TotalSize.ToString(CultureInfo.InvariantCulture),
                node.TotalFiles.ToString(CultureInfo.InvariantCulture), node.TotalDirs.ToString(CultureInfo.InvariantCulture),
                FormatTime(node.LastWriteUtc));
        }
    }

    /// <summary>Indexed files of at least <paramref name="minimumSize"/> bytes, largest first.</summary>
    public static void WriteFilesCsv(ScanTree tree, TextWriter writer, long minimumSize = 1L << 20, int maxFiles = 100_000, CancellationToken cancellationToken = default)
    {
        writer.WriteLine("Path,Size (bytes),Type,Last modified (UTC)");
        int written = 0;
        foreach (int file in Breakdown.LargeFiles(tree, minimumSize, maxResults: maxFiles))
        {
            if ((++written & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ref var record = ref tree.File(file);
            WriteCsvRow(writer, tree.GetFilePath(file), record.Size.ToString(CultureInfo.InvariantCulture),
                FileCategoryInfo.DisplayName(record.Category), FormatTime(record.LastWriteUtc));
        }
    }

    /// <summary>A summary, the file types, the folders and the large files in one JSON document.</summary>
    public static void WriteJson(ScanTree tree, Stream stream, long minimumFolderSize = 1L << 20, long minimumFileSize = 1L << 20,
        int maxFiles = 100_000, CancellationToken cancellationToken = default)
    {
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        var m = tree.Metadata;
        ref var root = ref tree.Root;

        json.WriteStartObject();
        json.WriteString("root", tree.RootPath);
        json.WriteString("scannedUtc", (m.CompletedUtc ?? m.StartedUtc).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        json.WriteBoolean("complete", !m.WasCancelled);
        json.WriteNumber("totalSize", root.TotalSize);
        json.WriteNumber("totalFiles", root.TotalFiles);
        json.WriteNumber("totalFolders", root.TotalDirs);
        json.WriteNumber("inaccessibleLocations", tree.ErrorCount);
        if (m.VolumeTotalBytes > 0)
        {
            json.WriteNumber("volumeTotalBytes", m.VolumeTotalBytes);
            json.WriteNumber("volumeFreeBytes", m.VolumeFreeBytes);
        }

        json.WriteStartArray("fileTypes");
        foreach (var category in FileCategoryInfo.All.OrderByDescending(c => tree.CategoryBytes[(int)c]))
        {
            if (tree.CategoryBytes[(int)category] <= 0)
            {
                continue;
            }

            json.WriteStartObject();
            json.WriteString("type", FileCategoryInfo.DisplayName(category));
            json.WriteNumber("size", tree.CategoryBytes[(int)category]);
            json.WriteNumber("files", tree.CategoryCounts[(int)category]);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartArray("folders");
        foreach (int dir in FoldersInTreeOrder(tree, minimumFolderSize, cancellationToken))
        {
            ref var node = ref tree.Dir(dir);
            json.WriteStartObject();
            json.WriteString("path", tree.GetPath(dir));
            json.WriteNumber("size", node.TotalSize);
            json.WriteNumber("files", node.TotalFiles);
            json.WriteNumber("folders", node.TotalDirs);
            if (FormatTime(node.LastWriteUtc) is { Length: > 0 } modified)
            {
                json.WriteString("modifiedUtc", modified);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartArray("largeFiles");
        foreach (int file in Breakdown.LargeFiles(tree, minimumFileSize, maxResults: maxFiles))
        {
            ref var record = ref tree.File(file);
            json.WriteStartObject();
            json.WriteString("path", tree.GetFilePath(file));
            json.WriteNumber("size", record.Size);
            json.WriteString("type", FileCategoryInfo.DisplayName(record.Category));
            if (FormatTime(record.LastWriteUtc) is { Length: > 0 } modified)
            {
                json.WriteString("modifiedUtc", modified);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>Live folders of at least <paramref name="minimumSize"/>, depth first, larger siblings first, root included.</summary>
    private static IEnumerable<int> FoldersInTreeOrder(ScanTree tree, long minimumSize, CancellationToken cancellationToken)
    {
        var stack = new Stack<int>();
        stack.Push(ScanTree.RootIndex);
        int visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int dir = stack.Pop();
            if (tree.Dir(dir).TotalSize < minimumSize && dir != ScanTree.RootIndex)
            {
                continue;
            }

            yield return dir;

            // Push smallest first so the largest child is visited next.
            var children = Breakdown.SortedChildren(tree, dir);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                if (tree.Dir(children[i]).TotalSize >= minimumSize)
                {
                    stack.Push(children[i]);
                }
            }
        }
    }

    private static string FormatTime(long fileTimeUtc) =>
        fileTimeUtc > 0 ? DateTime.FromFileTimeUtc(fileTimeUtc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "";

    private static void WriteCsvRow(TextWriter writer, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(EscapeCsv(fields[i]));
        }

        writer.WriteLine();
    }

    /// <summary>
    /// RFC 4180 quoting. Fields starting with = + - @ are prefixed with an apostrophe so a spreadsheet
    /// never evaluates a file name as a formula (paths start with a drive letter, but folder names in a
    /// folder-rooted scan may not).
    /// </summary>
    internal static string EscapeCsv(string field)
    {
        if (field.Length > 0 && field[0] is '=' or '+' or '-' or '@')
        {
            field = "'" + field;
        }

        if (field.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return field;
        }

        var sb = new StringBuilder(field.Length + 2);
        sb.Append('"');
        sb.Append(field.Replace("\"", "\"\""));
        sb.Append('"');
        return sb.ToString();
    }
}
