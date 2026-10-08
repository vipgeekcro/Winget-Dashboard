using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace WingetDashboard.Services;

/// <summary>
/// Runs one complete Updates operation in a single elevated helper process.
/// The main application requests elevation once, then this worker either updates
/// each selected package in order or executes winget upgrade --all.
/// </summary>
internal static class ElevatedUpdateWorker
{
    internal const string Switch = "--winget-dashboard-elevated-update-worker";

    internal static bool IsWorker(string[] args) =>
        args.Length >= 3 && args[0] == Switch;

    internal static int Run(string pipeName, string mode, IReadOnlyList<string> packageIds)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(15000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            if (string.Equals(mode, "all", StringComparison.Ordinal))
                return RunWinget(WingetUpdateService.CreateUpdateAllStartInfo(), writer);

            if (!string.Equals(mode, "selected", StringComparison.Ordinal) || packageIds.Count == 0)
                return unchecked((int)0x80070057);

            int failedPackages = 0;
            int lastFailure = 0;
            foreach (string packageId in packageIds)
            {
                int exitCode = RunWinget(WingetUpdateService.CreatePackageUpdateStartInfo(packageId), writer);
                if (exitCode != 0)
                {
                    failedPackages++;
                    lastFailure = exitCode;
                }
            }

            return failedPackages == 0 ? 0 : lastFailure;
        }
        catch (Exception ex)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                pipe.Connect(1000);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine("WingetDashboard elevated update worker error: " + ex);
            }
            catch { }
            return unchecked((int)0x80004005);
        }
    }

    private static int RunWinget(ProcessStartInfo startInfo, StreamWriter writer)
    {
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) SafeWrite(writer, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) SafeWrite(writer, e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void SafeWrite(StreamWriter writer, string line)
    {
        try { writer.WriteLine(line); } catch { }
    }
}
