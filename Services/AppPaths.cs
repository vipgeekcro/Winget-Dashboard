using System;
using System.IO;

namespace WingetDashboard.Services;

internal static class AppPaths
{
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Winget Dashboard");

    public static string SettingsPath => Path.Combine(UserDataDirectory, "settings.json");

    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Winget Dashboard");

    public static string CatalogPath => Path.Combine(UserDataDirectory, "Catalog", "catalog.json");

    public static string ListsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Winget Dashboard Lists");
}
