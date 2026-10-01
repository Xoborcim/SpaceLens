using System.IO.Enumeration;

namespace SpaceLens.Core.Scanning;

/// <summary>
/// Baseline enumerator built on <see cref="FileSystemEnumerator{TResult}"/>. It is portable and
/// allocation-free per entry, but cannot report allocated size or reparse tags.
/// </summary>
public sealed class ManagedDirectoryEnumeratorFactory : IDirectoryEnumeratorFactory
{
    public string Name => "Managed (FileSystemEnumerator)";

    public bool ReportsReparseTags => false;

    public IDirectoryEnumerator Create() => new ManagedDirectoryEnumerator();

    private sealed class ManagedDirectoryEnumerator : IDirectoryEnumerator
    {
        private static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        public int Enumerate(string path, IDirectoryEntrySink sink)
        {
            try
            {
                using var enumerator = new SinkEnumerator(path, sink);
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

        private sealed class SinkEnumerator : FileSystemEnumerator<byte>
        {
            private readonly IDirectoryEntrySink _sink;

            public SinkEnumerator(string directory, IDirectoryEntrySink sink)
                : base(directory, Options)
            {
                _sink = sink;
            }

            public int Error { get; private set; }

            protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
            {
                var raw = new RawDirectoryEntry(
                    entry.FileName,
                    entry.Attributes,
                    entry.Length,
                    allocatedSize: -1,
                    entry.LastWriteTimeUtc.ToFileTime(),
                    reparseTag: 0);
                _sink.OnEntry(raw);
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
