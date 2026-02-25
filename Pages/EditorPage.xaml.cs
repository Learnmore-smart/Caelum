using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsNotesApp.Controls;
using WindowsNotesApp.Models;
using WindowsNotesApp.Services;

namespace WindowsNotesApp.Pages
{
    public sealed partial class EditorPage : Page
    {
        private enum ToolType { None, Pen, Highlighter, Eraser, Text }
        private enum UnsavedChangesChoice { Save, Discard, Cancel }

        private ToolType _currentTool = ToolType.None;
        private Color _penColor = Colors.Black;
        private Color _highlighterColor = Colors.Yellow;
        private Color _textColor = Colors.Black;
        private double _penSize = 2.0;
        private double _highlighterSize = 12.0;
        private double _eraserSize = 20.0;
        private double _currentFontSize = 18.0;
        private bool _isUpdatingToolState;

        private readonly PdfService _pdfService;
        private CancellationTokenSource _loadCts;
        private int _loadSessionId;
        private bool _isDirty;
        private string _currentPdfPath;

        private TextBox _selectedTextBox;
        private Popup _textBoxPopup;
        private ComboBox _fontSizeComboBox;
        private Border _colorIndicator;

        private Popup _penPopup;
        private Popup _highlighterPopup;
        private Popup _eraserPopup;

        private bool _isDragging;
        private bool _dragArmed;
        private Point _dragStartOffset;
        private Point _dragPressPointOnCanvas;
        private Grid _draggedContainer;
        private double _dragStartX;
        private double _dragStartY;

        public EditorPage()
        {
            InitializeComponent();
            InitializeTextBoxPopup();
            CreateToolPopups();

            _pdfService = new PdfService();
            ActivateTool(ToolType.None);

            KeyDown += EditorPage_KeyDown;
        }

        public EditorPage(string filePath) : this()
        {
            _currentPdfPath = filePath;
            Loaded += async (s, e) => await LoadPdfAsync(filePath);
        }

