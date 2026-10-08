using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WingetDashboard.Services;

internal sealed record InstallOutcome(string Status, string Message);

/// <summary>
/// Owns a running WinGet process and the completion of its redirected output stream.
/// Output is kept in memory by the install session; no diagnostic or temporary log files
/// are created.
/// </summary>
internal sealed class InstallProcessHandle : IDisposable
{
    internal Process Process { get; }
    internal Task OutputCompleted { get; }

    internal InstallProcessHandle(Process process, Task outputCompleted)
    {
        Process = process;
        OutputCompleted = outputCompleted;
    }

    public void Dispose() => Process.Dispose();
}

internal static class SearchInstallService
{
    private const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    internal static void AllowElevationForeground() => AllowSetForegroundWindow(ASFW_ANY);

    internal static ProcessStartInfo CreateWingetStartInfo(string packageId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string arg in new[]
        {
            "install", "--id", packageId, "--exact", "--source", "winget", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    internal static InstallProcessHandle StartPackageInstall(string packageId, Action<string> onOutput)
    {
        WingetDashboard.Interop.InstallElevationDecision decision =
            WingetDashboard.Interop.WingetNativeMetadataService.GetDecision(packageId);

        return decision.Mode == WingetDashboard.Interop.InstallElevationMode.Elevated
            ? StartElevated(packageId, onOutput)
            : StartNormal(packageId, onOutput);
    }

    private static InstallProcessHandle StartNormal(string packageId, Action<string> onOutput)
    {
        var process = new Process
        {
            StartInfo = CreateWingetStartInfo(packageId),
            EnableRaisingEvents = true
        };

        int closedStreams = 0;
        var outputCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        DataReceivedEventHandler capture = (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
                return;
            }

            if (Interlocked.Increment(ref closedStreams) == 2)
                outputCompleted.TrySetResult(true);
        };

        process.OutputDataReceived += capture;
        process.ErrorDataReceived += capture;

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new InstallProcessHandle(process, outputCompleted.Task);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static InstallProcessHandle StartElevated(string packageId, Action<string> onOutput)
    {
        string pipeName = "WingetDashboard-" + Guid.NewGuid().ToString("N");
        var pipe = new System.IO.Pipes.NamedPipeServerStream(
            pipeName,
            System.IO.Pipes.PipeDirection.In,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Dashboard executable path could not be determined.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(ElevatedWingetWorker.Switch);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(packageId);

        // Keep the confirmed foreground handoff immediately before the process that
        // requests elevation. This is required for the tested foreground UAC behavior.
        AllowSetForegroundWindow(ASFW_ANY);

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
                    // A closed/failed pipe is reflected by the worker process exit code.
                }
            }
        });

