using Caelum.Services;

namespace Caelum.Pages
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
            if (PenToolButton != null)
                PenToolButton.ToolTip = LocalizationService.Get("Editor.PenTooltip");
            if (HighlighterToolButton != null)
                HighlighterToolButton.ToolTip = LocalizationService.Get("Editor.HighlighterTooltip");
            if (EraserToolButton != null)
                EraserToolButton.ToolTip = LocalizationService.Get("Editor.EraserTooltip");
            if (TextToolButton != null)
                TextToolButton.ToolTip = LocalizationService.Get("Editor.TextTooltip");
            if (SelectToolButton != null)
                SelectToolButton.ToolTip = LocalizationService.Get("Editor.SelectTooltip");
            if (SavePdfButton != null)
                SavePdfButton.ToolTip = LocalizationService.Get("Editor.SaveTooltip");
            if (ZoomOutButton != null)
                ZoomOutButton.ToolTip = LocalizationService.Get("Editor.ZoomOutTooltip");
            if (ZoomInButton != null)
                ZoomInButton.ToolTip = LocalizationService.Get("Editor.ZoomInTooltip");
            if (ZoomLabel != null)
                ZoomLabel.ToolTip = LocalizationService.Get("Editor.ZoomEditTooltip");

            CloseToolPopups();
            CreateToolPopups();
        }

        private string GetLocalizedToolName(ToolType tool)
        {
            return tool switch
            {
                ToolType.Pen => LocalizationService.Get("Editor.ModePen"),
                ToolType.Highlighter => LocalizationService.Get("Editor.ModeHighlighter"),
                ToolType.Eraser => LocalizationService.Get("Editor.ModeEraser"),
                ToolType.Text => LocalizationService.Get("Editor.ModeText"),
                ToolType.Select => LocalizationService.Get("Editor.ModeSelect"),
                _ => LocalizationService.Get("Editor.ModeSelect")
            };
        }
    }
}