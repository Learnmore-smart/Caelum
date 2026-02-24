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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using WindowsNotesApp.Controls;
using WindowsNotesApp.Models;
using WindowsNotesApp.Services;
using WinRT.Interop;

namespace WindowsNotesApp.Pages
{
    public sealed partial class EditorPage : Page
    {
        // ── Enums ──────────────────────────────────────────────────
        private enum ToolType { None, Pen, Highlighter, Eraser, Text }
        private enum UnsavedChangesChoice { Save, Discard, Cancel }

        // ── Per-tool settings ──────────────────────────────────────
        private ToolType _currentTool = ToolType.None;
        private Color _penColor = Colors.Black;
        private Color _highlighterColor = Colors.Yellow;
        private Color _textColor = Colors.Black;
        private double _penSize = 2.0;
        private double _highlighterSize = 12.0;
        private double _eraserSize = 20.0;
        private double _currentFontSize = 18.0;
        private bool _isUpdatingToolState;

        // ── Services / state ───────────────────────────────────────
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
        private readonly SemaphoreSlim _unsavedPromptLock = new(1, 1);
        private string _currentPdfPath;

        // ── Text tool state ────────────────────────────────────────
        private TextBox _selectedTextBox;
        private Flyout _textBoxFlyout;
        private ComboBox _textBoxFlyoutFontSizeComboBox;
        private Border _textBoxColorIndicator;
        private ColorPicker _textBoxFlyoutColorPicker;
        private Flyout _textBoxFlyoutColorFlyout;
        private bool _ignoreNextFontSync;
        private bool _suppressFlyoutClosedDeselect;
        private bool _isHuaweiPenHooked;

        // ── Tool flyouts (created in code) ───────────────────────
        private Flyout _penFlyout;
        private Flyout _highlighterFlyout;
        private Flyout _eraserFlyout;
        private ColorPicker _penColorPicker;
        private ColorPicker _highlighterColorPicker;

        // ── Drag text state ────────────────────────────────────────
        private bool _isDragging;
        private bool _dragArmed;
        private Windows.Foundation.Point _dragStartOffset;
        private Windows.Foundation.Point _dragPressPointOnCanvas;
        private Grid _draggedContainer;
        private double _dragStartX;
        private double _dragStartY;

        // ── Pending navigation ─────────────────────────────────────
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

        // ════════════════════════════════════════════════════════════
        //  Constructor & Init
        // ════════════════════════════════════════════════════════════

        public EditorPage()
        {
            this.InitializeComponent();
            InitializeTextBoxFlyout();
            CreateToolFlyouts();

            var window = (Application.Current as App)?.m_window;
            if (window != null)
            {
                _hostWindow = window;
                _hwnd = WindowNative.GetWindowHandle(window);
            }

            _pdfService = new PdfService();
            _huaweiPenService = new HuaweiPenService();

            ActivateTool(ToolType.None);

            this.Loaded += EditorPage_Loaded;

            var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
            escape.Invoked += Escape_Invoked;
            KeyboardAccelerators.Add(escape);
            this.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        }

        private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            ActivateTool(ToolType.None);
            args.Handled = true;
        }

