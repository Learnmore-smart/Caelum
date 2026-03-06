using System.Windows;
using WindowsNotesApp.Models;
using WindowsNotesApp.Services;

namespace WindowsNotesApp
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(AppSettings currentSettings)
        {
            InitializeComponent();
            MouseLeftButtonDown += (sender, args) => DragMove();

            LanguageComboBox.ItemsSource = LocalizationService.GetLanguageOptions();
            LanguageComboBox.SelectedValue = currentSettings.Language;

            ApplyLocalization();
        }

        public AppSettings SelectedSettings { get; private set; }

        public void ApplyLocalization()
        {
            TitleTextBlock.Text = LocalizationService.Get("Settings.Title");
            SubtitleTextBlock.Text = LocalizationService.Get("Settings.Subtitle");
            LanguageLabelTextBlock.Text = LocalizationService.Get("Settings.LanguageLabel");
            LanguageHintTextBlock.Text = LocalizationService.Get("Settings.LanguageHint");
            UtilityLabelTextBlock.Text = LocalizationService.Get("Settings.UtilityLabel");
            UtilityHintTextBlock.Text = LocalizationService.Get("Settings.UtilityHint");
            CancelButton.Content = LocalizationService.Get("Common.Cancel");
            SaveButton.Content = LocalizationService.Get("Common.Save");
            Title = LocalizationService.Get("Settings.Title");
        }

        public AppSettings GetSelectedSettings()
        {
            var selectedLanguage = LanguageComboBox.SelectedValue is AppLanguage language
                ? language
                : AppLanguage.English;

            return new AppSettings
            {
                Language = selectedLanguage
            };
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedSettings = GetSelectedSettings();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}

