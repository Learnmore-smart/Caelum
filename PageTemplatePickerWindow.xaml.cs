using System;
using System.Windows;
using System.Windows.Interop;
using Caelum.Models;
using Caelum.Services;
using Forms = System.Windows.Forms;

namespace Caelum
{
    public partial class PageTemplatePickerWindow : Window
    {
        private readonly bool _isNotebookCreationMode;

        public PageInsertTemplate SelectedTemplate { get; private set; } = PageInsertTemplate.Blank;
        public string SelectedFolderPath { get; private set; } = string.Empty;

        public PageTemplatePickerWindow() : this(false, null)
        {
        }

        public PageTemplatePickerWindow(bool notebookCreationMode, string initialFolderPath = null)
        {
            _isNotebookCreationMode = notebookCreationMode;
            SelectedFolderPath = string.IsNullOrWhiteSpace(initialFolderPath)
                ? GetDefaultFolderPath()
                : initialFolderPath;

            InitializeComponent();
            ApplyLocalization();
            UpdateModeVisualState();
            UpdateSelectionVisualState();
            UpdateFolderPathText();
        }

        private void ApplyLocalization()
        {
            Title = _isNotebookCreationMode
                ? LocalizationService.Get("Home.CreateNotebookDialogTitle")
                : LocalizationService.Get("Editor.InsertPageDialogTitle");
            TitleTextBlock.Text = Title;
            SubtitleTextBlock.Text = _isNotebookCreationMode
                ? LocalizationService.Get("Home.CreateNotebookDialogSubtitle")
                : LocalizationService.Get("Editor.InsertPageDialogSubtitle");

            BlankTitleTextBlock.Text = LocalizationService.Get("Editor.PageTemplateBlank");
            BlankHintTextBlock.Text = LocalizationService.Get("Editor.PageTemplateBlankHint");

            NotebookTitleTextBlock.Text = LocalizationService.Get("Editor.PageTemplateNotebook");
            NotebookHintTextBlock.Text = LocalizationService.Get("Editor.PageTemplateNotebookHint");

            LinedTitleTextBlock.Text = LocalizationService.Get("Editor.PageTemplateLined");
            LinedHintTextBlock.Text = LocalizationService.Get("Editor.PageTemplateLinedHint");

            QuadrilleTitleTextBlock.Text = LocalizationService.Get("Editor.PageTemplateQuadrille");
            QuadrilleHintTextBlock.Text = LocalizationService.Get("Editor.PageTemplateQuadrilleHint");

            PathLabelTextBlock.Text = LocalizationService.Get("Home.CreateNotebookPathLabel");
            BrowsePathButton.ToolTip = LocalizationService.Get("Home.CreateNotebookBrowseFolder");
            CreateButton.Content = LocalizationService.Get("Home.CreateNotebookAction");
            CancelButton.Content = LocalizationService.Get("Common.Cancel");
        }

        private void UpdateModeVisualState()
        {
            PathSectionBorder.Visibility = _isNotebookCreationMode ? Visibility.Visible : Visibility.Collapsed;
            CreateButton.Visibility = _isNotebookCreationMode ? Visibility.Visible : Visibility.Collapsed;
            Height = _isNotebookCreationMode ? 660 : 560;
        }

        private void UpdateSelectionVisualState()
        {
            BlankCard.Tag = SelectedTemplate == PageInsertTemplate.Blank;
            NotebookCard.Tag = SelectedTemplate == PageInsertTemplate.Notebook;
            LinedCard.Tag = SelectedTemplate == PageInsertTemplate.Lined;
            QuadrilleCard.Tag = SelectedTemplate == PageInsertTemplate.Quadrille;
        }

        private void UpdateFolderPathText()
        {
            SelectedFolderPathTextBlock.Text = SelectedFolderPath ?? string.Empty;
            SelectedFolderPathTextBlock.ToolTip = SelectedFolderPath ?? string.Empty;
            CreateButton.IsEnabled = !_isNotebookCreationMode || !string.IsNullOrWhiteSpace(SelectedFolderPath);
        }

        private void SelectTemplate(PageInsertTemplate template)
        {
            SelectedTemplate = template;
            UpdateSelectionVisualState();

            if (!_isNotebookCreationMode)
                DialogResult = true;
        }

        private void BlankCard_Click(object sender, RoutedEventArgs e) => SelectTemplate(PageInsertTemplate.Blank);
        private void NotebookCard_Click(object sender, RoutedEventArgs e) => SelectTemplate(PageInsertTemplate.Notebook);
        private void LinedCard_Click(object sender, RoutedEventArgs e) => SelectTemplate(PageInsertTemplate.Lined);
        private void QuadrilleCard_Click(object sender, RoutedEventArgs e) => SelectTemplate(PageInsertTemplate.Quadrille);

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BrowsePathButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isNotebookCreationMode)
                return;

            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = LocalizationService.Get("Home.CreateNotebookBrowseFolder"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = SelectedFolderPath ?? string.Empty
            };

            var ownerHandle = new WindowInteropHelper(this).Handle;
            var result = ownerHandle != IntPtr.Zero
                ? dialog.ShowDialog(new Win32Window(ownerHandle))
                : dialog.ShowDialog();

            if (result != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
                return;

            SelectedFolderPath = dialog.SelectedPath;
            UpdateFolderPathText();
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isNotebookCreationMode && !string.IsNullOrWhiteSpace(SelectedFolderPath))
                DialogResult = true;
        }

        private static string GetDefaultFolderPath()
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documentsPath))
                return documentsPath;

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrWhiteSpace(desktopPath) ? string.Empty : desktopPath;
        }

        private sealed class Win32Window : Forms.IWin32Window
        {
            public Win32Window(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}
