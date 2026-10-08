using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using WingetDashboard.Services;
using Beta29.SearchBackend;

namespace WingetDashboard;

public partial class SearchPage : System.Windows.Controls.UserControl
{
    private readonly CatalogService _catalogService;
    private readonly Action _returnToDashboard;
    private readonly System.Windows.Forms.TextBox _searchBox = new();

    public ObservableCollection<SearchResultViewModel> Results { get; } = new();

    public SearchPage(CatalogService catalogService, Action returnToDashboard)
    {
        _catalogService = catalogService;
        _returnToDashboard = returnToDashboard;
        InitializeComponent();
        ApplyLocalization();
        ConfigureSearchBox();
        DataContext = this;
        Loaded += SearchPage_Loaded;
    }

    private void ApplyLocalization()
    {
        BackButton.Content = LocalizationService.Get("Common.BackToDashboard");
        System.Windows.Automation.AutomationProperties.SetName(BackButton, LocalizationService.Get("Common.BackToDashboard"));
        TitleText.Text = LocalizationService.Get("Search.Title");
        SearchActionButton.Content = LocalizationService.Get("Search.Button");
        InstructionText.Text = LocalizationService.Get("Search.Instruction");
        InstallMenuItem.Header = LocalizationService.Get("Search.Context.Install");
        DetailsMenuItem.Header = LocalizationService.Get("Search.Context.Details");
        System.Windows.Automation.AutomationProperties.SetName(SearchBoxHost, LocalizationService.Get("Search.Box.Name"));
        System.Windows.Automation.AutomationProperties.SetHelpText(SearchBoxHost, LocalizationService.Get("Search.Box.HelpText"));
    }

    private void ConfigureSearchBox()
    {
        _searchBox.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
        _searchBox.Font = new System.Drawing.Font("Segoe UI", 11F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
        _searchBox.AccessibleName = LocalizationService.Get("Search.Box.Name");
        _searchBox.AccessibleDescription = LocalizationService.Get("Search.Box.HelpText");
        _searchBox.KeyDown += SearchBox_KeyDown;
        // GotFocus is required for reliable SelectAll across the WPF/WinForms boundary.
        _searchBox.GotFocus += SearchBox_GotFocus;

        // Keep SearchBox colors independent of WindowsFormsHost property mapping.
        SearchBoxHost.PropertyMap.Remove("Background");
        SearchBoxHost.PropertyMap.Remove("Foreground");
        SearchBoxHost.Child = _searchBox;

        // Preserve the current explicit RGB painting path; the remaining visual theme bug is deferred.
        ApplyResolvedSearchBoxColors();
    }

    private static System.Drawing.Color ToExplicitRgb(System.Drawing.Color color)
        => System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

    private void ApplyResolvedSearchBoxColors()
    {
        _searchBox.BackColor = ToExplicitRgb(System.Drawing.SystemColors.Window);
        _searchBox.ForeColor = ToExplicitRgb(System.Drawing.SystemColors.WindowText);
    }

    private void SearchPage_Loaded(object sender, RoutedEventArgs e)
    {
        _searchBox.Focus();
    }

    private void SearchBox_GotFocus(object? sender, EventArgs e)
    {
        _searchBox.BeginInvoke(new Action(() =>
        {
            if (_searchBox.Focused && _searchBox.TextLength > 0)
                _searchBox.SelectAll();
        }));
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _returnToDashboard();
    }

    private void SearchActionButton_Click(object sender, RoutedEventArgs e)
    {
        RunSearch();
    }

    private void SearchPage_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        // Enter/F2 belong to the results list only. A selected result may remain selected
        // while keyboard focus is in the hosted WinForms SearchBox; in that case the
        // page-level PreviewKeyDown must not steal SearchBox Enter and reinstall the
        // previously selected package.
        if (!ResultsList.IsKeyboardFocusWithin)
            return;

        if (ResultsList.SelectedItem is not SearchResultViewModel selected)
            return;

        if (key == System.Windows.Input.Key.F2)
        {
            e.Handled = true;
            ShowProgramDetails(selected);
            return;
        }

        if (key == System.Windows.Input.Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            InstallSelectedPackage(selected);
        }
    }

