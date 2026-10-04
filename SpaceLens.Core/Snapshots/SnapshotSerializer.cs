using System.IO.Compression;
using System.Text;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.Snapshots;

/// <summary>
/// Saves and loads a compact binary snapshot of a <see cref="ScanTree"/> (Brotli-compressed).
/// Only per-directory values are stored; aggregate totals are recomputed on load.
/// </summary>
public static class SnapshotSerializer
{
    private const uint Magic = 0x534C4E53; // "SNLS"
    private const int FormatVersion = 1;
    private const int MaxStoredErrors = 2000;

    public static void Save(ScanTree tree, Stream destination)
    {
        // Copy everything first while holding the tree's lock, so a removal made from the UI while the
        // (slow) compression runs cannot leave the file with counts that do not match its records.
        var capture = Capture(tree);

        using var compressor = new BrotliStream(destination, CompressionLevel.Fastest, leaveOpen: true);
        using var writer = new BinaryWriter(compressor, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(tree.RootPath);
        writer.Write(tree.FileIndexThreshold);

        var m = tree.Metadata;
        writer.Write(m.ScannerName);
        writer.Write(m.StartedUtc.Ticks);
        writer.Write((m.CompletedUtc ?? m.StartedUtc).Ticks);
        writer.Write(m.Duration.Ticks);
        writer.Write(m.WasCancelled);
        writer.Write(m.VolumeTotalBytes);
        writer.Write(m.VolumeFreeBytes);
        writer.Write(m.VolumeSerialNumber);
        writer.Write(m.VolumeLabel ?? "");
        writer.Write(m.FileSystem ?? "");
        writer.Write(m.UsnJournalId);
        writer.Write(m.UsnNextUsn);

        for (int i = 0; i < FileCategoryInfo.Count; i++)
        {
            writer.Write(capture.CategoryBytes[i]);
            writer.Write(capture.CategoryCounts[i]);
        }

        // Directories. Parents always precede children, which lets the loader rebuild links in one pass.
        writer.Write(capture.Dirs.Length);
        foreach (ref readonly var n in capture.Dirs.AsSpan())
        {
            writer.Write(n.Name ?? "");
            writer.Write(n.Parent);
            writer.Write((ushort)n.Flags);
            writer.Write(n.OwnSize);
            writer.Write(n.OwnFileCount);
            writer.Write(n.LastWriteUtc);
        }

        writer.Write(capture.Files.Count);
        foreach (var f in capture.Files)
        {
            writer.Write(f.Name);
            writer.Write(f.Directory);
            writer.Write(f.Size);
            writer.Write(f.LastWriteUtc);
            writer.Write((byte)f.Category);
            writer.Write((int)f.Attributes);
        }

        writer.Write(capture.ErrorCount);
        writer.Write(capture.Errors.Count);
        foreach (var e in capture.Errors)
        {
            writer.Write(e.Path);
            writer.Write(e.ErrorCode);
            writer.Write(e.Message);
        }
    }

    private sealed record SnapshotCapture(
        DirNode[] Dirs, List<FileRecord> Files, long[] CategoryBytes, long[] CategoryCounts, int ErrorCount, List<ScanError> Errors);

    private static SnapshotCapture Capture(ScanTree tree)
    {
        lock (tree.SyncRoot)
        {
            var dirs = new DirNode[tree.DirectoryCount];
            for (int i = 0; i < dirs.Length; i++)
            {
                dirs[i] = tree.Dir(i);
            }

            int fileCount = tree.FileRecordCount;
            var files = new List<FileRecord>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                ref var f = ref tree.File(i);
                if (!f.Removed)
                {
                    files.Add(f);
                }
            }

            return new SnapshotCapture(
                dirs, files, (long[])tree.CategoryBytes.Clone(), (long[])tree.CategoryCounts.Clone(),
                tree.ErrorCount, tree.Errors.Take(MaxStoredErrors).ToList());
        }
    }

    public static ScanTree Load(Stream source)
    {
        using var decompressor = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(new BufferedStream(decompressor, 1 << 16), Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a SpaceLens snapshot.");
        }

        int version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported snapshot version {version}.");
        }

        string root = reader.ReadString();
        long threshold = reader.ReadInt64();
        var tree = new ScanTree(root, threshold);

        var m = tree.Metadata;
        m.ScannerName = reader.ReadString();
        m.StartedUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        m.CompletedUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        m.Duration = new TimeSpan(reader.ReadInt64());
        m.WasCancelled = reader.ReadBoolean();
        m.VolumeTotalBytes = reader.ReadInt64();
        m.VolumeFreeBytes = reader.ReadInt64();
        m.VolumeSerialNumber = reader.ReadUInt32();
        m.VolumeLabel = reader.ReadString();
        m.FileSystem = reader.ReadString();
        m.UsnJournalId = reader.ReadUInt64();
        m.UsnNextUsn = reader.ReadInt64();
        m.LoadedFromSnapshot = true;

        for (int i = 0; i < FileCategoryInfo.Count; i++)
        {
            tree.CategoryBytes[i] = reader.ReadInt64();
            tree.CategoryCounts[i] = reader.ReadInt64();
        }

        int dirCount = reader.ReadInt32();
        for (int i = 0; i < dirCount; i++)
        {
            string name = reader.ReadString();
            int parent = reader.ReadInt32();
            var flags = (NodeFlags)reader.ReadUInt16();
            long ownSize = reader.ReadInt64();
            int ownFiles = reader.ReadInt32();
            long lastWrite = reader.ReadInt64();

            int index;
            if (i == 0)
            {
                index = ScanTree.RootIndex;
                tree.Dir(index).Flags = flags & ~NodeFlags.Removed;
            }
            else
            {
                if (parent < 0 || parent >= i)
                {
                    throw new InvalidDataException("Corrupt snapshot (parent order).");
                }

                if (tree.Dir(parent).IsRemoved)
                {
                    flags |= NodeFlags.Removed;
                }

                index = tree.AddDirectory(parent, name, flags, lastWrite);
                if ((flags & NodeFlags.Removed) != 0)
                {
                    // Keep the slot (indices must match) but detach it from the parent's child list.
                    tree.Dir(parent).FirstChild = tree.Dir(index).NextSibling;
                    tree.Dir(parent).SubdirCount--;
                }
            }

            ref var node = ref tree.Dir(index);
            node.OwnSize = ownSize;
            node.OwnFileCount = ownFiles;
        }

        int fileCount = reader.ReadInt32();
        for (int i = 0; i < fileCount; i++)
        {
            string name = reader.ReadString();
            int dir = reader.ReadInt32();
            long size = reader.ReadInt64();
            long lastWrite = reader.ReadInt64();
            var category = (FileCategory)reader.ReadByte();
            var attributes = (FileAttributes)reader.ReadInt32();
            if (dir < 0 || dir >= dirCount)
            {
                throw new InvalidDataException("Corrupt snapshot (file directory).");
            }

            if (!tree.Dir(dir).IsRemoved)
            {
                tree.AddFile(dir, name, size, category, lastWrite, attributes);
            }
        }

        int errorCount = reader.ReadInt32();
        int storedErrors = reader.ReadInt32();
        for (int i = 0; i < storedErrors; i++)
        {
            tree.AddStoredError(new ScanError(reader.ReadString(), reader.ReadInt32(), reader.ReadString()));
        }

        tree.SetErrorCount(errorCount);
        tree.RecomputeTotals();
        return tree;
    }

    public static void SaveToFile(ScanTree tree, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                Save(tree, stream);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static ScanTree LoadFromFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Load(stream);
    }
}
