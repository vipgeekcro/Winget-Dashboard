using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using WingetDashboard.Services;

namespace WingetDashboard;

public partial class MainWindow : Window
{
    private bool _startupCheckStarted;
    private readonly CatalogService _catalogService = new();
    private readonly CatalogBuilderService _catalogBuilderService = new();
    private CatalogUpdateService? _catalogUpdateService;
    private System.Windows.Controls.Button? _dashboardReturnFocusButton;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
        Version? version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
            Title = $"Winget Dashboard {version.Major}.{version.Minor}.{version.Build}";
        _catalogUpdateService = new CatalogUpdateService(_catalogService, _catalogBuilderService);
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        DashboardTitleText.Text = LocalizationService.Get("Dashboard.Title");
        DashboardSubtitleText.Text = LocalizationService.Get("Dashboard.Subtitle");

        ApplyDashboardCard(SearchButton, SearchTitleText, SearchDescriptionText, "Dashboard.Search.Title", "Dashboard.Search.Description", 'S');
        ApplyDashboardCard(UpdatesButton, UpdatesTitleText, UpdatesDescriptionText, "Dashboard.Updates.Title", "Dashboard.Updates.Description", 'U');
        ApplyDashboardCard(InstalledApplicationsButton, InstalledTitleText, InstalledDescriptionText, "Dashboard.Installed.Title", "Dashboard.Installed.Description", 'A');
        ApplyDashboardCard(ListsButton, ListsTitleText, ListsDescriptionText, "Dashboard.Lists.Title", "Dashboard.Lists.Description", 'L');
        ApplyDashboardCard(CatalogButton, CatalogTitleText, CatalogDescriptionText, "Dashboard.Catalog.Title", "Dashboard.Catalog.Description", 'C');
        ApplyDashboardCard(SettingsButton, SettingsTitleText, SettingsDescriptionText, "Dashboard.Settings.Title", "Dashboard.Settings.Description", 'T');
    }

    private static void ApplyDashboardCard(System.Windows.Controls.Button button, System.Windows.Controls.AccessText title,
        System.Windows.Controls.TextBlock description, string titleKey, string descriptionKey, char accessKey)
    {
        string localizedTitle = LocalizationService.Get(titleKey);
        string localizedDescription = LocalizationService.Get(descriptionKey);
        title.Text = AddAccessKey(localizedTitle, accessKey);
        description.Text = localizedDescription;
        System.Windows.Automation.AutomationProperties.SetName(button, localizedTitle);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, localizedDescription);
        System.Windows.Automation.AutomationProperties.SetAccessKey(button, $"Alt+{accessKey}");
    }

    private static string AddAccessKey(string text, char accessKey)
    {
        int index = text.IndexOf(accessKey.ToString(), StringComparison.CurrentCultureIgnoreCase);
        return index >= 0 ? text.Insert(index, "_") : text;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(MainWindow_WndProc);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_hwndSource is not null)
            _hwndSource.RemoveHook(MainWindow_WndProc);
    }

    private static IntPtr MainWindow_WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_KEYMENU = 0xF100;

        // A lone Alt key is being translated by the window into SC_KEYMENU,
        // which opens the standard system menu. Suppress only that command.
        // Other system commands (including Alt+F4) and WPF access keys remain untouched.
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU)
        {
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SearchButton.Focus();

        if (_startupCheckStarted)
            return;

        _startupCheckStarted = true;

        bool wingetAvailable = await WingetService.TestCommandWorksAsync();
        if (!wingetAvailable)
        {
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Get("Startup.WingetUnavailable"),
                "Winget Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        CatalogStatus catalogStatus = await _catalogService.GetStatusAsync();
        if (catalogStatus == CatalogStatus.Available)
        {
            await LoadCatalogAsync();
            await CheckCatalogUpdateReminderAsync();
            return;
        }

        string prompt = catalogStatus == CatalogStatus.Missing
            ? LocalizationService.Get("Startup.CatalogMissing")
            : LocalizationService.Get("Startup.CatalogInvalid");

        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            prompt,
            "Winget Dashboard",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            SearchButton.Focus();
            return;
        }

        await BuildCatalogAsync(catalogStatus);
        SearchButton.Focus();
    }

    private async Task CheckCatalogUpdateReminderAsync()
    {
        AppSettings settings = AppSettingsService.Load();
        int intervalDays = settings.CatalogUpdateSchedule switch
        {
            "7days" => 7,
            "14days" => 14,
            "30days" => 30,
            _ => 0
        };

        if (intervalDays == 0)
            return;

        DateTimeOffset now = DateTimeOffset.Now;
        if (settings.CatalogUpdateReminderAfter is DateTimeOffset remindAfter && now < remindAfter)
            return;

        DateTimeOffset? generatedAt = await ReadCatalogGeneratedAtAsync();
        if (generatedAt is null || now < generatedAt.Value.ToLocalTime().AddDays(intervalDays))
            return;

        CatalogUpdateReminderWindow reminder = new() { Owner = this };
        bool? result = reminder.ShowDialog();
        if (result != true || !reminder.UpdateNow)
        {
            SaveCatalogReminderForTomorrow(settings);
            SearchButton.Focus();
            return;
        }

        CatalogBuildWindow progressWindow = new(
            _catalogUpdateService!.UpdateCatalogAsync,
            LocalizationService.Get("Catalog.Page.Updating"),
            LocalizationService.Get("Catalog.Page.UpdateWindowTitle"))
        {
            Owner = this
        };

        bool? updateResult = progressWindow.ShowDialog();
        if (updateResult == true)
        {
            settings.CatalogUpdateReminderAfter = null;
            AppSettingsService.Save(settings);
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Get("Catalog.Page.UpdateSuccess"),
                LocalizationService.Get("Catalog.Page.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            SaveCatalogReminderForTomorrow(settings);
            string error = progressWindow.OperationException?.Message ?? LocalizationService.Get("Common.UnknownError");
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Format("Catalog.Page.UpdateFailure", error),
                LocalizationService.Get("Catalog.Page.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        SearchButton.Focus();
    }

    private async Task<DateTimeOffset?> ReadCatalogGeneratedAtAsync()
    {
        try
        {
            using FileStream stream = new(_catalogService.CatalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument document = await JsonDocument.ParseAsync(stream);
            JsonElement metadata = document.RootElement.GetProperty("metadata");
            if (!metadata.TryGetProperty("generatedAtUtc", out JsonElement generated))
                return null;
            return DateTimeOffset.TryParse(generated.GetString(), out DateTimeOffset value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCatalogReminderForTomorrow(AppSettings settings)
    {
        settings.CatalogUpdateReminderAfter = new DateTimeOffset(DateTime.Today.AddDays(1));
        AppSettingsService.Save(settings);
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            await _catalogService.LoadAndPrepareAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"{LocalizationService.Get("Catalog.Load.Failure")}\n\n{ex.Message}",
                "Winget Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private Task BuildCatalogAsync(CatalogStatus previousStatus)
    {
        CatalogBuildWindow progressWindow = new(async () =>
        {
            if (previousStatus == CatalogStatus.Invalid)
                _catalogService.RemoveInvalidCatalog();

            await _catalogBuilderService.BuildCatalogAsync(_catalogService.CatalogPath);

            CatalogStatus finalStatus = await _catalogService.GetStatusAsync();
            if (finalStatus != CatalogStatus.Available)
                throw new InvalidOperationException("The catalog was created, but validation failed.");

            await _catalogService.LoadAndPrepareAsync(forceReload: true);
        })
        {
            Owner = this
        };

        bool? result = progressWindow.ShowDialog();

        if (result == true)
        {
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Get("Catalog.Build.Success"),
                "Winget Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            string error = progressWindow.OperationException?.Message ?? "Unknown error.";
            System.Windows.MessageBox.Show(
                this,
                $"{LocalizationService.Get("Catalog.Build.Failure")}\n\n{error}",
                "Winget Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        if (DashboardView.Visibility == Visibility.Visible &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Alt)
        {
            System.Windows.Controls.Button? target = key switch
            {
                System.Windows.Input.Key.S => SearchButton,
                System.Windows.Input.Key.U => UpdatesButton,
                System.Windows.Input.Key.A => InstalledApplicationsButton,
                System.Windows.Input.Key.L => ListsButton,
                System.Windows.Input.Key.C => CatalogButton,
                System.Windows.Input.Key.T => SettingsButton,
                _ => null
            };
            if (target is not null)
            {
                e.Handled = true;
                target.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                return;
            }
        }

        if (PageHost.Visibility != Visibility.Visible)
            return;
        if (key == System.Windows.Input.Key.Left &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) == System.Windows.Input.ModifierKeys.Alt)
        {
            e.Handled = true;
            ReturnToDashboard();
        }
    }

    private void ReturnToDashboard()
    {
        if (PageHost.Visibility != Visibility.Visible)
            return;

        PageHost.Content = null;
        PageHost.Visibility = Visibility.Collapsed;
        DashboardView.Visibility = Visibility.Visible;

        System.Windows.Controls.Button focusTarget = _dashboardReturnFocusButton ?? SearchButton;
        Dispatcher.BeginInvoke(new Action(() => focusTarget.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void DashboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string section)
            return;

        _dashboardReturnFocusButton = button;

        if (section == "Search Winget")
        {
            DashboardView.Visibility = Visibility.Collapsed;
            PageHost.Content = new SearchPage(_catalogService, ReturnToDashboard);
            PageHost.Visibility = Visibility.Visible;
            return;
        }

        if (section == "Catalog")
        {
            DashboardView.Visibility = Visibility.Collapsed;
            PageHost.Content = new CatalogPage(_catalogService, _catalogBuilderService, ReturnToDashboard);
            PageHost.Visibility = Visibility.Visible;
            return;
        }

        if (section == "Updates")
        {
            DashboardView.Visibility = Visibility.Collapsed;
            PageHost.Content = new UpdatesPage(ReturnToDashboard);
            PageHost.Visibility = Visibility.Visible;
            return;
        }

        if (section == "Installed Applications")
        {
            DashboardView.Visibility = Visibility.Collapsed;
            PageHost.Content = new InstalledApplicationsPage(ReturnToDashboard);
            PageHost.Visibility = Visibility.Visible;
            return;
        }

        if (section == "Lists")
        {
            DashboardView.Visibility = Visibility.Collapsed;
            PageHost.Content = new CreateListPage(_catalogService, ReturnToDashboard);
            PageHost.Visibility = Visibility.Visible;
            return;
        }

        if (section == "Settings")
        {
            DashboardView.Visibility = Visibility.Collapsed;
            PageHost.Content = new SettingsPage(ReturnToDashboard, ApplyLocalization);
            PageHost.Visibility = Visibility.Visible;
            return;
        }

        System.Windows.MessageBox.Show(
            LocalizationService.Format("Dashboard.NotImplemented", System.Windows.Automation.AutomationProperties.GetName(button)),
            "Winget Dashboard",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
