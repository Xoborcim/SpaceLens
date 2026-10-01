using System.Diagnostics;

namespace SpaceLens.Tests;

/// <summary>Temporary directory tree for file system tests; deleted on dispose.</summary>
public sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "SpaceLensTests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(params string[] parts)
    {
        string path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relativePath, long size)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        if (size > 0)
        {
            // Write real data so the allocated size is deterministic (not sparse / not MFT-resident for large sizes).
            var buffer = new byte[Math.Min(size, 1 << 20)];
            Random.Shared.NextBytes(buffer);
            long remaining = size;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, buffer.Length);
                stream.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
        }

        return path;
    }

    /// <summary>Creates a directory junction (does not require administrator rights).</summary>
    public static bool TryCreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    public void Dispose()
    {
        try
        {
            // Remove links first so deletion never follows them.
            foreach (var dir in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    info.Delete();
                }
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
