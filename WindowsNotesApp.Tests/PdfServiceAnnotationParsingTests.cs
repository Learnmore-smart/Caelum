using Caelum.Services;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace Caelum.Tests;

public class PdfServiceAnnotationParsingTests
{
    private const double Scale = 96.0 / 72.0;
    private const double PageHeight = 792.0;

    [Test]
    public void TryExtractFreeTextAnnotation_UsesDefaultAppearance_WhenContentsPresent()
    {
        using var document = new PdfDocument();
        var annotation = new PdfDictionary(document);
        annotation.Elements.SetString("/Contents", "Edge note");
        annotation.Elements.SetRectangle("/Rect", new PdfRectangle(new XRect(72, 648, 144, 24)));
        annotation.Elements.SetString("/DA", "/Helv 14 Tf 0.1 0.2 0.3 rg");

        var result = PdfService.TryExtractFreeTextAnnotation(annotation, PageHeight, Scale);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Text, Is.EqualTo("Edge note"));
        Assert.That(result.X, Is.EqualTo(96d).Within(0.001));
        Assert.That(result.Y, Is.EqualTo(160d).Within(0.001));
        Assert.That(result.FontSize, Is.EqualTo(14 * Scale).Within(0.001));
        Assert.That(result.R, Is.EqualTo(26));
        Assert.That(result.G, Is.EqualTo(51));
        Assert.That(result.B, Is.EqualTo(76));
    }

    [Test]
    public void TryExtractFreeTextAnnotation_UsesRichTextAndStyleFallbacks_WhenContentsMissing()
    {
        using var document = new PdfDocument();
        var annotation = new PdfDictionary(document);
        annotation.Elements.SetRectangle("/Rect", new PdfRectangle(new XRect(36, 700, 180, 40)));
        annotation.Elements.SetString("/RC", "<body><p><span style=\"font-size:16pt;color:#336699\">Hello<br/>World</span></p></body>");

        var result = PdfService.TryExtractFreeTextAnnotation(annotation, PageHeight, Scale);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Text, Is.EqualTo("Hello\nWorld"));
        Assert.That(result.FontSize, Is.EqualTo(16 * Scale).Within(0.001));
        Assert.That(result.R, Is.EqualTo(0x33));
        Assert.That(result.G, Is.EqualTo(0x66));
        Assert.That(result.B, Is.EqualTo(0x99));
    }

    [Test]
    public void TryExtractFreeTextAnnotation_ReturnsNull_WhenNoUsableTextExists()
    {
        using var document = new PdfDocument();
        var annotation = new PdfDictionary(document);
        annotation.Elements.SetRectangle("/Rect", new PdfRectangle(new XRect(10, 10, 80, 20)));
        annotation.Elements.SetString("/RC", "<body><p><span style=\"font-size:12pt;color:#000000\"></span></p></body>");

        var result = PdfService.TryExtractFreeTextAnnotation(annotation, PageHeight, Scale);

        Assert.That(result, Is.Null);
    }
}
