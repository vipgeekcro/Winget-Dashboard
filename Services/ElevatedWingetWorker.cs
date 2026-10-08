using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace WingetDashboard.Services;

internal static class ElevatedWingetWorker
{
    internal const string Switch = "--winget-dashboard-elevated-worker";

    internal static bool IsWorker(string[] args) => args.Length == 3 && args[0] == Switch;

    internal static int Run(string pipeName, string packageId)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(15000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            ProcessStartInfo startInfo = SearchInstallService.CreateWingetStartInfo(packageId);
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
        catch (Exception ex)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                pipe.Connect(1000);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine("WingetDashboard elevated worker error: " + ex);
            }
            catch { }
            return unchecked((int)0x80004005);
        }
    }

    private static void SafeWrite(StreamWriter writer, string line)
    {
        try { writer.WriteLine(line); } catch { }
    }
}
