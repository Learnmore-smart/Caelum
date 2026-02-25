using System.Windows;
using System.Windows.Controls;

namespace WindowsNotesApp.Pages
{
    public sealed class HomeTileTemplateSelector : DataTemplateSelector
    {
        public DataTemplate AddTileTemplate { get; set; }

        public DataTemplate FileTileTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is HomeTile tile && tile.IsAddTile && AddTileTemplate != null)
            {
                return AddTileTemplate;
            }

            if (FileTileTemplate != null)
            {
                return FileTileTemplate;
            }

            if (AddTileTemplate != null)
            {
                return AddTileTemplate;
            }

            return base.SelectTemplate(item, container);
        }
    }
}
