using System.Windows;
using WingetDashboard.Services;

namespace WingetDashboard;

public partial class CatalogUpdateReminderWindow : Window
{
    public bool UpdateNow { get; private set; }

    public CatalogUpdateReminderWindow()
    {
        InitializeComponent();
        Title = LocalizationService.Get("Catalog.Reminder.Title");
        MessageText.Text = LocalizationService.Get("Catalog.Reminder.Message");
        UpdateNowButton.Content = LocalizationService.Get("Catalog.Reminder.UpdateNow");
        RemindTomorrowButton.Content = LocalizationService.Get("Catalog.Reminder.RemindTomorrow");
        System.Windows.Automation.AutomationProperties.SetName(UpdateNowButton, LocalizationService.Get("Catalog.Reminder.UpdateNow"));
        System.Windows.Automation.AutomationProperties.SetName(RemindTomorrowButton, LocalizationService.Get("Catalog.Reminder.RemindTomorrow"));
        Loaded += (_, _) => UpdateNowButton.Focus();
    }

    private void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateNow = true;
        DialogResult = true;
    }

    private void RemindTomorrowButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateNow = false;
        DialogResult = false;
    }
}
