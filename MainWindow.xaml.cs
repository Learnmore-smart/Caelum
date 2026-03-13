using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Caelum.Models;
using Caelum.Pages;
using Caelum.Services;

namespace Caelum
{
    public partial class MainWindow : Window
    {
        private readonly List<AppTab> _tabs = new List<AppTab>();
        private AppTab _activeTab;

        public MainWindow()
        {
            InitializeComponent();
            LoadAppIcon();
            SourceInitialized += MainWindow_SourceInitialized;
            StateChanged += MainWindow_StateChanged;
            KeyDown += MainWindow_KeyDown;
            TitleBarBorder.MouseLeftButtonDown += (sender, args) => DragMove();
            ApplyLocalization();

            // Create the first Home tab
            AddNewHomeTab(activate: true);
        }

        private void LoadAppIcon()
        {
            try
            {
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app-icon.ico");
                if (!File.Exists(iconPath)) return;
                using var fs = new FileStream(iconPath, FileMode.Open, FileAccess.Read);
                var decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // Pick the largest frame 鈥?preserves 32-bit ARGB transparency
                var best = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                Icon = best;
            }
            catch
            {
                // Fall back silently 鈥?window will use default icon
            }
        }

        // 鈹€鈹€鈹€ Drag & Drop 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        private static readonly string[] SupportedDropExtensions = { ".pdf" };

        private bool HasSupportedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            return files != null && files.Any(f =>
                SupportedDropExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = HasSupportedFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasSupportedFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null) return;

            var pdfFiles = files.Where(f =>
                SupportedDropExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();

            if (pdfFiles.Count == 0) return;

            // If the active tab is on the Home page, open the first file in-place
            bool isHomePage = ActiveFrame?.Content is HomePage;
            bool first = true;

            foreach (var file in pdfFiles)
            {
                if (first && isHomePage)
                {
                    // Open directly in the current Home tab
                    NavigateActiveTabToFile(file);
                    first = false;
                }
                else
                {
                    OpenFileInNewTab(file);
                    first = false;
                }
            }
            e.Handled = true;
        }

        // 鈹€鈹€鈹€ Tab Management 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

        private Frame ActiveFrame => _activeTab?.Frame;

        public void AddNewHomeTab(bool activate = true)
        {
            var tab = new AppTab { Title = GetHomeTabTitle(), Icon = "\uE80F" };
            var frame = new Frame
            {
                NavigationUIVisibility = NavigationUIVisibility.Hidden,
                AllowDrop = true,
                Background = Brushes.Transparent
            };
            frame.Navigated += Frame_Navigated;
            tab.Frame = frame;
            TabContentArea.Children.Add(frame);
            _tabs.Add(tab);
            RebuildTabBar();

            frame.Navigate(new HomePage());

            if (activate)
                ActivateTab(tab);
        }

        public void OpenFileInNewTab(string filePath)
        {
            RecentFilesService.AddOrPromote(filePath);

            // Check if this file is already open
            var existing = _tabs.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.FilePath) &&
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ActivateTab(existing);
                return;
            }

            var name = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            string icon = ext == ".pdf" ? "\uEA90" : "\uE7C3"; // PDF icon or generic document

            var tab = new AppTab { Title = name, Icon = icon, FilePath = filePath };
            var frame = new Frame
            {
                NavigationUIVisibility = NavigationUIVisibility.Hidden,
                AllowDrop = true,
                Background = Brushes.Transparent
            };
            frame.Navigated += Frame_Navigated;
            tab.Frame = frame;
            TabContentArea.Children.Add(frame);
            _tabs.Add(tab);
            RebuildTabBar();

