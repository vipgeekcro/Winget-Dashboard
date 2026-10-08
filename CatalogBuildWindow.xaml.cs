using System;
using System.Threading.Tasks;
using System.Windows;
using WingetDashboard.Services;
using System.Windows.Threading;

namespace WingetDashboard;

public partial class CatalogBuildWindow : Window
{
    private readonly Func<Task> _operation;
    private bool _started;

    public Exception? OperationException { get; private set; }

    public CatalogBuildWindow(Func<Task> operation, string? message = null, string? progressName = null)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        InitializeComponent();
        Title = "Winget Dashboard";
        string effectiveMessage = string.IsNullOrWhiteSpace(message) ? LocalizationService.Get("Catalog.Build.Progress") : message;
        string effectiveProgressName = string.IsNullOrWhiteSpace(progressName) ? LocalizationService.Get("Catalog.Build.Title") : progressName;
        MessageText.Text = effectiveMessage;
        System.Windows.Automation.AutomationProperties.SetName(MessageText, effectiveMessage);
        System.Windows.Automation.AutomationProperties.SetName(ProgressIndicator, effectiveProgressName);

        ContentRendered += CatalogBuildWindow_ContentRendered;
    }

    private async void CatalogBuildWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_started)
            return;

        _started = true;

        // Give WPF one idle pass so the text and indeterminate progress bar are fully rendered.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        try
        {
            await _operation();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            OperationException = ex;
            DialogResult = false;
        }
    }
}
