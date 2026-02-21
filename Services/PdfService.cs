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

        public int PageCount => _winRtPdfDocument != null ? (int)_winRtPdfDocument.PageCount : 0;

        public async Task LoadPdfAsync(StorageFile file, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                _winRtPdfDocument = null;
                _sourceFile = null;

                var document = await Task.Run(
                    () => WinPdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _winRtPdfDocument = document;
                _sourceFile = file;
            }
            finally
            {
                _documentLock.Release();
            }
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

        public Task<byte[]> RenderPagePngBytesAsync(uint pageIndex, CancellationToken cancellationToken = default)
        {
            if (_winRtPdfDocument == null) return Task.FromResult<byte[]>(null);
            if (pageIndex >= _winRtPdfDocument.PageCount) return Task.FromResult<byte[]>(null);

            return Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _documentLock.WaitAsync(cancellationToken);
                try
                {
                    if (_winRtPdfDocument == null) return null;
                    if (pageIndex >= _winRtPdfDocument.PageCount) return null;

                    using (WinPdfPage page = _winRtPdfDocument.GetPage(pageIndex))
                    {
                        using var stream = new InMemoryRandomAccessStream();
                        await page.RenderToStreamAsync(stream).AsTask(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();

                        var sizeUlong = stream.Size;
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
                        return bytes;
                    }
                }
                finally
                {
                    _documentLock.Release();
                }
            }, cancellationToken);
        }
        
        public async Task SavePdfWithAnnotationsAsync(StorageFile destinationFile, List<(int PageIndex, List<List<System.Drawing.PointF>> InkStrokes, List<(string Text, Windows.Foundation.Rect Rect, Windows.UI.Color Color, double FontSize)> TextAnnotations)> annotations)
        {
            if (_sourceFile == null) return;

            // Copy source to memory stream for modification
            using var sourceStream = await _sourceFile.OpenStreamForReadAsync();
            using var memoryStream = new MemoryStream();
            await sourceStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            // Open with PdfSharpCore
            using (var document = PdfReader.Open(memoryStream, PdfDocumentOpenMode.Modify))
            {
                foreach (var (pageIndex, inkStrokes, textAnnotations) in annotations)
                {
                    if (pageIndex >= document.PageCount) continue;
                    
                    var pdfPage = document.Pages[pageIndex];
                    double pageHeight = pdfPage.Height.Point;
                    
                    // WinUI (96 DPI) to PDF (72 DPI) scale factor
                    // Assumption: WinUI renders PDF at 100% scale which maps 1 PDF point to 1.333 pixels (96 DPI)
                    double scale = 72.0 / 96.0;

                    // Add Text Annotations (FreeText)
                    foreach (var textItem in textAnnotations)
                    {
                        // Convert Rect
                        double x = textItem.Rect.X * scale;
                        double y = pageHeight - ((textItem.Rect.Y + textItem.Rect.Height) * scale); // PDF Origin is Bottom-Left
                        double w = textItem.Rect.Width * scale;
                        double h = textItem.Rect.Height * scale;
                        
                        var rect = new PdfRectangle(new XRect(x, y, w, h));
                        
                        var dict = new PdfDictionary();
                        dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/FreeText");
                        dict.Elements.SetRectangle(PdfAnnotation.Keys.Rect, rect);
                        dict.Elements.SetString(PdfAnnotation.Keys.Contents, textItem.Text); // Contents is key for text
                        
                        // Set Default Appearance (DA) string for font/size/color
                        // "/Helv 12 Tf 0 g" -> Helvetica 12pt, Black
                        // Color in PDF is 0-1 range
                        string da = $"/Helv {textItem.FontSize * scale} Tf {textItem.Color.R/255.0} {textItem.Color.G/255.0} {textItem.Color.B/255.0} rg";
                        dict.Elements.SetString("/DA", da);
                        
                        // Add to annotations array
                        AddAnnotationToPage(pdfPage, dict);
                    }

                    // Add Ink Annotations
                    if (inkStrokes != null && inkStrokes.Any())
                    {
                        var dict = new PdfDictionary();
                        dict.Elements.SetName(PdfAnnotation.Keys.Subtype, "/Ink");
                        dict.Elements.SetRectangle(PdfAnnotation.Keys.Rect, new PdfRectangle(new XRect(0, 0, pdfPage.Width, pdfPage.Height)));

                        var inkListArray = new PdfArray();
                        foreach (var stroke in inkStrokes)
                        {
                            var pointArray = new PdfArray();
                            foreach (var pt in stroke)
                            {
                                pointArray.Elements.Add(new PdfReal(pt.X * scale));
                                pointArray.Elements.Add(new PdfReal(pageHeight - (pt.Y * scale)));
                            }
                            inkListArray.Elements.Add(pointArray);
                        }
                        dict.Elements.Add("/InkList", inkListArray);
                        
                        // Add to annotations array
                        AddAnnotationToPage(pdfPage, dict);
                    }
                }

                // Save to destination
                using var destStream = await destinationFile.OpenStreamForWriteAsync();
                destStream.SetLength(0);
                document.Save(destStream);
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
    }
}
