using BenchmarkDotNet.Attributes;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;
using SpaceLens.Core.Search;

namespace SpaceLens.Benchmarks;

[MemoryDiagnoser]
public class ClassificationBenchmarks
{
    private string[] _names = [];

    [GlobalSetup]
    public void Setup()
    {
        string[] ext = ["txt", "DLL", "png", "json", "mkv", "zip", "vmdk", "", "tar.gz", "verylongextensionname", "iso", "pak"];
        _names = Enumerable.Range(0, 10_000).Select(i => $"some_file_name_{i}.{ext[i % ext.Length]}").ToArray();
    }

    [Benchmark(Description = "Classify 10k names (span lookup)")]
    public int Classify()
    {
        int sum = 0;
        foreach (var n in _names)
        {
            sum += (int)ExtensionClassifier.Classify(n);
        }

        return sum;
    }

    [Benchmark(Baseline = true, Description = "Classify 10k names (Path.GetExtension + string dictionary)")]
    public int ClassifyNaive()
    {
        int sum = 0;
        var map = NaiveMap;
        foreach (var n in _names)
        {
            string ext = Path.GetExtension(n).TrimStart('.').ToLowerInvariant();
            sum += map.TryGetValue(ext, out var c) ? (int)c : 0;
        }

        return sum;
    }

    private static readonly Dictionary<string, FileCategory> NaiveMap = new()
    {
        ["txt"] = FileCategory.Document, ["dll"] = FileCategory.Executable, ["png"] = FileCategory.Image,
        ["mkv"] = FileCategory.Video, ["zip"] = FileCategory.Archive, ["vmdk"] = FileCategory.VirtualMachine,
        ["iso"] = FileCategory.DiskImage, ["pak"] = FileCategory.GameData, ["gz"] = FileCategory.Archive,
    };
}

[MemoryDiagnoser]
public class TopNBenchmarks
{
    private long[] _sizes = [];

    [Params(100_000, 1_000_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);
        _sizes = Enumerable.Range(0, Count).Select(_ => random.NextInt64(0, 1L << 36)).ToArray();
    }

    [Benchmark(Description = "Top 100 via bounded heap")]
    public long Heap()
    {
        var top = new TopN<int>(100);
        for (int i = 0; i < _sizes.Length; i++)
        {
            top.Offer(_sizes[i], i);
        }

        return top.ToSortedList()[0].Key;
    }

    [Benchmark(Baseline = true, Description = "Top 100 via full sort")]
    public long FullSort()
    {
        var copy = (long[])_sizes.Clone();
        Array.Sort(copy);
        return copy[^1];
    }
}

[MemoryDiagnoser]
public class TreeBenchmarks
{
    private ScanTree _tree = null!;
    private int[] _sampleDirs = [];

    [Params(10_000, 100_000)]
    public int Directories { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _tree = SyntheticTree.BuildInMemory(Directories, filesPerDir: 10);
        var random = new Random(3);
        _sampleDirs = Enumerable.Range(0, 1000).Select(_ => random.Next(1, _tree.DirectoryCount)).ToArray();
    }

    [Benchmark(Description = "Build tree + aggregate (10 files/dir)")]
    public long BuildAndAggregate() => SyntheticTree.BuildInMemory(Directories, filesPerDir: 10).Root.TotalSize;

    [Benchmark(Description = "Reconstruct 1000 paths from parent chain")]
    public int PathReconstruction()
    {
        int length = 0;
        foreach (var d in _sampleDirs)
        {
            length += _tree.GetPath(d).Length;
        }

        return length;
    }

    [Benchmark(Description = "Disjoint breakdown (40 items)")]
    public int Breakdown40() => Breakdown.Build(_tree).Count;

    [Benchmark(Description = "Sort children of 1000 directories")]
    public int SortChildren()
    {
        int n = 0;
        foreach (var d in _sampleDirs)
        {
            n += Breakdown.SortedChildren(_tree, d).Count;
        }

        return n;
    }

    [Benchmark(Description = "Large files >1GB (all indexed files)")]
    public int LargeFiles() => Breakdown.LargeFiles(_tree, 1L << 30).Count;

    [Benchmark(Description = "Search 'dir_1*' over index")]
    public int Search() => SearchEngine.Search(_tree, SearchQuery.Parse("dir_1* >1MB")).TotalMatches;
}
