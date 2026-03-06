using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WindowsNotesApp.Services;

namespace WindowsNotesApp.Pages
{
    public sealed partial class HomePage : Page
    {
        public ObservableCollection<HomeTile> HomeTiles { get; } = new ObservableCollection<HomeTile>();
        private bool _recentFilesLoaded;

        public HomePage()
        {
            this.InitializeComponent();
            this.DataContext = this;
            HomeTiles.Add(HomeTile.CreateAddTile());
            ApplyLocalization();
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

            await LoadRecentFilesAsync();
            _recentFilesLoaded = true;
        }

        private Task LoadRecentFilesAsync()
        {
            try
            {
                foreach (var entry in RecentFilesService.GetRecentEntries())
                {
                    if (!string.Equals(System.IO.Path.GetExtension(entry.Path), ".pdf", StringComparison.OrdinalIgnoreCase))
                        continue;

                    HomeTiles.Add(HomeTile.CreateFileTile(entry));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading recent files: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public bool IsSelectionMode { get; private set; }

        public void ToggleSelectionMode()
        {
            SetSelectionMode(!IsSelectionMode);
        }

        public void Filter(string query)
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(HomeTiles);
            if (string.IsNullOrWhiteSpace(query))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = item =>
                {
                    if (item is HomeTile tile)
                    {
                        if (tile.IsAddTile) return true;
                        return tile.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                    }
                    return false;
                };
            }
        }

        public void SortByName()
        {
            var view = (System.Windows.Data.CollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(HomeTiles);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription("IsAddTile", ListSortDirection.Descending)); // Add tile always first
            view.SortDescriptions.Add(new SortDescription("FileName", ListSortDirection.Ascending));
        }

        public void SortByDate()
        {
            var view = (System.Windows.Data.CollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(HomeTiles);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription("IsAddTile", ListSortDirection.Descending)); // Add tile always first
            view.SortDescriptions.Add(new SortDescription("LastModified", ListSortDirection.Descending));
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
                if (IsSelectionMode)
                {
                    ToggleTileSelection(tile);
                    return;
                }

                _ = OpenRecentTileAsync(tile);
            }
        }

        private void TileMenu_Click(object sender, RoutedEventArgs e)
        {
            // Legacy - no longer used (caret removed)
        }

