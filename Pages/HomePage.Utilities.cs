using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Caelum.Services;

namespace Caelum.Pages
{
    public sealed partial class HomePage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public int SelectedTileCount => HomeTiles.Count(tile => !tile.IsAddTile && tile.IsSelected);

        public bool HasSelectedTiles => SelectedTileCount > 0;

        public bool CanSelectAllTiles => GetVisibleFileTiles().Any(tile => !tile.IsSelected);

        public string SelectionSummary => HasSelectedTiles
            ? LocalizationService.Format("Home.Selection.Count", SelectedTileCount)
            : LocalizationService.Get("Home.Selection.None");

        public string SelectionHint => LocalizationService.Get("Home.Selection.Hint");

        public string SelectionClearText => LocalizationService.Get("Home.Selection.Clear");

        public string SelectionDoneText => LocalizationService.Get("Home.Selection.Done");

        public string SelectionRemoveText => LocalizationService.Get("Home.Selection.Remove");

        public string SelectionSelectAllText => LocalizationService.Get("Home.Selection.SelectAll");

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void RefreshSelectionState()
        {
            OnPropertyChanged(nameof(IsSelectionMode));
            OnPropertyChanged(nameof(SelectedTileCount));
            OnPropertyChanged(nameof(HasSelectedTiles));
            OnPropertyChanged(nameof(CanSelectAllTiles));
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(SelectionHint));
            OnPropertyChanged(nameof(SelectionClearText));
            OnPropertyChanged(nameof(SelectionDoneText));
            OnPropertyChanged(nameof(SelectionRemoveText));
            OnPropertyChanged(nameof(SelectionSelectAllText));
        }

        private IEnumerable<HomeTile> GetVisibleFileTiles()
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(HomeTiles);
            return view.Cast<object>()
                .OfType<HomeTile>()
                .Where(tile => !tile.IsAddTile);
        }

        private void SetSelectionMode(bool isEnabled)
        {
            if (IsSelectionMode == isEnabled)
                return;

            IsSelectionMode = isEnabled;
            if (!IsSelectionMode)
                ClearSelectedTiles(refreshState: false);

            RefreshSelectionState();
        }

        private void ToggleTileSelection(HomeTile tile)
        {
            if (tile == null || tile.IsAddTile)
                return;

            tile.IsSelected = !tile.IsSelected;
            RefreshSelectionState();
        }

        private void ClearSelectedTiles(bool refreshState = true)
        {
            foreach (var tile in HomeTiles)
            {
                if (!tile.IsAddTile && tile.IsSelected)
                    tile.IsSelected = false;
            }

            if (refreshState)
                RefreshSelectionState();
        }

        public void ApplyLocalization()
        {
            if (HomeTitleTextBlock != null)
                HomeTitleTextBlock.Text = LocalizationService.Get("Home.Title");
            if (HomeSubtitleTextBlock != null)
                HomeSubtitleTextBlock.Text = LocalizationService.Get("Home.Subtitle");

            foreach (var tile in HomeTiles)
                tile.RefreshDisplay();

            RefreshSelectionState();
        }

        private void SelectAllVisibleTiles()
        {
            foreach (var tile in GetVisibleFileTiles())
            {
                tile.IsSelected = true;
            }

            RefreshSelectionState();
        }

        private async Task RemoveSelectedTilesAsync()
        {
            var selectedTiles = HomeTiles
                .Where(tile => !tile.IsAddTile && tile.IsSelected)
                .ToList();

            if (selectedTiles.Count == 0)
                return;

            foreach (var tile in selectedTiles)
            {
                HomeTiles.Remove(tile);
                if (!string.IsNullOrWhiteSpace(tile.Path))
                    RecentFilesService.Remove(tile.Path);
            }

            RefreshSelectionState();

            if (Window.GetWindow(this) is MainWindow mw)
                mw.ShowToast(LocalizationService.Format("Home.Selection.RemovedCount", selectedTiles.Count), "\uE74D");

            await Task.CompletedTask;
        }

        private void SelectAllSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            SelectAllVisibleTiles();
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelectedTiles();
        }

        private async void RemoveSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            await RemoveSelectedTilesAsync();
        }

        private void DoneSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelectionMode(false);
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
