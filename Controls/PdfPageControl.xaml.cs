using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Input.Inking;

namespace WindowsNotesApp.Controls
{
    public sealed partial class PdfPageControl : UserControl
    {
        public static readonly DependencyProperty PageSourceProperty =
            DependencyProperty.Register(nameof(PageSource), typeof(BitmapImage), typeof(PdfPageControl), new PropertyMetadata(null, OnPageSourceChanged));

        public BitmapImage PageSource
        {
            get => (BitmapImage)GetValue(PageSourceProperty);
            set => SetValue(PageSourceProperty, value);
        }

        public int PageIndex { get; set; }

        public Canvas TextOverlay => TextOverlayCanvas;

        public event EventHandler<PointerRoutedEventArgs> TextOverlayPointerPressed;
        public event EventHandler<PointerRoutedEventArgs> BackgroundPointerPressed;
        public event EventHandler InkMutated;

        // Ink state
        private InkDrawingAttributes _drawingAttributes;
        private InkInputProcessingMode _inputMode = InkInputProcessingMode.Inking;
        private bool _isDrawing = false;
        private Polyline _currentPolyline;
        private List<Point> _currentPoints;
        private List<InkStroke> _strokes = new List<InkStroke>();
        // Map Polyline to InkStroke for erasure
        private Dictionary<Polyline, InkStroke> _strokeMap = new Dictionary<Polyline, InkStroke>();
        private bool _pointerHandlersAttached;
        private double _eraserSize = 20.0;
        private Canvas _cursorOverlayCanvas;
        private Ellipse _eraserCursor;
        private bool _systemCursorHidden;

        public PdfPageControl()
        {
            this.InitializeComponent();

            // Default attributes
            _drawingAttributes = new InkDrawingAttributes
            {
                Color = Colors.Black,
                Size = new Size(2, 2),
                FitToCurve = true
            };

            // By default, text overlay allows hit test only if explicitly enabled
            TextOverlayCanvas.IsHitTestVisible = false;

            _cursorOverlayCanvas = new Canvas
            {
                Background = new SolidColorBrush(Colors.Transparent),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            var inkIndex = PageGrid.Children.IndexOf(InkCanvas);
            if (inkIndex >= 0)
            {
                PageGrid.Children.Insert(inkIndex + 1, _cursorOverlayCanvas);
            }
            else
            {
                PageGrid.Children.Add(_cursorOverlayCanvas);
            }

            _eraserCursor = new Ellipse
            {
                Width = _eraserSize,
                Height = _eraserSize,
                StrokeThickness = 1,
                Stroke = new SolidColorBrush(Colors.Black),
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _cursorOverlayCanvas.Children.Add(_eraserCursor);
            Canvas.SetZIndex(_eraserCursor, int.MaxValue);
            _cursorOverlayCanvas.Visibility = Visibility.Collapsed;

            Loaded += PdfPageControl_Loaded;
            Unloaded += PdfPageControl_Unloaded;
        }

        private void PdfPageControl_Loaded(object sender, RoutedEventArgs e)
        {
            AttachPointerHandlers();
        }

        private void PdfPageControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachPointerHandlers();
        }

        private void AttachPointerHandlers()
        {
            if (_pointerHandlersAttached)
            {
                return;
            }

            TextOverlayCanvas.PointerPressed += TextOverlayCanvas_PointerPressed;

            InkCanvas.PointerPressed += InkCanvas_PointerPressed;
            InkCanvas.PointerMoved += InkCanvas_PointerMoved;
            InkCanvas.PointerReleased += InkCanvas_PointerReleased;
            InkCanvas.PointerCanceled += InkCanvas_PointerReleased;
            InkCanvas.PointerExited += InkCanvas_PointerReleased;
            InkCanvas.PointerEntered += InkCanvas_PointerEntered;
            InkCanvas.PointerExited += InkCanvas_PointerExitedForCursor;

            _pointerHandlersAttached = true;
        }

        private void DetachPointerHandlers()
        {
            if (!_pointerHandlersAttached)
            {
                return;
            }

            TextOverlayCanvas.PointerPressed -= TextOverlayCanvas_PointerPressed;

            InkCanvas.PointerPressed -= InkCanvas_PointerPressed;
            InkCanvas.PointerMoved -= InkCanvas_PointerMoved;
            InkCanvas.PointerReleased -= InkCanvas_PointerReleased;
            InkCanvas.PointerCanceled -= InkCanvas_PointerReleased;
            InkCanvas.PointerExited -= InkCanvas_PointerReleased;
            InkCanvas.PointerEntered -= InkCanvas_PointerEntered;
            InkCanvas.PointerExited -= InkCanvas_PointerExitedForCursor;

            InkCanvas.ReleasePointerCaptures();
            _isDrawing = false;
            _currentPolyline = null;
            _currentPoints = null;
            ShowSystemCursor();

            _pointerHandlersAttached = false;
        }

        private void TextOverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            TextOverlayPointerPressed?.Invoke(this, e);
        }

