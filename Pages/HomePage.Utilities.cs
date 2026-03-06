using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using WindowsNotesApp.Services;

namespace WindowsNotesApp.Pages
{
    public sealed partial class HomePage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SetSelectionMode(bool isEnabled)
        {
            if (IsSelectionMode == isEnabled)
                return;

            IsSelectionMode = isEnabled;
            if (!IsSelectionMode)
                ClearSelectedTiles();

            OnPropertyChanged(nameof(IsSelectionMode));
        }

        private void ToggleTileSelection(HomeTile tile)
        {
            if (tile == null || tile.IsAddTile)
                return;

            tile.IsSelected = !tile.IsSelected;
        }

        private void ClearSelectedTiles()
        {
            foreach (var tile in HomeTiles)
            {
                if (!tile.IsAddTile && tile.IsSelected)
                    tile.IsSelected = false;
            }
        }

        public void ApplyLocalization()
        {
            foreach (var tile in HomeTiles)
                tile.RefreshDisplay();

            OnPropertyChanged(nameof(IsSelectionMode));
        }

        private async Task ExportTileAsync(HomeTile tile)
        {
            if (tile == null || tile.IsAddTile || string.IsNullOrWhiteSpace(tile.Path) || !File.Exists(tile.Path))
                return;

            var dialog = new SaveFileDialog
            {
                Filter = LocalizationService.Get("Home.PdfFilter"),
                Title = LocalizationService.Get("Home.ExportTitle"),
                FileName = Path.GetFileName(tile.Path),
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                File.Copy(tile.Path, dialog.FileName, true);
                if (Window.GetWindow(this) is MainWindow mw)
                    mw.ShowToast(LocalizationService.Get("Home.ExportSucceeded"), "\uEDE1");
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Format("Home.ExportFailed", ex.Message));
            }
        }

        private void OpenContainingFolder(HomeTile tile)
        {
            if (tile == null || tile.IsAddTile || string.IsNullOrWhiteSpace(tile.Path) || !File.Exists(tile.Path))
                return;

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{tile.Path}\"")
            {
                UseShellExecute = true
            });
        }
    }
}
