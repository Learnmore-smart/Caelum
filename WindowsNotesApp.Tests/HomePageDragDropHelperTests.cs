using System.Threading;
using System.Windows;
using Caelum.Pages;

namespace WindowsNotesApp.Tests
{
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public class HomePageDragDropHelperTests
    {
        [Test]
        public void GetLibraryTilePaths_PrefersMultiSelectPayloadAndDeduplicates()
        {
            var data = new DataObject();
            data.SetData(HomePageDragDropHelper.LibraryTilePathDataFormat, @"C:\Docs\one.pdf");
            data.SetData(HomePageDragDropHelper.LibraryTilePathsDataFormat, new[]
            {
                @"C:\Docs\one.pdf",
                @"C:\Docs\two.pdf",
                @"C:\Docs\one.pdf"
            });

            var result = HomePageDragDropHelper.GetLibraryTilePaths(data);

            Assert.That(result, Is.EqualTo(new[]
            {
                @"C:\Docs\one.pdf",
                @"C:\Docs\two.pdf"
            }));
        }

        [Test]
        public void HasSupportedFolderDropPayload_ReturnsTrue_ForInternalTileDrag()
        {
            var data = new DataObject();
            data.SetData(HomePageDragDropHelper.LibraryTilePathDataFormat, @"C:\Docs\one.pdf");

            Assert.That(HomePageDragDropHelper.HasSupportedFolderDropPayload(data), Is.True);
        }

        [Test]
        public void HasSupportedFolderDropPayload_ReturnsTrue_ForExternalPdfDrop()
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { @"C:\Docs\one.pdf", @"C:\Docs\two.txt" });

            Assert.That(HomePageDragDropHelper.HasSupportedFolderDropPayload(data), Is.True);
            Assert.That(HomePageDragDropHelper.GetDroppedPdfPaths(data), Is.EqualTo(new[] { @"C:\Docs\one.pdf" }));
        }

        [Test]
        public void HasSupportedFolderDropPayload_ReturnsFalse_WhenNoSupportedFilesExist()
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { @"C:\Docs\one.txt" });

            Assert.That(HomePageDragDropHelper.HasSupportedFolderDropPayload(data), Is.False);
        }
    }
}
