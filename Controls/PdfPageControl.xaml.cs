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
using WindowsNotesApp.Models;

namespace WindowsNotesApp.Controls
{
    public enum CustomInkInputProcessingMode { None, Inking, Erasing }

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

            InkCanvas.InkPresenter.InputDeviceTypes = Microsoft.UI.Input.CoreInputDeviceTypes.Pen |
                                                      Microsoft.UI.Input.CoreInputDeviceTypes.Mouse |
                                                      Microsoft.UI.Input.CoreInputDeviceTypes.Touch;
            InkCanvas.InkPresenter.StrokesCollected += InkPresenter_StrokesCollected;
            InkCanvas.InkPresenter.StrokesErased += InkPresenter_StrokesErased;

            Loaded += PdfPageControl_Loaded;
            Unloaded += PdfPageControl_Unloaded;
        }

        private void InkPresenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        private void InkPresenter_StrokesErased(InkPresenter sender, InkStrokesErasedEventArgs args)
        {
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        private void PdfPageControl_Loaded(object sender, RoutedEventArgs e)
        {
            TextOverlayCanvas.PointerPressed += TextOverlayCanvas_PointerPressed;
            InkCanvas.PointerPressed += InkCanvas_PointerPressed;
        }

        private void PdfPageControl_Unloaded(object sender, RoutedEventArgs e)
        {
            TextOverlayCanvas.PointerPressed -= TextOverlayCanvas_PointerPressed;
            InkCanvas.PointerPressed -= InkCanvas_PointerPressed;
        }

        private void TextOverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            TextOverlayPointerPressed?.Invoke(this, e);
        }

        private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            BackgroundPointerPressed?.Invoke(this, e);
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
            _drawingAttributes.FitToCurve = true;
            InkCanvas.InkPresenter.UpdateDefaultDrawingAttributes(_drawingAttributes);
        }

        public void SetInputMode(CustomInkInputProcessingMode mode)
        {
            switch (mode)
            {
                case CustomInkInputProcessingMode.Inking:
                    InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
                    InkCanvas.InkPresenter.InputProcessingConfiguration.RightDragAction = InkInputRightDragAction.LeaveUnmodified;
                    break;
                case CustomInkInputProcessingMode.Erasing:
                    InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Erasing;
                    InkCanvas.InkPresenter.InputProcessingConfiguration.RightDragAction = InkInputRightDragAction.LeaveUnmodified;
                    break;
                case CustomInkInputProcessingMode.None:
                    InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.None;
                    InkCanvas.InkPresenter.InputProcessingConfiguration.RightDragAction = InkInputRightDragAction.LeaveUnmodified;
                    break;
            }
        }

        public void SetEraserSize(double size)
        {
            // Native InkCanvas handles eraser size automatically with the stylus tail or system defaults.
        }

        public IEnumerable<InkStroke> GetStrokes()
        {
            return InkCanvas.InkPresenter.StrokeContainer.GetStrokes();
        }

        public void ClearInk()
        {
            InkCanvas.InkPresenter.StrokeContainer.Clear();
            InkMutated?.Invoke(this, EventArgs.Empty);
        }

        public List<StrokeAnnotation> GetStrokeData()
        {
            var list = new List<StrokeAnnotation>();
            foreach (var stroke in InkCanvas.InkPresenter.StrokeContainer.GetStrokes())
            {
                var attrs = stroke.DrawingAttributes;
                var color = attrs.Color;
                var sa = new StrokeAnnotation
                {
                    R = color.R, G = color.G, B = color.B, A = color.A,
                    Size = attrs.Size.Width, IsHighlighter = attrs.DrawAsHighlighter
                };
                foreach (var pt in stroke.GetInkPoints())
                {
                    sa.Points.Add(new[] { pt.Position.X, pt.Position.Y });
                }
                list.Add(sa);
            }
            return list;
        }

        public void AddStroke(StrokeAnnotation sa)
        {
            if (sa.Points == null || sa.Points.Count == 0) return;

            var color = Color.FromArgb(sa.A, sa.R, sa.G, sa.B);
            var attrs = new InkDrawingAttributes
            {
                Color = color,
                Size = new Size(sa.Size > 0 ? sa.Size : 2.0, sa.Size > 0 ? sa.Size : 2.0),
                DrawAsHighlighter = sa.IsHighlighter,
                FitToCurve = true
            };

            var inkPoints = new List<InkPoint>();
            foreach (var pt in sa.Points)
            {
                if (pt == null || pt.Length < 2) continue;
                inkPoints.Add(new InkPoint(new Point(pt[0], pt[1]), 0.5f));
            }

            if (inkPoints.Count > 0)
            {
                var builder = new InkStrokeBuilder();
                builder.SetDefaultDrawingAttributes(attrs);

                if (inkPoints.Count == 1)
                {
                    inkPoints.Add(new InkPoint(new Point(inkPoints[0].Position.X + 0.1, inkPoints[0].Position.Y), 0.5f));
                }

                var stroke = builder.CreateStrokeFromInkPoints(inkPoints, Matrix3x2.Identity);
                InkCanvas.InkPresenter.StrokeContainer.AddStroke(stroke);
            }
        }

        public void ClearStrokes()
        {
            InkCanvas.InkPresenter.StrokeContainer.Clear();
        }
    }
}
