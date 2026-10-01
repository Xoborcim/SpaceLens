using SpaceLens.Core.Models;

namespace SpaceLens.Core.Classification;

/// <summary>
/// Maps file extensions to <see cref="FileCategory"/> without allocating: lookups use a
/// span-based alternate lookup on a case-insensitive dictionary.
/// </summary>
public static class ExtensionClassifier
{
    private static readonly Dictionary<string, FileCategory> Map = Build();
    private static readonly Dictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> SpanLookup =
        Map.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Classifies a file by the extension of <paramref name="fileName"/> (no leading path needed).</summary>
    public static FileCategory Classify(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return FileCategory.Other;
        }

        ReadOnlySpan<char> ext = fileName[(dot + 1)..];
        if (ext.Length > 12)
        {
            return FileCategory.Other;
        }

        return SpanLookup.TryGetValue(ext, out var category) ? category : FileCategory.Other;
    }

    /// <summary>Returns the extension (without dot, lower-case) or empty.</summary>
    public static string GetExtension(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1 ? string.Empty : fileName[(dot + 1)..].ToString().ToLowerInvariant();
    }

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);

        void Add(FileCategory category, string extensions)
        {
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                map[ext] = category;
            }
        }

        Add(FileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg m2ts mts ts vob 3gp ogv braw r3d prores mxf");
        Add(FileCategory.Audio, "mp3 wav flac aac ogg m4a wma opus aiff aif alac mid midi");
        Add(FileCategory.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd psb svg ico avif exr hdr tga dds kra xcf");
        Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md epub mobi csv one pst ost msg eml");
        Add(FileCategory.Archive, "zip rar 7z tar gz tgz bz2 xz zst lz4 cab tbz2 txz lzma");
        Add(FileCategory.DiskImage, "iso img dmg wim esd swm bin cue nrg");
        Add(FileCategory.VirtualMachine, "vmdk vdi vhd vhdx avhd avhdx qcow2 qcow vmem vmsn vmss nvram ova ovf hdd");
        Add(FileCategory.Installer, "msi msix msixbundle appx appxbundle msp msu");
        Add(FileCategory.Executable, "exe dll sys ocx so dylib lib a node winmd");
        Add(FileCategory.GameData, "pak vpk bsa ba2 forge wad bundle assets resource upk uasset umap ucas utoc bnk pck arc rpf big gcf ff unity3d xnb mpq");
        Add(FileCategory.Database, "db sqlite sqlite3 mdf ldf ndf mdb accdb ibd frm dbf realm leveldb ldb sst");
        Add(FileCategory.DeveloperFile, "pdb obj o ilk pch ipch idb class jar war nupkg whl pyc tlog sdf vsidx cache");
        Add(FileCategory.SystemFile, "etl evtx cat mum manifest regtrans-ms blf mui");
        Add(FileCategory.LogsAndDumps, "log dmp mdmp hdmp trace");

        // Windows-managed root files are detected separately by name; ".sys" stays "Executable" (drivers).
        return map;
    }
}
