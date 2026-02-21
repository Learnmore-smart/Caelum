using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WindowsNotesApp.Pages
{
    public sealed partial class HomePage : Page
    {
        public ObservableCollection<HomeTile> HomeTiles { get; } = new ObservableCollection<HomeTile>();
        private readonly SemaphoreSlim _recentFilesIoLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _recentFilesLoadLock = new SemaphoreSlim(1, 1);
        private bool _recentFilesLoaded;

        public HomePage()
        {
            this.InitializeComponent();
            HomeTiles.Add(HomeTile.CreateAddTile());
            Loaded += HomePage_Loaded;
        }

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            await EnsureRecentFilesLoadedAsync();
        }

        private async Task EnsureRecentFilesLoadedAsync()
        {
            if (_recentFilesLoaded)
            {
                return;
            }

            await _recentFilesLoadLock.WaitAsync();
            try
            {
                if (_recentFilesLoaded)
                {
                    return;
                }

                await LoadRecentFilesAsync();
                _recentFilesLoaded = true;
            }
            finally
            {
                _recentFilesLoadLock.Release();
            }
        }

        private string GetSettingsFilePath()
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsNotesApp");
            System.IO.Directory.CreateDirectory(folder);
            return System.IO.Path.Combine(folder, "recent_files.txt");
        }

        private async Task LoadRecentFilesAsync()
        {
            try
            {
                var path = GetSettingsFilePath();
                if (System.IO.File.Exists(path))
                {
                    string recentFilesStr;
                    await _recentFilesIoLock.WaitAsync();
                    try
                    {
                        recentFilesStr = await System.IO.File.ReadAllTextAsync(path);
                    }
                    finally
                    {
                        _recentFilesIoLock.Release();
                    }

                    if (!string.IsNullOrEmpty(recentFilesStr))
                    {
                        bool changed = false;
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var files = recentFilesStr.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var file in files)
                        {
                            var trimmed = file.Trim();
                            if (string.IsNullOrWhiteSpace(trimmed))
                            {
                                changed = true;
                                continue;
                            }

                            if (!System.IO.File.Exists(trimmed))
                            {
                                changed = true;
                                continue;
                            }

                            if (!string.Equals(System.IO.Path.GetExtension(trimmed), ".pdf", StringComparison.OrdinalIgnoreCase))
                            {
                                changed = true;
                                continue;
                            }

                            if (!seen.Add(trimmed))
                            {
                                changed = true;
                                continue;
                            }

                            HomeTiles.Add(HomeTile.CreateFileTile(trimmed));
                        }

                        if (changed)
                        {
                            await SaveRecentFilesAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading recent files: {ex.Message}");
            }
        }

        private async Task SaveRecentFilesAsync()
        {
            try
            {
                var path = GetSettingsFilePath();
                var paths = new List<string>();
                foreach (var item in HomeTiles)
                {
                    if (!item.IsAddTile && !string.IsNullOrWhiteSpace(item.Path))
                    {
                        paths.Add(item.Path);
                    }
                }

                var contents = string.Join("|", paths);
                await _recentFilesIoLock.WaitAsync();
                try
                {
                    await System.IO.File.WriteAllTextAsync(path, contents);
                }
                finally
                {
                    _recentFilesIoLock.Release();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving recent files: {ex.Message}");
            }
        }

        private async void TilesGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not HomeTile tile)
            {
                return;
            }

            if (tile.IsAddTile)
            {
                await PickAndOpenPdfAsync();
                return;
            }

            await OpenRecentTileAsync(tile);
        }

        private async Task PickAndOpenPdfAsync()
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".pdf");

            var window = (Application.Current as App)?.m_window;
            if (window == null)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(window);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            await AddToRecentFilesAsync(file.Path);
            Frame.Navigate(typeof(EditorPage), file);
        }

        private async Task AddToRecentFilesAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            for (int i = HomeTiles.Count - 1; i >= 0; i--)
            {
                var item = HomeTiles[i];
                if (!item.IsAddTile && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    HomeTiles.RemoveAt(i);
                }
            }

            var insertIndex = Math.Min(1, HomeTiles.Count);
            HomeTiles.Insert(insertIndex, HomeTile.CreateFileTile(path));
            await SaveRecentFilesAsync();
        }

        private async Task OpenRecentTileAsync(HomeTile tile)
        {
            if (tile.IsAddTile || string.IsNullOrWhiteSpace(tile.Path))
            {
                return;
            }

            if (!string.Equals(System.IO.Path.GetExtension(tile.Path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await RemoveMissingRecentTileAsync(tile);
                await ShowDialogAsync("Error", "Unsupported file type.");
                return;
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(tile.Path);
                Frame.Navigate(typeof(EditorPage), file);
            }
            catch (Exception)
            {
                await RemoveMissingRecentTileAsync(tile);
                await ShowDialogAsync("Error", "File not found or access denied.");
            }
        }

        private async Task RemoveMissingRecentTileAsync(HomeTile tile)
        {
            for (int i = 0; i < HomeTiles.Count; i++)
            {
                var item = HomeTiles[i];
                if (!item.IsAddTile && string.Equals(item.Path, tile.Path, StringComparison.OrdinalIgnoreCase))
                {
                    HomeTiles.RemoveAt(i);
                    await SaveRecentFilesAsync();
                    return;
                }
            }
        }

        private async Task ShowDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    public sealed class HomeTile
    {
        public bool IsAddTile { get; private set; }

        public string Path { get; private set; }

        public string FileName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Path))
                {
                    return string.Empty;
                }

                return System.IO.Path.GetFileName(Path);
            }
        }

        public static HomeTile CreateAddTile()
        {
            return new HomeTile { IsAddTile = true, Path = string.Empty };
        }

        public static HomeTile CreateFileTile(string path)
        {
            return new HomeTile { IsAddTile = false, Path = path ?? string.Empty };
        }
    }
}
