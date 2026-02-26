using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using WindowsNotesApp.Pages;

namespace WindowsNotesApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += MainWindow_SourceInitialized;
            StateChanged += MainWindow_StateChanged;
            RootFrame.Navigate(new HomePage());
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                MaximizeIcon.Text = "\uE923";
            }
            else
            {
                MaximizeIcon.Text = "\uE922";
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            EnableAcrylicBlur(handle);
        }

        private async void NavBack_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.Content is EditorPage editor)
            {
                var saved = await editor.AutoSaveAsync();
                if (saved) ShowToast("File auto saved");
            }
            if (RootFrame.CanGoBack) RootFrame.GoBack();
        }

        private void NavForward_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.CanGoForward) RootFrame.GoForward();
        }

        private async void NavHome_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.Content is EditorPage editor)
            {
                var saved = await editor.AutoSaveAsync();
                if (saved) ShowToast("File auto saved");
            }
            RootFrame.Navigate(new HomePage());
        }

        private void RootFrame_Navigated(object sender, NavigationEventArgs e)
        {
            NavBackButton.IsEnabled = RootFrame.CanGoBack;
            NavForwardButton.IsEnabled = RootFrame.CanGoForward;
        }

        // ─── Header Toolbar Buttons ────────────────────
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (RootFrame.Content is HomePage home)
            {
                home.Filter(SearchBox.Text);
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.Content is HomePage home)
            {
                home.ToggleSelectionMode();
                ShowToast(home.IsSelectionMode ? "Selection mode enabled" : "Selection mode disabled", "\uE762");
            }
            else
            {
                ShowToast("Select mode coming soon", "\uE762");
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
            if (RootFrame.Content is HomePage home)
            {
                home.SortByName();
            }
        }

        private void SortByDate_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.Content is HomePage home)
            {
                home.SortByDate();
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            ShowToast("Settings coming soon", "\uE713");
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Windows Notes App v1.0\nA modern PDF annotation tool.", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ─── Toast ─────────────────────────────────────
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

        // ─── Acrylic Blur (Glassmorphism) ──────────────
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
            int backdropType = 3;
            DwmSetWindowAttribute(handle, 38, ref backdropType, Marshal.SizeOf(typeof(int)));

            var accent = new AccentPolicy
            {
                AccentState = 4,
                AccentFlags = 2,
                GradientColor = unchecked((int)0x99000000)
            };

            var accentSize = Marshal.SizeOf(accent);
            var accentPtr = Marshal.AllocHGlobal(accentSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            SetWindowCompositionAttribute(handle, ref data);
            Marshal.FreeHGlobal(accentPtr);
        }
    }
}
