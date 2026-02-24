using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Annotations;
using PdfSharpCore.Pdf.Advanced;
using System.Linq;

// Alias to resolve ambiguity
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPage = Windows.Data.Pdf.PdfPage;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;
using SharpPdfPage = PdfSharpCore.Pdf.PdfPage;

namespace WindowsNotesApp.Services
{
    public class PdfService
    {
        private readonly SemaphoreSlim _documentLock = new SemaphoreSlim(1, 1);
        private WinPdfDocument _winRtPdfDocument;
        private StorageFile _sourceFile;
        private IRandomAccessStream _keepAliveStream;

        public int PageCount => _winRtPdfDocument != null ? (int)_winRtPdfDocument.PageCount : 0;
        public Dictionary<int, Models.PageAnnotation> ExtractedAnnotations { get; private set; } = new();

        public async Task LoadPdfAsync(StorageFile file, CancellationToken cancellationToken = default)
        {
            await LoadPdfCoreAsync(file: file, filePath: null, cancellationToken);
        }

        public async Task LoadPdfAsync(string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                if (file != null)
                {
                    await LoadPdfCoreAsync(file: file, filePath: filePath, cancellationToken);
                    return;
                }
            }
            catch
            {
                // Fallback
            }

            await LoadPdfCoreAsync(file: null, filePath: filePath, cancellationToken);
        }

