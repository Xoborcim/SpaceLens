using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceLens.Windows.FileSystem;

namespace SpaceLens.App.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public ScanEngine Engine { get; set; } = ScanEngine.Native;

    /// <summary>0 = automatic (based on drive type).</summary>
    public int Workers { get; set; }
    public bool DetectDeveloperFiles { get; set; } = true;
    public bool IncludeStoreApps { get; set; } = true;
    public bool RememberScans { get; set; } = true;
    public string? LastRoot { get; set; }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>Settings are a small JSON file in %LOCALAPPDATA%\SpaceLens. Nothing is stored elsewhere.</summary>
public static class SettingsService
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens");

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
