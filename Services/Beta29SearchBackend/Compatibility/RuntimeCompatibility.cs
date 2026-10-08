using System;
using System.Globalization;

namespace Beta29.SearchBackend.Compatibility;

internal static class RuntimeCompatibility
{
    // Only the separate verification assembly can bypass the Windows requirement.
    // Such a run is a portable smoke check, never a Windows PowerShell parity result.
    internal static bool PortableVerification;

    internal static void RequireWindowsNls()
    {
        if (PortableVerification) return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Beta 29 parity requires Windows and NLS globalization.");

        // Published Microsoft test for ICU; also reject globalization-invariant mode.
        SortVersion version = CultureInfo.InvariantCulture.CompareInfo.Version;
        byte[] bytes = version.SortId.ToByteArray();
        int embeddedVersion = bytes[3] << 24 | bytes[2] << 16 | bytes[1] << 8 | bytes[0];
        if (version.FullVersion == 0 || (embeddedVersion != 0 && embeddedVersion == version.FullVersion))
            throw new InvalidOperationException("Enable System.Globalization.UseNls=true in the executable project's runtime configuration before starting the process. Do not enable globalization-invariant mode.");
    }
}
