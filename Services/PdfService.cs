using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Caelum.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Annotations;
using PdfSharpCore.Pdf.Advanced;
using System.Linq;
using PdfiumViewer;

using PdfiumPdfDocument = PdfiumViewer.PdfDocument;
using PdfSharpPdfRectangle = PdfSharpCore.Pdf.PdfRectangle;

namespace Caelum.Services
{
    public class PdfService
    {
        private readonly SemaphoreSlim _documentLock = new SemaphoreSlim(1, 1);
        private const double PdfPointToDipScale = 96.0 / 72.0;
        private PdfiumPdfDocument _pdfDocument;
        private Stream _pdfBackingStream;
        private string _sourceFilePath;
        private readonly Dictionary<int, PdfPageTextInfo> _pageTextInfoCache = new Dictionary<int, PdfPageTextInfo>();
        private static readonly Regex RichTextBreakRegex = new Regex(@"<\s*br\s*/?\s*>|<\s*/p\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RichTextTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex DefaultAppearanceFontSizeRegex = new Regex(@"(?<size>[+-]?\d+(?:\.\d+)?)\s+Tf\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DefaultAppearanceRgbRegex = new Regex(@"(?<r>[+-]?\d*\.?\d+)\s+(?<g>[+-]?\d*\.?\d+)\s+(?<b>[+-]?\d*\.?\d+)\s+rg\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DefaultAppearanceGrayRegex = new Regex(@"(?<gray>[+-]?\d*\.?\d+)\s+g\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CssFontSizeRegex = new Regex(@"font-size\s*:\s*(?<size>[+-]?\d+(?:\.\d+)?)\s*(?<unit>pt|px)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CssFontRegex = new Regex(@"font\s*:[^;]*?(?<size>[+-]?\d+(?:\.\d+)?)\s*(?<unit>pt|px)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CssHexColorRegex = new Regex(@"color\s*:\s*(?<value>#[0-9a-f]{3}|#[0-9a-f]{6})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CssRgbColorRegex = new Regex(@"color\s*:\s*rgb\s*\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed class LoadedPdfDocument
        {
            public PdfiumPdfDocument Document { get; init; }
            public Stream BackingStream { get; init; }
            public Dictionary<int, Models.PageAnnotation> ExtractedAnnotations { get; init; } = new();
        }

        public sealed class PdfTextCharacterInfo
        {
            public int Offset { get; init; }
            public char Character { get; init; }
            public IReadOnlyList<Rect> Bounds { get; init; } = Array.Empty<Rect>();
            public Rect UnionBounds { get; init; }
        }

        public sealed class PdfPageTextInfo
        {
            public string Text { get; init; } = string.Empty;
            public IReadOnlyList<PdfTextCharacterInfo> Characters { get; init; } = Array.Empty<PdfTextCharacterInfo>();
        }

        public int PageCount => _pdfDocument?.PageCount ?? 0;
        public Dictionary<int, Models.PageAnnotation> ExtractedAnnotations { get; private set; } = new();

        public static async Task CreateBlankPdfAsync(
            string filePath,
            double widthPoints = 612,
            double heightPoints = 792,
            PageInsertTemplate template = PageInsertTemplate.Blank)
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);

                using var document = new PdfSharpCore.Pdf.PdfDocument();
                var page = document.AddPage();
                page.Width = widthPoints;
                page.Height = heightPoints;
                ApplyPageTemplate(page, template);
                document.Save(filePath);
            }).ConfigureAwait(false);
        }

        public async Task AppendBlankPageAsync(string filePath, double? widthPoints = null, double? heightPoints = null)
        {
            await InsertPageAsync(filePath, int.MaxValue, PageInsertTemplate.Blank, widthPoints, heightPoints).ConfigureAwait(false);
        }

