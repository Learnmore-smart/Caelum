using Caelum.Models;
using Caelum.Services;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Caelum.Tests;

public class PdfServiceAnnotationSavingTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "CaelumTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, true);
    }

    [Test]
    public async Task SaveAnnotationsToPdfAsync_SavesAnnotationsWithoutEOFError()
    {
        // Create a test PDF
        string filePath = Path.Combine(_tempDirectory, "test.pdf");
        CreateTestPdf(filePath);

        // Create annotations to save
        var annotations = new Dictionary<int, PageAnnotation>();
        var pageAnnots = new PageAnnotation();
        
        // Add a text annotation
        pageAnnots.Texts.Add(new TextAnnotation
        {
            Text = "Test annotation",
            X = 100,
            Y = 100,
            FontSize = 12,
            R = 0,
            G = 0,
            B = 0
        });

        annotations[0] = pageAnnots;

        // Save annotations - this should not throw an exception
        var service = new PdfService();
        await service.SaveAnnotationsToPdfAsync(filePath, annotations);

        // Verify the PDF can be opened again
        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.ReadOnly);
        Assert.That(document.PageCount, Is.EqualTo(1));
    }

    [Test]
    public async Task SaveAnnotationsToPdfAsync_WritesPrintableAppearanceStreams_ForAllAnnotationTypes()
    {
        string filePath = Path.Combine(_tempDirectory, "printable.pdf");
        CreateTestPdf(filePath);

        var annotations = new Dictionary<int, PageAnnotation>();
        var pageAnnots = new PageAnnotation();
        pageAnnots.Texts.Add(new TextAnnotation
        {
            Text = "Printable text",
            X = 100,
            Y = 100,
            FontSize = 16,
            R = 10,
            G = 20,
            B = 30
        });
        pageAnnots.Strokes.Add(new StrokeAnnotation
        {
            R = 20,
            G = 40,
            B = 60,
            Size = 4,
            Points = new List<double[]>
            {
                new[] { 96d, 120d },
                new[] { 144d, 168d }
            }
        });
        pageAnnots.Highlights.Add(new HighlightAnnotation
        {
            R = 255,
            G = 235,
            B = 59,
            A = 128,
            Rects = new List<double[]>
            {
                new[] { 120d, 200d, 60d, 24d }
            }
        });
        annotations[0] = pageAnnots;

        var service = new PdfService();
        await service.SaveAnnotationsToPdfAsync(filePath, annotations);

        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.ReadOnly);
        var annots = document.Pages[0].Elements.GetArray("/Annots");
        Assert.That(annots, Is.Not.Null);
        Assert.That(annots!.Elements.Count, Is.EqualTo(3));

        var dictionaries = annots.Elements.Select(GetAnnotationDictionary).ToList();

        var textAnnot = dictionaries.Single(dict => dict.Elements.GetName("/Subtype") == "/FreeText");
        Assert.That(textAnnot.Elements.GetName("/Type"), Is.EqualTo("/Annot"));
        Assert.That(textAnnot.Elements.GetInteger("/F") & 4, Is.EqualTo(4));
        Assert.That(textAnnot.Elements.GetDictionary("/AP"), Is.Not.Null);

        var inkAnnot = dictionaries.Single(dict => dict.Elements.GetName("/Subtype") == "/Ink");
        Assert.That(inkAnnot.Elements.GetInteger("/F") & 4, Is.EqualTo(4));
        Assert.That(inkAnnot.Elements.GetDictionary("/AP"), Is.Not.Null);

        var highlightAnnot = dictionaries.Single(dict => dict.Elements.GetName("/Subtype") == "/Highlight");
        Assert.That(highlightAnnot.Elements.GetInteger("/F") & 4, Is.EqualTo(4));
        Assert.That(highlightAnnot.Elements.GetDictionary("/AP"), Is.Not.Null);

        var highlightRect = highlightAnnot.Elements.GetRectangle("/Rect");
        Assert.That(highlightRect.X1, Is.EqualTo(90d).Within(0.01));
        Assert.That(highlightRect.Y1, Is.EqualTo(624d).Within(0.01));
        Assert.That(highlightRect.Width, Is.EqualTo(45d).Within(0.01));
        Assert.That(highlightRect.Height, Is.EqualTo(18d).Within(0.01));
    }

    private static PdfDictionary GetAnnotationDictionary(PdfItem item)
    {
        return (item as PdfReference)?.Value as PdfDictionary
            ?? item as PdfDictionary
            ?? throw new InvalidDataException("Annotation entry was not a PDF dictionary.");
    }

    private static void CreateTestPdf(string filePath)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = 612; // 8.5 x 72
        page.Height = 792; // 11 x 72
        document.Save(filePath);
    }
}