        private void EditorPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isHuaweiPenHooked)
                {
                    var window = (Application.Current as App)?.m_window;
                    if (window != null)
                    {
                        _huaweiPenService.Initialize(window);
                        _huaweiPenService.ToolToggleRequested += OnHuaweiPenDoubleTap;
                        _isHuaweiPenHooked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to init Huawei Pen Service: {ex.Message}");
            }
        }

        // ── Tool Flyout Creation (code-behind to avoid XAML compiler issues) ──

        private void CreateToolFlyouts()
        {
            _penFlyout = BuildToolFlyout(
                "Size", 1, 20, _penSize, 0.5,
                v => { _penSize = v; if (_currentTool == ToolType.Pen) ApplyToolToAllPages(); },
                "Color", _penColor,
                c => { _penColor = c; UpdateToolIconColors(); if (_currentTool == ToolType.Pen) ApplyToolToAllPages(); },
                out _penColorPicker);

            _highlighterFlyout = BuildToolFlyout(
                "Size", 4, 40, _highlighterSize, 1,
                v => { _highlighterSize = v; if (_currentTool == ToolType.Highlighter) ApplyToolToAllPages(); },
                "Color", _highlighterColor,
                c => { _highlighterColor = c; UpdateToolIconColors(); if (_currentTool == ToolType.Highlighter) ApplyToolToAllPages(); },
                out _highlighterColorPicker);

            _eraserFlyout = BuildToolFlyout(
                "Eraser Size", 4, 80, _eraserSize, 1,
                v => { _eraserSize = v; ApplyToolToAllPages(); },
                null, default, null,
                out _);
        }

        private static Flyout BuildToolFlyout(
            string sizeLabel, double min, double max, double value, double step, Action<double> sizeChanged,
            string colorLabel, Color initialColor, Action<Color> colorChanged,
            out ColorPicker outColorPicker)
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
            var panel = new StackPanel { Spacing = 12, Width = 256 };

            var sizeHeader = new TextBlock { Text = sizeLabel, FontSize = 12, Opacity = 0.7 };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, StepFrequency = step };
            slider.ValueChanged += (s, e) => sizeChanged?.Invoke(e.NewValue);
            panel.Children.Add(sizeHeader);
            panel.Children.Add(slider);

            outColorPicker = null;
            if (colorLabel != null)
            {
                var colorHeader = new TextBlock { Text = colorLabel, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
                var picker = new ColorPicker
                {
                    Color = initialColor,
                    IsAlphaEnabled = false,
                    IsColorSliderVisible = false,
                    IsColorChannelTextInputVisible = false,
                    IsHexInputVisible = false,
                    ColorSpectrumShape = ColorSpectrumShape.Ring,
                    IsMoreButtonVisible = false,
                    IsColorPreviewVisible = false,
                    IsAlphaSliderVisible = false
                };
                picker.ColorChanged += (s, e) => colorChanged?.Invoke(e.NewColor);
                panel.Children.Add(colorHeader);
                panel.Children.Add(picker);
                outColorPicker = picker;
            }

            flyout.Content = panel;
            return flyout;
        }

        // ════════════════════════════════════════════════════════════
        //  Navigation
        // ════════════════════════════════════════════════════════════

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Frame.Navigating += Frame_Navigating;
            TryAttachWindowCloseHandler();

            if (e.Parameter is StorageFile sf)
            {
                _currentPdfPath = sf.Path;
                await LoadPdf(file: sf);
            }
            else if (e.Parameter is string path && !string.IsNullOrEmpty(path))
            {
                _currentPdfPath = path;
                await LoadPdf(filePath: path);
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

        // ════════════════════════════════════════════════════════════
        //  PDF Loading
        // ════════════════════════════════════════════════════════════

        private async Task LoadPdf(StorageFile file = null, string filePath = null)
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
                System.Diagnostics.Debug.WriteLine($"LoadPdf: Starting to load PDF...");
                if (file != null)
                    await _pdfService.LoadPdfAsync(file, token);
                else
                    await _pdfService.LoadPdfAsync(filePath, token);
                System.Diagnostics.Debug.WriteLine($"LoadPdf: PDF loaded, {_pdfService.PageCount} pages");

                for (uint i = 0; i < _pdfService.PageCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    System.Diagnostics.Debug.WriteLine($"LoadPdf: Rendering page {i}...");
                    var imageBytes = await _pdfService.RenderPagePngBytesAsync(i, token);
                    if (imageBytes == null || imageBytes.Length == 0)
                        throw new InvalidOperationException($"Failed to render page {i + 1}.");
                    System.Diagnostics.Debug.WriteLine($"LoadPdf: Page {i} rendered, {imageBytes.Length} bytes");
                    token.ThrowIfCancellationRequested();

                    System.Diagnostics.Debug.WriteLine($"LoadPdf: Creating BitmapImage for page {i}...");
                    var image = await CreateBitmapImageAsync(imageBytes, token);
                    System.Diagnostics.Debug.WriteLine($"LoadPdf: BitmapImage created, PixelWidth={image.PixelWidth}, PixelHeight={image.PixelHeight}");

                    var pageControl = new PdfPageControl
                    {
                        PageSource = image,
                        PageIndex = (int)i,
                        Width = image.PixelWidth,
                        Height = image.PixelHeight
                    };
                    System.Diagnostics.Debug.WriteLine($"LoadPdf: PdfPageControl created");

                    pageControl.TextOverlayPointerPressed += PageControl_TextOverlayPointerPressed;
                    pageControl.BackgroundPointerPressed += PageControl_BackgroundPointerPressed;
                    pageControl.InkMutated += PageControl_InkMutated;

                    System.Diagnostics.Debug.WriteLine($"LoadPdf: Adding page {i} to PagesContainer...");
                    PagesContainer.Children.Add(pageControl);
                    System.Diagnostics.Debug.WriteLine($"LoadPdf: Page {i} added successfully");
                }

                System.Diagnostics.Debug.WriteLine($"LoadPdf: Applying tools to all pages...");
                ApplyToolToAllPages();

                if (!string.IsNullOrEmpty(_currentPdfPath))
                {
                    System.Diagnostics.Debug.WriteLine($"LoadPdf: Loading annotations...");
                    await LoadAnnotationsFromPdfServiceAsync();
                }
                System.Diagnostics.Debug.WriteLine($"LoadPdf: PDF loaded successfully");
            }
            catch (OperationCanceledException)
            {
                if (sessionId == _loadSessionId)
                    await ShowDialog("Canceled", "Loading document was canceled.");
            }
            catch (Exception ex)
            {
                string cName = ex.GetType().Name;
                System.Diagnostics.Debug.WriteLine($"LoadPdf EXCEPTION: {cName}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"LoadPdf STACK: {ex.StackTrace}");
                System.Diagnostics.Debug.WriteLine($"LoadPdf INNER: {ex.InnerException?.Message}");
                System.Diagnostics.Debug.WriteLine($"LoadPdf INNER STACK: {ex.InnerException?.StackTrace}");

                var errorMsg = $"Failed to load PDF: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMsg += $"\n\nDetails: {ex.InnerException.Message}";
                }

                if (sessionId == _loadSessionId)
                    await ShowDialog("Error", errorMsg);
            }
            finally
            {
                if (sessionId == _loadSessionId)
                    await HideLoadingOverlayAsync();
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Tool Management
        // ════════════════════════════════════════════════════════════

        private void PenToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            var btn = sender as ToggleButton;
            if (btn?.IsChecked == true)
            {
                ActivateTool(ToolType.Pen);
                if (_penColorPicker != null)
                    _penColorPicker.Color = _penColor;
                _penFlyout?.ShowAt(PenToolButton);
            }
            else
                ActivateTool(ToolType.None);
        }

        private void PenToolButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_currentTool == ToolType.Pen)
            {
                e.Handled = true;
                _penFlyout?.Hide();
                ActivateTool(ToolType.None);
            }
        }

        private void HighlighterToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            var btn = sender as ToggleButton;
            if (btn?.IsChecked == true)
            {
                ActivateTool(ToolType.Highlighter);
                if (_highlighterColorPicker != null)
                    _highlighterColorPicker.Color = _highlighterColor;
                _highlighterFlyout?.ShowAt(HighlighterToolButton);
            }
            else
                ActivateTool(ToolType.None);
        }

        private void HighlighterToolButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_currentTool == ToolType.Highlighter)
            {
                e.Handled = true;
                _highlighterFlyout?.Hide();
                ActivateTool(ToolType.None);
            }
        }

        private void EraserToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            var btn = sender as ToggleButton;
            if (btn?.IsChecked == true)
            {
                ActivateTool(ToolType.Eraser);
                _eraserFlyout?.ShowAt(EraserToolButton);
            }
            else
                ActivateTool(ToolType.None);
        }

        private void EraserToolButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_currentTool == ToolType.Eraser)
            {
                e.Handled = true;
                _eraserFlyout?.Hide();
                ActivateTool(ToolType.None);
            }
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

            // Active indicator bars
            PenActiveIndicator.Visibility = tool == ToolType.Pen ? Visibility.Visible : Visibility.Collapsed;
            HighlighterActiveIndicator.Visibility = tool == ToolType.Highlighter ? Visibility.Visible : Visibility.Collapsed;
            EraserActiveIndicator.Visibility = tool == ToolType.Eraser ? Visibility.Visible : Visibility.Collapsed;
            TextActiveIndicator.Visibility = tool == ToolType.Text ? Visibility.Visible : Visibility.Collapsed;

            // Active background style
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

            // Update icon foreground to reflect chosen color
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
                            atts.Size = new Windows.Foundation.Size(_penSize, _penSize);
                            atts.DrawAsHighlighter = false;
                            page.SetInkAttributes(atts);
                            break;
                        case ToolType.Highlighter:
                            page.SetInputMode(CustomInkInputProcessingMode.Inking);
                            atts.Color = _highlighterColor;
                            atts.Size = new Windows.Foundation.Size(_highlighterSize, _highlighterSize);
                            atts.DrawAsHighlighter = true;
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

        private void OnHuaweiPenDoubleTap(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentTool == ToolType.Pen || _currentTool == ToolType.Highlighter)
                    ActivateTool(ToolType.Eraser);
                else if (_currentTool == ToolType.Eraser)
                    ActivateTool(ToolType.Pen);
            });
        }

        // ════════════════════════════════════════════════════════════
        //  Back / Save
        // ════════════════════════════════════════════════════════════

        private async void Back_Click(object sender, RoutedEventArgs e)
        {
            await AttemptNavigateBackAsync();
        }

        private async void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            await SaveAnnotationsToPdfAsync();
        }

        // ════════════════════════════════════════════════════════════
        //  Text Tool
        // ════════════════════════════════════════════════════════════

        private void InitializeTextBoxFlyout()
        {
            _textBoxFlyout = new Flyout { Placement = FlyoutPlacementMode.Top };
            _textBoxFlyout.Closed += TextBoxFlyout_Closed;

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

            var deleteButton = new Button { Content = new SymbolIcon(Symbol.Delete), MinWidth = 40 };
            deleteButton.Click += (s, e) => DeleteSelectedTextBox();
            AutomationProperties.SetName(deleteButton, "Delete annotation");

            _textBoxFlyoutFontSizeComboBox = new ComboBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(_textBoxFlyoutFontSizeComboBox, "Font size");
            foreach (var size in new[] { 12, 18, 24, 36, 48, 72 })
                _textBoxFlyoutFontSizeComboBox.Items.Add(new ComboBoxItem { Content = size.ToString() });
            _textBoxFlyoutFontSizeComboBox.SelectionChanged += TextBoxFlyoutFontSizeComboBox_SelectionChanged;

            // Color indicator button — shows current text color as a filled circle
            _textBoxColorIndicator = new Border
            {
                Width = 20, Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(_textColor)
            };
            var colorButton = new Button { Content = _textBoxColorIndicator, MinWidth = 40 };
            AutomationProperties.SetName(colorButton, "Text color");

            _textBoxFlyoutColorFlyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
            _textBoxFlyoutColorPicker = new ColorPicker
            {
                IsAlphaEnabled = false,
                IsColorChannelTextInputVisible = false,
                IsHexInputVisible = false,
                ColorSpectrumShape = ColorSpectrumShape.Ring,
                IsMoreButtonVisible = false,
                IsColorSliderVisible = false,
                IsColorPreviewVisible = false,
                IsAlphaSliderVisible = false
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
            if (_suppressFlyoutClosedDeselect || _selectedTextBox == null) return;

            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (focused != null && FindAncestorTextBox(focused) == _selectedTextBox) return;

            DeselectTextBox();
        }

        private void HideTextBoxFlyouts(bool suppressClosedDeselect)
        {
            if (suppressClosedDeselect) _suppressFlyoutClosedDeselect = true;
            try
            {
                _textBoxFlyoutColorFlyout?.Hide();
                _textBoxFlyout?.Hide();
            }
            finally
            {
                if (suppressClosedDeselect)
                    DispatcherQueue.TryEnqueue(() => _suppressFlyoutClosedDeselect = false);
            }
        }

        private void ShowTextBoxFlyoutAt(FrameworkElement target)
        {
            if (_textBoxFlyout == null || target == null) return;
            _suppressFlyoutClosedDeselect = true;
            try { _textBoxFlyout.ShowAt(target); }
            finally { DispatcherQueue.TryEnqueue(() => _suppressFlyoutClosedDeselect = false); }
        }

        private void TextBoxFlyoutFontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ignoreNextFontSync) { _ignoreNextFontSync = false; return; }
            if (_selectedTextBox == null) return;
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
            if (_selectedTextBox == null) return;
            var oldColor = (_selectedTextBox.Foreground as SolidColorBrush)?.Color;
            if (oldColor != args.NewColor)
            {
                _selectedTextBox.Foreground = new SolidColorBrush(args.NewColor);
                _textColor = args.NewColor;
                if (_textBoxColorIndicator != null)
                    _textBoxColorIndicator.Background = new SolidColorBrush(args.NewColor);
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

            if (focusTextBox) textBox.Focus(FocusState.Programmatic);
        }

        private void DeselectTextBox()
        {
            if (_selectedTextBox == null) return;
            ApplyTextBoxChrome(_selectedTextBox, isSelected: false);
            _selectedTextBox.IsReadOnly = true;
            _selectedTextBox = null;
            HideTextBoxFlyouts(suppressClosedDeselect: true);
        }

        private void ApplyTextBoxChrome(TextBox textBox, bool isSelected)
        {
            textBox.BorderThickness = isSelected ? new Thickness(1) : new Thickness(0);
            textBox.BorderBrush = new SolidColorBrush(isSelected ? Colors.DodgerBlue : Colors.Transparent);
            textBox.Background = new SolidColorBrush(Colors.Transparent);

            if (textBox.Parent is Grid container && container.Children.Count > 0 && container.Children[0] is Border dragHandle)
            {
                dragHandle.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SyncFlyoutToSelectedTextBox()
        {
            if (_selectedTextBox == null) return;

            var size = _selectedTextBox.FontSize;
            var sizes = new[] { 12d, 18d, 24d, 36d, 48d, 72d };
            var nearest = sizes.OrderBy(s => Math.Abs(s - size)).First();

            _ignoreNextFontSync = true;
            for (var i = 0; i < _textBoxFlyoutFontSizeComboBox.Items.Count; i++)
            {
                if (_textBoxFlyoutFontSizeComboBox.Items[i] is ComboBoxItem item
                    && double.TryParse(item.Content?.ToString(), out var parsed)
                    && Math.Abs(parsed - nearest) < 0.001)
                {
                    _textBoxFlyoutFontSizeComboBox.SelectedIndex = i;
                    break;
                }
            }

            var current = (_selectedTextBox.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
            try { _textBoxFlyoutColorPicker.Color = current; } catch { }
            if (_textBoxColorIndicator != null)
                _textBoxColorIndicator.Background = new SolidColorBrush(current);
        }

        private static TextBox FindAncestorTextBox(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is Grid container && container.Children.Count > 1 && container.Children[1] is TextBox tbGrid) return tbGrid;
                if (current is TextBox tb) return tb;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void PageControl_TextOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_currentTool != ToolType.Text) return;
            if (FindAncestorTextBox(e.OriginalSource as DependencyObject) != null) return;

            var page = sender as PdfPageControl;
            if (page == null) return;
            var pointOnOverlay = e.GetCurrentPoint(page.TextOverlay);
            if (pointOnOverlay.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pointOnOverlay.Properties.IsLeftButtonPressed)
                return;
            var point = pointOnOverlay.Position;

            if (_selectedTextBox != null)
            {
                DeselectTextBox();
                e.Handled = true;
                return;
            }

            CreateTextBox(page, point);
        }

        private void CreateTextBox(PdfPageControl page, Windows.Foundation.Point position,
            Color? color = null, double? fontSize = null, string text = null, bool select = true)
        {
            var container = new Grid
            {
                Background = new SolidColorBrush(Colors.Transparent),
                IsHitTestVisible = true
            };
            AutomationProperties.SetName(container, "Text annotation container");

            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var dragHandle = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 240, 240, 240)),
                BorderBrush = new SolidColorBrush(Colors.LightGray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = select ? Visibility.Visible : Visibility.Collapsed
            };

            var dragIcon = new FontIcon
            {
                Glyph = "\uE7C2",
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.Gray),
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
                BorderBrush = new SolidColorBrush(select ? Colors.DodgerBlue : Colors.Transparent),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = fontSize ?? _currentFontSize,
                Foreground = new SolidColorBrush(color ?? _textColor),
                IsReadOnly = !select,
                Margin = new Thickness(0)
            };
            AutomationProperties.SetName(textBox, "Text annotation");

            Grid.SetRow(dragHandle, 0);
            Grid.SetRow(textBox, 1);

            container.Children.Add(dragHandle);
            container.Children.Add(textBox);

            Canvas.SetLeft(container, position.X);
            Canvas.SetTop(container, position.Y);

            dragHandle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(DragHandle_PointerPressed), true);
            dragHandle.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(DragHandle_PointerMoved), true);
            dragHandle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(DragHandle_PointerReleased), true);

            textBox.TextChanged += TextBox_TextChanged;
            textBox.GotFocus += (s, e) =>
            {
                if (_currentTool == ToolType.Text)
                    SelectTextBox((TextBox)s, focusTextBox: false);
            };

            page.TextOverlay.Children.Add(container);

            if (select)
            {
                SelectTextBox(textBox, focusTextBox: true);
                textBox.SelectAll();
                MarkDirty();
            }
        }

        // ── Text Box Dragging ──

        private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_currentTool != ToolType.Text) return;
            var handle = sender as Border;
            if (handle?.Parent is Grid container && container.Children.Count > 1 && container.Children[1] is TextBox tb)
            {
                SelectTextBox(tb, focusTextBox: false);
            }

            var pointOnHandle = e.GetCurrentPoint(handle).Position;

            _dragArmed = true;
            _draggedContainer = handle?.Parent as Grid;
            _dragStartOffset = pointOnHandle;
            if (_draggedContainer?.Parent is Canvas canvas)
                _dragPressPointOnCanvas = e.GetCurrentPoint(canvas).Position;
            if (_draggedContainer != null)
            {
                _dragStartX = Canvas.GetLeft(_draggedContainer);
                _dragStartY = Canvas.GetTop(_draggedContainer);
            }
            e.Handled = true;
        }

        private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if ((!_isDragging && !_dragArmed) || _draggedContainer == null) return;
            var canvas = _draggedContainer.Parent as Canvas;
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

                    var handle = _draggedContainer.Children[0] as Border;
                    handle?.CapturePointer(e.Pointer);
                    HideTextBoxFlyouts(suppressClosedDeselect: true);
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

        private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging && !_dragArmed) return;
            var wasDragging = _isDragging;
            _dragArmed = false;
            _isDragging = false;
            var handle = sender as Border;
            handle?.ReleasePointerCapture(e.Pointer);

            if (_draggedContainer != null)
            {
                var endX = Canvas.GetLeft(_draggedContainer);
                var endY = Canvas.GetTop(_draggedContainer);
                if (Math.Abs(endX - _dragStartX) > 0.5 || Math.Abs(endY - _dragStartY) > 0.5)
                    MarkDirty();
                if (wasDragging && _draggedContainer.Children.Count > 1 && _draggedContainer.Children[1] is TextBox tb)
                    ShowTextBoxFlyoutAt(tb);
            }
            _draggedContainer = null;
            e.Handled = wasDragging;
        }

        private void PageControl_BackgroundPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_selectedTextBox != null) DeselectTextBox();

            // For mouse clicks on background when not using pen input, deselect tool
            var sourceElement = sender as UIElement ?? this;
            var point = e.GetCurrentPoint(sourceElement);
            if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen) return;
            if ((point.Properties.IsLeftButtonPressed || point.Properties.IsEraser)
                && (_currentTool == ToolType.Pen || _currentTool == ToolType.Highlighter || _currentTool == ToolType.Eraser))
                return;
        }

        // ════════════════════════════════════════════════════════════
        //  Annotation Save/Load (PDF-Native)
        // ════════════════════════════════════════════════════════════

        private async Task SaveAnnotationsToPdfAsync()
        {
            if (string.IsNullOrEmpty(_currentPdfPath))
            {
                await ShowDialog("Error", "No PDF is currently loaded.");
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

                        // Strokes
                        pa.Strokes = page.GetStrokeData();

                        // Text boxes
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

                await HideLoadingOverlayAsync();
                await ShowDialog("Saved", "Annotations saved to PDF successfully.");
            }
            catch (Exception ex)
            {
                await HideLoadingOverlayAsync();
                await ShowDialog("Error", $"Failed to save annotations: {ex.Message}");
            }
        }

        private async Task LoadAnnotationsFromPdfServiceAsync()
        {
            System.Diagnostics.Debug.WriteLine($"LoadAnnotationsFromPdfServiceAsync: Starting, {_pdfService.ExtractedAnnotations?.Count ?? 0} annotations");
            if (_pdfService.ExtractedAnnotations == null || _pdfService.ExtractedAnnotations.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("LoadAnnotationsFromPdfServiceAsync: No annotations to load");
                return;
            }

            try
            {
                foreach (var child in PagesContainer.Children)
                {
                    if (child is PdfPageControl page && _pdfService.ExtractedAnnotations.TryGetValue(page.PageIndex, out var pa))
                    {
                        System.Diagnostics.Debug.WriteLine($"LoadAnnotationsFromPdfServiceAsync: Loading annotations for page {page.PageIndex}, {pa.Strokes.Count} strokes, {pa.Texts.Count} texts");

                        // Restore strokes
                        foreach (var sa in pa.Strokes)
                        {
                            System.Diagnostics.Debug.WriteLine($"LoadAnnotationsFromPdfServiceAsync: Adding stroke with {sa.Points?.Count ?? 0} points");
                            page.AddStroke(sa);
                        }

                        // Restore text boxes
                        foreach (var ta in pa.Texts)
                        {
                            System.Diagnostics.Debug.WriteLine($"LoadAnnotationsFromPdfServiceAsync: Adding text box at ({ta.X}, {ta.Y})");
                            var color = Color.FromArgb(255, ta.R, ta.G, ta.B);
                            CreateTextBox(page, new Windows.Foundation.Point(ta.X, ta.Y),
                                color: color, fontSize: ta.FontSize, text: ta.Text, select: false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadAnnotationsFromPdfServiceAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"LoadAnnotationsFromPdfServiceAsync STACK: {ex.StackTrace}");
            }
            await Task.CompletedTask;
        }

        // ════════════════════════════════════════════════════════════
        //  Dirty state / Navigation guards
        // ════════════════════════════════════════════════════════════

        private void PageControl_InkMutated(object sender, EventArgs e) => MarkDirty();
        private void TextBox_TextChanged(object sender, TextChangedEventArgs e) => MarkDirty();
        private void MarkDirty() => _isDirty = true;

        private async Task AttemptNavigateBackAsync()
        {
            if (!await EnsureCanLeaveAsync()) return;
            NavigateBackCore();
        }

        private void NavigateBackCore()
        {
            _ignoreNextFrameNavigation = true;
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(HomePage));
        }

        private async void Frame_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            if (_ignoreNextFrameNavigation) { _ignoreNextFrameNavigation = false; return; }
            if (_isDirty)
            {
                e.Cancel = true;
                var pending = new PendingFrameNavigation(e.NavigationMode, e.SourcePageType, e.Parameter, e.NavigationTransitionInfo);
                if (await EnsureCanLeaveAsync())
                    ReplayPendingNavigation(pending);
            }
        }

        private async void KeyboardGoBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            await AttemptNavigateBackAsync();
        }

        private XamlRoot GetDialogXamlRoot()
        {
            if (XamlRoot != null) return XamlRoot;
            if (_hostWindow?.Content is FrameworkElement rootElement) return rootElement.XamlRoot;
            return null;
        }

        private async Task<bool> EnsureCanLeaveAsync()
        {
            if (!_isDirty) return true;
            await _unsavedPromptLock.WaitAsync();
            try
            {
                if (!_isDirty) return true;
                var choice = await ShowUnsavedChangesDialogAsync();
                if (choice == UnsavedChangesChoice.Save)
                {
                    await SaveAnnotationsToPdfAsync();
                    return !_isDirty; // true if save succeeded
                }
                if (choice == UnsavedChangesChoice.Discard) { _isDirty = false; return true; }
                return false;
            }
            finally { _unsavedPromptLock.Release(); }
        }

        private void ReplayPendingNavigation(PendingFrameNavigation pending)
        {
            _ignoreNextFrameNavigation = true;
            switch (pending.Mode)
            {
                case NavigationMode.Back:
                    if (Frame.CanGoBack) Frame.GoBack();
                    else Frame.Navigate(typeof(HomePage));
                    break;
                case NavigationMode.Forward:
                    if (Frame.CanGoForward) Frame.GoForward();
                    break;
                default:
                    if (pending.DestinationPageType != null)
                        Frame.Navigate(pending.DestinationPageType, pending.Parameter, pending.TransitionInfo);
                    break;
            }
        }

        private async Task<UnsavedChangesChoice> ShowUnsavedChangesDialogAsync()
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot == null) return UnsavedChangesChoice.Cancel;
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

        // ════════════════════════════════════════════════════════════
        //  Window close handler
        // ════════════════════════════════════════════════════════════

        private void TryAttachWindowCloseHandler()
        {
            if (_appWindow != null || _hwnd == IntPtr.Zero) return;
            try
            {
                var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
                _appWindow = AppWindow.GetFromWindowId(windowId);
                _appWindow.Closing += AppWindow_Closing;
            }
            catch { _appWindow = null; }
        }

        private void DetachWindowCloseHandler()
        {
            if (_appWindow == null) return;
            try { _appWindow.Closing -= AppWindow_Closing; } catch { }
            _appWindow = null;
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose || !_isDirty) return;
            args.Cancel = true;
            if (Interlocked.Exchange(ref _closePromptScheduled, 1) == 1) return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (await EnsureCanLeaveAsync()) { _allowClose = true; _hostWindow?.Close(); }
                }
                finally { Interlocked.Exchange(ref _closePromptScheduled, 0); }
            });
        }

        // ════════════════════════════════════════════════════════════
        //  Utilities
        // ════════════════════════════════════════════════════════════

        private async Task ShowDialog(string title, string content)
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot == null) return;
            var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "OK", XamlRoot = xamlRoot };
            await dialog.ShowAsync();
        }

        private void CancelActiveLoad()
        {
            try { Interlocked.Increment(ref _loadSessionId); _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
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

            // Use BitmapCreateOptions to ensure synchronous decode and valid PixelWidth/PixelHeight
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                CreateOptions = Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.None
            };

            // Wait for image to be fully loaded to ensure PixelWidth/PixelHeight are valid
            var tcs = new TaskCompletionSource<object>();
            void OnImageOpened(object sender, RoutedEventArgs e)
            {
                image.ImageOpened -= OnImageOpened;
                image.ImageFailed -= OnImageFailed;
                tcs.TrySetResult(null);
            }
            void OnImageFailed(object sender, ExceptionRoutedEventArgs e)
            {
                image.ImageOpened -= OnImageOpened;
                image.ImageFailed -= OnImageFailed;
                tcs.TrySetException(new InvalidOperationException($"Failed to load image: {e.ErrorMessage}"));
            }
            image.ImageOpened += OnImageOpened;
            image.ImageFailed += OnImageFailed;

            await image.SetSourceAsync(stream);

            // If not yet loaded, wait for the event
            if (image.PixelWidth == 0 || image.PixelHeight == 0)
            {
                using (token.Register(() => tcs.TrySetCanceled()))
                {
                    await tcs.Task;
                }
            }
            else
            {
                image.ImageOpened -= OnImageOpened;
                image.ImageFailed -= OnImageFailed;
            }

            return image;
        }
    }
}
