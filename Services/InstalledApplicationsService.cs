using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WingetDashboard.Services;

public sealed record InstalledApplicationInfo(string Name, string Id, string Version, string Source, bool IsChecked = false)
{
    public string VersionLabel => LocalizationService.Get("Installed.Version") + " ";
    public string DisplayText => string.IsNullOrWhiteSpace(Version) || Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{Name} — {LocalizationService.Get("Installed.Version")} {Version}";
}

/// <summary>
/// Discovers installed applications through WinGet and applies the Installed Applications
/// safety filter. The filter is intentionally isolated from the UI so it can be audited and
/// refined without changing keyboard/accessibility code.
/// </summary>
public sealed class InstalledApplicationsService
{
    internal UninstallProcessHandle StartPackageUninstall(InstalledApplicationInfo app, Action<string> onOutput, Action<string> onError)
    {
        // Uninstallers frequently require elevation (Zoom is one confirmed example), and
        // unlike installs there is no reliable package-manifest elevation decision for the
        // already-installed ARP/MSIX entry. Each uninstall is intentionally a single user
        // action, so request one UAC consent for that one operation.
        return StartElevatedPackageUninstall(app, onOutput, onError);
    }

    internal static ProcessStartInfo CreateUninstallStartInfo(InstalledApplicationInfo app)
    {
        var psi = new ProcessStartInfo
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
        foreach (string argument in BuildUninstallArguments(app)) psi.ArgumentList.Add(argument);
        return psi;
    }

    internal static List<string> BuildUninstallArguments(InstalledApplicationInfo app)
    {
        var arguments = new List<string>
        {
            "uninstall", "--id", app.Id, "--exact", "--silent",
            "--accept-source-agreements", "--disable-interactivity"
        };
        if (!string.IsNullOrWhiteSpace(app.Version) &&
            !app.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--version");
            arguments.Add(app.Version);
        }
        return arguments;
    }

    private static UninstallProcessHandle StartElevatedPackageUninstall(InstalledApplicationInfo app, Action<string> onOutput, Action<string> onError)
    {
        string pipeName = "WingetDashboard-Uninstall-" + Guid.NewGuid().ToString("N");
        var pipe = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.In, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The Dashboard executable path could not be determined.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(ElevatedUninstallWorker.Switch);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(app.Id);
        startInfo.ArgumentList.Add(app.Version ?? "");
        SearchInstallService.AllowElevationForeground();

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
        }
        catch { pipe.Dispose(); throw; }

