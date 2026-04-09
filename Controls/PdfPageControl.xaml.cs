using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Ink;
using Caelum.Models;
using Caelum.Services;

namespace Caelum.Controls
{
    public enum CustomInkInputProcessingMode { None, Inking, Erasing }

    public enum SelectionFilter { Both, DrawingsOnly, TextOnly }
    public enum SelectionShape { Rectangle, FreeForm }

    public sealed class SelectionMoveCompletedEventArgs : EventArgs
    {
        public SelectionMoveCompletedEventArgs(double deltaX, double deltaY, List<System.Windows.Ink.Stroke> strokes, List<System.Windows.Controls.Grid> containers)
        {
            DeltaX = deltaX;
            DeltaY = deltaY;
            SelectedStrokes = strokes;
            SelectedTextContainers = containers;
        }
        public double DeltaX { get; }
        public double DeltaY { get; }
        public List<System.Windows.Ink.Stroke> SelectedStrokes { get; }
        public List<System.Windows.Controls.Grid> SelectedTextContainers { get; }
    }

    public sealed class SelectionResizeCompletedEventArgs : EventArgs
    {
        public SelectionResizeCompletedEventArgs(double totalScale, Point anchor, List<System.Windows.Ink.Stroke> strokes, List<System.Windows.Controls.Grid> containers)
        {
            TotalScale = totalScale;
            Anchor = anchor;
            SelectedStrokes = strokes;
            SelectedTextContainers = containers;
        }
        public double TotalScale { get; }
        public Point Anchor { get; }
        public List<System.Windows.Ink.Stroke> SelectedStrokes { get; }
        public List<System.Windows.Controls.Grid> SelectedTextContainers { get; }
    }

    public sealed class PdfTextSelectionPointerEventArgs : EventArgs
    {
        public PdfTextSelectionPointerEventArgs(Point position, MouseButtonState leftButton)
        {
            Position = position;
            LeftButton = leftButton;
        }

        public Point Position { get; }
        public MouseButtonState LeftButton { get; }
    }

    public sealed class AnnotationSelectionChangedEventArgs : EventArgs
    {
        public AnnotationSelectionChangedEventArgs(bool hasSelection, Rect bounds)
        {
            HasSelection = hasSelection;
            Bounds = bounds;
        }

        public bool HasSelection { get; }
        public Rect Bounds { get; }
    }

    public sealed partial class PdfPageControl : UserControl
    {
        public static readonly DependencyProperty PageSourceProperty =
            DependencyProperty.Register(nameof(PageSource), typeof(BitmapSource), typeof(PdfPageControl), new PropertyMetadata(null, OnPageSourceChanged));

        public BitmapSource PageSource
        {
            get => (BitmapSource)GetValue(PageSourceProperty);
            set => SetValue(PageSourceProperty, value);
        }

        public int PageIndex { get; set; }

        public Canvas TextOverlay => TextOverlayCanvas;

        public event EventHandler<MouseButtonEventArgs> TextOverlayPointerPressed;
        public event EventHandler<MouseButtonEventArgs> BackgroundPointerPressed;
        public event EventHandler<PdfTextSelectionPointerEventArgs> PdfTextSelectionPointerPressed;
        public event EventHandler<PdfTextSelectionPointerEventArgs> PdfTextSelectionPointerMoved;
        public event EventHandler<PdfTextSelectionPointerEventArgs> PdfTextSelectionPointerReleased;
        public event EventHandler InkMutated;
        public event EventHandler<Stroke> StrokeCollectedUndoable;
        public event EventHandler<CustomInkInputProcessingMode> ModeChanged;
        public event EventHandler<AnnotationSelectionChangedEventArgs> SelectionChanged;
        public event EventHandler<SelectionMoveCompletedEventArgs> SelectionMoveCompleted;
        public event EventHandler<SelectionResizeCompletedEventArgs> SelectionResizeCompleted;

        private DrawingAttributes _drawingAttributes;
        private CustomInkInputProcessingMode _currentMode = CustomInkInputProcessingMode.None;
        private double _eraserSize = 20;
        private bool _isErasing;
        private StylusPointCollection _erasePoints;
        private bool _isPdfTextSelectionEnabled;

        // Selection transform state
        private bool _isSelectionMode;
        private SelectionFilter _selectionFilter = SelectionFilter.Both;
        private SelectionShape _selectionShape = SelectionShape.Rectangle;
        private bool _isSelecting;
        private Point _selectionStartPoint;
        private System.Windows.Shapes.Rectangle _selectionRect;
        private System.Windows.Shapes.Polyline _freeSelectionPath;
        private System.Windows.Media.PointCollection _freeSelectionPoints;
        private List<Stroke> _selectedStrokes = new List<Stroke>();
        private List<Grid> _selectedTextContainers = new List<Grid>();
        private bool _isDraggingSelection;
        private Point _dragStartPoint;
        private double _totalDragDeltaX;
        private double _totalDragDeltaY;
        private bool _isResizingSelection;
        private int _resizeHandleIndex; // 0=TL, 1=TR, 2=BL, 3=BR
        private Point _resizeAnchorPoint;
        private double _resizeStartHandleDist;
        private double _lastResizeScale;
        // Tracks whether the stylus is currently inverted (physical eraser end),
        // which corresponds to Windows Ink's IsEraser / PointerPointProperties.IsEraser.
        // When inverted, we override the current mode to erase regardless of the
        // selected tool �?this is how standard Windows Ink pens work and is the
        // signal path used by Huawei M-Pencil when MateBook-E-Pen is active.
        private bool _isStylusInverted;

        // Universal pen service for pressure / tilt / device detection
        private WindowsPenService _penService;

        /// <summary>
        /// Whether pressure-sensitive width variation is active.
        /// When true, each stroke is post-processed to vary width by pressure.
        /// </summary>
        public bool PressureEnabled { get; set; } = true;

        /// <summary>
        /// Whether tilt-based width variation is active.
        /// </summary>
        public bool TiltEnabled { get; set; } = true;

        // Custom stroke collection to prevent WPF's InkCanvas from clearing strokes
        // during visibility or EditingMode toggles.
        private readonly StrokeCollection _strokes = new StrokeCollection();

        public PdfPageControl()
        {
            InitializeComponent();
            
            // Assign stable stroke collection
            InkCanvas.Strokes = _strokes;

            _drawingAttributes = new DrawingAttributes
            {
                Color = Colors.Black,
                Width = 2,
                Height = 2,
                FitToCurve = true,
                StylusTip = StylusTip.Ellipse,
                // Ensure WPF captures and applies pressure data from the digitiser.
                // This makes stroke width vary with pen pressure on all devices
                // that report NormalPressure (Surface Pen, Wacom, etc.).
                IgnorePressure = false
            };

            InkCanvas.DefaultDrawingAttributes = _drawingAttributes;
            InkCanvas.UseCustomCursor = true;
            InkCanvas.Cursor = Cursors.None;
            InkCanvas.EditingMode = InkCanvasEditingMode.None;
            InkCanvas.EditingModeInverted = InkCanvasEditingMode.None; // Disable native inverted erasing so we can use custom logic
            InkCanvas.StrokeCollected += InkCanvas_StrokeCollected;
            InkCanvas.StrokeErasing += InkCanvas_StrokeErasing;
            InkCanvas.StrokeErased += InkCanvas_StrokeErased;

            TextOverlayCanvas.IsHitTestVisible = false;
            PdfTextSelectionCanvas.IsHitTestVisible = false;
            InkCanvas.IsHitTestVisible = true;

            Loaded += PdfPageControl_Loaded;
            Unloaded += PdfPageControl_Unloaded;
        }