        public async Task InsertPageAsync(string filePath, int insertIndex, PageInsertTemplate template, double? widthPoints = null, double? heightPoints = null)
        {
            await _documentLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => InsertPageCore(filePath, insertIndex, template, widthPoints, heightPoints), CancellationToken.None).ConfigureAwait(false);
                await ReloadDocumentFromFileAsync(filePath).ConfigureAwait(false);
            }
            finally
            {
                _documentLock.Release();
            }
        }

        public async Task DeletePageAsync(string filePath, int pageIndex)
        {
            await _documentLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => DeletePageCore(filePath, pageIndex), CancellationToken.None).ConfigureAwait(false);
                await ReloadDocumentFromFileAsync(filePath).ConfigureAwait(false);
            }
            finally
            {
                _documentLock.Release();
            }
        }

        /// <summary>
        /// Returns the page size in device-independent pixels (at 192 DPI rendering).
        /// Fast 鈥?no rendering required; uses cached page sizes from the loaded document.
        /// </summary>
        public (double Width, double Height) GetPageSizeInDips(int pageIndex)
        {
            if (_pdfDocument == null || pageIndex < 0 || pageIndex >= _pdfDocument.PageCount)
                return (0, 0);

            // Render at 192 DPI but BitmapSource reports DIPs as pixelWidth * 96 / dpi.
            // So effective DIP size = (pagePoints * 192/72) * 96/192 = pagePoints * 96/72.
            const double renderDpi = 192.0;
            var size = _pdfDocument.PageSizes[pageIndex];
            int pixelW = (int)(size.Width * renderDpi / 72.0);
            int pixelH = (int)(size.Height * renderDpi / 72.0);
            double w = pixelW * 96.0 / renderDpi;
            double h = pixelH * 96.0 / renderDpi;
            return (w, h);
        }

        public async Task LoadPdfAsync(string filePath, CancellationToken cancellationToken = default)
        {
            await LoadPdfCoreAsync(filePath, cancellationToken);
        }

        public bool TryGetCachedPageTextInfo(int pageIndex, out PdfPageTextInfo textInfo)
        {
            return _pageTextInfoCache.TryGetValue(pageIndex, out textInfo);
        }

        public async Task<PdfPageTextInfo> GetPageTextInfoAsync(int pageIndex, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _documentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_pageTextInfoCache.TryGetValue(pageIndex, out var cached))
                    return cached;

                if (_pdfDocument == null || pageIndex < 0 || pageIndex >= _pdfDocument.PageCount)
                    return new PdfPageTextInfo();

                var textInfo = await Task.Run(() => BuildPageTextInfo(pageIndex, cancellationToken), cancellationToken).ConfigureAwait(false);
                _pageTextInfoCache[pageIndex] = textInfo;
                return textInfo;
            }
            finally
            {
                _documentLock.Release();
            }
        }

        private PdfPageTextInfo BuildPageTextInfo(int pageIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = _pdfDocument.GetPdfText(pageIndex) ?? string.Empty;
            var characters = new List<PdfTextCharacterInfo>(text.Length);

            for (int offset = 0; offset < text.Length; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var span = new PdfTextSpan(pageIndex, offset, 1);
                var bounds = GetTextBoundsInDips(pageIndex, span);

                Rect unionBounds = Rect.Empty;
                for (int i = 0; i < bounds.Count; i++)
                {
                    if (unionBounds.IsEmpty)
                        unionBounds = bounds[i];
                    else
                        unionBounds.Union(bounds[i]);
                }

                characters.Add(new PdfTextCharacterInfo
                {
                    Offset = offset,
                    Character = text[offset],
                    Bounds = bounds,
                    UnionBounds = unionBounds
                });
            }

            return new PdfPageTextInfo
            {
                Text = text,
                Characters = characters
            };
        }

        private IReadOnlyList<Rect> GetTextBoundsInDips(int pageIndex, PdfTextSpan span)
        {
            var pdfBounds = _pdfDocument.GetTextBounds(span);
            if (pdfBounds == null || pdfBounds.Count == 0)
                return Array.Empty<Rect>();

            var bounds = new List<Rect>(pdfBounds.Count);
            foreach (var pdfRect in pdfBounds)
            {
                if (!pdfRect.IsValid)
                    continue;

                var deviceRect = _pdfDocument.RectangleFromPdf(pageIndex, pdfRect.Bounds);
                if (deviceRect.Width <= 0 || deviceRect.Height <= 0)
                    continue;

                bounds.Add(new Rect(
                    deviceRect.X * PdfPointToDipScale,
                    deviceRect.Y * PdfPointToDipScale,
                    deviceRect.Width * PdfPointToDipScale,
                    deviceRect.Height * PdfPointToDipScale));
            }

            return bounds.Count == 0 ? Array.Empty<Rect>() : bounds;
        }

        private async Task LoadPdfCoreAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _documentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DisposeCurrentDocument();
                _sourceFilePath = filePath;

                var loaded = await Task.Run(() => LoadPdfDocument(filePath, cancellationToken), cancellationToken).ConfigureAwait(false);
                _pdfDocument = loaded.Document;
                _pdfBackingStream = loaded.BackingStream;
                ExtractedAnnotations = loaded.ExtractedAnnotations;
            }
            finally
            {
                _documentLock.Release();
            }
        }

        private LoadedPdfDocument LoadPdfDocument(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"PDF file not found: {filePath}");

            MemoryStream strippedStream = null;
            try
            {
                strippedStream = new MemoryStream();
                Dictionary<int, Models.PageAnnotation> extractedAnnotations;

                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    extractedAnnotations = ExtractAndStripAnnotations(sourceStream, strippedStream, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                strippedStream.Position = 0;

                return new LoadedPdfDocument
                {
                    Document = PdfiumPdfDocument.Load(strippedStream),
                    BackingStream = strippedStream,
                    ExtractedAnnotations = extractedAnnotations
                };
            }
            catch (OperationCanceledException)
            {
                strippedStream?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                strippedStream?.Dispose();
                System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: annotation stripping failed: {ex.Message}");

                return new LoadedPdfDocument
                {
                    Document = PdfiumPdfDocument.Load(filePath),
                    ExtractedAnnotations = new Dictionary<int, Models.PageAnnotation>()
                };
            }
        }

        private void DisposeCurrentDocument()
        {
            _pageTextInfoCache.Clear();
            _pdfDocument?.Dispose();
            _pdfDocument = null;
            _pdfBackingStream?.Dispose();
            _pdfBackingStream = null;
        }

        private async Task ReloadDocumentFromFileAsync(string filePath)
        {
            DisposeCurrentDocument();
            _sourceFilePath = filePath;

            var loaded = await Task.Run(() => LoadPdfDocument(filePath, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
            _pdfDocument = loaded.Document;
            _pdfBackingStream = loaded.BackingStream;
            ExtractedAnnotations = loaded.ExtractedAnnotations;
        }

        private static void InsertPageCore(string filePath, int insertIndex, PageInsertTemplate template, double? widthPoints, double? heightPoints)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("PDF to update not found.", filePath);

            string tempPath = Path.Combine(
                Path.GetDirectoryName(filePath) ?? string.Empty,
                $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var document = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Modify))
                {
                    int safeInsertIndex = Math.Max(0, Math.Min(insertIndex, document.PageCount));
                    var referencePage = document.PageCount == 0
                        ? null
                        : document.Pages[Math.Min(safeInsertIndex, document.PageCount - 1)];

                    var page = document.InsertPage(safeInsertIndex);
                    page.Width = widthPoints ?? referencePage?.Width.Point ?? 612;
                    page.Height = heightPoints ?? referencePage?.Height.Point ?? 792;

                    ApplyPageTemplate(page, template);
                    SaveModifiedDocument(document, tempPath);
                }

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

        private static void DeletePageCore(string filePath, int pageIndex)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("PDF to update not found.", filePath);

            string tempPath = Path.Combine(
                Path.GetDirectoryName(filePath) ?? string.Empty,
                $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var document = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Modify))
                {
                    if (document.PageCount <= 1)
                        throw new InvalidOperationException("At least one page must remain in the document.");

                    if (pageIndex < 0 || pageIndex >= document.PageCount)
                        throw new ArgumentOutOfRangeException(nameof(pageIndex));

                    document.Pages.RemoveAt(pageIndex);
                    SaveModifiedDocument(document, tempPath);
                }

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

        private static void SaveModifiedDocument(PdfSharpCore.Pdf.PdfDocument document, string tempPath)
        {
            using var outputStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            document.Save(outputStream, false);
        }

        private static void ApplyPageTemplate(PdfSharpCore.Pdf.PdfPage page, PageInsertTemplate template)
        {
            using var gfx = XGraphics.FromPdfPage(page);
            double width = page.Width.Point;
            double height = page.Height.Point;

            gfx.DrawRectangle(new XSolidBrush(GetTemplateBackground(template)), 0, 0, width, height);

            switch (template)
            {
                case PageInsertTemplate.Notebook:
                    DrawNotebookTemplate(gfx, width, height);
                    break;
                case PageInsertTemplate.Lined:
                    DrawLinedTemplate(gfx, width, height);
                    break;
                case PageInsertTemplate.Quadrille:
                    DrawQuadrilleTemplate(gfx, width, height);
                    break;
            }
        }

        private static XColor GetTemplateBackground(PageInsertTemplate template)
        {
            return template == PageInsertTemplate.Notebook
                ? XColor.FromArgb(255, 253, 249, 238)
                : XColors.White;
        }

        private static void DrawNotebookTemplate(XGraphics gfx, double width, double height)
        {
            DrawLinedTemplate(gfx, width, height, topMargin: 46, leftMargin: 54, rightMargin: 36, lineSpacing: 24, lineColor: XColor.FromArgb(255, 200, 221, 252));

            var marginPen = new XPen(XColor.FromArgb(255, 239, 68, 68), 1.3);
            double marginX = 78;
            gfx.DrawLine(marginPen, marginX, 28, marginX, height - 28);
        }

        private static void DrawLinedTemplate(XGraphics gfx, double width, double height, double topMargin = 40, double leftMargin = 30, double rightMargin = 30, double lineSpacing = 24, XColor? lineColor = null)
        {
            var pen = new XPen(lineColor ?? XColor.FromArgb(255, 203, 213, 225), 0.9);
            for (double y = topMargin; y < height - 24; y += lineSpacing)
                gfx.DrawLine(pen, leftMargin, y, width - rightMargin, y);
        }

        private static void DrawQuadrilleTemplate(XGraphics gfx, double width, double height)
        {
            var majorPen = new XPen(XColor.FromArgb(255, 191, 219, 254), 0.95);
            var minorPen = new XPen(XColor.FromArgb(255, 219, 234, 254), 0.65);
            const double spacing = 18;

            for (double x = 24; x < width - 24; x += spacing)
            {
                bool isMajor = Math.Abs(((x - 24) / spacing) % 4) < 0.001;
                gfx.DrawLine(isMajor ? majorPen : minorPen, x, 24, x, height - 24);
            }

            for (double y = 24; y < height - 24; y += spacing)
            {
                bool isMajor = Math.Abs(((y - 24) / spacing) % 4) < 0.001;
                gfx.DrawLine(isMajor ? majorPen : minorPen, 24, y, width - 24, y);
            }
        }

        private Dictionary<int, Models.PageAnnotation> ExtractAndStripAnnotations(Stream sourceStream, Stream outputStream, CancellationToken cancellationToken)
        {
            var extractedAnnotations = new Dictionary<int, Models.PageAnnotation>();
            const double dipDpi = 96.0;
            double scale = dipDpi / 72.0;

            using var document = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Modify);

            for (int i = 0; i < document.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.Pages[i];
                double pageHeight = page.Height.Point;
                var pageAnnots = new Models.PageAnnotation();

                var annots = page.Elements.GetArray("/Annots");
                if (annots != null)
                {
                    var elementsToRemove = new List<PdfItem>();

                    foreach (var annotItem in annots.Elements)
                    {
                        var dict = (annotItem as PdfReference)?.Value as PdfDictionary ?? annotItem as PdfDictionary;
                        if (dict != null)
                        {
                            var subtype = dict.Elements.GetName("/Subtype");

                            if (subtype == "/FreeText")
                            {
                                var textAnnotation = TryExtractFreeTextAnnotation(dict, pageHeight, scale);
                                if (textAnnotation != null)
                                {
                                    pageAnnots.Texts.Add(textAnnotation);
                                    elementsToRemove.Add(annotItem);
                                }
                            }
                            else if (subtype == "/Ink")
                            {
                                var inkList = dict.Elements.GetArray("/InkList");
                                bool extractedInk = false;
                                if (inkList != null && inkList.Elements.Count > 0)
                                {
                                    foreach (var strokeItem in inkList.Elements)
                                    {
                                        var pointArray = (strokeItem as PdfReference)?.Value as PdfArray ?? strokeItem as PdfArray;
                                        if (pointArray != null && pointArray.Elements.Count >= 2)
                                        {
                                            var strokeAnnot = new Models.StrokeAnnotation();

                                            var cArray = dict.Elements.GetArray("/C");
                                            if (cArray != null && cArray.Elements.Count >= 3)
                                            {
                                                strokeAnnot.R = (byte)(GetDouble(cArray.Elements[0]) * 255);
                                                strokeAnnot.G = (byte)(GetDouble(cArray.Elements[1]) * 255);
                                                strokeAnnot.B = (byte)(GetDouble(cArray.Elements[2]) * 255);
                                            }

                                            double ca = dict.Elements.ContainsKey("/CA") ? GetDouble(dict.Elements["/CA"], 1.0) : 1.0;
                                            strokeAnnot.A = (byte)(ca * 255);
                                            strokeAnnot.IsHighlighter = ca < 1.0;

                                            var bs = dict.Elements.GetDictionary("/BS");
                                            if (bs != null) strokeAnnot.Size = (bs.Elements.ContainsKey("/W") ? GetDouble(bs.Elements["/W"], 2.0) : 2.0) * scale;

                                            for (int pIdx = 0; pIdx < pointArray.Elements.Count - 1; pIdx += 2)
                                            {
                                                double ptX = GetDouble(pointArray.Elements[pIdx]);
                                                double ptY = GetDouble(pointArray.Elements[pIdx + 1]);
                                                strokeAnnot.Points.Add(new[] { ptX * scale, (pageHeight - ptY) * scale });
                                            }

                                            if (strokeAnnot.Points.Count > 0)
                                            {
                                                pageAnnots.Strokes.Add(strokeAnnot);
                                                extractedInk = true;
                                            }
                                        }
                                    }
                                }
                                if (extractedInk)
                                    elementsToRemove.Add(annotItem);
                            }
                            else if (subtype == "/Highlight")
                            {
                                var quadPoints = dict.Elements.GetArray("/QuadPoints");
                                if (quadPoints != null && quadPoints.Elements.Count >= 8)
                                {
                                    var highlightAnnot = new Models.HighlightAnnotation();

                                    var cArray = dict.Elements.GetArray("/C");
                                    if (cArray != null && cArray.Elements.Count >= 3)
                                    {
                                        highlightAnnot.R = (byte)(GetDouble(cArray.Elements[0]) * 255);
                                        highlightAnnot.G = (byte)(GetDouble(cArray.Elements[1]) * 255);
                                        highlightAnnot.B = (byte)(GetDouble(cArray.Elements[2]) * 255);
                                    }

                                    double ca = dict.Elements.ContainsKey("/CA") ? GetDouble(dict.Elements["/CA"], 1.0) : 1.0;
                                    highlightAnnot.A = (byte)(ca * 255);

                                    // QuadPoints: [TL.X, TL.Y, TR.X, TR.Y, BL.X, BL.Y, BR.X, BR.Y]
                                    for (int pIdx = 0; pIdx < quadPoints.Elements.Count - 7; pIdx += 8)
                                    {
                                        double x1 = GetDouble(quadPoints.Elements[pIdx]);
                                        double y1 = GetDouble(quadPoints.Elements[pIdx + 1]);
                                        double x2 = GetDouble(quadPoints.Elements[pIdx + 2]);
                                        double y2 = GetDouble(quadPoints.Elements[pIdx + 7]);

                                        double minX = Math.Min(x1, x2);
                                        double maxX = Math.Max(x1, x2);
                                        double minY = Math.Min(y1, y2);
                                        double maxY = Math.Max(y1, y2);

                                        double x_ui = minX * scale;
                                        double w_ui = (maxX - minX) * scale;
                                        double h_ui = (maxY - minY) * scale;
                                        double y_ui = (pageHeight - maxY) * scale; // Invert Y

                                        highlightAnnot.Rects.Add(new[] { x_ui, y_ui, w_ui, h_ui });
                                    }

                                    if (highlightAnnot.Rects.Count > 0)
                                    {
                                        pageAnnots.Highlights.Add(highlightAnnot);
                                        elementsToRemove.Add(annotItem);
                                    }
                                }
                            }
                        }
                    }

                    foreach (var item in elementsToRemove)
                    {
                        annots.Elements.Remove(item);
                    }
                }

                if (pageAnnots.Strokes.Count > 0 || pageAnnots.Texts.Count > 0 || pageAnnots.Highlights.Count > 0)
                {
                    extractedAnnotations[i] = pageAnnots;
                }
            }

            document.Save(outputStream);
            return extractedAnnotations;
        }

        public async Task<BitmapImage> RenderPageAsync(int pageIndex, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                if (_pdfDocument == null) return null;
                if (pageIndex < 0 || pageIndex >= _pdfDocument.PageCount) return null;

                const int renderDpi = 192;
                var size = _pdfDocument.PageSizes[pageIndex];
                int width = (int)(size.Width * renderDpi / 72.0);
                int height = (int)(size.Height * renderDpi / 72.0);

                using (var image = _pdfDocument.Render(pageIndex, width, height, renderDpi, renderDpi, PdfRenderFlags.Annotations))
                {
                    using (var ms = new MemoryStream())
                    {
                        image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Seek(0, SeekOrigin.Begin);

                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            finally
            {
                _documentLock.Release();
            }
        }

        public async Task<byte[]> RenderPagePngBytesAsync(int pageIndex, CancellationToken cancellationToken = default)
        {
            return await RenderPagePngBytesAsync(pageIndex, 1.0, cancellationToken);
        }

        public async Task<byte[]> RenderPagePngBytesAsync(int pageIndex, double dpiScale, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: start for page {pageIndex}, dpiScale={dpiScale}");

            if (_pdfDocument == null)
            {
                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: _pdfDocument is null");
                return null;
            }
            if (pageIndex < 0 || pageIndex >= _pdfDocument.PageCount)
            {
                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: pageIndex {pageIndex} out of range");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                if (_pdfDocument == null)
                {
                    System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: _pdfDocument is null after lock");
                    return null;
                }
                if (pageIndex < 0 || pageIndex >= _pdfDocument.PageCount)
                {
                    System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: pageIndex check failed after lock");
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: getting page {pageIndex}");
                int renderDpi = (int)(192 * Math.Max(dpiScale, 1.0));
                var size = _pdfDocument.PageSizes[pageIndex];
                int width = (int)(size.Width * renderDpi / 72.0);
                int height = (int)(size.Height * renderDpi / 72.0);

                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: page {pageIndex} size: {width}x{height}");

                using (var image = _pdfDocument.Render(pageIndex, width, height, renderDpi, renderDpi, PdfRenderFlags.Annotations))
                {
                    using (var ms = new MemoryStream())
                    {
                        image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        var bytes = ms.ToArray();
                        System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: returning {bytes.Length} bytes");
                        return bytes;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync STACK: {ex.StackTrace}");
                throw;
            }
            finally
            {
                _documentLock.Release();
            }
        }

        /// <summary>
        /// Fast render path: converts GDI+ Bitmap 鈫?frozen BitmapSource directly,
        /// bypassing PNG encode/decode. ~5-10x faster than the PNG roundtrip.
        /// </summary>
        public async Task<BitmapSource> RenderPageBitmapSourceAsync(int pageIndex, double dpiScale, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _documentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_pdfDocument == null) return null;
                if (pageIndex < 0 || pageIndex >= _pdfDocument.PageCount) return null;

                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int renderDpi = (int)(192 * Math.Max(dpiScale, 1.0));
                    var size = _pdfDocument.PageSizes[pageIndex];
                    int width = (int)(size.Width * renderDpi / 72.0);
                    int height = (int)(size.Height * renderDpi / 72.0);

                    using var gdiBitmap = (System.Drawing.Bitmap)_pdfDocument.Render(pageIndex, width, height, renderDpi, renderDpi, PdfRenderFlags.Annotations);
                    var bmpData = gdiBitmap.LockBits(
                        new System.Drawing.Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    try
                    {
                        var result = BitmapSource.Create(
                            width, height,
                            renderDpi, renderDpi,
                            PixelFormats.Bgra32,
                            null,
                            bmpData.Scan0,
                            bmpData.Stride * height,
                            bmpData.Stride);
                        result.Freeze();
                        return result;
                    }
                    finally
                    {
                        gdiBitmap.UnlockBits(bmpData);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _documentLock.Release();
            }
        }

        public async Task SaveAnnotationsToPdfAsync(string filePath, Dictionary<int, Models.PageAnnotation> annotations)
        {
            await _documentLock.WaitAsync().ConfigureAwait(false);
            try
            {
                bool requiresReload = _pdfBackingStream == null;
                if (requiresReload)
                    DisposeCurrentDocument();

                await Task.Run(() => SaveAnnotationsCore(filePath, annotations), CancellationToken.None).ConfigureAwait(false);

                if (requiresReload)
                {
                    var loaded = await Task.Run(() => LoadPdfDocument(filePath, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                    _pdfDocument = loaded.Document;
                    _pdfBackingStream = loaded.BackingStream;
                    ExtractedAnnotations = loaded.ExtractedAnnotations;
                }
            }
            finally
            {
                _documentLock.Release();
            }
        }

        private void SaveAnnotationsCore(string filePath, Dictionary<int, Models.PageAnnotation> annotations)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException("PDF to save not found.");

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.IsReadOnly)
                throw new UnauthorizedAccessException($"The file \"{Path.GetFileName(filePath)}\" is read-only. Please disable read-only mode in file properties and try again.");

            string tempPath = Path.Combine(
                Path.GetDirectoryName(filePath) ?? string.Empty,
                $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                // Read the entire PDF into memory first to avoid file locking issues
                byte[] pdfBytes;
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    pdfBytes = new byte[sourceStream.Length];
                    int bytesRead = sourceStream.Read(pdfBytes, 0, pdfBytes.Length);
                    if (bytesRead != sourceStream.Length)
                    {
                        throw new IOException($"Failed to read complete PDF file. Expected {sourceStream.Length} bytes, but read {bytesRead} bytes.");
                    }
                }

                // Use a memory stream for PDF operations to avoid file system issues
                using (var memoryStream = new MemoryStream(pdfBytes))
                using (var document = PdfReader.Open(memoryStream, PdfDocumentOpenMode.Modify))
                {
                    const double dipDpi = 96.0;
                    double scale = 72.0 / dipDpi;

                    for (int i = 0; i < document.PageCount; i++)
                    {
                        var pdfPage = document.Pages[i];
                        double pageHeight = pdfPage.Height.Point;

                        var annots = pdfPage.Elements.GetArray("/Annots");
                        if (annots != null)
                        {
                            var toRemove = new List<PdfItem>();
                            foreach (var item in annots.Elements)
                            {
                                var dict = (item as PdfReference)?.Value as PdfDictionary ?? item as PdfDictionary;
                                if (dict != null)
                                {
                                    var sub = dict.Elements.GetName("/Subtype");
                                    if (sub == "/FreeText" || sub == "/Ink" || sub == "/Highlight")
                                        toRemove.Add(item);
                                }
                            }

                            foreach (var item in toRemove)
                                annots.Elements.Remove(item);
                        }

                        if (!annotations.TryGetValue(i, out var pageAnnots))
                            continue;

                        foreach (var textItem in pageAnnots.Texts)
                        {
                            // Estimate annotation geometry
                            var textLines = textItem.Text.Split('\n');
                            double pdfFontSize = textItem.FontSize * scale;
                            double lineHeight = pdfFontSize * 1.4;
                            double w = Math.Max(150 * scale, textLines.Max(l => l.Length) * pdfFontSize * 0.55 + 12);
                            double h = textLines.Length * lineHeight + pdfFontSize * 0.4;

                            double x = textItem.X * scale;
                            double y = pageHeight - (textItem.Y * scale) - h;

                            var xRect = new XRect(x, y, w, h);
                            var annot = new PdfDictionary(document);
                            annot.Elements.SetName(PdfAnnotation.Keys.Subtype, "/FreeText");
                            annot.Elements.SetRectangle(PdfAnnotation.Keys.Rect, new PdfSharpPdfRectangle(xRect));
                            annot.Elements.SetString(PdfAnnotation.Keys.Contents, textItem.Text);
                            annot.Elements.SetString("/NM", $"wna_text_{Guid.NewGuid()}");
                            annot.Elements.SetInteger("/F", 4); // Printable

                            double r2 = textItem.R / 255.0, g2 = textItem.G / 255.0, b2 = textItem.B / 255.0;
                            // Remove border
                            var bsForText = new PdfDictionary();
                            bsForText.Elements.SetInteger("/W", 0);
                            annot.Elements["/BS"] = bsForText;

                            // /DA — required by spec
                            annot.Elements.SetString("/DA", $"/Helv {pdfFontSize:F2} Tf {r2:F3} {g2:F3} {b2:F3} rg");

                            // Build appearance stream as raw PDF content stream
                            var apStream = new StringBuilder();
                            apStream.AppendLine("q");
                            apStream.AppendLine($"{r2:F3} {g2:F3} {b2:F3} rg");
                            apStream.AppendLine("BT");
                            apStream.AppendLine($"/Helv {pdfFontSize:F2} Tf");
                            double yApStream = h - lineHeight + (lineHeight - pdfFontSize) / 2;
                            for (int li = 0; li < textLines.Length; li++)
                            {
                                // Escape parentheses in content
                                string escaped = textLines[li].Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
                                if (li == 0)
                                    apStream.AppendLine($"4 {yApStream:F2} Td");
                                else
                                    apStream.AppendLine($"0 {-lineHeight:F2} Td");
                                apStream.AppendLine($"({escaped}) Tj");
                            }
                            apStream.AppendLine("ET");
                            apStream.AppendLine("Q");

                            var apNormal = CreateAppearanceStream(
                                document,
                                w,
                                h,
                                apStream.ToString(),
                                CreateStandardFontResources(document));
                            annot.Elements["/AP"] = CreateAppearanceDictionary(document, apNormal);

                            AddAnnotationToPage(pdfPage, annot);
                        }

                        foreach (var stroke in pageAnnots.Strokes)
                        {
                            if (stroke.Points.Count == 0) continue;

                            var dict = new PdfDictionary(document);
                            dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/Ink");
                            dict.Elements.SetString("/NM", $"wna_ink_{Guid.NewGuid()}");
                            dict.Elements.SetInteger("/F", 4);

                            var colorArray = new PdfArray();
                            colorArray.Elements.Add(new PdfReal(stroke.R / 255.0));
                            colorArray.Elements.Add(new PdfReal(stroke.G / 255.0));
                            colorArray.Elements.Add(new PdfReal(stroke.B / 255.0));
                            dict.Elements.Add("/C", colorArray);

                            double opacity = stroke.IsHighlighter ? 0.5 : stroke.A / 255.0;
                            if (opacity < 1.0)
                                dict.Elements.SetReal("/CA", opacity);

                            double strokeWidth = Math.Max(stroke.Size * scale, 0.5);
                            var bsDict = new PdfDictionary();
                            bsDict.Elements.SetName("/Type", "/Border");
                            bsDict.Elements.SetReal("/W", strokeWidth);
                            dict.Elements.Add("/BS", bsDict);

                            var pdfPoints = new List<Point>(stroke.Points.Count);
                            var inkListArray = new PdfArray();
                            var pointArray = new PdfArray();
                            foreach (var pt in stroke.Points)
                            {
                                double pdfX = pt[0] * scale;
                                double pdfY = pageHeight - (pt[1] * scale);
                                pointArray.Elements.Add(new PdfReal(pdfX));
                                pointArray.Elements.Add(new PdfReal(pdfY));
                                pdfPoints.Add(new Point(pdfX, pdfY));
                            }
                            inkListArray.Elements.Add(pointArray);
                            dict.Elements.Add("/InkList", inkListArray);

                            if (pdfPoints.Count == 1)
                                pdfPoints.Add(new Point(pdfPoints[0].X + strokeWidth, pdfPoints[0].Y));

                            double padding = Math.Max(strokeWidth, 1.0);
                            double minX = Math.Max(0, pdfPoints.Min(point => point.X) - padding);
                            double maxX = Math.Min(pdfPage.Width.Point, pdfPoints.Max(point => point.X) + padding);
                            double minY = Math.Max(0, pdfPoints.Min(point => point.Y) - padding);
                            double maxY = Math.Min(pdfPage.Height.Point, pdfPoints.Max(point => point.Y) + padding);
                            double appearanceWidth = Math.Max(1.0, maxX - minX);
                            double appearanceHeight = Math.Max(1.0, maxY - minY);

                            dict.Elements.SetRectangle(
                                PdfAnnotation.Keys.Rect,
                                new PdfSharpPdfRectangle(new XRect(minX, minY, appearanceWidth, appearanceHeight)));

                            var appearanceStream = new StringBuilder();
                            appearanceStream.AppendLine("q");
                            if (opacity < 1.0)
                                appearanceStream.AppendLine("/GS1 gs");
                            appearanceStream.AppendLine($"{stroke.R / 255.0:F3} {stroke.G / 255.0:F3} {stroke.B / 255.0:F3} RG");
                            appearanceStream.AppendLine($"{strokeWidth:F2} w");
                            appearanceStream.AppendLine("1 J");
                            appearanceStream.AppendLine("1 j");
                            appearanceStream.AppendLine($"{pdfPoints[0].X - minX:F2} {pdfPoints[0].Y - minY:F2} m");
                            for (int pointIndex = 1; pointIndex < pdfPoints.Count; pointIndex++)
                            {
                                var point = pdfPoints[pointIndex];
                                appearanceStream.AppendLine($"{point.X - minX:F2} {point.Y - minY:F2} l");
                            }
                            appearanceStream.AppendLine("S");
                            appearanceStream.AppendLine("Q");

                            var appearance = CreateAppearanceStream(
                                document,
                                appearanceWidth,
                                appearanceHeight,
                                appearanceStream.ToString(),
                                CreateAppearanceResources(document, opacity, stroke.IsHighlighter));
                            dict.Elements["/AP"] = CreateAppearanceDictionary(document, appearance);

                            AddAnnotationToPage(pdfPage, dict);
                        }

                        foreach (var highlight in pageAnnots.Highlights)
                        {
                            if (highlight.Rects.Count == 0) continue;

                            var dict = new PdfDictionary(document);
                            dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/Highlight");
                            dict.Elements.SetString("/NM", $"wna_hl_{Guid.NewGuid()}");
                            dict.Elements.SetInteger("/F", 4);

                            double minX = double.MaxValue, minY = double.MaxValue;
                            double maxX = double.MinValue, maxY = double.MinValue;
                            var quadPoints = new PdfArray();
                            var appearanceRects = new List<XRect>(highlight.Rects.Count);

                            foreach (var rectInfo in highlight.Rects)
                            {
                                double x_ui = rectInfo[0];
                                double y_ui = rectInfo[1];
                                double w_ui = rectInfo[2];
                                double h_ui = rectInfo[3];

                                double x1 = x_ui * scale;
                                double y1 = pageHeight - (y_ui * scale); // Top Y in PDF coords
                                double x2 = (x_ui + w_ui) * scale;
                                double y2 = pageHeight - ((y_ui + h_ui) * scale); // Bottom Y in PDF coords

                                minX = Math.Min(minX, Math.Min(x1, x2));
                                minY = Math.Min(minY, Math.Min(y1, y2));
                                maxX = Math.Max(maxX, Math.Max(x1, x2));
                                maxY = Math.Max(maxY, Math.Max(y1, y2));
                                appearanceRects.Add(new XRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y1 - y2)));

                                // QuadPoints: [TL.X, TL.Y, TR.X, TR.Y, BL.X, BL.Y, BR.X, BR.Y]
                                quadPoints.Elements.Add(new PdfReal(x1));
                                quadPoints.Elements.Add(new PdfReal(y1));
                                quadPoints.Elements.Add(new PdfReal(x2));
                                quadPoints.Elements.Add(new PdfReal(y1));
                                quadPoints.Elements.Add(new PdfReal(x1));
                                quadPoints.Elements.Add(new PdfReal(y2));
                                quadPoints.Elements.Add(new PdfReal(x2));
                                quadPoints.Elements.Add(new PdfReal(y2));
                            }

                            dict.Elements.SetRectangle(PdfAnnotation.Keys.Rect, new PdfSharpPdfRectangle(new XRect(minX, minY, maxX - minX, maxY - minY)));
                            dict.Elements.Add("/QuadPoints", quadPoints);

                            var colorArray = new PdfArray();
                            colorArray.Elements.Add(new PdfReal(highlight.R / 255.0));
                            colorArray.Elements.Add(new PdfReal(highlight.G / 255.0));
                            colorArray.Elements.Add(new PdfReal(highlight.B / 255.0));
                            dict.Elements.Add("/C", colorArray);

                            double opacity = highlight.A / 255.0;
                            if (opacity < 1.0)
                                dict.Elements.SetReal("/CA", opacity);

                            var appearanceStream = new StringBuilder();
                            appearanceStream.AppendLine("q");
                            appearanceStream.AppendLine("/GS1 gs");
                            appearanceStream.AppendLine($"{highlight.R / 255.0:F3} {highlight.G / 255.0:F3} {highlight.B / 255.0:F3} rg");
                            foreach (var rect in appearanceRects)
                            {
                                appearanceStream.AppendLine($"{rect.X - minX:F2} {rect.Y - minY:F2} {rect.Width:F2} {rect.Height:F2} re");
                                appearanceStream.AppendLine("f");
                            }
                            appearanceStream.AppendLine("Q");

                            var appearance = CreateAppearanceStream(
                                document,
                                Math.Max(1.0, maxX - minX),
                                Math.Max(1.0, maxY - minY),
                                appearanceStream.ToString(),
                                CreateAppearanceResources(document, opacity, true));
                            dict.Elements["/AP"] = CreateAppearanceDictionary(document, appearance);

                            AddAnnotationToPage(pdfPage, dict);
                        }
                    }

                    // Save the document to the temporary file
                    using (var outputStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        document.Save(outputStream, false);
                    }
                }

                // Now that the document is saved and streams are closed, move the temp file
                try
                {
                    File.Move(tempPath, filePath, true);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new UnauthorizedAccessException(
                        $"Cannot save to \"{Path.GetFileName(filePath)}\". The file may be open in another program (e.g., PDF reader). " +
                        $"Please close the file and try again.", ex);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"Cannot save to \"{Path.GetFileName(filePath)}\". The file may be open in another program. " +
                        $"Please close the file and try again.", ex);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        private void AddAnnotationToPage(PdfSharpCore.Pdf.PdfPage page, PdfDictionary annotation)
        {
            if (!annotation.Elements.ContainsKey("/Type"))
                annotation.Elements.SetName("/Type", "/Annot");
            if (!annotation.Elements.ContainsKey("/F"))
                annotation.Elements.SetInteger("/F", 4);
            if (!annotation.Elements.ContainsKey("/P") && page.Reference != null)
                annotation.Elements["/P"] = page.Reference;
            // Register as an indirect object (PDF spec §12.3.3 requires annotations to be indirect
            // objects referenced by N 0 R). Inline annotation dicts can confuse strict viewers like Edge.
            if (annotation.Reference == null)
                page.Owner.Internals.AddObject(annotation);

            var annots = page.Elements.GetArray("/Annots");
            if (annots == null)
            {
                annots = new PdfArray(page.Owner);
                page.Elements.Add("/Annots", annots);
            }
            annots.Elements.Add(annotation.Reference);
        }

        private static PdfDictionary CreateAppearanceDictionary(PdfSharpCore.Pdf.PdfDocument document, PdfDictionary normalAppearance)
        {
            var appearanceDictionary = new PdfDictionary(document);
            appearanceDictionary.Elements["/N"] = normalAppearance.Reference;
            return appearanceDictionary;
        }

        private static PdfDictionary CreateStandardFontResources(PdfSharpCore.Pdf.PdfDocument document)
        {
            var font = new PdfDictionary(document);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", "/Helvetica");
            font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
            document.Internals.AddObject(font);

            var fonts = new PdfDictionary(document);
            fonts.Elements["/Helv"] = font.Reference;

            var resources = new PdfDictionary(document);
            resources.Elements["/Font"] = fonts;
            return resources;
        }

        private static PdfDictionary CreateAppearanceResources(PdfSharpCore.Pdf.PdfDocument document, double opacity, bool useMultiplyBlend)
        {
            if (opacity >= 0.999 && !useMultiplyBlend)
                return null;

            var graphicsState = new PdfDictionary(document);
            graphicsState.Elements.SetName("/Type", "/ExtGState");
            graphicsState.Elements.SetReal("/CA", opacity);
            graphicsState.Elements.SetReal("/ca", opacity);
            if (useMultiplyBlend)
                graphicsState.Elements.SetName("/BM", "/Multiply");
            document.Internals.AddObject(graphicsState);

            var extGState = new PdfDictionary(document);
            extGState.Elements["/GS1"] = graphicsState.Reference;

            var resources = new PdfDictionary(document);
            resources.Elements["/ExtGState"] = extGState;
            return resources;
        }

        private static PdfDictionary CreateAppearanceStream(
            PdfSharpCore.Pdf.PdfDocument document,
            double width,
            double height,
            string contentStream,
            PdfDictionary resources)
        {
            var appearanceStream = new PdfDictionary(document);
            appearanceStream.Elements.SetName("/Type", "/XObject");
            appearanceStream.Elements.SetName("/Subtype", "/Form");
            appearanceStream.Elements.SetInteger("/FormType", 1);

            var bbox = new PdfArray(document);
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(width));
            bbox.Elements.Add(new PdfReal(height));
            appearanceStream.Elements["/BBox"] = bbox;

            if (resources != null)
                appearanceStream.Elements["/Resources"] = resources;

            appearanceStream.CreateStream(Encoding.Latin1.GetBytes(contentStream));
            document.Internals.AddObject(appearanceStream);
            return appearanceStream;
        }

        internal static Models.TextAnnotation TryExtractFreeTextAnnotation(PdfDictionary dict, double pageHeight, double scale)
        {
            if (dict == null)
                return null;

            var rect = dict.Elements.GetRectangle("/Rect");
            string text = ExtractAnnotationText(dict);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var annotation = new Models.TextAnnotation
            {
                Text = text,
                X = rect.X1 * scale,
                Y = (pageHeight - rect.Y1 - rect.Height) * scale,
                FontSize = 18,
                R = 0,
                G = 0,
                B = 0
            };

            if (TryExtractFontSizeFromDefaultAppearance(dict.Elements.GetString("/DA"), scale, out var fontSizeFromDa))
                annotation.FontSize = fontSizeFromDa;
            else if (TryExtractFontSizeFromStyleString(dict.Elements.GetString("/DS"), scale, out var fontSizeFromStyle))
                annotation.FontSize = fontSizeFromStyle;
            else if (TryExtractFontSizeFromStyleString(dict.Elements.GetString("/RC"), scale, out var fontSizeFromRichText))
                annotation.FontSize = fontSizeFromRichText;

            if (TryExtractColorFromDefaultAppearance(dict.Elements.GetString("/DA"), out var r, out var g, out var b) ||
                TryExtractColorFromStyleString(dict.Elements.GetString("/DS"), out r, out g, out b) ||
                TryExtractColorFromStyleString(dict.Elements.GetString("/RC"), out r, out g, out b) ||
                TryExtractColorFromArray(dict.Elements.GetArray("/C"), out r, out g, out b))
            {
                annotation.R = r;
                annotation.G = g;
                annotation.B = b;
            }

            return annotation;
        }

        private static string ExtractAnnotationText(PdfDictionary dict)
        {
            string contents = NormalizeAnnotationText(dict.Elements.GetString("/Contents"));
            if (!string.IsNullOrWhiteSpace(contents))
                return contents;

            string richText = NormalizeAnnotationText(ConvertRichTextToPlainText(dict.Elements.GetString("/RC")));
            if (!string.IsNullOrWhiteSpace(richText))
                return richText;

            return NormalizeAnnotationText(dict.Elements.GetString("/V"));
        }

        private static string ConvertRichTextToPlainText(string richText)
        {
            if (string.IsNullOrWhiteSpace(richText))
                return string.Empty;

            string normalized = RichTextBreakRegex.Replace(richText, "\n");
            normalized = RichTextTagRegex.Replace(normalized, string.Empty);
            return WebUtility.HtmlDecode(normalized).Trim();
        }

        private static string NormalizeAnnotationText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim('\0');
        }

        private static bool TryExtractFontSizeFromDefaultAppearance(string defaultAppearance, double scale, out double fontSize)
        {
            fontSize = 0;
            if (string.IsNullOrWhiteSpace(defaultAppearance))
                return false;

            var match = DefaultAppearanceFontSizeRegex.Match(defaultAppearance);
            if (!match.Success)
                return false;

            if (!TryParseInvariantDouble(match.Groups["size"].Value, out var parsed))
                return false;

            fontSize = parsed * scale;
            return fontSize > 0;
        }

        private static bool TryExtractFontSizeFromStyleString(string styleText, double scale, out double fontSize)
        {
            fontSize = 0;
            if (string.IsNullOrWhiteSpace(styleText))
                return false;

            var match = CssFontSizeRegex.Match(styleText);
            if (!match.Success)
                match = CssFontRegex.Match(styleText);

            if (!match.Success || !TryParseInvariantDouble(match.Groups["size"].Value, out var parsed))
                return false;

            string unit = match.Groups["unit"].Value;
            fontSize = string.Equals(unit, "px", StringComparison.OrdinalIgnoreCase)
                ? parsed
                : parsed * scale;
            return fontSize > 0;
        }

        private static bool TryExtractColorFromDefaultAppearance(string defaultAppearance, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(defaultAppearance))
                return false;

            var rgbMatch = DefaultAppearanceRgbRegex.Match(defaultAppearance);
            if (rgbMatch.Success &&
                TryParseInvariantDouble(rgbMatch.Groups["r"].Value, out var red) &&
                TryParseInvariantDouble(rgbMatch.Groups["g"].Value, out var green) &&
                TryParseInvariantDouble(rgbMatch.Groups["b"].Value, out var blue))
            {
                r = ToByte(red * 255.0);
                g = ToByte(green * 255.0);
                b = ToByte(blue * 255.0);
                return true;
            }

            var grayMatch = DefaultAppearanceGrayRegex.Match(defaultAppearance);
            if (!grayMatch.Success || !TryParseInvariantDouble(grayMatch.Groups["gray"].Value, out var gray))
                return false;

            byte value = ToByte(gray * 255.0);
            r = value;
            g = value;
            b = value;
            return true;
        }

        private static bool TryExtractColorFromStyleString(string styleText, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(styleText))
                return false;

            var rgbMatch = CssRgbColorRegex.Match(styleText);
            if (rgbMatch.Success &&
                byte.TryParse(rgbMatch.Groups["r"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(rgbMatch.Groups["g"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(rgbMatch.Groups["b"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }

            var hexMatch = CssHexColorRegex.Match(styleText);
            if (!hexMatch.Success)
                return false;

            return TryParseHexColor(hexMatch.Groups["value"].Value, out r, out g, out b);
        }

        private static bool TryExtractColorFromArray(PdfArray colorArray, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (colorArray == null || colorArray.Elements.Count < 3)
                return false;

            r = ToByte(GetDouble(colorArray.Elements[0]) * 255.0);
            g = ToByte(GetDouble(colorArray.Elements[1]) * 255.0);
            b = ToByte(GetDouble(colorArray.Elements[2]) * 255.0);
            return true;
        }

        private static bool TryParseHexColor(string colorText, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(colorText) || colorText[0] != '#')
                return false;

            string hex = colorText.Substring(1);
            if (hex.Length == 3)
                hex = string.Concat(hex.Select(ch => new string(ch, 2)));

            if (hex.Length != 6)
                return false;

            return
                byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        private static bool TryParseInvariantDouble(string rawValue, out double value)
        {
            return double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
        }

        private static double GetDouble(PdfItem item, double defaultValue = 0)
        {
            if (item is PdfReal r) return r.Value;
            if (item is PdfInteger i) return i.Value;
            if (item is PdfReference pref && pref.Value != null) return GetDouble(pref.Value, defaultValue);
            return defaultValue;
        }
    }
}

