using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using Beta29.SearchBackend;
using WingetDashboard.Services;

namespace WingetDashboard;

public partial class CreateListPage : System.Windows.Controls.UserControl
{
    private readonly CatalogService _catalogService;
    private readonly Action _returnToDashboard;
    private readonly System.Windows.Forms.TextBox _searchBox = new();

    public ObservableCollection<SearchResultViewModel> Results { get; } = new();
    public ObservableCollection<ListApplicationItem> AddedApplications { get; } = new();

    public CreateListPage(CatalogService catalogService, Action returnToDashboard)
    {
        _catalogService = catalogService;
        _returnToDashboard = returnToDashboard;
        InitializeComponent();
        ApplyLocalization();
        ConfigureSearchBox();
        DataContext = this;
        Loaded += CreateListPage_Loaded;
    }

    private void ApplyLocalization()
    {
        TitleText.Text = LocalizationService.Get("Lists.Create.Title");
        SearchActionButton.Content = LocalizationService.Get("Search.Button");
        SearchInstructionText.Text = LocalizationService.Get("Lists.Create.SearchInstruction");
        ListNameLabel.Text = LocalizationService.Get("Lists.Save.Name");
        SelectedApplicationsLabel.Text = LocalizationService.Get("Lists.Create.SelectedApplications");
        CreateListButton.Content = LocalizationService.Get("Lists.Create.CreateButton");
        BackButton.Content = LocalizationService.Get("Common.BackToDashboard");
        AutomationProperties.SetName(BackButton, LocalizationService.Get("Common.BackToDashboard"));
        AutomationProperties.SetName(SearchBoxHost, LocalizationService.Get("Search.Box.Name"));
        AutomationProperties.SetHelpText(SearchBoxHost, LocalizationService.Get("Search.Box.HelpText"));
        AutomationProperties.SetName(AddedList, LocalizationService.Get("Lists.Create.SelectedApplications"));
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

    private void CreateListPage_Loaded(object sender, RoutedEventArgs e)
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

    private void SearchActionButton_Click(object sender, RoutedEventArgs e)
    {
        RunSearch();
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

    private void Page_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        if (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            _returnToDashboard();
            return;
        }

        if (!ResultsList.IsKeyboardFocusWithin || ResultsList.SelectedItem is not SearchResultViewModel selected)
            return;

        if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            AddSelectedApplication(selected);
        }
    }

