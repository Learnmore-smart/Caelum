using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace Caelum.Pages
{
    internal static class HomePageDragDropHelper
    {
        internal const string LibraryTilePathDataFormat = "Caelum.LibraryTilePath";
        internal const string LibraryTilePathsDataFormat = "Caelum.LibraryTilePaths";

        internal static string[] GetLibraryTilePaths(IDataObject data)
        {
            if (data == null)
                return Array.Empty<string>();

            if (data.GetDataPresent(LibraryTilePathsDataFormat) &&
                data.GetData(LibraryTilePathsDataFormat) is string[] filePaths)
            {
                return NormalizePaths(filePaths);
            }

            if (data.GetDataPresent(LibraryTilePathDataFormat) &&
                data.GetData(LibraryTilePathDataFormat) is string filePath)
            {
                return NormalizePaths(new[] { filePath });
            }

            return Array.Empty<string>();
        }

        internal static string[] GetDroppedPdfPaths(IDataObject data)
        {
            if (data == null ||
                !data.GetDataPresent(DataFormats.FileDrop) ||
                data.GetData(DataFormats.FileDrop) is not string[] files)
            {
                return Array.Empty<string>();
            }

            return NormalizePaths(files.Where(IsPdfFile));
        }

        internal static bool HasSupportedFolderDropPayload(IDataObject data)
        {
            return GetLibraryTilePaths(data).Length > 0 ||
                   GetDroppedPdfPaths(data).Length > 0;
        }

        private static string[] NormalizePaths(System.Collections.Generic.IEnumerable<string> paths)
        {
            return (paths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsPdfFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
        }
    }
}
