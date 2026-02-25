using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Navigation;
using WindowsNotesApp.Pages;

namespace WindowsNotesApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            RootFrame.Navigate(new HomePage());
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ToggleMaximize()
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

        private void NavBack_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.CanGoBack)
                RootFrame.GoBack();
        }

        private void NavForward_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame.CanGoForward)
                RootFrame.GoForward();
        }

        private void NavHome_Click(object sender, RoutedEventArgs e)
        {
            RootFrame.Navigate(new HomePage());
        }

        private void RootFrame_Navigated(object sender, NavigationEventArgs e)
        {
            NavBackButton.IsEnabled = RootFrame.CanGoBack;
            NavForwardButton.IsEnabled = RootFrame.CanGoForward;
        }
    }
}