    private void InstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResultViewModel selected)
            InstallSelectedPackage(selected);
    }

    private void DetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResultViewModel selected)
            ShowProgramDetails(selected);
    }

    private void InstallSelectedPackage(SearchResultViewModel selected)
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            LocalizationService.Format("Install.Confirm", selected.Name),
            LocalizationService.Get("Install.Confirm.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            RestoreResultFocus(selected);
            return;
        }

        InstallOutcome outcome;
        System.Windows.Window? ownerWindow = Window.GetWindow(this);

        using (var session = new SinglePackageInstallSession(selected.Name, selected.Entry.Id, ownerWindow))
            outcome = session.Run();

        string finalText = outcome.Status switch
        {
            "Success" => LocalizationService.Format("Install.Success", selected.Name),
            "AlreadyInstalled" => LocalizationService.Format("Install.AlreadyInstalled", selected.Name),
            _ => $"{LocalizationService.Format("Install.Failed", selected.Name)}\n\n{outcome.Message}"
        };
        System.Windows.MessageBox.Show(
            Window.GetWindow(this), finalText, LocalizationService.Get("Install.Complete.Title"), MessageBoxButton.OK,
            outcome.Status == "Error" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        RestoreResultFocus(selected);
    }

    private void RestoreResultFocus(SearchResultViewModel selected)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ResultsList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
                item.Focus();
            else
                ResultsList.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void ShowProgramDetails(SearchResultViewModel selected)
    {
        var window = new ProgramDetailsWindow(selected.Entry);
        window.Closed += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ResultsList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
                    item.Focus();
                else
                    ResultsList.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        };
        window.Show();
    }

    private void AnnounceResultsInstruction()
    {
        if (UIElementAutomationPeer.CreatePeerForElement(ResultsList) is not AutomationPeer peer)
            peer = new ListBoxAutomationPeer(ResultsList);

        peer.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.ImportantMostRecent,
            LocalizationService.Get("Search.Instruction"),
            "SearchResultsInstruction");
    }

    private void SearchBox_KeyDown(object? sender, System.Windows.Forms.KeyEventArgs e)
    {
        if (e.KeyCode == System.Windows.Forms.Keys.Left && e.Modifiers == System.Windows.Forms.Keys.Alt)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            _returnToDashboard();
            return;
        }

        if (e.KeyCode != System.Windows.Forms.Keys.Enter || e.Modifiers != System.Windows.Forms.Keys.None)
            return;

        e.SuppressKeyPress = true;
        e.Handled = true;
        RunSearch();
    }

    private void RunSearch()
    {
        if (_catalogService.LoadState != CatalogLoadState.Ready)
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                LocalizationService.Get("Search.CatalogNotReady"),
                LocalizationService.Get("Search.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _searchBox.Focus();
            return;
        }

        string query = _searchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                LocalizationService.Get("Search.EnterQuery"),
                LocalizationService.Get("Search.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _searchBox.Focus();
            return;
        }

        try
        {
            List<FastScoredEntry> searchResults = CatalogSearchBackend.SearchCatalog(query, 100);
            Results.Clear();
            foreach (FastScoredEntry scored in searchResults)
                Results.Add(new SearchResultViewModel(scored.Entry));

            if (Results.Count > 0)
            {
                ResultsList.IsEnabled = true;
                ResultsList.SelectedIndex = 0;
                ResultsList.UpdateLayout();

                // Announce the one-time list instruction before moving keyboard focus to the
                // selected result. The subsequent real focus event lets NVDA read that item.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    AnnounceResultsInstruction();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem firstItem)
                            firstItem.Focus();
                        else
                            ResultsList.Focus();
                    }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                ResultsList.SelectedIndex = -1;
                ResultsList.IsEnabled = false;
                System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    LocalizationService.Get("Search.NoResults"),
                    LocalizationService.Get("Search.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                _searchBox.Focus();
            }
        }
        catch (Exception ex)
        {
            Results.Clear();
            ResultsList.SelectedIndex = -1;
            ResultsList.IsEnabled = false;
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"{LocalizationService.Get("Search.Failed")}\n\n{ex.Message}",
                LocalizationService.Get("Search.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _searchBox.Focus();
        }
    }

}

public sealed class SearchResultViewModel
{
    public FastSearchEntry Entry { get; }
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher;
    public string Version => Entry.Version;
    public string VersionLabel => LocalizationService.Get("Search.Result.Version") + " ";
    public string ShortDescription => Entry.ShortDescription;

    public SearchResultViewModel(FastSearchEntry entry)
    {
        Entry = entry;
    }

    public override string ToString() => $"{Name}, {LocalizationService.Get("Search.Result.Version")} {Version}, {Publisher}. {ShortDescription}";
}
