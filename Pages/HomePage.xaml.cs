using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Caelum.Services;

namespace Caelum.Pages
{
    public sealed partial class HomePage : Page
    {
        private enum HomeSortMode
        {
            Date,
            Name
        }

        public ObservableCollection<HomeTile> HomeTiles { get; } = new ObservableCollection<HomeTile>();

        private bool _libraryLoaded;
        private string _currentFolderId = string.Empty;
        private string _currentFolderName = string.Empty;
        private string _searchQuery = string.Empty;
        private HomeSortMode _currentSortMode = HomeSortMode.Date;
        private Point _dragStartPoint;
        private HomeTile _dragCandidateTile;
        private string[] _dragCandidatePaths = Array.Empty<string>();

        public bool IsSelectionMode { get; private set; }

        public HomePage()
        {
            InitializeComponent();
            DataContext = this;
            ApplyLocalization();
            Loaded += HomePage_Loaded;
            DragOver += HomePage_DragOver;
            Drop += HomePage_Drop;
            DragLeave += HomePage_DragLeave;
        }

        public bool IsInsideFolder => !string.IsNullOrWhiteSpace(_currentFolderId);

        public string NavigateUpText => LocalizationService.Get("Home.NavigateUp");

        public string FolderBreadcrumb
        {
            get
            {
                if (!IsInsideFolder)
                    return string.Empty;

                var names = new List<string>();
                var cursor = RecentFilesService.GetFolder(_currentFolderId);
                while (cursor != null)
                {
                    names.Add(cursor.DisplayName);
                    cursor = string.IsNullOrWhiteSpace(cursor.ParentFolderId)
                        ? null
                        : RecentFilesService.GetFolder(cursor.ParentFolderId);
                }

                names.Reverse();
                names.Insert(0, LocalizationService.Get("Home.LibraryRoot"));
                return string.Join(" / ", names);
            }
        }

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            await EnsureLibraryLoadedAsync();
        }

        private async Task EnsureLibraryLoadedAsync()
        {
            if (_libraryLoaded)
            {
                await RefreshCurrentFolderAsync();
                return;
            }

            await RefreshCurrentFolderAsync();
            _libraryLoaded = true;
        }

        public void Filter(string query)
        {
            _searchQuery = query?.Trim() ?? string.Empty;
            ApplyCollectionView();
            RefreshSelectionState();
        }

        public void SortByName()
        {
            _currentSortMode = HomeSortMode.Name;
            ApplyCollectionView();
        }

        public void SortByDate()
        {
            _currentSortMode = HomeSortMode.Date;
            ApplyCollectionView();
        }

        private async Task RefreshCurrentFolderAsync()
        {
            var selectionState = HomeTiles
                .Where(tile => tile.IsFile && tile.IsSelected)
                .ToDictionary(tile => tile.Path, tile => tile.IsSelected, StringComparer.OrdinalIgnoreCase);

            var activeFolder = string.IsNullOrWhiteSpace(_currentFolderId) ? null : RecentFilesService.GetFolder(_currentFolderId);
            if (!string.IsNullOrWhiteSpace(_currentFolderId) && activeFolder == null)
            {
                _currentFolderId = string.Empty;
                _currentFolderName = string.Empty;
            }
            else
            {
                _currentFolderName = activeFolder?.DisplayName ?? string.Empty;
            }

            HomeTiles.Clear();
            HomeTiles.Add(HomeTile.CreateAddTile());

            foreach (var entry in RecentFilesService.GetLibraryEntries(_currentFolderId))
            {
                if (entry.IsFolder)
                {
                    HomeTiles.Add(HomeTile.CreateFolderTile(entry, RecentFilesService.GetDirectChildCount(entry.Id)));
                    continue;
                }

                if (!string.Equals(Path.GetExtension(entry.Path), ".pdf", StringComparison.OrdinalIgnoreCase))
                    continue;

                var tile = HomeTile.CreateFileTile(entry);
                if (selectionState.TryGetValue(tile.Path, out var isSelected))
                    tile.IsSelected = isSelected;

                HomeTiles.Add(tile);
            }

            UpdateHeaderText();
            ApplyCollectionView();
            RefreshSelectionState();
            await Task.CompletedTask;
        }