        return new InstallProcessHandle(process, outputCompleted);
    }

    private static bool Matches(string value, string pattern) =>
        Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);

    internal static InstallOutcome GetPackageOutcome(int exitCode, IReadOnlyCollection<string> outputLines)
    {
        string text = string.Join("\n", outputLines);

        if (Matches(text, "Successfully installed"))
            return new("Success", LocalizationService.Get("Install.Outcome.Success"));

        if (Matches(text, "No available upgrade found|No newer package versions are available|No applicable upgrade found|No applicable update found"))
            return new("AlreadyInstalled", LocalizationService.Get("Install.Outcome.AlreadyInstalled"));

        if (Matches(text, "already installed") && !Matches(text, "upgrade|updat"))
            return new("AlreadyInstalled", LocalizationService.Get("Install.Outcome.AlreadyInstalled"));

        if (exitCode == 0)
            return new("Success", LocalizationService.Get("Install.Outcome.Success"));

        (string Pattern, string Message)[] errors =
        [
            ("cannot be run from an administrator context|elevation.*prohibited", LocalizationService.Get("Install.Error.ElevationProhibited")),
            ("No package found matching input criteria|No package found|Unable to find package|package.*not found", LocalizationService.Get("Install.Error.PackageNotFound")),
            ("not enough (disk )?space|insufficient (disk )?space|disk is full", LocalizationService.Get("Install.Error.DiskSpace")),
            ("another installation is (already )?in progress|another install.*in progress|0x80070652", LocalizationService.Get("Install.Error.AnotherInstall")),
            ("cancelled by the user|canceled by the user|operation was canceled|operation was cancelled|user cancelled|user canceled", LocalizationService.Get("Install.Error.Cancelled")),
            ("restart.*required|reboot.*required|restart your (computer|machine)|reboot your (computer|machine)", LocalizationService.Get("Install.Error.RestartRequired")),
            ("not supported on this (system|machine)|unsupported architecture|not applicable to this machine|does not support this architecture", LocalizationService.Get("Install.Error.Unsupported")),
            ("blocked by.*policy|policy.*blocked|group policy|administrator has blocked", LocalizationService.Get("Install.Error.Policy")),
            ("hash.*mismatch|hash.*does not match|installer hash", LocalizationService.Get("Install.Error.Hash")),
            ("download.*failed|failed to download|could not download|unable to download", LocalizationService.Get("Install.Error.Download")),
            ("0x80072|internet.*(unavailable|connection)|network.*(unavailable|error)|connection.*(failed|timed out)|name resolution|could not resolve", LocalizationService.Get("Install.Error.Network")),
            ("file.*in use|being used by another process|application.*is running|close.*application", LocalizationService.Get("Install.Error.FileInUse")),
            ("dependency.*(missing|failed)|prerequisite.*(missing|required)|required.*component", LocalizationService.Get("Install.Error.Dependency"))
        ];

        foreach (var (pattern, message) in errors)
        {
            if (Matches(text, pattern))
                return new("Error", message);
        }

        return new("Error", LocalizationService.Format("Install.Error.ExitCode", $"{exitCode} (0x{unchecked((uint)exitCode):X8})"));
    }

    internal static string? GetInstallPhase(string packageName, string line)
    {
        string text = line.Trim();
        if (text.Length == 0)
            return null;

        if (Matches(text, @"\bDownloading\b"))
            return LocalizationService.Format("Install.Downloading", packageName);

        if (Matches(text, "Starting package install|Starting installer|Installing"))
            return LocalizationService.Format("Install.Installing", packageName);

        if (Matches(text, "Successfully installed"))
            return LocalizationService.Format("Install.Success", packageName);

        return null;
    }
}

internal sealed class SinglePackageInstallSession : IDisposable
{
    private readonly string _name;
    private readonly string _id;
    private readonly InstallationStatusWindow _dialog;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private readonly List<string> _allOutput = new();

    private InstallProcessHandle? _install;
    private string _lastPhase = "";
    private InstallOutcome? _outcome;
    private bool _processExitObserved;

    internal SinglePackageInstallSession(string name, string id, System.Windows.Window? owner)
    {
        _name = name;
        _id = id;
        _dialog = new InstallationStatusWindow(LocalizationService.Format("Install.Preparing", _name));

        if (owner is not null)
        {
            _dialog.Owner = owner;
            _dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        }

        _timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            _dialog.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _timer.Tick += Tick;
        _dialog.Closing += DialogClosing;
        _dialog.ContentRendered += DialogContentRendered;
    }

    internal InstallOutcome Run()
    {
        _dialog.ShowDialog();
        return _outcome ?? new InstallOutcome("Error", LocalizationService.Get("Install.UnknownError"));
    }

