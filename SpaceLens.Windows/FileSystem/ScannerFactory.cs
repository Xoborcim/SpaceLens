using SpaceLens.Core.Scanning;

namespace SpaceLens.Windows.FileSystem;

public enum ScanEngine
{
    /// <summary>Default: GetFileInformationByHandleEx batches, reports allocated size and reparse tags.</summary>
    Native,

    /// <summary>.NET FileSystemEnumerator; used for comparison.</summary>
    Managed,

    /// <summary>FindFirstFileEx; used for comparison.</summary>
    FindFirstFile,
}

public static class ScannerFactory
{
    public static IDiskScanner Create(ScanEngine engine) => engine switch
    {
        ScanEngine.Managed => new ParallelDirectoryScanner(new ManagedDirectoryEnumeratorFactory()),
        ScanEngine.FindFirstFile => new ParallelDirectoryScanner(new FindFirstFileEnumeratorFactory()),
        _ => new ParallelDirectoryScanner(new FileFullDirInfoEnumeratorFactory()),
    };
}
