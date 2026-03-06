using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Ink;
using WindowsNotesApp.Models;
using WindowsNotesApp.Services;

namespace WindowsNotesApp.Controls
{
    public enum CustomInkInputProcessingMode { None, Inking, Erasing }

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
        public event EventHandler InkMutated;
        public event EventHandler<Stroke> StrokeCollectedUndoable;
        public event EventHandler<CustomInkInputProcessingMode> ModeChanged;

        private DrawingAttributes _drawingAttributes;
        private CustomInkInputProcessingMode _currentMode = CustomInkInputProcessingMode.None;
        private double _eraserSize = 20;
        private bool _isErasing;
        private StylusPointCollection _erasePoints;

        // Tracks whether the stylus is currently inverted (physical eraser end),
        // which corresponds to Windows Ink's IsEraser / PointerPointProperties.IsEraser.
        // When inverted, we override the current mode to erase regardless of the
        // selected tool â€?this is how standard Windows Ink pens work and is the
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

        public PdfPageControl()
        {
            InitializeComponent();

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
            InkCanvas.EditingMode = InkCanvasEditingMode.None;
            InkCanvas.EditingModeInverted = InkCanvasEditingMode.None; // Disable native inverted erasing so we can use custom logic
            InkCanvas.StrokeCollected += InkCanvas_StrokeCollected;
            InkCanvas.StrokeErasing += InkCanvas_StrokeErasing;
            InkCanvas.StrokeErased += InkCanvas_StrokeErased;

            TextOverlayCanvas.IsHitTestVisible = false;
            InkCanvas.IsHitTestVisible = true;

            Loaded += PdfPageControl_Loaded;
            Unloaded += PdfPageControl_Unloaded;
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

            // Fix for auto-scroll bug: Prevent ScrollViewer from scrolling when InkCanvas gets focus
            this.RequestBringIntoView += PdfPageControl_RequestBringIntoView;
        }

        private void PdfPageControl_Unloaded(object sender, RoutedEventArgs e)
        {
            TextOverlayCanvas.MouseDown -= TextOverlayCanvas_MouseDown;
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

            this.RequestBringIntoView -= PdfPageControl_RequestBringIntoView;
        }

        private void PdfPageControl_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

        private void InkCanvas_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_currentMode == CustomInkInputProcessingMode.Erasing || _isStylusInverted)
            {
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
                    $"[PdfPageControl] Stylus Inverted changed â†?{inverted} (device={e.StylusDevice?.Name})");

                if (inverted)
                {
                    // Show eraser indicator while hovering with inverted pen
                    InkCanvas.EditingMode = InkCanvasEditingMode.None; // suppress inking
                    EraserIndicator.Visibility = Visibility.Visible;
                    InkCanvas.Cursor = Cursors.None;
                }
                else if (_currentMode != CustomInkInputProcessingMode.Erasing)
                {
                    // Pen flipped back to normal â€?restore previous mode
                    EraserIndicator.Visibility = Visibility.Collapsed;
                    SetInputMode(_currentMode);
                }
            }

            if (inverted)
            {
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
                BackgroundPointerPressed?.Invoke(this, e);
            }
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

            // Ignore the tip button â€?it fires on every pen contact and is NOT a side button.
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
            if (_currentMode == CustomInkInputProcessingMode.Erasing)
            {
                var point = e.GetPosition(PageGrid);
                UpdateEraserIndicatorPosition(point);

                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    EraseStrokesAtPoints(new StylusPointCollection { new StylusPoint(e.GetPosition(InkCanvas).X, e.GetPosition(InkCanvas).Y) });
                }
            }
        }

        private void UpdateEraserIndicatorPosition(Point point)
        {
            EraserIndicator.Width = _eraserSize;
            EraserIndicator.Height = _eraserSize;
            Canvas.SetLeft(EraserIndicator, point.X - _eraserSize / 2);
            Canvas.SetTop(EraserIndicator, point.Y - _eraserSize / 2);
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
            TextOverlayCanvas.IsHitTestVisible = isTextMode;
            InkCanvas.IsHitTestVisible = !isTextMode;
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
                    InkCanvas.IsHitTestVisible = true;
                    InkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    EraserIndicator.Visibility = Visibility.Collapsed;
                    InkCanvas.Cursor = Cursors.Cross;
                    Cursor = Cursors.Arrow;
                    break;
                case CustomInkInputProcessingMode.Erasing:
                    InkCanvas.IsHitTestVisible = true;
                    InkCanvas.EditingMode = InkCanvasEditingMode.None;
                    InkCanvas.Cursor = Cursors.None;
                    break;
                case CustomInkInputProcessingMode.None:
                    InkCanvas.IsHitTestVisible = false; // Allow events to pass through for scrolling
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
    }
}

