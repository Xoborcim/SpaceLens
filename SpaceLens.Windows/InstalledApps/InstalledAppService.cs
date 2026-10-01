using System.ComponentModel;
using System.Diagnostics;
using SpaceLens.Core.InstalledApps;
using SpaceLens.Core.Models;
using SpaceLens.Core.Scanning;
using SpaceLens.Windows.FileSystem;
using SpaceLens.Windows.Native;

namespace SpaceLens.Windows.InstalledApps;

public sealed record UninstallLaunchResult(bool Started, string? Error = null, bool Cancelled = false, Process? Process = null);

public static class InstalledAppService
{
    public static List<InstalledApp> LoadAll(bool includeMsix, CancellationToken cancellationToken = default)
    {
        var registry = RegistryAppSource.ReadAll(cancellationToken);
        var merged = AppDeduplicator.Merge(registry);
        if (includeMsix)
        {
            merged.AddRange(MsixAppSource.ReadAll(cancellationToken));
        }

        return merged;
    }

    /// <summary>Fills <see cref="InstalledApp.MeasuredSize"/> from an existing scan when it covers the install folder.</summary>
    public static bool TryMeasureFromTree(InstalledApp app, ScanTree tree)
    {
        if (app.InstallLocation is null)
        {
            return false;
        }

        int index = tree.FindDirectory(app.InstallLocation);
        if (index < 0 || (tree.Dir(index).Flags & NodeFlags.AccessDenied) != 0 && tree.Dir(index).TotalSize == 0)
        {
            return false;
        }

        app.MeasuredSize = tree.Dir(index).TotalSize;
        if (app.DataLocation is not null)
        {
            int data = tree.FindDirectory(app.DataLocation);
            if (data >= 0)
            {
                app.MeasuredDataSize = tree.Dir(data).TotalSize;
            }
        }

        app.Measurement = MeasurementState.Measured;
        return true;
    }

    /// <summary>Measures the install folder on disk with a small, low-priority scan.</summary>
    public static async Task MeasureOnDiskAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        if (app.InstallLocation is null || !Directory.Exists(app.InstallLocation))
        {
            app.Measurement = MeasurementState.Unavailable;
            return;
        }

        app.Measurement = MeasurementState.Measuring;
        var scanner = ScannerFactory.Create(ScanEngine.Native);
        var options = new ScanOptions { MaxParallelism = 2 };
        var tree = new ScanTree(app.InstallLocation, long.MaxValue);
        var result = await scanner.ScanAsync(tree, options, null, cancellationToken).ConfigureAwait(false);
        app.MeasuredSize = result.Bytes;

        if (app.DataLocation is not null && Directory.Exists(app.DataLocation))
        {
            var dataTree = new ScanTree(app.DataLocation, long.MaxValue);
            var dataResult = await scanner.ScanAsync(dataTree, options, null, cancellationToken).ConfigureAwait(false);
            app.MeasuredDataSize = dataResult.Bytes;
        }

        app.Measurement = MeasurementState.Measured;
    }

    /// <summary>
    /// Starts the application's registered uninstaller. Interactive by default; the quiet variant is only
    /// used when the user explicitly asks for it. Uninstallers that need administrator rights trigger the
    /// normal UAC prompt.
    /// </summary>
    public static UninstallLaunchResult LaunchUninstaller(InstalledApp app, bool quiet)
    {
        if (app.Source == AppSource.Msix)
        {
            return new UninstallLaunchResult(false, "Use RemoveMsixAsync for packaged apps.");
        }

        var command = UninstallCommand.ForApp(app, quiet, File.Exists);
        if (command is null)
        {
            return new UninstallLaunchResult(false, "This program does not provide an uninstaller.");
        }

        var startInfo = new ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = true };
        try
        {
            return new UninstallLaunchResult(true, Process: Process.Start(startInfo));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NativeMethods.ERROR_ELEVATION_REQUIRED)
        {
            try
            {
                startInfo.Verb = "runas";
                return new UninstallLaunchResult(true, Process: Process.Start(startInfo));
            }
            catch (Win32Exception inner) when (inner.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
            {
                return new UninstallLaunchResult(false, Cancelled: true);
            }
            catch (Win32Exception inner)
            {
                return new UninstallLaunchResult(false, inner.Message);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
        {
            return new UninstallLaunchResult(false, Cancelled: true);
        }
        catch (Win32Exception ex)
        {
            return new UninstallLaunchResult(false, $"{ex.Message} ({command.FileName})");
        }
    }

    /// <summary>Checks whether the registration still exists (after an uninstaller exits).</summary>
    public static bool IsStillInstalled(InstalledApp app)
    {
        if (app.Source == AppSource.Msix)
        {
            return app.InstallLocation is not null && Directory.Exists(app.InstallLocation);
        }

        var current = LoadAll(includeMsix: false);
        return current.Any(a => string.Equals(a.RegistrationKey, app.RegistrationKey, StringComparison.OrdinalIgnoreCase));
    }
}
