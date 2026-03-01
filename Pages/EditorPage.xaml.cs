using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WindowsNotesApp.Controls;
using WindowsNotesApp.Models;
using WindowsNotesApp.Services;

namespace WindowsNotesApp.Pages
{
    public sealed partial class EditorPage : Page
    {
        private enum ToolType { None, Pen, Highlighter, Eraser, Text }

        private ToolType _currentTool = ToolType.None;
        private ToolType _previousTool = ToolType.Pen;
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
        public string CurrentPdfPath => _currentPdfPath;

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

        // Undo/Redo
        private interface IUndoAction { void Undo(); void Redo(); }
        private class StrokeAddedAction : IUndoAction
        {
            private readonly PdfPageControl _page;
            private readonly System.Windows.Ink.Stroke _stroke;
            public StrokeAddedAction(PdfPageControl page, System.Windows.Ink.Stroke stroke) { _page = page; _stroke = stroke; }
            public void Undo() => _page.RemoveStrokeQuiet(_stroke);
            public void Redo() => _page.AddStrokeQuiet(_stroke);
        }
        private readonly List<IUndoAction> _undoStack = new List<IUndoAction>();
        private readonly List<IUndoAction> _redoStack = new List<IUndoAction>();

        private double _zoomLevel = 1.0;
        private double _lastRenderedDpiScale = 1.0;
        private CancellationTokenSource _reRenderCts;

        // Tracks which pages have been re-rendered at the current _lastRenderedDpiScale
        private readonly HashSet<int> _pagesRenderedAtScale = new HashSet<int>();
        private CancellationTokenSource _scrollReRenderCts;

        // Smooth scrolling
        private double _targetVerticalOffset;
        private double _targetHorizontalOffset;
        private bool _smoothScrollInitialized;

        // Middle-mouse-button pan state
        private bool _isMiddleMousePanning;
        private Point _middleMouseStartPoint;
        private double _middleMouseStartVerticalOffset;
        private double _middleMouseStartHorizontalOffset;

        // Touch manipulation state
        private double _manipulationBaseZoom;

        private const double ZoomMin = 0.25;
        private const double ZoomMax = 4.0;
        private const double ZoomStep = 0.1;

        // Pen scrolling state
        private bool _isPenScrolling;
        private Point _penScrollStartPoint;
        private double _penScrollStartVerticalOffset;
        private double _penScrollStartHorizontalOffset;

        // Universal pen support (Surface, Wacom, Huawei, Dell, HP, Lenovo, etc.)
        private WindowsPenService _penService;

        // Auto-save timer (every 60 seconds)
        private System.Windows.Threading.DispatcherTimer _autoSaveTimer;

        // Horizontal mouse wheel hook for precision touchpads
        private HwndSource _hwndSource;
        private const int WM_MOUSEHWHEEL = 0x020E;

        public EditorPage()
        {
            InitializeComponent();
            InitializeTextBoxPopup();
            CreateToolPopups();

            _pdfService = new PdfService();
            ActivateTool(ToolType.None);

            FixPopupTopmost(_textBoxPopup);
            FixPopupTopmost(_penPopup);
            FixPopupTopmost(_highlighterPopup);
            FixPopupTopmost(_eraserPopup);

            KeyDown += EditorPage_KeyDown;

            Loaded += EditorPage_Loaded;
            Unloaded += EditorPage_Unloaded;

            // Re-render newly-visible pages when scrolling after a zoom
            PdfScrollViewer.ScrollChanged += PdfScrollViewer_ScrollChanged;

            // Start interval auto-save timer
            _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;
            _autoSaveTimer.Start();
        }

        private void EditorPage_Loaded(object sender, RoutedEventArgs e)
        {
            InitializePenService();
            InstallHorizontalWheelHook();
        }

        private void EditorPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _autoSaveTimer?.Stop();
            _autoSaveTimer = null;
            _penService?.Dispose();
            _penService = null;
            RemoveHorizontalWheelHook();
        }

        // ─── WM_MOUSEHWHEEL hook for precision touchpad horizontal scrolling ───
        private void InstallHorizontalWheelHook()
        {
            var window = Window.GetWindow(this);
            if (window == null) return;
            _hwndSource = PresentationSource.FromVisual(window) as HwndSource;
            _hwndSource?.AddHook(WndProc);
        }

