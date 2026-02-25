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
            var popup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            var panel = new StackPanel { Margin = new Thickness(16) };
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 4,
                    Opacity = 0.12,
                    Color = Colors.Black
                }
            };

            // Size section
            var sizeHeader = new TextBlock
            {
                Text = sizeLabel,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                TickFrequency = step,
                Width = 240,
                IsSnapToTickEnabled = true
            };
            slider.ValueChanged += (s, e) => sizeChanged?.Invoke(e.NewValue);
            panel.Children.Add(sizeHeader);
            panel.Children.Add(slider);

            if (colorLabel != null)
            {
                // Separator
                var separator = new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromArgb(25, 0, 0, 0)),
                    Margin = new Thickness(-16, 14, -16, 14)
                };
                panel.Children.Add(separator);

                var colorHeader = new TextBlock
                {
                    Text = colorLabel,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                panel.Children.Add(colorHeader);

                // HSV color palette grid
                int cols = 12;
                int rows = 8;
                double cellSize = 20;
                var paletteGrid = new Grid { Width = cols * cellSize, Height = rows * cellSize, ClipToBounds = true };

                // Selection indicator border (drawn on top of palette)
                var selectionIndicator = new Border
                {
                    Width = cellSize,
                    Height = cellSize,
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(2),
                    Background = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };

                for (int row = 0; row < rows; row++)
                {
                    for (int col = 0; col < cols; col++)
                    {
                        Color cellColor;
                        if (row == 0)
                        {
                            // Top row: grayscale from black to white
                            byte gray = (byte)(col * 255 / (cols - 1));
                            cellColor = Color.FromRgb(gray, gray, gray);
                        }
                        else
                        {
                            // HSV palette: hue across columns, saturation/value down rows
                            double hue = col * 360.0 / cols;
                            double saturation = 1.0;
                            double val = 1.0;
                            if (row <= rows / 2)
                            {
                                // Top half: full value, varying saturation (light → saturated)
                                saturation = (double)row / (rows / 2);
                            }
                            else
                            {
                                // Bottom half: full saturation, decreasing value (saturated → dark)
                                val = 1.0 - (double)(row - rows / 2) / (rows / 2);
                            }
                            cellColor = HsvToColor(hue, saturation, val);
                        }

                        var cell = new Border
                        {
                            Width = cellSize,
                            Height = cellSize,
                            Background = new SolidColorBrush(cellColor),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(col * cellSize, row * cellSize, 0, 0),
                            Cursor = Cursors.Hand,
                            Tag = cellColor
                        };

                        cell.MouseLeftButtonDown += (s, e) =>
                        {
                            var b = s as Border;
                            var picked = (Color)b.Tag;
                            selectionIndicator.Margin = b.Margin;
                            selectionIndicator.Visibility = Visibility.Visible;
                            colorChanged?.Invoke(picked);
                            e.Handled = true;
                        };

                        paletteGrid.Children.Add(cell);
                    }
                }

                // Place selection indicator on initial color if found
                foreach (Border cell in paletteGrid.Children)
                {
                    if (cell.Tag is Color c && c == initialColor)
                    {
                        selectionIndicator.Margin = cell.Margin;
                        selectionIndicator.Visibility = Visibility.Visible;
                        break;
                    }
                }

                paletteGrid.Children.Add(selectionIndicator);
                panel.Children.Add(paletteGrid);
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

            UpdateToolIconColors();
            ApplyToolToAllPages();
        }

        private void UpdateToolIconColors()
        {
            PenIcon.Foreground = new SolidColorBrush(_penColor);
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
            _textBoxPopup = new Popup { Placement = PlacementMode.Top, StaysOpen = true, AllowsTransparency = true, VerticalOffset = -6 };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 3,
                    Opacity = 0.12,
                    Color = Colors.Black
                }
            };

            var deleteButton = new Button { Content = "删除", MinWidth = 50, Height = 28, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, FontSize = 12 };
            deleteButton.Click += (s, e) => DeleteSelectedTextBox();

            _fontSizeComboBox = new ComboBox { Width = 70, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var size in new[] { 12, 18, 24, 36, 48, 72 })
                _fontSizeComboBox.Items.Add(size.ToString());
            _fontSizeComboBox.SelectionChanged += FontSizeComboBox_SelectionChanged;

            _colorIndicator = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(_textColor) };
            var colorButton = new Button { Content = _colorIndicator, MinWidth = 36, Height = 28, Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            var colorPopup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            var colorPanel = new WrapPanel { Margin = new Thickness(10), Width = 180 };
            var colorBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = colorPanel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.12, Color = Colors.Black }
            };

            var textColors = new[] { Colors.Black, Color.FromRgb(220, 38, 38), Color.FromRgb(37, 99, 235), Color.FromRgb(22, 163, 74), Color.FromRgb(245, 158, 11), Color.FromRgb(147, 51, 234), Color.FromRgb(127, 29, 29) };
            foreach (var c in textColors)
            {
                var swatch = new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Background = new SolidColorBrush(c),
                    Margin = new Thickness(3),
                    Cursor = Cursors.Hand,
                    Tag = c
                };
                swatch.MouseLeftButtonDown += (s, e) =>
                {
                    if (_selectedTextBox != null)
                    {
                        var sw = s as Border;
                        var selectedColor = (Color)sw.Tag;
                        _selectedTextBox.Foreground = new SolidColorBrush(selectedColor);
                        _textColor = selectedColor;
                        _colorIndicator.Background = new SolidColorBrush(selectedColor);
                        MarkDirty();
                    }
                    colorPopup.IsOpen = false;
                    e.Handled = true;
                };
                colorPanel.Children.Add(swatch);
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

        // Popup no longer auto-deselects. Deselection happens via:
        // - Clicking on canvas background
        // - Switching tools
        // - Clicking outside in PageControl_BackgroundPointerPressed

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
            _textBoxPopup.PlacementTarget = textBox.Parent as UIElement ?? textBox;
            _textBoxPopup.IsOpen = true;
            textBox.Focus();
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
                Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Height = 26,
                Visibility = select ? Visibility.Visible : Visibility.Collapsed,
                Cursor = Cursors.SizeAll
            };

            var dragIcon = new TextBlock
            {
                Text = "\uE7C2",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dragHandle.Child = dragIcon;

            var textBox = new TextBox
            {
                Text = text ?? "Text",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 120,
                MinHeight = 36,
                BorderThickness = new Thickness(select ? 1 : 0),
                BorderBrush = select ? new SolidColorBrush(Color.FromRgb(0, 120, 212)) : Brushes.Transparent,
                Background = Brushes.Transparent,
                FontSize = fontSize ?? _currentFontSize,
                Foreground = new SolidColorBrush(color ?? _textColor),
                IsReadOnly = !select,
                Padding = new Thickness(8, 6, 8, 6)
            };

            Grid.SetRow(dragHandle, 0);
            Grid.SetRow(textBox, 1);

            container.Children.Add(dragHandle);
            container.Children.Add(textBox);

            Canvas.SetLeft(container, position.X);
            Canvas.SetTop(container, position.Y);
            Panel.SetZIndex(container, 1000);

            dragHandle.MouseLeftButtonDown += DragHandle_MouseLeftButtonDown;
            dragHandle.MouseMove += DragHandle_MouseMove;
            dragHandle.MouseLeftButtonUp += DragHandle_MouseLeftButtonUp;

            textBox.TextChanged += (s, e) => MarkDirty();
            textBox.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (_currentTool == ToolType.Text)
                {
                    SelectTextBox((TextBox)s);
                }
            };
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
                textBox.Focus();
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
                    _textBoxPopup.PlacementTarget = _draggedContainer;
                    _textBoxPopup.IsOpen = true;
                    tb.Focus();
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

        private static Color HsvToColor(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255));
        }
    }
}
