using System.Linq;
using WindowsNotesApp.Models;
using WindowsNotesApp.Pages;
using WindowsNotesApp.Services;

namespace WindowsNotesApp
{
    public partial class MainWindow
    {
        private string GetHomeTabTitle()
        {
            return LocalizationService.Get("Main.HomeTabTitle");
        }

        private void ApplyLocalization()
        {
            SearchPlaceholder.Text = LocalizationService.Get("Main.SearchPlaceholder");
            SelectButtonLabel.Text = LocalizationService.Get("Main.Select");
            SortByNameMenuItem.Header = LocalizationService.Get("Main.SortByName");
            SortByDateMenuItem.Header = LocalizationService.Get("Main.SortByDate");
            SettingsMenuItem.Header = LocalizationService.Get("Main.Settings");
            AboutMenuItem.Header = LocalizationService.Get("Main.About");
            NewTabButton.ToolTip = LocalizationService.Get("Main.NewTabTooltip");
            RefreshOpenContentLocalization();
            RebuildTabBar();
        }

        private void ApplySettings(AppSettings settings)
        {
            var savedSettings = AppSettingsService.Save(settings);
            LocalizationService.ApplyLanguage(savedSettings.Language);
            ApplyLocalization();
        }

        private void RefreshOpenContentLocalization()
        {
            foreach (var tab in _tabs.Where(tab => tab != null))
            {
                if (tab.Frame?.Content is HomePage home)
                {
                    tab.Title = GetHomeTabTitle();
                    home.ApplyLocalization();
                }
                else if (tab.Frame?.Content is EditorPage editor)
                {
                    editor.ApplyLocalization();
                }
            }
        }

        private void OpenSettingsDialog()
        {
            var dialog = new SettingsWindow(AppSettingsService.Load())
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                ApplySettings(dialog.SelectedSettings ?? dialog.GetSelectedSettings());
                ShowToast(LocalizationService.Get("Main.SettingsSaved"), "\uE713");
            }
        }
    }
}
