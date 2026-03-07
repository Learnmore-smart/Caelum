using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                                var contents = dict.Elements.GetString("/Contents") ?? "";
                                var rect = dict.Elements.GetRectangle("/Rect");

                                double x_ui = rect.X1 * scale;
                                double w_ui = rect.Width * scale;
                                double h_ui = rect.Height * scale;
                                double y_ui = (pageHeight - rect.Y1 - rect.Height) * scale;

                                double fontSize = 18;
                                byte r = 0, g = 0, b = 0;
                                string da = dict.Elements.GetString("/DA") ?? "";
                                var parts = da.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                for (int j = 0; j < parts.Length; j++)
                                {
                                    if (parts[j] == "Tf" && j >= 1 && double.TryParse(parts[j-1], out double fs))
                                        fontSize = fs * scale;
                                    if (parts[j] == "rg" && j >= 3)
                                    {
                                        if (double.TryParse(parts[j-3], out double rD)) r = (byte)(rD * 255);
                                        if (double.TryParse(parts[j-2], out double gD)) g = (byte)(gD * 255);
                                        if (double.TryParse(parts[j-1], out double bD)) b = (byte)(bD * 255);
                                    }
                                }

                                pageAnnots.Texts.Add(new Models.TextAnnotation
                                {
                                    Text = contents,
                                    X = x_ui,
                                    Y = y_ui,
                                    FontSize = fontSize,
                                    R = r, G = g, B = b
                                });

                                elementsToRemove.Add(annotItem);
                            }
                            else if (subtype == "/Ink")
                            {
                                var inkList = dict.Elements.GetArray("/InkList");
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
                                                pageAnnots.Strokes.Add(strokeAnnot);
                                        }
                                    }
                                }
                                elementsToRemove.Add(annotItem);
                            }
                        }
                    }

                    foreach (var item in elementsToRemove)
                    {
                        annots.Elements.Remove(item);
                    }
                }

                if (pageAnnots.Strokes.Count > 0 || pageAnnots.Texts.Count > 0)
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
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var document = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Modify))
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
                                if (sub == "/FreeText" || sub == "/Ink")
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
                        double w = 300 * scale;
                        double h = (textItem.FontSize * 1.5) * scale;
                        double x = textItem.X * scale;
                        double y = pageHeight - (textItem.Y * scale) - h;

                        var rect = new PdfSharpPdfRectangle(new XRect(x, y, w, h));
                        var dict = new PdfDictionary(document);
                        dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/FreeText");
                        dict.Elements.SetRectangle(PdfAnnotation.Keys.Rect, rect);
                        dict.Elements.SetString(PdfAnnotation.Keys.Contents, textItem.Text);
                        dict.Elements.SetString("/NM", $"wna_text_{Guid.NewGuid()}");

                        string da = $"/Helv {textItem.FontSize * scale} Tf {textItem.R / 255.0} {textItem.G / 255.0} {textItem.B / 255.0} rg";
                        dict.Elements.SetString("/DA", da);

                        AddAnnotationToPage(pdfPage, dict);
                    }

                    foreach (var stroke in pageAnnots.Strokes)
                    {
                        if (stroke.Points.Count == 0) continue;

                        var dict = new PdfDictionary(document);
                        dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/Ink");
                        dict.Elements.SetRectangle(PdfAnnotation.Keys.Rect, new PdfSharpPdfRectangle(new XRect(0, 0, pdfPage.Width, pdfPage.Height)));
                        dict.Elements.SetString("/NM", $"wna_ink_{Guid.NewGuid()}");

                        var colorArray = new PdfArray();
                        colorArray.Elements.Add(new PdfReal(stroke.R / 255.0));
                        colorArray.Elements.Add(new PdfReal(stroke.G / 255.0));
                        colorArray.Elements.Add(new PdfReal(stroke.B / 255.0));
                        dict.Elements.Add("/C", colorArray);

                        if (stroke.A < 255 || stroke.IsHighlighter)
                            dict.Elements.SetReal("/CA", stroke.IsHighlighter ? 0.5 : stroke.A / 255.0);

                        var bsDict = new PdfDictionary();
                        bsDict.Elements.SetName("/Type", "/Border");
                        bsDict.Elements.SetReal("/W", stroke.Size * scale);
                        dict.Elements.Add("/BS", bsDict);

                        var inkListArray = new PdfArray();
                        var pointArray = new PdfArray();
                        foreach (var pt in stroke.Points)
                        {
                            pointArray.Elements.Add(new PdfReal(pt[0] * scale));
                            pointArray.Elements.Add(new PdfReal(pageHeight - (pt[1] * scale)));
                        }
                        inkListArray.Elements.Add(pointArray);
                        dict.Elements.Add("/InkList", inkListArray);

                        AddAnnotationToPage(pdfPage, dict);
                    }
                }

                using (var outputStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    document.Save(outputStream);
                }
                } // sourceStream and document are disposed here, before File.Move

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

        private static double GetDouble(PdfItem item, double defaultValue = 0)
        {
            if (item is PdfReal r) return r.Value;
            if (item is PdfInteger i) return i.Value;
            if (item is PdfReference pref && pref.Value != null) return GetDouble(pref.Value, defaultValue);
            return defaultValue;
        }
    }
}

