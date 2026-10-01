using System.Diagnostics;
using System.Runtime.InteropServices;
using SpaceLens.Core.Models;
using SpaceLens.Core.Safety;
using SpaceLens.Windows.Native;

namespace SpaceLens.Windows.Shell;

public sealed record ShellResult(bool Success, string? Error = null, bool Cancelled = false)
{
    public static ShellResult Ok { get; } = new(true);
}

/// <summary>
/// File operations exposed in the UI. Destructive operations re-check <see cref="SafetyPolicy"/>
/// here as a second line of defense, independent of what the UI allowed.
/// </summary>
public sealed class ShellActions
{
    private readonly SafetyPolicy _policy;

    public ShellActions(SafetyPolicy policy) => _policy = policy;

    public static ShellResult Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return ShellResult.Ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ShellResult(false, ex.Message);
        }
    }

    /// <summary>Opens Explorer. For files (or when <paramref name="select"/> is set) the item is selected in its parent folder.</summary>
    public static unsafe ShellResult OpenInExplorer(string path, bool select)
    {
        if (!select && Directory.Exists(path))
        {
            return Open(path);
        }

        int hr = NativeMethods.SHParseDisplayName(path, 0, out nint pidl, 0, out _);
        if (hr < 0 || pidl == 0)
        {
            string? parent = Path.GetDirectoryName(path);
            return parent is not null && Directory.Exists(parent) ? Open(parent) : new ShellResult(false, "The item no longer exists.");
        }

        try
        {
            hr = NativeMethods.SHOpenFolderAndSelectItems(pidl, 0, null, 0);
            return hr >= 0 ? ShellResult.Ok : new ShellResult(false, Marshal.GetExceptionForHR(hr)?.Message);
        }
        finally
        {
            NativeMethods.CoTaskMemFree(pidl);
        }
    }

    public static ShellResult ShowProperties(string path, nint ownerWindow)
    {
        return NativeMethods.SHObjectProperties(ownerWindow, NativeMethods.SHOP_FILEPATH, path, null)
            ? ShellResult.Ok
            : new ShellResult(false, "The Properties dialog could not be opened.");
    }

    public static ShellResult OpenUri(string uri) => Open(uri);

    public SafetyAssessment Assess(string path, bool isDirectory, FileAttributes attributes = 0) =>
        isDirectory ? _policy.AssessDirectory(path) : _policy.AssessFile(path, attributes);

    /// <summary>
    /// Moves items to the Recycle Bin using SHFileOperation (FO_DELETE + FOF_ALLOWUNDO). The caller has
    /// already confirmed with the user, so the shell's own confirmation is suppressed, but Windows still
    /// warns when an item is too large for the Recycle Bin (FOF_WANTNUKEWARNING).
    /// Runs on a dedicated STA thread because the shell may show progress UI.
    /// </summary>
    public Task<ShellResult> MoveToRecycleBinAsync(IReadOnlyList<(string Path, bool IsDirectory)> items, nint ownerWindow)
    {
        foreach (var (path, isDirectory) in items)
        {
            var assessment = Assess(path, isDirectory);
            if (!assessment.CanDelete)
            {
                return Task.FromResult(new ShellResult(false, $"{path} is protected: {assessment.Label}."));
            }

            if (path.Length >= 260)
            {
                return Task.FromResult(new ShellResult(false, "The path is too long for the Recycle Bin. Use File Explorer to remove it."));
            }
        }

        var completion = new TaskCompletionSource<ShellResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(RecycleCore(items, ownerWindow));
            }
            catch (Exception ex)
            {
                completion.SetResult(new ShellResult(false, ex.Message));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static unsafe ShellResult RecycleCore(IReadOnlyList<(string Path, bool IsDirectory)> items, nint ownerWindow)
    {
        string from = string.Join('\0', items.Select(i => PathUtil.NormalizeDisplayPath(i.Path))) + "\0\0";
        fixed (char* pFrom = from)
        {
            var op = new NativeMethods.ShFileOpStruct
            {
                Hwnd = ownerWindow,
                Func = NativeMethods.FO_DELETE,
                From = pFrom,
                Flags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_WANTNUKEWARNING),
            };

            int result = NativeMethods.SHFileOperation(&op);
            if (op.AnyOperationsAborted != 0)
            {
                return new ShellResult(false, "The operation was cancelled.", Cancelled: true);
            }

            if (result != 0)
            {
                return new ShellResult(false, $"Windows could not move the item to the Recycle Bin (code 0x{result:X}).");
            }
        }

        foreach (var (path, isDirectory) in items)
        {
            if (isDirectory ? Directory.Exists(path) : File.Exists(path))
            {
                return new ShellResult(false, "Some items were not removed.");
            }
        }

        return ShellResult.Ok;
    }

    /// <summary>Permanently deletes a single file (not folders). Only offered as an explicit, separately confirmed action.</summary>
    public ShellResult DeleteFilePermanently(string path)
    {
        var assessment = _policy.AssessFile(path);
        if (!assessment.CanDelete)
        {
            return new ShellResult(false, $"{path} is protected: {assessment.Label}.");
        }

        try
        {
            File.Delete(PathUtil.ToLongPath(path));
            return ShellResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShellResult(false, ex.Message);
        }
    }

    public static (long Bytes, long Items) QueryRecycleBin(string? driveRoot)
    {
        var info = new NativeMethods.ShQueryRBInfo { Size = Marshal.SizeOf<NativeMethods.ShQueryRBInfo>() };
        int hr = NativeMethods.SHQueryRecycleBin(driveRoot, ref info);
        return hr >= 0 ? (info.SizeBytes, info.NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin of one drive with the standard Windows confirmation dialog.</summary>
    public static ShellResult EmptyRecycleBin(string? driveRoot, nint ownerWindow)
    {
        int hr = NativeMethods.SHEmptyRecycleBin(ownerWindow, driveRoot, 0);
        return hr >= 0 ? ShellResult.Ok : new ShellResult(false, hr == unchecked((int)0x800704C7) ? null : $"Error 0x{hr:X}", Cancelled: hr == unchecked((int)0x800704C7));
    }
}
