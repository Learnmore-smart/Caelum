using Caelum.Models;
using Caelum.Services;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.IO;
using System.Collections.Generic;

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

    private static void CreateTestPdf(string filePath)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = 612; // 8.5 x 72
        page.Height = 792; // 11 x 72
        document.Save(filePath);
    }
}