using SpaceLens.Core.InstalledApps;
using Windows.Management.Deployment;

namespace SpaceLens.Windows.InstalledApps;

/// <summary>
/// Lists MSIX/AppX packages installed for the current user via <see cref="PackageManager"/>.
/// Framework, resource and system (non-removable) packages are excluded.
/// </summary>
public static class MsixAppSource
{
    public static List<InstalledApp> ReadAll(CancellationToken cancellationToken = default)
    {
        var apps = new List<InstalledApp>();
        string packagesData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

        IEnumerable<global::Windows.ApplicationModel.Package> packages;
        try
        {
            packages = new PackageManager().FindPackagesForUser(string.Empty);
        }
        catch (Exception)
        {
            return apps;
        }

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (package.IsFramework || package.IsResourcePackage)
                {
                    continue;
                }

                if (package.SignatureKind == global::Windows.ApplicationModel.PackageSignatureKind.System)
                {
                    continue;
                }

                string name = SafeGet(() => package.DisplayName) ?? package.Id.Name;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    name = package.Id.Name;
                }

                var version = package.Id.Version;
                string? location = SafeGet(() => package.InstalledPath);
                var app = new InstalledApp
                {
                    Id = "msix:" + package.Id.FullName,
                    Name = name,
                    Publisher = SafeGet(() => package.PublisherDisplayName),
                    Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                    InstallDate = SafeGet(() => (DateTime?)package.InstalledDate.LocalDateTime),
                    Source = AppSource.Msix,
                    RegistrationKey = package.Id.FullName,
                    PackageFullName = package.Id.FullName,
                    InstallLocation = location,
                    DataLocation = Path.Combine(packagesData, package.Id.FamilyName),
                };
                apps.Add(app);
            }
            catch (Exception)
            {
                // Packages in a bad state are skipped.
            }
        }

        return apps;
    }

    public static async Task<(bool Success, string? Error)> RemoveAsync(string packageFullName)
    {
        try
        {
            var result = await new PackageManager().RemovePackageAsync(packageFullName);
            return result.IsRegistered ? (false, result.ErrorText) : (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