        Task outputCompleted = Task.Run(async () =>
        {
            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, leaveOpen: false);
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (line.StartsWith("STDERR|", StringComparison.Ordinal)) onError(line[7..]);
                        else if (line.StartsWith("STDOUT|", StringComparison.Ordinal)) onOutput(line[7..]);
                        else onOutput(line);
                    }
                }
                catch { }
            }
        });
        return new UninstallProcessHandle(process, outputCompleted);
    }

    public Task<IReadOnlyList<InstalledApplicationInfo>> GetInstalledApplicationsAsync() => Task.Run<IReadOnlyList<InstalledApplicationInfo>>(() =>
    {
        WingetListResult winget = RunWingetList();
        // APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND is an explicit empty inventory.
        if (winget.ExitCode == unchecked((int)0x8A150014))
            return Array.Empty<InstalledApplicationInfo>();

        if (!winget.Success)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(winget.Error)
                ? "Winget could not enumerate installed applications."
                : winget.Error.Trim());

        List<InstalledApplicationInfo> all = ParseListTable(winget.Output);
        var registry = RegistryInventory.Read();
        var shown = new List<InstalledApplicationInfo>();

        foreach (InstalledApplicationInfo app in all)
        {
            if (InstalledApplicationFilter.ShouldShow(app, registry))
                shown.Add(app);
        }

        // Keep normal applications first. Runtime/SDK/redistributable/dependency packages
        // remain fully visible, but are grouped at the end so they do not clutter the
        // primary application list. Each group is sorted alphabetically.
        shown.Sort((a, b) =>
        {
            bool aRuntime = RuntimeDevelopmentAllowlist.IsKnownRuntimeOrDevelopmentPackage(a);
            bool bRuntime = RuntimeDevelopmentAllowlist.IsKnownRuntimeOrDevelopmentPackage(b);
            if (aRuntime != bRuntime)
                return aRuntime ? 1 : -1;
            return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });
        return shown;
    });

    private static WingetListResult RunWingetList()
    {
        try
        {
            var psi = new ProcessStartInfo
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
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("--accept-source-agreements");
            psi.ArgumentList.Add("--disable-interactivity");

            using Process? process = Process.Start(psi);
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

    internal static List<InstalledApplicationInfo> ParseListTable(string output)
    {
        string[] lines = output.Replace("\r", string.Empty).Split('\n');
        int header = Array.FindIndex(lines, line => line.TrimStart().StartsWith("Name", StringComparison.OrdinalIgnoreCase) &&
                                                   line.Contains("Id", StringComparison.OrdinalIgnoreCase) &&
                                                   line.Contains("Version", StringComparison.OrdinalIgnoreCase));
        if (header < 0 || header + 1 >= lines.Length)
            throw new InvalidOperationException("Winget returned an unrecognized installed-applications table.");

        string headerLine = lines[header];
        int idStart = headerLine.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
        int versionStart = headerLine.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
        int sourceStart = headerLine.IndexOf("Source", StringComparison.OrdinalIgnoreCase);
        if (idStart <= 0 || versionStart <= idStart)
            throw new InvalidOperationException("Winget returned an unrecognized installed-applications table.");

        if (!Regex.IsMatch(lines[header + 1].Trim(), @"^-+$"))
            throw new InvalidOperationException("Winget returned an incomplete installed-applications table.");

        var results = new List<InstalledApplicationInfo>();
        for (int i = header + 2; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            string trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^\d+\s+(upgrades?|packages?)\b", RegexOptions.IgnoreCase)) continue;

            string name = Slice(line, 0, idStart).Trim();
            string id = Slice(line, idStart, versionStart).Trim();
            string version = sourceStart > versionStart ? Slice(line, versionStart, sourceStart).Trim() : Slice(line, versionStart, line.Length).Trim();
            string source = sourceStart >= 0 && line.Length > sourceStart ? line[sourceStart..].Trim() : string.Empty;

            // Fixed-width output may be shortened with an ellipsis, but a real row still needs name/id.
            if (name.Length == 0 || id.Length == 0)
                throw new InvalidOperationException("Winget returned an incomplete installed-application row.");
            results.Add(new(name, id, version, source));
        }
        return results;
    }

    private static string Slice(string value, int start, int end)
    {
        if (start >= value.Length) return string.Empty;
        int safeEnd = Math.Min(end, value.Length);
        return safeEnd <= start ? string.Empty : value[start..safeEnd];
    }

    private sealed record WingetListResult(bool Success, int ExitCode, string Output, string Error);
}

/// <summary>
/// Conservative safety filter: unknown entries remain visible. Only entries with strong
/// evidence of being Windows infrastructure, package-manager plumbing, resource/framework
/// internals, compatibility plumbing, security UI, or device-driver packages are hidden.
/// Developer runtimes/SDKs/redistributables are deliberately NOT hidden.
/// </summary>
internal static class InstalledApplicationFilter
{
    private static readonly string[] HiddenIdPrefixes =
    {
        "MSIX\\Microsoft.WinAppRuntime.DDLM.",
        "MSIX\\MicrosoftCorporationII.WinAppRuntime.Main.",
        "MSIX\\MicrosoftCorporationII.WinAppRuntime.Singleton_",
        "MSIX\\Microsoft.Winget.Source_",
        "Microsoft.AppInstaller",
        "MSIX\\Microsoft.WindowsStore_",
        "MSIX\\Microsoft.StorePurchaseApp_",
        "MSIX\\Microsoft.Services.Store.Engagement_",
        "MSIX\\Microsoft.Office.ActionsServer_",
        "MSIX\\Microsoft.OfficePushNotificationUtility_",
        "MSIX\\Microsoft.ApplicationCompatibilityEnhancements_",
        "MSIX\\Microsoft.SecHealthUI_",
        "MSIX\\Microsoft.LanguageExperiencePack",
        "ARP\\Machine\\X64\\{22221111-1111-1111-1111-111111111111}.sdb"
    };

    private static readonly string[] DriverNameTokens =
    {
        " chipset software", " audio driver", " ethernet controller driver", " pcie adapter driver",
        " wi-fi adapter driver", " wifi adapter driver", " bluetooth adapter driver"
    };

    internal static bool ShouldShow(InstalledApplicationInfo app, RegistryInventory registry)
    {
        if (HiddenIdPrefixes.Any(prefix => app.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (RuntimeDevelopmentAllowlist.IsKnownRuntimeOrDevelopmentPackage(app))
            return true;

        if (registry.IsSystemComponent(app.Name))
            return false;

        if (LooksLikeDriverPackage(app.Name))
            return false;

        return true;
    }

    private static bool LooksLikeDriverPackage(string name)
    {
        string normalized = " " + name.Trim().ToLowerInvariant();
        return DriverNameTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }
}

/// <summary>
/// Reads Windows' uninstall registration. SystemComponent=1 is an OS/vendor supplied signal
/// used to keep entries out of normal Apps & Features UI; it is stronger than guessing by name.
/// </summary>
internal sealed class RegistryInventory
{
    private readonly HashSet<string> _systemComponents;
    private RegistryInventory(HashSet<string> systemComponents) => _systemComponents = systemComponents;

    internal bool IsSystemComponent(string displayName) => _systemComponents.Contains(displayName.Trim());

    internal static RegistryInventory Read()
    {
        var system = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, system);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, system);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Registry64, system);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Registry32, system);
        return new(system);
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, HashSet<string> system)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null) return;
            foreach (string subName in root.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? sub = root.OpenSubKey(subName);
                    string? name = sub?.GetValue("DisplayName") as string;
                    object? marker = sub?.GetValue("SystemComponent");
                    if (!string.IsNullOrWhiteSpace(name) && Convert.ToInt32(marker ?? 0) == 1)
                        system.Add(name.Trim());
                }
                catch { }
            }
        }
        catch { }
    }
}