        /// <summary>
        /// Returns true if the given stylus device is a finger touch rather
        /// than a pen/stylus.  Used to let finger events pass through to the
        /// WPF manipulation system for pan/zoom gestures.
        /// </summary>
        private static bool IsTouchFinger(StylusDevice device)
        {
            if (device == null) return false;
            var tablet = device.TabletDevice;
            if (tablet == null) return false;
            // Pen devices report as Stylus. Some pen-as-touch devices (e.g.
            // Huawei M-Pencil) report as Touch but have multiple stylus buttons.
            // Real fingers have TabletDeviceType.Touch with ≤1 button.
            if (tablet.Type == System.Windows.Input.TabletDeviceType.Stylus)
                return false;
            return tablet.Type == System.Windows.Input.TabletDeviceType.Touch
                && device.StylusButtons.Count <= 1;
        }

        /// <summary>
        /// Set the shared <see cref="WindowsPenService"/> so this control can
        /// probe stylus devices and read pressure/tilt capabilities.
        /// </summary>
        public void SetPenService(WindowsPenService service)
        {
            _penService = service;
            if (service != null)
            {
                PressureEnabled = service.PressureEnabled;
                TiltEnabled = service.TiltEnabled;
            }
        }

        private void InkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            PreserveTapStroke(e.Stroke);

            InkMutated?.Invoke(this, EventArgs.Empty);
            StrokeCollectedUndoable?.Invoke(this, e.Stroke);
        }

        private static void PreserveTapStroke(Stroke stroke)
        {
            if (stroke?.StylusPoints == null || stroke.StylusPoints.Count != 1)
            {
                return;
            }

            // A tap that should render as a dot may arrive as a single stylus
            // point. Expand it to a tiny segment so WPF keeps it visible.
            var point = stroke.StylusPoints[0];
            stroke.StylusPoints.Add(new StylusPoint(point.X + 0.1, point.Y));
        }

        private void InkCanvas_StrokeErasing(object sender, InkCanvasStrokeErasingEventArgs e)
        {
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        private void InkCanvas_StrokeErased(object sender, RoutedEventArgs e)
        {
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        private void PdfPageControl_Loaded(object sender, RoutedEventArgs e)
        {
            TextOverlayCanvas.MouseDown += TextOverlayCanvas_MouseDown;
            PdfTextSelectionCanvas.MouseLeftButtonDown += PdfTextSelectionCanvas_MouseLeftButtonDown;
            PdfTextSelectionCanvas.MouseMove += PdfTextSelectionCanvas_MouseMove;
            PdfTextSelectionCanvas.MouseLeftButtonUp += PdfTextSelectionCanvas_MouseLeftButtonUp;
            PdfTextSelectionCanvas.StylusDown += PdfTextSelectionCanvas_StylusDown;
            PdfTextSelectionCanvas.StylusMove += PdfTextSelectionCanvas_StylusMove;
            PdfTextSelectionCanvas.StylusUp += PdfTextSelectionCanvas_StylusUp;
            PageGrid.MouseDown += PageGrid_MouseDown;
            InkCanvas.MouseMove += InkCanvas_MouseMove;
            InkCanvas.MouseUp += InkCanvas_MouseUp;
            InkCanvas.StylusDown += InkCanvas_StylusDown;
            InkCanvas.StylusMove += InkCanvas_StylusMove;
            InkCanvas.StylusUp += InkCanvas_StylusUp;
            InkCanvas.StylusInAirMove += InkCanvas_StylusInAirMove;
            InkCanvas.StylusButtonDown += InkCanvas_StylusButtonDown;
            InkCanvas.StylusButtonUp += InkCanvas_StylusButtonUp;
            InkCanvas.MouseEnter += InkCanvas_MouseEnter;
            InkCanvas.MouseLeave += InkCanvas_MouseLeave;
            SelectionOverlayCanvas.MouseLeftButtonDown += SelectionOverlayCanvas_MouseLeftButtonDown;
            SelectionOverlayCanvas.MouseMove += SelectionOverlayCanvas_MouseMove;
            SelectionOverlayCanvas.MouseLeftButtonUp += SelectionOverlayCanvas_MouseLeftButtonUp;
            SelectionOverlayCanvas.StylusDown += SelectionOverlayCanvas_StylusDown;
            SelectionOverlayCanvas.StylusMove += SelectionOverlayCanvas_StylusMove;
            SelectionOverlayCanvas.StylusUp += SelectionOverlayCanvas_StylusUp;

            // Fix for auto-scroll bug: Prevent ScrollViewer from scrolling when InkCanvas gets focus
            this.RequestBringIntoView += PdfPageControl_RequestBringIntoView;
        }

        private void PdfPageControl_Unloaded(object sender, RoutedEventArgs e)
        {
            TextOverlayCanvas.MouseDown -= TextOverlayCanvas_MouseDown;
            PdfTextSelectionCanvas.MouseLeftButtonDown -= PdfTextSelectionCanvas_MouseLeftButtonDown;
            PdfTextSelectionCanvas.MouseMove -= PdfTextSelectionCanvas_MouseMove;
            PdfTextSelectionCanvas.MouseLeftButtonUp -= PdfTextSelectionCanvas_MouseLeftButtonUp;
            PdfTextSelectionCanvas.StylusDown -= PdfTextSelectionCanvas_StylusDown;
            PdfTextSelectionCanvas.StylusMove -= PdfTextSelectionCanvas_StylusMove;
            PdfTextSelectionCanvas.StylusUp -= PdfTextSelectionCanvas_StylusUp;
            PageGrid.MouseDown -= PageGrid_MouseDown;
            InkCanvas.MouseMove -= InkCanvas_MouseMove;
            InkCanvas.MouseUp -= InkCanvas_MouseUp;
            InkCanvas.StylusDown -= InkCanvas_StylusDown;
            InkCanvas.StylusMove -= InkCanvas_StylusMove;
            InkCanvas.StylusUp -= InkCanvas_StylusUp;
            InkCanvas.StylusInAirMove -= InkCanvas_StylusInAirMove;
            InkCanvas.StylusButtonDown -= InkCanvas_StylusButtonDown;
            InkCanvas.StylusButtonUp -= InkCanvas_StylusButtonUp;
            InkCanvas.MouseEnter -= InkCanvas_MouseEnter;
            InkCanvas.MouseLeave -= InkCanvas_MouseLeave;
            SelectionOverlayCanvas.MouseLeftButtonDown -= SelectionOverlayCanvas_MouseLeftButtonDown;
            SelectionOverlayCanvas.MouseMove -= SelectionOverlayCanvas_MouseMove;
            SelectionOverlayCanvas.MouseLeftButtonUp -= SelectionOverlayCanvas_MouseLeftButtonUp;
            SelectionOverlayCanvas.StylusDown -= SelectionOverlayCanvas_StylusDown;
            SelectionOverlayCanvas.StylusMove -= SelectionOverlayCanvas_StylusMove;
            SelectionOverlayCanvas.StylusUp -= SelectionOverlayCanvas_StylusUp;

            this.RequestBringIntoView -= PdfPageControl_RequestBringIntoView;
        }

        private void PdfPageControl_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

        private void InkCanvas_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_currentMode == CustomInkInputProcessingMode.Erasing || _isStylusInverted || _currentMode == CustomInkInputProcessingMode.Inking)
            {
                UpdateBrushIndicatorStyle();
                EraserIndicator.Visibility = Visibility.Visible;
                Cursor = Cursors.None;
                UpdateEraserIndicatorPosition(e.GetPosition(PageGrid));
            }
        }