        private static void OnPageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (PdfPageControl)d;
            control.PdfImage.Source = (BitmapImage)e.NewValue;
        }

        public void SetMode(bool isTextMode)
        {
            TextOverlayCanvas.IsHitTestVisible = isTextMode;
            InkCanvas.IsHitTestVisible = !isTextMode;
        }

        public InkDrawingAttributes CopyDefaultDrawingAttributes()
        {
            // Return a copy
            return new InkDrawingAttributes
            {
                Color = _drawingAttributes.Color,
                Size = _drawingAttributes.Size,
                FitToCurve = _drawingAttributes.FitToCurve,
                DrawAsHighlighter = _drawingAttributes.DrawAsHighlighter,
                PenTip = _drawingAttributes.PenTip,
                IgnorePressure = _drawingAttributes.IgnorePressure,
                PenTipTransform = _drawingAttributes.PenTipTransform
            };
        }

        public void SetInkAttributes(InkDrawingAttributes attributes)
        {
            _drawingAttributes = attributes;
            // Ensure FitToCurve is set if user didn't set it (though EditorPage sets it)
            // But EditorPage sets attributes that it got from CopyDefaultDrawingAttributes.
            // We set FitToCurve = true in CopyDefaultDrawingAttributes initially?
            // Or here.
            _drawingAttributes.FitToCurve = true; 
        }

        public void SetInputMode(InkInputProcessingMode mode)
        {
            if (_isDrawing && mode != InkInputProcessingMode.Inking)
            {
                InkCanvas.ReleasePointerCaptures();
                if (_currentPolyline != null)
                {
                    InkCanvas.Children.Remove(_currentPolyline);
                }
                _isDrawing = false;
                _currentPolyline = null;
                _currentPoints = null;
            }
            _inputMode = mode;
            ApplyEraserVisualState();
        }

        public void SetEraserSize(double size)
        {
            if (size <= 0)
            {
                return;
            }

            _eraserSize = size;
            if (_eraserCursor != null)
            {
                _eraserCursor.Width = _eraserSize;
                _eraserCursor.Height = _eraserSize;
            }
        }

        public IEnumerable<InkStroke> GetStrokes()
        {
            return _strokes.ToList();
        }

