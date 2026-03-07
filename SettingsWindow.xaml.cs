using System.Windows;
using System.Windows.Controls;
using Caelum.Models;
using Caelum.Services;

namespace Caelum
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _originalSettings;

        public SettingsWindow(AppSettings currentSettings)
        {
            _originalSettings = new AppSettings
            {
                Language = currentSettings.Language
            };

            InitializeComponent();
            MouseLeftButtonDown += (sender, args) => DragMove();
            LanguageComboBox.SelectionChanged += LanguageComboBox_SelectionChanged;

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

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
                return;

            var previewSettings = GetSelectedSettings();
            LocalizationService.ApplyLanguage(previewSettings.Language);
            ApplyLocalization();

            if (Owner is MainWindow mainWindow)
                mainWindow.PreviewSettings(previewSettings);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedSettings = GetSelectedSettings();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (Owner is MainWindow mainWindow)
                mainWindow.PreviewSettings(_originalSettings);
            DialogResult = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (Owner is MainWindow mainWindow)
                mainWindow.PreviewSettings(_originalSettings);
            DialogResult = false;
        }
    }
}
