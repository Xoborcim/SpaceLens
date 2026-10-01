using System.Runtime.InteropServices;
using SpaceLens.Core.Scanning;
using SpaceLens.Windows.Native;

namespace SpaceLens.Windows.FileSystem;

/// <summary>
/// Fast enumerator based on <c>GetFileInformationByHandleEx(FileFullDirectoryInfo)</c>.
/// <para>
/// One handle is opened per directory and the entries are read in 64 KB batches into a buffer owned by
/// the worker, so a directory with a few hundred entries typically costs three system calls. Each
/// FILE_FULL_DIR_INFO record carries the allocation size (real on-disk usage, which is what matters for
/// OneDrive placeholders and compressed files) and, for reparse points, the reparse tag in EaSize.
/// Names are passed to the sink as spans over the native buffer: no per-entry allocation.
/// </para>
/// </summary>
public sealed class FileFullDirInfoEnumeratorFactory : IDirectoryEnumeratorFactory
{
    public const int DefaultBufferSize = 64 * 1024;

    public FileFullDirInfoEnumeratorFactory(int bufferSize = DefaultBufferSize) => BufferSize = bufferSize;

    public int BufferSize { get; }

    public string Name => "Native (FileFullDirectoryInfo)";

    public bool ReportsReparseTags => true;

    public IDirectoryEnumerator Create() => new Enumerator(BufferSize);

    private sealed unsafe class Enumerator : IDirectoryEnumerator
    {
        // FILE_FULL_DIR_INFO field offsets (all entries are 8-byte aligned).
        private const int OffsetNextEntry = 0;
        private const int OffsetLastWriteTime = 24;
        private const int OffsetEndOfFile = 40;
        private const int OffsetAllocationSize = 48;
        private const int OffsetFileAttributes = 56;
        private const int OffsetFileNameLength = 60;
        private const int OffsetEaSize = 64;
        private const int OffsetFileName = 68;

        private readonly uint _bufferSize;
        private byte* _buffer;

        public Enumerator(int bufferSize)
        {
            _bufferSize = (uint)bufferSize;
            _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)bufferSize, 8);
        }

        public int Enumerate(string path, IDirectoryEntrySink sink)
        {
            using var handle = NativeMethods.CreateFile(
                path,
                NativeMethods.FILE_LIST_DIRECTORY | NativeMethods.SYNCHRONIZE,
                NativeMethods.FILE_SHARE_ALL,
                0,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
                0);

            if (handle.IsInvalid)
            {
                return Marshal.GetLastPInvokeError();
            }

            while (NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileFullDirectoryInfo, _buffer, _bufferSize))
            {
                byte* entry = _buffer;
                while (true)
                {
                    uint next = *(uint*)(entry + OffsetNextEntry);
                    int nameChars = (int)(*(uint*)(entry + OffsetFileNameLength) / 2);
                    var name = new ReadOnlySpan<char>(entry + OffsetFileName, nameChars);

                    if (!IsDotEntry(name))
                    {
                        uint attributes = *(uint*)(entry + OffsetFileAttributes);
                        uint reparseTag = (attributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? *(uint*)(entry + OffsetEaSize) : 0;
                        sink.OnEntry(new RawDirectoryEntry(
                            name,
                            (FileAttributes)attributes,
                            *(long*)(entry + OffsetEndOfFile),
                            *(long*)(entry + OffsetAllocationSize),
                            *(long*)(entry + OffsetLastWriteTime),
                            reparseTag));
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    entry += next;
                }
            }

            int error = Marshal.GetLastPInvokeError();
            return error == NativeMethods.ERROR_NO_MORE_FILES ? 0 : error;
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                NativeMemory.AlignedFree(_buffer);
                _buffer = null;
            }
        }
    }

    internal static bool IsDotEntry(ReadOnlySpan<char> name) =>
        name.Length <= 2 && name.Length > 0 && name[0] == '.' && (name.Length == 1 || name[1] == '.');
}

/// <summary>
/// Enumerator based on <c>FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH)</c>.
/// Kept as a benchmark baseline: one call per entry and no allocation size.
/// </summary>
public sealed class FindFirstFileEnumeratorFactory : IDirectoryEnumeratorFactory
{
    public string Name => "Native (FindFirstFileEx)";

    public bool ReportsReparseTags => true;

    public IDirectoryEnumerator Create() => new Enumerator();

    private sealed unsafe class Enumerator : IDirectoryEnumerator
    {
        public int Enumerate(string path, IDirectoryEntrySink sink)
        {
            NativeMethods.Win32FindData data;
            string pattern = path.EndsWith('\\') ? path + "*" : path + "\\*";
            nint handle = NativeMethods.FindFirstFileEx(pattern, NativeMethods.FindExInfoBasic, &data, NativeMethods.FindExSearchNameMatch, 0, NativeMethods.FIND_FIRST_EX_LARGE_FETCH);
            if (handle == -1)
            {
                int error = Marshal.GetLastPInvokeError();
                return error == NativeMethods.ERROR_FILE_NOT_FOUND ? 0 : error;
            }

            try
            {
                do
                {
                    var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data.FileName);
                    if (FileFullDirInfoEnumeratorFactory.IsDotEntry(name))
                    {
                        continue;
                    }

                    long length = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
                    uint tag = (data.FileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? data.Reserved0 : 0;
                    sink.OnEntry(new RawDirectoryEntry(name, (FileAttributes)data.FileAttributes, length, -1, data.LastWriteTime, tag));
                }
                while (NativeMethods.FindNextFile(handle, &data));

                int last = Marshal.GetLastPInvokeError();
                return last == NativeMethods.ERROR_NO_MORE_FILES ? 0 : last;
            }
            finally
            {
                NativeMethods.FindClose(handle);
            }
        }

        public void Dispose()
        {
        }
    }
}