        private void RemoveHorizontalWheelHook()
        {
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEHWHEEL)
            {
                // wParam high word = horizontal delta (positive = right, negative = left)
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

                // Check if mouse is over our ScrollViewer
                var mousePos = Mouse.GetPosition(PdfScrollViewer);
                if (mousePos.X >= 0 && mousePos.Y >= 0 &&
                    mousePos.X <= PdfScrollViewer.ActualWidth &&
                    mousePos.Y <= PdfScrollViewer.ActualHeight)
                {
                    if (!_smoothScrollInitialized)
                    {
                        _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;
                        _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
                        _smoothScrollInitialized = true;
                    }

                    double scrollAmount = delta * 0.8;
                    _targetHorizontalOffset = Math.Max(0,
                        Math.Min(PdfScrollViewer.ScrollableWidth, _targetHorizontalOffset + scrollAmount));

                    AnimateHorizontalScroll(_targetHorizontalOffset);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private async void AutoSaveTimer_Tick(object sender, EventArgs e)
        {
            var saved = await AutoSaveAsync();
            if (saved)
            {
                var mw = Window.GetWindow(this) as MainWindow;
                mw?.ShowToast("Auto saved", "\uE74E", 1500);
            }
        }

        private void InitializePenService()
        {
            if (_penService != null)
            {
                Console.WriteLine("[EditorPage] WindowsPenService already initialized, skipping");
                return;
            }

            var window = Window.GetWindow(this);
            Console.WriteLine($"[EditorPage] InitializePenService – Window={window?.GetType().Name ?? "NULL"}");

            _penService = new WindowsPenService();
            _penService.ToolToggleRequested += PenService_ToolToggleRequested;
            _penService.PenDeviceDetected += PenService_PenDeviceDetected;
            _penService.Initialize(window);
            Console.WriteLine("[EditorPage] WindowsPenService.Initialize() returned");

            // Push the pen service to all existing page controls
            PushPenServiceToPages();
        }

        /// <summary>
        /// Propagate the shared <see cref="WindowsPenService"/> to every
        /// <see cref="PdfPageControl"/> currently in the pages container so
        /// they can probe devices and honour pressure/tilt settings.
        /// </summary>
        private void PushPenServiceToPages()
        {
            if (_penService == null) return;
            foreach (var child in PagesContainer.Children)
            {
                if (child is PdfPageControl page)
                    page.SetPenService(_penService);
            }
        }

        private void PenService_ToolToggleRequested(object sender, EventArgs e)
        {
            Console.WriteLine($"[EditorPage] ToolToggleRequested received on thread {System.Threading.Thread.CurrentThread.ManagedThreadId}");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Console.WriteLine($"[EditorPage] ToggleEraserMode executing, current={_currentTool}");
                ToggleEraserMode();
            }));
        }

        private void PenService_PenDeviceDetected(object sender, PenDeviceInfo info)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string brandName = info.PenBrand == PenBrand.Generic ? "Stylus" : info.PenBrand.ToString();
                string features = "";
                if (info.SupportsPressure) features += " pressure";
                if (info.SupportsXTilt || info.SupportsYTilt) features += " tilt";
                if (info.SupportsBarrelButton) features += " barrel-button";
                features = features.Trim();
                if (!string.IsNullOrEmpty(features))
                    features = $" ({features})";

                Console.WriteLine($"[EditorPage] Pen detected: {brandName}{features}");
                var mw = Window.GetWindow(this) as MainWindow;
                mw?.ShowToast($"{brandName} pen detected{features}", "\uEDA4", 2500);
            }));
        }

        private void ToggleEraserMode()
        {
            if (_currentTool == ToolType.Eraser)
            {
                Console.WriteLine($"[EditorPage] Eraser → {_previousTool}");
                ActivateTool(_previousTool);
            }
            else
            {
                Console.WriteLine($"[EditorPage] {_currentTool} → Eraser");
                _previousTool = _currentTool;
                ActivateTool(ToolType.Eraser);
            }
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
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                PerformUndo();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                PerformRedo();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
            {
                SetZoom(1.0);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
            {
                SetZoom(_zoomLevel + ZoomStep);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                SetZoom(_zoomLevel - ZoomStep);
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Zoom around mouse position
                var mousePos = e.GetPosition(PdfScrollViewer);
                double oldZoom = _zoomLevel;
                double delta = e.Delta > 0 ? ZoomStep : -ZoomStep;
                double newZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, _zoomLevel + delta));

                if (Math.Abs(newZoom - oldZoom) > 0.001)
                    ZoomAroundPoint(newZoom, mousePos);

                e.Handled = true;
                return;
            }

            e.Handled = true;

            if (!_smoothScrollInitialized)
            {
                _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
                _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;
                _smoothScrollInitialized = true;
            }

            // Shift+Wheel → horizontal scroll
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                double scrollAmount = -e.Delta * 0.8;
                _targetHorizontalOffset = Math.Max(0,
                    Math.Min(PdfScrollViewer.ScrollableWidth, _targetHorizontalOffset + scrollAmount));

                AnimateHorizontalScroll(_targetHorizontalOffset);
                return;
            }

            // Normal wheel → vertical scroll
            double vScrollAmount = -e.Delta * 0.8;
            _targetVerticalOffset = Math.Max(0,
                Math.Min(PdfScrollViewer.ScrollableHeight, _targetVerticalOffset + vScrollAmount));

            AnimateScroll(_targetVerticalOffset);
        }

        // ─── Zoom around a point (keeps that point stable on screen) ───
        private void ZoomAroundPoint(double newZoom, Point viewportPoint)
        {
            double oldZoom = _zoomLevel;

            // Convert viewport point to content coordinates
            double contentX = (PdfScrollViewer.HorizontalOffset + viewportPoint.X) / oldZoom;
            double contentY = (PdfScrollViewer.VerticalOffset + viewportPoint.Y) / oldZoom;

            // Apply zoom
            _zoomLevel = newZoom;
            PagesZoomTransform.ScaleX = _zoomLevel;
            PagesZoomTransform.ScaleY = _zoomLevel;

            // Force layout so ScrollableHeight/Width update
            PdfScrollViewer.UpdateLayout();

            // Calculate new offsets to keep the point stable
            double newOffsetX = contentX * newZoom - viewportPoint.X;
            double newOffsetY = contentY * newZoom - viewportPoint.Y;

            PdfScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffsetX));
            PdfScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffsetY));

            // Sync smooth scroll target
            _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
            _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;
            _smoothScrollInitialized = true;

            ScheduleReRenderForZoom();

            UpdateZoomLabel();
        }

        // ─── Touch Manipulation (pinch-to-zoom + pan) ───
        private void PdfScrollViewer_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            // Only handle multi-finger gestures (pinch-to-zoom, two-finger pan).
            // Single-touch from M-Pencil (which some Huawei digitizers report as
            // a touch device rather than stylus) must pass through to the InkCanvas.
            if (e.Manipulators.Count() < 2 && _currentTool != ToolType.None)
            {
                e.Cancel();
                return;
            }

            e.ManipulationContainer = PdfScrollViewer;
            e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
            _manipulationBaseZoom = _zoomLevel;
            e.Handled = true;
        }

        private void PdfScrollViewer_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            // Pan (translate)
            double panX = e.DeltaManipulation.Translation.X;
            double panY = e.DeltaManipulation.Translation.Y;

            PdfScrollViewer.ScrollToHorizontalOffset(PdfScrollViewer.HorizontalOffset - panX);
            PdfScrollViewer.ScrollToVerticalOffset(PdfScrollViewer.VerticalOffset - panY);

            // Pinch-to-zoom
            double scaleX = e.DeltaManipulation.Scale.X;
            double scaleY = e.DeltaManipulation.Scale.Y;
            double pinchScale = (scaleX + scaleY) / 2.0;

            if (Math.Abs(pinchScale - 1.0) > 0.001)
            {
                var origin = e.ManipulationOrigin;
                double newZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, _zoomLevel * pinchScale));
                ZoomAroundPoint(newZoom, origin);
            }

            // Sync smooth scroll state
            _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
            _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;
            _smoothScrollInitialized = true;

            e.Handled = true;
        }

        private void PdfScrollViewer_ManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
        {
            // Deceleration for flick-to-scroll
            e.TranslationBehavior.DesiredDeceleration = 0.002; // DIPs per ms^2
            e.ExpansionBehavior.DesiredDeceleration = 0.0001;
            e.Handled = true;
        }

        // ─── Smooth scroll animation (vertical) ───
        private double _scrollAnimationTarget;
        private double _scrollAnimationStart;
        private DateTime _scrollAnimationStartTime;
        private TimeSpan _scrollAnimationDuration;
        private bool _isScrollAnimating;

        // ─── Smooth scroll animation (horizontal) ───
        private double _hScrollAnimationTarget;
        private double _hScrollAnimationStart;
        private DateTime _hScrollAnimationStartTime;
        private TimeSpan _hScrollAnimationDuration;
        private bool _isHScrollAnimating;

        private void AnimateScroll(double toOffset)
        {
            _scrollAnimationTarget = toOffset;
            _scrollAnimationStart = PdfScrollViewer.VerticalOffset;
            _scrollAnimationStartTime = DateTime.UtcNow;
            _scrollAnimationDuration = TimeSpan.FromMilliseconds(180);

            if (!_isScrollAnimating)
            {
                _isScrollAnimating = true;
                System.Windows.Media.CompositionTarget.Rendering += CompositionTarget_ScrollRendering;
            }
        }

        private void AnimateHorizontalScroll(double toOffset)
        {
            _hScrollAnimationTarget = toOffset;
            _hScrollAnimationStart = PdfScrollViewer.HorizontalOffset;
            _hScrollAnimationStartTime = DateTime.UtcNow;
            _hScrollAnimationDuration = TimeSpan.FromMilliseconds(180);

            if (!_isHScrollAnimating)
            {
                _isHScrollAnimating = true;
                System.Windows.Media.CompositionTarget.Rendering += CompositionTarget_HScrollRendering;
            }
        }

        private void CompositionTarget_HScrollRendering(object sender, EventArgs e)
        {
            var elapsed = DateTime.UtcNow - _hScrollAnimationStartTime;
            double progress = Math.Min(1.0, elapsed.TotalMilliseconds / _hScrollAnimationDuration.TotalMilliseconds);
            double easedProgress = 1.0 - Math.Pow(1.0 - progress, 3);

            double currentOffset = _hScrollAnimationStart + (_hScrollAnimationTarget - _hScrollAnimationStart) * easedProgress;
            PdfScrollViewer.ScrollToHorizontalOffset(currentOffset);

            if (progress >= 1.0)
            {
                _isHScrollAnimating = false;
                System.Windows.Media.CompositionTarget.Rendering -= CompositionTarget_HScrollRendering;
            }
        }

        private void CompositionTarget_ScrollRendering(object sender, EventArgs e)
        {
            var elapsed = DateTime.UtcNow - _scrollAnimationStartTime;
            double progress = Math.Min(1.0, elapsed.TotalMilliseconds / _scrollAnimationDuration.TotalMilliseconds);
            double easedProgress = 1.0 - Math.Pow(1.0 - progress, 3);

            double currentOffset = _scrollAnimationStart + (_scrollAnimationTarget - _scrollAnimationStart) * easedProgress;
            PdfScrollViewer.ScrollToVerticalOffset(currentOffset);

            if (progress >= 1.0)
            {
                _isScrollAnimating = false;
                System.Windows.Media.CompositionTarget.Rendering -= CompositionTarget_ScrollRendering;
            }
        }

        private void PdfScrollViewer_PreviewStylusDown(object sender, StylusDownEventArgs e)
        {
            // Only handle pen (not finger touch) for stylus-drag scrolling.
            // Finger touch should go through to ManipulationDelta for pinch-to-zoom.
            // Include both Stylus and Touch tablet types — some Huawei MateBook
            // digitizers report the M-Pencil as Touch rather than Stylus.
            bool isPenDevice = e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus;

            // Heuristic: single-point non-finger device is likely a pen.
            // This catches Huawei M-Pencil on MateBooks that report as Touch.
            if (!isPenDevice && e.StylusDevice != null)
            {
                var tabletDevice = e.StylusDevice.TabletDevice;
                // A real finger touch typically has TabletDeviceType.Touch.
                // But a pen-as-touch has a single StylusDevice with StylusButtons
                // (real fingers don't have buttons). Check for barrel/eraser buttons.
                if (tabletDevice != null && e.StylusDevice.StylusButtons.Count > 1)
                {
                    isPenDevice = true;
                    Console.WriteLine($"[EditorPage] Detected pen-as-touch device: {tabletDevice.Name}, buttons={e.StylusDevice.StylusButtons.Count}");
                }
            }

            if (_currentTool == ToolType.None && isPenDevice)
            {
                _isPenScrolling = true;
                _penScrollStartPoint = e.GetPosition(PdfScrollViewer);
                _penScrollStartVerticalOffset = PdfScrollViewer.VerticalOffset;
                _penScrollStartHorizontalOffset = PdfScrollViewer.HorizontalOffset;
                PdfScrollViewer.CaptureStylus();
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewStylusMove(object sender, StylusEventArgs e)
        {
            if (_isPenScrolling && _currentTool == ToolType.None)
            {
                Point currentPoint = e.GetPosition(PdfScrollViewer);
                double deltaY = currentPoint.Y - _penScrollStartPoint.Y;
                double deltaX = currentPoint.X - _penScrollStartPoint.X;

                PdfScrollViewer.ScrollToVerticalOffset(_penScrollStartVerticalOffset - deltaY);
                PdfScrollViewer.ScrollToHorizontalOffset(_penScrollStartHorizontalOffset - deltaX);
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewStylusUp(object sender, StylusEventArgs e)
        {
            if (_isPenScrolling)
            {
                _isPenScrolling = false;
                PdfScrollViewer.ReleaseStylusCapture();
                e.Handled = true;
            }
        }

        // ─── Middle mouse button panning ───
        private void PdfScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                _isMiddleMousePanning = true;
                _middleMouseStartPoint = e.GetPosition(PdfScrollViewer);
                _middleMouseStartVerticalOffset = PdfScrollViewer.VerticalOffset;
                _middleMouseStartHorizontalOffset = PdfScrollViewer.HorizontalOffset;
                PdfScrollViewer.CaptureMouse();
                PdfScrollViewer.Cursor = Cursors.ScrollAll;
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isMiddleMousePanning)
            {
                Point currentPoint = e.GetPosition(PdfScrollViewer);
                double deltaY = currentPoint.Y - _middleMouseStartPoint.Y;
                double deltaX = currentPoint.X - _middleMouseStartPoint.X;

                PdfScrollViewer.ScrollToVerticalOffset(_middleMouseStartVerticalOffset - deltaY);
                PdfScrollViewer.ScrollToHorizontalOffset(_middleMouseStartHorizontalOffset - deltaX);
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isMiddleMousePanning && e.ChangedButton == MouseButton.Middle)
            {
                _isMiddleMousePanning = false;
                PdfScrollViewer.ReleaseMouseCapture();
                PdfScrollViewer.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        private void SetZoom(double level)
        {
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            PagesZoomTransform.ScaleX = _zoomLevel;
            PagesZoomTransform.ScaleY = _zoomLevel;

            UpdateZoomLabel();

            // Sync smooth scroll state
            _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
            _smoothScrollInitialized = true;

            // Re-render pages at higher DPI when zoomed in (debounced)
            ScheduleReRenderForZoom();
        }

        private async void ScheduleReRenderForZoom()
        {
            // Cancel any pending re-render
            _reRenderCts?.Cancel();
            _reRenderCts = new CancellationTokenSource();
            var token = _reRenderCts.Token;

            try
            {
                // Debounce: wait 250ms after last zoom change
                await Task.Delay(250, token);
                token.ThrowIfCancellationRequested();

                // Only re-render if zoom changed significantly from last render
                double neededScale = Math.Max(_zoomLevel, 1.0);
                // Clamp max re-render to 3x to avoid huge memory usage
                neededScale = Math.Min(neededScale, 3.0);

                if (Math.Abs(neededScale - _lastRenderedDpiScale) < 0.15)
                    return; // Not enough difference to warrant re-render

                _lastRenderedDpiScale = neededScale;
                _pagesRenderedAtScale.Clear();

                // Only re-render pages currently visible in the viewport
                var visiblePages = GetVisiblePageControls();
                await ReRenderPagesAsync(visiblePages, neededScale, token);
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Called on scroll — lazily render pages that haven't been rendered yet
        /// (progressive loading) and re-render at higher DPI when zoomed in.
        /// Uses a scroll-anchor to prevent the view from jumping when off-screen
        /// pages change size during rendering.
        /// </summary>
        private async void PdfScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            _scrollReRenderCts?.Cancel();
            _scrollReRenderCts = new CancellationTokenSource();
            var token = _scrollReRenderCts.Token;

            try
            {
                // Small debounce so we don't re-render on every scroll tick
                await Task.Delay(100, token);
                token.ThrowIfCancellationRequested();

                var visiblePages = GetVisiblePageControls();

                // Progressive loading: render pages that haven't been rendered at all yet
                var needsInitialRender = visiblePages
                    .Where(p => !_pagesInitiallyRendered.Contains(p.PageIndex))
                    .ToList();

                if (needsInitialRender.Count > 0)
                {
                    // Snapshot anchor before any rendering
                    var anchor = CaptureScrollAnchor();

                    foreach (var page in needsInitialRender)
                    {
                        token.ThrowIfCancellationRequested();
                        await RenderPageInitialAsync(page, token);
                    }

                    // Restore anchor – this keeps the user's view rock-steady
                    RestoreScrollAnchor(anchor);
                }

                // Zoom re-render: upgrade pages to higher DPI when zoomed in
                if (_lastRenderedDpiScale > 1.0 && _pagesRenderedAtScale.Count < PagesContainer.Children.Count)
                {
                    var needsZoomRender = visiblePages
                        .Where(p => !_pagesRenderedAtScale.Contains(p.PageIndex))
                        .ToList();

                    if (needsZoomRender.Count > 0)
                        await ReRenderPagesAsync(needsZoomRender, _lastRenderedDpiScale, token);
                }
            }
            catch (OperationCanceledException) { }
        }

        // ─── Scroll Anchor: keeps the user's view locked in place ───

        private struct ScrollAnchor
        {
            public PdfPageControl AnchorPage;
            public double OffsetFromViewportTop; // how far the page top sits from the viewport top (can be negative)
        }

        /// <summary>
        /// Captures the page closest to the top of the viewport and its exact
        /// y-offset relative to the viewport. This lets us restore the exact
        /// same view after layout changes.
        /// </summary>
        private ScrollAnchor CaptureScrollAnchor()
        {
            var anchor = new ScrollAnchor();
            double bestDist = double.MaxValue;

            foreach (UIElement child in PagesContainer.Children)
            {
                if (child is PdfPageControl page)
                {
                    try
                    {
                        var transform = page.TransformToAncestor(PdfScrollViewer);
                        var topLeft = transform.Transform(new Point(0, 0));
                        // topLeft.Y is the position of the page top relative to the viewport top
                        // (negative = page starts above viewport, positive = below)
                        double dist = Math.Abs(topLeft.Y);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            anchor.AnchorPage = page;
                            anchor.OffsetFromViewportTop = topLeft.Y;
                        }
                    }
                    catch { }
                }
            }

            return anchor;
        }

        /// <summary>
        /// After layout has changed (pages rendered / resized), snaps the
        /// ScrollViewer so the anchor page sits at the same viewport position.
        /// </summary>
        private void RestoreScrollAnchor(ScrollAnchor anchor)
        {
            if (anchor.AnchorPage == null) return;

            try
            {
                PdfScrollViewer.UpdateLayout();
                var transform = anchor.AnchorPage.TransformToAncestor(PdfScrollViewer);
                var currentTopLeft = transform.Transform(new Point(0, 0));

                double drift = currentTopLeft.Y - anchor.OffsetFromViewportTop;
                if (Math.Abs(drift) > 0.5)
                {
                    double newOffset = PdfScrollViewer.VerticalOffset + drift;
                    PdfScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffset));
                    _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
                }
            }
            catch { /* page may no longer be in tree */ }
        }

        /// <summary>
        /// Re-renders a list of page controls at the given DPI scale using
        /// the fast BitmapSource path (no PNG encode/decode).
        /// </summary>
        private async Task ReRenderPagesAsync(List<PdfPageControl> pages, double dpiScale, CancellationToken token)
        {
            foreach (var page in pages)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var bitmapSource = await _pdfService.RenderPageBitmapSourceAsync(page.PageIndex, dpiScale, token);
                    if (bitmapSource != null)
                    {
                        token.ThrowIfCancellationRequested();
                        page.PageSource = bitmapSource;
                        _pagesRenderedAtScale.Add(page.PageIndex);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip failed page, keep going */ }
            }
        }

        /// <summary>
        /// Returns PdfPageControl instances that are currently visible (or nearly visible)
        /// in the ScrollViewer viewport.
        /// </summary>
        private List<PdfPageControl> GetVisiblePageControls()
        {
            var result = new List<PdfPageControl>();
            if (PagesContainer.Children.Count == 0) return result;

            double viewTop = PdfScrollViewer.VerticalOffset;
            double viewBottom = viewTop + PdfScrollViewer.ViewportHeight;

            // Add generous margin (~half viewport) so we pre-render pages just off-screen
            double margin = PdfScrollViewer.ViewportHeight * 0.5;
            viewTop = Math.Max(0, viewTop - margin);
            viewBottom += margin;

            foreach (UIElement child in PagesContainer.Children)
            {
                if (child is PdfPageControl page)
                {
                    // Get the page's position relative to the ScrollViewer
                    var transform = page.TransformToAncestor(PdfScrollViewer);
                    var topLeft = transform.Transform(new Point(0, 0));
                    double pageTop = topLeft.Y + PdfScrollViewer.VerticalOffset;
                    double pageBottom = pageTop + page.ActualHeight * _zoomLevel;

                    // Check overlap with viewport
                    if (pageBottom >= viewTop && pageTop <= viewBottom)
                        result.Add(page);
                }
            }

            return result;
        }

        private void PerformUndo()
        {
            if (_undoStack.Count == 0) return;
            var action = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            action.Undo();
            _redoStack.Add(action);
            UpdateUndoRedoButtons();
            MarkDirty();
        }

        private void PerformRedo()
        {
            if (_redoStack.Count == 0) return;
            var action = _redoStack[_redoStack.Count - 1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            action.Redo();
            _undoStack.Add(action);
            UpdateUndoRedoButtons();
            MarkDirty();
        }

        private void UpdateUndoRedoButtons()
        {
            UndoButton.IsEnabled = _undoStack.Count > 0;
            RedoButton.IsEnabled = _redoStack.Count > 0;
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e) => PerformUndo();
        private void RedoButton_Click(object sender, RoutedEventArgs e) => PerformRedo();

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

        // Tracks which pages have been rendered with their initial image
        private readonly HashSet<int> _pagesInitiallyRendered = new HashSet<int>();

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
            _lastRenderedDpiScale = 1.0;
            _pagesRenderedAtScale.Clear();
            _pagesInitiallyRendered.Clear();

            try
            {
                await _pdfService.LoadPdfAsync(filePath, token);

                int pageCount = _pdfService.PageCount;

                // Phase 1: Create placeholder page controls with correct dimensions
                // (fast – no rendering). This lets the UI appear quickly even for
                // very large documents.
                for (int i = 0; i < pageCount; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var (w, h) = _pdfService.GetPageSizeInDips(i);
                    if (w <= 0 || h <= 0)
                    {
                        w = 1584; // A4 at 192 DPI fallback
                        h = 2245;
                    }

                    var pageControl = new PdfPageControl
                    {
                        PageIndex = i,
                        Width = w,
                        Height = h
                    };

                    pageControl.TextOverlayPointerPressed += PageControl_TextOverlayPointerPressed;
                    pageControl.BackgroundPointerPressed += PageControl_BackgroundPointerPressed;
                    pageControl.InkMutated += PageControl_InkMutated;
                    pageControl.StrokeCollectedUndoable += PageControl_StrokeCollectedUndoable;
                    pageControl.ModeChanged += PageControl_ModeChanged;

                    if (_penService != null)
                        pageControl.SetPenService(_penService);

                    PagesContainer.Children.Add(pageControl);
                }

                ApplyToolToAllPages();

                // Force layout so TransformToAncestor works for visibility checks
                PdfScrollViewer.UpdateLayout();

                // Reset scroll position: top-left, centered horizontally
                PdfScrollViewer.ScrollToVerticalOffset(0);
                PdfScrollViewer.ScrollToHorizontalOffset(0);
                _targetVerticalOffset = 0;
                _targetHorizontalOffset = 0;
                _smoothScrollInitialized = true;

                // Phase 2: Render only the initially visible pages (typically 1-3)
                var visiblePages = GetVisiblePageControls();
                foreach (var page in visiblePages)
                {
                    token.ThrowIfCancellationRequested();
                    await RenderPageInitialAsync(page, token);
                }

                // Phase 3: Load annotations
                if (!string.IsNullOrEmpty(_currentPdfPath))
                {
                    await LoadAnnotationsFromPdfServiceAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // silently handle cancellation
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to load PDF: {ex.Message}";
                if (ex.InnerException != null)
                    errorMsg += $"\n\nDetails: {ex.InnerException.Message}";

                if (sessionId == _loadSessionId)
                {
                    MessageBox.Show(errorMsg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                if (sessionId == _loadSessionId)
                    HideLoadingOverlay();
            }
        }

        /// <summary>
        /// Renders a single page's initial image using the fast BitmapSource path.
        /// Only renders if the page hasn't been rendered yet.
        /// Does NOT adjust scroll — the caller is responsible for anchor save/restore.
        /// </summary>
        private async Task RenderPageInitialAsync(PdfPageControl page, CancellationToken token)
        {
            if (_pagesInitiallyRendered.Contains(page.PageIndex)) return;

            try
            {
                var bitmapSource = await _pdfService.RenderPageBitmapSourceAsync(page.PageIndex, 1.0, token);
                if (bitmapSource != null)
                {
                    token.ThrowIfCancellationRequested();
                    page.PageSource = bitmapSource;
                    page.Width = bitmapSource.Width;
                    page.Height = bitmapSource.Height;
                    _pagesInitiallyRendered.Add(page.PageIndex);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RenderPageInitialAsync page {page.PageIndex} failed: {ex.Message}");
            }
        }

        private void PageControl_ModeChanged(object sender, CustomInkInputProcessingMode mode)
        {
            // Update UI when mode changes from double tap
            if (mode == CustomInkInputProcessingMode.Erasing && _currentTool != ToolType.Eraser)
            {
                _previousTool = _currentTool;
                ActivateTool(ToolType.Eraser);
            }
            else if (mode == CustomInkInputProcessingMode.Inking && _currentTool == ToolType.Eraser)
            {
                ActivateTool(_previousTool);
            }
        }

        private void PenToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            if (_currentTool == ToolType.Pen)
            {
                ActivateTool(ToolType.None);
            }
            else
            {
                ActivateTool(ToolType.Pen);
                _penPopup.PlacementTarget = PenToolButton;
                _penPopup.IsOpen = true;
            }
        }

        private void HighlighterToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            if (_currentTool == ToolType.Highlighter)
            {
                ActivateTool(ToolType.None);
            }
            else
            {
                ActivateTool(ToolType.Highlighter);
                _highlighterPopup.PlacementTarget = HighlighterToolButton;
                _highlighterPopup.IsOpen = true;
            }
        }

        private void EraserToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            if (_currentTool == ToolType.Eraser)
            {
                ActivateTool(ToolType.None);
            }
            else
            {
                ActivateTool(ToolType.Eraser);
                _eraserPopup.PlacementTarget = EraserToolButton;
                _eraserPopup.IsOpen = true;
            }
        }

        private void TextToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingToolState) return;
            if (_currentTool == ToolType.Text)
            {
                ActivateTool(ToolType.None);
            }
            else
            {
                ActivateTool(ToolType.Text);
            }
        }

        private void ActivateTool(ToolType tool)
        {
            if (_currentTool == tool) return;

            if (tool != ToolType.Text)
                DeselectTextBox();

            _isUpdatingToolState = true;
            _currentTool = tool;

            PenToolButton.Background = tool == ToolType.Pen ? new SolidColorBrush(Color.FromArgb(34, 0, 120, 212)) : Brushes.Transparent;
            HighlighterToolButton.Background = tool == ToolType.Highlighter ? new SolidColorBrush(Color.FromArgb(34, 0, 120, 212)) : Brushes.Transparent;
            EraserToolButton.Background = tool == ToolType.Eraser ? new SolidColorBrush(Color.FromArgb(34, 0, 120, 212)) : Brushes.Transparent;
            TextToolButton.Background = tool == ToolType.Text ? new SolidColorBrush(Color.FromArgb(34, 0, 120, 212)) : Brushes.Transparent;

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
            if (_isDirty && !string.IsNullOrEmpty(_currentPdfPath))
            {
                await AutoSaveAsync();
                GetMainWindow()?.ShowToast("File auto saved");
            }
            NavigateBackCore();
        }

        private async void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            await SaveAnnotationsToPdfAsync();
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            var center = new Point(PdfScrollViewer.ViewportWidth / 2, PdfScrollViewer.ViewportHeight / 2);
            double newZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, _zoomLevel + ZoomStep));
            ZoomAroundPoint(newZoom, center);
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            var center = new Point(PdfScrollViewer.ViewportWidth / 2, PdfScrollViewer.ViewportHeight / 2);
            double newZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, _zoomLevel - ZoomStep));
            ZoomAroundPoint(newZoom, center);
        }

        private void ZoomLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Show editable text box with current percentage
            ZoomLabel.Visibility = Visibility.Collapsed;
            ZoomTextBox.Text = $"{(int)Math.Round(_zoomLevel * 100)}";
            ZoomTextBox.Visibility = Visibility.Visible;
            ZoomTextBox.Focus();
            ZoomTextBox.SelectAll();
        }

        private void ZoomTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyZoomFromTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideZoomTextBox();
                e.Handled = true;
            }
        }

        private void ZoomTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyZoomFromTextBox();
        }

        private void ApplyZoomFromTextBox()
        {
            var text = ZoomTextBox.Text.Trim().TrimEnd('%');
            if (int.TryParse(text, out int pct) && pct >= (int)(ZoomMin * 100) && pct <= (int)(ZoomMax * 100))
            {
                var center = new Point(PdfScrollViewer.ViewportWidth / 2, PdfScrollViewer.ViewportHeight / 2);
                ZoomAroundPoint(pct / 100.0, center);
            }
            HideZoomTextBox();
        }

        private void HideZoomTextBox()
        {
            ZoomTextBox.Visibility = Visibility.Collapsed;
            ZoomLabel.Visibility = Visibility.Visible;
        }

        private void UpdateZoomLabel()
        {
            if (ZoomLabel != null)
                ZoomLabel.Text = $"{(int)Math.Round(_zoomLevel * 100)}%";
        }

        private void InitializeTextBoxPopup()
        {
            _textBoxPopup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, VerticalOffset = 6 };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 6) };
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 4,
                    Opacity = 0.14,
                    Color = Colors.Black
                }
            };

            // Delete button - clean icon
            var deleteButton = new Button
            {
                Width = 32, Height = 32,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Delete",
                Margin = new Thickness(2)
            };
            deleteButton.Template = CreateIconButtonTemplate("#FECACA", "#FCA5A5");
            deleteButton.Content = new TextBlock
            {
                Text = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            deleteButton.Click += (s, e) => DeleteSelectedTextBox();

            // Separator
            var sep1 = new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)), Margin = new Thickness(4, 6, 4, 6) };

            _fontSizeComboBox = new ComboBox
            {
                Width = 72, Margin = new Thickness(2),
                Style = (Style)Application.Current.FindResource("ModernComboBox")
            };
            foreach (var size in new[] { 12, 18, 24, 36, 48, 72 })
            {
                var item = new ComboBoxItem
                {
                    Content = size.ToString(),
                    Style = (Style)Application.Current.FindResource("ModernComboBoxItem")
                };
                _fontSizeComboBox.Items.Add(item);
            }
            _fontSizeComboBox.SelectionChanged += FontSizeComboBox_SelectionChanged;

            // Separator
            var sep2 = new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)), Margin = new Thickness(4, 6, 4, 6) };

            _colorIndicator = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(_textColor),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                BorderThickness = new Thickness(1.5)
            };
            var colorButton = new Button
            {
                Content = _colorIndicator, Width = 34, Height = 32,
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Margin = new Thickness(2)
            };
            colorButton.Template = CreateIconButtonTemplate("#E8E8E8", "#DCDCDC");
            var colorPopup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            var colorPanel = new WrapPanel { Margin = new Thickness(10), Width = 210 };
            var colorBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = colorPanel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.14, Color = Colors.Black }
            };

            var textColors = new[] { Colors.Black, Colors.White, Color.FromRgb(220, 38, 38), Color.FromRgb(37, 99, 235), Color.FromRgb(22, 163, 74), Color.FromRgb(245, 158, 11), Color.FromRgb(147, 51, 234), Color.FromRgb(127, 29, 29), Color.FromRgb(14, 116, 144), Color.FromRgb(161, 98, 7) };
            foreach (var c in textColors)
            {
                var isLight = c.R > 220 && c.G > 220 && c.B > 220;
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = new SolidColorBrush(c),
                    Margin = new Thickness(3),
                    Cursor = Cursors.Hand,
                    Tag = c,
                    BorderBrush = isLight ? new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) : Brushes.Transparent,
                    BorderThickness = new Thickness(isLight ? 1.5 : 0)
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
            panel.Children.Add(sep1);
            panel.Children.Add(_fontSizeComboBox);
            panel.Children.Add(sep2);
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
            var content = _fontSizeComboBox.SelectedItem is ComboBoxItem cbi ? cbi.Content?.ToString() : _fontSizeComboBox.SelectedItem.ToString();
            if (double.TryParse(content, out var size))
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
            textBox.BorderThickness = new Thickness(0);
            textBox.Background = Brushes.Transparent;

            if (textBox.Parent is Grid container)
            {
                foreach (UIElement child in container.Children)
                {
                    if (child is Border b && !b.IsHitTestVisible && b.Tag is string tag && tag == "chrome")
                    {
                        b.BorderBrush = isSelected
                            ? new SolidColorBrush(Color.FromArgb(90, 0, 120, 212))
                            : Brushes.Transparent;
                        b.BorderThickness = isSelected ? new Thickness(1.5) : new Thickness(0);
                        b.Background = isSelected
                            ? new SolidColorBrush(Color.FromArgb(10, 0, 120, 212))
                            : Brushes.Transparent;
                    }
                    else if (child is Border handle && handle.Cursor == Cursors.SizeAll)
                    {
                        handle.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }

        private void SyncPopupToSelectedTextBox()
        {
            if (_selectedTextBox == null) return;

            var size = _selectedTextBox.FontSize;
            var sizes = new[] { 12d, 18d, 24d, 36d, 48d, 72d };
            var nearest = sizes.OrderBy(s => Math.Abs(s - size)).First();

            for (int i = 0; i < _fontSizeComboBox.Items.Count; i++)
            {
                var item = _fontSizeComboBox.Items[i];
                var content = item is ComboBoxItem cbi ? cbi.Content?.ToString() : item.ToString();
                if (content == nearest.ToString())
                {
                    _fontSizeComboBox.SelectedIndex = i;
                    break;
                }
            }

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

            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Visual chrome border spanning both rows
            var chrome = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = select ? new Thickness(1.5) : new Thickness(0),
                BorderBrush = select ? new SolidColorBrush(Color.FromArgb(90, 0, 120, 212)) : Brushes.Transparent,
                Background = select ? new SolidColorBrush(Color.FromArgb(10, 0, 120, 212)) : Brushes.Transparent,
                IsHitTestVisible = false,
                Tag = "chrome"
            };
            Grid.SetRowSpan(chrome, 2);

            var textBox = new TextBox
            {
                Text = text ?? "Text",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 100,
                MinHeight = 30,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = fontSize ?? _currentFontSize,
                Foreground = new SolidColorBrush(color ?? _textColor),
                IsReadOnly = !select,
                Padding = new Thickness(10, 8, 10, 8),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212))
            };

            var dragHandle = new Border
            {
                Height = 16,
                Visibility = select ? Visibility.Visible : Visibility.Collapsed,
                Cursor = Cursors.SizeAll,
                Background = Brushes.Transparent
            };

            var dragIcon = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            for (int i = 0; i < 3; i++)
            {
                dragIcon.Children.Add(new Ellipse
                {
                    Width = 3, Height = 3,
                    Fill = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    Margin = new Thickness(1.5, 0, 1.5, 0)
                });
            }
            dragHandle.Child = dragIcon;

            Grid.SetRow(textBox, 0);
            Grid.SetRow(dragHandle, 1);

            container.Children.Add(chrome);
            container.Children.Add(textBox);
            container.Children.Add(dragHandle);

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
            if (handle?.Parent is Grid container)
            {
                var tb = container.Children.OfType<TextBox>().FirstOrDefault();
                if (tb != null) SelectTextBox(tb);
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
                    var handle = _draggedContainer.Children.OfType<Border>().FirstOrDefault(b => b.Cursor == Cursors.SizeAll);
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
                if (wasDragging)
                {
                    var tb = _draggedContainer.Children.OfType<TextBox>().FirstOrDefault();
                    if (tb != null)
                    {
                        _textBoxPopup.PlacementTarget = _draggedContainer;
                        _textBoxPopup.IsOpen = true;
                        tb.Focus();
                    }
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
                GetMainWindow()?.ShowToast("No PDF is currently loaded", "\uE783");
                return;
            }

            try
            {
                ShowLoadingOverlay();
                var annotations = CollectAnnotations();

                await _pdfService.SaveAnnotationsToPdfAsync(_currentPdfPath, annotations);
                _isDirty = false;

                HideLoadingOverlay();
                GetMainWindow()?.ShowToast("Saved successfully");
            }
            catch (Exception ex)
            {
                HideLoadingOverlay();
                MessageBox.Show($"Failed to save annotations: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task<bool> AutoSaveAsync()
        {
            if (!_isDirty || string.IsNullOrEmpty(_currentPdfPath)) return false;
            try
            {
                var annotations = CollectAnnotations();
                await _pdfService.SaveAnnotationsToPdfAsync(_currentPdfPath, annotations);
                _isDirty = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private Dictionary<int, PageAnnotation> CollectAnnotations()
        {
            var annotations = new Dictionary<int, PageAnnotation>();
            foreach (var child in PagesContainer.Children)
            {
                if (child is PdfPageControl page)
                {
                    var pa = new PageAnnotation();
                    pa.Strokes = page.GetStrokeData();

                    foreach (var element in page.TextOverlay.Children)
                    {
                        var containerTb = (element is Grid container) ? container.Children.OfType<TextBox>().FirstOrDefault() : null;
                        if (containerTb != null)
                        {
                            var color = (containerTb.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
                            pa.Texts.Add(new TextAnnotation
                            {
                                Text = containerTb.Text,
                                X = Canvas.GetLeft((Grid)containerTb.Parent),
                                Y = Canvas.GetTop((Grid)containerTb.Parent),
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
            return annotations;
        }

        private MainWindow GetMainWindow()
        {
            return Application.Current.MainWindow as MainWindow;
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

        private void PageControl_StrokeCollectedUndoable(object sender, System.Windows.Ink.Stroke stroke)
        {
            if (sender is PdfPageControl page)
            {
                _undoStack.Add(new StrokeAddedAction(page, stroke));
                _redoStack.Clear();
                UpdateUndoRedoButtons();
            }
        }

        private void MarkDirty() => _isDirty = true;

        private void NavigateBackCore()
        {
            if (NavigationService != null && NavigationService.CanGoBack)
                NavigationService.GoBack();
            else if (NavigationService != null)
                NavigationService.Navigate(new HomePage());
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
                    pageControl.StrokeCollectedUndoable -= PageControl_StrokeCollectedUndoable;
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

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private void FixPopupTopmost(Popup popup)
        {
            popup.Opened += (s, e) =>
            {
                var source = PresentationSource.FromVisual(popup.Child) as System.Windows.Interop.HwndSource;
                if (source != null)
                    SetWindowPos(source.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0010 | 0x0002 | 0x0001);
            };
        }

        private static ControlTemplate CreateIconButtonTemplate(string hoverColor, string pressedColor)
        {
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(4));
            borderFactory.Name = "Root";

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);

            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverColor)), "Root"));
            template.Triggers.Add(hoverTrigger);

            var pressTrigger = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(pressedColor)), "Root"));
            template.Triggers.Add(pressTrigger);

            return template;
        }
    }
}
