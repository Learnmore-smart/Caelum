using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

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
            this.DataContext = this;
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

        private void TilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void AddTile_Click(object sender, RoutedEventArgs e)
        {
            _ = PickAndOpenPdfAsync();
        }

        private void FileTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is HomeTile tile)
            {
                _ = OpenRecentTileAsync(tile);
            }
        }

        private async Task PickAndOpenPdfAsync()
        {
            var picker = new OpenFileDialog
            {
                Filter = "PDF Files (*.pdf)|*.pdf",
                Title = "Open PDF File"
            };

            if (picker.ShowDialog() == true)
            {
                var filePath = picker.FileName;
                await AddToRecentFilesAsync(filePath);
                NavigationService?.Navigate(new EditorPage(filePath));
            }
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
                if (!System.IO.File.Exists(tile.Path))
                {
                    await RemoveMissingRecentTileAsync(tile);
                    await ShowDialogAsync("Error", "File not found.");
                    return;
                }
                NavigationService?.Navigate(new EditorPage(tile.Path));
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
            var window = Window.GetWindow(this);
            if (window != null)
            {
                var dialog = new Window
                {
                    Title = title,
                    Width = 300,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = window,
                    ResizeMode = ResizeMode.NoResize
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var textBlock = new TextBlock
                {
                    Text = content,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20)
                };
                Grid.SetRow(textBlock, 0);
                grid.Children.Add(textBlock);

                var button = new Button
                {
                    Content = "OK",
                    Width = 80,
                    Height = 28,
                    Margin = new Thickness(0, 0, 0, 15)
                };
                button.Click += (s, e) => dialog.Close();
                Grid.SetRow(button, 1);
                grid.Children.Add(button);

                dialog.Content = grid;
                dialog.ShowDialog();
            }
            await Task.CompletedTask;
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
