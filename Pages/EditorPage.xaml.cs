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
using Caelum.Controls;
using Caelum.Models;
using Caelum.Services;

namespace Caelum.Pages
{
    public sealed partial class EditorPage : Page
    {
        private enum ToolType { None, Pen, Highlighter, Eraser, Text, Select, TextHighlight }

        private ToolType _currentTool = ToolType.None;
        private ToolType _previousTool = ToolType.Pen;
        private Color _penColor = Colors.Black;
        private Color _highlighterColor = Colors.Yellow;
        private Color _textColor = Colors.Black;
        private double _penSize = 1.5;
        private double _highlighterSize = 8.0;
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
        private Popup _selectionPopup;       // settings popup (opens on button click)
        private Popup _selectionActionPopup; // action popup (opens when selection exists)
        private Button _scaleUpButton;
        private Button _scaleDownButton;
        private PdfPageControl _activeSelectionPage;

        private const double PdfTextSelectionDragThreshold = 4.0;
        private PdfPageControl _pdfTextSelectionPage;
        private PdfService.PdfPageTextInfo _pdfTextSelectionInfo;
        private Point _pdfTextSelectionPressPoint;
        private int _pdfTextSelectionAnchorOffset = -1;
        private int _pdfTextSelectionActiveOffset = -1;
        private bool _isPdfTextSelectionDragging;
        private bool _pdfTextSelectionExceededThreshold;
        private int _pdfTextSelectionRequestId;
        private string _selectedPdfText;

        private bool _isDragging;
        private bool _dragArmed;
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
        private class SelectionMoveAction : IUndoAction
        {
            private readonly PdfPageControl _page;
            private readonly double _deltaX;
            private readonly double _deltaY;
            private readonly List<System.Windows.Ink.Stroke> _strokes;
            private readonly List<System.Windows.Controls.Grid> _containers;
            public SelectionMoveAction(PdfPageControl page, double deltaX, double deltaY,
                List<System.Windows.Ink.Stroke> strokes, List<System.Windows.Controls.Grid> containers)
            {
                _page = page;
                _deltaX = deltaX;
                _deltaY = deltaY;
                _strokes = strokes;
                _containers = containers;
            }
            public void Undo() => _page.MoveItemsDirectly(_strokes, _containers, -_deltaX, -_deltaY);
            public void Redo() => _page.MoveItemsDirectly(_strokes, _containers, _deltaX, _deltaY);
        }
        private class SelectionResizeAction : IUndoAction
        {
            private readonly PdfPageControl _page;
            private readonly double _totalScale;
            private readonly System.Windows.Point _anchor;
            private readonly List<System.Windows.Ink.Stroke> _strokes;
            private readonly List<System.Windows.Controls.Grid> _containers;
            public SelectionResizeAction(PdfPageControl page, double totalScale, System.Windows.Point anchor,
                List<System.Windows.Ink.Stroke> strokes, List<System.Windows.Controls.Grid> containers)
            {
                _page = page;
                _totalScale = totalScale;
                _anchor = anchor;
                _strokes = strokes;
                _containers = containers;
            }
            public void Undo() => _page.ScaleItemsDirectly(_strokes, _containers, 1.0 / _totalScale, _anchor);
            public void Redo() => _page.ScaleItemsDirectly(_strokes, _containers, _totalScale, _anchor);
        }
        private readonly List<IUndoAction> _undoStack = new List<IUndoAction>();
        private readonly List<IUndoAction> _redoStack = new List<IUndoAction>();

        // Selection tool state
        private SelectionFilter _selectionFilter = SelectionFilter.Both;
        private SelectionShape _selectionShape = SelectionShape.Rectangle;

        private double _zoomLevel = 1.0;
        private double _lastRenderedDpiScale = 1.0;
        private CancellationTokenSource _reRenderCts;

        // Tracks which pages have been re-rendered at the current _lastRenderedDpiScale
        private readonly HashSet<int> _pagesRenderedAtScale = new HashSet<int>();
        private readonly List<PdfPageControl> _pageControls = new List<PdfPageControl>();
        private readonly List<double> _pageTopOffsets = new List<double>();
        private readonly List<double> _pageHeights = new List<double>();
        private CancellationTokenSource _scrollReRenderCts;
        private const double PageSpacing = 20.0;

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
        private int _activeTouchCount;

        // Raw pinch-zoom tracking (bypasses WPF manipulation system so the
        // first finger can still reach InkCanvas while two fingers zoom)
        private readonly Dictionary<int, Point> _activeTouches = new Dictionary<int, Point>();
        private double _pinchStartDistance;
        private double _pinchStartZoom;
        private bool _isPinchActive;

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
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int MK_CONTROL = 0x0008;

        public EditorPage()
        {
            InitializeComponent();
            InitializeTextBoxPopup();
            CreateToolPopups();
            ApplyLocalization();

            _pdfService = new PdfService();
            ActivateTool(ToolType.None);

            FixPopupTopmost(_textBoxPopup);
            FixPopupTopmost(_penPopup);
            FixPopupTopmost(_highlighterPopup);
            FixPopupTopmost(_eraserPopup);
            FixPopupTopmost(_selectionPopup);
            FixPopupTopmost(_selectionActionPopup);

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
            ClearPdfTextSelection();
        }

        private void ClearPdfTextSelection(bool clearCopiedText = true)
        {
            Interlocked.Increment(ref _pdfTextSelectionRequestId);

            foreach (var page in _pageControls)
                page.ClearPdfTextSelection();

            _pdfTextSelectionPage = null;
            _pdfTextSelectionInfo = null;
            _pdfTextSelectionAnchorOffset = -1;
            _pdfTextSelectionActiveOffset = -1;
            _isPdfTextSelectionDragging = false;
            _pdfTextSelectionExceededThreshold = false;

            if (clearCopiedText)
                _selectedPdfText = null;
        }