        private void FileTile_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Grid grid && grid.Tag is HomeTile tile && !tile.IsAddTile)
            {
                var menu = new ContextMenu();
                menu.Style = BuildContextMenuStyle();

                var viewItem = CreateMenuItem(LocalizationService.Get("Home.Context.Open"), "\uE7C3");
                viewItem.Click += (s, ev) => _ = OpenRecentTileAsync(tile);
                menu.Items.Add(viewItem);

                var editItem = CreateMenuItem(LocalizationService.Get("Home.Context.Rename"), "\uE70F");
                editItem.Click += async (s, ev) => await RenameTileAsync(tile);
                menu.Items.Add(editItem);

                var selectItem = CreateMenuItem(LocalizationService.Get("Home.Context.Select"), "\uE762");
                selectItem.Click += (s, ev) =>
                {
                    if (!IsSelectionMode)
                        ToggleSelectionMode();
                    ToggleTileSelection(tile);
                };
                menu.Items.Add(selectItem);

                var copyItem = CreateMenuItem(LocalizationService.Get("Home.Context.CopyPath"), "\uE8C8");
                copyItem.Click += (s, ev) =>
                {
                    try { Clipboard.SetText(tile.Path); } catch { }
                };
                menu.Items.Add(copyItem);

                var openFolderItem = CreateMenuItem(LocalizationService.Get("Home.Context.OpenFolder"), "\uE838");
                openFolderItem.Click += (s, ev) => OpenContainingFolder(tile);
                menu.Items.Add(openFolderItem);

                var exportItem = CreateMenuItem(LocalizationService.Get("Home.Context.Export"), "\uEDE1");
                exportItem.Click += async (s, ev) => await ExportTileAsync(tile);
                menu.Items.Add(exportItem);

                menu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });

                var deleteItem = CreateMenuItem(LocalizationService.Get("Home.Context.Remove"), "\uE74D", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)));
                deleteItem.Click += async (s, ev) => await RemoveMissingRecentTileAsync(tile);
                menu.Items.Add(deleteItem);

                menu.PlacementTarget = grid;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }

        private MenuItem CreateMenuItem(string text, string icon, System.Windows.Media.Brush foreground = null)
        {
            var item = new MenuItem { Padding = new Thickness(8, 6, 16, 6) };
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            var iconText = new TextBlock { Text = icon, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            var textBlock = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            if (foreground != null)
            {
                iconText.Foreground = foreground;
                textBlock.Foreground = foreground;
            }
            else
            {
                iconText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60));
                textBlock.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30));
            }
            stack.Children.Add(iconText);
            stack.Children.Add(textBlock);
            item.Header = stack;
            return item;
        }

        private async Task RenameTileAsync(HomeTile tile)
        {
            if (string.IsNullOrWhiteSpace(tile.Path) || !System.IO.File.Exists(tile.Path)) return;

            var mw = Window.GetWindow(this) as MainWindow;
            if (mw == null) return;

            // Modern input dialog
            var inputWin = new Window
            {
                Title = "Rename File",
                Width = 400,
                Height = 210,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = mw,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 253, 253)),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
                ShowInTaskbar = false
            };

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            inputWin.MouseLeftButtonDown += (s, ev) => { inputWin.DragMove(); };

            var titleLabel = new TextBlock
            {
                Text = LocalizationService.Get("Home.RenameTitle"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(titleLabel, 0);
            grid.Children.Add(titleLabel);

            var label = new TextBlock
            {
                Text = LocalizationService.Get("Home.RenamePrompt"),
                FontSize = 14,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(label, 1);
            grid.Children.Add(label);

            var nameBoxStyle = new Style(typeof(TextBox));
            nameBoxStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(10, 8, 10, 8)));
            nameBoxStyle.Setters.Add(new Setter(TextBox.FontSizeProperty, 14.0));
            nameBoxStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));

            // To mimic modern rounded corner without custom template, wrap in Border (though standard TextBox template is square, setting borderbrush clear and wrapping helps)
            var textBoxBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
                BorderThickness = new Thickness(1),
                Background = System.Windows.Media.Brushes.White,
                Padding = new Thickness(0)
            };
            Grid.SetRow(textBoxBorder, 2);

            var nameBox = new TextBox
            {
                Text = System.IO.Path.GetFileNameWithoutExtension(tile.Path),
                Style = nameBoxStyle,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent
            };
            nameBox.SelectAll();
            textBoxBorder.Child = nameBox;
            grid.Children.Add(textBoxBorder);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRow(btnPanel, 3);

            var cancelBtn = new Button
            {
                Content = LocalizationService.Get("Common.Cancel"),
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true
            };
            var secStyle = Application.Current.TryFindResource("DialogSecondaryButton") as Style;
            if (secStyle != null)
            {
                cancelBtn.Style = secStyle;
            }
            else
            {
                cancelBtn.Width = 80;
                cancelBtn.Height = 32;
            }
            cancelBtn.Click += (s, ev) => inputWin.DialogResult = false;

            var okBtn = new Button
            {
                Content = LocalizationService.Get("Home.RenameAction"),
                IsDefault = true
            };
            var priStyle = Application.Current.TryFindResource("DialogPrimaryButton") as Style;
            if (priStyle != null)
            {
                okBtn.Style = priStyle;
            }
            else
            {
                okBtn.Width = 80;
                okBtn.Height = 32;
            }
            okBtn.Click += (s, ev) => inputWin.DialogResult = true;

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            grid.Children.Add(btnPanel);

            inputWin.Content = grid;
            nameBox.Focus();

            if (inputWin.ShowDialog() == true)
            {
                var newName = nameBox.Text.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    var dir = System.IO.Path.GetDirectoryName(tile.Path);
                    var ext = System.IO.Path.GetExtension(tile.Path);
                    var newPath = System.IO.Path.Combine(dir, newName + ext);
                    try
                    {
                        var oldPath = tile.Path;
                        System.IO.File.Move(oldPath, newPath);
                        tile.SetPath(newPath);
                        tile.LastModified = System.IO.File.GetLastWriteTime(newPath);
                        RecentFilesService.Remove(oldPath);
                        RecentFilesService.AddOrPromote(newPath, tile.PageCount, System.IO.File.GetLastWriteTimeUtc(newPath));
                    }
                    catch (Exception ex)
                    {
                        await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Format("Home.RenameFailed", ex.Message));
                    }
                }
            }
        }

        private Style BuildContextMenuStyle()
        {
            var style = new Style(typeof(ContextMenu));
            style.Setters.Add(new Setter(ContextMenu.BackgroundProperty,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(248, 255, 255, 255))));
            style.Setters.Add(new Setter(ContextMenu.BorderBrushProperty,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 0, 0, 0))));
            style.Setters.Add(new Setter(ContextMenu.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(ContextMenu.PaddingProperty, new Thickness(4, 8, 4, 8)));
            style.Setters.Add(new Setter(ContextMenu.FontSizeProperty, 13.0));

            // Add rounded corners to ContextMenu
            var template = new ControlTemplate(typeof(ContextMenu));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ContextMenu.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(ContextMenu.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(ContextMenu.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(ContextMenu.PaddingProperty));

            var effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.15, Color = System.Windows.Media.Colors.Black };
            border.SetValue(Border.EffectProperty, effect);

            var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            border.AppendChild(itemsPresenter);
            template.VisualTree = border;
            style.Setters.Add(new Setter(ContextMenu.TemplateProperty, template));

            return style;
        }

        private async Task PickAndOpenPdfAsync()
        {
            var picker = new OpenFileDialog
            {
                Filter = LocalizationService.Get("Home.PdfFilter"),
                Title = LocalizationService.Get("Home.OpenPdfTitle")
            };

            if (picker.ShowDialog() == true)
            {
                var filePath = picker.FileName;
                await AddToRecentFilesAsync(filePath);
                if (Window.GetWindow(this) is MainWindow mw)
                    mw.NavigateActiveTabToFile(filePath);
                else
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
            RecentFilesService.AddOrPromote(path);
            await Task.CompletedTask;
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
                await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Get("Home.ErrorUnsupportedType"));
                return;
            }

            try
            {
                if (!System.IO.File.Exists(tile.Path))
                {
                    await RemoveMissingRecentTileAsync(tile);
                    await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Get("Home.ErrorFileNotFound"));
                    return;
                }
                if (Window.GetWindow(this) is MainWindow mw)
                    mw.NavigateActiveTabToFile(tile.Path);
                else
                    NavigationService?.Navigate(new EditorPage(tile.Path));
            }
            catch (Exception)
            {
                await RemoveMissingRecentTileAsync(tile);
                await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Get("Home.ErrorAccessDenied"));
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
                    RecentFilesService.Remove(tile.Path);
                    await Task.CompletedTask;
                    return;
                }
            }
        }

        private async Task ShowDialogAsync(string title, string content)
        {
            MessageBox.Show(content, title, MessageBoxButton.OK, MessageBoxImage.Information);
            await Task.CompletedTask;
        }

        // 鈹€鈹€鈹€ Smooth Scrolling 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        private double _targetVerticalOffset;
        private bool _smoothScrollInitialized;
        private double _scrollAnimationTarget;
        private double _scrollAnimationStart;
        private DateTime _scrollAnimationStartTime;
        private TimeSpan _scrollAnimationDuration;
        private bool _isScrollAnimating;

        private void HomeScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;

            if (!_smoothScrollInitialized)
            {
                _targetVerticalOffset = HomeScrollViewer.VerticalOffset;
                _smoothScrollInitialized = true;
            }

            double scrollAmount = -e.Delta * 0.8;
            _targetVerticalOffset = Math.Max(0,
                Math.Min(HomeScrollViewer.ScrollableHeight, _targetVerticalOffset + scrollAmount));

            _scrollAnimationTarget = _targetVerticalOffset;
            _scrollAnimationStart = HomeScrollViewer.VerticalOffset;
            _scrollAnimationStartTime = DateTime.UtcNow;
            _scrollAnimationDuration = TimeSpan.FromMilliseconds(180);

            if (!_isScrollAnimating)
            {
                _isScrollAnimating = true;
                System.Windows.Media.CompositionTarget.Rendering += HomeCompositionTarget_Rendering;
            }
        }

        private void HomeCompositionTarget_Rendering(object sender, EventArgs e)
        {
            var elapsed = DateTime.UtcNow - _scrollAnimationStartTime;
            double progress = Math.Min(1.0, elapsed.TotalMilliseconds / _scrollAnimationDuration.TotalMilliseconds);
            double easedProgress = 1.0 - Math.Pow(1.0 - progress, 3);

            double currentOffset = _scrollAnimationStart + (_scrollAnimationTarget - _scrollAnimationStart) * easedProgress;
            HomeScrollViewer.ScrollToVerticalOffset(currentOffset);

            if (progress >= 1.0)
            {
                _isScrollAnimating = false;
                System.Windows.Media.CompositionTarget.Rendering -= HomeCompositionTarget_Rendering;
            }
        }
    }

    public sealed class HomeTile : INotifyPropertyChanged
    {
        public bool IsAddTile { get; private set; }

        public string Path { get; private set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public void SetPath(string newPath)
        {
            Path = newPath;
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(InfoText));
        }

        private int _pageCount;
        public int PageCount
        {
            get => _pageCount;
            set { _pageCount = value; OnPropertyChanged(nameof(PageCount)); OnPropertyChanged(nameof(InfoText)); }
        }

        private DateTime _lastModified;
        public DateTime LastModified
        {
            get => _lastModified;
            set { _lastModified = value; OnPropertyChanged(nameof(LastModified)); OnPropertyChanged(nameof(InfoText)); }
        }

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

        public string InfoText
        {
            get
            {
                if (IsAddTile || string.IsNullOrWhiteSpace(Path)) return string.Empty;
                var parts = new List<string>();
                if (PageCount > 0) parts.Add(LocalizationService.Format("Home.Info.Pages", PageCount));
                if (LastModified != default) parts.Add(LastModified.ToString("d", LocalizationService.CurrentCulture));
                return string.Join(" · ", parts);
            }
        }

        public void RefreshDisplay()
        {
            OnPropertyChanged(nameof(InfoText));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static HomeTile CreateAddTile()
        {
            return new HomeTile { IsAddTile = true, Path = string.Empty };
        }

        public static HomeTile CreateFileTile(string path, int pageCount = 0, DateTime? lastModified = null)
        {
            return new HomeTile
            {
                IsAddTile = false,
                Path = path ?? string.Empty,
                _pageCount = pageCount,
                _lastModified = lastModified?.ToLocalTime() ?? default
            };
        }

        public static HomeTile CreateFileTile(RecentFileEntry entry)
        {
            if (entry == null)
                return CreateFileTile(string.Empty);

            return CreateFileTile(entry.Path, entry.PageCount, entry.LastModifiedUtc);
        }
    }
}





















