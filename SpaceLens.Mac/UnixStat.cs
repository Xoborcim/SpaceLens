using System.Runtime.InteropServices;

namespace SpaceLens.Mac;

/// <summary>
/// Space a file really occupies (st_blocks × 512) through lstat. Sparse files, APFS clones and compressed
/// files can use far less than their length: Docker's Docker.raw may report 64 GB while using a few.
/// <para>
/// struct stat differs by platform; the layout in use is checked once against a file whose length is known,
/// and if it does not match (an unexpected platform), allocated sizes are simply not reported.
/// </para>
/// </summary>
public static unsafe partial class UnixStat
{
    private const int BufferSize = 256;

    private static readonly Lazy<bool> Verified = new(Verify);

    /// <summary>(offset of st_size, offset of st_blocks) for this platform, or null.</summary>
    private static readonly (int Size, int Blocks)? Layout =
        OperatingSystem.IsMacOS() ? (96, 104) :                 // Darwin struct stat with 64-bit inodes
        OperatingSystem.IsLinux() ? (48, 64) :                   // glibc x86_64 and the generic arm64 layout
        null;

    public static bool IsAvailable => Verified.Value;

    /// <summary>Bytes allocated on disk for <paramref name="path"/> (not following symbolic links); -1 when unknown.</summary>
    public static long AllocatedSize(string path) =>
        IsAvailable && TryStat(path, out _, out long blocks) ? blocks * 512 : -1;

    private static bool TryStat(string path, out long size, out long blocks)
    {
        size = blocks = 0;
        if (Layout is not { } layout)
        {
            return false;
        }

        byte* buffer = stackalloc byte[BufferSize];
        int result;
        try
        {
            result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? lstat_darwin_x64(path, buffer)
                : lstat(path, buffer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        if (result != 0)
        {
            return false;
        }

        size = *(long*)(buffer + layout.Size);
        blocks = *(long*)(buffer + layout.Blocks);
        return true;
    }

    /// <summary>The layout is right if lstat reports this assembly's known length and a plausible block count.</summary>
    private static bool Verify()
    {
        string? probe = typeof(UnixStat).Assembly.Location;
        if (string.IsNullOrEmpty(probe) || !File.Exists(probe))
        {
            probe = Environment.ProcessPath;
        }

        if (probe is null || !File.Exists(probe))
        {
            return false;
        }

        long length = new FileInfo(probe).Length;
        return TryStat(probe, out long size, out long blocks) && size == length && blocks >= 0 && blocks * 512 < length + (64L << 20);
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int lstat(string path, byte* buffer);

    /// <summary>On Intel Macs the 64-bit-inode variant of lstat has its own symbol.</summary>
    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int lstat_darwin_x64(string path, byte* buffer);
}
