using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using WindowsNotesApp.Controls;
using WindowsNotesApp.Services;
using WinRT.Interop;
using Windows.UI.Input.Inking;

namespace WindowsNotesApp.Pages
{
    public sealed partial class EditorPage : Page
    {
        private enum ToolType { SelectPan, Pen, Highlighter, Eraser, Text }
        private enum UnsavedChangesChoice { Save, Discard, Cancel }

        private ToolType _currentTool = ToolType.SelectPan;
        private Color _currentColor = Colors.Black;
        private Color _highlighterColor = Colors.Yellow; // Default highlighter color
        private double _penSize = 2.0;
        private double _highlighterSize = 12.0;
        private double _eraserSize = 20.0;
        private double _currentFontSize = 18.0;
        private AppBarElementContainer _eraserSizeContainer;
        private Slider _eraserSizeSlider;

        private readonly PdfService _pdfService;
        private readonly HuaweiPenService _huaweiPenService;
        private IntPtr _hwnd;
        private CancellationTokenSource _loadCts;
        private int _loadSessionId;
        private Window _hostWindow;
        private AppWindow _appWindow;
        private bool _isDirty;
        private int _closePromptScheduled;
        private bool _allowClose;
        private bool _ignoreNextFrameNavigation;
        private readonly SemaphoreSlim _unsavedPromptLock = new SemaphoreSlim(1, 1);

        private readonly struct PendingFrameNavigation
        {
            public PendingFrameNavigation(NavigationMode mode, Type destinationPageType, object parameter, NavigationTransitionInfo transitionInfo)
            {
                Mode = mode;
                DestinationPageType = destinationPageType;
                Parameter = parameter;
                TransitionInfo = transitionInfo;
            }

            public NavigationMode Mode { get; }
            public Type DestinationPageType { get; }
            public object Parameter { get; }
            public NavigationTransitionInfo TransitionInfo { get; }
        }

        private double _dragStartX;
        private double _dragStartY;

        public EditorPage()
        {
            this.InitializeComponent();

            InitializeTextBoxFlyout();
            InitializeEraserSizeControl();
            
            var window = (Application.Current as App)?.m_window;
            if (window != null)
            {
                _hostWindow = window;
                _hwnd = WindowNative.GetWindowHandle(window);
            }

            _pdfService = new PdfService();
            _huaweiPenService = new HuaweiPenService();

            // Set default tool state
            UpdateToolState(ToolType.SelectPan);

            // Defer color picker init until loaded to avoid issues
            this.Loaded += EditorPage_Loaded;

            var goBack = new KeyboardAccelerator { Key = VirtualKey.GoBack };
            goBack.Invoked += KeyboardGoBack_Invoked;
            KeyboardAccelerators.Add(goBack);

            var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
            escape.Invoked += Escape_Invoked;
            KeyboardAccelerators.Add(escape);
        }

        private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            UpdateToolState(ToolType.SelectPan);
            args.Handled = true;
        }

