using System.Windows;

namespace WingetDashboard;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (WingetDashboard.Services.ElevatedUninstallWorker.IsWorker(e.Args))
        {
            int exitCode = WingetDashboard.Services.ElevatedUninstallWorker.Run(e.Args[1], e.Args[2], e.Args[3]);
            Environment.Exit(exitCode);
            return;
        }

        if (WingetDashboard.Services.ElevatedWingetWorker.IsWorker(e.Args))
        {
            int exitCode = WingetDashboard.Services.ElevatedWingetWorker.Run(e.Args[1], e.Args[2]);
            Environment.Exit(exitCode);
            return;
        }

        if (WingetDashboard.Services.ElevatedUpdateWorker.IsWorker(e.Args))
        {
            int exitCode = WingetDashboard.Services.ElevatedUpdateWorker.Run(
                e.Args[1],
                e.Args[2],
                e.Args.Skip(3).ToArray());
            Environment.Exit(exitCode);
            return;
        }

        var settings = WingetDashboard.Services.AppSettingsService.Load();
        WingetDashboard.Services.LocalizationService.Initialize(settings.Language);

        // Configure WinForms theming before creating the hosted Search TextBox.
        System.Windows.Forms.Application.SetColorMode(
            System.Windows.Forms.SystemColorMode.System);

        base.OnStartup(e);
    }
}
