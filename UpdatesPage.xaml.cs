using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using WingetDashboard.Services;

namespace WingetDashboard;

public partial class UpdatesPage : System.Windows.Controls.UserControl
{
    private readonly Action _returnToDashboard;
    private readonly WingetUpdateService _updateService = new();
    private readonly List<System.Windows.Controls.CheckBox> _updateBoxes = new();
    public ObservableCollection<UpdateItemViewModel> Updates { get; } = new();

    public UpdatesPage(Action returnToDashboard)
    {
        _returnToDashboard = returnToDashboard ?? throw new ArgumentNullException(nameof(returnToDashboard));
        InitializeComponent();
        ApplyLocalization();
        DataContext = this;
        Loaded += UpdatesPage_Loaded;
    }

    private void ApplyLocalization()
    {
        TitleText.Text = LocalizationService.Get("Updates.Title");
        StatusText.Text = LocalizationService.Get("Updates.Checking");
        SelectAllButton.Content = LocalizationService.Get("Updates.SelectAll");
        DeselectAllButton.Content = LocalizationService.Get("Updates.DeselectAll");
        UpdateSelectedButton.Content = LocalizationService.Get("Updates.UpdateSelected");
        UpdateAllButton.Content = LocalizationService.Get("Updates.UpdateAll");
        BackButton.Content = LocalizationService.Get("Common.BackToDashboard");
    }

    private async void UpdatesPage_Loaded(object sender, RoutedEventArgs e) => await RefreshUpdatesAsync(focusList: true);