            frame.Navigate(new EditorPage(filePath));
            ActivateTab(tab);
        }

        private void ActivateTab(AppTab tab)
        {
            if (_activeTab == tab) return;

            foreach (var t in _tabs)
            {
                t.IsActive = false;
                if (t.Frame != null)
                    t.Frame.Visibility = Visibility.Collapsed;
            }

            tab.IsActive = true;
            tab.Frame.Visibility = Visibility.Visible;
            _activeTab = tab;

            UpdateNavButtons();
            RebuildTabBar();
        }

        private async void CloseTab(AppTab tab)
        {
            // Auto-save if editor
            if (tab.Frame?.Content is EditorPage editor)
            {
                var saved = await editor.AutoSaveAsync();
                if (saved) ShowToast(LocalizationService.Get("Main.FileAutoSaved"));
            }

            tab.Frame.Navigated -= Frame_Navigated;
            TabContentArea.Children.Remove(tab.Frame);
            _tabs.Remove(tab);

            if (_tabs.Count == 0)
            {
                // Always keep at least one tab
                AddNewHomeTab(activate: true);
            }
            else if (tab == _activeTab)
            {
                ActivateTab(_tabs.Last());
            }

            RebuildTabBar();
        }

        private void RebuildTabBar()
        {
            TabBar.Children.Clear();

            foreach (var tab in _tabs)
            {
                var tabButton = CreateTabButton(tab);
                TabBar.Children.Add(tabButton);
            }
        }

        private Border CreateTabButton(AppTab tab)
        {
            bool isActive = tab == _activeTab;
            var activeForeground = new SolidColorBrush(Color.FromRgb(31, 41, 55));
            var inactiveForeground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            var activeCloseForeground = new SolidColorBrush(Color.FromRgb(75, 85, 99));
            var inactiveCloseForeground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            var activeBackground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var hoverBackground = new SolidColorBrush(Color.FromArgb(18, 17, 24, 39));
            var transparentBackground = Brushes.Transparent;
            var activeBorderBrush = new SolidColorBrush(Color.FromArgb(18, 17, 24, 39));

            // Tab content: icon + title + close button
            var icon = new TextBlock
            {
                Text = tab.Icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = isActive ? activeForeground : inactiveForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var title = new TextBlock
            {
                Text = tab.Title.Length > 20 ? tab.Title.Substring(0, 17) + "..." : tab.Title,
                FontSize = 13,
                Foreground = isActive ? activeForeground : inactiveForeground,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 132,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = isActive ? FontWeights.Medium : FontWeights.Normal
            };

            var closeBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = "\uE8BB",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Foreground = isActive ? activeCloseForeground : inactiveCloseForeground
                },
                Width = 20,
                Height = 20,
                Background = transparentBackground,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
                Opacity = isActive ? 1 : 0.72,
                ToolTip = LocalizationService.Get("Main.CloseTabTooltip")
            };

            // Close button template with hover
            var closeBtnTemplate = new ControlTemplate(typeof(Button));
            var closeBorder = new FrameworkElementFactory(typeof(Border));
            closeBorder.SetValue(Border.BackgroundProperty, transparentBackground);
            closeBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            closeBorder.Name = "CloseBg";
            var closeContent = new FrameworkElementFactory(typeof(ContentPresenter));
            closeContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            closeContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            closeBorder.AppendChild(closeContent);
            closeBtnTemplate.VisualTree = closeBorder;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(18, 17, 24, 39)), "CloseBg"));
            closeBtnTemplate.Triggers.Add(hoverTrigger);

            closeBtn.Template = closeBtnTemplate;

            var capturedTab = tab;
            closeBtn.Click += (s, e) => { e.Handled = true; CloseTab(capturedTab); };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(icon);
            panel.Children.Add(title);
            panel.Children.Add(closeBtn);

            var border = new Border
            {
                Child = panel,
                Background = isActive ? activeBackground : transparentBackground,
                BorderBrush = isActive ? activeBorderBrush : transparentBackground,
                BorderThickness = isActive ? new Thickness(1) : new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 4, 0),
                Height = 32,
                MinWidth = 72,
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true
            }

            border.MouseEnter += (s, e) =>
            {
                if (capturedTab != _activeTab)
                {
                    border.Background = hoverBackground;
                }

                closeBtn.Opacity = 1;
            };

            border.MouseLeave += (s, e) =>
            {
                if (capturedTab != _activeTab)
                {
                    border.Background = transparentBackground;
                }

                closeBtn.Opacity = capturedTab == _activeTab ? 1 : 0.72;
            };

            border.MouseLeftButtonDown += (s, e) => ActivateTab(capturedTab);

            // Middle-click to close
            border.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle && _tabs.Count > 1)
                {
                    e.Handled = true;
                    CloseTab(capturedTab);
                }
            };

            return border;
        }

        // 鈹€鈹€鈹€ Navigation (operates on active tab's frame) 鈹€鈹€

        private void Frame_Navigated(object sender, NavigationEventArgs e)
        {
            if (sender == _activeTab?.Frame)
            {
                UpdateNavButtons();
                UpdateActiveTabInfo();
            }
        }

        private void UpdateNavButtons()
        {
            var frame = ActiveFrame;
            NavBackButton.IsEnabled = frame?.CanGoBack == true;
            NavForwardButton.IsEnabled = frame?.CanGoForward == true;
        }

        private async void NavBack_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFrame?.Content is EditorPage editor)
            {
                var saved = await editor.AutoSaveAsync();
                if (saved) ShowToast(LocalizationService.Get("Main.FileAutoSaved"));
            }
            if (ActiveFrame?.CanGoBack == true)
            {
                ActiveFrame.GoBack();
            }
        }

        private void NavForward_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFrame?.CanGoForward == true)
            {
                ActiveFrame.GoForward();
            }
        }

        private async void NavHome_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFrame == null) return;
            if (ActiveFrame.Content is EditorPage editor)
            {
                var saved = await editor.AutoSaveAsync();
                if (saved) ShowToast(LocalizationService.Get("Main.FileAutoSaved"));
            }
            ActiveFrame.Navigate(new HomePage());
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            AddNewHomeTab(activate: true);
        }

        private void UpdateActiveTabInfo()
        {
            if (_activeTab == null) return;
            if (ActiveFrame?.Content is HomePage)
            {
                _activeTab.Title = GetHomeTabTitle();
                _activeTab.Icon = "\uE80F";
                _activeTab.FilePath = null;
            }
            else if (ActiveFrame?.Content is EditorPage ep && !string.IsNullOrEmpty(ep.CurrentPdfPath))
            {
                _activeTab.Title = Path.GetFileNameWithoutExtension(ep.CurrentPdfPath);
                _activeTab.FilePath = ep.CurrentPdfPath;
                _activeTab.Icon = Path.GetExtension(ep.CurrentPdfPath).ToLowerInvariant() == ".pdf" ? "\uEA90" : "\uE7C3";
            }
            RebuildTabBar();
        }

        // Called by HomePage/EditorPage to navigate the current tab to a file
        public void NavigateActiveTabToFile(string filePath)
        {
            if (_activeTab == null) return;

            RecentFilesService.AddOrPromote(filePath);

            // Check if already open in another tab
            var existing = _tabs.FirstOrDefault(t =>
                t != _activeTab &&
                !string.IsNullOrEmpty(t.FilePath) &&
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ActivateTab(existing);
                return;
            }

            var name = Path.GetFileNameWithoutExtension(filePath);
            _activeTab.Title = name;
            _activeTab.FilePath = filePath;
            _activeTab.Icon = Path.GetExtension(filePath).ToLowerInvariant() == ".pdf" ? "\uEA90" : "\uE7C3";
            ActiveFrame?.Navigate(new EditorPage(filePath));
            RebuildTabBar();
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.T)
                {
                    AddNewHomeTab(activate: true);
                    e.Handled = true;
                }
                else if (e.Key == Key.W && _tabs.Count > 0)
                {
                    CloseTab(_activeTab);
                    e.Handled = true;
                }
            }
        }

        // 鈹€鈹€鈹€ Window State 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Auto-save all open editor tabs
            foreach (var tab in _tabs)
            {
                if (tab.Frame?.Content is EditorPage editor)
                {
                    await editor.AutoSaveAsync();
                }
            }
            base.OnClosing(e);
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;

            // Enforce transparent icon at Win32 level
            try
            {
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app-icon.ico");
                if (File.Exists(iconPath))
                {
                    const int IMAGE_ICON = 1;
                    const int LR_LOADFROMFILE = 0x00000010;
                    const int LR_SHARED = 0x00008000;
                    const int WM_SETICON = 0x0080;
                    const int ICON_SMALL = 0;
                    const int ICON_BIG = 1;

                    // Fetch the absolute maximum icons internally to force extreme clarity and size
                    IntPtr hIconSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 64, 64, LR_LOADFROMFILE | LR_SHARED);
                    IntPtr hIconBig = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 256, 256, LR_LOADFROMFILE | LR_SHARED);

                    if (hIconSmall != IntPtr.Zero) SendMessage(handle, WM_SETICON, (IntPtr)ICON_SMALL, hIconSmall);
                    if (hIconBig != IntPtr.Zero) SendMessage(handle, WM_SETICON, (IntPtr)ICON_BIG, hIconBig);
                }
            }
            catch { }

            EnableAcrylicBlur(handle);

            // Allow drag-drop messages through UIPI (fixes blocked drops when running elevated)
            ChangeWindowMessageFilterEx(handle, 0x0233, 1, IntPtr.Zero); // WM_DROPFILES
            ChangeWindowMessageFilterEx(handle, 0x004A, 1, IntPtr.Zero); // WM_COPYDATA
            ChangeWindowMessageFilterEx(handle, 0x0049, 1, IntPtr.Zero); // WM_COPYGLOBALDATA
            DragDrop.AddPreviewDropHandler(this, Window_Drop);
            DragDrop.AddPreviewDragOverHandler(this, Window_DragOver);
            DragDrop.AddPreviewDragEnterHandler(this, Window_DragEnter);
        }



        // 鈹€鈹€鈹€ Header Toolbar Buttons 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ActiveFrame?.Content is HomePage home)
            {
                home.Filter(SearchBox.Text);
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFrame?.Content is HomePage home)
            {
                home.ToggleSelectionMode();
                ShowToast(home.IsSelectionMode ? LocalizationService.Get("Main.SelectionEnabled") : LocalizationService.Get("Main.SelectionDisabled"), "\uE762");
                return;
            }

            if (ActiveFrame?.Content is EditorPage editor)
            {
                editor.ToggleSelectionMode();
                ShowToast(editor.IsSelectionMode ? LocalizationService.Get("Main.SelectionEnabled") : LocalizationService.Get("Main.SelectionDisabled"), "\uE762");
            }
        }

        private void SortButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void SortByName_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFrame?.Content is HomePage home)
            {
                home.SortByName();
            }
        }

        private void SortByDate_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFrame?.Content is HomePage home)
            {
                home.SortByDate();
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsDialog();
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            await DialogService.ShowInfoAsync(this, LocalizationService.Get("Main.AboutTitle"), LocalizationService.Get("Main.AboutMessage"));
        }

        // 鈹€鈹€鈹€ Toast 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        public async void ShowToast(string message, string icon = "\uE73E", int durationMs = 2500)
        {
            ToastIcon.Text = icon;
            ToastText.Text = message;
            ToastBorder.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
            fadeIn.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            ToastBorder.BeginAnimation(OpacityProperty, fadeIn);

            await Task.Delay(durationMs);

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
            fadeOut.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
            fadeOut.Completed += (s, ev) => ToastBorder.Visibility = Visibility.Collapsed;
            ToastBorder.BeginAnimation(OpacityProperty, fadeOut);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, int uType, int cxDesired, int cyDesired, int fuLoad);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

        // 鈹€鈹€鈹€ Acrylic Blur (Glassmorphism) 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        private void EnableAcrylicBlur(IntPtr handle)
        {
            // Solid white background — no acrylic/DWM backdrop needed
            int backdropType = 1; // DWMWCP_DEFAULT
            DwmSetWindowAttribute(handle, 38, ref backdropType, Marshal.SizeOf(typeof(int)));
        }
    }
}