        private void EditorPage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ActivateTool(ToolType.None);
                e.Handled = true;
            }
        }

        private void CreateToolPopups()
        {
            _penPopup = BuildToolPopup(
                "Size", 1, 20, _penSize, 0.5,
                v => { _penSize = v; if (_currentTool == ToolType.Pen) ApplyToolToAllPages(); },
                "Color", _penColor,
                c => { _penColor = c; UpdateToolIconColors(); if (_currentTool == ToolType.Pen) ApplyToolToAllPages(); });

            _highlighterPopup = BuildToolPopup(
                "Size", 4, 40, _highlighterSize, 1,
                v => { _highlighterSize = v; if (_currentTool == ToolType.Highlighter) ApplyToolToAllPages(); },
                "Color", _highlighterColor,
                c => { _highlighterColor = c; UpdateToolIconColors(); if (_currentTool == ToolType.Highlighter) ApplyToolToAllPages(); });

            _eraserPopup = BuildToolPopup(
                "Eraser Size", 4, 80, _eraserSize, 1,
                v => { _eraserSize = v; ApplyToolToAllPages(); },
                null, default, null);
        }

        private Popup BuildToolPopup(
            string sizeLabel, double min, double max, double value, double step, Action<double> sizeChanged,
            string colorLabel, Color initialColor, Action<Color> colorChanged)
        {
            var popup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = false };
            var panel = new StackPanel { Margin = new Thickness(12) };
            var border = new Border { Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel };

            var sizeHeader = new TextBlock { Text = sizeLabel, FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, Width = 200 };
            slider.ValueChanged += (s, e) => sizeChanged?.Invoke(e.NewValue);
            panel.Children.Add(sizeHeader);
            panel.Children.Add(slider);

            if (colorLabel != null)
            {
                var colorHeader = new TextBlock { Text = colorLabel, FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 8) };
                panel.Children.Add(colorHeader);

                var colorGrid = new WrapPanel { Width = 200 };
                var colors = new[] { Colors.Black, Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple, Colors.Brown, Colors.Gray };
                foreach (var c in colors)
                {
                    var colorBtn = new Button
                    {
                        Width = 24,
                        Height = 24,
                        Margin = new Thickness(2),
                        Background = new SolidColorBrush(c),
                        Tag = c,
                        Cursor = Cursors.Hand
                    };
                    colorBtn.Click += (s, e) =>
                    {
                        var btn = s as Button;
                        var selectedColor = (Color)btn.Tag;
                        colorChanged?.Invoke(selectedColor);
                    };
                    colorGrid.Children.Add(colorBtn);
                }
                panel.Children.Add(colorGrid);
            }

            popup.Child = border;
            return popup;
        }

        public async Task LoadPdfAsync(string filePath)
        {
            _currentPdfPath = filePath;
            await LoadPdf(filePath);
        }

        private async Task LoadPdf(string filePath)
        {
            CancelActiveLoad();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            var sessionId = Interlocked.Increment(ref _loadSessionId);

            ShowLoadingOverlay();
            DetachAllPageControlEvents();
            PagesContainer.Children.Clear();
            DeselectTextBox();
            _isDirty = false;

            try
            {
                await _pdfService.LoadPdfAsync(filePath, token);

                for (int i = 0; i < _pdfService.PageCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var imageBytes = await _pdfService.RenderPagePngBytesAsync(i, token);
                    if (imageBytes == null || imageBytes.Length == 0)
                        throw new InvalidOperationException($"Failed to render page {i + 1}.");

                    token.ThrowIfCancellationRequested();
                    var image = await CreateBitmapImageAsync(imageBytes, token);

                    var pageControl = new PdfPageControl
                    {
                        PageSource = image,
                        PageIndex = (int)i,
                        Width = image.PixelWidth,
                        Height = image.PixelHeight
                    };

                    pageControl.TextOverlayPointerPressed += PageControl_TextOverlayPointerPressed;
                    pageControl.BackgroundPointerPressed += PageControl_BackgroundPointerPressed;
                    pageControl.InkMutated += PageControl_InkMutated;

                    PagesContainer.Children.Add(pageControl);
                }

                ApplyToolToAllPages();

                if (!string.IsNullOrEmpty(_currentPdfPath))
                {
                    await LoadAnnotationsFromPdfServiceAsync();
                }
            }
            catch (OperationCanceledException)
            {
                if (sessionId == _loadSessionId)
                    MessageBox.Show("Loading document was canceled.", "Canceled", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to load PDF: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMsg += $"\n\nDetails: {ex.InnerException.Message}";
                }

                if (sessionId == _loadSessionId)
                    MessageBox.Show(errorMsg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (sessionId == _loadSessionId)
                    HideLoadingOverlay();
            }
        }

        private void PenToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            var btn = sender as ToggleButton;
            if (btn?.IsChecked == true)
            {
                ActivateTool(ToolType.Pen);
                _penPopup.PlacementTarget = PenToolButton;
                _penPopup.IsOpen = true;
            }
            else
                ActivateTool(ToolType.None);
        }

        private void HighlighterToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            var btn = sender as ToggleButton;
            if (btn?.IsChecked == true)
            {
                ActivateTool(ToolType.Highlighter);
                _highlighterPopup.PlacementTarget = HighlighterToolButton;
                _highlighterPopup.IsOpen = true;
            }
            else
                ActivateTool(ToolType.None);
        }

        private void EraserToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            var btn = sender as ToggleButton;
            if (btn?.IsChecked == true)
            {
                ActivateTool(ToolType.Eraser);
                _eraserPopup.PlacementTarget = EraserToolButton;
                _eraserPopup.IsOpen = true;
            }
            else
                ActivateTool(ToolType.None);
        }

        private void TextToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            var btn = sender as ToggleButton;
            ActivateTool(btn?.IsChecked == true ? ToolType.Text : ToolType.None);
        }

        private void ActivateTool(ToolType tool)
        {
            if (tool != ToolType.Text)
                DeselectTextBox();

            _isUpdatingToolState = true;
            _currentTool = tool;
            PenToolButton.IsChecked = tool == ToolType.Pen;
            HighlighterToolButton.IsChecked = tool == ToolType.Highlighter;
            EraserToolButton.IsChecked = tool == ToolType.Eraser;
            TextToolButton.IsChecked = tool == ToolType.Text;
            _isUpdatingToolState = false;

            PenActiveIndicator.Visibility = tool == ToolType.Pen ? Visibility.Visible : Visibility.Collapsed;
            HighlighterActiveIndicator.Visibility = tool == ToolType.Highlighter ? Visibility.Visible : Visibility.Collapsed;
            EraserActiveIndicator.Visibility = tool == ToolType.Eraser ? Visibility.Visible : Visibility.Collapsed;
            TextActiveIndicator.Visibility = tool == ToolType.Text ? Visibility.Visible : Visibility.Collapsed;

            PenToolButton.Style = tool == ToolType.Pen
                ? Application.Current.Resources["ActiveToolbarToggleButtonStyle"] as Style
                : Application.Current.Resources["ToolbarToggleButtonStyle"] as Style;
            HighlighterToolButton.Style = tool == ToolType.Highlighter
                ? Application.Current.Resources["ActiveToolbarToggleButtonStyle"] as Style
                : Application.Current.Resources["ToolbarToggleButtonStyle"] as Style;
            EraserToolButton.Style = tool == ToolType.Eraser
                ? Application.Current.Resources["ActiveToolbarToggleButtonStyle"] as Style
                : Application.Current.Resources["ToolbarToggleButtonStyle"] as Style;
            TextToolButton.Style = tool == ToolType.Text
                ? Application.Current.Resources["ActiveToolbarToggleButtonStyle"] as Style
                : Application.Current.Resources["ToolbarToggleButtonStyle"] as Style;

            UpdateToolIconColors();
            ApplyToolToAllPages();
        }

        private void UpdateToolIconColors()
        {
            PenIcon.Foreground = new SolidColorBrush(_penColor);
            HighlighterIcon.Foreground = new SolidColorBrush(_highlighterColor);
        }

        private void ApplyToolToAllPages()
        {
            foreach (var child in PagesContainer.Children)
            {
                if (child is PdfPageControl page)
                {
                    page.SetMode(_currentTool == ToolType.Text);
                    var atts = page.CopyDefaultDrawingAttributes();

                    switch (_currentTool)
                    {
                        case ToolType.None:
                            page.SetInputMode(CustomInkInputProcessingMode.None);
                            break;
                        case ToolType.Pen:
                            page.SetInputMode(CustomInkInputProcessingMode.Inking);
                            atts.Color = _penColor;
                            atts.Width = _penSize;
                            atts.Height = _penSize;
                            atts.IsHighlighter = false;
                            page.SetInkAttributes(atts);
                            break;
                        case ToolType.Highlighter:
                            page.SetInputMode(CustomInkInputProcessingMode.Inking);
                            atts.Color = _highlighterColor;
                            atts.Width = _highlighterSize;
                            atts.Height = _highlighterSize;
                            atts.IsHighlighter = true;
                            page.SetInkAttributes(atts);
                            break;
                        case ToolType.Eraser:
                            page.SetInputMode(CustomInkInputProcessingMode.Erasing);
                            break;
                        case ToolType.Text:
                            page.SetInputMode(CustomInkInputProcessingMode.None);
                            break;
                    }

                    page.SetEraserSize(_eraserSize);
                }
            }
        }

        private async void Back_Click(object sender, RoutedEventArgs e)
        {
            await AttemptNavigateBackAsync();
        }

        private async void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            await SaveAnnotationsToPdfAsync();
        }

        private void InitializeTextBoxPopup()
        {
            _textBoxPopup = new Popup { Placement = PlacementMode.Top, StaysOpen = false };
            _textBoxPopup.Closed += TextBoxPopup_Closed;

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            var border = new Border { Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel };

            var deleteButton = new Button { Content = "Delete", MinWidth = 60, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            deleteButton.Click += (s, e) => DeleteSelectedTextBox();

            _fontSizeComboBox = new ComboBox { Width = 70, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var size in new[] { 12, 18, 24, 36, 48, 72 })
                _fontSizeComboBox.Items.Add(size.ToString());
            _fontSizeComboBox.SelectionChanged += FontSizeComboBox_SelectionChanged;

            _colorIndicator = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(_textColor) };
            var colorButton = new Button { Content = _colorIndicator, MinWidth = 40, Cursor = Cursors.Hand };
            var colorPopup = new Popup { Placement = PlacementMode.Bottom };
            var colorPanel = new WrapPanel { Margin = new Thickness(8), Width = 150 };
            var colorBorder = new Border { Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = colorPanel };

            var textColors = new[] { Colors.Black, Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple, Colors.Brown };
            foreach (var c in textColors)
            {
                var colorBtn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(c),
                    Tag = c,
                    Cursor = Cursors.Hand
                };
                colorBtn.Click += (s, e) =>
                {
                    if (_selectedTextBox != null)
                    {
                        var btn = s as Button;
                        var selectedColor = (Color)btn.Tag;
                        _selectedTextBox.Foreground = new SolidColorBrush(selectedColor);
                        _textColor = selectedColor;
                        _colorIndicator.Background = new SolidColorBrush(selectedColor);
                        MarkDirty();
                    }
                    colorPopup.IsOpen = false;
                };
                colorPanel.Children.Add(colorBtn);
            }

            colorPopup.Child = colorBorder;
            colorButton.Click += (s, e) =>
            {
                colorPopup.PlacementTarget = colorButton;
                colorPopup.IsOpen = true;
            };

            panel.Children.Add(deleteButton);
            panel.Children.Add(_fontSizeComboBox);
            panel.Children.Add(colorButton);

            _textBoxPopup.Child = border;
        }

        private void TextBoxPopup_Closed(object sender, EventArgs e)
        {
            if (_selectedTextBox == null) return;
            DeselectTextBox();
        }

        private void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedTextBox == null || _fontSizeComboBox.SelectedItem == null) return;
            if (double.TryParse(_fontSizeComboBox.SelectedItem.ToString(), out var size))
            {
                _selectedTextBox.FontSize = size;
                _currentFontSize = size;
                MarkDirty();
            }
        }

        private void DeleteSelectedTextBox()
        {
            if (_selectedTextBox == null) return;
            var tb = _selectedTextBox;
            DeselectTextBox();

            if (tb.Parent is Grid container && container.Parent is Panel panel1)
            {
                panel1.Children.Remove(container);
                MarkDirty();
            }
            else if (tb.Parent is Panel panel2)
            {
                panel2.Children.Remove(tb);
                MarkDirty();
            }
        }

        private void SelectTextBox(TextBox textBox)
        {
            if (_selectedTextBox != null && _selectedTextBox != textBox)
            {
                ApplyTextBoxChrome(_selectedTextBox, isSelected: false);
                _selectedTextBox.IsReadOnly = true;
            }

            _selectedTextBox = textBox;
            textBox.IsReadOnly = false;
            ApplyTextBoxChrome(textBox, isSelected: true);
            SyncPopupToSelectedTextBox();
            _textBoxPopup.PlacementTarget = textBox;
            _textBoxPopup.IsOpen = true;
        }

        private void DeselectTextBox()
        {
            if (_selectedTextBox == null) return;
            ApplyTextBoxChrome(_selectedTextBox, isSelected: false);
            _selectedTextBox.IsReadOnly = true;
            _selectedTextBox = null;
            _textBoxPopup.IsOpen = false;
        }

        private void ApplyTextBoxChrome(TextBox textBox, bool isSelected)
        {
            textBox.BorderThickness = isSelected ? new Thickness(1) : new Thickness(0);
            textBox.BorderBrush = isSelected ? Brushes.DodgerBlue : Brushes.Transparent;
            textBox.Background = Brushes.Transparent;

            if (textBox.Parent is Grid container && container.Children.Count > 0 && container.Children[0] is Border dragHandle)
            {
                dragHandle.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SyncPopupToSelectedTextBox()
        {
            if (_selectedTextBox == null) return;

            var size = _selectedTextBox.FontSize;
            var sizes = new[] { 12d, 18d, 24d, 36d, 48d, 72d };
            var nearest = sizes.OrderBy(s => Math.Abs(s - size)).First();

            _fontSizeComboBox.SelectedItem = nearest.ToString();

            var current = (_selectedTextBox.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
            _colorIndicator.Background = new SolidColorBrush(current);
        }

        private void PageControl_TextOverlayPointerPressed(object sender, MouseButtonEventArgs e)
        {
            if (_currentTool != ToolType.Text) return;

            var page = sender as PdfPageControl;
            if (page == null) return;
            var point = e.GetPosition(page.TextOverlay);

            if (_selectedTextBox != null)
            {
                DeselectTextBox();
                e.Handled = true;
                return;
            }

            CreateTextBox(page, point);
        }

        private void CreateTextBox(PdfPageControl page, Point position, Color? color = null, double? fontSize = null, string text = null, bool select = true)
        {
            var container = new Grid { Background = Brushes.Transparent };

            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var dragHandle = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Height = 24,
                Visibility = select ? Visibility.Visible : Visibility.Collapsed,
                Cursor = Cursors.SizeAll
            };

            var dragIcon = new TextBlock
            {
                Text = "\uE7C2",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dragHandle.Child = dragIcon;

            var textBox = new TextBox
            {
                Text = text ?? "Text",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 100,
                MinHeight = 30,
                BorderThickness = new Thickness(select ? 1 : 0),
                BorderBrush = select ? Brushes.DodgerBlue : Brushes.Transparent,
                Background = Brushes.Transparent,
                FontSize = fontSize ?? _currentFontSize,
                Foreground = new SolidColorBrush(color ?? _textColor),
                IsReadOnly = !select
            };

            Grid.SetRow(dragHandle, 0);
            Grid.SetRow(textBox, 1);

            container.Children.Add(dragHandle);
            container.Children.Add(textBox);

            Canvas.SetLeft(container, position.X);
            Canvas.SetTop(container, position.Y);

            dragHandle.MouseLeftButtonDown += DragHandle_MouseLeftButtonDown;
            dragHandle.MouseMove += DragHandle_MouseMove;
            dragHandle.MouseLeftButtonUp += DragHandle_MouseLeftButtonUp;

            textBox.TextChanged += (s, e) => MarkDirty();
            textBox.GotFocus += (s, e) =>
            {
                if (_currentTool == ToolType.Text)
                    SelectTextBox((TextBox)s);
            };

            page.TextOverlay.Children.Add(container);

            if (select)
            {
                SelectTextBox(textBox);
                textBox.SelectAll();
                MarkDirty();
            }
        }

        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentTool != ToolType.Text) return;
            var handle = sender as Border;
            if (handle?.Parent is Grid container && container.Children.Count > 1 && container.Children[1] is TextBox tb)
            {
                SelectTextBox(tb);
            }

            var pointOnHandle = e.GetPosition(handle);

            _dragArmed = true;
            _draggedContainer = handle?.Parent as Grid;
            _dragStartOffset = pointOnHandle;
            if (_draggedContainer?.Parent is Canvas canvas)
                _dragPressPointOnCanvas = e.GetPosition(canvas);
            if (_draggedContainer != null)
            {
                _dragStartX = Canvas.GetLeft(_draggedContainer);
                _dragStartY = Canvas.GetTop(_draggedContainer);
            }
            e.Handled = true;
        }

        private void DragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if ((!_isDragging && !_dragArmed) || _draggedContainer == null) return;
            var canvas = _draggedContainer.Parent as Canvas;
            if (canvas == null) return;

            var currentPoint = e.GetPosition(canvas);

            if (_dragArmed && !_isDragging)
            {
                var dx = currentPoint.X - _dragPressPointOnCanvas.X;
                var dy = currentPoint.Y - _dragPressPointOnCanvas.Y;
                if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
                {
                    _isDragging = true;
                    _dragArmed = false;
                    var handle = _draggedContainer.Children[0] as Border;
                    handle?.CaptureMouse();
                    _textBoxPopup.IsOpen = false;
                }
            }

            if (_isDragging)
            {
                var newX = currentPoint.X - _dragStartOffset.X;
                var newY = currentPoint.Y - _dragStartOffset.Y;
                Canvas.SetLeft(_draggedContainer, newX);
                Canvas.SetTop(_draggedContainer, newY);
                e.Handled = true;
            }
        }

        private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging && !_dragArmed) return;
            var wasDragging = _isDragging;
            _dragArmed = false;
            _isDragging = false;
            var handle = sender as Border;
            handle?.ReleaseMouseCapture();

            if (_draggedContainer != null)
            {
                var endX = Canvas.GetLeft(_draggedContainer);
                var endY = Canvas.GetTop(_draggedContainer);
                if (Math.Abs(endX - _dragStartX) > 0.5 || Math.Abs(endY - _dragStartY) > 0.5)
                    MarkDirty();
                if (wasDragging && _draggedContainer.Children.Count > 1 && _draggedContainer.Children[1] is TextBox tb)
                {
                    _textBoxPopup.PlacementTarget = tb;
                    _textBoxPopup.IsOpen = true;
                }
            }
            _draggedContainer = null;
            e.Handled = wasDragging;
        }

        private void PageControl_BackgroundPointerPressed(object sender, MouseButtonEventArgs e)
        {
            if (_selectedTextBox != null) DeselectTextBox();
        }

        private async Task SaveAnnotationsToPdfAsync()
        {
            if (string.IsNullOrEmpty(_currentPdfPath))
            {
                MessageBox.Show("No PDF is currently loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                ShowLoadingOverlay();
                var annotations = new Dictionary<int, PageAnnotation>();

                foreach (var child in PagesContainer.Children)
                {
                    if (child is PdfPageControl page)
                    {
                        var pa = new PageAnnotation();
                        pa.Strokes = page.GetStrokeData();

                        foreach (var element in page.TextOverlay.Children)
                        {
                            if (element is Grid container && container.Children.Count > 1 && container.Children[1] is TextBox containerTb)
                            {
                                var color = (containerTb.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
                                pa.Texts.Add(new TextAnnotation
                                {
                                    Text = containerTb.Text,
                                    X = Canvas.GetLeft(container),
                                    Y = Canvas.GetTop(container),
                                    R = color.R, G = color.G, B = color.B,
                                    FontSize = containerTb.FontSize
                                });
                            }
                            else if (element is TextBox tb)
                            {
                                var color = (tb.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
                                pa.Texts.Add(new TextAnnotation
                                {
                                    Text = tb.Text,
                                    X = Canvas.GetLeft(tb),
                                    Y = Canvas.GetTop(tb),
                                    R = color.R, G = color.G, B = color.B,
                                    FontSize = tb.FontSize
                                });
                            }
                        }

                        if (pa.Strokes.Count > 0 || pa.Texts.Count > 0)
                            annotations[page.PageIndex] = pa;
                    }
                }

                await _pdfService.SaveAnnotationsToPdfAsync(_currentPdfPath, annotations);
                _isDirty = false;

                HideLoadingOverlay();
                MessageBox.Show("Annotations saved to PDF successfully.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                HideLoadingOverlay();
                MessageBox.Show($"Failed to save annotations: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadAnnotationsFromPdfServiceAsync()
        {
            if (_pdfService.ExtractedAnnotations == null || _pdfService.ExtractedAnnotations.Count == 0) return;

            try
            {
                foreach (var child in PagesContainer.Children)
                {
                    if (child is PdfPageControl page && _pdfService.ExtractedAnnotations.TryGetValue(page.PageIndex, out var pa))
                    {
                        foreach (var sa in pa.Strokes)
                        {
                            page.AddStroke(sa);
                        }

                        foreach (var ta in pa.Texts)
                        {
                            var color = Color.FromRgb(ta.R, ta.G, ta.B);
                            CreateTextBox(page, new Point(ta.X, ta.Y), color: color, fontSize: ta.FontSize, text: ta.Text, select: false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadAnnotationsFromPdfServiceAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private void PageControl_InkMutated(object sender, EventArgs e) => MarkDirty();
        private void MarkDirty() => _isDirty = true;

        private async Task AttemptNavigateBackAsync()
        {
            if (!await EnsureCanLeaveAsync()) return;
            NavigateBackCore();
        }

        private void NavigateBackCore()
        {
            if (NavigationService != null && NavigationService.CanGoBack)
                NavigationService.GoBack();
            else if (NavigationService != null)
                NavigationService.Navigate(new HomePage());
        }

        private async Task<bool> EnsureCanLeaveAsync()
        {
            if (!_isDirty) return true;
            var choice = await ShowUnsavedChangesDialogAsync();
            if (choice == UnsavedChangesChoice.Save)
            {
                await SaveAnnotationsToPdfAsync();
                return !_isDirty;
            }
            if (choice == UnsavedChangesChoice.Discard) { _isDirty = false; return true; }
            return false;
        }

        private async Task<UnsavedChangesChoice> ShowUnsavedChangesDialogAsync()
        {
            var result = MessageBox.Show("You have unsaved changes. Save before leaving?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return result switch
            {
                MessageBoxResult.Yes => UnsavedChangesChoice.Save,
                MessageBoxResult.No => UnsavedChangesChoice.Discard,
                _ => UnsavedChangesChoice.Cancel
            };
        }

        private void ShowLoadingOverlay()
        {
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void HideLoadingOverlay()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void CancelActiveLoad()
        {
            try { Interlocked.Increment(ref _loadSessionId); _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
            _loadCts = null;
        }

        private void DetachAllPageControlEvents()
        {
            foreach (var child in PagesContainer.Children)
            {
                if (child is PdfPageControl pageControl)
                {
                    pageControl.TextOverlayPointerPressed -= PageControl_TextOverlayPointerPressed;
                    pageControl.BackgroundPointerPressed -= PageControl_BackgroundPointerPressed;
                    pageControl.InkMutated -= PageControl_InkMutated;
                }
            }
        }

        private static async Task<BitmapImage> CreateBitmapImageAsync(byte[] pngBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var image = new BitmapImage();
                using (var stream = new MemoryStream(pngBytes))
                {
                    stream.Position = 0;
                    image.BeginInit();
                    image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                }
                return image;
            });
        }
    }
}
