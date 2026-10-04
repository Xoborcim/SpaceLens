using Microsoft.Win32;

namespace SpaceLens.Windows.Shell;

/// <summary>
/// The optional "Analyze with SpaceLens" entry in File Explorer's right-click menu for folders, folder
/// backgrounds and drives. It is per user (HKEY_CURRENT_USER\Software\Classes, no administrator rights)
/// and written only when the user turns it on in Settings; turning it off deletes the keys again.
/// On Windows 11 the entry appears under "Show more options".
/// </summary>
public static class ExplorerIntegration
{
    public const string MenuText = "Analyze with SpaceLens";

    /// <summary>(registry key, argument): %1 is the clicked folder or drive, %V the folder whose background was clicked.</summary>
    private static readonly (string Key, string Argument)[] Entries =
    [
        (@"Software\Classes\Directory\shell\SpaceLens", "%1"),
        (@"Software\Classes\Directory\Background\shell\SpaceLens", "%V"),
        (@"Software\Classes\Drive\shell\SpaceLens", "%1"),
    ];

    /// <summary>The command line Explorer runs: the executable and the clicked location, both quoted.</summary>
    public static string CommandFor(string exePath, string argument) => $"\"{exePath}\" \"{argument}\"";

    /// <summary>True when every entry exists and points at <paramref name="exePath"/> (false after the app was moved).</summary>
    public static bool IsRegistered(string exePath)
    {
        foreach (var (key, argument) in Entries)
        {
            using var command = Registry.CurrentUser.OpenSubKey(key + @"\command");
            if (command?.GetValue(null) is not string value || !value.Equals(CommandFor(exePath, argument), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static void Register(string exePath)
    {
        foreach (var (key, argument) in Entries)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(key);
            verb.SetValue(null, MenuText);
            verb.SetValue("Icon", $"\"{exePath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, CommandFor(exePath, argument));
        }
    }

    public static void Unregister()
    {
        foreach (var (key, _) in Entries)
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }
}
