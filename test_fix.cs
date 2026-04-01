using Caelum.Models;
using Caelum.Services;
using System;
using System.Collections.Generic;
using System.IO;

namespace TestAnnotationSave
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                // Create a test PDF
                string tempDir = Path.Combine(Path.GetTempPath(), "CaelumTest");
                Directory.CreateDirectory(tempDir);
                string filePath = Path.Combine(tempDir, "test.pdf");
                
                // Create a blank PDF
                await PdfService.CreateBlankPdfAsync(filePath);
                Console.WriteLine("Created test PDF file");
                
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
                
                // Add a stroke annotation
                var stroke = new StrokeAnnotation
                {
                    R = 255,
                    G = 0,
                    B = 0,
                    A = 255,
                    Size = 2,
                    IsHighlighter = false,
                    Points = new List<double[]>
                    {
                        new double[] { 50, 50 },
                        new double[] { 150, 150 },
                        new double[] { 200, 100 }
                    }
                };
                pageAnnots.Strokes.Add(stroke);
                
                annotations[0] = pageAnnots;
                Console.WriteLine("Created test annotations");
                
                // Save annotations - this should not throw an exception
                var service = new PdfService();
                await service.SaveAnnotationsToPdfAsync(filePath, annotations);
                Console.WriteLine("Successfully saved annotations!");
                
                // Verify the file exists and has content
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    Console.WriteLine($"PDF file saved successfully. Size: {fileInfo.Length} bytes");
                }
                else
                {
                    Console.WriteLine("ERROR: PDF file was not saved");
                }
                
                // Test loading the PDF back
                await service.LoadPdfAsync(filePath);
                Console.WriteLine($"Successfully loaded PDF with {service.PageCount} pages");
                Console.WriteLine($"Extracted {service.ExtractedAnnotations.Count} annotations");
                
                Console.WriteLine("Test completed successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}