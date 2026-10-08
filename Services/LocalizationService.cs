using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace WingetDashboard.Services;

internal static class LocalizationService
{
    public const string SystemDefault = "system";
    public const string English = "en-US";

    private static readonly string[] SupportedPackCultures =
    {
        "hr-HR", "sl-SI", "de-DE", "fr-FR", "es-ES", "it-IT"
    };

    private static Dictionary<string, string> _english = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);

    public static string Preference { get; private set; } = SystemDefault;
    public static string EffectiveCulture { get; private set; } = English;

    public static void Initialize(string? preference)
    {
        _english = LoadEnglishFallback();
        SetLanguage(preference, save: false);
    }

    public static void SetLanguage(string? preference, bool save)
    {
        string normalized = NormalizePreference(preference);
        string effective = normalized == SystemDefault
            ? ResolveSystemCulture(CultureInfo.CurrentUICulture)
            : normalized;

        Dictionary<string, string> selected = effective == English
            ? _english
            : LoadExternalPack(effective) ?? _english;

        Preference = normalized;
        EffectiveCulture = selected == _english && effective != English ? English : effective;
        _current = selected;

        if (save)
        {
            AppSettings settings = AppSettingsService.Load();
            settings.Language = Preference;
            AppSettingsService.Save(settings);
        }
    }

    public static string Get(string key)
    {
        if (_current.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
            return value;
        if (_english.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            return value;
        return key;
    }

    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static IReadOnlyList<LanguageChoice> GetLanguageChoices() => new[]
    {
        new LanguageChoice(SystemDefault, Get("Settings.SystemDefault")),
        new LanguageChoice(English, "English"),
        new LanguageChoice("hr-HR", "Hrvatski"),
        new LanguageChoice("sl-SI", "Slovenščina"),
        new LanguageChoice("de-DE", "Deutsch"),
        new LanguageChoice("fr-FR", "Français"),
        new LanguageChoice("es-ES", "Español"),
        new LanguageChoice("it-IT", "Italiano")
    };

    private static string NormalizePreference(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference) || preference.Equals(SystemDefault, StringComparison.OrdinalIgnoreCase))
            return SystemDefault;
        if (preference.Equals(English, StringComparison.OrdinalIgnoreCase))
            return English;
        foreach (string culture in SupportedPackCultures)
            if (preference.Equals(culture, StringComparison.OrdinalIgnoreCase))
                return culture;
        return SystemDefault;
    }

    private static string ResolveSystemCulture(CultureInfo culture)
    {
        string name = culture.Name;
        if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return English;
        foreach (string supported in SupportedPackCultures)
        {
            if (name.Equals(supported, StringComparison.OrdinalIgnoreCase) ||
                culture.TwoLetterISOLanguageName.Equals(supported[..2], StringComparison.OrdinalIgnoreCase))
                return supported;
        }
        return English;
    }

    private static Dictionary<string, string> LoadEnglishFallback()
    {
        Assembly assembly = typeof(LocalizationService).Assembly;
        const string resourceName = "WingetDashboard.Languages.en-US.json";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException("The built-in English language resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("The built-in English language resource is invalid.");
    }

    private static Dictionary<string, string>? LoadExternalPack(string culture)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Languages", culture + ".json");
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record LanguageChoice(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}
