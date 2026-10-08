using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WingetDashboard.Services;

public sealed record WingetUpdateInfo(string Name, string Id, string InstalledVersion, string AvailableVersion)
{
    public string DisplayText => LocalizationService.Format("Updates.ItemDisplay", Name, InstalledVersion, AvailableVersion);
}

public sealed record WingetCommandResult(bool Success, int ExitCode, string Output, string Error);

internal sealed class UpdateProcessHandle : IDisposable
{
    internal Process Process { get; }
    internal Task OutputCompleted { get; }

    internal UpdateProcessHandle(Process process, Task outputCompleted)
    {
        Process = process;
        OutputCompleted = outputCompleted;
    }

    public void Dispose() => Process.Dispose();
}

public sealed class WingetUpdateService
{
    public Task<IReadOnlyList<WingetUpdateInfo>> GetAvailableUpdatesAsync() => Task.Run(() =>
    {
        WingetCommandResult result = RunWinget("upgrade --accept-source-agreements --disable-interactivity");
        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "Winget could not check for updates." : result.Error.Trim());

        List<WingetUpdateInfo> updates = ParseUpgradeTable(result.Output);
        if (updates.Count == 0 && !OutputClearlySaysNoUpdates(result.Output))
        {
            string detail = string.IsNullOrWhiteSpace(result.Error)
                ? "WinGet returned output that Winget Dashboard could not recognize."
                : result.Error.Trim();
            throw new InvalidOperationException(detail);
        }

        return (IReadOnlyList<WingetUpdateInfo>)updates;
    });

    internal UpdateProcessHandle StartUpdateSession(IReadOnlyList<WingetUpdateInfo> updates, bool updateAll, Action<string> onOutput)
    {
        if (!updateAll && updates.Count == 0)
            throw new InvalidOperationException("No packages were selected for update.");

        string pipeName = "WingetDashboard-Update-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Dashboard executable path could not be determined.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(ElevatedUpdateWorker.Switch);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(updateAll ? "all" : "selected");
        if (!updateAll)
        {
            foreach (WingetUpdateInfo update in updates)
                startInfo.ArgumentList.Add(update.Id);
        }

        // Use the same confirmed foreground handoff as Search installation/uninstall.
        SearchInstallService.AllowElevationForeground();

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        Task outputCompleted = Task.Run(async () =>
        {
            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, leaveOpen: false);
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                        onOutput(line);
                }
                catch
                {
                    // Worker exit code remains authoritative if the pipe closes or fails.
                }
            }
        });

        return new UpdateProcessHandle(process, outputCompleted);
    }

    internal static ProcessStartInfo CreatePackageUpdateStartInfo(string packageId)
    {
        var startInfo = CreateUpdateStartInfo();
        foreach (string arg in new[]
        {
            "upgrade", "--id", packageId, "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        })
            startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    internal static ProcessStartInfo CreateUpdateAllStartInfo()
    {
        var startInfo = CreateUpdateStartInfo();
        foreach (string arg in new[]
        {
            "upgrade", "--all", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        })
            startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    private static ProcessStartInfo CreateUpdateStartInfo() => new()
    {
        FileName = "winget.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static WingetCommandResult RunWinget(string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("winget.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            });

            if (process is null)
                return new(false, -1, string.Empty, "Winget could not be started.");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new(process.ExitCode == 0, process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new(false, -1, string.Empty, ex.Message);
        }
    }

    private static bool OutputClearlySaysNoUpdates(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        string text = output.ToLowerInvariant();
        return text.Contains("no applicable upgrade found") ||
               text.Contains("no available upgrade found") ||
               text.Contains("no installed package found matching input criteria") ||
               Regex.IsMatch(text, @"\b0\s+upgrades?\s+available\b");
    }

    private static List<WingetUpdateInfo> ParseUpgradeTable(string output)
    {
        string[] lines = output.Replace("\r", string.Empty).Split('\n');
        int separatorIndex = Array.FindIndex(lines, line =>
        {
            string trimmed = line.Trim();
            return trimmed.Length >= 10 && trimmed.All(c => c == '-');
        });

        if (separatorIndex < 0)
            return new List<WingetUpdateInfo>();

        var results = new List<WingetUpdateInfo>();
        for (int row = separatorIndex + 1; row < lines.Length; row++)
        {
            string line = lines[row].Trim();
            if (line.Length == 0) continue;

            Match match = Regex.Match(line, @"^(?<name>.+?)\s+(?<id>\S+)\s+(?<installed>\S+)\s+(?<available>\S+)\s+(?<source>\S+)\s*$");
            if (!match.Success) continue;

            string name = match.Groups["name"].Value.Trim();
            string id = match.Groups["id"].Value.Trim();
            string installed = match.Groups["installed"].Value.Trim();
            string available = match.Groups["available"].Value.Trim();
            string source = match.Groups["source"].Value.Trim();
            if (name.Length == 0 || id.Length == 0 || installed.Length == 0 || available.Length == 0 || source.Length == 0)
                continue;
            results.Add(new WingetUpdateInfo(name, id, installed, available));
        }
        return results;
    }
}

internal sealed class UpdateStatusSession : IDisposable
{
    private readonly WingetUpdateService _service;
    private readonly IReadOnlyList<WingetUpdateInfo> _knownUpdates;
    private readonly bool _updateAll;
    private readonly InstallationStatusWindow _dialog;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private readonly List<string> _allOutput = new();
    private UpdateProcessHandle? _process;
    private string _currentName;
    private string _lastPhase = "";
    private WingetCommandResult? _result;
    private bool _processExitObserved;
    private int _failedPackages;

    internal UpdateStatusSession(WingetUpdateService service, IReadOnlyList<WingetUpdateInfo> updates, bool updateAll, System.Windows.Window? owner)
    {
        _service = service;
        _knownUpdates = updates;
        _updateAll = updateAll;
        _currentName = updates.Count > 0 ? updates[0].Name : "application";
        _dialog = new InstallationStatusWindow(LocalizationService.Format("Updates.Preparing", _currentName), LocalizationService.Get("Updates.WindowTitle"));
        if (owner is not null)
        {
            _dialog.Owner = owner;
            _dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        }
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _dialog.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _timer.Tick += Tick;
        _dialog.Closing += DialogClosing;
        _dialog.ContentRendered += DialogContentRendered;
    }

    internal WingetCommandResult Run()
    {
        _dialog.ShowDialog();
        return _result ?? new WingetCommandResult(false, -1, string.Join(Environment.NewLine, _allOutput), LocalizationService.Get("Updates.UnknownError"));
    }

    private void DialogClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_process is not null) e.Cancel = true;
    }

    private void DialogContentRendered(object? sender, EventArgs e)
    {
        _dialog.PrepareForLiveUpdates();
        _dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                StartCurrentProcess();
            }
            catch (Exception ex)
            {
                _result = new WingetCommandResult(false, -1, string.Empty, ex.Message);
                Finish();
            }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_process is null) return;
        DrainOutput();
        try { if (!_process.Process.HasExited) return; } catch { return; }

        if (!_processExitObserved)
        {
            _processExitObserved = true;
            _timer.Stop();
            try { await _process.OutputCompleted.ConfigureAwait(true); } catch { }
            DrainOutput();
        }

        int exitCode = _process.Process.ExitCode;
        if (exitCode != 0)
            _failedPackages++;

        _process.Dispose();
        _process = null;

        string output = string.Join(Environment.NewLine, _allOutput);
        bool success = _failedPackages == 0;
        _result = new WingetCommandResult(success, success ? 0 : exitCode, output, success ? string.Empty : output);
        Finish();
    }

    private void StartCurrentProcess()
    {
        _process = _service.StartUpdateSession(_knownUpdates, _updateAll, _pendingOutput.Enqueue);
        _timer.Start();
    }

    private void DrainOutput()
    {
        while (_pendingOutput.TryDequeue(out string? line))
        {
            _allOutput.Add(line);
            UpdateCurrentName(line);
            string? phase = GetUpdatePhase(_currentName, line);
            if (phase is null || string.Equals(phase, _lastPhase, StringComparison.OrdinalIgnoreCase)) continue;
            _lastPhase = phase;
            _dialog.SetStatus(phase);
        }
    }

    private void UpdateCurrentName(string line)
    {
        // WinGet normally identifies each package as: Found <display name> [<package id>] Version ...
        Match found = Regex.Match(line.Trim(), @"^Found\s+(?<name>.+?)\s+\[(?<id>[^\]]+)\](?:\s+Version\s+.*)?$", RegexOptions.IgnoreCase);
        if (found.Success)
        {
            string id = found.Groups["id"].Value.Trim();
            WingetUpdateInfo? known = _knownUpdates.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            _currentName = known?.Name ?? found.Groups["name"].Value.Trim();
            string preparing = LocalizationService.Format("Updates.Preparing", _currentName);
            if (!string.Equals(preparing, _lastPhase, StringComparison.OrdinalIgnoreCase))
            {
                _lastPhase = preparing;
                _dialog.SetStatus(preparing);
            }
        }
    }

    private static string? GetUpdatePhase(string packageName, string line)
    {
        string text = line.Trim();
        if (text.Length == 0) return null;
        if (Regex.IsMatch(text, @"\bDownloading\b", RegexOptions.IgnoreCase))
            return LocalizationService.Format("Updates.Downloading", packageName);
        if (Regex.IsMatch(text, "Starting package install|Starting installer|Installing", RegexOptions.IgnoreCase))
            return LocalizationService.Format("Updates.Installing", packageName);
        return null;
    }

    private void Finish()
    {
        _timer.Stop();
        _process?.Dispose();
        _process = null;
        _dialog.Dispatcher.BeginInvoke(new Action(() => _dialog.Close()), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Tick;
        _dialog.Closing -= DialogClosing;
        _dialog.ContentRendered -= DialogContentRendered;
        _process?.Dispose();
        if (_dialog.IsVisible) _dialog.Close();
    }
}
