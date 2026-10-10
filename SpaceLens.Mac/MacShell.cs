using System.Diagnostics;
using System.Runtime.InteropServices;
using SpaceLens.Core.Safety;

namespace SpaceLens.Mac;

public sealed record MacShellResult(bool Success, string? Error = null)
{
    public static MacShellResult Ok { get; } = new(true);
}

/// <summary>
/// Finder and Trash operations. Moving to the Trash goes through Foundation's NSFileManager
/// (trashItemAtURL:), the same call Finder uses, so items can be put back from the Trash. Destructive
/// operations re-check the safety policy here, independently of what the UI allowed.
/// </summary>
public sealed partial class MacShell(IItemSafetyPolicy policy)
{
    /// <summary>System Settings > Privacy & Security > Full Disk Access.</summary>
    public const string FullDiskAccessSettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles";

    public static MacShellResult Open(string path) => Run(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open", path);

    /// <summary>Shows the item selected in a Finder window.</summary>
    public static MacShellResult Reveal(string path) =>
        OperatingSystem.IsMacOS() ? Run("/usr/bin/open", "-R", path) : Run("xdg-open", Path.GetDirectoryName(path) ?? path);

    public static MacShellResult OpenUrl(string url) => Open(url);

    /// <summary>
    /// macOS only lets an app read Mail, Messages, Safari and other app data after the person grants Full
    /// Disk Access. Reading ~/Library/Safari is a reliable probe; null when it cannot be decided.
    /// </summary>
    public static bool? HasFullDiskAccess(string home)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        string probe = Path.Combine(home, "Library", "Safari");
        if (!Directory.Exists(probe))
        {
            return null;
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(probe).FirstOrDefault();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<(string Path, MacShellResult Result)> MoveToTrash(IReadOnlyList<(string Path, bool IsDirectory)> items)
    {
        var results = new List<(string, MacShellResult)>();
        foreach (var (path, isDirectory) in items)
        {
            var assessment = isDirectory ? policy.AssessDirectory(path) : policy.AssessFile(path);
            results.Add((path, assessment.CanDelete ? TrashOne(path) : new MacShellResult(false, $"{path} is protected: {assessment.Label}.")));
        }

        return results;
    }

    /// <summary>Moves an application bundle to the Trash: the macOS way of removing an application.</summary>
    public MacShellResult RemoveApplication(MacApp app)
    {
        if (app.IsSystem)
        {
            return new MacShellResult(false, $"{app.Name} is part of macOS and cannot be removed.");
        }

        if (!app.BundlePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(app.BundlePath))
        {
            return new MacShellResult(false, "The application bundle was not found.");
        }

        return TrashOne(app.BundlePath);
    }

    private static MacShellResult TrashOne(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new MacShellResult(false, "Moving to the Trash is only available on macOS.");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return new MacShellResult(false, $"{path} no longer exists.");
        }

        try
        {
            return Foundation.TrashItem(path, out string? error) ? MacShellResult.Ok : new MacShellResult(false, error ?? "The item could not be moved to the Trash.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return new MacShellResult(false, ex.Message);
        }
    }

    private static MacShellResult Run(string program, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process.Start(start)?.Dispose();
            return MacShellResult.Ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new MacShellResult(false, ex.Message);
        }
    }

    /// <summary>The few Foundation calls needed, through the Objective-C runtime (no binding library).</summary>
    private static unsafe partial class Foundation
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";
        private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(FoundationPath, out _));

        public static bool TrashItem(string path, out string? error)
        {
            error = null;
            if (!Loaded.Value)
            {
                error = "Foundation could not be loaded.";
                return false;
            }

            nint pool = objc_autoreleasePoolPush();
            nint utf8 = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                nint nsPath = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
                nint url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), nsPath);
                nint manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
                nint nsError = 0;
                bool ok = SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, 0, (nint)(&nsError));
                if (!ok && nsError != 0)
                {
                    nint description = Send(nsError, sel_registerName("localizedDescription"));
                    error = Marshal.PtrToStringUTF8(Send(description, sel_registerName("UTF8String")));
                }

                return ok;
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
                objc_autoreleasePoolPop(pool);
            }
        }

        [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint objc_getClass(string name);

        [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint sel_registerName(string name);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector, nint argument);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static partial bool SendTrash(nint receiver, nint selector, nint url, nint resultingUrl, nint error);

        [LibraryImport(ObjC)]
        private static partial nint objc_autoreleasePoolPush();

        [LibraryImport(ObjC)]
        private static partial void objc_autoreleasePoolPop(nint pool);
    }
}
