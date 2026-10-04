using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceLens.Windows.Native;

/// <summary>
/// Win32 declarations used by SpaceLens. All calls are documented Win32 APIs; no undocumented NT
/// functions are used.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint FILE_SHARE_ALL = 0x7; // read | write | delete
    public const uint OPEN_EXISTING = 3;

    /// <summary>Required to obtain a handle to a directory.</summary>
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_ELEVATION_REQUIRED = 740;
    public const int ERROR_CANCELLED = 1223;

    /// <summary>
    /// FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryInfo: returns a buffer of FILE_FULL_DIR_INFO records,
    /// i.e. many directory entries per system call, including allocation size and (for reparse points)
    /// the reparse tag in the EaSize field.
    /// </summary>
    public const int FileFullDirectoryInfo = 14;

    /// <summary>
    /// CreateFileW: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew
    /// Used with FILE_FLAG_BACKUP_SEMANTICS and FILE_LIST_DIRECTORY to open a directory for enumeration.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    /// <summary>
    /// GetFileInformationByHandleEx: https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
    /// With FileFullDirectoryInfo, each call fills the buffer with as many entries as fit and returns
    /// FALSE with ERROR_NO_MORE_FILES at the end of the directory.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, void* buffer, uint bufferSize);

    public const uint FILE_READ_ATTRIBUTES = 0x0080;

    /// <summary>Open the reparse point itself: never follows a link and never recalls a cloud file.</summary>
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    /// <summary>FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo: FILE_ATTRIBUTE_TAG_INFO { FileAttributes, ReparseTag }.</summary>
    public const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION: https://learn.microsoft.com/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information</summary>
    /// FILETIME members are pairs of DWORDs, so the structure is 4-byte aligned (52 bytes).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>
    /// GetFileInformationByHandle: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
    /// The volume serial number and file index identify a file; hard links share them.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    public const int FindExInfoBasic = 1;          // skip 8.3 short names
    public const int FindExSearchNameMatch = 0;
    public const int FIND_FIRST_EX_LARGE_FETCH = 2; // larger internal buffer for directory queries

    /// <summary>
    /// FindFirstFileExW: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-findfirstfileexw
    /// Classic enumeration API; kept as a benchmark baseline.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindFirstFileEx(string fileName, int infoLevel, Win32FindData* findData, int searchOp, nint searchFilter, int additionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(nint findFile, Win32FindData* findData);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint findFile);

    /// <summary>WIN32_FIND_DATAW. FILETIME members are pairs of DWORDs, hence 4-byte packing.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Win32FindData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0; // reparse tag when FILE_ATTRIBUTE_REPARSE_POINT is set
        public uint Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateFileName[14];
    }

    /// <summary>
    /// GetVolumeInformationW: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, int fileSystemNameSize);

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    /// <summary>
    /// DeviceIoControl: https://learn.microsoft.com/windows/win32/api/ioapiset/nf-ioapiset-deviceiocontrol
    /// Used for IOCTL_STORAGE_QUERY_PROPERTY (bus type, seek penalty = HDD vs SSD) on a volume handle
    /// opened with zero access rights, which does not require administrator privileges.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct StoragePropertyQuery
    {
        public int PropertyId;  // StorageDeviceProperty = 0, StorageDeviceSeekPenaltyProperty = 7
        public int QueryType;   // PropertyStandardQuery = 0
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsnJournalData
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    // ---------------------------------------------------------------------------------------------
    // Shell
    // ---------------------------------------------------------------------------------------------

    public const uint FO_DELETE = 3;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint Func;
        public char* From;   // double-null-terminated list
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    /// <summary>
    /// SHFileOperationW: https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shfileoperationw
    /// With FO_DELETE + FOF_ALLOWUNDO the items are moved to the Recycle Bin. FOF_WANTNUKEWARNING makes
    /// Windows warn if an item is too large for the Recycle Bin and would be deleted permanently.
    /// </summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation(ShFileOpStruct* fileOp);

    [StructLayout(LayoutKind.Sequential)]
    public struct ShQueryRBInfo
    {
        public int Size;
        public long SizeBytes;
        public long NumItems;
    }

    /// <summary>SHQueryRecycleBinW: size and item count of the Recycle Bin for one drive.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHQueryRecycleBin(string? rootPath, ref ShQueryRBInfo info);

    /// <summary>SHEmptyRecycleBinW: empties the Recycle Bin with the standard Windows confirmation and progress UI.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHEmptyRecycleBin(nint hwnd, string? rootPath, uint flags);

    public const uint SHOP_FILEPATH = 2;

    /// <summary>SHObjectProperties: shows the standard Explorer Properties dialog for a file or folder.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHObjectProperties", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SHObjectProperties(nint hwnd, uint shopObjectType, string objectName, string? propertyPage);

    [LibraryImport("shell32.dll", EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string name, nint bindingContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    /// <summary>SHOpenFolderAndSelectItems: opens Explorer with the item selected (works for long paths, unlike "explorer /select").</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHOpenFolderAndSelectItems(nint pidlFolder, uint cidl, nint* apidl, uint flags);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);
}
