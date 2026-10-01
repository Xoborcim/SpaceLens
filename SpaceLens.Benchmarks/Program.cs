using BenchmarkDotNet.Running;
using SpaceLens.Benchmarks;
using SpaceLens.Windows.FileSystem;

// Usage:
//   SpaceLens.Benchmarks synthetic [--sizes 10000,100000,1000000] [--runs 5]
//   SpaceLens.Benchmarks scan <path> [--runs 3] [--engines native,managed,findfirst] [--workers 1,4,8,12]
//   SpaceLens.Benchmarks describe <path> [--engine native]
//   SpaceLens.Benchmarks micro [BenchmarkDotNet args]

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

string? Option(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

IEnumerable<ScanEngine> ParseEngines(string? value) =>
    (value ?? "native,findfirst,managed").Split(',').Select(e => e.Trim().ToLowerInvariant() switch
    {
        "managed" => ScanEngine.Managed,
        "findfirst" => ScanEngine.FindFirstFile,
        _ => ScanEngine.Native,
    });

int[]? ParseInts(string? value) => value?.Split(',').Select(int.Parse).ToArray();

switch (mode)
{
    case "synthetic":
    {
        string baseDir = Option("--dir") ?? Path.Combine(Path.GetTempPath(), "SpaceLensBench");
        int runs = int.Parse(Option("--runs") ?? "5");
        foreach (int size in ParseInts(Option("--sizes")) ?? [10_000, 100_000, 1_000_000])
        {
            string root = Path.Combine(baseDir, $"tree_{size}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SyntheticTree.Generate(root, size);
            Console.WriteLine($"Synthetic tree {size:N0} files ready in {sw.Elapsed.TotalSeconds:0.0}s: {root}");
            await ScanHarness.RunAsync(root, ParseEngines(Option("--engines")), runs, ParseInts(Option("--workers")));
            Console.WriteLine();
        }

        break;
    }

    case "scan":
        await ScanHarness.RunAsync(args[1], ParseEngines(Option("--engines")), int.Parse(Option("--runs") ?? "3"), ParseInts(Option("--workers")));
        break;

    case "describe":
        await ScanHarness.DescribeAsync(args[1], ParseEngines(Option("--engine") ?? "native").First());
        break;

    case "opencost":
        OpenCostProbe.Run(args[1], int.Parse(Option("--limit") ?? "5000"));
        break;

    case "micro":
        BenchmarkSwitcher.FromAssembly(typeof(SyntheticTree).Assembly).Run(args[1..]);
        break;

    default:
        Console.WriteLine("Modes: synthetic | scan <path> | describe <path> | micro");
        break;
}
