using System;
using System.Linq;

namespace WingetDashboard.Services;

/// <summary>
/// Allowlist for legitimate user-manageable runtimes, SDKs, redistributables and
/// development runtimes. It is evaluated before the registry SystemComponent=1 rule.
///
/// This deliberately identifies product families rather than individual versions so
/// future versions remain visible. Windows App Runtime plumbing (DDLM/Main/Singleton),
/// Store infrastructure and other internal packages are rejected earlier by the main
/// InstalledApplicationFilter and therefore cannot be re-enabled by this allowlist.
/// </summary>
internal static class RuntimeDevelopmentAllowlist
{
    // Stable WinGet/package-ID families where available.
    private static readonly string[] IdPrefixes =
    {
        // Microsoft .NET / ASP.NET / Windows Desktop / Framework developer packs.
        "Microsoft.DotNet.",
        "Microsoft.AspNetCore.",

        // Microsoft C/C++ redistributables and UWP VC libraries.
        "Microsoft.VCRedist.",
        "Microsoft.VCLibs.",

        // Microsoft SDK/developer/runtime families.
        "Microsoft.WindowsSDK.",
        "Microsoft.WindowsAppRuntime.",
        "Microsoft.UI.Xaml.",
        "Microsoft.EdgeWebView2Runtime",
        "Microsoft.EdgeWebView2.",

        // Java/OpenJDK distributions commonly exposed by package managers.
        "Oracle.JavaRuntimeEnvironment",
        "Oracle.JDK.",
        "Oracle.OpenJDK.",
        "Microsoft.OpenJDK.",
        "EclipseAdoptium.Temurin.",
        "AdoptOpenJDK.",
        "Amazon.Corretto.",
        "Azul.Zulu.",
        "BellSoft.LibericaJDK.",
        "IBM.Semuru.",
        "SapMachine.",

        // Common language/application runtimes and development platforms.
        "Python.Python.",
        "Python.PythonInstallManager",
        "OpenJS.NodeJS",
        "OpenJS.NodeJS.LTS",
        "RubyInstallerTeam.RubyWithDevKit.",
        "RubyInstallerTeam.Ruby.",
        "PHP.PHP.",
        "GoLang.Go",
        "Rustlang.Rustup",
        "Rustlang.Rust.MSVC",
        "StrawberryPerl.StrawberryPerl"
    };

    // Used when winget list can only correlate an ARP/MSIX entry to an opaque ID.
    // These are intentionally product-family phrases, not generic words such as
    // "framework" or "runtime" on their own.
    private static readonly string[] NamePhrases =
    {
        "microsoft .net runtime",
        "microsoft windows desktop runtime",
        "microsoft asp.net core runtime",
        "microsoft asp.net core hosting",
        "microsoft .net sdk",
        "microsoft .net framework",
        "microsoft .net native framework",
        "microsoft .net native runtime",
        "microsoft.ui.xaml",
        "windowsappruntime.",
        "windows app runtime",
        "windows sdk addon",
        "microsoft visual c++",
        "visual c++ redistributable",
        "microsoft edge webview2 runtime",
        "webview2 runtime",
        "java runtime environment",
        "java se runtime environment",
        "java development kit",
        "openjdk",
        "eclipse temurin",
        "adoptopenjdk",
        "amazon corretto",
        "azul zulu",
        "liberica jdk",
        "ibm semeru",
        "sapmachine",
        "python ",
        "node.js",
        "ruby with devkit",
        "rubyinstaller",
        "strawberry perl"
    };

    internal static bool IsKnownRuntimeOrDevelopmentPackage(InstalledApplicationInfo app)
    {
        string id = app.Id?.Trim() ?? string.Empty;
        if (IdPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return true;

        string name = (app.Name ?? string.Empty).Trim().ToLowerInvariant();
        return NamePhrases.Any(phrase => name.Contains(phrase, StringComparison.Ordinal));
    }
}
