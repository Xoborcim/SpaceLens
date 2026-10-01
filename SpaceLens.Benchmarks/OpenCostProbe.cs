using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceLens.Benchmarks;

/// <summary>
/// Diagnoses where per-directory time goes: opening the handle (absolute path vs. relative to the
/// parent handle via NtCreateFile) versus reading the entries.
/// </summary>
public static unsafe partial class OpenCostProbe
{
    public static void Run(string parent, int limit)
    {
        var children = Directory.EnumerateDirectories(parent).Take(limit).ToList();
        Console.WriteLine($"{children.Count} subdirectories of {parent}");

        // Warm-up pass.
        foreach (var c in children)
        {
            using var h = OpenAbsolute(c);
        }

        var sw = Stopwatch.StartNew();
        foreach (var c in children)
        {
            using var h = OpenAbsolute(c);
        }

        Console.WriteLine($"CreateFileW absolute open+close:    {sw.Elapsed.TotalMicroseconds / children.Count,8:0.0} us/dir");

        using var parentHandle = OpenAbsolute(parent);
        sw.Restart();
        foreach (var c in children)
        {
            using var h = OpenRelative(parentHandle, Path.GetFileName(c));
        }

        Console.WriteLine($"NtCreateFile relative open+close:   {sw.Elapsed.TotalMicroseconds / children.Count,8:0.0} us/dir");

        byte* buffer = (byte*)NativeMemory.AlignedAlloc(65536, 8);
        sw.Restart();
        long entries = 0;
        foreach (var c in children)
        {
            using var h = OpenAbsolute(c);
            while (GetFileInformationByHandleEx(h, 14, buffer, 65536))
            {
                entries++;
            }
        }

        Console.WriteLine($"Absolute open + full enumerate:     {sw.Elapsed.TotalMicroseconds / children.Count,8:0.0} us/dir ({entries} batches)");
        NativeMemory.AlignedFree(buffer);
    }

    private static SafeFileHandle OpenAbsolute(string path) =>
        CreateFile(@"\\?\" + path, 0x0001 | 0x00100000, 7, 0, 3, 0x02000000, 0);

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name)
    {
        fixed (char* pName = name)
        {
            var unicode = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = pName };
            bool added = false;
            parent.DangerousAddRef(ref added);
            try
            {
                var attributes = new ObjectAttributes
                {
                    Length = sizeof(ObjectAttributes),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = &unicode,
                    Attributes = 0x40, // OBJ_CASE_INSENSITIVE
                };
                const uint FILE_DIRECTORY_FILE = 0x1, FILE_SYNCHRONOUS_IO_NONALERT = 0x20, FILE_OPEN_FOR_BACKUP_INTENT = 0x4000;
                int status = NtCreateFile(out var handle, 0x0001 | 0x00100000, &attributes, out _, null, 0, 7, 1 /* FILE_OPEN */,
                    FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT | FILE_OPEN_FOR_BACKUP_INTENT, null, 0);
                return status >= 0 ? handle : new SafeFileHandle(0, false);
            }
            finally
            {
                if (added)
                {
                    parent.DangerousRelease();
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out SafeFileHandle handle, uint access, ObjectAttributes* attributes, out IoStatusBlock ioStatus, long* allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, void* eaBuffer, uint eaLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, void* buffer, uint bufferSize);
}