        private async Task LoadPdfCoreAsync(StorageFile file, string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                _winRtPdfDocument = null;
                _keepAliveStream?.Dispose();
                _keepAliveStream = null;
                _sourceFile = file;

                byte[] fileBytes = null;
                if (file != null)
                {
                    System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: reading via StorageFile");
                    using (var ras = await file.OpenReadAsync())
                    {
                        using (var stream = ras.AsStreamForRead())
                        {
                            using (var ms = new MemoryStream())
                            {
                                await stream.CopyToAsync(ms);
                                fileBytes = ms.ToArray();
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(filePath))
                {
                    System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: reading via path: {filePath}");
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists) throw new FileNotFoundException($"PDF file not found: {filePath}");
                    if (fileInfo.Length == 0) throw new InvalidOperationException($"PDF file is empty: {filePath}");
                    fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                }

                if (fileBytes != null)
                {
                    System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: read {fileBytes.Length} bytes");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 1. Try to extract and strip annotations (only if we have bytes)
                byte[] strippedBytes = null;
                if (fileBytes != null && fileBytes.Length > 0)
                {
                    try
                    {
                        using (var sourceMs = new MemoryStream(fileBytes))
                        {
                            using (var strippedMs = new MemoryStream())
                            {
                                ExtractAndStripAnnotations(sourceMs, strippedMs);
                                strippedBytes = strippedMs.ToArray();
                            }
                        }
                        System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: annotation stripping produced {strippedBytes.Length} bytes");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: annotation stripping failed: {ex.Message}");
                        ExtractedAnnotations.Clear();
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();

                // 2. Try loading with multiple strategies
                // Priority: stripped bytes (in memory) > original bytes (in memory) > Direct StorageFile stream > StorageFile from path
                WinPdfDocument loadedDoc = null;
                IRandomAccessStream keepAlive = null;
                Exception lastException = null;

                // Attempt A: stripped bytes (if annotation stripping succeeded)
                if (loadedDoc == null && strippedBytes != null && strippedBytes.Length > 0)
                {
                    IRandomAccessStream rasA = null;
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt A - loading stripped bytes");
                        rasA = new InMemoryRandomAccessStream();
                        await WriteBytesToStreamAsync(rasA, strippedBytes);
                        loadedDoc = await WinPdfDocument.LoadFromStreamAsync(rasA).AsTask(cancellationToken);
                        keepAlive = rasA;
                        rasA = null; // Prevent disposal
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt A succeeded");
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        string cName = ex.GetType().Name;
                        System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: attempt A failed: {cName}: {ex.Message}");
                        ExtractedAnnotations.Clear();
                    }
                    finally
                    {
                        rasA?.Dispose();
                    }
                }

                // Attempt B: original bytes
                if (loadedDoc == null && fileBytes != null && fileBytes.Length > 0)
                {
                    IRandomAccessStream rasB = null;
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt B - loading original bytes");
                        rasB = new InMemoryRandomAccessStream();
                        await WriteBytesToStreamAsync(rasB, fileBytes);
                        loadedDoc = await WinPdfDocument.LoadFromStreamAsync(rasB).AsTask(cancellationToken);
                        keepAlive = rasB;
                        rasB = null; // Prevent disposal
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt B succeeded");
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        string cName = ex.GetType().Name;
                        System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: attempt B failed: {cName}: {ex.Message}");
                    }
                    finally
                    {
                        rasB?.Dispose();
                    }
                }

                // Attempt C: Direct StorageFile stream (Fallback)
                if (loadedDoc == null && file != null)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt C - loading via direct StorageFile stream");
                        var directRas = await file.OpenReadAsync();
                        loadedDoc = await WinPdfDocument.LoadFromStreamAsync(directRas).AsTask(cancellationToken);
                        keepAlive = directRas;
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt C succeeded");
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        string cName = ex.GetType().Name;
                        System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: attempt C failed: {cName}: {ex.Message}");
                    }
                }

                // Attempt D: StorageFile from path (Fallback)
                if (loadedDoc == null && file == null && !string.IsNullOrEmpty(filePath))
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt D - loading via StorageFile from path");
                        var sf = await StorageFile.GetFileFromPathAsync(filePath);
                        var directRas = await sf.OpenReadAsync();
                        loadedDoc = await WinPdfDocument.LoadFromStreamAsync(directRas).AsTask(cancellationToken);
                        keepAlive = directRas;
                        _sourceFile = sf;
                        System.Diagnostics.Debug.WriteLine("LoadPdfCoreAsync: attempt D succeeded");
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        string cName = ex.GetType().Name;
                        System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: attempt D failed: {cName}: {ex.Message}");
                    }
                }

                if (loadedDoc == null)
                {
                    var errorMsg = lastException != null
                        ? $"Unable to load this PDF: {lastException.Message}"
                        : "Unable to load this PDF. The file may be corrupted, encrypted, or unsupported.";
                    throw new InvalidOperationException(errorMsg);
                }

                System.Diagnostics.Debug.WriteLine($"LoadPdfCoreAsync: loaded successfully, {loadedDoc.PageCount} pages");
                _winRtPdfDocument = loadedDoc;
                _keepAliveStream = keepAlive;
            }
            finally
            {
                _documentLock.Release();
            }
        }

        private static async Task WriteBytesToStreamAsync(IRandomAccessStream ras, byte[] bytes)
        {
            using (var writer = new DataWriter(ras))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ras.Seek(0);
        }

        private void ExtractAndStripAnnotations(Stream sourceStream, Stream outputStream)
        {
            ExtractedAnnotations.Clear();
            double scale = 96.0 / 72.0;

            using var document = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Modify);

            for (int i = 0; i < document.PageCount; i++)
            {
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

                                // Parse DA for color/size (naive fallback)
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

                                // Only remove annotations we can edit (or all if we want full takeover)
                                elementsToRemove.Add(annotItem);
                            }
                            else if (subtype == "/Ink")
                            {
                                var inkList = dict.Elements.GetArray("/InkList");
                                if (inkList != null && inkList.Elements.Count > 0)
                                {
                                    // Each element in InkList is a separate stroke
                                    foreach (var strokeItem in inkList.Elements)
                                    {
                                        var pointArray = (strokeItem as PdfReference)?.Value as PdfArray ?? strokeItem as PdfArray;
                                        if (pointArray != null && pointArray.Elements.Count >= 2)
                                        {
                                            var strokeAnnot = new Models.StrokeAnnotation();

                                            // Color
                                            var cArray = dict.Elements.GetArray("/C");
                                            if (cArray != null && cArray.Elements.Count >= 3)
                                            {
                                                strokeAnnot.R = (byte)(GetDouble(cArray.Elements[0]) * 255);
                                                strokeAnnot.G = (byte)(GetDouble(cArray.Elements[1]) * 255);
                                                strokeAnnot.B = (byte)(GetDouble(cArray.Elements[2]) * 255);
                                            }

                                            // Opacity / Highlighter
                                            double ca = dict.Elements.ContainsKey("/CA") ? GetDouble(dict.Elements["/CA"], 1.0) : 1.0;
                                            strokeAnnot.A = (byte)(ca * 255);
                                            strokeAnnot.IsHighlighter = ca < 1.0;

                                            // Size
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
                    ExtractedAnnotations[i] = pageAnnots;
                }
            }

            document.Save(outputStream);
        }

        public async Task<BitmapImage> RenderPageAsync(uint pageIndex, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                if (_winRtPdfDocument == null) return null;
                if (pageIndex >= _winRtPdfDocument.PageCount) return null;

                using (WinPdfPage page = _winRtPdfDocument.GetPage(pageIndex))
                {
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        await page.RenderToStreamAsync(stream).AsTask(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.Seek(0);

                        var image = new BitmapImage();
                        await image.SetSourceAsync(stream);
                        return image;
                    }
                }
            }
            finally
            {
                _documentLock.Release();
            }
        }

        public async Task<byte[]> RenderPagePngBytesAsync(uint pageIndex, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: start for page {pageIndex}");

            if (_winRtPdfDocument == null)
            {
                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: _winRtPdfDocument is null");
                return null;
            }
            if (pageIndex >= _winRtPdfDocument.PageCount)
            {
                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: pageIndex {pageIndex} >= PageCount {_winRtPdfDocument.PageCount}");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                if (_winRtPdfDocument == null)
                {
                    System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: _winRtPdfDocument is null after lock");
                    return null;
                }
                if (pageIndex >= _winRtPdfDocument.PageCount)
                {
                    System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: pageIndex check failed after lock");
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: getting page {pageIndex}");
                using (WinPdfPage page = _winRtPdfDocument.GetPage(pageIndex))
                {
                    System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: page {pageIndex} obtained, rendering...");
                    using var stream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(stream).AsTask(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var sizeUlong = stream.Size;
                    System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: rendered, stream size = {sizeUlong}");

                    if (sizeUlong > uint.MaxValue)
                    {
                        throw new InvalidOperationException("Rendered page stream is too large.");
                    }

                    var size = (uint)sizeUlong;
                    stream.Seek(0);

                    using var input = stream.GetInputStreamAt(0);
                    using var reader = new DataReader(input);
                    await reader.LoadAsync(size).AsTask(cancellationToken);

                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);
                    System.Diagnostics.Debug.WriteLine($"RenderPagePngBytesAsync: returning {bytes.Length} bytes");
                    return bytes;
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

        public async Task SaveAnnotationsToPdfAsync(string filePath, Dictionary<int, Models.PageAnnotation> annotations)
        {
            // Lock to ensure we don't read/write simultaneously
            await _documentLock.WaitAsync();
            try
            {
                // 1. Release locks on the current file so we can overwrite it
                _winRtPdfDocument = null;
                _keepAliveStream?.Dispose();
                _keepAliveStream = null;

                // 2. Read the original bytes
                if (!File.Exists(filePath)) throw new FileNotFoundException("PDF to save not found.");
                var fileBytes = await File.ReadAllBytesAsync(filePath);
                using var sourceMs = new MemoryStream(fileBytes);

                // 3. Apply annotations
                using (var document = PdfReader.Open(sourceMs, PdfDocumentOpenMode.Modify))
                {
                    double scale = 72.0 / 96.0;

                    for (int i = 0; i < document.PageCount; i++)
                    {
                        var pdfPage = document.Pages[i];
                        double pageHeight = pdfPage.Height.Point;

                        // First remove all existing FreeText/Ink to replace with current state
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
                                    if (sub == "/FreeText" || sub == "/Ink") toRemove.Add(item);
                                }
                            }
                            foreach (var item in toRemove) annots.Elements.Remove(item);
                        }

                        if (annotations.TryGetValue(i, out var pageAnnots))
                        {
                            // Add Text
                            foreach (var textItem in pageAnnots.Texts)
                            {
                                double w = 300 * scale; // Approximate width if we don't store it
                                double h = (textItem.FontSize * 1.5) * scale;
                                double x = textItem.X * scale;
                                double y = pageHeight - (textItem.Y * scale) - h;

                                var rect = new PdfRectangle(new XRect(x, y, w, h));
                                var dict = new PdfDictionary();
                                dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/FreeText");
                                dict.Elements.SetRectangle(PdfAnnotation.Keys.Rect, rect);
                                dict.Elements.SetString(PdfAnnotation.Keys.Contents, textItem.Text);
                                dict.Elements.SetString("/NM", $"wna_text_{Guid.NewGuid()}");

                                string da = $"/Helv {textItem.FontSize * scale} Tf {textItem.R/255.0} {textItem.G/255.0} {textItem.B/255.0} rg";
                                dict.Elements.SetString("/DA", da);

                                AddAnnotationToPage(pdfPage, dict);
                            }

                            // Add Strokes
                            foreach (var stroke in pageAnnots.Strokes)
                            {
                                if (stroke.Points.Count == 0) continue;

                                var dict = new PdfDictionary();
                                dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/Ink");
                                dict.Elements.SetRectangle(PdfAnnotation.Keys.Rect, new PdfRectangle(new XRect(0, 0, pdfPage.Width, pdfPage.Height)));
                                dict.Elements.SetString("/NM", $"wna_ink_{Guid.NewGuid()}");

                                // Color
                                var colorArray = new PdfArray();
                                colorArray.Elements.Add(new PdfReal(stroke.R / 255.0));
                                colorArray.Elements.Add(new PdfReal(stroke.G / 255.0));
                                colorArray.Elements.Add(new PdfReal(stroke.B / 255.0));
                                dict.Elements.Add("/C", colorArray);

                                // Opacity
                                if (stroke.A < 255 || stroke.IsHighlighter)
                                {
                                    dict.Elements.SetReal("/CA", stroke.IsHighlighter ? 0.5 : stroke.A / 255.0);
                                }

                                // Size
                                var bsDict = new PdfDictionary();
                                bsDict.Elements.SetName("/Type", "/Border");
                                bsDict.Elements.SetReal("/W", stroke.Size * scale);
                                dict.Elements.Add("/BS", bsDict);

                                // Points
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
                    }

                    // 4. Save to temp stream
                    using var outStream = new MemoryStream();
                    document.Save(outStream);

                    // 5. Overwrite original file
                    try
                    {
                        await File.WriteAllBytesAsync(filePath, outStream.ToArray());
                    }
                    catch (IOException)
                    {
                        // Fallback: force garbage collection to release any unmanaged file handles or memory maps.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        await File.WriteAllBytesAsync(filePath, outStream.ToArray());
                    }
                }
            }
            finally
            {
                _documentLock.Release();
            }
        }

        private void AddAnnotationToPage(SharpPdfPage page, PdfDictionary annotation)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots == null)
            {
                annots = new PdfArray(page.Owner);
                page.Elements.Add("/Annots", annots);
            }
            annots.Elements.Add(annotation);
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