        public void ClearInk()
        {
            _strokes.Clear();
            _strokeMap.Clear();
            InkCanvas.Children.Clear();
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            BackgroundPointerPressed?.Invoke(this, e);

            if (_inputMode == InkInputProcessingMode.None)
            {
                return;
            }

            var point = e.GetCurrentPoint(InkCanvas);

            if (_inputMode == InkInputProcessingMode.Inking)
            {
                if (!point.Properties.IsLeftButtonPressed && point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
                {
                    return;
                }

                _isDrawing = true;
                InkCanvas.CapturePointer(e.Pointer);

                _currentPoints = new List<Point>();
                _currentPoints.Add(point.Position);

                _currentPolyline = new Polyline
                {
                    StrokeThickness = _drawingAttributes.Size.Width,
                    Stroke = new SolidColorBrush(_drawingAttributes.Color),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = _drawingAttributes.DrawAsHighlighter ? 0.5 : 1.0
                };

                _currentPolyline.Points.Add(point.Position);
                InkCanvas.Children.Add(_currentPolyline);
            }
            else if (_inputMode == InkInputProcessingMode.Erasing)
            {
                if (!point.Properties.IsLeftButtonPressed && point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen && !point.Properties.IsEraser)
                {
                    return;
                }
                UpdateEraserCursorPosition(point.Position);
                TryEraseAt(point.Position);
            }

            e.Handled = true;
        }

        private void InkCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(InkCanvas);

            if (_inputMode == InkInputProcessingMode.Erasing)
            {
                UpdateEraserCursorPosition(point.Position);
            }

            if (_isDrawing && _currentPolyline != null)
            {
                _currentPoints.Add(point.Position);
                _currentPolyline.Points.Add(point.Position);
            }
            else if (_inputMode == InkInputProcessingMode.Erasing && (point.Properties.IsLeftButtonPressed || point.Properties.IsEraser || (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen && point.IsInContact)))
            {
                TryEraseAt(point.Position);
            }
            else
            {
                return;
            }

            e.Handled = true;
        }

