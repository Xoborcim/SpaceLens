using System.Text.Json;
using SpaceLens.Core.Export;
using SpaceLens.Core.Models;

namespace SpaceLens.Tests;

public class ExportTests
{
    [Fact]
    public void Folders_csv_lists_the_tree_depth_first_with_largest_folders_first()
    {
        var tree = ScanTreeTests.Sample();
        var writer = new StringWriter();
        ReportExporter.WriteFoldersCsv(tree, writer);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Path,Size (bytes),Files,Folders,Last modified (UTC)", lines[0]);
        var paths = lines.Skip(1).Select(l => l.Split(',')[0]).ToList();
        Assert.Equal([@"C:\", @"C:\Games", @"C:\Games\Big", @"C:\Users", @"C:\Users\me", @"C:\Users\me\Downloads", @"C:\Users\me\Videos"], paths);
        Assert.StartsWith(@"C:\Games\Big," + (20L << 30) + ",1000,0,", lines[3]);
    }

    [Fact]
    public void Files_csv_lists_large_files_largest_first()
    {
        var tree = ScanTreeTests.Sample();
        var writer = new StringWriter();
        ReportExporter.WriteFilesCsv(tree, writer);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith(@"C:\Users\me\Downloads\ubuntu.iso," + (8L << 30) + ",", lines[1]);
        Assert.StartsWith(@"C:\Users\me\Videos\movie.mkv,", lines[2]);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=HYPERLINK(1)", "'=HYPERLINK(1)")]
    [InlineData("-x,y", "\"'-x,y\"")]
    public void Csv_fields_are_quoted_and_never_formulas(string field, string expected) =>
        Assert.Equal(expected, ReportExporter.EscapeCsv(field));

    [Fact]
    public void Json_report_contains_summary_types_folders_and_files()
    {
        var tree = ScanTreeTests.Sample();
        tree.Metadata.CompletedUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        tree.CategoryBytes[(int)FileCategory.Video] = 3L << 30;
        tree.CategoryCounts[(int)FileCategory.Video] = 1;
        using var stream = new MemoryStream();
        ReportExporter.WriteJson(tree, stream);

        using var doc = JsonDocument.Parse(stream.ToArray());
        var root = doc.RootElement;
        Assert.Equal(@"C:\", root.GetProperty("root").GetString());
        Assert.Equal("2026-10-04T12:00:00Z", root.GetProperty("scannedUtc").GetString());
        Assert.Equal(tree.Root.TotalSize, root.GetProperty("totalSize").GetInt64());
        Assert.Equal("Videos", root.GetProperty("fileTypes")[0].GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("folders").GetArrayLength());
        Assert.Equal(2, root.GetProperty("largeFiles").GetArrayLength());
        Assert.Equal(@"C:\Users\me\Downloads\ubuntu.iso", root.GetProperty("largeFiles")[0].GetProperty("path").GetString());
    }
}
