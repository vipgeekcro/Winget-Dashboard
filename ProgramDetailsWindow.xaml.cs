using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using WingetDashboard.Services;
using System.Windows.Input;
using Beta29.SearchBackend;

namespace WingetDashboard;

public partial class ProgramDetailsWindow : System.Windows.Window
{
    private readonly string _website;

    public ProgramDetailsWindow(FastSearchEntry entry)
    {
        InitializeComponent();
        ApplyLocalization();
        DataContext = new ProgramDetailsViewModel(entry);
        _website = ProgramDetailsViewModel.NormalizeDetailValue(CatalogObject.GetMember(entry.Package, "homepage"));
        OpenWebsiteButton.IsEnabled = _website != ProgramDetailsViewModel.NotSpecified;
    }

    private void ApplyLocalization()
    {
        Title = LocalizationService.Get("Details.Title");
        OpenWebsiteButton.Content = LocalizationService.Get("Details.OpenWebPage");
        CancelButton.Content = LocalizationService.Get("Common.Cancel");
    }

    private void ProgramDetailsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Keyboard.Focus(NameValue);
    }

    private void ProgramDetailsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OpenWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_website == ProgramDetailsViewModel.NotSpecified)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_website) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"{LocalizationService.Get("Details.WebsiteOpenFailed")}\n\n{ex.Message}",
                LocalizationService.Get("Details.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class ProgramDetailsViewModel
{
    internal static string NotSpecified => LocalizationService.Get("Details.NotSpecified");

    public string Name { get; }
    public string Version { get; }
    public string Publisher { get; }
    public string PackageId { get; }
    public string ShortDescription { get; }
    public string Description { get; }
    public string License { get; }
    public string Website { get; }

    public string NameDisplay => $"{LocalizationService.Get("Details.Name")}: {Name}";
    public string VersionDisplay => $"{LocalizationService.Get("Details.Version")}: {Version}";
    public string PublisherDisplay => $"{LocalizationService.Get("Details.Publisher")}: {Publisher}";
    public string PackageIdDisplay => $"{LocalizationService.Get("Details.PackageId")}: {PackageId}";
    public string ShortDescriptionDisplay => $"{LocalizationService.Get("Details.ShortDescription")}: {ShortDescription}";
    public string DescriptionDisplay => $"{LocalizationService.Get("Details.Description")}: {Description}";
    public string LicenseDisplay => $"{LocalizationService.Get("Details.License")}: {License}";
    public string WebsiteDisplay => $"{LocalizationService.Get("Details.Website")}: {Website}";

    public ProgramDetailsViewModel(FastSearchEntry entry)
    {
        Name = NormalizeDetailValue(entry.Name);
        Version = NormalizeDetailValue(entry.Version);
        Publisher = NormalizeDetailValue(entry.Publisher);
        PackageId = NormalizeDetailValue(entry.Id);
        ShortDescription = NormalizeDetailValue(entry.ShortDescription);
        Description = NormalizeDetailValue(CatalogObject.GetMember(entry.Package, "description"));
        License = NormalizeDetailValue(CatalogObject.GetMember(entry.Package, "license"));
        Website = NormalizeDetailValue(CatalogObject.GetMember(entry.Package, "homepage"));
    }

    internal static string NormalizeDetailValue(object? value)
    {
        if (value is null)
            return NotSpecified;

        string text = value.ToString() ?? string.Empty;
        text = Regex.Replace(text, "[\\r\\n\\t]+", " ");
        text = Regex.Replace(text, "\\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? NotSpecified : text;
    }
}
