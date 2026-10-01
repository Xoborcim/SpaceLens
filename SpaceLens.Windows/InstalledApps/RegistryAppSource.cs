using Microsoft.Win32;
using SpaceLens.Core.InstalledApps;

namespace SpaceLens.Windows.InstalledApps;

/// <summary>
/// Reads uninstall registrations from
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c> (64-bit and 32-bit/WOW6432Node views)
/// and <c>HKCU\...\Uninstall</c>. Read-only: SpaceLens never writes to the registry.
/// </summary>
public static class RegistryAppSource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] ValueNames =
    [
        "DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "InstallDate", "EstimatedSize",
        "UninstallString", "QuietUninstallString", "WindowsInstaller", "SystemComponent", "ParentKeyName",
        "ParentDisplayName", "ReleaseType", "DisplayIcon",
    ];

    public static List<InstalledApp> ReadAll(CancellationToken cancellationToken = default)
    {
        var apps = new List<InstalledApp>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, AppSource.MachineRegistry64, apps, cancellationToken);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, AppSource.MachineRegistry32, apps, cancellationToken);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, AppSource.UserRegistry, apps, cancellationToken);
        Read(RegistryHive.CurrentUser, RegistryView.Registry32, AppSource.UserRegistry, apps, cancellationToken);
        return apps;
    }

    private static void Read(RegistryHive hive, RegistryView view, AppSource source, List<InstalledApp> output, CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey, writable: false);
            if (uninstall is null)
            {
                return;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var key = uninstall.OpenSubKey(keyName, writable: false);
                    if (key is null)
                    {
                        continue;
                    }

                    values.Clear();
                    foreach (var name in ValueNames)
                    {
                        values[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    }

                    var app = UninstallEntryParser.Parse(keyName, values, source);
                    if (app is not null)
                    {
                        output.Add(app);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Unreadable entries are skipped.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }
}