    private void DialogClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_install is not null)
            e.Cancel = true;
    }

    private void DialogContentRendered(object? sender, EventArgs e)
    {
        _dialog.PrepareForLiveUpdates();
        _dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _install = SearchInstallService.StartPackageInstall(_id, _pendingOutput.Enqueue);
                _timer.Start();
            }
            catch
            {
                _outcome = new InstallOutcome("Error", LocalizationService.Get("Install.StartFailed"));
                Finish();
            }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_install is null)
            return;

        DrainOutput();

        try
        {
            if (!_install.Process.HasExited)
                return;
        }
        catch
        {
            return;
        }

        if (!_processExitObserved)
        {
            _processExitObserved = true;
            _timer.Stop();
            try
            {
                await _install.OutputCompleted.ConfigureAwait(true);
            }
            catch
            {
                // The process exit code still provides the final fallback result.
            }
            DrainOutput();
        }

        int exitCode = _install.Process.ExitCode;
        _outcome = SearchInstallService.GetPackageOutcome(exitCode, _allOutput);
        _dialog.SetStatus(_outcome.Status switch
        {
            "Success" => LocalizationService.Format("Install.Success", _name),
            "AlreadyInstalled" => LocalizationService.Format("Install.AlreadyInstalled", _name),
            _ => $"{_name}: {_outcome.Message}"
        });
        Finish();
    }

    private void DrainOutput()
    {
        while (_pendingOutput.TryDequeue(out string? line))
        {
            _allOutput.Add(line);
            string? phase = SearchInstallService.GetInstallPhase(_name, line);
            if (phase is null || string.Equals(phase, _lastPhase, StringComparison.OrdinalIgnoreCase))
                continue;

            _lastPhase = phase;
            _dialog.SetStatus(phase);
        }
    }

    private void Finish()
    {
        _timer.Stop();
        _install?.Dispose();
        _install = null;
        _dialog.Dispatcher.BeginInvoke(
            new Action(() => _dialog.Close()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Tick;
        _dialog.Closing -= DialogClosing;
        _dialog.ContentRendered -= DialogContentRendered;
        _install?.Dispose();

        if (_dialog.IsVisible)
            _dialog.Close();
    }
}

/// <summary>
/// WPF installation-status surface. Status announcements are UI Automation live-region
/// events and therefore do not depend on keyboard focus remaining on this window.
/// </summary>
internal sealed class InstallationStatusWindow : System.Windows.Window
{
    private readonly System.Windows.Controls.TextBlock _status;
    private string _lastAnnouncedText = "";
    private bool _liveUpdatesReady;

    internal InstallationStatusWindow(string initialStatus, string? title = null)
    {
        Title = title ?? LocalizationService.Get("Install.WindowTitle");
        Width = 650;
        Height = 145;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        SizeToContent = System.Windows.SizeToContent.Manual;

        // ShowDialog() makes WPF expose WindowAutomationPeer.IsDialog=true by default.
        // NVDA can then re-announce the dialog title plus its initial content later,
        // which duplicated "Preparing to install..." during a long download.
        // Explicit false overrides that automatic dialog classification while the
        // window remains modal and the child TextBlock remains a UIA LiveRegion.
        System.Windows.Automation.AutomationProperties.SetIsDialog(this, false);

        _status = new System.Windows.Controls.TextBlock
        {
            Margin = new System.Windows.Thickness(20),
            Padding = new System.Windows.Thickness(12),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Focusable = true,
            Text = initialStatus
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(
            _status, System.Windows.Automation.AutomationLiveSetting.Assertive);
        System.Windows.Automation.AutomationProperties.SetName(_status, initialStatus);

        var border = new System.Windows.Controls.Border
        {
            BorderThickness = new System.Windows.Thickness(1),
            Child = _status
        };
        Content = border;
    }

    internal void PrepareForLiveUpdates()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(PrepareForLiveUpdates);
            return;
        }

        // Give NVDA one stable focus target immediately when the modal window opens.
        // Preparing is therefore spoken by ordinary focus/window presentation, not by
        // LiveRegionChanged. Subsequent phases use only the live-region path.
        _status.Focus();
        _lastAnnouncedText = _status.Text;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _status.Focusable = false;
            _liveUpdatesReady = true;
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    internal void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(text));
            return;
        }

        text = text.Trim();
        if (!_liveUpdatesReady || text.Length == 0 || string.Equals(text, _lastAnnouncedText, StringComparison.Ordinal))
            return;

        _lastAnnouncedText = text;
        _status.Text = text;
        // NVDA reads the UIA element's Name for live-region changes, so keep Name and
        // visible text identical before raising exactly one LiveRegionChanged event.
        System.Windows.Automation.AutomationProperties.SetName(_status, text);
        UpdateLayout();

        System.Windows.Automation.Peers.AutomationPeer? peer =
            System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(_status)
            ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
}
