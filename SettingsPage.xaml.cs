using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using WingetDashboard.Services;

namespace WingetDashboard;

public partial class SettingsPage : System.Windows.Controls.UserControl
{
    private sealed record CatalogScheduleChoice(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private readonly Action _backToDashboard;
    private readonly Action _languageChanged;
    private bool _initializing;

    public SettingsPage(Action backToDashboard, Action languageChanged)
    {
        _backToDashboard = backToDashboard;
        _languageChanged = languageChanged;
        InitializeComponent();
        ApplyLocalizationAndChoices();
        Loaded += (_, _) => LanguageComboBox.Focus();
    }

    private void ApplyLocalizationAndChoices()
    {
        _initializing = true;
        AppSettings settings = AppSettingsService.Load();

        var choices = LocalizationService.GetLanguageChoices();
        LanguageComboBox.ItemsSource = choices;
        LanguageComboBox.DisplayMemberPath = nameof(LanguageChoice.DisplayName);
        LanguageComboBox.SelectedItem = choices.FirstOrDefault(choice => choice.Code == settings.Language) ?? choices[0];

        List<CatalogScheduleChoice> schedules =
        [
            new("never", LocalizationService.Get("Settings.CatalogUpdate.Never")),
            new("7days", LocalizationService.Get("Settings.CatalogUpdate.Every7Days")),
            new("14days", LocalizationService.Get("Settings.CatalogUpdate.Every14Days")),
            new("30days", LocalizationService.Get("Settings.CatalogUpdate.EveryMonth"))
        ];
        CatalogUpdateComboBox.ItemsSource = schedules;
        CatalogUpdateComboBox.DisplayMemberPath = nameof(CatalogScheduleChoice.DisplayName);
        CatalogUpdateComboBox.SelectedItem = schedules.FirstOrDefault(x => x.Code == settings.CatalogUpdateSchedule) ?? schedules[0];

        BackButton.Content = LocalizationService.Get("Common.BackToDashboard");
        TitleText.Text = LocalizationService.Get("Settings.Title");
        LanguageLabel.Text = LocalizationService.Get("Settings.ChooseLanguage");
        CatalogUpdateLabel.Text = LocalizationService.Get("Settings.CatalogUpdate.Label");
        ResetDefaultsButton.Content = LocalizationService.Get("Settings.ResetDefaults");
        SaveSettingsButton.Content = LocalizationService.Get("Settings.Save");
        AutomationProperties.SetName(BackButton, LocalizationService.Get("Common.BackToDashboard"));
        AutomationProperties.SetName(LanguageComboBox, LocalizationService.Get("Settings.ChooseLanguage"));
        AutomationProperties.SetName(CatalogUpdateComboBox, LocalizationService.Get("Settings.CatalogUpdate.Label"));
        AutomationProperties.SetName(ResetDefaultsButton, LocalizationService.Get("Settings.ResetDefaults"));
        AutomationProperties.SetName(SaveSettingsButton, LocalizationService.Get("Settings.Save"));
        _initializing = false;
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing ||
            LanguageComboBox.SelectedItem is not LanguageChoice language ||
            CatalogUpdateComboBox.SelectedItem is not CatalogScheduleChoice schedule)
            return;

        AppSettings settings = AppSettingsService.Load();
        settings.Language = language.Code;
        settings.CatalogUpdateSchedule = schedule.Code;
        if (schedule.Code == "never")
            settings.CatalogUpdateReminderAfter = null;
        AppSettingsService.Save(settings);

        LocalizationService.SetLanguage(settings.Language, save: false);
        _languageChanged();
        ApplyLocalizationAndChoices();
        StatusText.Text = LocalizationService.Get("Settings.Saved");
        SaveSettingsButton.Focus();
    }

    private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            LocalizationService.Get("Settings.ResetConfirm"),
            LocalizationService.Get("Settings.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            ResetDefaultsButton.Focus();
            return;
        }

        AppSettings settings = new();
        AppSettingsService.Save(settings);
        LocalizationService.SetLanguage(settings.Language, save: false);
        _languageChanged();
        ApplyLocalizationAndChoices();
        StatusText.Text = LocalizationService.Get("Settings.ResetComplete");
        ResetDefaultsButton.Focus();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _backToDashboard();
}
