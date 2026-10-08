using System;
using System.IO;
using System.Text.Json;

namespace WingetDashboard.Services;

internal sealed class AppSettings
{
    public string Language { get; set; } = "system";
    public string CatalogUpdateSchedule { get; set; } = "never";
    public DateTimeOffset? CatalogUpdateReminderAfter { get; set; }
}

internal static class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
                return new AppSettings();

            string json = File.ReadAllText(AppPaths.SettingsPath);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch
        {
            // A damaged/unreadable settings file must never prevent the Dashboard from starting.
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.UserDataDirectory);
        string temporaryPath = AppPaths.SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, AppPaths.SettingsPath, true);
    }
}