        private void EditorPage_Loaded(object sender, RoutedEventArgs e)
        {
            try { ToolColorPicker.Color = _currentColor; } catch { }
            
            // Initialize services that need HWND
             try
            {
                if (_isHuaweiPenHooked)
                {
                    return;
                }

                var window = (Application.Current as App)?.m_window;
                if (window != null)
                {
                    _huaweiPenService.Initialize(window);
                    _huaweiPenService.ToolToggleRequested += OnHuaweiPenDoubleTap;
                    _isHuaweiPenHooked = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to init Huawei Pen Service: {ex.Message}");
            }
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

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Frame.Navigating += Frame_Navigating;
            TryAttachWindowCloseHandler();
            if (e.Parameter is StorageFile file)
            {
                await LoadPdf(file);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Frame.Navigating -= Frame_Navigating;
            DetachWindowCloseHandler();
            CancelActiveLoad();
            DeselectTextBox();
            DetachAllPageControlEvents();
            if (_isHuaweiPenHooked)
            {
                _huaweiPenService.ToolToggleRequested -= OnHuaweiPenDoubleTap;
                _isHuaweiPenHooked = false;
            }
            _huaweiPenService?.Uninitialize();
        }

        private async Task LoadPdf(StorageFile file)
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
                await _pdfService.LoadPdfAsync(file, token);

                for (uint i = 0; i < _pdfService.PageCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var imageBytes = await _pdfService.RenderPagePngBytesAsync(i, token);
                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        throw new InvalidOperationException($"Failed to render page {i + 1}.");
                    }
                    token.ThrowIfCancellationRequested();
                    var image = await CreateBitmapImageAsync(imageBytes, token);

                    var pageControl = new PdfPageControl
                    {
                        PageSource = image,
                        PageIndex = (int)i,
                        Width = image.PixelWidth, // Set size explicitly to match PDF render
                        Height = image.PixelHeight
                    };

                    // Subscribe to events
                    pageControl.TextOverlayPointerPressed += PageControl_TextOverlayPointerPressed;
                    pageControl.BackgroundPointerPressed += PageControl_BackgroundPointerPressed;
                    pageControl.InkMutated += PageControl_InkMutated;

                    PagesContainer.Children.Add(pageControl);
                }

                // Apply current tool settings to new pages
                ApplyToolToAllPages();
            }
            catch (OperationCanceledException)
            {
                if (sessionId == _loadSessionId)
                {
                    await ShowDialog("Canceled", "Loading document was canceled.");
                }
            }
            catch (Exception ex)
            {
                if (sessionId == _loadSessionId)
                {
                    await ShowDialog("Error", $"Failed to load PDF: {ex.Message}");
                }
            }
            finally
            {
                if (sessionId == _loadSessionId)
                {
                    await HideLoadingOverlayAsync();
                }
            }
        }

        private void OnHuaweiPenDoubleTap(object sender, EventArgs e)
        {
            // Toggle between Pen and Eraser
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentTool == ToolType.Pen || _currentTool == ToolType.Highlighter)
                {
                    UpdateToolState(ToolType.Eraser);
                }
                else if (_currentTool == ToolType.Eraser)
                {
                    UpdateToolState(ToolType.Pen); // Default back to pen
                }
            });
        }

        private async void OpenPdf_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureCanLeaveAsync())
            {
                return;
            }

            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".pdf");

            InitializeWithWindow.Initialize(picker, _hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                // Navigate to same page with new file or just reload?
                // Reloading here directly
                await LoadPdf(file);
            }
        }

        private async void Back_Click(object sender, RoutedEventArgs e)
        {
            await AttemptNavigateBackAsync();
        }

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is AppBarToggleButton button)
            {
                if (button == SelectToolButton) UpdateToolState(ToolType.SelectPan);
                else if (button == PenToolButton) UpdateToolState(ToolType.Pen);
                else if (button == HighlighterToolButton) UpdateToolState(ToolType.Highlighter);
                else if (button == EraserToolButton) UpdateToolState(ToolType.Eraser);
                else if (button == TextToolButton) UpdateToolState(ToolType.Text);
            }
        }

        private void UpdateToolState(ToolType newTool)
        {
            if (newTool != ToolType.Text)
            {
                DeselectTextBox();
            }

            _currentTool = newTool;

            // Update UI
            SelectToolButton.IsChecked = newTool == ToolType.SelectPan;
            PenToolButton.IsChecked = newTool == ToolType.Pen;
            HighlighterToolButton.IsChecked = newTool == ToolType.Highlighter;
            EraserToolButton.IsChecked = newTool == ToolType.Eraser;
            TextToolButton.IsChecked = newTool == ToolType.Text;
            if (_eraserSizeContainer != null)
            {
                _eraserSizeContainer.Visibility = newTool == ToolType.Eraser ? Visibility.Visible : Visibility.Collapsed;
            }

            ApplyToolToAllPages();
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
                        case ToolType.SelectPan:
                            page.SetInputMode(InkInputProcessingMode.None);
                            break;
                        case ToolType.Pen:
                            page.SetInputMode(InkInputProcessingMode.Inking);
                            atts.Color = _currentColor;
                            atts.Size = new Windows.Foundation.Size(_penSize, _penSize);
                            atts.DrawAsHighlighter = false;
                            page.SetInkAttributes(atts);
                            break;
                        case ToolType.Highlighter:
                            page.SetInputMode(InkInputProcessingMode.Inking);
                            atts.Color = _highlighterColor;
                            atts.Size = new Windows.Foundation.Size(_highlighterSize, _highlighterSize);
                            atts.DrawAsHighlighter = true;
                            page.SetInkAttributes(atts);
                            break;
                        case ToolType.Eraser:
                            page.SetInputMode(InkInputProcessingMode.Erasing);
                            break;
                        case ToolType.Text:
                            page.SetInputMode(InkInputProcessingMode.None);
                            break;
                    }

                    page.SetEraserSize(_eraserSize);
                }
            }
        }

        private void EraserSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _eraserSize = e.NewValue;
            ApplyToolToAllPages();
        }

        private void InitializeEraserSizeControl()
        {
            var commandBar = FindDescendant<CommandBar>(this);
            if (commandBar == null)
            {
                return;
            }

            var label = new TextBlock
            {
                Text = "Eraser size",
                VerticalAlignment = VerticalAlignment.Center
            };

            _eraserSizeSlider = new Slider
            {
                Width = 140,
                Minimum = 4,
                Maximum = 80,
                Value = _eraserSize
            };
            _eraserSizeSlider.ValueChanged += EraserSizeSlider_ValueChanged;

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(label);
            panel.Children.Add(_eraserSizeSlider);

            _eraserSizeContainer = new AppBarElementContainer
            {
                Content = panel,
                Visibility = Visibility.Collapsed
            };

            var insertIndex = FindCommandBarElementIndex(commandBar, "Color");
            if (insertIndex < 0)
            {
                commandBar.PrimaryCommands.Add(new AppBarSeparator());
                commandBar.PrimaryCommands.Add(_eraserSizeContainer);
                commandBar.PrimaryCommands.Add(new AppBarSeparator());
                return;
            }

            commandBar.PrimaryCommands.Insert(insertIndex, _eraserSizeContainer);
            commandBar.PrimaryCommands.Insert(insertIndex + 1, new AppBarSeparator());
        }

        private int FindCommandBarElementIndex(CommandBar commandBar, string label)
        {
            if (commandBar == null)
            {
                return -1;
            }

            for (var i = 0; i < commandBar.PrimaryCommands.Count; i++)
            {
                if (commandBar.PrimaryCommands[i] is AppBarButton button && string.Equals(button.Label, label, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    return typed;
                }

                var found = FindDescendant<T>(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void ToolColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            _currentColor = args.NewColor;

            if (_currentTool == ToolType.Pen)
            {
                ApplyToolToAllPages();
            }
            else if (_currentTool == ToolType.Highlighter)
            {
                _highlighterColor = args.NewColor;
                ApplyToolToAllPages();
            }
            else if (_currentTool == ToolType.Text && _selectedTextBox != null)
            {
                var oldColor = (_selectedTextBox.Foreground as SolidColorBrush)?.Color;
                if (oldColor != _currentColor)
                {
                    _selectedTextBox.Foreground = new SolidColorBrush(_currentColor);
                    MarkDirty();
                }
            }
        }

        private void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FontSizeComboBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Content.ToString(), out double size))
            {
                _currentFontSize = size;
                if (_selectedTextBox != null)
                {
                    if (Math.Abs(_selectedTextBox.FontSize - _currentFontSize) > 0.001)
                    {
                        _selectedTextBox.FontSize = _currentFontSize;
                        MarkDirty();
                    }
                }
            }
        }

        // --- Text Tool Logic ---

        private TextBox _selectedTextBox;
        private Flyout _textBoxFlyout;
        private ComboBox _textBoxFlyoutFontSizeComboBox;
        private Flyout _textBoxFlyoutColorFlyout;
        private ColorPicker _textBoxFlyoutColorPicker;
        private bool _ignoreNextFontSync;
        private bool _suppressFlyoutClosedDeselect;
        private bool _isHuaweiPenHooked;

        private void InitializeTextBoxFlyout()
        {
            _textBoxFlyout = new Flyout
            {
                Placement = FlyoutPlacementMode.Top
            };
            _textBoxFlyout.Closed += TextBoxFlyout_Closed;

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

            var deleteButton = new Button { Content = new SymbolIcon(Symbol.Delete), MinWidth = 40 };
            deleteButton.Click += (s, e) => DeleteSelectedTextBox();
            AutomationProperties.SetName(deleteButton, "Delete annotation");

            _textBoxFlyoutFontSizeComboBox = new ComboBox
            {
                Width = 90,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(_textBoxFlyoutFontSizeComboBox, "Font size");

            foreach (var size in new[] { 12, 18, 24, 36, 48, 72 })
            {
                _textBoxFlyoutFontSizeComboBox.Items.Add(new ComboBoxItem { Content = size.ToString() });
            }

            _textBoxFlyoutFontSizeComboBox.SelectionChanged += TextBoxFlyoutFontSizeComboBox_SelectionChanged;

            var colorButton = new Button { Content = new SymbolIcon(Symbol.FontColor), MinWidth = 40 };
            AutomationProperties.SetName(colorButton, "Text color");
            _textBoxFlyoutColorFlyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
            _textBoxFlyoutColorPicker = new ColorPicker
            {
                IsColorSpectrumVisible = true,
                IsColorChannelTextInputVisible = false,
                IsHexInputVisible = false
            };
            AutomationProperties.SetName(_textBoxFlyoutColorPicker, "Text color picker");
            _textBoxFlyoutColorPicker.ColorChanged += TextBoxFlyoutColorPicker_ColorChanged;
            _textBoxFlyoutColorFlyout.Content = _textBoxFlyoutColorPicker;
            colorButton.Flyout = _textBoxFlyoutColorFlyout;

            panel.Children.Add(deleteButton);
            panel.Children.Add(_textBoxFlyoutFontSizeComboBox);
            panel.Children.Add(colorButton);

            _textBoxFlyout.Content = panel;
        }

        private void TextBoxFlyout_Closed(object sender, object e)
        {
            if (_suppressFlyoutClosedDeselect || _selectedTextBox == null)
            {
                return;
            }

            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (focused != null && FindAncestorTextBox(focused) == _selectedTextBox)
            {
                return;
            }

            DeselectTextBox();
        }

        private void HideTextBoxFlyouts(bool suppressClosedDeselect)
        {
            if (suppressClosedDeselect)
            {
                _suppressFlyoutClosedDeselect = true;
            }

            try
            {
                _textBoxFlyoutColorFlyout?.Hide();
                _textBoxFlyout?.Hide();
            }
            finally
            {
                if (suppressClosedDeselect)
                {
                    DispatcherQueue.TryEnqueue(() => _suppressFlyoutClosedDeselect = false);
                }
            }
        }

        private void ShowTextBoxFlyoutAt(FrameworkElement target)
        {
            if (_textBoxFlyout == null || target == null)
            {
                return;
            }

            _suppressFlyoutClosedDeselect = true;
            try
            {
                _textBoxFlyout.ShowAt(target);
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() => _suppressFlyoutClosedDeselect = false);
            }
        }

        private void TextBoxFlyoutFontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ignoreNextFontSync)
            {
                _ignoreNextFontSync = false;
                return;
            }

            if (_selectedTextBox == null)
            {
                return;
            }

            if (_textBoxFlyoutFontSizeComboBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Content?.ToString(), out var size))
            {
                if (Math.Abs(_selectedTextBox.FontSize - size) > 0.001)
                {
                    _selectedTextBox.FontSize = size;
                    _currentFontSize = size;
                    MarkDirty();
                }
            }
        }

        private void TextBoxFlyoutColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (_selectedTextBox == null)
            {
                return;
            }

            var oldColor = (_selectedTextBox.Foreground as SolidColorBrush)?.Color;
            if (oldColor != args.NewColor)
            {
                _selectedTextBox.Foreground = new SolidColorBrush(args.NewColor);
                _currentColor = args.NewColor;
                try { ToolColorPicker.Color = args.NewColor; } catch { }
                MarkDirty();
            }
        }

        private void DeleteSelectedTextBox()
        {
            if (_selectedTextBox == null)
            {
                return;
            }

            if (_selectedTextBox.Parent is Panel panel)
            {
                var toDelete = _selectedTextBox;
                DeselectTextBox();
                panel.Children.Remove(toDelete);
                MarkDirty();
            }
        }

        private void SelectTextBox(TextBox textBox, bool focusTextBox)
        {
            if (_selectedTextBox != null && _selectedTextBox != textBox)
            {
                ApplyTextBoxChrome(_selectedTextBox, isSelected: false);
                _selectedTextBox.IsReadOnly = true;
            }

            _selectedTextBox = textBox;
            textBox.IsReadOnly = false;
            ApplyTextBoxChrome(textBox, isSelected: true);
            SyncFlyoutToSelectedTextBox();
            ShowTextBoxFlyoutAt(textBox);

            if (focusTextBox)
            {
                textBox.Focus(FocusState.Programmatic);
            }
        }

        private void DeselectTextBox()
        {
            if (_selectedTextBox == null)
            {
                return;
            }

            ApplyTextBoxChrome(_selectedTextBox, isSelected: false);
            _selectedTextBox.IsReadOnly = true;
            _selectedTextBox = null;
            HideTextBoxFlyouts(suppressClosedDeselect: true);
        }

        private void ApplyTextBoxChrome(TextBox textBox, bool isSelected)
        {
            if (isSelected)
            {
                textBox.BorderThickness = new Thickness(1);
                textBox.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
                textBox.Background = new SolidColorBrush(Colors.Transparent);
            }
            else
            {
                textBox.BorderThickness = new Thickness(0);
                textBox.BorderBrush = new SolidColorBrush(Colors.Transparent);
                textBox.Background = new SolidColorBrush(Colors.Transparent);
            }
        }

        private void SyncFlyoutToSelectedTextBox()
        {
            if (_selectedTextBox == null)
            {
                return;
            }

            var size = _selectedTextBox.FontSize;
            var sizes = new[] { 12d, 18d, 24d, 36d, 48d, 72d };
            var nearest = sizes.OrderBy(s => Math.Abs(s - size)).First();

            _ignoreNextFontSync = true;
            for (var i = 0; i < _textBoxFlyoutFontSizeComboBox.Items.Count; i++)
            {
                if (_textBoxFlyoutFontSizeComboBox.Items[i] is ComboBoxItem item && double.TryParse(item.Content?.ToString(), out var parsed) && Math.Abs(parsed - nearest) < 0.001)
                {
                    _textBoxFlyoutFontSizeComboBox.SelectedIndex = i;
                    break;
                }
            }

            var current = (_selectedTextBox.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
            try { _textBoxFlyoutColorPicker.Color = current; } catch { }
        }

        private static TextBox FindAncestorTextBox(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is TextBox tb)
                {
                    return tb;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void PageControl_TextOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_currentTool != ToolType.Text) return;

            if (FindAncestorTextBox(e.OriginalSource as DependencyObject) != null)
            {
                return;
            }

            var page = sender as PdfPageControl;
            if (page == null)
            {
                return;
            }
            var pointOnOverlay = e.GetCurrentPoint(page.TextOverlay);
            if (pointOnOverlay.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pointOnOverlay.Properties.IsLeftButtonPressed)
            {
                return;
            }
            var point = e.GetCurrentPoint(page.TextOverlay).Position;

            if (_selectedTextBox != null)
            {
                DeselectTextBox();
                UpdateToolState(ToolType.SelectPan);
                e.Handled = true;
                return;
            }

            CreateTextBox(page, point);
        }

        private void CreateTextBox(PdfPageControl page, Windows.Foundation.Point position)
        {
            var textBox = new TextBox
            {
                Text = "Text",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 100,
                MinHeight = 30,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.DodgerBlue),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = _currentFontSize,
                Foreground = new SolidColorBrush(_currentColor),
                IsReadOnly = false
            };
            AutomationProperties.SetName(textBox, "Text annotation");

            // Position
            Canvas.SetLeft(textBox, position.X);
            Canvas.SetTop(textBox, position.Y);

            // Events
            textBox.PointerPressed += TextBox_PointerPressed;
            textBox.PointerMoved += TextBox_PointerMoved;
            textBox.PointerReleased += TextBox_PointerReleased;
            textBox.TextChanged += TextBox_TextChanged;
            textBox.GotFocus += (s, e) => SelectTextBox((TextBox)s, focusTextBox: false);

            page.TextOverlay.Children.Add(textBox);
            SelectTextBox(textBox, focusTextBox: true);
            textBox.SelectAll();
            MarkDirty();
        }

        // Dragging State
        private bool _isDragging = false;
        private bool _dragArmed = false;
        private Windows.Foundation.Point _dragStartOffset;
        private Windows.Foundation.Point _dragPressPointOnCanvas;
        private TextBox _draggedTextBox;

        private void TextBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_currentTool == ToolType.Text)
            {
                var tb = sender as TextBox;
                SelectTextBox(tb, focusTextBox: true);

                var pointOnTb = e.GetCurrentPoint(tb).Position;
                var width = tb.ActualWidth > 0 ? tb.ActualWidth : tb.MinWidth;
                var height = tb.ActualHeight > 0 ? tb.ActualHeight : tb.MinHeight;
                var edge = 8d;
                var onEdge = pointOnTb.X <= edge || pointOnTb.X >= width - edge || pointOnTb.Y <= edge || pointOnTb.Y >= height - edge;

                _dragArmed = onEdge;
                if (onEdge)
                {
                    _draggedTextBox = tb;
                    _dragStartOffset = pointOnTb;
                    if (tb.Parent is Canvas canvas)
                    {
                        _dragPressPointOnCanvas = e.GetCurrentPoint(canvas).Position;
                    }
                    _dragStartX = Canvas.GetLeft(tb);
                    _dragStartY = Canvas.GetTop(tb);
                }
                else
                {
                    _draggedTextBox = null;
                }
            }
        }

        private void TextBox_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if ((_isDragging || _dragArmed) && _draggedTextBox != null)
            {
                var canvas = _draggedTextBox.Parent as Canvas;
                if (canvas == null) return;

                var currentPoint = e.GetCurrentPoint(canvas).Position;

                if (_dragArmed && !_isDragging)
                {
                    var dx = currentPoint.X - _dragPressPointOnCanvas.X;
                    var dy = currentPoint.Y - _dragPressPointOnCanvas.Y;
                    if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
                    {
                        _isDragging = true;
                        _dragArmed = false;
                        _draggedTextBox.CapturePointer(e.Pointer);
                    HideTextBoxFlyouts(suppressClosedDeselect: true);
                    }
                }

                if (_isDragging)
                {
                    double newX = currentPoint.X - _dragStartOffset.X;
                    double newY = currentPoint.Y - _dragStartOffset.Y;

                    Canvas.SetLeft(_draggedTextBox, newX);
                    Canvas.SetTop(_draggedTextBox, newY);
                    e.Handled = true;
                }
            }
        }

        private void TextBox_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging || _dragArmed)
            {
                var wasDragging = _isDragging;
                _dragArmed = false;
                _isDragging = false;
                _draggedTextBox?.ReleasePointerCapture(e.Pointer);
                if (_draggedTextBox != null)
                {
                    var endX = Canvas.GetLeft(_draggedTextBox);
                    var endY = Canvas.GetTop(_draggedTextBox);
                    if (Math.Abs(endX - _dragStartX) > 0.5 || Math.Abs(endY - _dragStartY) > 0.5)
                    {
                        MarkDirty();
                    }
                }
                ShowTextBoxFlyoutAt(_draggedTextBox);
                _draggedTextBox = null;
                e.Handled = wasDragging;
            }
        }

        private void PageControl_BackgroundPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_selectedTextBox != null)
            {
                DeselectTextBox();
            }

            if (_currentTool == ToolType.SelectPan)
            {
                return;
            }

            var sourceElement = sender as UIElement ?? this;
            var point = e.GetCurrentPoint(sourceElement);

            if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
            {
                return;
            }

            if ((point.Properties.IsLeftButtonPressed || point.Properties.IsEraser) &&
                (_currentTool == ToolType.Pen || _currentTool == ToolType.Highlighter || _currentTool == ToolType.Eraser))
            {
                return;
            }

            UpdateToolState(ToolType.SelectPan);
        }

        // --- Save Logic ---

        private async void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            await SavePdfAsync();
        }

        private async Task<bool> SavePdfAsync()
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("PDF File", new List<string>() { ".pdf" });
            picker.SuggestedFileName = "Annotated.pdf";

            InitializeWithWindow.Initialize(picker, _hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    var annotations = new List<(int PageIndex, List<List<System.Drawing.PointF>> InkStrokes, List<(string Text, Windows.Foundation.Rect Rect, Windows.UI.Color Color, double FontSize)> TextAnnotations)>();

                    foreach (var child in PagesContainer.Children)
                    {
                        if (child is PdfPageControl page)
                        {
                            // 1. Ink Strokes
                            var inkStrokes = new List<List<System.Drawing.PointF>>();
                            var strokes = page.GetStrokes();
                            foreach (var stroke in strokes)
                            {
                                var points = stroke.GetInkPoints().Select(p => new System.Drawing.PointF((float)p.Position.X, (float)p.Position.Y)).ToList();
                                inkStrokes.Add(points);
                            }

                            // 2. Text Annotations
                            var textAnnotations = new List<(string Text, Windows.Foundation.Rect Rect, Windows.UI.Color Color, double FontSize)>();
                            foreach (var element in page.TextOverlay.Children)
                            {
                                if (element is TextBox tb)
                                {
                                     var x = Canvas.GetLeft(tb);
                                     var y = Canvas.GetTop(tb);
                                     var rect = new Windows.Foundation.Rect(x, y, tb.ActualWidth, tb.ActualHeight);
                                     var color = (tb.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
                                     textAnnotations.Add((tb.Text, rect, color, tb.FontSize));
                                }
                            }

                            if (inkStrokes.Any() || textAnnotations.Any())
                            {
                                annotations.Add((page.PageIndex, inkStrokes, textAnnotations));
                            }
                        }
                    }

                    await _pdfService.SavePdfWithAnnotationsAsync(file, annotations);
                    _isDirty = false;
                    await ShowDialog("Success", "PDF saved successfully.");
                    return true;
                }
                catch (Exception ex)
                {
                    await ShowDialog("Error", $"Failed to save PDF: {ex.Message}");
                    return false;
                }
            }
            return false;
        }

        private void PageControl_InkMutated(object sender, EventArgs e)
        {
            MarkDirty();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            MarkDirty();
        }

        private void MarkDirty()
        {
            _isDirty = true;
        }

        private async Task AttemptNavigateBackAsync()
        {
            if (!await EnsureCanLeaveAsync())
            {
                return;
            }

            NavigateBackCore();
        }

        private void NavigateBackCore()
        {
            _ignoreNextFrameNavigation = true;
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                Frame.Navigate(typeof(HomePage));
            }
        }

        private async void Frame_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            if (_ignoreNextFrameNavigation)
            {
                _ignoreNextFrameNavigation = false;
                return;
            }

            if (_isDirty)
            {
                e.Cancel = true;
                var pending = new PendingFrameNavigation(e.NavigationMode, e.SourcePageType, e.Parameter, e.NavigationTransitionInfo);
                if (await EnsureCanLeaveAsync())
                {
                    ReplayPendingNavigation(pending);
                }
            }
        }

        private async void KeyboardGoBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            await AttemptNavigateBackAsync();
        }

        private XamlRoot GetDialogXamlRoot()
        {
            if (XamlRoot != null)
            {
                return XamlRoot;
            }

            if (_hostWindow?.Content is FrameworkElement rootElement)
            {
                return rootElement.XamlRoot;
            }

            return null;
        }

        private async Task<bool> EnsureCanLeaveAsync()
        {
            if (!_isDirty)
            {
                return true;
            }

            await _unsavedPromptLock.WaitAsync();
            try
            {
                if (!_isDirty)
                {
                    return true;
                }

                var choice = await ShowUnsavedChangesDialogAsync();
                if (choice == UnsavedChangesChoice.Save)
                {
                    return await SavePdfAsync();
                }

                if (choice == UnsavedChangesChoice.Discard)
                {
                    _isDirty = false;
                    return true;
                }

                return false;
            }
            finally
            {
                _unsavedPromptLock.Release();
            }
        }

        private void ReplayPendingNavigation(PendingFrameNavigation pending)
        {
            _ignoreNextFrameNavigation = true;

            switch (pending.Mode)
            {
                case NavigationMode.Back:
                    if (Frame.CanGoBack)
                    {
                        Frame.GoBack();
                    }
                    else
                    {
                        Frame.Navigate(typeof(HomePage));
                    }
                    break;
                case NavigationMode.Forward:
                    if (Frame.CanGoForward)
                    {
                        Frame.GoForward();
                    }
                    break;
                default:
                    if (pending.DestinationPageType != null)
                    {
                        Frame.Navigate(pending.DestinationPageType, pending.Parameter, pending.TransitionInfo);
                    }
                    break;
            }
        }

        private async Task<UnsavedChangesChoice> ShowUnsavedChangesDialogAsync()
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot == null)
            {
                return UnsavedChangesChoice.Cancel;
            }

            var dialog = new ContentDialog
            {
                Title = "Unsaved changes",
                Content = "You have unsaved changes. Save before leaving?",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Discard",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => UnsavedChangesChoice.Save,
                ContentDialogResult.Secondary => UnsavedChangesChoice.Discard,
                _ => UnsavedChangesChoice.Cancel
            };
        }

        private void TryAttachWindowCloseHandler()
        {
            if (_appWindow != null || _hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
                _appWindow = AppWindow.GetFromWindowId(windowId);
                _appWindow.Closing += AppWindow_Closing;
            }
            catch
            {
                _appWindow = null;
            }
        }

        private void DetachWindowCloseHandler()
        {
            if (_appWindow == null)
            {
                return;
            }

            try
            {
                _appWindow.Closing -= AppWindow_Closing;
            }
            catch { }
            _appWindow = null;
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose || !_isDirty)
            {
                return;
            }

            args.Cancel = true;
            if (Interlocked.Exchange(ref _closePromptScheduled, 1) == 1)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (await EnsureCanLeaveAsync())
                    {
                        _allowClose = true;
                        _hostWindow?.Close();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _closePromptScheduled, 0);
                }
            });
        }

        private async Task ShowDialog(string title, string content)
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot == null) return;
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = xamlRoot
            };
            await dialog.ShowAsync();
        }

        private void CancelActiveLoad()
        {
            try
            {
                Interlocked.Increment(ref _loadSessionId);
                _loadCts?.Cancel();
                _loadCts?.Dispose();
            }
            catch { }
            _loadCts = null;
        }

        private void ShowLoadingOverlay()
        {
            LoadingOverlayFadeOut.Stop();
            LoadingOverlay.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingOverlay.IsHitTestVisible = true;
            LoadingOverlayRing.IsActive = true;
            LoadingOverlayFadeIn.Begin();
        }

        private Task HideLoadingOverlayAsync()
        {
            if (LoadingOverlay.Visibility != Visibility.Visible)
            {
                LoadingOverlayRing.IsActive = false;
                LoadingOverlay.IsHitTestVisible = false;
                LoadingOverlay.Opacity = 0;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Completed(object sender, object args)
            {
                LoadingOverlayFadeOut.Completed -= Completed;
                LoadingOverlayRing.IsActive = false;
                LoadingOverlay.IsHitTestVisible = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                tcs.TrySetResult(null);
            }

            try
            {
                LoadingOverlayFadeOut.Completed += Completed;
                LoadingOverlayFadeIn.Stop();
                LoadingOverlayFadeOut.Begin();
            }
            catch
            {
                LoadingOverlayFadeOut.Completed -= Completed;
                LoadingOverlayRing.IsActive = false;
                LoadingOverlay.IsHitTestVisible = false;
                LoadingOverlay.Opacity = 0;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }

        private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage> CreateBitmapImageAsync(byte[] pngBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            token.ThrowIfCancellationRequested();

            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
    }
}
