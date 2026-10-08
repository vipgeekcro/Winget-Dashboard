using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using WingetDashboard.Services;

namespace WingetDashboard;

public partial class InstalledApplicationsPage : System.Windows.Controls.UserControl
{
    private readonly Action _backToDashboard;
    private readonly InstalledApplicationsService _service = new();
    public ObservableCollection<InstalledApplicationInfo> Applications { get; } = new();

    public InstalledApplicationsPage(Action backToDashboard)
    {
        InitializeComponent();
        ApplyLocalization();
        DataContext = this;
        _backToDashboard = backToDashboard;
        ApplicationsList.ItemContainerGenerator.StatusChanged += ApplicationsList_ContainerStatusChanged;
        Loaded += InstalledApplicationsPage_Loaded;
    }

    private void ApplyLocalization()
    {
        BackButton.Content = LocalizationService.Get("Common.BackToDashboard");
        TitleText.Text = LocalizationService.Get("Installed.Title");
        StatusText.Text = LocalizationService.Get("Installed.Loading");
        InstructionText.Text = LocalizationService.Get("Installed.Instruction");
    }

    private async void InstalledApplicationsPage_Loaded(object sender, RoutedEventArgs e) => await RefreshApplicationsAsync(true);

    private async System.Threading.Tasks.Task RefreshApplicationsAsync(bool focusList)
    {
        SetBusy(true, LocalizationService.Get("Installed.Loading"));
        try
        {
            var result = await _service.GetInstalledApplicationsAsync();
            Applications.Clear();
            foreach (var app in result)
                Applications.Add(app);
            StatusText.Text = Applications.Count == 0 ? LocalizationService.Get("Installed.NoneFound") : LocalizationService.Format("Installed.CountFound", Applications.Count);
        }
        catch (Exception ex)
        {
            Applications.Clear();
            StatusText.Text = LocalizationService.Get("Installed.LoadFailed");
            System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Installed.LoadFailedDetails", ex.Message), LocalizationService.Get("Installed.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            if (focusList && Applications.Count > 0)
                BeginInitialAnnouncementAndFocus();
            else if (focusList)
                await Dispatcher.InvokeAsync(new Action(() => StatusText.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void InstalledApplicationsPage_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!ApplicationsList.IsKeyboardFocusWithin || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (ApplicationsList.SelectedItem is not InstalledApplicationInfo selected)
            return;
        e.Handled = true;
        UninstallSelectedApplication(selected);
    }

    private async void UninstallSelectedApplication(InstalledApplicationInfo selected)
    {
        var confirmation = System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Uninstall.Confirm", selected.Name), LocalizationService.Get("Installed.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            RestoreListFocus(selected);
            return;
        }

        SetBusy(true, LocalizationService.Format("Uninstall.Progress", selected.Name));
        using (var session = new UninstallStatusSession(_service, new[] { selected }, Window.GetWindow(this)))
            session.Run();

        bool? stillInstalled = null;
        try
        {
            var refreshed = await _service.GetInstalledApplicationsAsync();
            stillInstalled = refreshed.Any(app => SameInstalledEntry(app, selected));
            Applications.Clear();
            foreach (var app in refreshed)
                Applications.Add(app);
            StatusText.Text = Applications.Count == 0 ? LocalizationService.Get("Installed.NoneFound") : LocalizationService.Format("Installed.CountFound", Applications.Count);
        }
        catch
        {
            stillInstalled = null;
            StatusText.Text = LocalizationService.Get("Installed.ReloadFailed");
        }

        SetBusy(false, StatusText.Text);
        if (stillInstalled is null)
            System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Uninstall.VerificationFailed", selected.Name), LocalizationService.Get("Installed.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (stillInstalled == false)
            System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Uninstall.Success", selected.Name), LocalizationService.Get("Installed.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        else
            System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Uninstall.Failed", selected.Name), LocalizationService.Get("Installed.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        FocusNearestAfterRefresh(selected);
    }

    private static bool SameInstalledEntry(InstalledApplicationInfo current, InstalledApplicationInfo original)
    {
        if (!current.Id.Equals(original.Id, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(original.Version) || original.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return true;
        return current.Version.Equals(original.Version, StringComparison.OrdinalIgnoreCase);
    }

    private void BeginInitialAnnouncementAndFocus()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(StatusText) ?? new TextBlockAutomationPeer(StatusText);
            peer.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, $"{StatusText.Text} {InstructionText.Text}", "InstalledApplicationsSummary");
            Dispatcher.BeginInvoke(new Action(() => FocusIndex(0)), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RestoreListFocus(InstalledApplicationInfo selected)
    {
        ApplicationsList.SelectedItem = selected;
        ApplicationsList.UpdateLayout();
        if (ApplicationsList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
        {
            item.Focus();
            Keyboard.Focus(item);
        }
        else
            ApplicationsList.Focus();
    }

    private void FocusNearestAfterRefresh(InstalledApplicationInfo previous)
    {
        if (Applications.Count == 0)
        {
            StatusText.Focus();
            return;
        }
        int index = 0;
        for (int i = 0; i < Applications.Count; i++)
        {
            index = i;
            if (StringComparer.CurrentCultureIgnoreCase.Compare(Applications[i].Name, previous.Name) >= 0)
                break;
        }
        FocusIndex(index);
    }

    private void FocusIndex(int index)
    {
        ApplicationsList.SelectedIndex = Math.Clamp(index, 0, Applications.Count - 1);
        ApplicationsList.UpdateLayout();
        if (ApplicationsList.ItemContainerGenerator.ContainerFromIndex(ApplicationsList.SelectedIndex) is ListBoxItem item)
        {
            item.Focus();
            Keyboard.Focus(item);
            item.BringIntoView();
        }
        else
            ApplicationsList.Focus();
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        ApplicationsList.IsEnabled = !busy && Applications.Count > 0;
        BackButton.IsEnabled = !busy;
    }

    private void ApplicationsList_ContainerStatusChanged(object? sender, EventArgs e)
    {
        if (ApplicationsList.ItemContainerGenerator.Status !=
            System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            return;

        int count = ApplicationsList.Items.Count;
        for (int index = 0; index < count; index++)
        {
            if (ApplicationsList.ItemContainerGenerator.ContainerFromIndex(index) is System.Windows.Controls.ListBoxItem item)
            {
                System.Windows.Automation.AutomationProperties.SetPositionInSet(item, index + 1);
                System.Windows.Automation.AutomationProperties.SetSizeOfSet(item, count);
            }
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _backToDashboard();
}
