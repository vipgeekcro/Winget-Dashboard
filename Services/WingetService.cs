using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WingetDashboard.Services;

internal static class WingetService
{
    // C# counterpart of the proven Beta 29 / Accessible Quick Installer check:
    // winget.exe --version must start, finish within 10 seconds, and return exit code 0.
    internal static bool TestCommandWorks()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("winget.exe", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
                return false;

            if (!process.WaitForExit(10000))
            {
                try { process.Kill(); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static Task<bool> TestCommandWorksAsync()
    {
        // Keep the proven synchronous check unchanged and run it off the UI thread,
        // so startup never blocks the WPF dashboard while Winget is being checked.
        return Task.Run(TestCommandWorks);
    }
}