        private bool TryCopySelectedPdfTextToClipboard()
        {
            if ((_currentTool != ToolType.None && _currentTool != ToolType.Select) || string.IsNullOrEmpty(_selectedPdfText))
                return false;

            try
            {
                Clipboard.SetText(_selectedPdfText);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool RectContainsWithPadding(Rect rect, Point point, double padding)
        {
            var padded = rect;
            padded.Inflate(padding, padding);
            return padded.Contains(point);
        }

        private static double DistanceToRect(Point point, Rect rect)
        {
            double dx = 0;
            if (point.X < rect.Left)
                dx = rect.Left - point.X;
            else if (point.X > rect.Right)
                dx = point.X - rect.Right;

            double dy = 0;
            if (point.Y < rect.Top)
                dy = rect.Top - point.Y;
            else if (point.Y > rect.Bottom)
                dy = point.Y - rect.Bottom;

            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private int FindNearestTextOffset(PdfService.PdfPageTextInfo textInfo, Point point, double maxDistance)
        {
            if (textInfo?.Characters == null || textInfo.Characters.Count == 0)
                return -1;

            int bestOffset = -1;
            double bestDistance = double.MaxValue;

            foreach (var character in textInfo.Characters)
            {
                if (character.Bounds == null || character.Bounds.Count == 0 || character.UnionBounds.IsEmpty)
                    continue;

                if (RectContainsWithPadding(character.UnionBounds, point, 3.0))
                    return character.Offset;

                double distance = DistanceToRect(point, character.UnionBounds);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestOffset = character.Offset;
                }
            }

            return bestDistance <= maxDistance ? bestOffset : -1;
        }

        private static bool ShouldMergeSelectionRects(Rect current, Rect next)
        {
            double verticalOverlap = Math.Min(current.Bottom, next.Bottom) - Math.Max(current.Top, next.Top);
            bool sameLine = verticalOverlap >= Math.Min(current.Height, next.Height) * 0.35;
            bool closeEnough = next.Left <= current.Right + 8;
            return sameLine && closeEnough;
        }

        private static IReadOnlyList<Rect> BuildPdfTextSelectionRects(PdfService.PdfPageTextInfo textInfo, int startOffset, int endOffset)
        {
            var mergedRects = new List<Rect>();
            if (textInfo?.Characters == null || textInfo.Characters.Count == 0)
                return mergedRects;

            int start = Math.Max(0, Math.Min(startOffset, endOffset));
            int end = Math.Min(textInfo.Characters.Count - 1, Math.Max(startOffset, endOffset));

            for (int i = start; i <= end; i++)
            {
                var character = textInfo.Characters[i];
                if (character.Bounds == null || character.Bounds.Count == 0)
                    continue;

                foreach (var rect in character.Bounds)
                {
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                        continue;

                    if (mergedRects.Count > 0 && ShouldMergeSelectionRects(mergedRects[mergedRects.Count - 1], rect))
                    {
                        var merged = mergedRects[mergedRects.Count - 1];
                        merged.Union(rect);
                        mergedRects[mergedRects.Count - 1] = merged;
                    }
                    else
                    {
                        mergedRects.Add(rect);
                    }
                }
            }

            return mergedRects;
        }

        private void UpdatePdfTextSelectionVisuals()
        {
            if (_pdfTextSelectionPage == null || _pdfTextSelectionInfo == null || _pdfTextSelectionInfo.Text == null)
            {
                ClearPdfTextSelection();
                return;
            }

            int start = Math.Min(_pdfTextSelectionAnchorOffset, _pdfTextSelectionActiveOffset);
            int end = Math.Max(_pdfTextSelectionAnchorOffset, _pdfTextSelectionActiveOffset);
            if (start < 0 || end < start || end >= _pdfTextSelectionInfo.Text.Length)
            {
                ClearPdfTextSelection();
                return;
            }

            foreach (var pageControl in _pageControls)
            {
                if (!ReferenceEquals(pageControl, _pdfTextSelectionPage))
                    pageControl.ClearPdfTextSelection();
            }

            _pdfTextSelectionPage.SetPdfTextSelectionRects(BuildPdfTextSelectionRects(_pdfTextSelectionInfo, start, end));
            _selectedPdfText = _pdfTextSelectionInfo.Text.Substring(start, end - start + 1);
        }

        private async void PageControl_PdfTextSelectionPointerPressed(object sender, PdfTextSelectionPointerEventArgs e)
        {
            if ((_currentTool != ToolType.None && _currentTool != ToolType.TextHighlight) || sender is not PdfPageControl page)
                return;

            if (_selectedTextBox != null)
                DeselectTextBox();

            Keyboard.Focus(PdfScrollViewer);
            ClearPdfTextSelection();
            _pdfTextSelectionPressPoint = e.Position;

            int requestId = Interlocked.Increment(ref _pdfTextSelectionRequestId);

            try
            {
                var textInfo = _pdfService.TryGetCachedPageTextInfo(page.PageIndex, out var cachedTextInfo)
                    ? cachedTextInfo
                    : await _pdfService.GetPageTextInfoAsync(page.PageIndex);

                if (requestId != _pdfTextSelectionRequestId || (_currentTool != ToolType.None && _currentTool != ToolType.TextHighlight))
                    return;

                int anchorOffset = FindNearestTextOffset(textInfo, e.Position, 24.0);
                if (anchorOffset < 0)
                    return;

                _pdfTextSelectionPage = page;
                _pdfTextSelectionInfo = textInfo;
                _pdfTextSelectionAnchorOffset = anchorOffset;
                _pdfTextSelectionActiveOffset = anchorOffset;
                _isPdfTextSelectionDragging = true;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void PageControl_PdfTextSelectionPointerMoved(object sender, PdfTextSelectionPointerEventArgs e)
        {
            if (!_isPdfTextSelectionDragging || _pdfTextSelectionInfo == null || !ReferenceEquals(sender, _pdfTextSelectionPage))
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            int offset = FindNearestTextOffset(_pdfTextSelectionInfo, e.Position, double.PositiveInfinity);
            if (offset < 0)
                return;

            _pdfTextSelectionActiveOffset = offset;
            if (!_pdfTextSelectionExceededThreshold)
            {
                var delta = e.Position - _pdfTextSelectionPressPoint;
                _pdfTextSelectionExceededThreshold = Math.Abs(delta.X) >= PdfTextSelectionDragThreshold || Math.Abs(delta.Y) >= PdfTextSelectionDragThreshold;
            }

            if (_pdfTextSelectionExceededThreshold)
                UpdatePdfTextSelectionVisuals();
        }

        private void PageControl_PdfTextSelectionPointerReleased(object sender, PdfTextSelectionPointerEventArgs e)
        {
            Interlocked.Increment(ref _pdfTextSelectionRequestId);

            if (!_isPdfTextSelectionDragging || _pdfTextSelectionInfo == null || !ReferenceEquals(sender, _pdfTextSelectionPage))
            {
                ClearPdfTextSelection();
                return;
            }

            int offset = FindNearestTextOffset(_pdfTextSelectionInfo, e.Position, double.PositiveInfinity);
            if (offset >= 0)
                _pdfTextSelectionActiveOffset = offset;

            bool keepSelection = _pdfTextSelectionExceededThreshold
                && _pdfTextSelectionAnchorOffset >= 0
                && _pdfTextSelectionActiveOffset >= 0;

            _isPdfTextSelectionDragging = false;
            _pdfTextSelectionExceededThreshold = false;

            if (!keepSelection)
            {
                ClearPdfTextSelection();
                return;
            }

            if (_currentTool == ToolType.TextHighlight)
            {
                int start = Math.Min(_pdfTextSelectionAnchorOffset, _pdfTextSelectionActiveOffset);
                int end = Math.Max(_pdfTextSelectionAnchorOffset, _pdfTextSelectionActiveOffset);
                var rects = BuildPdfTextSelectionRects(_pdfTextSelectionInfo, start, end);
                if (rects.Count > 0)
                {
                    _pdfTextSelectionPage.AddHighlightAnnotation(rects, _highlighterColor);
                    MarkDirty();
                }
                ClearPdfTextSelection();
                return;
            }

            UpdatePdfTextSelectionVisuals();
        }
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
            if (msg == WM_MOUSEWHEEL)
            {
                int keys = (int)(wParam.ToInt64() & 0xFFFF);
                if ((keys & MK_CONTROL) != 0)
                {
                    // Precision touchpad pinch-to-zoom sends Ctrl+Wheel.
                    // Handle it here so we don't rely on Keyboard.Modifiers
                    // which can miss the synthetic Ctrl from touchpad drivers.
                    int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    var mousePos = Mouse.GetPosition(PdfScrollViewer);
                    if (mousePos.X >= 0 && mousePos.Y >= 0 &&
                        mousePos.X <= PdfScrollViewer.ActualWidth &&
                        mousePos.Y <= PdfScrollViewer.ActualHeight)
                    {
                        double oldZoom = _zoomLevel;
                        double step = delta > 0 ? ZoomStep : -ZoomStep;
                        double newZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, _zoomLevel + step));
                        if (Math.Abs(newZoom - oldZoom) > 0.001)
                            ZoomAroundPoint(newZoom, mousePos);
                        handled = true;
                    }
                }
            }
            else if (msg == WM_MOUSEHWHEEL)
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
                mw?.ShowToast(LocalizationService.Get("Editor.AutoSaved"), "\uE74E", 1500);
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
            Console.WriteLine($"[EditorPage] InitializePenService 闁?Window={window?.GetType().Name ?? "NULL"}");

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
            foreach (var page in _pageControls)
                page.SetPenService(_penService);
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
                Console.WriteLine($"[EditorPage] Eraser 闁?{_previousTool}");
                ActivateTool(_previousTool);
            }
            else
            {
                Console.WriteLine($"[EditorPage] {_currentTool} 闁?Eraser");
                _previousTool = _currentTool;
                ActivateTool(ToolType.Eraser);
            }
        }

