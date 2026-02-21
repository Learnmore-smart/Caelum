using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsNotesApp.Pages
{
    public sealed class HomeTileTemplateSelector : DataTemplateSelector
    {
        public DataTemplate AddTileTemplate { get; set; }

        public DataTemplate FileTileTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is HomeTile tile && tile.IsAddTile && AddTileTemplate != null)
            {
                return AddTileTemplate;
            }

            return FileTileTemplate ?? AddTileTemplate ?? base.SelectTemplateCore(item);
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
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

            return base.SelectTemplateCore(item, container);
        }
    }
}
