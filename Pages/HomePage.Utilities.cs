using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Caelum.Services;

namespace Caelum.Pages
{
    public sealed partial class HomePage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public int SelectedTileCount => HomeTiles.Count(tile => tile.IsFile && tile.IsSelected);

        public bool HasSelectedTiles => SelectedTileCount > 0;

        public bool CanSelectAllTiles => GetVisibleFileTiles().Any(tile => !tile.IsSelected);

        public string SelectionSummary => HasSelectedTiles
            ? LocalizationService.Format("Home.Selection.Count", SelectedTileCount)
            : LocalizationService.Get("Home.Selection.None");

        public string SelectionHint => LocalizationService.Get("Home.Selection.Hint");

        public string SelectionClearText => LocalizationService.Get("Home.Selection.Clear");

        public string SelectionDoneText => LocalizationService.Get("Home.Selection.Done");

        public string SelectionMoveText => LocalizationService.Get("Home.Selection.Move");

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
            OnPropertyChanged(nameof(SelectionMoveText));
            OnPropertyChanged(nameof(SelectionRemoveText));
            OnPropertyChanged(nameof(SelectionSelectAllText));
            GetMainWindow()?.RefreshSelectButtonVisualState();
        }

        private IEnumerable<HomeTile> GetVisibleFileTiles()
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(HomeTiles);
            return view.Cast<object>()
                .OfType<HomeTile>()
                .Where(tile => tile.IsFile);
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
            if (tile == null || !tile.IsFile)
                return;

            tile.IsSelected = !tile.IsSelected;
            RefreshSelectionState();
        }

        private void ClearSelectedTiles(bool refreshState = true)
        {
            foreach (var tile in HomeTiles)
            {
                if (tile.IsFile && tile.IsSelected)
                    tile.IsSelected = false;
            }

            if (refreshState)
                RefreshSelectionState();
        }

        public void ApplyLocalization()
        {
            foreach (var tile in HomeTiles)
                tile.RefreshDisplay();

            UpdateHeaderText();
            RefreshSelectionState();
        }

        public void ToggleSelectionMode()
        {
            SetSelectionMode(!IsSelectionMode);
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
                .Where(tile => tile.IsFile && tile.IsSelected)
                .ToList();

            if (selectedTiles.Count == 0)
                return;

            foreach (var tile in selectedTiles)
            {
                if (!string.IsNullOrWhiteSpace(tile.Path))
                    RecentFilesService.Remove(tile.Path);
            }

            await RefreshCurrentFolderAsync();
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

        private void MoveSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedTiles = HomeTiles
                .Where(candidate => candidate.IsFile && candidate.IsSelected)
                .ToList();

            if (selectedTiles.Count == 0)
                return;

            var contextMenu = new System.Windows.Controls.ContextMenu();

            if (IsInsideFolder)
            {
                var rootMenuItem = new System.Windows.Controls.MenuItem { Header = LocalizationService.Get("Home.LibraryRoot") };
                rootMenuItem.Click += async (s, args) =>
                {
                    foreach (var tile in selectedTiles) RecentFilesService.MoveToFolder(tile.Path, null);
                    await RefreshCurrentFolderAsync();
                    ClearSelectedTiles();
                };
                contextMenu.Items.Add(rootMenuItem);

                var currentFolder = RecentFilesService.GetFolder(_currentFolderId);
                if (currentFolder != null && !string.IsNullOrWhiteSpace(currentFolder.ParentFolderId))
                {
                    var parentFolder = RecentFilesService.GetFolder(currentFolder.ParentFolderId);
                    if (parentFolder != null)
                    {
                        var parentMenuItem = new System.Windows.Controls.MenuItem { Header = parentFolder.DisplayName };
                        parentMenuItem.Click += async (s, args) =>
                        {
                            foreach (var tile in selectedTiles) RecentFilesService.MoveToFolder(tile.Path, parentFolder.Id);
                            await RefreshCurrentFolderAsync();
                            ClearSelectedTiles();
                        };
                        contextMenu.Items.Add(parentMenuItem);
                    }
                }

                contextMenu.Items.Add(new System.Windows.Controls.Separator());
            }

            var folderTiles = HomeTiles.Where(t => t.IsFolder).ToList();
            foreach (var folder in folderTiles)
            {
                var menuItem = new System.Windows.Controls.MenuItem { Header = folder.FileName };
                menuItem.Click += async (s, args) =>
                {
                    foreach (var tile in selectedTiles) RecentFilesService.MoveToFolder(tile.Path, folder.Id);
                    await RefreshCurrentFolderAsync();
                    ClearSelectedTiles();
                };
                contextMenu.Items.Add(menuItem);
            }

            if (contextMenu.Items.Count == 0 || (contextMenu.Items.Count == 1 && contextMenu.Items[0] is System.Windows.Controls.Separator))
            {
                contextMenu.Items.Clear();
                contextMenu.Items.Add(new System.Windows.Controls.MenuItem { Header = "No folders available", IsEnabled = false });
            }

            contextMenu.PlacementTarget = sender as UIElement;
            contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            contextMenu.IsOpen = true;
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
