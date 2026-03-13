using Caelum.Models;
using Caelum.Services;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.IO;

namespace Caelum.Tests;

public class PdfServicePageEditingTests
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
    public async Task InsertPageAsync_InsertsAtRequestedIndex_AndKeepsNeighborPageSizes()
    {
        string filePath = Path.Combine(_tempDirectory, "insert.pdf");
        CreatePdf(filePath, (200, 300), (410, 520));

        var service = new PdfService();

        await service.InsertPageAsync(filePath, 1, PageInsertTemplate.Notebook);

        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.ReadOnly);

        Assert.That(document.PageCount, Is.EqualTo(3));
        Assert.That(document.Pages[0].Width.Point, Is.EqualTo(200).Within(0.01));
        Assert.That(document.Pages[1].Width.Point, Is.EqualTo(410).Within(0.01));
        Assert.That(document.Pages[1].Height.Point, Is.EqualTo(520).Within(0.01));
        Assert.That(document.Pages[2].Width.Point, Is.EqualTo(410).Within(0.01));
    }

    [Test]
    public async Task DeletePageAsync_RemovesRequestedPage_AndLeavesRemainingPages()
    {
        string filePath = Path.Combine(_tempDirectory, "delete.pdf");
        CreatePdf(filePath, (210, 320), (330, 440), (470, 580));

        var service = new PdfService();

        await service.DeletePageAsync(filePath, 1);

        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.ReadOnly);

        Assert.That(document.PageCount, Is.EqualTo(2));
        Assert.That(document.Pages[0].Width.Point, Is.EqualTo(210).Within(0.01));
        Assert.That(document.Pages[1].Width.Point, Is.EqualTo(470).Within(0.01));
        Assert.That(document.Pages[1].Height.Point, Is.EqualTo(580).Within(0.01));
    }

    [Test]
    public void DeletePageAsync_Throws_WhenRemovingTheLastPage()
    {
        string filePath = Path.Combine(_tempDirectory, "single.pdf");
        CreatePdf(filePath, (200, 300));

        var service = new PdfService();

        Assert.That(async () => await service.DeletePageAsync(filePath, 0), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task CreateBlankPdfAsync_CreatesSinglePageUsingRequestedTemplate()
    {
        string filePath = Path.Combine(_tempDirectory, "created.pdf");

        await PdfService.CreateBlankPdfAsync(filePath, widthPoints: 320, heightPoints: 500, template: PageInsertTemplate.Notebook);

        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.ReadOnly);

        Assert.That(document.PageCount, Is.EqualTo(1));
        Assert.That(document.Pages[0].Width.Point, Is.EqualTo(320).Within(0.01));
        Assert.That(document.Pages[0].Height.Point, Is.EqualTo(500).Within(0.01));
        Assert.That(document.Pages[0].Contents.Elements.Count, Is.GreaterThan(0));
    }

    private static void CreatePdf(string filePath, params (double Width, double Height)[] pageSizes)
    {
        using var document = new PdfDocument();

        foreach (var (width, height) in pageSizes)
        {
            var page = document.AddPage();
            page.Width = width;
            page.Height = height;
        }

        document.Save(filePath);
    }
}