public sealed record WingetUninstallResult(
    bool Success,
    IReadOnlyList<InstalledApplicationInfo> FailedApplications,
    string Output);

internal sealed class UninstallProcessHandle : IDisposable
{
    internal Process Process { get; }
    internal Task OutputCompleted { get; }

    internal UninstallProcessHandle(Process process, Task outputCompleted)
    {
        Process = process;
        OutputCompleted = outputCompleted;
    }

    public void Dispose() => Process.Dispose();
}

internal sealed class UninstallStatusSession : IDisposable
{
    private readonly InstalledApplicationsService _service;
    private readonly IReadOnlyList<InstalledApplicationInfo> _applications;
    private readonly InstallationStatusWindow _dialog;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingOutput = new();
    private readonly List<string> _allOutput = new();
    private readonly List<InstalledApplicationInfo> _failed = new();
    private UninstallProcessHandle? _process;
    private WingetUninstallResult? _result;
    private int _currentIndex;
    private bool _processExitObserved;

    internal UninstallStatusSession(
        InstalledApplicationsService service,
        IReadOnlyList<InstalledApplicationInfo> applications,
        System.Windows.Window? owner)
    {
        _service = service;
        _applications = applications;
        string firstName = applications.Count > 0 ? applications[0].Name : "application";
        _dialog = new InstallationStatusWindow(LocalizationService.Format("Uninstall.Status", firstName), LocalizationService.Get("Uninstall.WindowTitle"));
        if (owner is not null)
        {
            _dialog.Owner = owner;
            _dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        }

        _timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, _dialog.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _timer.Tick += Tick;
        _dialog.Closing += DialogClosing;
        _dialog.ContentRendered += DialogContentRendered;
    }

    internal WingetUninstallResult Run()
    {
        _dialog.ShowDialog();
        return _result ?? new WingetUninstallResult(false, _applications.ToList(), string.Join(Environment.NewLine, _allOutput));
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
            try { StartCurrentProcess(); }
            catch (Exception ex)
            {
                _allOutput.Add(ex.Message);
                if (_applications.Count > 0) _failed.Add(_applications[_currentIndex]);
                CompleteOrContinue();
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
            _failed.Add(_applications[_currentIndex]);

        _process.Dispose();
        _process = null;
        CompleteOrContinue();
    }

    private void CompleteOrContinue()
    {
        if (_currentIndex + 1 < _applications.Count)
        {
            _currentIndex++;
            _processExitObserved = false;
            _dialog.SetStatus(LocalizationService.Format("Uninstall.Status", _applications[_currentIndex].Name));
            try { StartCurrentProcess(); }
            catch (Exception ex)
            {
                _allOutput.Add(ex.Message);
                _failed.Add(_applications[_currentIndex]);
                CompleteOrContinue();
            }
            return;
        }

        _result = new WingetUninstallResult(_failed.Count == 0, _failed.ToList(), string.Join(Environment.NewLine, _allOutput));
        Finish();
    }

    private void StartCurrentProcess()
    {
        InstalledApplicationInfo app = _applications[_currentIndex];
        _process = _service.StartPackageUninstall(app,
            _pendingOutput.Enqueue,
            _pendingOutput.Enqueue);
        _timer.Start();
    }

    private void DrainOutput()
    {
        while (_pendingOutput.TryDequeue(out var entry))
        {
            _allOutput.Add(entry);
        }
    }

    private void Finish()
    {
        _timer.Stop();
        _process?.Dispose();
        _process = null;
        _dialog.Dispatcher.BeginInvoke(new Action(() => _dialog.Close()),
            System.Windows.Threading.DispatcherPriority.Background);
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