        public EditorPage(string filePath) : this()
        {
            _currentPdfPath = filePath;
            Loaded += async (s, e) => await LoadPdfAsync(filePath);
        }

        private async void EditorPage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ActivateTool(ToolType.None);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                await SaveAnnotationsToPdfAsync();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                if (TryCopySelectedPdfTextToClipboard())
                {
                    var mw = Window.GetWindow(this) as MainWindow;
                    mw?.ShowToast("Text copied", "\uE8C8", 1500);
                    e.Handled = true;
                }
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

            // Shift+Wheel 闁?horizontal scroll
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                double scrollAmount = -e.Delta * 0.8;
                _targetHorizontalOffset = Math.Max(0,
                    Math.Min(PdfScrollViewer.ScrollableWidth, _targetHorizontalOffset + scrollAmount));

                AnimateHorizontalScroll(_targetHorizontalOffset);
                return;
            }

            // Normal wheel 闁?vertical scroll
            double vScrollAmount = -e.Delta * 0.8;
            _targetVerticalOffset = Math.Max(0,
                Math.Min(PdfScrollViewer.ScrollableHeight, _targetVerticalOffset + vScrollAmount));

            AnimateScroll(_targetVerticalOffset);
        }

        private void CancelSmoothScroll()
        {
            if (_isScrollAnimating)
            {
                _isScrollAnimating = false;
                System.Windows.Media.CompositionTarget.Rendering -= CompositionTarget_ScrollRendering;
            }
            if (_isHScrollAnimating)
            {
                _isHScrollAnimating = false;
                System.Windows.Media.CompositionTarget.Rendering -= CompositionTarget_HScrollRendering;
            }
        }

        // 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?Zoom around a point (keeps that point stable on screen) 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?
        private void ZoomAroundPoint(double newZoom, Point viewportPoint)
        {
            if (IsSelectablePdfSurfaceActive)
            {
                ApplySelectableViewerZoom(newZoom, viewportPoint);
                return;
            }

            CancelSmoothScroll();
            double oldZoom = _zoomLevel;

            // Convert viewport point to content coordinates.
            double contentX = (PdfScrollViewer.HorizontalOffset + viewportPoint.X) / oldZoom;
            double contentY = (PdfScrollViewer.VerticalOffset + viewportPoint.Y) / oldZoom;

            ApplyCustomZoom(newZoom);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                CancelSmoothScroll();
                double newOffsetX = Math.Max(0, contentX * _zoomLevel - viewportPoint.X);
                double newOffsetY = Math.Max(0, contentY * _zoomLevel - viewportPoint.Y);

                PdfScrollViewer.ScrollToHorizontalOffset(newOffsetX);
                PdfScrollViewer.ScrollToVerticalOffset(newOffsetY);
                _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
                _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;
                _smoothScrollInitialized = true;
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        // 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?Touch Manipulation (pinch-to-zoom + pan) 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?
        private void PdfScrollViewer_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            // Cancel manipulation if user is touching the toolbar — we don't want
            // the ScrollViewer manipulation to swallow toolbar button taps.
            if (e.OriginalSource is DependencyObject touchOrigin && IsDescendantOf(touchOrigin, ToolbarBorder))
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
            // If our raw-touch pinch is active, skip manipulation-based handling
            // to avoid conflicting pan during a 2-finger zoom gesture.
            if (_isPinchActive)
            {
                e.Handled = true;
                return;
            }

            // Pan (translate) for single-finger navigation (ToolType.None)
            double panX = e.DeltaManipulation.Translation.X;
            double panY = e.DeltaManipulation.Translation.Y;

            CancelSmoothScroll();
            PdfScrollViewer.ScrollToHorizontalOffset(PdfScrollViewer.HorizontalOffset - panX);
            PdfScrollViewer.ScrollToVerticalOffset(PdfScrollViewer.VerticalOffset - panY);

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

        private void PdfScrollViewer_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            var pos = e.GetTouchPoint(PdfScrollViewer).Position;
            _activeTouches[e.TouchDevice.Id] = pos;
            _activeTouchCount++;

            // NOTE: PreviewTouchDown fires for FINGER touches only.
            // The stylus/pen arrives via PreviewStylusDown, not TouchDown.
            // So any touch event here is a finger — we can safely treat it as scroll input.
            // When a drawing tool is active, the InkCanvas has IsHitTestVisible=true, which would
            // swallow this event. By marking it Handled in this tunneling phase, we prevent InkCanvas
            // from capturing it, allowing ManipulationDelta to handle single-finger panning.

            if (_activeTouches.Count == 2)
            {
                // Second finger: always handle for pinch-zoom tracking
                var pts = new List<Point>(_activeTouches.Values);
                double dist = PinchDistance(pts[0], pts[1]);
                if (dist > 10)
                {
                    _pinchStartDistance = dist;
                    _pinchStartZoom = _zoomLevel;
                    _isPinchActive = true;
                }
                e.Handled = true; // Consume so InkCanvas doesn't see a second-finger stroke start
            }
            else if (_currentTool == ToolType.Pen ||
                     _currentTool == ToolType.Highlighter ||
                     _currentTool == ToolType.Eraser)
            {
                // Single finger touch while a drawing tool is active.
                // Treat as scroll/pan gesture — do NOT let InkCanvas draw from finger input.
                e.Handled = true;
            }
        }



        private void PdfScrollViewer_PreviewTouchMove(object sender, TouchEventArgs e)
        {
            if (!_activeTouches.ContainsKey(e.TouchDevice.Id)) return;
            _activeTouches[e.TouchDevice.Id] = e.GetTouchPoint(PdfScrollViewer).Position;

            if (_isPinchActive && _activeTouches.Count >= 2)
            {
                var pts = new List<Point>(_activeTouches.Values);
                double newDist = PinchDistance(pts[0], pts[1]);
                if (_pinchStartDistance > 5 && newDist > 0)
                {
                    double newZoom = Math.Max(ZoomMin, Math.Min(ZoomMax,
                        _pinchStartZoom * (newDist / _pinchStartDistance)));
                    var center = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);
                    ZoomAroundPoint(newZoom, center);
                }
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            _activeTouches.Remove(e.TouchDevice.Id);
            if (_activeTouchCount > 0) _activeTouchCount--;
            if (_activeTouches.Count < 2)
                _isPinchActive = false;
        }

        private static double PinchDistance(Point a, Point b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?Smooth scroll animation (vertical) 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?
        private double _scrollAnimationTarget;
        private double _scrollAnimationStart;
        private DateTime _scrollAnimationStartTime;
        private TimeSpan _scrollAnimationDuration;
        private bool _isScrollAnimating;

        // 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?Smooth scroll animation (horizontal) 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?
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
            // Include both Stylus and Touch tablet types 闁?some Huawei MateBook
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
                CancelSmoothScroll();
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

        // 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?Middle mouse button panning 闁冲厜鍋撻柍鍏夊亾闁冲厜鍋?
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
            if (IsSelectablePdfSurfaceActive)
            {
                ApplySelectableViewerZoom(level);
                return;
            }

            ApplyCustomZoom(level);
        }

        private void ApplyCustomZoom(double level)
        {
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            PagesZoomTransform.ScaleX = _zoomLevel;
            PagesZoomTransform.ScaleY = _zoomLevel;

            UpdateZoomLabel();

            // Sync smooth scroll state
            _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
            _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;
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
        /// Called on scroll to lazily render pages that are entering the viewport and
        /// re-render visible pages at higher DPI after zooming.
        /// </summary>
        private async void PdfScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdatePageNumberIndicator();

            _scrollReRenderCts?.Cancel();
            _scrollReRenderCts = new CancellationTokenSource();
            var token = _scrollReRenderCts.Token;

            try
            {
                await Task.Delay(100, token);
                token.ThrowIfCancellationRequested();

                var visiblePages = GetVisiblePageControls();
                var needsInitialRender = visiblePages
                    .Where(p => !_pagesInitiallyRendered.Contains(p.PageIndex))
                    .ToList();

                foreach (var page in needsInitialRender)
                {
                    token.ThrowIfCancellationRequested();
                    await RenderPageInitialAsync(page, token);
                }

                if (_lastRenderedDpiScale > 1.0 && _pagesRenderedAtScale.Count < _pageControls.Count)
                {
                    var needsZoomRender = visiblePages
                        .Where(p => !_pagesRenderedAtScale.Contains(p.PageIndex))
                        .ToList();

                    if (needsZoomRender.Count > 0)
                        await ReRenderPagesAsync(needsZoomRender, _lastRenderedDpiScale, token);
                }

                if (_textBoxPopup != null && _textBoxPopup.IsOpen)
                {
                    var offset = _textBoxPopup.HorizontalOffset;
                    _textBoxPopup.HorizontalOffset = offset + 0.001;
                    _textBoxPopup.HorizontalOffset = offset;
                }
            }
            catch (OperationCanceledException) { }
        }

        private struct ScrollAnchor
        {
            public PdfPageControl AnchorPage;
            public double OffsetFromViewportTop;
        }

        private ScrollAnchor CaptureScrollAnchor()
        {
            var visiblePages = GetVisiblePageControls();
            if (visiblePages.Count == 0)
                return default;

            var anchorPage = visiblePages[0];
            return new ScrollAnchor
            {
                AnchorPage = anchorPage,
                OffsetFromViewportTop = GetScaledPageTop(anchorPage.PageIndex) - PdfScrollViewer.VerticalOffset
            };
        }

        private void RestoreScrollAnchor(ScrollAnchor anchor)
        {
            if (anchor.AnchorPage == null)
                return;

            double newOffset = GetScaledPageTop(anchor.AnchorPage.PageIndex) - anchor.OffsetFromViewportTop;
            PdfScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffset));
            _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
        }

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
                catch { }
            }
        }

        private double GetScaledPageTop(int pageIndex)
            => pageIndex >= 0 && pageIndex < _pageTopOffsets.Count ? _pageTopOffsets[pageIndex] * _zoomLevel : 0;

        private double GetScaledPageHeight(int pageIndex)
            => pageIndex >= 0 && pageIndex < _pageHeights.Count ? _pageHeights[pageIndex] * _zoomLevel : 0;

        private int FindFirstVisiblePageIndex(double viewTop)
        {
            int lo = 0;
            int hi = _pageControls.Count - 1;
            int result = _pageControls.Count - 1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                double pageBottom = GetScaledPageTop(mid) + GetScaledPageHeight(mid);
                if (pageBottom >= viewTop)
                {
                    result = mid;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return Math.Max(0, result);
        }

        private List<PdfPageControl> GetVisiblePageControls()
        {
            var result = new List<PdfPageControl>();
            if (_pageControls.Count == 0)
                return result;

            double viewportHeight = PdfScrollViewer.ViewportHeight;
            if (viewportHeight <= 0)
            {
                int initialCount = Math.Min(2, _pageControls.Count);
                for (int i = 0; i < initialCount; i++)
                    result.Add(_pageControls[i]);
                return result;
            }

            double viewTop = Math.Max(0, PdfScrollViewer.VerticalOffset - (viewportHeight * 0.5));
            double viewBottom = PdfScrollViewer.VerticalOffset + viewportHeight + (viewportHeight * 0.5);
            int startIndex = FindFirstVisiblePageIndex(viewTop);

            for (int i = startIndex; i < _pageControls.Count; i++)
            {
                double pageTop = GetScaledPageTop(i);
                if (pageTop > viewBottom)
                    break;

                double pageBottom = pageTop + GetScaledPageHeight(i);
                if (pageBottom >= viewTop)
                    result.Add(_pageControls[i]);
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
            // Pen popup — with size preview section
            Line penSizePreview = null;
            _penPopup = BuildToolPopup(
                LocalizationService.Get("Editor.PopupSize"), 0.5, 8, _penSize, 0.25,
                v => { _penSize = v; if (penSizePreview != null) penSizePreview.StrokeThickness = v; if (_currentTool == ToolType.Pen) ApplyToolToAllPages(); },
                LocalizationService.Get("Editor.PopupColor"), _penColor,
                c => { _penColor = c; if (penSizePreview != null) penSizePreview.Stroke = new SolidColorBrush(c); UpdateToolIconColors(); if (_currentTool == ToolType.Pen) ApplyToolToAllPages(); });
            penSizePreview = AddSizePreviewSection(_penPopup, _penSize, _penColor, isHighlighter: false);

            // Highlighter popup — with size preview section
            Line hlSizePreview = null;
            _highlighterPopup = BuildToolPopup(
                LocalizationService.Get("Editor.PopupSize"), 2, 48, _highlighterSize, 0.5,
                v => { _highlighterSize = v; if (hlSizePreview != null) hlSizePreview.StrokeThickness = v; if (_currentTool == ToolType.Highlighter) ApplyToolToAllPages(); },
                LocalizationService.Get("Editor.PopupColor"), _highlighterColor,
                c => { _highlighterColor = c; if (hlSizePreview != null) hlSizePreview.Stroke = new SolidColorBrush(Color.FromArgb(140, c.R, c.G, c.B)); UpdateToolIconColors(); if (_currentTool == ToolType.Highlighter) ApplyToolToAllPages(); });
            hlSizePreview = AddSizePreviewSection(_highlighterPopup, _highlighterSize, _highlighterColor, isHighlighter: true);

            _eraserPopup = BuildToolPopup(
                LocalizationService.Get("Editor.PopupEraserSize"), 4, 80, _eraserSize, 1,
                v => { _eraserSize = v; ShowEraserSizePreview(v); ApplyToolToAllPages(); },
                null, default, null);

            CreateSelectionPopup();
        }

        private Line AddSizePreviewSection(Popup popup, double initialSize, Color initialColor, bool isHighlighter)
        {
            if (popup?.Child is not Border border || border.Child is not StackPanel panel)
                return null;

            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(25, 0, 0, 0)),
                Margin = new Thickness(-16, 14, -16, 10)
            });

            panel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("Editor.PopupPreview"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var previewBorder = new Border
            {
                Height = 60,
                Background = new SolidColorBrush(Color.FromArgb(18, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true
            };

            Line line;
            if (isHighlighter)
            {
                // Highlighter: horizontal stroke band showing actual stroke height
                line = new Line
                {
                    X1 = 8, Y1 = 30, X2 = 212, Y2 = 30,
                    Stroke = new SolidColorBrush(Color.FromArgb(140, initialColor.R, initialColor.G, initialColor.B)),
                    StrokeThickness = initialSize,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
            }
            else
            {
                // Pen: diagonal stroke line showing actual stroke width
                line = new Line
                {
                    X1 = 8, Y1 = 48, X2 = 212, Y2 = 12,
                    Stroke = new SolidColorBrush(initialColor),
                    StrokeThickness = initialSize,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
            }

            previewBorder.Child = line;
            panel.Children.Add(previewBorder);
            return line;
        }

        private CancellationTokenSource _eraserPreviewCts;

        private void ShowEraserSizePreview(double size)
        {
            EraserSizePreviewEllipse.Width = size;
            EraserSizePreviewEllipse.Height = size;
            EraserSizePreviewEllipse.Visibility = Visibility.Visible;

            _eraserPreviewCts?.Cancel();
            _eraserPreviewCts = new CancellationTokenSource();
            var token = _eraserPreviewCts.Token;

            Task.Delay(1200).ContinueWith(_ =>
            {
                if (!token.IsCancellationRequested)
                    Dispatcher.Invoke(() => EraserSizePreviewEllipse.Visibility = Visibility.Collapsed);
            }, TaskScheduler.Default);
        }

        private void CreateSelectionPopup()
        {
            // ── Settings popup (opens when Select button is clicked) ────────────────
            _selectionPopup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, VerticalOffset = 6 };

            var settingsPanel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            var settingsBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = settingsPanel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20, ShadowDepth = 4, Opacity = 0.12, Color = Colors.Black
                }
            };

            // ── Shape section ──────────────────────────────────────────────────────
            settingsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("Editor.SelectShape"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var shapePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

            Button MakeShapeButton(string icon, string tooltip, SelectionShape shape)
            {
                var isActive = _selectionShape == shape;
                var btn = new Button
                {
                    Width = 36, Height = 32,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(0),
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(1),
                    ToolTip = tooltip,
                    Tag = shape
                };
                btn.Template = CreateIconButtonTemplate("#E8E8E8", "#DCDCDC");
                btn.Content = new TextBlock
                {
                    Text = icon,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                UpdateFilterButtonStyle(btn, isActive);
                btn.Click += (s, ev) =>
                {
                    _selectionShape = (SelectionShape)((Button)s).Tag;
                    ApplyToolToAllPages();
                    foreach (Button b in shapePanel.Children)
                        UpdateFilterButtonStyle(b, (SelectionShape)b.Tag == _selectionShape);
                };
                return btn;
            }

            shapePanel.Children.Add(MakeShapeButton("\uE73F", LocalizationService.Get("Editor.SelectShapeRect"), SelectionShape.Rectangle));
            shapePanel.Children.Add(MakeShapeButton("\uED63", LocalizationService.Get("Editor.SelectShapeFree"), SelectionShape.FreeForm));
            settingsPanel.Children.Add(shapePanel);

            // ── Filter section header
            settingsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("Editor.SelectFilter"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Filter radio buttons
            var filterPanel = new StackPanel { Orientation = Orientation.Horizontal };

            Button MakeFilterButton(string label, SelectionFilter filter)
            {
                var isActive = _selectionFilter == filter;
                var btn = new Button
                {
                    Content = label,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(10, 5, 10, 5),
                    FontSize = 12,
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(1),
                    Tag = filter
                };
                btn.Template = CreateIconButtonTemplate("#E8E8E8", "#DCDCDC");
                UpdateFilterButtonStyle(btn, isActive);
                btn.Click += (s, ev) =>
                {
                    _selectionFilter = (SelectionFilter)((Button)s).Tag;
                    ApplyToolToAllPages();
                    // Refresh all filter button styles
                    foreach (Button b in filterPanel.Children)
                        UpdateFilterButtonStyle(b, (SelectionFilter)b.Tag == _selectionFilter);
                };
                return btn;
            }

            filterPanel.Children.Add(MakeFilterButton(LocalizationService.Get("Editor.SelectFilterBoth"), SelectionFilter.Both));
            filterPanel.Children.Add(MakeFilterButton(LocalizationService.Get("Editor.SelectFilterDrawings"), SelectionFilter.DrawingsOnly));
            filterPanel.Children.Add(MakeFilterButton(LocalizationService.Get("Editor.SelectFilterText"), SelectionFilter.TextOnly));

            settingsPanel.Children.Add(filterPanel);
            _selectionPopup.Child = settingsBorder;

            // ── Action popup (opens when annotations are selected) ──────────────────
            _selectionActionPopup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, VerticalOffset = 6 };

            var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 6) };
            var actionBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = actionPanel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20, ShadowDepth = 4, Opacity = 0.14, Color = Colors.Black
                }
            };

            var scaleDownButton = new Button
            {
                Width = 36, Height = 32,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("Editor.ZoomOutTooltip"),
                Margin = new Thickness(2)
            };
            scaleDownButton.Template = CreateIconButtonTemplate("#E8E8E8", "#DCDCDC");
            scaleDownButton.Content = new TextBlock
            {
                Text = "\uE738",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            scaleDownButton.Click += (s, e) => ScaleSelection(0.9);

            var scaleUpButton = new Button
            {
                Width = 36, Height = 32,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("Editor.ZoomInTooltip"),
                Margin = new Thickness(2)
            };
            scaleUpButton.Template = CreateIconButtonTemplate("#E8E8E8", "#DCDCDC");
            scaleUpButton.Content = new TextBlock
            {
                Text = "\uE710",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            scaleUpButton.Click += (s, e) => ScaleSelection(1.1);

            var deleteButton = new Button
            {
                Width = 36, Height = 32,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("Editor.DeleteTooltip"),
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
            deleteButton.Click += (s, e) => DeleteSelection();

            actionPanel.Children.Add(scaleDownButton);
            actionPanel.Children.Add(scaleUpButton);

            var sep = new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)), Margin = new Thickness(4, 6, 4, 6) };
            actionPanel.Children.Add(sep);
            actionPanel.Children.Add(deleteButton);

            _selectionActionPopup.Child = actionBorder;
        }

        private void UpdateFilterButtonStyle(Button btn, bool isActive)
        {
            btn.Background = isActive
                ? new SolidColorBrush(Color.FromArgb(34, 0, 120, 212))
                : Brushes.Transparent;
            btn.BorderBrush = isActive
                ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
                : new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            btn.Foreground = isActive
                ? new SolidColorBrush(Color.FromRgb(0, 90, 160))
                : new SolidColorBrush(Color.FromRgb(60, 60, 60));
        }

        private void ScaleSelection(double factor)
        {
            if (_activeSelectionPage == null || !_activeSelectionPage.HasSelection)
                return;

            var bounds = _activeSelectionPage.GetSelectionBounds();
            if (bounds.IsEmpty)
                return;

            var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            _activeSelectionPage.ScaleSelection(factor, center);
            MarkDirty();
        }

        private void DeleteSelection()
        {
            if (_activeSelectionPage == null)
                return;

            _activeSelectionPage.ClearSelection();
            _selectionActionPopup.IsOpen = false;
            MarkDirty();
        }

        private Popup BuildToolPopup(
            string sizeLabel, double min, double max, double value, double step, Action<double> sizeChanged,
            string colorLabel, Color initialColor, Action<Color> colorChanged)
        {
            var popup = new Popup { Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true };
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
                                // Top half: full value, varying saturation (light 闁?saturated)
                                saturation = (double)row / (rows / 2);
                            }
                            else
                            {
                                // Bottom half: full saturation, decreasing value (saturated 闁?dark)
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

        private void EditorPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                if (_currentTool == ToolType.Select && _activeSelectionPage != null && _activeSelectionPage.HasSelection)
                {
                    DeleteSelection();
                    e.Handled = true;
                }
                else if (_currentTool == ToolType.Text && _selectedTextBox != null)
                {
                    if (string.IsNullOrEmpty(_selectedTextBox.Text) && e.Key == Key.Back)
                    {
                        DeleteSelectedTextBox();
                        e.Handled = true;
                    }
                    else if (!_selectedTextBox.IsFocused)
                    {
                        DeleteSelectedTextBox();
                        e.Handled = true;
                    }
                }
            }
        }

        private void EditorPage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ShouldClosePopupAndConsume(e.OriginalSource as DependencyObject))
            {
                CloseToolPopups();
                e.Handled = true;
            }
        }

        private void EditorPage_PreviewStylusDown(object sender, StylusDownEventArgs e)
        {
            if (ShouldClosePopupAndConsume(e.OriginalSource as DependencyObject))
            {
                CloseToolPopups();
                e.Handled = true;
            }
        }

        private bool ShouldClosePopupAndConsume(DependencyObject originalSource)
        {
            if (originalSource == null) return false;

            var popups = new[] { _penPopup, _highlighterPopup, _eraserPopup, _selectionPopup };
            bool anyPopupOpen = false;
            foreach (var popup in popups)
            {
                if (popup != null && popup.IsOpen)
                {
                    anyPopupOpen = true;
                    if (IsSourceInPopup(originalSource, popup))
                    {
                        return false;
                    }
                }
            }

            if (!anyPopupOpen) return false;

            if (IsSourceInToolbar(originalSource))
            {
                return false;
            }

            return true;
        }

        private bool IsSourceInPopup(DependencyObject source, Popup popup)
        {
            if (popup?.Child == null) return false;
            return IsDescendantOf(source, popup.Child);
        }

        private bool IsSourceInToolbar(DependencyObject source)
        {
            return IsDescendantOf(source, ToolbarBorder);
        }

        private bool IsDescendantOf(DependencyObject descendant, DependencyObject ancestor)
        {
            if (descendant == null || ancestor == null) return false;
            var current = descendant;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        private void CloseToolPopups(ToolType toolToKeepOpen = ToolType.None)
        {
            if (toolToKeepOpen != ToolType.Pen && _penPopup != null)
                _penPopup.IsOpen = false;

            if (toolToKeepOpen != ToolType.Highlighter && _highlighterPopup != null)
                _highlighterPopup.IsOpen = false;

            if (toolToKeepOpen != ToolType.Eraser && _eraserPopup != null)
                _eraserPopup.IsOpen = false;

            if (toolToKeepOpen != ToolType.Select && _selectionPopup != null)
                _selectionPopup.IsOpen = false;
            if (toolToKeepOpen != ToolType.Select && _selectionActionPopup != null)
                _selectionActionPopup.IsOpen = false;
        }

        private void ToggleToolButton(ToolType tool, ToggleButton button, Popup popup = null)
        {
            if (_isUpdatingToolState) return;

            var isActiveTool = _currentTool == tool;
            CloseToolPopups();

            if (isActiveTool)
            {
                ActivateTool(ToolType.None);
                return;
            }

            ActivateTool(tool);

            if (tool == ToolType.Select && _selectionPopup != null)
            {
                _selectionPopup.PlacementTarget = button;
                _selectionPopup.IsOpen = true;
            }
            else if (popup != null)
            {
                popup.PlacementTarget = button;
                popup.IsOpen = true;
            }
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
            _pageControls.Clear();
            _pageTopOffsets.Clear();
            _pageHeights.Clear();
            DeselectTextBox();
            _isDirty = false;
            _lastRenderedDpiScale = 1.0;
            _pagesRenderedAtScale.Clear();
            _pagesInitiallyRendered.Clear();
            DisposeSelectablePdfDocument();
            UpdatePdfSurfaceVisibility();

            try
            {
                await _pdfService.LoadPdfAsync(filePath, token);
                await LoadSelectablePdfDocumentAsync(filePath, token);
                RecentFilesService.UpdateMetadata(filePath, _pdfService.PageCount, File.GetLastWriteTimeUtc(filePath));

                int pageCount = _pdfService.PageCount;
                double currentTop = 0;

                for (int i = 0; i < pageCount; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var (w, h) = _pdfService.GetPageSizeInDips(i);
                    if (w <= 0 || h <= 0)
                    {
                        w = 1584;
                        h = 2245;
                    }

                    var pageControl = new PdfPageControl
                    {
                        PageIndex = i,
                        Width = w,
                        Height = h,
                        Margin = new Thickness(0, 0, 0, PageSpacing)
                    };

                    pageControl.TextOverlayPointerPressed += PageControl_TextOverlayPointerPressed;
                    pageControl.BackgroundPointerPressed += PageControl_BackgroundPointerPressed;
                    pageControl.PdfTextSelectionPointerPressed += PageControl_PdfTextSelectionPointerPressed;
                    pageControl.PdfTextSelectionPointerMoved += PageControl_PdfTextSelectionPointerMoved;
                    pageControl.PdfTextSelectionPointerReleased += PageControl_PdfTextSelectionPointerReleased;
                    pageControl.InkMutated += PageControl_InkMutated;
                    pageControl.StrokeCollectedUndoable += PageControl_StrokeCollectedUndoable;
                    pageControl.ModeChanged += PageControl_ModeChanged;
                    pageControl.SelectionChanged += PageControl_SelectionChanged;
                    pageControl.SelectionMoveCompleted += PageControl_SelectionMoveCompleted;
                    pageControl.SelectionResizeCompleted += PageControl_SelectionResizeCompleted;

                    if (_penService != null)
                        pageControl.SetPenService(_penService);

                    _pageControls.Add(pageControl);
                    _pageTopOffsets.Add(currentTop);
                    _pageHeights.Add(h);
                    currentTop += h + PageSpacing;
                    PagesContainer.Children.Add(pageControl);
                }

                ApplyToolToAllPages();

                PdfScrollViewer.ScrollToVerticalOffset(0);
                PdfScrollViewer.ScrollToHorizontalOffset(0);
                _targetVerticalOffset = 0;
                _targetHorizontalOffset = 0;
                _smoothScrollInitialized = true;

                var visiblePages = GetVisiblePageControls();
                foreach (var page in visiblePages)
                {
                    token.ThrowIfCancellationRequested();
                    await RenderPageInitialAsync(page, token);
                }

                if (!string.IsNullOrEmpty(_currentPdfPath))
                    await LoadAnnotationsFromPdfServiceAsync();

                UpdatePageNumberIndicator();
                SyncSelectableViewerFromCustomView();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to load PDF: {ex.Message}";
                if (ex.InnerException != null)
                    errorMsg += $"\n\nDetails: {ex.InnerException.Message}";

                if (sessionId == _loadSessionId)
                {
                    var mw = GetMainWindow();
                    if (mw != null)
                        await DialogService.ShowErrorAsync(mw, "Error", errorMsg);
                    else
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
        /// Does NOT adjust scroll 闁?the caller is responsible for anchor save/restore.
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

        private void PageControl_SelectionChanged(object sender, AnnotationSelectionChangedEventArgs e)
        {
            if (_currentTool != ToolType.Select) return;

            if (sender is PdfPageControl page)
            {
                _activeSelectionPage = page;
            }

            if (e.HasSelection)
            {
                _selectionActionPopup.PlacementTarget = SelectToolButton;
                _selectionActionPopup.IsOpen = true;
            }
            else
            {
                _selectionActionPopup.IsOpen = false;
            }
        }

        private void PageControl_SelectionMoveCompleted(object sender, SelectionMoveCompletedEventArgs e)
        {
            if (sender is not PdfPageControl page) return;
            var action = new SelectionMoveAction(page, e.DeltaX, e.DeltaY, e.SelectedStrokes, e.SelectedTextContainers);
            _undoStack.Add(action);
            _redoStack.Clear();
            UpdateUndoRedoButtons();
            MarkDirty();
        }

        private void PageControl_SelectionResizeCompleted(object sender, SelectionResizeCompletedEventArgs e)
        {
            if (sender is not PdfPageControl page) return;
            var action = new SelectionResizeAction(page, e.TotalScale, e.Anchor, e.SelectedStrokes, e.SelectedTextContainers);
            _undoStack.Add(action);
            _redoStack.Clear();
            UpdateUndoRedoButtons();
            MarkDirty();
        }

        private void PenToolButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleToolButton(ToolType.Pen, PenToolButton, _penPopup);
        }

        private void HighlighterToolButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleToolButton(ToolType.Highlighter, HighlighterToolButton, _highlighterPopup);
        }



        private void EraserToolButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleToolButton(ToolType.Eraser, EraserToolButton, _eraserPopup);
        }

        private void TextToolButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleToolButton(ToolType.Text, TextToolButton);
        }

        private void SelectToolButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleToolButton(ToolType.Select, SelectToolButton);
        }



        private void ActivateTool(ToolType tool)
        {
            if (_currentTool == tool) return;

            bool wasSelectableSurfaceActive = IsSelectablePdfSurfaceActive;

            if (tool != ToolType.Text)
                DeselectTextBox();

            _isUpdatingToolState = true;
            _currentTool = tool;
            CloseToolPopups(tool);

            PenToolButton.IsChecked = tool == ToolType.Pen;
            HighlighterToolButton.IsChecked = tool == ToolType.Highlighter;
            EraserToolButton.IsChecked = tool == ToolType.Eraser;
            TextToolButton.IsChecked = tool == ToolType.Text;
            SelectToolButton.IsChecked = tool == ToolType.Select;
            _isUpdatingToolState = false;

            UpdateToolIconColors();
            ApplyToolToAllPages();
            UpdatePdfSurfaceVisibility();

            if (wasSelectableSurfaceActive && !IsSelectablePdfSurfaceActive)
            {
                SyncCustomSurfaceFromSelectableViewer();
            }
            else if (!wasSelectableSurfaceActive && IsSelectablePdfSurfaceActive)
            {
                SyncSelectableViewerFromCustomView();
            }
        }

        private void UpdateToolIconColors()
        {
            PenIcon.Foreground = new SolidColorBrush(_penColor);
        }

        private void ApplyToolToAllPages()
        {
            bool enablePressure = AppSettingsService.Load().EnablePressure;

            foreach (var page in _pageControls)
            {
                page.PressureEnabled = enablePressure;
                page.SetMode(_currentTool == ToolType.Text);
                page.SetPdfTextSelectionEnabled(_currentTool == ToolType.None || _currentTool == ToolType.TextHighlight);
                page.SetSelectionMode(_currentTool == ToolType.Select);
                page.SetSelectionFilter(_selectionFilter);
                page.SetSelectionShape(_selectionShape);
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
                    case ToolType.Select:
                        page.SetInputMode(CustomInkInputProcessingMode.None);
                        break;
                }

                page.SetEraserSize(_eraserSize);
            }
        }

        private void UpdatePageNumberIndicator()
        {
            if (PageNumberText == null) return;

            if (_pageControls.Count == 0)
            {
                PageNumberText.Text = "0 / 0";
                return;
            }

            double viewportHeight = PdfScrollViewer.ViewportHeight;
            if (viewportHeight <= 0)
            {
                PageNumberText.Text = $"1 / {_pageControls.Count}";
                return;
            }

            double centerOffset = PdfScrollViewer.VerticalOffset + (viewportHeight / 2);
            int currentPageIndex = 0;

            for (int i = 0; i < _pageControls.Count; i++)
            {
                double pageTop = GetScaledPageTop(i);
                if (pageTop > centerOffset)
                {
                    break;
                }
                currentPageIndex = i;
            }

            PageNumberText.Text = $"{currentPageIndex + 1} / {_pageControls.Count}";
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
                ToolTip = LocalizationService.Get("Editor.DeleteTooltip"),
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
            
            int cols = 12;
            int rows = 8;
            double cellSize = 20;
            var paletteGrid = new Grid { Width = cols * cellSize, Height = rows * cellSize, ClipToBounds = true };

            var selectionIndicator = new Border
            {
                Width = cellSize, Height = cellSize,
                BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
                Background = Brushes.Transparent, IsHitTestVisible = false,
                Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    Color cellColor;
                    if (row == 0)
                    {
                        byte gray = (byte)(col * 255 / (cols - 1));
                        cellColor = Color.FromRgb(gray, gray, gray);
                    }
                    else
                    {
                        double hue = col * 360.0 / cols;
                        double saturation = row <= rows / 2 ? (double)row / (rows / 2) : 1.0;
                        double val = row <= rows / 2 ? 1.0 : 1.0 - (double)(row - rows / 2) / (rows / 2);
                        cellColor = HsvToColor(hue, saturation, val);
                    }

                    var cell = new Border
                    {
                        Width = cellSize, Height = cellSize,
                        Background = new SolidColorBrush(cellColor),
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(col * cellSize, row * cellSize, 0, 0),
                        Cursor = Cursors.Hand, Tag = cellColor
                    };

                    cell.MouseLeftButtonDown += (s, ev) =>
                    {
                        var b = s as Border;
                        var picked = (Color)b.Tag;
                        selectionIndicator.Margin = b.Margin;
                        selectionIndicator.Visibility = Visibility.Visible;
                        if (_selectedTextBox != null)
                        {
                            _selectedTextBox.Foreground = new SolidColorBrush(picked);
                            _textColor = picked;
                            _colorIndicator.Background = new SolidColorBrush(picked);
                            MarkDirty();
                        }
                        colorPopup.IsOpen = false;
                        ev.Handled = true;
                    };

                    paletteGrid.Children.Add(cell);
                }
            }

            foreach (Border cell in paletteGrid.Children)
            {
                if (cell.Tag is Color c && c == _textColor)
                {
                    selectionIndicator.Margin = cell.Margin;
                    selectionIndicator.Visibility = Visibility.Visible;
                    break;
                }
            }

            paletteGrid.Children.Add(selectionIndicator);

            colorPopup.Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = new StackPanel { Margin = new Thickness(16), Children = { paletteGrid } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.14, Color = Colors.Black }
            };
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

        private void FontSizeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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
            // Auto-switch to Text tool when clicking on an existing textbox,
            // regardless of the currently active tool.
            if (_currentTool != ToolType.Text)
                ActivateTool(ToolType.Text);

            if (_selectedTextBox != null && _selectedTextBox != textBox)
            {
                ApplyTextBoxChrome(_selectedTextBox, isSelected: false);
                _selectedTextBox.IsReadOnly = true;
            }

            _selectedTextBox = textBox;
            textBox.IsReadOnly = false;
            ApplyTextBoxChrome(textBox, isSelected: true);
            SyncPopupToSelectedTextBox();

            // Close then reopen so WPF repositions the popup to the new target.
            // Simply changing PlacementTarget while the popup is open doesn't move it.
            _textBoxPopup.IsOpen = false;
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
                return; // Clicking outside simply deselects and stops
            }

            CreateTextBox(page, point, alignToPointer: true);
            e.Handled = true;
        }

        private void CreateTextBox(PdfPageControl page, Point position, Color? color = null, double? fontSize = null, string text = null, bool select = true, bool alignToPointer = false)
        {
            var textPadding = new Thickness(10, 8, 10, 8);
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
                Padding = textPadding,
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

            var initialLeft = position.X;
            var initialTop = position.Y;
            if (alignToPointer)
            {
                initialLeft -= textPadding.Left;
                initialTop -= textPadding.Top;
            }

            Canvas.SetLeft(container, Math.Max(0, initialLeft));
            Canvas.SetTop(container, Math.Max(0, initialTop));
            Panel.SetZIndex(container, 1000);

            dragHandle.MouseLeftButtonDown += DragHandle_MouseLeftButtonDown;
            dragHandle.MouseMove += DragHandle_MouseMove;
            dragHandle.MouseLeftButtonUp += DragHandle_MouseLeftButtonUp;

            textBox.TextChanged += (s, e) => MarkDirty();
            textBox.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // Only intercept clicks that land directly on the textbox itself.
                // If the original source is not inside the textbox's own visual tree exclude
                // toolbar and popup hits so Undo/Redo buttons remain clickable.
                if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, ToolbarBorder))
                    return; // Let toolbar buttons handle their own clicks

                // Allow clicking the textbox to select/edit it, regardless of active tool
                SelectTextBox((TextBox)s);
                e.Handled = true; // Prevent click from bubbling to Canvas
            };
            textBox.GotFocus += (s, e) =>
            {
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

            _dragArmed = true;
            _draggedContainer = handle?.Parent as Grid;
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
                var dx = currentPoint.X - _dragPressPointOnCanvas.X;
                var dy = currentPoint.Y - _dragPressPointOnCanvas.Y;
                var maxX = Math.Max(0, canvas.ActualWidth - _draggedContainer.ActualWidth);
                var maxY = Math.Max(0, canvas.ActualHeight - _draggedContainer.ActualHeight);
                var newX = Math.Max(0, Math.Min(_dragStartX + dx, maxX));
                var newY = Math.Max(0, Math.Min(_dragStartY + dy, maxY));
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
                var annotations = CollectAnnotations();

                await _pdfService.SaveAnnotationsToPdfAsync(_currentPdfPath, annotations);
                _isDirty = false;

                GetMainWindow()?.ShowToast("Saved successfully");
            }
            catch (Exception ex)
            {
                var mw = GetMainWindow();
                if (mw != null)
                    await DialogService.ShowErrorAsync(mw, "Error", $"Failed to save annotations: {ex.Message}");
                else
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
            foreach (var page in _pageControls)
            {
                var pa = new PageAnnotation();
                pa.Strokes = page.GetStrokeData();
                pa.Highlights = page.GetHighlights().ToList();

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

                if (pa.Strokes.Count > 0 || pa.Texts.Count > 0 || pa.Highlights.Count > 0)
                    annotations[page.PageIndex] = pa;
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
                foreach (var page in _pageControls)
                {
                    if (_pdfService.ExtractedAnnotations.TryGetValue(page.PageIndex, out var pa))
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

                        foreach (var hl in pa.Highlights)
                        {
                            page.AddHighlight(hl);
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
            UpdatePdfSurfaceVisibility();
        }

        private void HideLoadingOverlay()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            UpdatePdfSurfaceVisibility();
        }

        private void CancelActiveLoad()
        {
            try { Interlocked.Increment(ref _loadSessionId); _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
            _loadCts = null;
        }

        private void DetachAllPageControlEvents()
        {
            foreach (var pageControl in _pageControls)
            {
                pageControl.TextOverlayPointerPressed -= PageControl_TextOverlayPointerPressed;
                pageControl.BackgroundPointerPressed -= PageControl_BackgroundPointerPressed;
                pageControl.InkMutated -= PageControl_InkMutated;
                pageControl.StrokeCollectedUndoable -= PageControl_StrokeCollectedUndoable;
                pageControl.SelectionChanged -= PageControl_SelectionChanged;
                pageControl.SelectionMoveCompleted -= PageControl_SelectionMoveCompleted;
                pageControl.SelectionResizeCompleted -= PageControl_SelectionResizeCompleted;
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

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private void FixPopupTopmost(Popup popup)
        {
            popup.Opened += (s, e) =>
            {
                var source = PresentationSource.FromVisual(popup.Child) as System.Windows.Interop.HwndSource;
                if (source != null)
                {
                    // Remove topmost z-order imposed by WPF's transparent popup
                    SetWindowPos(source.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0010 | 0x0002 | 0x0001);
                    // Add WS_EX_NOACTIVATE so the popup never steals Windows-level focus
                    // from the main window. Without this, interacting with a Slider or
                    // other focusable control inside the popup causes the popup HWND to
                    // become the active window; the first subsequent click on the main
                    // window is then swallowed by Windows to re-activate it, making
                    // toolbar buttons appear to require two clicks.
                    const int GWL_EXSTYLE = -20;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    int exStyle = GetWindowLong(source.Handle, GWL_EXSTYLE);
                    SetWindowLong(source.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
                }
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