    private async Task RefreshUpdatesAsync(bool focusList, bool afterUpdate = false)
    {
        SetBusy(true, LocalizationService.Get("Updates.Checking"));
        AnnounceCheckingStatus();
        bool announceAndFocusFirst = false;
        bool focusBack = false;

        try
        {
            var available = await _updateService.GetAvailableUpdatesAsync();
            Updates.Clear();
            foreach (WingetUpdateInfo item in available)
                Updates.Add(new UpdateItemViewModel(item));

            BuildUpdateCheckBoxes();

            StatusText.Text = Updates.Count == 0
                ? (afterUpdate ? LocalizationService.Get("Updates.NoneAvailable") : LocalizationService.Get("Updates.NoneFound"))
                : LocalizationService.Format("Updates.CountFound", Updates.Count);

            if (focusList)
            {
                announceAndFocusFirst = Updates.Count > 0;
                if (Updates.Count == 0)
                    _ = Dispatcher.BeginInvoke(new Action(FocusEmptyStatus),
                        System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        catch (Exception ex)
        {
            Updates.Clear();
            BuildUpdateCheckBoxes();
            StatusText.Text = LocalizationService.Get("Updates.CheckFailed");
            System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Updates.CheckFailedDetails", ex.Message),
                LocalizationService.Get("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            focusBack = focusList;
        }
        finally
        {
            SetBusy(false, StatusText.Text);

            if (announceAndFocusFirst)
                BeginInitialAnnouncementAndFocus();
            else if (focusBack)
                _ = Dispatcher.BeginInvoke(new Action(() => BackButton.Focus()),
                    System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void BuildUpdateCheckBoxes()
    {
        UpdatesPanel.Children.Clear();
        _updateBoxes.Clear();

        foreach (UpdateItemViewModel item in Updates)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = item.DisplayText,
                FontSize = 16,
                Margin = new Thickness(5, 5, 5, 5),
                Padding = new Thickness(3),
                IsChecked = item.IsChecked,
                IsTabStop = false,
                Style = (Style)FindResource("UpdateCheckBoxStyle")
            };

            // Name contains only application/version text. CheckBox role and checked state
            // are supplied natively by WPF/UI Automation and localized by NVDA.
            AutomationProperties.SetName(box, item.DisplayText);

            box.Checked += (_, _) => item.IsChecked = true;
            box.Unchecked += (_, _) => item.IsChecked = false;
            box.GotKeyboardFocus += UpdateBox_GotKeyboardFocus;
            box.PreviewKeyDown += UpdateBox_PreviewKeyDown;

            UpdatesPanel.Children.Add(box);
            _updateBoxes.Add(box);
        }

        if (_updateBoxes.Count > 0)
            _updateBoxes[0].IsTabStop = true;
    }

    private void UpdateBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox box)
            return;

        foreach (var other in _updateBoxes)
            other.IsTabStop = false;
        box.IsTabStop = true;
        box.BringIntoView();
    }

    private void UpdateBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox box || (e.Key != Key.Up && e.Key != Key.Down))
            return;

        int current = _updateBoxes.IndexOf(box);
        int next = Math.Clamp(current + (e.Key == Key.Up ? -1 : 1), 0, _updateBoxes.Count - 1);
        if (next != current)
        {
            box.IsTabStop = false;
            _updateBoxes[next].IsTabStop = true;
            _updateBoxes[next].Focus();
            _updateBoxes[next].BringIntoView();
        }
        e.Handled = true;
    }

    private void AnnounceCheckingStatus()
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(StatusText)
                ?? new TextBlockAutomationPeer(StatusText);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BeginInitialAnnouncementAndFocus()
    {
        // Proven v3.3 sequence: announce the summary first, then move real keyboard
        // focus to the first native WPF CheckBox at ContextIdle.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            AnnounceUpdatesSummary();
            _ = Dispatcher.BeginInvoke(new Action(() => FocusUpdateBox(0)),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AnnounceUpdatesSummary()
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(StatusText)
            ?? new TextBlockAutomationPeer(StatusText);
        string message = LocalizationService.Format("Updates.CountFound", Updates.Count);
        peer.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.ImportantMostRecent,
            message,
            "UpdatesSummary");
    }

    private void FocusUpdateBox(int index)
    {
        if (index < 0 || index >= _updateBoxes.Count)
            return;
        foreach (var box in _updateBoxes)
            box.IsTabStop = false;
        _updateBoxes[index].IsTabStop = true;
        _updateBoxes[index].Focus();
        Keyboard.Focus(_updateBoxes[index]);
        _updateBoxes[index].BringIntoView();
    }

    private int CurrentBoxIndex()
    {
        for (int i = 0; i < _updateBoxes.Count; i++)
            if (_updateBoxes[i].IsKeyboardFocusWithin || _updateBoxes[i].IsTabStop)
                return i;
        return 0;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Updates)
            item.IsChecked = true;
        foreach (var box in _updateBoxes)
            box.IsChecked = true;
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Updates)
            item.IsChecked = false;
        foreach (var box in _updateBoxes)
            box.IsChecked = false;
    }

    private async void UpdateSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = Updates.Where(x => x.IsChecked).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Updates.SelectAtLeastOne"),
                LocalizationService.Get("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            RestoreListFocus();
            return;
        }

        if (System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Updates.ConfirmSelected", selected.Count),
            LocalizationService.Get("Updates.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            RestoreListFocus();
            return;
        }

        SetBusy(true, LocalizationService.Get("Updates.UpdatingSelected"));
        WingetCommandResult result;
        var updateInfos = selected.Select(x => x.Update).ToList();
        using (var session = new UpdateStatusSession(_updateService, updateInfos, updateAll: false, Window.GetWindow(this)))
            result = session.Run();

        await RefreshUpdatesAsync(focusList: false, afterUpdate: true);
        string message = result.Success
            ? LocalizationService.Get("Updates.SelectedSuccess")
            : LocalizationService.Get("Updates.SelectedPartialFailure");
        System.Windows.MessageBox.Show(Window.GetWindow(this), message, LocalizationService.Get("Updates.Title"), MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        FocusAfterRefresh();
    }

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.Count == 0)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Updates.NoUpdatesAvailable"),
                LocalizationService.Get("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (System.Windows.MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Updates.ConfirmAll"),
            LocalizationService.Get("Updates.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true, LocalizationService.Get("Updates.UpdatingAll"));
        WingetCommandResult result;
        using (var session = new UpdateStatusSession(_updateService, Updates.Select(x => x.Update).ToList(), updateAll: true, Window.GetWindow(this)))
            result = session.Run();

        await RefreshUpdatesAsync(focusList: false, afterUpdate: true);
        System.Windows.MessageBox.Show(Window.GetWindow(this),
            result.Success ? LocalizationService.Get("Updates.AllComplete") : LocalizationService.Get("Updates.AllPartialFailure"),
            LocalizationService.Get("Updates.Title"), MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        FocusAfterRefresh();
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        SelectAllButton.IsEnabled = !busy && Updates.Count > 0;
        DeselectAllButton.IsEnabled = !busy && Updates.Count > 0;
        UpdateSelectedButton.IsEnabled = !busy && Updates.Count > 0;
        UpdateAllButton.IsEnabled = !busy && Updates.Count > 0;
        BackButton.IsEnabled = !busy;
        UpdatesScrollViewer.IsEnabled = !busy && Updates.Count > 0;
    }

    private void FocusAfterRefresh()
    {
        if (Updates.Count > 0)
            RestoreListFocus();
        else
            FocusEmptyStatus();
    }

    private void FocusEmptyStatus()
    {
        StatusText.Focus();
        Keyboard.Focus(StatusText);
    }

    private void RestoreListFocus()
    {
        if (_updateBoxes.Count == 0)
            return;
        int target = CurrentBoxIndex();
        _ = Dispatcher.BeginInvoke(new Action(() => FocusUpdateBox(target)),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _returnToDashboard();
}

public sealed class UpdateItemViewModel : INotifyPropertyChanged
{
    private bool _isChecked;
    public WingetUpdateInfo Update { get; }
    public string DisplayText => Update.DisplayText;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public UpdateItemViewModel(WingetUpdateInfo update) => Update = update;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public override string ToString() => DisplayText;
}
