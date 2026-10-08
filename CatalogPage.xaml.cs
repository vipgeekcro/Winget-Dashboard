using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using WingetDashboard.Services;

namespace WingetDashboard;

public partial class CatalogPage : System.Windows.Controls.UserControl
{
    private readonly CatalogService _catalogService;
    private readonly CatalogBuilderService _catalogBuilderService;
    private readonly Action _returnToDashboard;
    private readonly CatalogUpdateService _catalogUpdateService;

    public CatalogPage(CatalogService catalogService, CatalogBuilderService catalogBuilderService, Action returnToDashboard)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _catalogBuilderService = catalogBuilderService ?? throw new ArgumentNullException(nameof(catalogBuilderService));
        _returnToDashboard = returnToDashboard ?? throw new ArgumentNullException(nameof(returnToDashboard));
        _catalogUpdateService = new CatalogUpdateService(_catalogService, _catalogBuilderService);

        InitializeComponent();
        ApplyLocalization();
        Loaded += CatalogPage_Loaded;
    }

    private void ApplyLocalization()
    {
        BackButton.Content = LocalizationService.Get("Common.BackToDashboard");
        System.Windows.Automation.AutomationProperties.SetName(BackButton, LocalizationService.Get("Common.BackToDashboard"));
        TitleText.Text = LocalizationService.Get("Catalog.Page.Title");
        UpdateCatalogButton.Content = LocalizationService.Get("Catalog.Page.UpdateButton");
        System.Windows.Automation.AutomationProperties.SetName(UpdateCatalogButton, LocalizationService.Get("Catalog.Page.UpdateButton"));
    }

    private async void CatalogPage_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshInformationAsync();
        StatusText.Focus();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _returnToDashboard();

    private async void UpdateCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            LocalizationService.Get("Catalog.Page.ConfirmUpdate"),
            LocalizationService.Get("Catalog.Page.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            UpdateCatalogButton.Focus();
            return;
        }

        CatalogBuildWindow progressWindow = new(
            _catalogUpdateService.UpdateCatalogAsync,
            LocalizationService.Get("Catalog.Page.Updating"),
            LocalizationService.Get("Catalog.Page.UpdateWindowTitle"))
        {
            Owner = Window.GetWindow(this)
        };

        bool? result = progressWindow.ShowDialog();

        if (result == true)
        {
            await RefreshInformationAsync();
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                LocalizationService.Get("Catalog.Page.UpdateSuccess"),
                LocalizationService.Get("Catalog.Page.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            string error = progressWindow.OperationException?.Message ?? "Unknown error.";
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                LocalizationService.Format("Catalog.Page.UpdateFailure", error),
                LocalizationService.Get("Catalog.Page.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        UpdateCatalogButton.Focus();
    }

    private async Task RefreshInformationAsync()
    {
        CatalogStatus status = await _catalogService.GetStatusAsync();
        if (status != CatalogStatus.Available)
        {
            StatusText.Text = status == CatalogStatus.Missing ? LocalizationService.Get("Catalog.Status.NotCreated") : LocalizationService.Get("Catalog.Status.Invalid");
            UpdatedText.Text = LocalizationService.Get("Catalog.LastUpdated.NotAvailable");
            PackagesText.Text = LocalizationService.Get("Catalog.Packages.NotAvailable");
            return;
        }

        try
        {
            using FileStream stream = new(_catalogService.CatalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument document = await JsonDocument.ParseAsync(stream);
            JsonElement metadata = document.RootElement.GetProperty("metadata");

            int packageCount = metadata.GetProperty("packageCount").GetInt32();
            string? generatedAt = metadata.TryGetProperty("generatedAtUtc", out JsonElement generated)
                ? generated.GetString()
                : null;

            string updated = LocalizationService.Get("Details.NotAvailable");
            if (DateTimeOffset.TryParse(generatedAt, out DateTimeOffset generatedUtc))
                updated = generatedUtc.ToLocalTime().ToString("g");

            StatusText.Text = LocalizationService.Get("Catalog.Status.Ready");
            UpdatedText.Text = LocalizationService.Format("Catalog.LastUpdated.Value", updated);
            PackagesText.Text = LocalizationService.Format("Catalog.Packages.Value", packageCount.ToString("N0"));
        }
        catch
        {
            StatusText.Text = LocalizationService.Get("Catalog.Status.Invalid");
            UpdatedText.Text = LocalizationService.Get("Catalog.LastUpdated.NotAvailable");
            PackagesText.Text = LocalizationService.Get("Catalog.Packages.NotAvailable");
        }
    }
}
