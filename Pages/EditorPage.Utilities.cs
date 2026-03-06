using WindowsNotesApp.Services;

namespace WindowsNotesApp.Pages
{
    public sealed partial class EditorPage
    {
        public bool IsSelectionMode => _currentTool == ToolType.None;

        public void ToggleSelectionMode()
        {
            if (IsSelectionMode)
            {
                var fallbackTool = _previousTool == ToolType.None ? ToolType.Pen : _previousTool;
                ActivateTool(fallbackTool);
                return;
            }

            _previousTool = _currentTool == ToolType.None ? ToolType.Pen : _currentTool;
            ActivateTool(ToolType.None);
        }

        public void ApplyLocalization()
        {
            if (LoadingText != null)
                LoadingText.Text = LocalizationService.Get("Editor.Loading");
            if (UndoButton != null)
                UndoButton.ToolTip = LocalizationService.Get("Editor.UndoTooltip");
            if (RedoButton != null)
                RedoButton.ToolTip = LocalizationService.Get("Editor.RedoTooltip");
            if (SavePdfButton != null)
                SavePdfButton.ToolTip = LocalizationService.Get("Editor.SaveTooltip");
            if (ZoomOutButton != null)
                ZoomOutButton.ToolTip = LocalizationService.Get("Editor.ZoomOutTooltip");
            if (ZoomInButton != null)
                ZoomInButton.ToolTip = LocalizationService.Get("Editor.ZoomInTooltip");
            if (ZoomLabel != null)
                ZoomLabel.ToolTip = LocalizationService.Get("Editor.ZoomEditTooltip");
        }
    }
}