        private void UpdateHeaderText()
        {
            HomeTitleTextBlock.Text = IsInsideFolder
                ? _currentFolderName
                : LocalizationService.Get("Home.Title");
            HomeSubtitleTextBlock.Text = IsInsideFolder
                ? LocalizationService.Format("Home.FolderSubtitle", _currentFolderName)
                : LocalizationService.Get("Home.Subtitle");

            OnPropertyChanged(nameof(IsInsideFolder));
            OnPropertyChanged(nameof(NavigateUpText));
            OnPropertyChanged(nameof(FolderBreadcrumb));
        }

        private void ApplyCollectionView()
        {
            var view = (CollectionView)CollectionViewSource.GetDefaultView(HomeTiles);
            view.Filter = item =>
            {
                if (item is not HomeTile tile)
                    return false;

                if (tile.IsAddTile)
                    return true;

                if (string.IsNullOrWhiteSpace(_searchQuery))
                    return true;

                return tile.FileName?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) == true;
            };

            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(HomeTile.SortPriority), ListSortDirection.Ascending));

            if (_currentSortMode == HomeSortMode.Name)
            {
                view.SortDescriptions.Add(new SortDescription(nameof(HomeTile.FileName), ListSortDirection.Ascending));
            }
            else
            {
                view.SortDescriptions.Add(new SortDescription(nameof(HomeTile.LastModified), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(HomeTile.FileName), ListSortDirection.Ascending));
            }

            view.Refresh();
        }

        private void AddTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement placementTarget)
                ShowAddTileMenu(placementTarget);
        }

        private void ShowAddTileMenu(FrameworkElement placementTarget)
        {
            var menu = new ContextMenu
            {
                Style = BuildContextMenuStyle(),
                PlacementTarget = placementTarget
            };

            var openItem = CreateMenuItem(LocalizationService.Get("Home.Menu.OpenFile"), "\uE8E5");
            openItem.Click += async (_, _) => await PickAndOpenPdfAsync();
            menu.Items.Add(openItem);

            var createFolderItem = CreateMenuItem(LocalizationService.Get("Home.Menu.CreateFolder"), "\uE8B7");
            createFolderItem.Click += async (_, _) => await CreateFolderAsync();
            menu.Items.Add(createFolderItem);

            var createNotebookItem = CreateMenuItem(LocalizationService.Get("Home.Menu.CreateNotebook"), "\uE70B");
            createNotebookItem.Click += async (_, _) => await CreateEmptyNotebookAsync();
            menu.Items.Add(createNotebookItem);

            menu.IsOpen = true;
        }

        private void FileTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not HomeTile tile || !tile.IsFile)
                return;

            if (IsSelectionMode)
            {
                ToggleTileSelection(tile);
                return;
            }

            _ = OpenFileTileAsync(tile);
        }

        private void FolderTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not HomeTile tile || !tile.IsFolder)
                return;

            _currentFolderId = tile.Id;
            _currentFolderName = tile.FileName;
            _ = RefreshCurrentFolderAsync();
        }

        private void FileTile_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not HomeTile tile || !tile.IsFile)
                return;

            ShowFileContextMenu(tile, element);
            e.Handled = true;
        }

        private void FolderTile_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not HomeTile tile || !tile.IsFolder)
                return;

            ShowFolderContextMenu(tile, element);
            e.Handled = true;
        }

        private void ShowFileContextMenu(HomeTile tile, FrameworkElement placementTarget)
        {
            var menu = new ContextMenu
            {
                Style = BuildContextMenuStyle(),
                PlacementTarget = placementTarget
            };

            var openItem = CreateMenuItem(LocalizationService.Get("Home.Context.Open"), "\uE7C3");
            openItem.Click += async (_, _) => await OpenFileTileAsync(tile);
            menu.Items.Add(openItem);

            var renameItem = CreateMenuItem(LocalizationService.Get("Home.Context.Rename"), "\uE70F");
            renameItem.Click += async (_, _) => await RenameTileAsync(tile);
            menu.Items.Add(renameItem);

            var selectItem = CreateMenuItem(LocalizationService.Get("Home.Context.Select"), "\uE762");
            selectItem.Click += (_, _) =>
            {
                if (!IsSelectionMode)
                    ToggleSelectionMode();
                ToggleTileSelection(tile);
            };
            menu.Items.Add(selectItem);

            if (IsInsideFolder)
            {
                var moveToRootItem = CreateMenuItem(LocalizationService.Get("Home.Context.MoveToLibrary"), "\uE8DE");
                moveToRootItem.Click += async (_, _) =>
                {
                    RecentFilesService.MoveToLibraryRoot(tile.Path);
                    await RefreshCurrentFolderAsync();
                };
                menu.Items.Add(moveToRootItem);
            }

            var copyItem = CreateMenuItem(LocalizationService.Get("Home.Context.CopyPath"), "\uE8C8");
            copyItem.Click += (_, _) =>
            {
                try { Clipboard.SetText(tile.Path); } catch { }
            };
            menu.Items.Add(copyItem);

            var openFolderItem = CreateMenuItem(LocalizationService.Get("Home.Context.OpenFolder"), "\uE838");
            openFolderItem.Click += (_, _) => OpenContainingFolder(tile);
            menu.Items.Add(openFolderItem);

            var exportItem = CreateMenuItem(LocalizationService.Get("Home.Context.Export"), "\uEDE1");
            exportItem.Click += async (_, _) => await ExportTileAsync(tile);
            menu.Items.Add(exportItem);

            menu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });

            var removeItem = CreateMenuItem(LocalizationService.Get("Home.Context.Remove"), "\uE74D", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)));
            removeItem.Click += async (_, _) => await RemoveFileTileAsync(tile);
            menu.Items.Add(removeItem);

            menu.IsOpen = true;
        }

        private void ShowFolderContextMenu(HomeTile tile, FrameworkElement placementTarget)
        {
            var menu = new ContextMenu
            {
                Style = BuildContextMenuStyle(),
                PlacementTarget = placementTarget
            };

            var openItem = CreateMenuItem(LocalizationService.Get("Home.Context.Open"), "\uE8B7");
            openItem.Click += (_, _) =>
            {
                _currentFolderId = tile.Id;
                _currentFolderName = tile.FileName;
                _ = RefreshCurrentFolderAsync();
            };
            menu.Items.Add(openItem);

            var renameItem = CreateMenuItem(LocalizationService.Get("Home.Context.Rename"), "\uE70F");
            renameItem.Click += async (_, _) => await RenameTileAsync(tile);
            menu.Items.Add(renameItem);

            menu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });

            var removeItem = CreateMenuItem(LocalizationService.Get("Home.Context.RemoveFolder"), "\uE74D", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)));
            removeItem.Click += async (_, _) =>
            {
                RecentFilesService.RemoveFolder(tile.Id);
                await RefreshCurrentFolderAsync();
            };
            menu.Items.Add(removeItem);

            menu.IsOpen = true;
        }

        private MenuItem CreateMenuItem(string text, string icon, System.Windows.Media.Brush foreground = null)
        {
            var item = new MenuItem { Padding = new Thickness(8, 6, 16, 6) };
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            var iconText = new TextBlock
            {
                Text = icon,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Width = 28,
                VerticalAlignment = VerticalAlignment.Center
            };
            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };

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

            var template = new ControlTemplate(typeof(ContextMenu));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ContextMenu.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(ContextMenu.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(ContextMenu.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(ContextMenu.PaddingProperty));
            border.SetValue(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 4,
                Opacity = 0.15,
                Color = System.Windows.Media.Colors.Black
            });

            var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            border.AppendChild(itemsPresenter);
            template.VisualTree = border;
            style.Setters.Add(new Setter(ContextMenu.TemplateProperty, template));
            return style;
        }

        private async Task RenameTileAsync(HomeTile tile)
        {
            if (tile == null || tile.IsAddTile)
                return;

            if (tile.IsFolder)
            {
                var newName = PromptForInput(
                    LocalizationService.Get("Home.RenameFolderTitle"),
                    LocalizationService.Get("Home.RenameFolderPrompt"),
                    tile.FileName,
                    LocalizationService.Get("Home.RenameAction"));

                if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName.Trim(), tile.FileName, StringComparison.Ordinal))
                    return;

                RecentFilesService.RenameFolder(tile.Id, newName.Trim());
                await RefreshCurrentFolderAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(tile.Path) || !File.Exists(tile.Path))
                return;

            var oldPath = tile.Path;
            var newBaseName = PromptForInput(
                LocalizationService.Get("Home.RenameTitle"),
                LocalizationService.Get("Home.RenamePrompt"),
                Path.GetFileNameWithoutExtension(oldPath),
                LocalizationService.Get("Home.RenameAction"));

            if (string.IsNullOrWhiteSpace(newBaseName))
                return;

            var directory = Path.GetDirectoryName(oldPath) ?? string.Empty;
            var extension = Path.GetExtension(oldPath);
            var newPath = Path.Combine(directory, newBaseName.Trim() + extension);

            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                File.Move(oldPath, newPath);
                RecentFilesService.UpdatePath(oldPath, newPath);
                tile.SetPath(newPath);
                tile.LastModified = File.GetLastWriteTime(newPath);
                GetMainWindow()?.HandleFilePathChanged(oldPath, newPath);
                await RefreshCurrentFolderAsync();
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Format("Home.RenameFailed", ex.Message));
            }
        }

        private string PromptForInput(string title, string prompt, string initialValue, string confirmText)
        {
            var owner = Window.GetWindow(this) as MainWindow;
            if (owner == null)
                return null;

            var inputWindow = new Window
            {
                Title = title,
                Width = 430,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false
            };

            var mainBorder = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 253, 253)),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 4,
                    Opacity = 0.15,
                    Color = System.Windows.Media.Colors.Black
                }
            };

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var titleLabel = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(titleLabel, 0);

            var promptLabel = new TextBlock
            {
                Text = prompt,
                FontSize = 14,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(promptLabel, 1);

            var textBoxBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
                BorderThickness = new Thickness(1),
                Background = System.Windows.Media.Brushes.White
            };
            Grid.SetRow(textBoxBorder, 2);

            var inputBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Padding = new Thickness(10, 8, 10, 8),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent
            };
            inputBox.SelectAll();
            textBoxBorder.Child = inputBox;

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRow(buttonPanel, 3);

            var cancelButton = new Button
            {
                Content = LocalizationService.Get("Common.Cancel"),
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true
            };
            var secondaryStyle = Application.Current.TryFindResource("DialogSecondaryButton") as Style;
            if (secondaryStyle != null)
                cancelButton.Style = secondaryStyle;
            cancelButton.Click += (_, _) => inputWindow.DialogResult = false;

            var confirmButton = new Button
            {
                Content = confirmText,
                IsDefault = true
            };
            var primaryStyle = Application.Current.TryFindResource("DialogPrimaryButton") as Style;
            if (primaryStyle != null)
                confirmButton.Style = primaryStyle;
            confirmButton.Click += (_, _) => inputWindow.DialogResult = true;

            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(confirmButton);

            grid.Children.Add(titleLabel);
            grid.Children.Add(promptLabel);
            grid.Children.Add(textBoxBorder);
            grid.Children.Add(buttonPanel);

            mainBorder.Child = grid;
            inputWindow.Content = mainBorder;
            inputWindow.MouseLeftButtonDown += (_, _) => inputWindow.DragMove();

            inputBox.Focus();
            return inputWindow.ShowDialog() == true ? inputBox.Text.Trim() : null;
        }

        private async Task PickAndOpenPdfAsync()
        {
            var picker = new OpenFileDialog
            {
                Filter = LocalizationService.Get("Home.PdfFilter"),
                Title = LocalizationService.Get("Home.OpenPdfTitle")
            };

            if (picker.ShowDialog() != true)
                return;

            var folderId = IsInsideFolder ? _currentFolderId : null;
            await AddFileToLibraryAsync(picker.FileName, folderId, false);

            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateActiveTabToFile(picker.FileName);
            else
                NavigationService?.Navigate(new EditorPage(picker.FileName));
        }

        private async Task CreateFolderAsync()
        {
            var name = PromptForInput(
                LocalizationService.Get("Home.CreateFolderTitle"),
                LocalizationService.Get("Home.CreateFolderPrompt"),
                string.Empty,
                LocalizationService.Get("Home.CreateFolderAction"));

            if (string.IsNullOrWhiteSpace(name))
                return;

            RecentFilesService.CreateFolder(name, _currentFolderId);
            await RefreshCurrentFolderAsync();
            GetMainWindow()?.ShowToast(LocalizationService.Format("Home.FolderCreated", name.Trim()), "\uE8B7");
        }

        private async Task CreateEmptyNotebookAsync()
        {
            var owner = Window.GetWindow(this) as MainWindow;
            var picker = new PageTemplatePickerWindow(
                notebookCreationMode: true,
                initialFolderPath: GetDefaultNotebookDirectory());

            if (owner != null)
                picker.Owner = owner;

            if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedFolderPath))
                return;

            Directory.CreateDirectory(picker.SelectedFolderPath);
            var notebookPath = BuildNotebookFilePath(picker.SelectedFolderPath);

            try
            {
                await PdfService.CreateBlankPdfAsync(notebookPath, template: picker.SelectedTemplate);
                await AddFileToLibraryAsync(notebookPath, IsInsideFolder ? _currentFolderId : null, true);

                if (owner != null)
                    owner.NavigateActiveTabToFile(notebookPath);
                else
                    NavigationService?.Navigate(new EditorPage(notebookPath));
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Format("Home.CreateNotebookFailed", ex.Message));
            }
        }

        private async Task AddFileToLibraryAsync(string path, string folderId, bool isNotebook)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                return;

            DateTime? lastModifiedUtc = null;
            if (File.Exists(path))
                lastModifiedUtc = File.GetLastWriteTimeUtc(path);

            RecentFilesService.AddOrPromote(path, null, lastModifiedUtc, folderId, isNotebook);
            await RefreshCurrentFolderAsync();
        }

        private async Task OpenFileTileAsync(HomeTile tile)
        {
            if (tile == null || !tile.IsFile || string.IsNullOrWhiteSpace(tile.Path))
                return;

            if (!string.Equals(Path.GetExtension(tile.Path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await RemoveFileTileAsync(tile);
                await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Get("Home.ErrorUnsupportedType"));
                return;
            }

            try
            {
                if (!File.Exists(tile.Path))
                {
                    await RemoveFileTileAsync(tile);
                    await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Get("Home.ErrorFileNotFound"));
                    return;
                }

                RecentFilesService.AddOrPromote(tile.Path);
                if (Window.GetWindow(this) is MainWindow mw)
                    mw.NavigateActiveTabToFile(tile.Path);
                else
                    NavigationService?.Navigate(new EditorPage(tile.Path));
            }
            catch
            {
                await RemoveFileTileAsync(tile);
                await ShowDialogAsync(LocalizationService.Get("Common.Error"), LocalizationService.Get("Home.ErrorAccessDenied"));
            }
        }

        private async Task RemoveFileTileAsync(HomeTile tile)
        {
            if (tile == null || !tile.IsFile)
                return;

            RecentFilesService.Remove(tile.Path);
            await RefreshCurrentFolderAsync();
        }

        private async Task ShowDialogAsync(string title, string content)
        {
            var owner = Window.GetWindow(this);
            if (owner != null)
                await DialogService.ShowInfoAsync(owner, title, content);
            else
                MessageBox.Show(content, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void NavigateUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsInsideFolder)
                return;

            var parentFolder = RecentFilesService.GetFolder(_currentFolderId)?.ParentFolderId;
            _currentFolderId = parentFolder ?? string.Empty;
            _currentFolderName = RecentFilesService.GetFolder(_currentFolderId)?.DisplayName ?? string.Empty;
            _ = RefreshCurrentFolderAsync();
        }

        public bool ShouldDeferWindowFileDrop(DependencyObject originalSource, IDataObject data)
        {
            return HomePageDragDropHelper.HasSupportedFolderDropPayload(data);
        }

        private void FileTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not HomeTile tile || !tile.IsFile)
            {
                ClearDragCandidate();
                return;
            }

            if (IsSelectionMode && !tile.IsSelected)
            {
                ClearDragCandidate();
                return;
            }

            _dragCandidatePaths = GetDragCandidatePaths(tile);
            if (_dragCandidatePaths.Length == 0)
            {
                ClearDragCandidate();
                return;
            }

            _dragCandidateTile = tile;
            _dragStartPoint = e.GetPosition(this);
        }

        private void FileTile_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragCandidateTile == null || _dragCandidatePaths.Length == 0 || e.LeftButton != MouseButtonState.Pressed)
                return;

            var currentPosition = e.GetPosition(this);
            if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var dataObject = new DataObject();
            dataObject.SetData(HomePageDragDropHelper.LibraryTilePathsDataFormat, _dragCandidatePaths);
            if (_dragCandidatePaths.Length == 1)
                dataObject.SetData(HomePageDragDropHelper.LibraryTilePathDataFormat, _dragCandidatePaths[0]);

            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, dataObject, DragDropEffects.Move);
            }
            finally
            {
                ClearDragCandidate();
            }
        }

        private void FolderTile_DragEnter(object sender, DragEventArgs e)
        {
            UpdateFolderDropState(sender, e, true);
        }

        private void FolderTile_DragOver(object sender, DragEventArgs e)
        {
            UpdateFolderDropState(sender, e, true);
        }

        private void FolderTile_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is HomeTile tile)
                tile.IsDropTarget = false;
        }

        private async void FolderTile_Drop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not HomeTile tile || !tile.IsFolder)
                return;

            tile.IsDropTarget = false;
            var movedAny = false;

            var libraryTilePaths = HomePageDragDropHelper.GetLibraryTilePaths(e.Data);
            if (libraryTilePaths.Length > 0)
            {
                foreach (var filePath in libraryTilePaths)
                    movedAny = RecentFilesService.MoveToFolder(filePath, tile.Id) || movedAny;
            }
            else
            {
                foreach (var file in HomePageDragDropHelper.GetDroppedPdfPaths(e.Data))
                {
                    RecentFilesService.AddOrPromote(
                        file,
                        null,
                        File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null,
                        tile.Id,
                        false);
                    movedAny = true;
                }
            }

            if (movedAny)
            {
                await RefreshCurrentFolderAsync();
                GetMainWindow()?.ShowToast(LocalizationService.Format("Home.MovedToFolder", tile.FileName), "\uE8B7");
            }

            e.Handled = true;
        }

        private void UpdateFolderDropState(object sender, DragEventArgs e, bool isActive)
        {
            if (sender is not FrameworkElement element || element.Tag is not HomeTile tile || !tile.IsFolder)
                return;

            var pdfPaths = HomePageDragDropHelper.GetDroppedPdfPaths(e.Data);
            var libraryPaths = HomePageDragDropHelper.GetLibraryTilePaths(e.Data);
            var canAcceptDrop = pdfPaths.Length > 0 || libraryPaths.Length > 0;

            tile.IsDropTarget = isActive && canAcceptDrop;
            e.Effects = canAcceptDrop
                ? (libraryPaths.Length > 0 ? DragDropEffects.Move : DragDropEffects.Copy)
                : DragDropEffects.None;
            e.Handled = true;
        }

        private string[] GetDragCandidatePaths(HomeTile tile)
        {
            if (tile == null || !tile.IsFile)
                return Array.Empty<string>();

            if (!IsSelectionMode)
                return string.IsNullOrWhiteSpace(tile.Path)
                    ? Array.Empty<string>()
                    : new[] { tile.Path };

            return HomeTiles
                .Where(candidate => candidate.IsFile && candidate.IsSelected && !string.IsNullOrWhiteSpace(candidate.Path))
                .Select(candidate => candidate.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void ClearDragCandidate()
        {
            _dragCandidateTile = null;
            _dragCandidatePaths = Array.Empty<string>();
        }

        private void HomePage_DragOver(object sender, DragEventArgs e)
        {
            if (GetFolderTileFromSource(e.OriginalSource as DependencyObject) != null)
            {
                DragDropOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var pdfPaths = HomePageDragDropHelper.GetDroppedPdfPaths(e.Data);
            var libraryPaths = HomePageDragDropHelper.GetLibraryTilePaths(e.Data);
            if (pdfPaths.Length > 0 || libraryPaths.Length > 0)
            {
                e.Effects = libraryPaths.Length > 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                DragDropOverlay.Visibility = Visibility.Visible;
                e.Handled = true;
            }
            else
            {
                DragDropOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void HomePage_DragLeave(object sender, DragEventArgs e)
        {
            DragDropOverlay.Visibility = Visibility.Collapsed;
        }

        private async void HomePage_Drop(object sender, DragEventArgs e)
        {
            DragDropOverlay.Visibility = Visibility.Collapsed;

            if (GetFolderTileFromSource(e.OriginalSource as DependencyObject) != null)
                return;

            var libraryPaths = HomePageDragDropHelper.GetLibraryTilePaths(e.Data);
            var pdfPaths = HomePageDragDropHelper.GetDroppedPdfPaths(e.Data);
            bool movedAny = false;

            if (libraryPaths.Length > 0 && IsInsideFolder)
            {
                foreach (var filePath in libraryPaths)
                    movedAny = RecentFilesService.MoveToFolder(filePath, _currentFolderId) || movedAny;
            }
            else if (pdfPaths.Length > 0)
            {
                foreach (var file in pdfPaths)
                {
                    RecentFilesService.AddOrPromote(
                        file,
                        null,
                        File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null,
                        IsInsideFolder ? _currentFolderId : null,
                        false);
                    movedAny = true;
                }
            }

            if (movedAny)
            {
                await RefreshCurrentFolderAsync();
                var targetName = IsInsideFolder ? _currentFolderName : LocalizationService.Get("Home.LibraryRoot");
                GetMainWindow()?.ShowToast(LocalizationService.Format("Home.MovedToFolder", targetName), "\uE8B7");
            }

            e.Handled = true;
        }

        private HomeTile GetFolderTileFromSource(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element &&
                    element.Tag is HomeTile tile &&
                    tile.IsFolder)
                {
                    return tile;
                }

                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static bool IsPdfFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string((name ?? string.Empty).Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "Notebook" : sanitized;
        }

        private static string GetDefaultNotebookDirectory()
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documentsPath))
                return documentsPath;

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrWhiteSpace(desktopPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caelum", "Notebooks")
                : desktopPath;
        }

        private static string BuildNotebookFilePath(string directory)
        {
            var notebookName = SanitizeFileName(LocalizationService.Get("Home.NewNotebookName"));
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", LocalizationService.CurrentCulture);
            var baseName = $"{notebookName} {timestamp}";
            var filePath = Path.Combine(directory, $"{baseName}.pdf");
            var counter = 1;

            while (File.Exists(filePath))
            {
                filePath = Path.Combine(directory, $"{baseName} ({counter}).pdf");
                counter++;
            }

            return filePath;
        }

        private MainWindow GetMainWindow()
        {
            return Application.Current.MainWindow as MainWindow;
        }

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
        public string Id { get; private set; } = string.Empty;

        public bool IsAddTile { get; private set; }

        public bool IsFolder { get; private set; }

        public bool IsFile => !IsAddTile && !IsFolder;

        public bool IsNotebook { get; private set; }

        public string ParentFolderId { get; private set; } = string.Empty;

        public string Path { get; private set; } = string.Empty;

        public int SortPriority => IsAddTile ? 0 : IsFolder ? 1 : 2;

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

        private bool _isDropTarget;
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set
            {
                _isDropTarget = value;
                OnPropertyChanged(nameof(IsDropTarget));
            }
        }

        private int _pageCount;
        public int PageCount
        {
            get => _pageCount;
            set
            {
                _pageCount = value;
                OnPropertyChanged(nameof(PageCount));
                OnPropertyChanged(nameof(InfoText));
            }
        }

        private int _childCount;
        public int ChildCount
        {
            get => _childCount;
            set
            {
                _childCount = value;
                OnPropertyChanged(nameof(ChildCount));
                OnPropertyChanged(nameof(InfoText));
            }
        }

        private DateTime _lastModified;
        public DateTime LastModified
        {
            get => _lastModified;
            set
            {
                _lastModified = value;
                OnPropertyChanged(nameof(LastModified));
                OnPropertyChanged(nameof(InfoText));
            }
        }

        private string _displayName = string.Empty;

        public string FileName
        {
            get
            {
                if (IsFolder)
                    return _displayName;

                if (string.IsNullOrWhiteSpace(Path))
                    return string.Empty;

                return System.IO.Path.GetFileName(Path);
            }
        }

        public string InfoText
        {
            get
            {
                if (IsAddTile)
                    return string.Empty;

                if (IsFolder)
                    return LocalizationService.Format("Home.Info.Items", ChildCount);

                if (string.IsNullOrWhiteSpace(Path))
                    return string.Empty;

                var parts = new List<string>();
                if (IsNotebook)
                    parts.Add(LocalizationService.Get("Home.Info.Notebook"));
                if (PageCount > 0)
                    parts.Add(LocalizationService.Format("Home.Info.Pages", PageCount));
                if (LastModified != default)
                    parts.Add(LastModified.ToString("d", LocalizationService.CurrentCulture));
                return string.Join(" · ", parts);
            }
        }

        public void SetPath(string newPath)
        {
            Path = newPath ?? string.Empty;
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(InfoText));
        }

        public void RefreshDisplay()
        {
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(InfoText));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static HomeTile CreateAddTile()
        {
            return new HomeTile
            {
                IsAddTile = true
            };
        }

        public static HomeTile CreateFileTile(RecentFileEntry entry)
        {
            return new HomeTile
            {
                Id = entry?.Id ?? string.Empty,
                Path = entry?.Path ?? string.Empty,
                ParentFolderId = entry?.ParentFolderId ?? string.Empty,
                IsNotebook = entry?.IsNotebook == true,
                _pageCount = entry?.PageCount ?? 0,
                _lastModified = entry?.LastModifiedUtc?.ToLocalTime() ?? default
            };
        }

        public static HomeTile CreateFolderTile(RecentFileEntry entry, int childCount)
        {
            return new HomeTile
            {
                Id = entry?.Id ?? string.Empty,
                IsFolder = true,
                ParentFolderId = entry?.ParentFolderId ?? string.Empty,
                _displayName = entry?.DisplayName ?? string.Empty,
                _childCount = childCount,
                _lastModified = entry?.LastModifiedUtc?.ToLocalTime() ?? default
            };
        }
    }
}