        private void InkCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawing || _currentPolyline == null)
            {
                return;
            }

            if (_isDrawing && _currentPolyline != null)
            {
                _isDrawing = false;
                InkCanvas.ReleasePointerCapture(e.Pointer);

                // Create InkStroke
                if (_currentPoints.Count > 1)
                {
                    var builder = new InkStrokeBuilder();
                    builder.SetDefaultDrawingAttributes(_drawingAttributes);
                    
                    // InkPoint requires Position and Pressure. We default pressure to 0.5
                    var inkPoints = _currentPoints.Select(p => new InkPoint(p, 0.5f)).ToList();
                    var stroke = builder.CreateStrokeFromInkPoints(inkPoints, Matrix3x2.Identity);
                    
                    _strokes.Add(stroke);
                    _strokeMap[_currentPolyline] = stroke;

                    // Optional: Replace Polyline with smoothed path if FitToCurve is true?
                    // For now, keep the raw polyline for performance, or replace it with points from stroke (which are smoothed).
                    if (_drawingAttributes.FitToCurve)
                    {
                        var smoothedPoints = stroke.GetInkPoints();
                        _currentPolyline.Points.Clear();
                        foreach (var ip in smoothedPoints)
                        {
                            _currentPolyline.Points.Add(ip.Position);
                        }
                    }

                    InkMutated?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    InkCanvas.Children.Remove(_currentPolyline);
                }

                _currentPolyline = null;
                _currentPoints = null;
            }
            
            e.Handled = true;
        }

        private void TryEraseAt(Point position)
        {
            double eraserRadius = _eraserSize / 2.0;
            double eraserRadiusSquared = eraserRadius * eraserRadius;
            var mutated = false;
            
            // Create a snapshot of keys to iterate safely while modifying the dictionary
            var strokesToCheck = _strokeMap.Keys.ToList();

            foreach (var polyline in strokesToCheck)
            {
                if (!_strokeMap.TryGetValue(polyline, out var stroke)) continue;

                var points = stroke.GetInkPoints();
                bool isHit = false;

                // 1. Check if the stroke is hit by the eraser
                foreach (var pt in points)
                {
                    if (DistanceSquared(pt.Position, position) < eraserRadiusSquared)
                    {
                        isHit = true;
                        break;
                    }
                }

                if (!isHit) continue;

                // 2. The stroke is hit. Split it into segments.
                var newSegments = new List<List<InkPoint>>();
                var currentSegment = new List<InkPoint>();

                foreach (var pt in points)
                {
                    if (DistanceSquared(pt.Position, position) < eraserRadiusSquared)
                    {
                        // Point is erased. Close current segment if it has points.
                        if (currentSegment.Count > 0)
                        {
                            newSegments.Add(currentSegment);
                            currentSegment = new List<InkPoint>();
                        }
                    }
                    else
                    {
                        // Point is safe. Add to current segment.
                        currentSegment.Add(pt);
                    }
                }

                // Add the final segment if it has points
                if (currentSegment.Count > 0)
                {
                    newSegments.Add(currentSegment);
                }

                // 3. Remove the original stroke
                _strokes.Remove(stroke);
                _strokeMap.Remove(polyline);
                InkCanvas.Children.Remove(polyline);
                mutated = true;

                // 4. Create new strokes from segments
                if (newSegments.Count > 0)
                {
                    var builder = new InkStrokeBuilder();
                    builder.SetDefaultDrawingAttributes(stroke.DrawingAttributes);

                    foreach (var segment in newSegments)
                    {
                        // InkStrokeBuilder requires at least one point
                        if (segment.Count == 0) continue;

                        var newStroke = builder.CreateStrokeFromInkPoints(segment, Matrix3x2.Identity);
                        _strokes.Add(newStroke);

                        // Create visual Polyline
                        var newPolyline = new Polyline
                        {
                            StrokeThickness = stroke.DrawingAttributes.Size.Width,
                            Stroke = new SolidColorBrush(stroke.DrawingAttributes.Color),
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            Opacity = stroke.DrawingAttributes.DrawAsHighlighter ? 0.5 : 1.0
                        };

                        var newStrokePoints = newStroke.GetInkPoints();
                        foreach (var p in newStrokePoints)
                        {
                            newPolyline.Points.Add(p.Position);
                        }

                        // Fix for single-point stroke visibility: duplicate the point to make it a visible dot
                        if (newStrokePoints.Count == 1)
                        {
                            newPolyline.Points.Add(newStrokePoints[0].Position);
                        }

                        _strokeMap[newPolyline] = newStroke;
                        InkCanvas.Children.Add(newPolyline);
                    }
                }
            }

            if (mutated)
            {
                InkMutated?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ApplyEraserVisualState()
        {
            if (_inputMode == InkInputProcessingMode.Erasing)
            {
                _cursorOverlayCanvas.Visibility = Visibility.Visible;
            }
            else
            {
                _cursorOverlayCanvas.Visibility = Visibility.Collapsed;
                HideEraserCursor();
                ShowSystemCursor();
            }
        }

        private void InkCanvas_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_inputMode != InkInputProcessingMode.Erasing)
            {
                return;
            }

            var point = e.GetCurrentPoint(InkCanvas);
            if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                HideSystemCursor();
            }
            UpdateEraserCursorPosition(point.Position);
        }

        private void InkCanvas_PointerExitedForCursor(object sender, PointerRoutedEventArgs e)
        {
            ShowSystemCursor();
            HideEraserCursor();
        }

        private void UpdateEraserCursorPosition(Point position)
        {
            if (_eraserCursor == null || _cursorOverlayCanvas.Visibility != Visibility.Visible)
            {
                return;
            }

            Canvas.SetLeft(_eraserCursor, position.X - (_eraserSize / 2.0));
            Canvas.SetTop(_eraserCursor, position.Y - (_eraserSize / 2.0));
            _eraserCursor.Visibility = Visibility.Visible;
        }

        private void HideEraserCursor()
        {
            if (_eraserCursor != null)
            {
                _eraserCursor.Visibility = Visibility.Collapsed;
            }
        }

        private void HideSystemCursor()
        {
            if (_systemCursorHidden)
            {
                return;
            }

            _systemCursorHidden = true;
            while (ShowCursor(false) >= 0) { }
        }

        private void ShowSystemCursor()
        {
            if (!_systemCursorHidden)
            {
                return;
            }

            _systemCursorHidden = false;
            while (ShowCursor(true) < 0) { }
        }

        [DllImport("user32.dll")]
        private static extern int ShowCursor(bool bShow);

        private double DistanceSquared(Point p1, Point p2)
        {
            double dx = p1.X - p2.X;
            double dy = p1.Y - p2.Y;
            return dx * dx + dy * dy;
        }
    }
}
