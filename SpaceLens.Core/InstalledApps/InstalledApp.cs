namespace SpaceLens.Core.InstalledApps;

public enum AppSource
{
    MachineRegistry64,
    MachineRegistry32,
    UserRegistry,
    Msix,
}

public enum MeasurementState
{
    NotMeasured,
    Measuring,
    Measured,
    Unavailable,
}

/// <summary>An installed program, merged from one or more uninstall registrations.</summary>
public sealed class InstalledApp
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public DateTime? InstallDate { get; set; }
    public AppSource Source { get; set; }

    /// <summary>Registry key name (e.g. an MSI product code) or MSIX package full name.</summary>
    public string? RegistrationKey { get; set; }

    /// <summary>Install folder, either declared by the program or inferred from its uninstaller/icon path.</summary>
    public string? InstallLocation { get; set; }
    public bool InstallLocationInferred { get; set; }

    /// <summary>Additional per-user data folder (MSIX packages store data under %LOCALAPPDATA%\Packages).</summary>
    public string? DataLocation { get; set; }

    /// <summary>Size the program reported to Windows (EstimatedSize). Often missing or stale.</summary>
    public long? ReportedSize { get; set; }

    /// <summary>Size measured from the install folder on disk.</summary>
    public long? MeasuredSize { get; set; }
    public long? MeasuredDataSize { get; set; }
    public MeasurementState Measurement { get; set; }

    public string? UninstallString { get; set; }
    public string? QuietUninstallString { get; set; }
    public bool IsMsi { get; set; }
    public string? MsiProductCode { get; set; }
    public string? PackageFullName { get; set; }
    public string? DisplayIcon { get; set; }
    public bool IsGame { get; set; }

    public bool CanUninstall =>
        Source == AppSource.Msix ? PackageFullName is not null : !string.IsNullOrWhiteSpace(UninstallString) || (IsMsi && MsiProductCode is not null);

    public bool CanQuietUninstall => Source != AppSource.Msix && !string.IsNullOrWhiteSpace(QuietUninstallString);

    /// <summary>Best available size: measured when known, otherwise reported.</summary>
    public long BestSize => MeasuredSize is > 0 ? MeasuredSize.Value + (MeasuredDataSize ?? 0) : ReportedSize ?? 0;

    public bool IsSizeMeasured => MeasuredSize is > 0;

    public string SourceLabel => Source switch
    {
        AppSource.Msix => "Microsoft Store / MSIX",
        AppSource.UserRegistry => "Current user",
        AppSource.MachineRegistry32 => IsMsi ? "Windows Installer (32-bit)" : "All users (32-bit)",
        _ => IsMsi ? "Windows Installer" : "All users",
    };
}