        private void InkCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            EraserIndicator.Visibility = Visibility.Collapsed;
            Cursor = Cursors.Arrow;
            _isStylusInverted = false;
        }

        /// <summary>
        /// Detects when the stylus is hovering in inverted (eraser) mode.
        /// This is the standard Windows Ink path for IsEraser: when the user
        /// flips the pen to the eraser end, StylusDevice.Inverted becomes true
        /// even while hovering, before contact.  Also triggered by Huawei
        /// M-Pencil when MateBook-E-Pen patches AcAppDaemon.exe.
        /// </summary>
        private void InkCanvas_StylusInAirMove(object sender, StylusEventArgs e)
        {
            // Probe the device early while hovering so capabilities are known
            // before the first stroke lands.
            _penService?.ProbeDevice(e.StylusDevice);

            bool inverted = e.StylusDevice?.Inverted == true;

            if (inverted != _isStylusInverted)
            {
                _isStylusInverted = inverted;
                Console.WriteLine(
                    $"[PdfPageControl] Stylus Inverted changed �?{inverted} (device={e.StylusDevice?.Name})");

                if (inverted)
                {
                    // Show eraser indicator while hovering with inverted pen
                    InkCanvas.EditingMode = InkCanvasEditingMode.None; // suppress inking
                    EraserIndicator.Visibility = Visibility.Visible;
                    InkCanvas.Cursor = Cursors.None;
                }
                else if (_currentMode != CustomInkInputProcessingMode.Erasing)
                {
                    // Pen flipped back to normal �?restore previous mode
                    EraserIndicator.Visibility = Visibility.Collapsed;
                    SetInputMode(_currentMode);
                }
            }

            if (inverted)
            {
                UpdateBrushIndicatorStyle();
                UpdateEraserIndicatorPosition(e.GetPosition(PageGrid));
            }
            else if (_currentMode == CustomInkInputProcessingMode.Inking)
            {
                UpdateBrushIndicatorStyle();
                UpdateEraserIndicatorPosition(e.GetPosition(PageGrid));
            }
        }

        private void TextOverlayCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Only forward clicks directly on the canvas background, not on child elements like TextBoxes
            if (e.OriginalSource == TextOverlayCanvas)
                TextOverlayPointerPressed?.Invoke(this, e);
        }

        private void PageGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentMode == CustomInkInputProcessingMode.None)
            {
                if (e.OriginalSource is DependencyObject source &&
                    IsDescendantOf(source, TextOverlayCanvas) &&
                    source != TextOverlayCanvas)
                {
                    return;
                }

                BackgroundPointerPressed?.Invoke(this, e);
            }
        }

        private void PdfTextSelectionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isPdfTextSelectionEnabled)
                return;

            PdfTextSelectionCanvas.CaptureMouse();
            PdfTextSelectionPointerPressed?.Invoke(this, new PdfTextSelectionPointerEventArgs(e.GetPosition(PageGrid), e.LeftButton));
            e.Handled = true;
        }

        private void PdfTextSelectionCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPdfTextSelectionEnabled)
                return;

            PdfTextSelectionPointerMoved?.Invoke(this, new PdfTextSelectionPointerEventArgs(e.GetPosition(PageGrid), e.LeftButton));
            if (PdfTextSelectionCanvas.IsMouseCaptured)
                e.Handled = true;
        }

        private void PdfTextSelectionCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPdfTextSelectionEnabled)
                return;

            PdfTextSelectionPointerReleased?.Invoke(this, new PdfTextSelectionPointerEventArgs(e.GetPosition(PageGrid), e.LeftButton));
            if (PdfTextSelectionCanvas.IsMouseCaptured)
                PdfTextSelectionCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void PdfTextSelectionCanvas_StylusDown(object sender, StylusDownEventArgs e)
        {
            if (!_isPdfTextSelectionEnabled)
                return;

            // Let finger touches pass through to WPF manipulation (pan/zoom).
            // Only pen/stylus devices should trigger PDF text selection.
            if (IsTouchFinger(e.StylusDevice))
                return;

            PdfTextSelectionCanvas.CaptureStylus();
            PdfTextSelectionPointerPressed?.Invoke(this, new PdfTextSelectionPointerEventArgs(e.GetPosition(PageGrid), MouseButtonState.Pressed));
            e.Handled = true;
        }

        private void PdfTextSelectionCanvas_StylusMove(object sender, StylusEventArgs e)
        {
            if (!_isPdfTextSelectionEnabled)
                return;

            if (IsTouchFinger(e.StylusDevice))
                return;

            PdfTextSelectionPointerMoved?.Invoke(this, new PdfTextSelectionPointerEventArgs(e.GetPosition(PageGrid), MouseButtonState.Pressed));
            if (PdfTextSelectionCanvas.IsStylusCaptured)
                e.Handled = true;
        }

        private void PdfTextSelectionCanvas_StylusUp(object sender, StylusEventArgs e)
        {
            if (!_isPdfTextSelectionEnabled)
                return;

            if (IsTouchFinger(e.StylusDevice))
                return;

            PdfTextSelectionPointerReleased?.Invoke(this, new PdfTextSelectionPointerEventArgs(e.GetPosition(PageGrid), MouseButtonState.Released));
            if (PdfTextSelectionCanvas.IsStylusCaptured)
                PdfTextSelectionCanvas.ReleaseStylusCapture();
            e.Handled = true;
        }

        private bool _isBarrelButtonPressed = false;
        private DateTime _lastBarrelButtonDownTime = DateTime.MinValue;
        private CustomInkInputProcessingMode _previousMode = CustomInkInputProcessingMode.Inking;

        private static readonly Guid[] SideButtonGuids = new[]
        {
            StylusPointProperties.BarrelButton.Id,
            StylusPointProperties.SecondaryTipButton.Id,
            // NOTE: TipButton is the primary pen tip contact, NOT a side button.
            // Including it here caused every pen-down to be treated as a barrel-button
            // press, breaking inking on Huawei M-Pencil and similar devices.
        };

        private void InkCanvas_StylusButtonDown(object sender, StylusButtonEventArgs e)
        {
            Console.WriteLine($"[PdfPageControl] StylusButtonDown: name={e.StylusButton.Name}, GUID={e.StylusButton.Guid}, device={e.StylusDevice?.Name}");

            // Ignore the tip button �?it fires on every pen contact and is NOT a side button.
            if (e.StylusButton.Guid == StylusPointProperties.TipButton.Id)
                return;

            bool isSideButton = false;
            foreach (var guid in SideButtonGuids)
            {
                if (e.StylusButton.Guid == guid)
                {
                    isSideButton = true;
                    break;
                }
            }

            if (isSideButton || e.StylusButton.Name.Contains("Barrel") || e.StylusButton.Name.Contains("Side") || e.StylusButton.Name.Contains("Secondary"))
            {
                _isBarrelButtonPressed = true;

                if ((DateTime.Now - _lastBarrelButtonDownTime).TotalMilliseconds < 500)
                {
                    if (_currentMode == CustomInkInputProcessingMode.Erasing)
                    {
                        SetInputMode(_previousMode);
                    }
                    else
                    {
                        _previousMode = _currentMode;
                        SetInputMode(CustomInkInputProcessingMode.Erasing);
                    }
                    _lastBarrelButtonDownTime = DateTime.MinValue;
                }
                else
                {
                    _lastBarrelButtonDownTime = DateTime.Now;
                }

                if (InkCanvas.EditingMode != InkCanvasEditingMode.None)
                {
                    InkCanvas.EditingMode = InkCanvasEditingMode.None;
                }
            }
        }

        private void InkCanvas_StylusButtonUp(object sender, StylusButtonEventArgs e)
        {
            if (e.StylusButton.Guid == StylusPointProperties.TipButton.Id)
                return;

            bool isSideButton = false;
            foreach (var guid in SideButtonGuids)
            {
                if (e.StylusButton.Guid == guid)
                {
                    isSideButton = true;
                    break;
                }
            }

            if (isSideButton || e.StylusButton.Name.Contains("Barrel") || e.StylusButton.Name.Contains("Side") || e.StylusButton.Name.Contains("Secondary"))
            {
                _isBarrelButtonPressed = false;
                SetInputMode(_currentMode);
            }
        }

        private void InkCanvas_StylusDown(object sender, StylusDownEventArgs e)
        {
            // Probe the stylus device so WindowsPenService can detect its
            // capabilities (pressure levels, tilt, barrel button, etc.).
            _penService?.ProbeDevice(e.StylusDevice);

            bool shouldErase = e.Inverted || _isStylusInverted
                || _isBarrelButtonPressed
                || _currentMode == CustomInkInputProcessingMode.Erasing;

            Console.WriteLine($"[PdfPageControl] StylusDown: Inverted={e.Inverted}, _isStylusInverted={_isStylusInverted}, barrel={_isBarrelButtonPressed}, mode={_currentMode}, shouldErase={shouldErase}, device={e.StylusDevice?.Name}");

            if (shouldErase)
            {
                _isErasing = true;
                // Ensure InkCanvas doesn't draw while we're erasing
                if (InkCanvas.EditingMode != InkCanvasEditingMode.None)
                    InkCanvas.EditingMode = InkCanvasEditingMode.None;

                // Show eraser indicator at contact point
                UpdateBrushIndicatorStyle();
                EraserIndicator.Visibility = Visibility.Visible;
                InkCanvas.Cursor = Cursors.None;
                UpdateEraserIndicatorPosition(e.GetPosition(PageGrid));

                _erasePoints = e.GetStylusPoints(InkCanvas);
                EraseStrokesAtPoints(_erasePoints);
                e.Handled = true;
            }
        }

        private void InkCanvas_StylusMove(object sender, StylusEventArgs e)
        {
            bool shouldErase = _isErasing
                && (e.StylusDevice?.Inverted == true || _isStylusInverted
                    || _isBarrelButtonPressed
                    || _currentMode == CustomInkInputProcessingMode.Erasing);

            if (shouldErase)
            {
                UpdateEraserIndicatorPosition(e.GetPosition(PageGrid));
                var newPoints = e.GetStylusPoints(InkCanvas);
                EraseStrokesAtPoints(newPoints);
            }
        }

        private void InkCanvas_StylusUp(object sender, StylusEventArgs e)
        {
            // Safety: always clear barrel-button flag on pen lift to prevent stuck state.
            // On some Huawei digitizers, StylusButtonUp may not fire reliably for all buttons.
            _isBarrelButtonPressed = false;

            if (_isErasing)
            {
                _isErasing = false;
                _erasePoints = null;

                // If the pen is still inverted (hovering after lift-off), keep
                // showing the eraser indicator.  Otherwise restore the mode.
                if (!_isStylusInverted && _currentMode != CustomInkInputProcessingMode.Erasing)
                {
                    EraserIndicator.Visibility = Visibility.Collapsed;
                    SetInputMode(_currentMode);
                }
            }
        }

        private void InkCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_currentMode == CustomInkInputProcessingMode.Erasing || _currentMode == CustomInkInputProcessingMode.Inking)
            {
                UpdateBrushIndicatorStyle();
                var point = e.GetPosition(PageGrid);
                UpdateEraserIndicatorPosition(point);

                if (_currentMode == CustomInkInputProcessingMode.Erasing && e.LeftButton == MouseButtonState.Pressed)
                {
                    EraseStrokesAtPoints(new StylusPointCollection { new StylusPoint(e.GetPosition(InkCanvas).X, e.GetPosition(InkCanvas).Y) });
                }
            }
        }

        private void UpdateBrushIndicatorStyle()
        {
            if (_currentMode == CustomInkInputProcessingMode.Erasing || _isStylusInverted)
            {
                EraserIndicator.Width = _eraserSize;
                EraserIndicator.Height = _eraserSize;
                EraserIndicator.Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
                EraserIndicator.Fill = new SolidColorBrush(Color.FromArgb(16, 0, 120, 212));
            }
            else if (_currentMode == CustomInkInputProcessingMode.Inking)
            {
                double size = _drawingAttributes.Width;
                EraserIndicator.Width = Math.Max(4, size);
                EraserIndicator.Height = Math.Max(4, size);
                
                Color c = _drawingAttributes.Color;
                EraserIndicator.Stroke = new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B));
                EraserIndicator.Fill = _drawingAttributes.IsHighlighter ? new SolidColorBrush(Color.FromArgb(50, c.R, c.G, c.B)) : Brushes.Transparent;
            }
        }

        private void UpdateEraserIndicatorPosition(Point point)
        {
            double w = EraserIndicator.Width;
            double h = EraserIndicator.Height;
            Canvas.SetLeft(EraserIndicator, point.X - w / 2);
            Canvas.SetTop(EraserIndicator, point.Y - h / 2);
            EraserIndicator.Visibility = Visibility.Visible;
        }

        private void InkCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isErasing = false;
        }

        private void EraseStrokesAtPoints(StylusPointCollection points)
        {
            if (points == null || points.Count == 0 || InkCanvas.Strokes.Count == 0)
                return;

            var eraserRects = CreateEraserRects(points);
            if (eraserRects.Count == 0)
                return;

            var candidateBounds = eraserRects[0];
            for (int i = 1; i < eraserRects.Count; i++)
                candidateBounds.Union(eraserRects[i]);

            var candidateStrokes = InkCanvas.Strokes
                .Cast<Stroke>()
                .Where(stroke => stroke.GetBounds().IntersectsWith(candidateBounds))
                .ToList();

            if (candidateStrokes.Count == 0)
                return;

            bool mutated = false;
            foreach (var stroke in candidateStrokes)
            {
                if (!stroke.StylusPoints.Any(sp => PointHitsEraser(new Point(sp.X, sp.Y), eraserRects)))
                    continue;

                var clippedStrokes = ClipStrokeByErasers(stroke, eraserRects);
                InkCanvas.Strokes.Remove(stroke);
                foreach (var newStroke in clippedStrokes)
                    InkCanvas.Strokes.Add(newStroke);

                mutated = true;
            }

            if (mutated)
                InkMutated?.Invoke(this, EventArgs.Empty);
        }

        private List<Rect> CreateEraserRects(StylusPointCollection points)
        {
            var eraserRects = new List<Rect>(points.Count);
            foreach (var pt in points)
            {
                eraserRects.Add(new Rect(
                    pt.X - _eraserSize / 2,
                    pt.Y - _eraserSize / 2,
                    _eraserSize,
                    _eraserSize));
            }

            return eraserRects;
        }

        private static bool PointHitsEraser(Point point, IReadOnlyList<Rect> eraserRects)
        {
            for (int i = 0; i < eraserRects.Count; i++)
            {
                if (eraserRects[i].Contains(point))
                    return true;
            }

            return false;
        }

        private List<Stroke> ClipStrokeByErasers(Stroke stroke, IReadOnlyList<Rect> eraserRects)
        {
            var result = new List<Stroke>();
            var stylusPoints = stroke.StylusPoints;
            var currentSegment = new StylusPointCollection();

            for (int i = 0; i < stylusPoints.Count; i++)
            {
                var pt = stylusPoints[i];
                var point = new Point(pt.X, pt.Y);
                bool inEraser = PointHitsEraser(point, eraserRects);

                if (!inEraser)
                {
                    currentSegment.Add(pt);
                }
                else if (currentSegment.Count > 1)
                {
                    var newStroke = new Stroke(currentSegment.Clone())
                    {
                        DrawingAttributes = stroke.DrawingAttributes.Clone()
                    };
                    result.Add(newStroke);
                    currentSegment.Clear();
                }
                else
                {
                    currentSegment.Clear();
                }
            }

            if (currentSegment.Count > 1)
            {
                var newStroke = new Stroke(currentSegment.Clone())
                {
                    DrawingAttributes = stroke.DrawingAttributes.Clone()
                };
                result.Add(newStroke);
            }

            return result;
        }

        private static void OnPageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PdfPageControl)d;
            control.PdfImage.Source = (BitmapSource)e.NewValue;
        }

        public void SetMode(bool isTextMode)
        {
            // Text annotations should only be directly interactive while the text
            // tool is active. In every other mode, let the input fall through to
            // the drawing/selection layers underneath.
            TextOverlayCanvas.IsHitTestVisible = isTextMode;
            TextOverlayCanvas.Background = isTextMode ? Brushes.Transparent : null;
        }

        public void SetPdfTextSelectionEnabled(bool enabled)
        {
            _isPdfTextSelectionEnabled = enabled;
            PdfTextSelectionCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            PdfTextSelectionCanvas.IsHitTestVisible = enabled;

            if (!enabled)
            {
                if (PdfTextSelectionCanvas.IsMouseCaptured)
                    PdfTextSelectionCanvas.ReleaseMouseCapture();
                if (PdfTextSelectionCanvas.IsStylusCaptured)
                    PdfTextSelectionCanvas.ReleaseStylusCapture();
                ClearPdfTextSelection();
                return;
            }

            // When PDF text selection is enabled, we need InkCanvas to NOT intercept events
            InkCanvas.IsHitTestVisible = false;
            Cursor = Cursors.IBeam;
        }

        public void SetPdfTextSelectionRects(IEnumerable<Rect> rects)
        {
            PdfTextSelectionCanvas.Children.Clear();
            if (rects == null)
                return;

            foreach (var rect in rects)
            {
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                    continue;

                var highlight = new System.Windows.Shapes.Rectangle
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 0, 120, 212)),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(highlight, rect.X);
                Canvas.SetTop(highlight, rect.Y);
                PdfTextSelectionCanvas.Children.Add(highlight);
            }
        }

        public void ClearPdfTextSelection()
        {
            PdfTextSelectionCanvas.Children.Clear();
        }

        public DrawingAttributes CopyDefaultDrawingAttributes()
        {
            return _drawingAttributes.Clone();
        }

        public void SetInkAttributes(DrawingAttributes attributes)
        {
            _drawingAttributes = attributes.Clone();
            _drawingAttributes.FitToCurve = true;
            // Always honour digitiser pressure so Surface Pen, Wacom, etc.
            // produce natural width variation.
            _drawingAttributes.IgnorePressure = false;
            InkCanvas.DefaultDrawingAttributes = _drawingAttributes;
        }

        public void SetInputMode(CustomInkInputProcessingMode mode)
        {
            _currentMode = mode;
            switch (mode)
            {
                case CustomInkInputProcessingMode.Inking:
                    if (!_isPdfTextSelectionEnabled)
                        InkCanvas.IsHitTestVisible = true;
                    InkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    // Keep indicator visible since we're updating it on mouse/stylus move now
                    InkCanvas.Cursor = Cursors.Cross;
                    Cursor = Cursors.Arrow;
                    break;
                case CustomInkInputProcessingMode.Erasing:
                    if (!_isPdfTextSelectionEnabled)
                        InkCanvas.IsHitTestVisible = true;
                    InkCanvas.EditingMode = InkCanvasEditingMode.None;
                    InkCanvas.Cursor = Cursors.None;
                    break;
                case CustomInkInputProcessingMode.None:
                    if (!_isPdfTextSelectionEnabled)
                        InkCanvas.IsHitTestVisible = false; // Allow events to pass through for scrolling
                    else
                        InkCanvas.IsHitTestVisible = false; // Keep it false for text selection
                    InkCanvas.EditingMode = InkCanvasEditingMode.None;
                    EraserIndicator.Visibility = Visibility.Collapsed;
                    InkCanvas.Cursor = Cursors.Arrow;
                    Cursor = Cursors.Arrow;
                    break;
            }
            ModeChanged?.Invoke(this, mode);
        }

        public void SetEraserSize(double size)
        {
            _eraserSize = size;
        }

        public StrokeCollection GetStrokes()
        {
            return InkCanvas.Strokes;
        }

        public void ClearInk()
        {
            InkCanvas.Strokes.Clear();
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        public List<StrokeAnnotation> GetStrokeData()
        {
            var list = new List<StrokeAnnotation>();
            foreach (var stroke in InkCanvas.Strokes)
            {
                var attrs = stroke.DrawingAttributes;
                var color = attrs.Color;
                var sa = new StrokeAnnotation
                {
                    R = color.R,
                    G = color.G,
                    B = color.B,
                    A = color.A,
                    Size = attrs.Width,
                    IsHighlighter = attrs.IsHighlighter
                };
                foreach (var pt in stroke.StylusPoints)
                {
                    sa.Points.Add(new[] { pt.X, pt.Y });
                }
                list.Add(sa);
            }
            return list;
        }

        public void AddStroke(StrokeAnnotation sa)
        {
            if (sa.Points == null || sa.Points.Count == 0) return;

            var color = Color.FromArgb(sa.A, sa.R, sa.G, sa.B);
            var attrs = new DrawingAttributes
            {
                Color = color,
                Width = sa.Size > 0 ? sa.Size : 2.0,
                Height = sa.Size > 0 ? sa.Size : 2.0,
                IsHighlighter = sa.IsHighlighter,
                FitToCurve = true
            };

            var stylusPoints = new StylusPointCollection();
            foreach (var pt in sa.Points)
            {
                if (pt == null || pt.Length < 2) continue;
                stylusPoints.Add(new StylusPoint(pt[0], pt[1]));
            }

            if (stylusPoints.Count > 0)
            {
                if (stylusPoints.Count == 1)
                {
                    stylusPoints.Add(new StylusPoint(stylusPoints[0].X + 0.1, stylusPoints[0].Y));
                }

                var stroke = new Stroke(stylusPoints);
                stroke.DrawingAttributes = attrs;
                InkCanvas.Strokes.Add(stroke);
            }
        }

        public void ClearStrokes()
        {
            InkCanvas.Strokes.Clear();
        }

        public void RemoveStrokeQuiet(Stroke stroke)
        {
            InkCanvas.Strokes.Remove(stroke);
        }

        public void AddStrokeQuiet(Stroke stroke)
        {
            InkCanvas.Strokes.Add(stroke);
        }

        public void SetSelectionMode(bool enabled)
        {
            _isSelectionMode = enabled;
            SelectionOverlayCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            SelectionOverlayCanvas.IsHitTestVisible = enabled;

            if (!enabled)
            {
                ClearSelection();
                if (SelectionOverlayCanvas.IsMouseCaptured)
                    SelectionOverlayCanvas.ReleaseMouseCapture();
                if (SelectionOverlayCanvas.IsStylusCaptured)
                    SelectionOverlayCanvas.ReleaseStylusCapture();
            }
            else
            {
                InkCanvas.IsHitTestVisible = false;
                Cursor = Cursors.Cross;
            }
        }

        public void SetSelectionFilter(SelectionFilter filter)
        {
            _selectionFilter = filter;
        }

        public void SetSelectionShape(SelectionShape shape)
        {
            _selectionShape = shape;
        }

        public void ClearSelection()
        {
            _selectedStrokes.Clear();
            _selectedTextContainers.Clear();
            _isSelecting = false;
            _isDraggingSelection = false;
            _isResizingSelection = false;
            _lastResizeScale = 1.0;
            _totalDragDeltaX = 0;
            _totalDragDeltaY = 0;
            _freeSelectionPath = null;
            _freeSelectionPoints = null;
            SelectionOverlayCanvas.Children.Clear();
            _selectionRect = null;
            SelectionChanged?.Invoke(this, new AnnotationSelectionChangedEventArgs(false, Rect.Empty));
        }

        public void MoveSelection(double deltaX, double deltaY)
        {
            if (_selectedStrokes.Count == 0 && _selectedTextContainers.Count == 0)
                return;

            MoveItemsDirectly(_selectedStrokes, _selectedTextContainers, deltaX, deltaY);
        }

        public void MoveItemsDirectly(List<Stroke> strokes, List<Grid> containers, double deltaX, double deltaY)
        {
            if (strokes.Count == 0 && containers.Count == 0)
                return;

            foreach (var stroke in strokes)
            {
                var newPoints = new StylusPointCollection();
                foreach (var pt in stroke.StylusPoints)
                {
                    newPoints.Add(new StylusPoint(pt.X + deltaX, pt.Y + deltaY));
                }
                stroke.StylusPoints = newPoints;
            }

            foreach (var container in containers)
            {
                var left = Canvas.GetLeft(container);
                var top = Canvas.GetTop(container);
                Canvas.SetLeft(container, left + deltaX);
                Canvas.SetTop(container, top + deltaY);
            }

            UpdateSelectionVisuals();
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        public void ScaleSelection(double scaleFactor, Point center)
        {
            if (_selectedStrokes.Count == 0 && _selectedTextContainers.Count == 0)
                return;

            ScaleItemsDirectly(_selectedStrokes, _selectedTextContainers, scaleFactor, center);
        }

        public void ScaleItemsDirectly(List<Stroke> strokes, List<Grid> containers, double scaleFactor, Point center)
        {
            if (strokes.Count == 0 && containers.Count == 0)
                return;

            foreach (var stroke in strokes)
            {
                var newPoints = new StylusPointCollection();
                foreach (var pt in stroke.StylusPoints)
                {
                    var newX = center.X + (pt.X - center.X) * scaleFactor;
                    var newY = center.Y + (pt.Y - center.Y) * scaleFactor;
                    newPoints.Add(new StylusPoint(newX, newY));
                }
                stroke.StylusPoints = newPoints;

                stroke.DrawingAttributes.Width *= scaleFactor;
                stroke.DrawingAttributes.Height *= scaleFactor;
            }

            foreach (var container in containers)
            {
                var left = Canvas.GetLeft(container);
                var top = Canvas.GetTop(container);
                var newLeft = center.X + (left - center.X) * scaleFactor;
                var newTop = center.Y + (top - center.Y) * scaleFactor;
                Canvas.SetLeft(container, newLeft);
                Canvas.SetTop(container, newTop);

                var tb = container.Children.OfType<TextBox>().FirstOrDefault();
                if (tb != null)
                {
                    tb.FontSize *= scaleFactor;
                }
            }

            UpdateSelectionVisuals();
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        public Rect GetSelectionBounds()
        {
            if (_selectedStrokes.Count == 0 && _selectedTextContainers.Count == 0)
                return Rect.Empty;

            var bounds = Rect.Empty;

            foreach (var stroke in _selectedStrokes)
            {
                var strokeBounds = stroke.GetBounds();
                if (bounds.IsEmpty)
                    bounds = strokeBounds;
                else
                    bounds.Union(strokeBounds);
            }

            foreach (var container in _selectedTextContainers)
            {
                var left = Canvas.GetLeft(container);
                var top = Canvas.GetTop(container);
                var rect = new Rect(left, top, container.ActualWidth, container.ActualHeight);
                if (bounds.IsEmpty)
                    bounds = rect;
                else
                    bounds.Union(rect);
            }

            return bounds;
        }

        public bool HasSelection => _selectedStrokes.Count > 0 || _selectedTextContainers.Count > 0;

        public List<Stroke> SelectedStrokes => _selectedStrokes;
        public List<Grid> SelectedTextContainers => _selectedTextContainers;

        private void UpdateSelectionVisuals()
        {
            var bounds = GetSelectionBounds();
            if (bounds.IsEmpty)
                return;

            SelectionOverlayCanvas.Children.Clear();
            var accentBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));

            var selectionBorder = new System.Windows.Shapes.Rectangle
            {
                Width = bounds.Width + 8,
                Height = bounds.Height + 8,
                Stroke = accentBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(18, 37, 99, 235)),
                Cursor = Cursors.SizeAll
            };
            Canvas.SetLeft(selectionBorder, bounds.Left - 4);
            Canvas.SetTop(selectionBorder, bounds.Top - 4);
            SelectionOverlayCanvas.Children.Add(selectionBorder);

            var handles = new[] {
                new Point(bounds.Left - 4, bounds.Top - 4),    // 0: TL
                new Point(bounds.Right + 4, bounds.Top - 4),   // 1: TR
                new Point(bounds.Left - 4, bounds.Bottom + 4), // 2: BL
                new Point(bounds.Right + 4, bounds.Bottom + 4) // 3: BR
            };

            var handleCursors = new[] {
                Cursors.SizeNWSE,  // TL
                Cursors.SizeNESW,  // TR
                Cursors.SizeNESW,  // BL
                Cursors.SizeNWSE,  // BR
            };

            for (int i = 0; i < handles.Length; i++)
            {
                var handlePos = handles[i];
                var handle = new System.Windows.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = Brushes.White,
                    Stroke = accentBrush,
                    StrokeThickness = 1.5,
                    Cursor = handleCursors[i],
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 6,
                        ShadowDepth = 0,
                        Opacity = 0.12,
                        Color = Colors.Black
                    }
                };
                Canvas.SetLeft(handle, handlePos.X - 6);
                Canvas.SetTop(handle, handlePos.Y - 6);
                SelectionOverlayCanvas.Children.Add(handle);
            }

            SelectionChanged?.Invoke(this, new AnnotationSelectionChangedEventArgs(true, bounds));
        }

        private static bool IsDescendantOf(DependencyObject descendant, DependencyObject ancestor)
        {
            var current = descendant;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;

                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }

            return false;
        }

        private static Point GetOppositeCorner(Rect bounds, int handleIndex)
        {
            return handleIndex switch
            {
                0 => new Point(bounds.Right, bounds.Bottom),  // TL → BR
                1 => new Point(bounds.Left, bounds.Bottom),   // TR → BL
                2 => new Point(bounds.Right, bounds.Top),     // BL → TR
                3 => new Point(bounds.Left, bounds.Top),      // BR → TL
                _ => new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
            };
        }

        private static double PointDistance(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Cursor GetResizeCursor(int handleIndex)
        {
            return handleIndex switch
            {
                0 => Cursors.SizeNWSE,  // TL
                1 => Cursors.SizeNESW,  // TR
                2 => Cursors.SizeNESW,  // BL
                3 => Cursors.SizeNWSE,  // BR
                _ => Cursors.SizeAll
            };
        }

        private void SelectionOverlayCanvas_MouseLeftButtonDownCore(Point point)
        {
            if (!_isSelectionMode) return;

            if (HasSelection)
            {
                var bounds = GetSelectionBounds();

                // Check corner handles first (resize)
                var cornerHandles = new[] {
                    new Point(bounds.Left - 4, bounds.Top - 4),    // 0: TL
                    new Point(bounds.Right + 4, bounds.Top - 4),   // 1: TR
                    new Point(bounds.Left - 4, bounds.Bottom + 4), // 2: BL
                    new Point(bounds.Right + 4, bounds.Bottom + 4) // 3: BR
                };
                for (int i = 0; i < cornerHandles.Length; i++)
                {
                    var hitRect = new Rect(cornerHandles[i].X - 8, cornerHandles[i].Y - 8, 16, 16);
                    if (hitRect.Contains(point))
                    {
                        _isResizingSelection = true;
                        _resizeHandleIndex = i;
                        _resizeAnchorPoint = GetOppositeCorner(bounds, i);
                        _resizeStartHandleDist = PointDistance(cornerHandles[i], _resizeAnchorPoint);
                        if (_resizeStartHandleDist < 1.0) _resizeStartHandleDist = 1.0;
                        _lastResizeScale = 1.0;
                        SelectionOverlayCanvas.CaptureMouse();
                        return;
                    }
                }

                var inflatedBounds = bounds;
                inflatedBounds.Inflate(8, 8);

                if (inflatedBounds.Contains(point))
                {
                    _isDraggingSelection = true;
                    _dragStartPoint = point;
                    _totalDragDeltaX = 0;
                    _totalDragDeltaY = 0;
                    SelectionOverlayCanvas.CaptureMouse();
                    return;
                }
            }

            ClearSelection();
            _isSelecting = true;
            _selectionStartPoint = point;
            SelectionOverlayCanvas.CaptureMouse();

            if (_selectionShape == SelectionShape.FreeForm)
            {
                _freeSelectionPoints = new System.Windows.Media.PointCollection { point };
                _freeSelectionPath = new System.Windows.Shapes.Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Points = _freeSelectionPoints
                };
                SelectionOverlayCanvas.Children.Add(_freeSelectionPath);
            }
            else
            {
                _selectionRect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 212))
                };
                Canvas.SetLeft(_selectionRect, point.X);
                Canvas.SetTop(_selectionRect, point.Y);
                SelectionOverlayCanvas.Children.Add(_selectionRect);
            }
        }

        private void SelectionOverlayCanvas_MouseMoveCore(Point point)
        {
            if (!_isSelectionMode) return;

            if (_isResizingSelection)
            {
                var dist = PointDistance(_resizeAnchorPoint, point);
                if (dist < 1.0) dist = 1.0;
                var totalScale = dist / _resizeStartHandleDist;
                if (totalScale < 0.01) totalScale = 0.01;
                var deltaScale = totalScale / _lastResizeScale;
                _lastResizeScale = totalScale;
                ScaleSelection(deltaScale, _resizeAnchorPoint);
                Cursor = GetResizeCursor(_resizeHandleIndex);
            }
            else if (_isDraggingSelection)
            {
                var deltaX = point.X - _dragStartPoint.X;
                var deltaY = point.Y - _dragStartPoint.Y;
                _totalDragDeltaX += deltaX;
                _totalDragDeltaY += deltaY;
                MoveSelection(deltaX, deltaY);
                _dragStartPoint = point;
                Cursor = Cursors.SizeAll;
            }
            else if (_isSelecting)
            {
                if (_selectionShape == SelectionShape.FreeForm && _freeSelectionPath != null)
                {
                    _freeSelectionPoints.Add(point);
                }
                else if (_selectionRect != null)
                {
                    var x = Math.Min(_selectionStartPoint.X, point.X);
                    var y = Math.Min(_selectionStartPoint.Y, point.Y);
                    var width = Math.Abs(point.X - _selectionStartPoint.X);
                    var height = Math.Abs(point.Y - _selectionStartPoint.Y);

                    Canvas.SetLeft(_selectionRect, x);
                    Canvas.SetTop(_selectionRect, y);
                    _selectionRect.Width = width;
                    _selectionRect.Height = height;
                }
            }
            else if (HasSelection)
            {
                var bounds = GetSelectionBounds();

                // Show resize cursor when hovering over corner handles
                var cornerHandles = new[] {
                    new Point(bounds.Left - 4, bounds.Top - 4),
                    new Point(bounds.Right + 4, bounds.Top - 4),
                    new Point(bounds.Left - 4, bounds.Bottom + 4),
                    new Point(bounds.Right + 4, bounds.Bottom + 4)
                };
                for (int i = 0; i < cornerHandles.Length; i++)
                {
                    var hitRect = new Rect(cornerHandles[i].X - 8, cornerHandles[i].Y - 8, 16, 16);
                    if (hitRect.Contains(point))
                    {
                        Cursor = GetResizeCursor(i);
                        return;
                    }
                }

                var inflatedBounds = bounds;
                inflatedBounds.Inflate(8, 8);
                Cursor = inflatedBounds.Contains(point) ? Cursors.SizeAll : Cursors.Cross;
            }
            else
            {
                Cursor = Cursors.Cross;
            }
        }

        private void SelectionOverlayCanvas_MouseLeftButtonUpCore()
        {
            if (!_isSelectionMode) return;

            if (_isResizingSelection)
            {
                _isResizingSelection = false;
                if (SelectionOverlayCanvas.IsMouseCaptured)
                    SelectionOverlayCanvas.ReleaseMouseCapture();

                if (Math.Abs(_lastResizeScale - 1.0) > 0.001)
                    SelectionResizeCompleted?.Invoke(this, new SelectionResizeCompletedEventArgs(
                        _lastResizeScale, _resizeAnchorPoint,
                        new List<Stroke>(_selectedStrokes),
                        new List<Grid>(_selectedTextContainers)));
                _lastResizeScale = 1.0;
            }
            else if (_isDraggingSelection)
            {
                _isDraggingSelection = false;
                if (SelectionOverlayCanvas.IsMouseCaptured)
                    SelectionOverlayCanvas.ReleaseMouseCapture();

                if (Math.Abs(_totalDragDeltaX) > 0.5 || Math.Abs(_totalDragDeltaY) > 0.5)
                    SelectionMoveCompleted?.Invoke(this, new SelectionMoveCompletedEventArgs(
                        _totalDragDeltaX, _totalDragDeltaY,
                        new List<Stroke>(_selectedStrokes),
                        new List<Grid>(_selectedTextContainers)));
                _totalDragDeltaX = 0;
                _totalDragDeltaY = 0;
            }
            else if (_isSelecting)
            {
                _isSelecting = false;
                if (SelectionOverlayCanvas.IsMouseCaptured)
                    SelectionOverlayCanvas.ReleaseMouseCapture();

                _selectedStrokes.Clear();
                _selectedTextContainers.Clear();

                if (_selectionShape == SelectionShape.FreeForm && _freeSelectionPoints?.Count > 2)
                {
                    var polygon = _freeSelectionPoints;

                    if (_selectionFilter != SelectionFilter.TextOnly)
                    {
                        foreach (var stroke in InkCanvas.Strokes)
                        {
                            if (IsRectInsidePolygon(polygon, stroke.GetBounds()))
                                _selectedStrokes.Add(stroke);
                        }
                    }

                    if (_selectionFilter != SelectionFilter.DrawingsOnly)
                    {
                        foreach (var element in TextOverlayCanvas.Children)
                        {
                            if (element is Grid container)
                            {
                                var containerRect = new Rect(Canvas.GetLeft(container), Canvas.GetTop(container), container.ActualWidth, container.ActualHeight);
                                if (IsRectInsidePolygon(polygon, containerRect))
                                    _selectedTextContainers.Add(container);
                            }
                        }
                    }

                    _freeSelectionPath = null;
                    _freeSelectionPoints = null;
                }
                else if (_selectionRect != null)
                {
                    var selX = Canvas.GetLeft(_selectionRect);
                    var selY = Canvas.GetTop(_selectionRect);
                    var selRect = new Rect(selX, selY, _selectionRect.Width, _selectionRect.Height);

                    if (_selectionFilter != SelectionFilter.TextOnly)
                    {
                        foreach (var stroke in InkCanvas.Strokes)
                        {
                            if (selRect.Contains(stroke.GetBounds()))
                                _selectedStrokes.Add(stroke);
                        }
                    }

                    if (_selectionFilter != SelectionFilter.DrawingsOnly)
                    {
                        foreach (var element in TextOverlayCanvas.Children)
                        {
                            if (element is Grid container)
                            {
                                var containerRect = new Rect(Canvas.GetLeft(container), Canvas.GetTop(container), container.ActualWidth, container.ActualHeight);
                                if (selRect.Contains(containerRect))
                                    _selectedTextContainers.Add(container);
                            }
                        }
                    }

                    _selectionRect = null;
                }

                // Auto-clear visuals if nothing was caught
                if (_selectedStrokes.Count == 0 && _selectedTextContainers.Count == 0)
                    SelectionOverlayCanvas.Children.Clear();
                else
                    UpdateSelectionVisuals();
            }
        }

        private static bool IsPointInPolygon(System.Windows.Media.PointCollection polygon, Point p)
        {
            bool inside = false;
            int j = polygon.Count - 1;
            for (int i = 0; i < polygon.Count; i++)
            {
                if (((polygon[i].Y > p.Y) != (polygon[j].Y > p.Y)) &&
                    (p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X))
                    inside = !inside;
                j = i;
            }
            return inside;
        }

        private static bool IsRectInsidePolygon(System.Windows.Media.PointCollection polygon, Rect rect)
        {
            return IsPointInPolygon(polygon, rect.TopLeft) &&
                   IsPointInPolygon(polygon, rect.TopRight) &&
                   IsPointInPolygon(polygon, rect.BottomLeft) &&
                   IsPointInPolygon(polygon, rect.BottomRight);
        }

        private void SelectionOverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isSelectionMode) return;
            var point = e.GetPosition(SelectionOverlayCanvas);
            SelectionOverlayCanvas_MouseLeftButtonDownCore(point);
            e.Handled = true;
        }

        private void SelectionOverlayCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isSelectionMode) return;
            var point = e.GetPosition(SelectionOverlayCanvas);
            SelectionOverlayCanvas_MouseMoveCore(point);
            e.Handled = true;
        }

        private void SelectionOverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSelectionMode) return;
            SelectionOverlayCanvas_MouseLeftButtonUpCore();
            e.Handled = true;
        }

        private void SelectionOverlayCanvas_StylusDown(object sender, StylusDownEventArgs e)
        {
            if (!_isSelectionMode) return;
            var point = e.GetPosition(SelectionOverlayCanvas);
            SelectionOverlayCanvas_MouseLeftButtonDownCore(point);
            e.Handled = true;
        }

        private void SelectionOverlayCanvas_StylusMove(object sender, StylusEventArgs e)
        {
            if (!_isSelectionMode) return;
            var point = e.GetPosition(SelectionOverlayCanvas);
            SelectionOverlayCanvas_MouseMoveCore(point);
            e.Handled = true;
        }

        private void SelectionOverlayCanvas_StylusUp(object sender, StylusEventArgs e)
        {
            if (!_isSelectionMode) return;
            SelectionOverlayCanvas_MouseLeftButtonUpCore();
            e.Handled = true;
        }

        private readonly List<HighlightAnnotation> _highlights = new();

        public IReadOnlyList<HighlightAnnotation> GetHighlights() => _highlights;

        public void AddHighlightAnnotation(IReadOnlyList<Rect> rects, Color color)
        {
            var highlight = new HighlightAnnotation
            {
                R = color.R,
                G = color.G,
                B = color.B,
                A = 120 // Semi-transparent overlay
            };

            foreach (var r in rects)
            {
                highlight.Rects.Add(new double[] { r.X, r.Y, r.Width, r.Height });
            }

            _highlights.Add(highlight);
            RenderHighlightVisual(highlight);
        }

        public void AddHighlight(HighlightAnnotation highlight)
        {
            _highlights.Add(highlight);
            RenderHighlightVisual(highlight);
        }

        private void RenderHighlightVisual(HighlightAnnotation highlight)
        {
            var color = Color.FromArgb(highlight.A, highlight.R, highlight.G, highlight.B);
            var brush = new SolidColorBrush(color);

            foreach (var rectInfo in highlight.Rects)
            {
                if (rectInfo.Length >= 4)
                {
                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        Width = rectInfo[2],
                        Height = rectInfo[3],
                        Fill = brush,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(rect, rectInfo[0]);
                    Canvas.SetTop(rect, rectInfo[1]);
                    HighlightsCanvas.Children.Add(rect);
                }
            }
        }
    }
}

