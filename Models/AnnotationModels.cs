using System.Collections.Generic;

namespace Caelum.Models
{
    public class AnnotationData
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, PageAnnotation> Pages { get; set; } = new();
    }

    public class PageAnnotation
    {
        public List<StrokeAnnotation> Strokes { get; set; } = new();
        public List<TextAnnotation> Texts { get; set; } = new();
        public List<HighlightAnnotation> Highlights { get; set; } = new();
    }

    public class StrokeAnnotation
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; } = 255;
        public double Size { get; set; } = 2.0;
        public bool IsHighlighter { get; set; }
        public List<double[]> Points { get; set; } = new();
    }

    public class TextAnnotation
    {
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public double FontSize { get; set; } = 18;
    }

    public class HighlightAnnotation
    {
        // Each array contains [X, Y, Width, Height]
        public List<double[]> Rects { get; set; } = new();
        public byte R { get; set; } = 255;
        public byte G { get; set; } = 255;
        public byte B { get; set; } = 0;
        public byte A { get; set; } = 128;
    }
}
