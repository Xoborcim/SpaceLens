namespace SpaceLens.Core.InstalledApps;

/// <summary>A command line split into executable and arguments, ready for process creation.</summary>
public sealed record UninstallCommand(string FileName, string Arguments)
{
    /// <summary>
    /// Splits a registry command line. Handles quoted executables, unquoted paths containing spaces
    /// ("C:\Program Files\App\uninst.exe /S") and bare commands ("MsiExec.exe /X{GUID}").
    /// </summary>
    public static UninstallCommand? TryParse(string? commandLine, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string text = commandLine.Trim();
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            if (end < 0)
            {
                return new UninstallCommand(text.Trim('"'), "");
            }

            return new UninstallCommand(text[1..end], text[(end + 1)..].Trim());
        }

        // Unquoted: find the earliest executable extension that is followed by a space or the end.
        string[] extensions = [".exe", ".cmd", ".bat", ".com"];
        int best = -1;
        foreach (var ext in extensions)
        {
            int search = 0;
            while (true)
            {
                int i = text.IndexOf(ext, search, StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                {
                    break;
                }

                int after = i + ext.Length;
                if (after == text.Length || text[after] == ' ')
                {
                    if (best < 0 || after < best)
                    {
                        best = after;
                    }

                    break;
                }

                search = after;
            }
        }

        if (best > 0)
        {
            return new UninstallCommand(text[..best], text[best..].Trim());
        }

        // No extension: try progressively longer space-separated prefixes against the file system.
        if (fileExists is not null)
        {
            int space = text.IndexOf(' ');
            while (space > 0)
            {
                string candidate = text[..space];
                if (fileExists(candidate))
                {
                    return new UninstallCommand(candidate, text[(space + 1)..].Trim());
                }

                space = text.IndexOf(' ', space + 1);
            }
        }

        int firstSpace = text.IndexOf(' ');
        return firstSpace > 0 && !text[..firstSpace].Contains('\\')
            ? new UninstallCommand(text[..firstSpace], text[(firstSpace + 1)..].Trim())
            : new UninstallCommand(text, "");
    }

    /// <summary>
    /// Builds the command that runs the program's official uninstaller.
    /// MSI products are always removed through <c>msiexec /x {ProductCode}</c>; many registrations store
    /// <c>/I</c> (which opens the maintenance dialog) instead of <c>/X</c>.
    /// </summary>
    public static UninstallCommand? ForApp(InstalledApp app, bool quiet, Func<string, bool>? fileExists = null)
    {
        if (app.IsMsi && app.MsiProductCode is not null)
        {
            return new UninstallCommand("msiexec.exe", quiet ? $"/x {app.MsiProductCode} /qb" : $"/x {app.MsiProductCode}");
        }

        string? command = quiet ? app.QuietUninstallString : app.UninstallString;
        return TryParse(command, fileExists);
    }
}
