using System.IO.Enumeration;
using SpaceLens.Core.Scanning;

namespace SpaceLens.Mac;

/// <summary>
/// Directory enumeration for macOS and Linux: .NET's FileSystemEnumerator (one getdirentries/getdents
/// batch per directory, no per-entry allocation), plus the allocated size from lstat for files of at least
/// <see cref="AllocatedSizeThreshold"/> bytes, where sparse files and clones make a real difference. Smaller
/// files count with their length. Symbolic links are reported as links and never followed.
/// </summary>
public sealed class UnixDirectoryEnumeratorFactory(long allocatedSizeThreshold = 1L << 20) : IDirectoryEnumeratorFactory
{
    public long AllocatedSizeThreshold { get; } = allocatedSizeThreshold;

    public string Name => UnixStat.IsAvailable ? "Unix (FileSystemEnumerator + lstat)" : "Unix (FileSystemEnumerator)";

    /// <summary>No reparse tags on Unix: every directory link is treated as a link (not followed).</summary>
    public bool ReportsReparseTags => false;

    public IDirectoryEnumerator Create() => new Enumerator(AllocatedSizeThreshold);

    private sealed class Enumerator(long threshold) : IDirectoryEnumerator
    {
        private static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        public int Enumerate(string path, IDirectoryEntrySink sink)
        {
            try
            {
                using var enumerator = new SinkEnumerator(path, sink, threshold);
                while (enumerator.MoveNext())
                {
                }

                return enumerator.Error;
            }
            catch (UnauthorizedAccessException)
            {
                return 5;
            }
            catch (DirectoryNotFoundException)
            {
                return 3;
            }
            catch (PathTooLongException)
            {
                return 206;
            }
            catch (IOException ex)
            {
                int code = ex.HResult & 0xFFFF;
                return code == 0 ? 1 : code;
            }
        }

        public void Dispose()
        {
        }

        private sealed class SinkEnumerator(string directory, IDirectoryEntrySink sink, long threshold)
            : FileSystemEnumerator<byte>(directory, Options)
        {
            private readonly bool _allocatedSizes = UnixStat.IsAvailable;

            public int Error { get; private set; }

            protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
            {
                long allocated = -1;
                if (_allocatedSizes && !entry.IsDirectory && entry.Length >= threshold && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    allocated = UnixStat.AllocatedSize(entry.ToFullPath());
                }

                sink.OnEntry(new RawDirectoryEntry(entry.FileName, entry.Attributes, entry.Length, allocated, entry.LastWriteTimeUtc.ToFileTime(), reparseTag: 0));
                return false;
            }

            protected override bool ContinueOnError(int error)
            {
                Error = error;
                return false;
            }

            protected override byte TransformEntry(ref FileSystemEntry entry) => 0;
        }
    }
}
