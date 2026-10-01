using SpaceLens.Core.Models;

namespace SpaceLens.Benchmarks;

/// <summary>Creates reproducible synthetic directory trees on disk and in memory.</summary>
public static class SyntheticTree
{
    private static readonly string[] Extensions = ["txt", "dll", "png", "json", "js", "cs", "mp4", "zip", "log", "dat", "exe", "jpg", "pak", "iso", "xml"];

    /// <summary>
    /// Writes a tree with roughly <paramref name="fileCount"/> files, ~20 files per directory, fan-out 6.
    /// Files are 0-600 bytes so that NTFS stores most of them resident in the MFT and the tree uses
    /// little disk space; the benchmark measures metadata enumeration, not data reads.
    /// </summary>
    public static void Generate(string root, int fileCount, int seed = 42)
    {
        if (Directory.Exists(root) && File.Exists(Path.Combine(root, ".complete")))
        {
            return;
        }

        Directory.CreateDirectory(root);
        int dirCount = Math.Max(1, fileCount / 20);
        var dirs = new List<string>(dirCount) { root };
        var random = new Random(seed);
        for (int i = 1; i < dirCount; i++)
        {
            string parent = dirs[(i - 1) / 6];
            dirs.Add(Path.Combine(parent, $"dir_{i:x5}"));
        }

        foreach (var dir in dirs)
        {
            Directory.CreateDirectory(dir);
        }

        var assignments = new int[fileCount];
        var sizes = new int[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            assignments[i] = random.Next(dirs.Count);
            sizes[i] = random.Next(0, 600);
        }

        Parallel.For(0, fileCount, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            string path = Path.Combine(dirs[assignments[i]], $"file_{i:x6}.{Extensions[i % Extensions.Length]}");
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None);
            if (sizes[i] > 0)
            {
                stream.SetLength(sizes[i]);
            }
        });

        File.WriteAllText(Path.Combine(root, ".complete"), fileCount.ToString());
    }

    /// <summary>Builds an in-memory tree with the given number of directories and files (no disk access).</summary>
    public static ScanTree BuildInMemory(int dirCount, int filesPerDir, int seed = 7, long indexThreshold = ScanTree.DefaultFileIndexThreshold)
    {
        var tree = new ScanTree(@"X:\", indexThreshold);
        var random = new Random(seed);
        var nodes = new List<int>(dirCount) { ScanTree.RootIndex };
        for (int i = 1; i < dirCount; i++)
        {
            int parent = nodes[(i - 1) / 8];
            nodes.Add(tree.AddDirectory(parent, $"dir_{i}"));
        }

        Span<long> catBytes = stackalloc long[FileCategoryInfo.Count];
        Span<long> catCounts = stackalloc long[FileCategoryInfo.Count];
        foreach (int node in nodes)
        {
            long own = 0;
            for (int f = 0; f < filesPerDir; f++)
            {
                long size = random.Next(0, 100) < 2 ? random.NextInt64(1L << 20, 8L << 30) : random.Next(0, 200_000);
                own += size;
                var category = (FileCategory)(f % FileCategoryInfo.Count);
                catBytes[(int)category] += size;
                catCounts[(int)category]++;
                if (size >= tree.FileIndexThreshold)
                {
                    tree.AddFile(node, $"file_{f}.{Extensions[f % Extensions.Length]}", size, category, 0);
                }
            }

            tree.CompleteDirectory(node, own, filesPerDir, tree.Dir(node).SubdirCount);
        }

        tree.AddCategoryTotals(catBytes, catCounts);
        return tree;
    }
}
