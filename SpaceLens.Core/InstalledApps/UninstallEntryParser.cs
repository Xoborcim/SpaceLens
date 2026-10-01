using System.Globalization;
using System.Text.RegularExpressions;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.InstalledApps;

/// <summary>
/// Turns the values of an <c>...\CurrentVersion\Uninstall\{key}</c> registry entry into an <see cref="InstalledApp"/>.
/// Pure logic (values are passed in as a dictionary) so it can be unit tested without the registry.
/// </summary>
public static partial class UninstallEntryParser
{
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidPattern();

    /// <returns>The app, or null when the entry is hidden, an update, or unusable.</returns>
    public static InstalledApp? Parse(string keyName, IReadOnlyDictionary<string, object?> values, AppSource source)
    {
        string? name = GetString(values, "DisplayName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (GetInt(values, "SystemComponent") == 1)
        {
            return null;
        }

        // Updates and hotfixes attach to a parent product.
        if (!string.IsNullOrEmpty(GetString(values, "ParentKeyName")) || !string.IsNullOrEmpty(GetString(values, "ParentDisplayName")))
        {
            return null;
        }

        string? releaseType = GetString(values, "ReleaseType");
        if (releaseType is not null && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string? uninstall = Expand(GetString(values, "UninstallString"));
        string? quiet = Expand(GetString(values, "QuietUninstallString"));
        bool isMsi = GetInt(values, "WindowsInstaller") == 1;
        string? productCode = null;
        if (isMsi)
        {
            productCode = GuidPattern().IsMatch(keyName) ? GuidPattern().Match(keyName).Value : ExtractGuid(uninstall);
        }
        else if (uninstall is not null && uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            productCode = ExtractGuid(uninstall);
            isMsi = productCode is not null;
        }

        if (string.IsNullOrWhiteSpace(uninstall) && productCode is null)
        {
            return null;
        }

        var app = new InstalledApp
        {
            Id = $"{source}:{keyName}",
            Name = name.Trim(),
            Publisher = GetString(values, "Publisher")?.Trim(),
            Version = GetString(values, "DisplayVersion")?.Trim(),
            InstallDate = ParseInstallDate(GetString(values, "InstallDate")),
            Source = source,
            RegistrationKey = keyName,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            IsMsi = isMsi,
            MsiProductCode = productCode,
            DisplayIcon = Expand(GetString(values, "DisplayIcon")),
        };

        // EstimatedSize is a DWORD in KB, written by the installer and frequently stale or absent.
        int? estimatedKb = GetInt(values, "EstimatedSize");
        if (estimatedKb is > 0)
        {
            app.ReportedSize = (long)(uint)estimatedKb.Value * 1024;
        }

        string? location = CleanPath(Expand(GetString(values, "InstallLocation")));
        if (location is not null && IsPlausibleInstallFolder(location))
        {
            app.InstallLocation = location;
        }
        else
        {
            string? inferred = InferInstallFolder(app.DisplayIcon) ?? InferInstallFolder(UninstallCommand.TryParse(uninstall)?.FileName);
            if (inferred is not null)
            {
                app.InstallLocation = inferred;
                app.InstallLocationInferred = true;
            }
        }

        return app;
    }

    /// <summary>
    /// Rejects folders that are too broad to represent one application (drive roots, Program Files
    /// itself, the Windows folder, a user profile...). Measuring those would wildly overstate sizes.
    /// </summary>
    public static bool IsPlausibleInstallFolder(string path)
    {
        path = PathUtil.NormalizeDisplayPath(path);
        if (path.Length <= 3 || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        string[] tooBroad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Common Files"),
        ];

        foreach (var broad in tooBroad)
        {
            if (!string.IsNullOrEmpty(broad) && PathUtil.NormalizeDisplayPath(broad).Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Folders inside Windows or installer caches are not install folders.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && PathUtil.IsSameOrUnder(path, windows))
        {
            return false;
        }

        if (path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(@"\Package Cache", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase) && path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? InferInstallFolder(string? filePath)
    {
        filePath = CleanPath(filePath);
        if (filePath is null)
        {
            return null;
        }

        // DisplayIcon is often "C:\path\app.exe,0".
        int comma = filePath.LastIndexOf(',');
        if (comma > 2 && filePath.Length - comma <= 4)
        {
            filePath = filePath[..comma];
        }

        if (!filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !filePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (filePath.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || filePath.Contains("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? folder = PathUtil.GetParent(filePath);
        if (folder is null)
        {
            return null;
        }

        // Uninstallers commonly live in a subfolder ("uninst", "_uninstall", "Uninstall").
        string leaf = PathUtil.GetName(folder);
        if (leaf.Contains("uninst", StringComparison.OrdinalIgnoreCase) && PathUtil.GetParent(folder) is { } up)
        {
            folder = up;
        }

        return IsPlausibleInstallFolder(folder) ? folder : null;
    }

    public static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim().Trim('"').Trim();
        if (path.Length < 3 || path[1] != ':' && !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        return PathUtil.NormalizeDisplayPath(path);
    }

    private static string? ExtractGuid(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = GuidPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    private static DateTime? ParseInstallDate(string? value)
    {
        if (value is { Length: 8 } && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        if (value is not null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Year > 1990)
        {
            return parsed;
        }

        return null;
    }

    private static string? Expand(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value.Trim());

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var v) ? v switch
        {
            string s => s,
            null => null,
            _ => v.ToString(),
        } : null;

    private static int? GetInt(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var v) || v is null)
        {
            return null;
        }

        return v switch
        {
            int i => i,
            long l => unchecked((int)l),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }
}
