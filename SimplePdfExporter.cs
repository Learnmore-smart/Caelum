#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Shapes;

namespace Caelum
{
    public class SimplePdfExporter
    {
        private const float DPI = 96.0f;
        private const float POINTS_PER_INCH = 72.0f;

        public static void SaveToPdf(string path, List<Polyline> strokes, double canvasWidth, double canvasHeight)
        {
            try
            {
                float widthPoints = (float)(canvasWidth * POINTS_PER_INCH / DPI);
                float heightPoints = (float)(canvasHeight * POINTS_PER_INCH / DPI);

                if (widthPoints <= 0) widthPoints = 612;
                if (heightPoints <= 0) heightPoints = 792;

                var content = BuildPdfContent(strokes, widthPoints, heightPoints);
                File.WriteAllBytes(path, content);
                Debug.WriteLine($"[SimplePdfExporter] Successfully saved PDF to: {path}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SimplePdfExporter] Error saving PDF: {ex.Message}");
                throw;
            }
        }

        private static byte[] BuildPdfContent(List<Polyline> strokes, float width, float height)
        {
            StringBuilder pdf = new StringBuilder();
            List<long> xrefOffsets = new List<long>();

            pdf.Append("%PDF-1.4\n");
            xrefOffsets.Add(pdf.Length);
            pdf.Append("1 0 obj\n");
            pdf.Append("<< /Type /Catalog\n");
            pdf.Append("   /Pages 2 0 R\n");
            pdf.Append(">>\n");
            pdf.Append("endobj\n");

            xrefOffsets.Add(pdf.Length);
            pdf.Append("2 0 obj\n");
            pdf.Append("<< /Type /Pages\n");
            pdf.Append("   /Kids [3 0 R]\n");
            pdf.Append("   /Count 1\n");
            pdf.Append(">>\n");
            pdf.Append("endobj\n");

            xrefOffsets.Add(pdf.Length);
            pdf.Append("3 0 obj\n");
            pdf.Append("<< /Type /Page\n");
            pdf.Append("   /Parent 2 0 R\n");
            pdf.Append($"   /MediaBox [0 0 {width:F2} {height:F2}]\n");
            pdf.Append("   /Resources << /ProcSet [/PDF] >>\n");
            pdf.Append("   /Contents 4 0 R\n");
            pdf.Append(">>\n");
            pdf.Append("endobj\n");

            string contentStream = BuildContentStream(strokes, width, height);
            xrefOffsets.Add(pdf.Length);
            pdf.Append("4 0 obj\n");
            pdf.Append($"<< /Length {contentStream.Length} >>\n");
            pdf.Append("stream\n");
            pdf.Append(contentStream);
            pdf.Append("endstream\n");
            pdf.Append("endobj\n");

            long xrefPos = pdf.Length;
            pdf.Append("xref\n");
            pdf.Append("0 5\n");
            pdf.Append("0000000000 65535 f \n");
            pdf.Append($"{xrefOffsets[0]:D10} 00000 n \n");
            pdf.Append($"{xrefOffsets[1]:D10} 00000 n \n");
            pdf.Append($"{xrefOffsets[2]:D10} 00000 n \n");
            pdf.Append($"{xrefOffsets[3]:D10} 00000 n \n");
            pdf.Append("trailer\n");
            pdf.Append("<< /Size 5\n");
            pdf.Append("   /Root 1 0 R\n");
            pdf.Append(">>\n");
            pdf.Append("startxref\n");
            pdf.Append($"{xrefPos}\n");
            pdf.Append("%%EOF\n");

            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        private static string BuildContentStream(List<Polyline> strokes, float width, float height)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var stroke in strokes)
            {
                if (stroke == null || stroke.Points.Count < 2)
                    continue;

                sb.AppendLine("0.0 0.0 0.0 rg");
                sb.AppendLine("3 w");
                sb.AppendLine("1 J");
                sb.AppendLine("1 j");

                var first = stroke.Points[0];
                float x1 = (float)(first.X * POINTS_PER_INCH / DPI);
                float y1 = height - (float)(first.Y * POINTS_PER_INCH / DPI);
                sb.AppendLine($"{x1:F2} {y1:F2} m");

                for (int i = 1; i < stroke.Points.Count; i++)
                {
                    var p = stroke.Points[i];
                    float x = (float)(p.X * POINTS_PER_INCH / DPI);
                    float y = height - (float)(p.Y * POINTS_PER_INCH / DPI);
                    sb.AppendLine($"{x:F2} {y:F2} l");
                }

                sb.AppendLine("S");
            }

            return sb.ToString();
        }
    }
}
