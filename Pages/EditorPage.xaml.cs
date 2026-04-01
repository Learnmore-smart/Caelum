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
using Microsoft.Win32;
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
        private bool _promptSaveAsAfterLoad;
        private bool _hasPromptedForSaveAs;
        private string _pendingLibraryFolderId;
        private bool _isNotebookDraft;

        private TextBox _selectedTextBox;
        private Popup _textBoxPopup;
        private Border _colorIndicator;
        private static readonly double[] TextFontSizeSteps = { 12d, 14d, 16d, 18d, 20d, 24d, 28d, 32d, 40d, 48d, 60d, 72d };

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
        private interface IUndoAction
        {
            bool LeavesDocumentDirty { get; }
            Task UndoAsync();
            Task RedoAsync();
        }

        private class StrokeAddedAction : IUndoAction
        {
            private readonly PdfPageControl _page;
            private readonly System.Windows.Ink.Stroke _stroke;
            public StrokeAddedAction(PdfPageControl page, System.Windows.Ink.Stroke stroke) { _page = page; _stroke = stroke; }
            public bool LeavesDocumentDirty => true;
            public Task UndoAsync()
            {
                _page.RemoveStrokeQuiet(_stroke);
                return Task.CompletedTask;
            }

            public Task RedoAsync()
            {
                _page.AddStrokeQuiet(_stroke);
                return Task.CompletedTask;
            }
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
            public bool LeavesDocumentDirty => true;
            public Task UndoAsync()
            {
                _page.MoveItemsDirectly(_strokes, _containers, -_deltaX, -_deltaY);
                return Task.CompletedTask;
            }

            public Task RedoAsync()
            {
                _page.MoveItemsDirectly(_strokes, _containers, _deltaX, _deltaY);
                return Task.CompletedTask;
            }
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
            public bool LeavesDocumentDirty => true;
            public Task UndoAsync()
            {
                _page.ScaleItemsDirectly(_strokes, _containers, 1.0 / _totalScale, _anchor);
                return Task.CompletedTask;
            }

            public Task RedoAsync()
            {
                _page.ScaleItemsDirectly(_strokes, _containers, _totalScale, _anchor);
                return Task.CompletedTask;
            }
        }

        private sealed class DocumentSnapshotAction : IUndoAction
        {
            private readonly EditorPage _owner;
            private readonly byte[] _beforeBytes;
            private readonly byte[] _afterBytes;
            private readonly int _undoFocusPageIndex;
            private readonly int _redoFocusPageIndex;

            public DocumentSnapshotAction(EditorPage owner, byte[] beforeBytes, byte[] afterBytes, int undoFocusPageIndex, int redoFocusPageIndex)
            {
                _owner = owner;
                _beforeBytes = beforeBytes;
                _afterBytes = afterBytes;
                _undoFocusPageIndex = undoFocusPageIndex;
                _redoFocusPageIndex = redoFocusPageIndex;
            }

            public bool LeavesDocumentDirty => false;
            public Task UndoAsync() => _owner.ApplyDocumentSnapshotAsync(_beforeBytes, _undoFocusPageIndex);
            public Task RedoAsync() => _owner.ApplyDocumentSnapshotAsync(_afterBytes, _redoFocusPageIndex);
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
        private readonly List<Button> _pageDeleteButtons = new List<Button>();
        private readonly List<Button> _pageInsertButtons = new List<Button>();
        private CancellationTokenSource _scrollReRenderCts;
        private const double PageSpacing = 28.0;

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

        public EditorPage(string filePath) : this(filePath, false, null, false)
        {
        }

        public EditorPage(string filePath, bool promptSaveAsAfterLoad, string pendingLibraryFolderId, bool isNotebookDraft) : this()
        {
            _currentPdfPath = filePath;
            _promptSaveAsAfterLoad = promptSaveAsAfterLoad;
            _pendingLibraryFolderId = pendingLibraryFolderId;
            _isNotebookDraft = isNotebookDraft;
            Loaded += async (s, e) => await LoadPdfAsync(filePath);
        }

        public void UpdateCurrentPdfPath(string filePath)
        {
            _currentPdfPath = filePath;
        }

        private bool IsEditableTextInputFocused()
        {
            if (Keyboard.FocusedElement is not DependencyObject focusedElement)
                return false;

            var textBoxBase = FindAncestor<TextBoxBase>(focusedElement);
            if (textBoxBase != null)
                return textBoxBase.IsEnabled && !textBoxBase.IsReadOnly;

            var comboBox = FindAncestor<ComboBox>(focusedElement);
            return comboBox != null && comboBox.IsEnabled && comboBox.IsEditable;
        }

        private async Task<bool> TryHandleUndoRedoShortcutAsync(KeyEventArgs e)
        {
            if (IsEditableTextInputFocused())
                return false;

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                e.Handled = true;
                await PerformUndoAsync();
                return true;
            }

            if ((Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) ||
                (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z))
            {
                e.Handled = true;
                await PerformRedoAsync();
                return true;
            }

            return false;
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
                else if (_activeSelectionPage != null && _activeSelectionPage.HasSelection)
                {
                    CopySelection();
                    e.Handled = true;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
            {
                PasteSelection();
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

        private void SyncSmoothScrollState(bool cancelAnimations = false)
        {
            if (cancelAnimations)
                CancelSmoothScroll();

            _targetVerticalOffset = PdfScrollViewer.VerticalOffset;
            _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;
            _smoothScrollInitialized = true;
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

            SyncSmoothScrollState();

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
            UpdateSelectedTextBoxPopupVisibility(forceRefresh: e.VerticalChange != 0 || e.HorizontalChange != 0);

            if (!_isScrollAnimating)
                _targetVerticalOffset = PdfScrollViewer.VerticalOffset;

            if (!_isHScrollAnimating)
                _targetHorizontalOffset = PdfScrollViewer.HorizontalOffset;

            _smoothScrollInitialized = true;

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

                UpdateSelectedTextBoxPopupVisibility(forceRefresh: true);
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
            SyncSmoothScrollState();
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

        private async Task PerformUndoAsync()
        {
            if (_undoStack.Count == 0) return;
            var action = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            await action.UndoAsync();
            _redoStack.Add(action);
            UpdateUndoRedoButtons();
            ApplyDirtyStateForAction(action);
        }

        private async Task PerformRedoAsync()
        {
            if (_redoStack.Count == 0) return;
            var action = _redoStack[_redoStack.Count - 1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            await action.RedoAsync();
            _undoStack.Add(action);
            UpdateUndoRedoButtons();
            ApplyDirtyStateForAction(action);
        }

        private void UpdateUndoRedoButtons()
        {
            UndoButton.IsEnabled = _undoStack.Count > 0;
            RedoButton.IsEnabled = _redoStack.Count > 0;
        }

        private void ApplyDirtyStateForAction(IUndoAction action)
        {
            _isDirty = action.LeavesDocumentDirty;
        }

        private void ClearUndoRedoHistory()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            UpdateUndoRedoButtons();
        }

        private void PushUndoAction(IUndoAction action)
        {
            _undoStack.Add(action);
            _redoStack.Clear();
            UpdateUndoRedoButtons();
            ApplyDirtyStateForAction(action);
        }

        private async void UndoButton_Click(object sender, RoutedEventArgs e) => await PerformUndoAsync();
        private async void RedoButton_Click(object sender, RoutedEventArgs e) => await PerformRedoAsync();

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

            var copyButton = new Button
            {
                Width = 36, Height = 32,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Copy",
                Margin = new Thickness(2)
            };
            copyButton.Template = CreateIconButtonTemplate("#E0F2FE", "#BAE6FD");
            copyButton.Content = new TextBlock
            {
                Text = "\uE14D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            copyButton.Click += (s, e) => CopySelection();

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
            actionPanel.Children.Add(copyButton);
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

        private void CopySelection()
        {
            if (_activeSelectionPage == null || !_activeSelectionPage.HasSelection)
                return;

            try
            {
                // Create annotation data for selected items
                var annotationData = new AnnotationData();
                var pageAnnotation = new PageAnnotation();

                // Add selected strokes
                foreach (var stroke in _activeSelectionPage.SelectedStrokes)
                {
                    var strokeAnnotation = new StrokeAnnotation
                    {
                        R = stroke.DrawingAttributes.Color.R,
                        G = stroke.DrawingAttributes.Color.G,
                        B = stroke.DrawingAttributes.Color.B,
                        A = stroke.DrawingAttributes.Color.A,
                        Size = stroke.DrawingAttributes.Width,
                        IsHighlighter = stroke.DrawingAttributes.IsHighlighter,
                        Points = new List<double[]>()
                    };

                    foreach (var point in stroke.StylusPoints)
                    {
                        strokeAnnotation.Points.Add(new double[] { point.X, point.Y });
                    }

                    pageAnnotation.Strokes.Add(strokeAnnotation);
                }

                // Add selected text annotations
                foreach (var container in _activeSelectionPage.SelectedTextContainers)
                {
                    if (container.Children.OfType<TextBox>().FirstOrDefault() is TextBox textBox)
                    {
                        var textAnnotation = new TextAnnotation
                        {
                            Text = textBox.Text,
                            X = Canvas.GetLeft(container),
                            Y = Canvas.GetTop(container),
                            R = ((SolidColorBrush)textBox.Foreground).Color.R,
                            G = ((SolidColorBrush)textBox.Foreground).Color.G,
                            B = ((SolidColorBrush)textBox.Foreground).Color.B,
                            FontSize = textBox.FontSize
                        };

                        pageAnnotation.Texts.Add(textAnnotation);
                    }
                }

                annotationData.Pages["0"] = pageAnnotation;

                // Serialize to JSON
                var json = System.Text.Json.JsonSerializer.Serialize(annotationData);

                // Copy to clipboard
                System.Windows.Clipboard.SetText(json);

                var mw = Window.GetWindow(this) as MainWindow;
                mw?.ShowToast("Selection copied", "\uE14D", 1500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CopySelection] Error: {ex.Message}");
            }
        }

        private void PasteSelection()
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsText())
                    return;

                var json = System.Windows.Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(json))
                    return;

                var annotationData = System.Text.Json.JsonSerializer.Deserialize<AnnotationData>(json);
                if (annotationData?.Pages == null || !annotationData.Pages.ContainsKey("0"))
                    return;

                var pageAnnotation = annotationData.Pages["0"];
                if (pageAnnotation == null)
                    return;

                // Find the active page - prefer _activeSelectionPage, otherwise find first visible page
                var targetPage = _activeSelectionPage;
                if (targetPage == null)
                {
                    targetPage = _pageControls.FirstOrDefault();
                }

                if (targetPage == null)
                    return;

                // Add a small offset to prevent pasting exactly on top of original
                const double pasteOffset = 20.0;

                // Paste strokes
                if (pageAnnotation.Strokes != null)
                {
                    foreach (var strokeAnnotation in pageAnnotation.Strokes)
                    {
                        // Apply paste offset
                        var offsetStroke = new StrokeAnnotation
                        {
                            R = strokeAnnotation.R,
                            G = strokeAnnotation.G,
                            B = strokeAnnotation.B,
                            A = strokeAnnotation.A,
                            Size = strokeAnnotation.Size,
                            IsHighlighter = strokeAnnotation.IsHighlighter,
                            Points = new List<double[]>()
                        };

                        foreach (var point in strokeAnnotation.Points)
                        {
                            offsetStroke.Points.Add(new double[] { point[0] + pasteOffset, point[1] + pasteOffset });
                        }

                        targetPage.AddStroke(offsetStroke);
                    }
                }

                // Paste text annotations
                if (pageAnnotation.Texts != null)
                {
                    foreach (var textAnnotation in pageAnnotation.Texts)
                    {
                        var offsetTextAnnotation = new TextAnnotation
                        {
                            Text = textAnnotation.Text,
                            X = textAnnotation.X + pasteOffset,
                            Y = textAnnotation.Y + pasteOffset,
                            R = textAnnotation.R,
                            G = textAnnotation.G,
                            B = textAnnotation.B,
                            FontSize = textAnnotation.FontSize
                        };

                        // Create the text box on the target page
                        var color = Color.FromRgb(offsetTextAnnotation.R, offsetTextAnnotation.G, offsetTextAnnotation.B);
                        CreateTextBox(targetPage, new Point(offsetTextAnnotation.X, offsetTextAnnotation.Y), color, offsetTextAnnotation.FontSize, offsetTextAnnotation.Text, select: false, alignToPointer: false);
                    }
                }

                MarkDirty();

                var mw = Window.GetWindow(this) as MainWindow;
                mw?.ShowToast("Selection pasted", "\uE14D", 1500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PasteSelection] Error: {ex.Message}");
                // Don't crash if paste fails - just ignore
            }
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

        private async void EditorPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (await TryHandleUndoRedoShortcutAsync(e))
                return;

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

        private static T FindAncestor<T>(DependencyObject descendant) where T : DependencyObject
        {
            var current = descendant;
            while (current != null)
            {
                if (current is T match)
                    return match;

                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }

            return null;
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
            _pageDeleteButtons.Clear();
            _pageInsertButtons.Clear();
            DeselectTextBox();
            _isDirty = false;
            _lastRenderedDpiScale = 1.0;
            _pagesRenderedAtScale.Clear();
            _pagesInitiallyRendered.Clear();
            DisposeSelectablePdfDocument();
            UpdatePdfSurfaceVisibility();
            ClearUndoRedoHistory();

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
                        Height = h
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

                    if (i > 0)
                        PagesContainer.Children.Add(CreatePageInsertGap(i));

                    PagesContainer.Children.Add(CreatePageHost(pageControl));
                }

                if (pageCount > 0)
                    PagesContainer.Children.Add(CreatePageInsertGap(pageCount));

                ApplyToolToAllPages();
                RefreshPageDeleteButtons();

                CancelSmoothScroll();
                PdfScrollViewer.ScrollToVerticalOffset(0);
                PdfScrollViewer.ScrollToHorizontalOffset(0);
                SyncSmoothScrollState();

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

                if (_promptSaveAsAfterLoad && !_hasPromptedForSaveAs)
                    await PromptSaveAsForDraftAsync();
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

        private FrameworkElement CreatePageHost(PdfPageControl pageControl)
        {
            var host = new Grid
            {
                Width = pageControl.Width,
                Height = pageControl.Height,
                HorizontalAlignment = HorizontalAlignment.Center,
                ClipToBounds = false
            };

            host.Children.Add(pageControl);

            var deleteButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 14, 0),
                MinHeight = 34,
                Padding = new Thickness(10, 6, 10, 6),
                Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(252, 165, 165)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Hidden,
                ToolTip = LocalizationService.Get("Editor.DeletePageTooltip"),
                Template = CreatePageChromeButtonTemplate("#FEE2E2", "#FECACA")
            };

            deleteButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = "\uE74D",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = LocalizationService.Get("Editor.DeletePageTooltip"),
                        Margin = new Thickness(6, 0, 0, 0),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(127, 29, 29)),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };

            deleteButton.Click += async (sender, args) =>
            {
                args.Handled = true;
                await DeletePageAtAsync(pageControl.PageIndex);
            };

            host.MouseEnter += (_, __) =>
            {
                if (_pageControls.Count > 1)
                    deleteButton.Visibility = Visibility.Visible;
            };
            host.MouseLeave += (_, __) =>
            {
                if (!deleteButton.IsMouseOver)
                    deleteButton.Visibility = Visibility.Hidden;
            };
            deleteButton.MouseLeave += (_, __) =>
            {
                if (!host.IsMouseOver)
                    deleteButton.Visibility = Visibility.Hidden;
            };

            _pageDeleteButtons.Add(deleteButton);
            host.Children.Add(deleteButton);
            return host;
        }

        private FrameworkElement CreatePageInsertGap(int insertIndex)
        {
            var zone = new Grid
            {
                Height = PageSpacing,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ClipToBounds = false
            };

            var guideLine = new Border
            {
                Width = 150,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var insertButton = new Button
            {
                Width = 78,
                Height = 32,
                Background = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                ToolTip = LocalizationService.Get("Editor.InsertPageHereTooltip"),
                Template = CreatePageChromeButtonTemplate("#EFF6FF", "#DBEAFE")
            };

            insertButton.Content = new TextBlock
            {
                Text = "\uE710",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            insertButton.Click += async (_, __) => await InsertPageAtAsync(insertIndex);

            zone.MouseEnter += (_, __) =>
            {
                guideLine.Visibility = Visibility.Visible;
                insertButton.Visibility = Visibility.Visible;
            };
            zone.MouseLeave += (_, __) =>
            {
                if (!insertButton.IsMouseOver)
                {
                    guideLine.Visibility = Visibility.Collapsed;
                    insertButton.Visibility = Visibility.Collapsed;
                }
            };
            insertButton.MouseLeave += (_, __) =>
            {
                if (!zone.IsMouseOver)
                {
                    guideLine.Visibility = Visibility.Collapsed;
                    insertButton.Visibility = Visibility.Collapsed;
                }
            };

            _pageInsertButtons.Add(insertButton);
            zone.Children.Add(guideLine);
            zone.Children.Add(insertButton);
            return zone;
        }

        private void RefreshPageDeleteButtons()
        {
            var visibility = _pageControls.Count > 1 ? Visibility.Hidden : Visibility.Collapsed;
            foreach (var button in _pageDeleteButtons)
            {
                button.Visibility = visibility;
                button.ToolTip = LocalizationService.Get("Editor.DeletePageTooltip");
                if (button.Content is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock label)
                    label.Text = LocalizationService.Get("Editor.DeletePageTooltip");
            }

            foreach (var button in _pageInsertButtons)
                button.ToolTip = LocalizationService.Get("Editor.InsertPageHereTooltip");
        }

        private async Task InsertPageAtAsync(int insertIndex)
        {
            if (string.IsNullOrWhiteSpace(_currentPdfPath))
            {
                GetMainWindow()?.ShowToast(LocalizationService.Get("Editor.NoDocumentLoaded"), "\uE783");
                return;
            }

            var owner = GetMainWindow();
            var picker = new PageTemplatePickerWindow();
            if (owner != null)
                picker.Owner = owner;

            if (picker.ShowDialog() != true)
                return;

            try
            {
                if (_isDirty)
                    await AutoSaveAsync();

                byte[] beforeBytes = await File.ReadAllBytesAsync(_currentPdfPath);
                int undoFocusIndex = Math.Max(0, Math.Min(insertIndex, Math.Max(_pageControls.Count - 1, 0)));

                await _pdfService.InsertPageAsync(_currentPdfPath, insertIndex, picker.SelectedTemplate);

                byte[] afterBytes = await File.ReadAllBytesAsync(_currentPdfPath);
                await LoadPdf(_currentPdfPath);

                int insertedPageIndex = Math.Max(0, Math.Min(insertIndex, _pageControls.Count - 1));
                JumpToPage(insertedPageIndex);
                RecentFilesService.UpdateMetadata(_currentPdfPath, _pageControls.Count, File.GetLastWriteTimeUtc(_currentPdfPath));
                PushUndoAction(new DocumentSnapshotAction(this, beforeBytes, afterBytes, undoFocusIndex, insertedPageIndex));
                GetMainWindow()?.ShowToast(LocalizationService.Get("Editor.PageAdded"), "\uE710");
            }
            catch (Exception ex)
            {
                var mw = GetMainWindow();
                if (mw != null)
                    await DialogService.ShowErrorAsync(mw, LocalizationService.Get("Common.Error"), LocalizationService.Format("Editor.AddPageFailed", ex.Message));
                else
                    MessageBox.Show(LocalizationService.Format("Editor.AddPageFailed", ex.Message), LocalizationService.Get("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DeletePageAtAsync(int pageIndex)
        {
            if (string.IsNullOrWhiteSpace(_currentPdfPath))
            {
                GetMainWindow()?.ShowToast(LocalizationService.Get("Editor.NoDocumentLoaded"), "\uE783");
                return;
            }

            if (_pageControls.Count <= 1)
            {
                GetMainWindow()?.ShowToast(LocalizationService.Get("Editor.PageDeleteBlocked"), "\uE783");
                return;
            }

            try
            {
                if (_isDirty)
                    await AutoSaveAsync();

                byte[] beforeBytes = await File.ReadAllBytesAsync(_currentPdfPath);
                await _pdfService.DeletePageAsync(_currentPdfPath, pageIndex);

                byte[] afterBytes = await File.ReadAllBytesAsync(_currentPdfPath);
                await LoadPdf(_currentPdfPath);

                int focusAfterDelete = Math.Max(0, Math.Min(pageIndex, _pageControls.Count - 1));
                JumpToPage(focusAfterDelete);
                RecentFilesService.UpdateMetadata(_currentPdfPath, _pageControls.Count, File.GetLastWriteTimeUtc(_currentPdfPath));
                PushUndoAction(new DocumentSnapshotAction(this, beforeBytes, afterBytes, pageIndex, focusAfterDelete));
                GetMainWindow()?.ShowToast(LocalizationService.Get("Editor.PageDeleted"), "\uE74D");
            }
            catch (InvalidOperationException)
            {
                GetMainWindow()?.ShowToast(LocalizationService.Get("Editor.PageDeleteBlocked"), "\uE783");
            }
            catch (Exception ex)
            {
                var mw = GetMainWindow();
                if (mw != null)
                    await DialogService.ShowErrorAsync(mw, LocalizationService.Get("Common.Error"), LocalizationService.Format("Editor.DeletePageFailed", ex.Message));
                else
                    MessageBox.Show(LocalizationService.Format("Editor.DeletePageFailed", ex.Message), LocalizationService.Get("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ApplyDocumentSnapshotAsync(byte[] snapshotBytes, int focusPageIndex)
        {
            if (string.IsNullOrWhiteSpace(_currentPdfPath))
                return;

            await WriteDocumentBytesAsync(_currentPdfPath, snapshotBytes);
            await LoadPdf(_currentPdfPath);

            if (_pageControls.Count > 0)
                JumpToPage(Math.Max(0, Math.Min(focusPageIndex, _pageControls.Count - 1)));

            RecentFilesService.UpdateMetadata(_currentPdfPath, _pageControls.Count, File.GetLastWriteTimeUtc(_currentPdfPath));
        }

        private static async Task WriteDocumentBytesAsync(string filePath, byte[] snapshotBytes)
        {
            string tempPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(filePath) ?? string.Empty,
                $"{System.IO.Path.GetFileName(filePath)}.{Guid.NewGuid():N}.snapshot");

            try
            {
                await File.WriteAllBytesAsync(tempPath, snapshotBytes);
                File.Copy(tempPath, filePath, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
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
            PushUndoAction(action);
        }

        private void PageControl_SelectionResizeCompleted(object sender, SelectionResizeCompletedEventArgs e)
        {
            if (sender is not PdfPageControl page) return;
            var action = new SelectionResizeAction(page, e.TotalScale, e.Anchor, e.SelectedStrokes, e.SelectedTextContainers);
            PushUndoAction(action);
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
            if (PageNumberLabel == null || PageCountText == null) return;

            if (_pageControls.Count == 0)
            {
                PageNumberLabel.Text = "0";
                PageCountText.Text = "/ 0";
                return;
            }

            int currentPageNumber = GetCurrentPageIndex() + 1;
            PageNumberLabel.Text = currentPageNumber.ToString();
            PageCountText.Text = $"/ {_pageControls.Count}";
        }

        private int GetCurrentPageIndex()
        {
            if (_pageControls.Count == 0)
                return 0;

            double viewportHeight = PdfScrollViewer.ViewportHeight;
            if (viewportHeight <= 0)
                return 0;

            double centerOffset = PdfScrollViewer.VerticalOffset + (viewportHeight / 2);
            int currentPageIndex = 0;

            for (int i = 0; i < _pageControls.Count; i++)
            {
                double pageTop = GetScaledPageTop(i);
                if (pageTop > centerOffset)
                    break;

                currentPageIndex = i;
            }

            return currentPageIndex;
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

        private async Task PromptSaveAsForDraftAsync()
        {
            if (_hasPromptedForSaveAs || string.IsNullOrWhiteSpace(_currentPdfPath))
                return;

            _hasPromptedForSaveAs = true;
            _promptSaveAsAfterLoad = false;

            var initialName = System.IO.Path.GetFileName(_currentPdfPath);
            var dialog = new SaveFileDialog
            {
                Filter = LocalizationService.Get("Home.PdfFilter"),
                Title = LocalizationService.Get("Home.SaveNotebookTitle"),
                FileName = initialName,
                AddExtension = true,
                DefaultExt = ".pdf",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
                return;

            var oldPath = _currentPdfPath;
            var newPath = dialog.FileName;

            try
            {
                if (_isDirty)
                    await AutoSaveAsync();

                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(newPath) ?? string.Empty);
                    File.Copy(oldPath, newPath, true);
                }

                RecentFilesService.UpdatePath(oldPath, newPath);
                RecentFilesService.AddOrPromote(newPath, _pageControls.Count, File.GetLastWriteTimeUtc(newPath), _pendingLibraryFolderId, true);
                UpdateCurrentPdfPath(newPath);
                _isNotebookDraft = false;
                GetMainWindow()?.HandleFilePathChanged(oldPath, newPath);
                GetMainWindow()?.ShowToast(LocalizationService.Get("Home.NotebookSaved"), "\uE74E");

                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) &&
                    oldPath.IndexOf(System.IO.Path.Combine("Caelum", "Drafts"), StringComparison.OrdinalIgnoreCase) >= 0 &&
                    File.Exists(oldPath))
                {
                    try
                    {
                        File.Delete(oldPath);
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                var mw = GetMainWindow();
                if (mw != null)
                    await DialogService.ShowErrorAsync(mw, LocalizationService.Get("Common.Error"), LocalizationService.Format("Home.CreateNotebookFailed", ex.Message));
                else
                    MessageBox.Show(LocalizationService.Format("Home.CreateNotebookFailed", ex.Message), LocalizationService.Get("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        private void PageJumpBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_pageControls.Count == 0)
                return;

            if (PageNumberTextBox.Visibility == Visibility.Visible)
                return;

            PageNumberLabel.Visibility = Visibility.Collapsed;
            PageNumberTextBox.Text = (GetCurrentPageIndex() + 1).ToString();
            PageNumberTextBox.Visibility = Visibility.Visible;
            PageNumberTextBox.Focus();
            PageNumberTextBox.SelectAll();
            e.Handled = true;
        }

        private void PageNumberTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyPageJumpFromTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HidePageNumberTextBox();
                e.Handled = true;
            }
        }

        private void PageNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (PageNumberTextBox.Visibility == Visibility.Visible)
                ApplyPageJumpFromTextBox();
        }

        private void ApplyPageJumpFromTextBox()
        {
            if (_pageControls.Count == 0)
            {
                HidePageNumberTextBox();
                return;
            }

            if (int.TryParse(PageNumberTextBox.Text.Trim(), out int requestedPage))
            {
                requestedPage = Math.Max(1, Math.Min(_pageControls.Count, requestedPage));
                JumpToPage(requestedPage - 1);
            }

            HidePageNumberTextBox();
        }

        private void HidePageNumberTextBox()
        {
            PageNumberTextBox.Visibility = Visibility.Collapsed;
            PageNumberLabel.Visibility = Visibility.Visible;
        }

        private void JumpToPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _pageControls.Count)
                return;

            CancelSmoothScroll();
            double targetOffset = Math.Max(0, GetScaledPageTop(pageIndex) - 12);
            PdfScrollViewer.ScrollToVerticalOffset(targetOffset);
            PdfScrollViewer.UpdateLayout();
            SyncSmoothScrollState();
            UpdatePageNumberIndicator();
            UpdateSelectedTextBoxPopupVisibility(forceRefresh: true);
        }

        private void UpdateZoomLabel()
        {
            if (ZoomLabel != null)
                ZoomLabel.Text = $"{(int)Math.Round(_zoomLevel * 100)}%";
        }

        private void InitializeTextBoxPopup()
        {
            _textBoxPopup = new Popup { Placement = PlacementMode.Top, StaysOpen = true, AllowsTransparency = true, VerticalOffset = -10 };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(28, 15, 23, 42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Child = panel
            };

            var deleteButton = new Button
            {
                Width = 28,
                Height = 28,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = LocalizationService.Get("Editor.DeleteTooltip"),
                Margin = new Thickness(0)
            };
            deleteButton.Template = CreateIconButtonTemplate("#FEE2E2", "#FECACA");
            deleteButton.Content = new TextBlock
            {
                Text = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            deleteButton.Click += (s, e) => DeleteSelectedTextBox();

            var sep1 = new Border
            {
                Width = 1,
                Height = 18,
                Background = new SolidColorBrush(Color.FromArgb(24, 15, 23, 42)),
                Margin = new Thickness(6, 5, 6, 5),
                VerticalAlignment = VerticalAlignment.Center
            };

            var decreaseFontButton = new Button
            {
                Width = 30,
                Height = 28,
                Margin = new Thickness(0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "Smaller text",
                Content = CreateTextSizeButtonContent(increase: false)
            };
            decreaseFontButton.Template = CreateIconButtonTemplate("#E5E7EB", "#D1D5DB");
            decreaseFontButton.Click += (s, e) => AdjustSelectedTextBoxFontSize(increase: false);

            var increaseFontButton = new Button
            {
                Width = 30,
                Height = 28,
                Margin = new Thickness(0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "Bigger text",
                Content = CreateTextSizeButtonContent(increase: true)
            };
            increaseFontButton.Template = CreateIconButtonTemplate("#E5E7EB", "#D1D5DB");
            increaseFontButton.Click += (s, e) => AdjustSelectedTextBoxFontSize(increase: true);

            var fontButtonGroup = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(24, 15, 23, 42)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(2, 0, 2, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        decreaseFontButton,
                        new Border
                        {
                            Width = 1,
                            Height = 16,
                            Margin = new Thickness(1, 0, 1, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Background = new SolidColorBrush(Color.FromArgb(30, 15, 23, 42))
                        },
                        increaseFontButton
                    }
                }
            };

            var sep2 = new Border
            {
                Width = 1,
                Height = 18,
                Background = new SolidColorBrush(Color.FromArgb(24, 15, 23, 42)),
                Margin = new Thickness(6, 5, 6, 5),
                VerticalAlignment = VerticalAlignment.Center
            };

            _colorIndicator = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(_textColor),
                BorderBrush = new SolidColorBrush(Color.FromArgb(36, 15, 23, 42)),
                BorderThickness = new Thickness(1)
            };
            var colorButton = new Button
            {
                Content = _colorIndicator,
                Width = 28,
                Height = 28,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0)
            };
            colorButton.Template = CreateIconButtonTemplate("#E0E7FF", "#DBEAFE");
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
                Background = new SolidColorBrush(Color.FromArgb(250, 248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(28, 15, 23, 42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Child = new StackPanel { Margin = new Thickness(16), Children = { paletteGrid } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.18, Color = Colors.Black }
            };
            colorButton.Click += (s, e) =>
            {
                colorPopup.PlacementTarget = colorButton;
                colorPopup.IsOpen = true;
            };

            panel.Children.Add(deleteButton);
            panel.Children.Add(sep1);
            panel.Children.Add(fontButtonGroup);
            panel.Children.Add(sep2);
            panel.Children.Add(colorButton);

            _textBoxPopup.Child = border;
        }

        // Popup no longer auto-deselects. Deselection happens via:
        // - Clicking on canvas background
        // - Switching tools
        // - Clicking outside in PageControl_BackgroundPointerPressed

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

        private void SelectTextBox(TextBox textBox, bool focusTextBox = true, bool refreshPopupPlacement = false)
        {
            if (textBox == null)
                return;

            // Auto-switch to Text tool when clicking on an existing textbox,
            // regardless of the currently active tool.
            if (_currentTool != ToolType.Text)
                ActivateTool(ToolType.Text);

            bool selectionChanged = !ReferenceEquals(_selectedTextBox, textBox);

            if (_selectedTextBox != null && selectionChanged)
            {
                ApplyTextBoxChrome(_selectedTextBox, isSelected: false);
                _selectedTextBox.IsReadOnly = true;
            }

            _selectedTextBox = textBox;
            textBox.IsReadOnly = false;
            ApplyTextBoxChrome(textBox, isSelected: true);
            SyncPopupToSelectedTextBox();
            ShowTextBoxPopupFor(textBox.Parent as UIElement ?? textBox, refreshPopupPlacement || selectionChanged);

            if (focusTextBox && !textBox.IsKeyboardFocusWithin)
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

            _currentFontSize = _selectedTextBox.FontSize;
            var current = (_selectedTextBox.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
            _colorIndicator.Background = new SolidColorBrush(current);
        }

        private void AdjustSelectedTextBoxFontSize(bool increase)
        {
            if (_selectedTextBox == null)
                return;

            double currentSize = _selectedTextBox.FontSize;
            double nextSize = GetSteppedFontSize(currentSize, increase);
            if (Math.Abs(nextSize - currentSize) < 0.01)
                return;

            _selectedTextBox.FontSize = nextSize;
            _currentFontSize = nextSize;
            MarkDirty();
            RefreshPopupPlacement(_textBoxPopup);
            _selectedTextBox.Focus();
        }

        private static double GetSteppedFontSize(double currentSize, bool increase)
        {
            if (TextFontSizeSteps.Length == 0)
                return currentSize;

            if (increase)
            {
                foreach (double size in TextFontSizeSteps)
                {
                    if (size > currentSize + 0.1)
                        return size;
                }

                return TextFontSizeSteps[^1];
            }

            for (int i = TextFontSizeSteps.Length - 1; i >= 0; i--)
            {
                if (TextFontSizeSteps[i] < currentSize - 0.1)
                    return TextFontSizeSteps[i];
            }

            return TextFontSizeSteps[0];
        }

        private static UIElement CreateTextSizeButtonContent(bool increase)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "A",
                        FontSize = increase ? 15 : 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = increase ? "^" : "v",
                        FontSize = 8,
                        Margin = new Thickness(1, 0, 0, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                        VerticalAlignment = increase ? VerticalAlignment.Top : VerticalAlignment.Bottom
                    }
                }
            };
        }

        private void UpdateSelectedTextBoxPopupVisibility(bool forceRefresh)
        {
            if (_selectedTextBox == null || _textBoxPopup == null)
                return;

            var placementTarget = _selectedTextBox.Parent as UIElement ?? _selectedTextBox;
            if (!IsElementVisibleInPdfViewport(placementTarget))
            {
                _textBoxPopup.IsOpen = false;
                return;
            }

            ShowTextBoxPopupFor(placementTarget, forceRefresh);
        }

        private bool IsElementVisibleInPdfViewport(UIElement element)
        {
            if (element == null || PdfScrollViewer == null || !element.IsVisible)
                return false;

            double viewportWidth = PdfScrollViewer.ViewportWidth > 0 ? PdfScrollViewer.ViewportWidth : PdfScrollViewer.ActualWidth;
            double viewportHeight = PdfScrollViewer.ViewportHeight > 0 ? PdfScrollViewer.ViewportHeight : PdfScrollViewer.ActualHeight;
            if (viewportWidth <= 0 || viewportHeight <= 0 || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0)
                return false;

            try
            {
                var bounds = element.TransformToAncestor(PdfScrollViewer)
                    .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
                var viewportBounds = new Rect(0, 0, viewportWidth, viewportHeight);
                return bounds.IntersectsWith(viewportBounds);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void ShowTextBoxPopupFor(UIElement placementTarget, bool forceRefresh)
        {
            if (_textBoxPopup == null || placementTarget == null)
                return;

            bool targetChanged = !ReferenceEquals(_textBoxPopup.PlacementTarget, placementTarget);
            _textBoxPopup.PlacementTarget = placementTarget;

            if (!_textBoxPopup.IsOpen)
            {
                _textBoxPopup.IsOpen = true;
                return;
            }

            if (forceRefresh || targetChanged)
                RefreshPopupPlacement(_textBoxPopup);
        }

        private static void RefreshPopupPlacement(Popup popup)
        {
            if (popup == null || !popup.IsOpen)
                return;

            double horizontalOffset = popup.HorizontalOffset;
            double verticalOffset = popup.VerticalOffset;

            popup.HorizontalOffset = horizontalOffset + 0.001;
            popup.VerticalOffset = verticalOffset + 0.001;
            popup.HorizontalOffset = horizontalOffset;
            popup.VerticalOffset = verticalOffset;
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

            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Visual chrome border spanning both columns
            var chrome = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = select ? new Thickness(1.5) : new Thickness(0),
                BorderBrush = select ? new SolidColorBrush(Color.FromArgb(90, 0, 120, 212)) : Brushes.Transparent,
                Background = select ? new SolidColorBrush(Color.FromArgb(10, 0, 120, 212)) : Brushes.Transparent,
                IsHitTestVisible = false,
                Tag = "chrome"
            };
            Grid.SetColumnSpan(chrome, 2);

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
                Width = 18,
                Height = 36,
                Margin = new Thickness(8, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(9),
                Visibility = select ? Visibility.Visible : Visibility.Collapsed,
                Cursor = Cursors.SizeAll,
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 1,
                    Opacity = 0.10,
                    Color = Colors.Black
                }
            };

            var dragIcon = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            for (int column = 0; column < 2; column++)
            {
                var dotColumn = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(column == 0 ? 0 : 2, 0, 0, 0)
                };

                for (int row = 0; row < 3; row++)
                {
                    dotColumn.Children.Add(new Ellipse
                    {
                        Width = 3,
                        Height = 3,
                        Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                        Margin = new Thickness(0, 1.5, 0, 1.5)
                    });
                }

                dragIcon.Children.Add(dotColumn);
            }
            dragHandle.Child = dragIcon;

            Grid.SetColumn(textBox, 0);
            Grid.SetColumn(dragHandle, 1);

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
                // Let the native TextBox click logic place the caret.
                // We only switch selection/read-only state before WPF handles the click.
                SelectTextBox((TextBox)s, focusTextBox: false);
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
                if (tb != null)
                    SelectTextBox(tb, focusTextBox: false);
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
                        ShowTextBoxPopupFor(_draggedContainer, forceRefresh: true);
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
                PushUndoAction(new StrokeAddedAction(page, stroke));
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

        private static ControlTemplate CreatePageChromeButtonTemplate(string hoverColor, string pressedColor)
        {
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "Root";
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);

            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverColor)), "Root"));
            template.Triggers.Add(hoverTrigger);

            var pressTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(pressedColor)), "Root"));
            template.Triggers.Add(pressTrigger);

            return template;
        }
    }
}








