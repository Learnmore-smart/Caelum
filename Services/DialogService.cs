using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Caelum.Services
{
    public static class DialogService
    {
        public static async System.Threading.Tasks.Task ShowInfoAsync(Window owner, string title, string content)
        {
            await ShowDialogAsync(owner, title, content, null, LocalizationService.Get("Common.OK"));
        }

        public static async System.Threading.Tasks.Task ShowErrorAsync(Window owner, string title, string content)
        {
            await ShowDialogAsync(owner, title, content, null, LocalizationService.Get("Common.OK"));
        }

        public static async System.Threading.Tasks.Task<bool?> ShowDialogAsync(
            Window owner,
            string title,
            string content,
            string cancelButtonText = null,
            string okButtonText = null)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 520,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false
            };

            var mainBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 248, 251)),
                CornerRadius = new CornerRadius(22),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 226)),
                Padding = new Thickness(24)
            };

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Padding = new Thickness(0)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            scrollViewer.Content = grid;
            mainBorder.Child = scrollViewer;
            dialog.MouseLeftButtonDown += (s, ev) => { dialog.DragMove(); };

            // Header with title and close button
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStackPanel = new StackPanel();
            var titleLabel = new TextBlock
            {
                Text = title,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
            };
            titleStackPanel.Children.Add(titleLabel);
            Grid.SetColumn(titleStackPanel, 0);
            headerGrid.Children.Add(titleStackPanel);

            // Close button
            var closeButton = new Button
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(12, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = new TextBlock
                {
                    Text = "\xE8BB",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99))
                }
            };
            var closeButtonTemplate = new ControlTemplate(typeof(Button));
            var templateFactory = new FrameworkElementFactory(typeof(Border));
            templateFactory.Name = "Root";
            templateFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            templateFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            templateFactory.AppendChild(contentPresenter);
            closeButtonTemplate.VisualTree = templateFactory;
            closeButton.Template = closeButtonTemplate;
            closeButton.Click += (s, ev) =>
            {
                dialog.DialogResult = false;
            };
            Grid.SetColumn(closeButton, 1);
            headerGrid.Children.Add(closeButton);

            Grid.SetRow(headerGrid, 0);
            grid.Children.Add(headerGrid);

            var contentText = new TextBlock
            {
                Text = content,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 22, 0, 0)
            };
            Grid.SetRow(contentText, 1);
            grid.Children.Add(contentText);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 16, 0, 0)
            };
            Grid.SetRow(btnPanel, 2);

            bool? result = null;

            if (!string.IsNullOrEmpty(cancelButtonText))
            {
                var cancelBtn = new Button
                {
                    Content = cancelButtonText,
                    Margin = new Thickness(0, 0, 10, 0),
                    IsCancel = true
                };
                var secStyle = Application.Current.TryFindResource("DialogSecondaryButton") as Style;
                if (secStyle != null)
                {
                    cancelBtn.Style = secStyle;
                }
                cancelBtn.Click += (s, ev) =>
                {
                    result = false;
                    dialog.DialogResult = false;
                };
                btnPanel.Children.Add(cancelBtn);
            }

            if (!string.IsNullOrEmpty(okButtonText))
            {
                var okBtn = new Button
                {
                    Content = okButtonText,
                    IsDefault = true
                };
                var priStyle = Application.Current.TryFindResource("DialogPrimaryButton") as Style;
                if (priStyle != null)
                {
                    okBtn.Style = priStyle;
                }
                okBtn.Click += (s, ev) =>
                {
                    result = true;
                    dialog.DialogResult = true;
                };
                btnPanel.Children.Add(okBtn);
            }

            grid.Children.Add(btnPanel);

            dialog.Content = mainBorder;
            dialog.ShowDialog();

            await System.Threading.Tasks.Task.CompletedTask;
            return result;
        }
    }
}
