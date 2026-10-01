using System.Text;
using SpaceLens.Core.Models;

namespace SpaceLens.Core.InstalledApps;

/// <summary>
/// Merges duplicate registrations: the same program often appears in both the 32- and 64-bit views,
/// in HKLM and HKCU, or as several entries sharing an install folder.
/// </summary>
public static class AppDeduplicator
{
    public static List<InstalledApp> Merge(IEnumerable<InstalledApp> apps)
    {
        var byKey = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        var byRegistration = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledApp>();

        foreach (var app in apps)
        {
            // Same registry key name seen through two views (HKCU is shared between the views).
            if (app.RegistrationKey is not null && app.Source != AppSource.Msix)
            {
                string regKey = app.Source + "|" + app.RegistrationKey;
                if (app.Source == AppSource.UserRegistry && byRegistration.TryGetValue(regKey, out var sameKey))
                {
                    Absorb(sameKey, app);
                    continue;
                }

                byRegistration[regKey] = app;
            }

            string key = NormalizeName(app.Name) + "|" + NormalizeName(app.Publisher ?? "");
            if (byKey.TryGetValue(key, out var existing) && LooksSame(existing, app))
            {
                Absorb(existing, app);
                continue;
            }

            byKey[key] = app;
            result.Add(app);
        }

        return result;
    }

    private static bool LooksSame(InstalledApp a, InstalledApp b)
    {
        if (a.InstallLocation is not null && b.InstallLocation is not null)
        {
            return PathUtil.NormalizeDisplayPath(a.InstallLocation).Equals(PathUtil.NormalizeDisplayPath(b.InstallLocation), StringComparison.OrdinalIgnoreCase);
        }

        // Without locations, same name, publisher and version is the same product.
        return string.Equals(a.Version, b.Version, StringComparison.OrdinalIgnoreCase) || a.Version is null || b.Version is null;
    }

    private static void Absorb(InstalledApp target, InstalledApp other)
    {
        target.Publisher ??= other.Publisher;
        target.Version ??= other.Version;
        target.InstallDate ??= other.InstallDate;
        if (target.InstallLocation is null || target.InstallLocationInferred && other.InstallLocation is not null && !other.InstallLocationInferred)
        {
            target.InstallLocation = other.InstallLocation;
            target.InstallLocationInferred = other.InstallLocationInferred;
        }

        if (other.ReportedSize > (target.ReportedSize ?? 0))
        {
            target.ReportedSize = other.ReportedSize;
        }

        if (string.IsNullOrWhiteSpace(target.UninstallString))
        {
            target.UninstallString = other.UninstallString;
            target.IsMsi = other.IsMsi;
            target.MsiProductCode = other.MsiProductCode;
        }

        target.QuietUninstallString ??= other.QuietUninstallString;
        target.DisplayIcon ??= other.DisplayIcon;
    }

    public static string NormalizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool lastSpace = false;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastSpace = false;
            }
            else if (!lastSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        // Ignore architecture suffixes that installers add inconsistently.
        string s = sb.ToString().Trim();
        foreach (var suffix in new[] { " x64", " x86", " 64 bit", " 32 bit", " amd64" })
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                s = s[..^suffix.Length].TrimEnd();
            }
        }

        return s;
    }
}