    private void RunSearch()
    {
        if (_catalogService.LoadState != CatalogLoadState.Ready)
        {
            MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Search.CatalogNotReady"), LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            _searchBox.Focus();
            return;
        }

        string query = _searchBox.Text.Trim();
        if (query.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Search.EnterQuery"), LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            _searchBox.Focus();
            return;
        }

        try
        {
            List<FastScoredEntry> searchResults = CatalogSearchBackend.SearchCatalog(query, 100);
            Results.Clear();
            foreach (FastScoredEntry scored in searchResults)
                Results.Add(new SearchResultViewModel(scored.Entry));

            if (Results.Count == 0)
            {
                ResultsList.SelectedIndex = -1;
                ResultsList.IsEnabled = false;
                MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Search.NoResults"), LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                _searchBox.Focus();
                return;
            }

            ResultsList.IsEnabled = true;
            ResultsList.SelectedIndex = 0;
            ResultsList.UpdateLayout();
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
        catch (Exception ex)
        {
            Results.Clear();
            ResultsList.SelectedIndex = -1;
            ResultsList.IsEnabled = false;
            MessageBox.Show(Window.GetWindow(this), $"{LocalizationService.Get("Search.Failed")}\n\n{ex.Message}", LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            _searchBox.Focus();
        }
    }

    private void AnnounceResultsInstruction()
    {
        if (UIElementAutomationPeer.CreatePeerForElement(ResultsList) is not AutomationPeer peer)
            peer = new ListBoxAutomationPeer(ResultsList);

        peer.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.ImportantMostRecent,
            LocalizationService.Get("Lists.Create.SearchInstruction"),
            "CreateListSearchResultsInstruction");
    }

    private void AddSelectedApplication(SearchResultViewModel selected)
    {
        if (AddedApplications.Any(x => string.Equals(x.Id, selected.Entry.Id, StringComparison.OrdinalIgnoreCase)))
        {
            Announce(LocalizationService.Format("Lists.Create.AlreadyAdded", selected.Name), "CreateListDuplicate");
            ReturnToSearch();
            return;
        }

        AddedApplications.Add(new ListApplicationItem(selected.Name, selected.Entry.Id));
        Announce(LocalizationService.Format("Lists.Create.Added", selected.Name, AddedApplications.Count), "CreateListAdded");
        ReturnToSearch();
    }

    private void ReturnToSearch()
    {
        Results.Clear();
        ResultsList.SelectedIndex = -1;
        ResultsList.IsEnabled = false;
        _searchBox.Clear();
        Dispatcher.BeginInvoke(new Action(() => _searchBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Announce(string text, string activityId)
    {
        if (UIElementAutomationPeer.CreatePeerForElement(this) is not AutomationPeer peer)
            peer = new FrameworkElementAutomationPeer(this);
        peer.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, activityId);
    }

    private void AddedList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (AddedApplications.Count == 0 || AddedList.SelectedIndex >= 0)
            return;

        AddedList.SelectedIndex = 0;
        FocusAddedIndex(0);
    }

    private void AddedList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // This is a plain navigation list, not a checklist. Space must not toggle
        // selection state or expose checkbox-like semantics to screen readers.
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Delete || AddedList.SelectedItem is not ListApplicationItem item)
            return;

        e.Handled = true;
        int oldIndex = AddedList.SelectedIndex;
        AddedApplications.Remove(item);
        Announce(LocalizationService.Format("Lists.Create.Removed", item.Name), "CreateListRemoved");

        if (AddedApplications.Count == 0)
        {
            ListNameBox.Focus();
            return;
        }

        int newIndex = Math.Min(oldIndex, AddedApplications.Count - 1);
        AddedList.SelectedIndex = newIndex;
        Dispatcher.BeginInvoke(new Action(() => FocusAddedIndex(newIndex)), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusAddedIndex(int index)
    {
        if (index < 0 || index >= AddedApplications.Count) return;
        AddedList.UpdateLayout();
        if (AddedList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
        {
            item.Focus();
            Keyboard.Focus(item);
            item.BringIntoView();
        }
        else
        {
            AddedList.Focus();
            Keyboard.Focus(AddedList);
        }
    }

    private void CreateListButton_Click(object sender, RoutedEventArgs e)
    {
        string listName = ListNameBox.Text.Trim();
        if (listName.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Lists.Save.NameRequired"), LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            ListNameBox.Focus();
            return;
        }

        if (AddedApplications.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), LocalizationService.Get("Lists.Save.Empty"), LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            _searchBox.Focus();
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.ListsDirectory);
            string fileName = MakeSafeFileName(listName) + ".json";
            string path = Path.Combine(AppPaths.ListsDirectory, fileName);

            if (File.Exists(path) && MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Lists.Save.Overwrite", fileName), LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                ListNameBox.Focus();
                return;
            }

            var document = new ListFileDocument(listName, AddedApplications.Select(x => new ListFileApp(x.Name, x.Id)).ToList());
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);

            MessageBox.Show(
                Window.GetWindow(this),
                LocalizationService.Format("Lists.Save.SuccessWithPath", listName, AppPaths.ListsDirectory),
                LocalizationService.Get("Lists.Create.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            ResetForNewList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), LocalizationService.Format("Lists.Save.Failed", ex.Message), LocalizationService.Get("Lists.Create.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            CreateListButton.Focus();
        }
    }

    private void ResetForNewList()
    {
        // Stay on Create List so the user can immediately create another list.
        ListNameBox.Clear();
        AddedApplications.Clear();
        Results.Clear();
        ResultsList.SelectedIndex = -1;
        ResultsList.IsEnabled = false;
        AddedList.SelectedIndex = -1;
        _searchBox.Clear();

        Dispatcher.BeginInvoke(new Action(() => _searchBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static string MakeSafeFileName(string name)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "List" : safe;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _returnToDashboard();
}

public sealed record ListApplicationItem(string Name, string Id)
{
    public override string ToString() => Name;
}
public sealed record ListFileDocument(string name, IReadOnlyList<ListFileApp> apps);
public sealed record ListFileApp(string name, string id);
