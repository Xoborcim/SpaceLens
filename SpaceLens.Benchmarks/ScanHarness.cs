using System.Diagnostics;
using SpaceLens.Core.Aggregation;
using SpaceLens.Core.Formatting;
using SpaceLens.Core.Models;
using SpaceLens.Core.Scanning;
using SpaceLens.Windows.FileSystem;

namespace SpaceLens.Benchmarks;

/// <summary>
/// End-to-end scan timing on real directory trees. Reports throughput, peak memory and GC allocation
/// per engine. Runs each configuration several times and reports the median (first run is a warm-up
/// for the OS metadata cache unless --cold is used).
/// </summary>
public static class ScanHarness
{
    public static async Task RunAsync(string root, IEnumerable<ScanEngine> engines, int runs, IEnumerable<int>? workerCounts = null)
    {
        Console.WriteLine($"Scanning {root} ({runs} runs per configuration, median reported)");
        Console.WriteLine($"{"Engine",-34} {"Workers",7} {"Files",12} {"Dirs",10} {"Size",10} {"Time",9} {"Files/s",11} {"Alloc",9} {"PeakWS",9} {"Errors",6}");

        foreach (var engine in engines)
        {
            foreach (int workers in workerCounts ?? [0])
            {
                var results = new List<(ScanResult Result, long Allocated, long PeakWs)>();
                for (int i = 0; i < runs; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var tree = new ScanTree(root);
                    var scanner = ScannerFactory.Create(engine);
                    var result = await scanner.ScanAsync(tree, new ScanOptions { MaxParallelism = workers, LowPriorityThreads = false }, null, CancellationToken.None);
                    long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                    using var process = Process.GetCurrentProcess();
                    results.Add((result, allocated, process.PeakWorkingSet64));
                    GC.KeepAlive(tree);
                }

                var median = results.OrderBy(r => r.Result.Duration).ElementAt(results.Count / 2);
                var r = median.Result;
                Console.WriteLine(
                    $"{r.Scanner,-34} {r.Workers,7} {r.Files,12:N0} {r.Directories,10:N0} {SizeFormatter.Format(r.Bytes),10} " +
                    $"{r.Duration.TotalSeconds,8:0.000}s {r.FilesPerSecond,11:N0} {SizeFormatter.Format(median.Allocated),9} {SizeFormatter.Format(median.PeakWs),9} {r.Errors,6}");
            }
        }
    }

    /// <summary>Prints a diagnostic summary for a single scan in the format shown in the app's debug panel.</summary>
    public static async Task DescribeAsync(string root, ScanEngine engine)
    {
        long before = GC.GetTotalAllocatedBytes(true);
        var tree = new ScanTree(root);
        var scanner = ScannerFactory.Create(engine);
        var sw = Stopwatch.StartNew();
        var result = await scanner.ScanAsync(tree, new ScanOptions(), null, CancellationToken.None);
        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        using var process = Process.GetCurrentProcess();

        Console.WriteLine("Scan completed");
        Console.WriteLine();
        Console.WriteLine($"Files:        {result.Files:N0}");
        Console.WriteLine($"Folders:      {result.Directories:N0}");
        Console.WriteLine($"Size:         {SizeFormatter.Format(result.Bytes)}");
        Console.WriteLine($"Time:         {result.Duration.TotalSeconds:0.00} sec");
        Console.WriteLine($"Rate:         {result.FilesPerSecond / 1000:0}k files/sec, {result.DirectoriesPerSecond / 1000:0.0}k folders/sec, {SizeFormatter.Format((long)result.BytesPerSecond)}/sec");
        Console.WriteLine($"Indexed:      {tree.FileRecordCount:N0} files >= {SizeFormatter.Format(tree.FileIndexThreshold)}");
        Console.WriteLine($"Records:      {SizeFormatter.Format(tree.ApproximateRecordBytes)} (struct arrays)");
        Console.WriteLine($"GC alloc:     {SizeFormatter.Format(allocated)}  (gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)})");
        Console.WriteLine($"Managed heap: {SizeFormatter.Format(GC.GetTotalMemory(false))}");
        Console.WriteLine($"Peak RAM:     {SizeFormatter.Format(process.PeakWorkingSet64)}");
        Console.WriteLine($"Errors:       {result.Errors:N0} inaccessible locations");

        var sw2 = Stopwatch.StartNew();
        var breakdown = Breakdown.Build(tree);
        var large = Breakdown.LargeFiles(tree, 1L << 30);
        sw2.Stop();
        Console.WriteLine($"Breakdown + large files query: {sw2.Elapsed.TotalMilliseconds:0.0} ms");
        Console.WriteLine();
        Console.WriteLine("Largest items:");
        foreach (var item in breakdown.Take(15))
        {
            string name = item.Kind switch
            {
                BreakdownItemKind.File => tree.GetFilePath(item.Index),
                BreakdownItemKind.LooseFiles => tree.GetPath(item.Index) + " (files)",
                _ => tree.GetPath(item.Index),
            };
            Console.WriteLine($"  {SizeFormatter.Format(item.Size),10}  {name}");
        }
    }
}
