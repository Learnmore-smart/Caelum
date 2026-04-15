using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Caelum.Models;

namespace Caelum.Services
{
    public static class LocalizationService
    {
        private static readonly IReadOnlyList<LanguageOption> LanguageOptions = new[]
        {
            new LanguageOption(AppLanguage.English, "English"),
            new LanguageOption(AppLanguage.Chinese, "\u4E2D\u6587"),
            new LanguageOption(AppLanguage.French, "Fran\u00E7ais")
        };

        private static readonly Dictionary<string, (string English, string Chinese, string French)> Strings = new()
        {
            ["Common.Cancel"] = ("Cancel", "\u53D6\u6D88", "Annuler"),
            ["Common.Error"] = ("Error", "\u9519\u8BEF", "Erreur"),
            ["Common.OK"] = ("OK", "\u786E\u5B9A", "OK"),
            ["Common.Save"] = ("Save", "\u4FDD\u5B58", "Enregistrer"),
            ["Editor.AutoSaved"] = ("Auto-saved", "\u5DF2\u81EA\u52A8\u4FDD\u5B58", "Enregistr\u00E9 automatiquement"),
            ["Editor.DeleteTooltip"] = ("Delete", "\u5220\u9664", "Supprimer"),
            ["Editor.EraserTooltip"] = ("Eraser", "\u6A61\u76AE\u64E6", "Gomme"),
            ["Editor.HighlighterTooltip"] = ("Highlighter", "\u8357\u5149\u7B14", "Surligneur"),
            ["Editor.AddPageFailed"] = ("Failed to add a page: {0}", "\u65E0\u6CD5\u6DFB\u52A0\u9875\u9762\uFF1A{0}", "\u00C9chec de l'ajout d'une page : {0}"),
            ["Editor.AddPageTooltip"] = ("Add page", "\u6DFB\u52A0\u9875\u9762", "Ajouter une page"),
            ["Editor.DeletePageFailed"] = ("Failed to delete the page: {0}", "\u65E0\u6CD5\u5220\u9664\u9875\u9762\uFF1A{0}", "\u00C9chec de la suppression de la page : {0}"),
            ["Editor.DeletePageTooltip"] = ("Delete page", "\u5220\u9664\u9875\u9762", "Supprimer la page"),
            ["Editor.InsertPageDialogTitle"] = ("Insert page", "\u63D2\u5165\u9875\u9762", "Ins\u00E9rer une page"),
            ["Editor.InsertPageDialogSubtitle"] = ("Choose the page style to insert at this position.", "\u9009\u62E9\u8981\u63D2\u5165\u5230\u6B64\u5904\u7684\u9875\u9762\u6837\u5F0F\u3002", "Choisissez le style de page \u00E0 ins\u00E9rer \u00E0 cet emplacement."),
            ["Editor.InsertPageHereTooltip"] = ("Insert page here", "\u5728\u6B64\u5904\u63D2\u5165\u9875\u9762", "Ins\u00E9rer une page ici"),
            ["Editor.Loading"] = ("Loading...", "\u52A0\u8F7D\u4E2D...", "Chargement..."),
            ["Editor.ModeEraser"] = ("Eraser", "\u6A61\u76AE\u64E6", "Gomme"),
            ["Editor.ModeHighlighter"] = ("Highlighter", "\u8357\u5149\u7B14", "Surligneur"),
            ["Editor.ModePen"] = ("Pen", "\u753B\u7B14", "Stylo"),
            ["Editor.ModeSelect"] = ("Select", "\u9009\u62E9", "S\u00E9lection"),
            ["Editor.ModeText"] = ("Text", "\u6587\u672C", "Texte"),
            ["Editor.NoDocumentLoaded"] = ("No PDF is currently loaded", "\u5F53\u524D\u6CA1\u6709\u6253\u5F00 PDF", "Aucun PDF n'est ouvert"),
            ["Editor.PageAdded"] = ("Page added", "\u5DF2\u6DFB\u52A0\u9875\u9762", "Page ajout\u00E9e"),
            ["Editor.PageDeleted"] = ("Page deleted", "\u9875\u9762\u5DF2\u5220\u9664", "Page supprim\u00E9e"),
            ["Editor.PageDeleteBlocked"] = ("The document must keep at least one page", "\u6587\u6863\u81F3\u5C11\u9700\u8981\u4FDD\u7559\u4E00\u9875", "Le document doit conserver au moins une page"),
            ["Editor.PageJumpTooltip"] = ("Click to jump to a page", "\u70B9\u51FB\u8DF3\u8F6C\u5230\u6307\u5B9A\u9875", "Cliquer pour aller \u00E0 une page"),
            ["Editor.PageTemplateBlank"] = ("Blank", "\u7A7A\u767D\u9875", "Vierge"),
            ["Editor.PageTemplateBlankHint"] = ("Plain white paper for sketches or mixed notes.", "\u9002\u5408\u7D20\u63CF\u6216\u6DF7\u5408\u8BB0\u5F55\u7684\u7EAF\u767D\u9875\u9762\u3002", "Une feuille blanche simple pour dessiner ou prendre des notes libres."),
            ["Editor.PageTemplateLined"] = ("Lined", "\u6A2A\u7EBF\u9875", "Lign\u00E9e"),
            ["Editor.PageTemplateLinedHint"] = ("Even horizontal rules for clean handwriting.", "\u5747\u5300\u7684\u6A2A\u5411\u7EBF\u6761\uFF0C\u4FBF\u4E8E\u6574\u9F50\u4E66\u5199\u3002", "Des lignes horizontales r\u00E9guli\u00E8res pour une \u00E9criture soign\u00E9e."),
            ["Editor.PageTemplateNotebook"] = ("Notebook", "\u7B14\u8BB0\u672C\u9875", "Carnet"),
            ["Editor.PageTemplateNotebookHint"] = ("Warm paper with a margin line and notebook rules.", "\u5E26\u5DE6\u4FA7\u8FB9\u7EBF\u548C\u7B14\u8BB0\u672C\u6A2A\u7EBF\u7684\u6696\u8272\u9875\u9762\u3002", "Une page chaude avec marge rouge et lignes de carnet."),
            ["Editor.PageTemplateQuadrille"] = ("Quadrille", "\u65B9\u683C\u9875", "Quadrill\u00E9e"),
            ["Editor.PageTemplateQuadrilleHint"] = ("Grid paper for diagrams, layouts, and math.", "\u9002\u5408\u7ED8\u56FE\u3001\u5E03\u5C40\u548C\u7B97\u5F0F\u7684\u65B9\u683C\u7EB8\u3002", "Une grille pour les sch\u00E9mas, les mises en page et les calculs."),
            ["Editor.PenTooltip"] = ("Pen", "\u753B\u7B14", "Stylo"),
            ["Editor.SelectTooltip"] = ("Select and Transform", "\u9009\u62E9\u5E76\u53D8\u6362", "S\u00E9lectionner et transformer"),
            ["Editor.SelectFilter"] = ("Select", "\u9009\u62E9\u5185\u5BB9", "Filtrer"),
            ["Editor.SelectFilterBoth"] = ("Both", "\u5168\u90E8", "Tous"),
            ["Editor.SelectFilterDrawings"] = ("Drawings", "\u56FE\u5F62", "Dessins"),
            ["Editor.SelectFilterText"] = ("Text", "\u6587\u672C", "Texte"),
            ["Editor.SelectShape"] = ("Shape", "\u9009\u62E9\u65B9\u5F0F", "Forme"),
            ["Editor.SelectShapeRect"] = ("Rectangle", "\u77E9\u5F62\u9009\u62E9", "Rectangle"),
            ["Editor.SelectShapeFree"] = ("Freehand", "\u81EA\u7531\u9009\u62E9", "Main lev\u00E9e"),
            ["Editor.PopupColor"] = ("Color", "\u989C\u8272", "Couleur"),
            ["Editor.PopupEraserSize"] = ("Eraser size", "\u6A61\u76AE\u64E6\u5927\u5C0F", "Taille de la gomme"),
            ["Editor.PopupPreview"] = ("Preview", "\u9884\u89C8", "Aper\u00E7u"),
            ["Editor.PopupSize"] = ("Size", "\u5927\u5C0F", "Taille"),
            ["Editor.RedoTooltip"] = ("Redo (Ctrl+Y)", "\u91CD\u505A (Ctrl+Y)", "R\u00E9tablir (Ctrl+Y)"),
            ["Editor.SaveTooltip"] = ("Save", "\u4FDD\u5B58", "Enregistrer"),
            ["Editor.TextTooltip"] = ("Text", "\u6587\u672C", "Texte"),
            ["Editor.UndoTooltip"] = ("Undo (Ctrl+Z)", "\u64A4\u9500 (Ctrl+Z)", "Annuler (Ctrl+Z)"),
            ["Editor.ZoomEditTooltip"] = ("Click to set zoom", "\u70B9\u51FB\u8BBE\u7F6E\u7F29\u653E", "Cliquer pour r\u00E9gler le zoom"),
            ["Editor.ZoomInTooltip"] = ("Zoom in", "\u653E\u5927", "Zoom avant"),
            ["Editor.ZoomOutTooltip"] = ("Zoom out", "\u7F29\u5C0F", "Zoom arri\u00E8re"),
            ["Home.Context.CopyPath"] = ("Copy path", "\u590D\u5236\u8DEF\u5F84", "Copier le chemin"),
            ["Home.Context.Export"] = ("Export copy", "\u5BFC\u51FA\u526F\u672C", "Exporter une copie"),
            ["Home.Context.Open"] = ("Open", "\u6253\u5F00", "Ouvrir"),
            ["Home.Context.OpenFolder"] = ("Open folder", "\u6253\u5F00\u6240\u5728\u6587\u4EF6\u5939", "Ouvrir le dossier"),
            ["Home.Context.MoveToLibrary"] = ("Move to library root", "\u79FB\u5230\u5E93\u6839\u76EE\u5F55", "D\u00E9placer vers la racine de la biblioth\u00E8que"),
            ["Home.Context.Remove"] = ("Remove from library", "\u4ECE\u5E93\u4E2D\u79FB\u9664", "Retirer de la biblioth\u00E8que"),
            ["Home.Context.RemoveFolder"] = ("Remove folder", "\u79FB\u9664\u6587\u4EF6\u5939", "Supprimer le dossier"),
            ["Home.Context.Rename"] = ("Rename", "\u91CD\u547D\u540D", "Renommer"),
            ["Home.Context.Select"] = ("Select", "\u9009\u62E9", "S\u00E9lection"),
            ["Home.CreateFolderAction"] = ("Create folder", "\u521B\u5EFA\u6587\u4EF6\u5939", "Cr\u00E9er un dossier"),
            ["Home.CreateFolderPrompt"] = ("Enter a name for the new folder:", "\u8F93\u5165\u65B0\u6587\u4EF6\u5939\u540D\u79F0\uFF1A", "Saisissez un nom pour le nouveau dossier :"),
            ["Home.CreateFolderTitle"] = ("Create folder", "\u521B\u5EFA\u6587\u4EF6\u5939", "Cr\u00E9er un dossier"),
            ["Home.CreateNotebookAction"] = ("Create notebook", "\u521B\u5EFA\u7B14\u8BB0\u672C", "Cr\u00E9er le carnet"),
            ["Home.CreateNotebookBrowseFolder"] = ("Choose folder", "\u9009\u62E9\u6587\u4EF6\u5939", "Choisir le dossier"),
            ["Home.CreateNotebookDialogSubtitle"] = ("Choose a page style and where to save the new notebook.", "\u9009\u62E9\u9875\u9762\u6837\u5F0F\u548C\u65B0\u7B14\u8BB0\u672C\u7684\u4FDD\u5B58\u4F4D\u7F6E\u3002", "Choisissez un style de page et l'emplacement du nouveau carnet."),
            ["Home.CreateNotebookDialogTitle"] = ("Create notebook", "\u521B\u5EFA\u7B14\u8BB0\u672C", "Cr\u00E9er un carnet"),
            ["Home.CreateNotebookFailed"] = ("Failed to create notebook: {0}", "\u65E0\u6CD5\u521B\u5EFA\u7B14\u8BB0\u672C\uFF1A{0}", "\u00C9chec de la cr\u00E9ation du carnet : {0}"),
            ["Home.CreateNotebookPathLabel"] = ("Save to", "\u4FDD\u5B58\u5230", "Enregistrer dans"),
            ["Home.ErrorAccessDenied"] = ("File not found or access denied.", "\u627E\u4E0D\u5230\u6587\u4EF6\u6216\u65E0\u6743\u8BBF\u95EE\u3002", "Fichier introuvable ou acc\u00E8s refus\u00E9."),
            ["Home.ErrorFileNotFound"] = ("File not found.", "\u627E\u4E0D\u5230\u6587\u4EF6\u3002", "Fichier introuvable."),
            ["Home.ErrorUnsupportedType"] = ("Unsupported file type.", "\u4E0D\u652F\u6301\u7684\u6587\u4EF6\u7C7B\u578B\u3002", "Type de fichier non pris en charge."),
            ["Home.ExportFailed"] = ("Failed to export: {0}", "\u5BFC\u51FA\u5931\u8D25\uFF1A{0}", "\u00C9chec de l'export : {0}"),
            ["Home.ExportSucceeded"] = ("Exported copy saved.", "\u5DF2\u4FDD\u5B58\u5BFC\u51FA\u526F\u672C\u3002", "Copie export\u00E9e enregistr\u00E9e."),
            ["Home.ExportTitle"] = ("Export PDF Copy", "\u5BFC\u51FA PDF \u526F\u672C", "Exporter une copie PDF"),
            ["Home.FolderCreated"] = ("Folder \"{0}\" created.", "\u5DF2\u521B\u5EFA\u6587\u4EF6\u5939\u201C{0}\u201D\u3002", "Dossier \"{0}\" cr\u00E9\u00E9."),
            ["Home.FolderSubtitle"] = ("Organize the files inside {0}.", "\u7BA1\u7406 {0} \u4E2D\u7684\u6587\u4EF6\u3002", "Organisez les fichiers dans {0}."),
            ["Home.Info.Pages"] = ("{0} pages", "{0} \u9875", "{0} pages"),
            ["Home.Info.Items"] = ("{0} items", "{0} \u9879", "{0} \u00E9l\u00E9ments"),
            ["Home.Info.Notebook"] = ("Notebook", "\u7B14\u8BB0\u672C", "Carnet"),
            ["Home.LibraryRoot"] = ("Library", "\u5E93", "Biblioth\u00E8que"),
            ["Home.Menu.CreateFolder"] = ("Create folder", "\u521B\u5EFA\u6587\u4EF6\u5939", "Cr\u00E9er un dossier"),
            ["Home.Menu.CreateNotebook"] = ("Create empty notebook", "\u521B\u5EFA\u7A7A\u767D\u7B14\u8BB0\u672C", "Cr\u00E9er un carnet vide"),
            ["Home.Menu.OpenFile"] = ("Open file", "\u6253\u5F00\u6587\u4EF6", "Ouvrir un fichier"),
            ["Home.MovedToFolder"] = ("Moved to {0}.", "\u5DF2\u79FB\u52A8\u5230 {0}\u3002", "D\u00E9plac\u00E9 vers {0}."),
            ["Home.NavigateUp"] = ("Back", "\u8FD4\u56DE", "Retour"),
            ["Home.NewNotebookName"] = ("Untitled Notebook", "\u672A\u547D\u540D\u7B14\u8BB0\u672C", "Carnet sans titre"),
            ["Home.NotebookSaved"] = ("Notebook saved.", "\u7B14\u8BB0\u672C\u5DF2\u4FDD\u5B58\u3002", "Carnet enregistr\u00E9."),
            ["Home.OpenPdfTitle"] = ("Open PDF File", "\u6253\u5F00 PDF \u6587\u4EF6", "Ouvrir un fichier PDF"),
            ["Home.PdfFilter"] = ("PDF Files (*.pdf)|*.pdf", "PDF \u6587\u4EF6 (*.pdf)|*.pdf", "Fichiers PDF (*.pdf)|*.pdf"),
            ["Home.RenameAction"] = ("Rename", "\u91CD\u547D\u540D", "Renommer"),
            ["Home.RenameFailed"] = ("Failed to rename: {0}", "\u91CD\u547D\u540D\u5931\u8D25\uFF1A{0}", "\u00C9chec du renommage : {0}"),
            ["Home.RenameFolderPrompt"] = ("Enter a new name for this folder:", "\u8F93\u5165\u6587\u4EF6\u5939\u7684\u65B0\u540D\u79F0\uFF1A", "Saisissez un nouveau nom pour ce dossier :"),
            ["Home.RenameFolderTitle"] = ("Rename Folder", "\u91CD\u547D\u540D\u6587\u4EF6\u5939", "Renommer le dossier"),
            ["Home.RenamePrompt"] = ("Enter a new name for this file:", "\u8F93\u5165\u6587\u4EF6\u7684\u65B0\u540D\u79F0\uFF1A", "Saisissez un nouveau nom pour ce fichier :"),
            ["Home.RenameTitle"] = ("Rename File", "\u91CD\u547D\u540D\u6587\u4EF6", "Renommer le fichier"),
            ["Home.SaveNotebookTitle"] = ("Save Notebook", "\u4FDD\u5B58\u7B14\u8BB0\u672C", "Enregistrer le carnet"),
            ["Home.Selection.Clear"] = ("Clear selection", "\u6E05\u9664\u9009\u62E9", "Effacer la s\u00E9lection"),
            ["Home.Selection.Count"] = ("{0} selected", "\u5DF2\u9009\u62E9 {0} \u4E2A", "{0} s\u00E9lectionn\u00E9(s)"),
            ["Home.Selection.Done"] = ("Done", "\u5B8C\u6210", "Termin\u00E9"),
            ["Home.Selection.Hint"] = ("Selected files will be removed from the library only. The original files stay on disk.", "\u9009\u4E2D\u7684\u6587\u4EF6\u53EA\u4F1A\u4ECE\u5E93\u4E2D\u79FB\u9664\uFF0C\u539F\u59CB\u6587\u4EF6\u4ECD\u4FDD\u7559\u5728\u78C1\u76D8\u4E0A\u3002", "Les fichiers s\u00E9lectionn\u00E9s seront retir\u00E9s de la biblioth\u00E8que uniquement. Les originaux restent sur le disque."),
            ["Home.Selection.None"] = ("No files selected", "未选择文件", "Aucun fichier sélectionné"),
            ["Home.Selection.Move"] = ("Move to folder", "移动到文件夹", "Déplacer vers un dossier"),
            ["Home.Selection.Remove"] = ("Remove from library", "从库中移除", "Retirer de la bibliothèque"),
            ["Home.Selection.RemovedCount"] = ("Removed {0} file(s) from the library.", "\u5DF2\u4ECE\u5E93\u4E2D\u79FB\u9664 {0} \u4E2A\u6587\u4EF6\u3002", "{0} fichier(s) retir\u00E9(s) de la biblioth\u00E8que."),
            ["Home.Selection.SelectAll"] = ("Select all", "\u5168\u9009", "Tout s\u00E9lectionner"),
            ["Home.Subtitle"] = ("Open a PDF, create a notebook, or organize your library.", "\u6253\u5F00 PDF\u3001\u521B\u5EFA\u7B14\u8BB0\u672C\uFF0C\u6216\u6574\u7406\u4F60\u7684\u5E93\u3002", "Ouvrez un PDF, cr\u00E9ez un carnet ou organisez votre biblioth\u00E8que."),
            ["Home.Title"] = ("Library", "\u5E93", "Biblioth\u00E8que"),
            ["Main.About"] = ("About", "\u5173\u4E8E", "\u00C0 propos"),
            ["Main.AboutMessage"] = ("Caelum\nThe Modern Digital Ink Notetaker for Windows", "Caelum\n\u9762\u5411 Windows \u7684\u73B0\u4EE3\u6570\u5B57\u58A8\u8FF9\u7B14\u8BB0\u5DE5\u5177", "Caelum\nLe carnet d'encre num\u00E9rique moderne pour Windows"),
            ["Main.AboutTitle"] = ("About", "\u5173\u4E8E", "\u00C0 propos"),
            ["Main.CloseTabTooltip"] = ("Close tab", "\u5173\u95ED\u9009\u9879\u5361", "Fermer l'onglet"),
            ["Main.FileAutoSaved"] = ("File auto-saved", "\u6587\u4EF6\u5DF2\u81EA\u52A8\u4FDD\u5B58", "Fichier enregistr\u00E9 automatiquement"),
            ["Main.HomeTabTitle"] = ("Home", "\u4E3B\u9875", "Accueil"),
            ["Main.NewTabTooltip"] = ("New tab (Ctrl+T)", "\u65B0\u5EFA\u9009\u9879\u5361 (Ctrl+T)", "Nouvel onglet (Ctrl+T)"),
            ["Main.SearchPlaceholder"] = ("Search library", "\u641C\u7D22\u5E93", "Rechercher dans la biblioth\u00E8que"),
            ["Main.Select"] = ("Select", "\u9009\u62E9", "S\u00E9lection"),
            ["Main.SelectionDisabled"] = ("Select mode disabled", "\u9009\u62E9\u6A21\u5F0F\u5DF2\u5173\u95ED", "Mode s\u00E9lection d\u00E9sactiv\u00E9"),
            ["Main.SelectionEnabled"] = ("Select mode enabled", "\u9009\u62E9\u6A21\u5F0F\u5DF2\u542F\u7528", "Mode s\u00E9lection activ\u00E9"),
            ["Main.Settings"] = ("Settings", "\u8BBE\u7F6E", "Param\u00E8tres"),
            ["Main.SettingsSaved"] = ("Settings saved", "\u8BBE\u7F6E\u5DF2\u4FDD\u5B58", "Param\u00E8tres enregistr\u00E9s"),
            ["Main.SortByDate"] = ("Sort by date", "\u6309\u65E5\u671F\u6392\u5E8F", "Trier par date"),
            ["Main.SortByName"] = ("Sort by name", "\u6309\u540D\u79F0\u6392\u5E8F", "Trier par nom"),
            ["Settings.LanguageHint"] = ("Choose the interface language for Caelum. Changes preview immediately.", "\u9009\u62E9 Caelum \u7684\u754C\u9762\u8BED\u8A00\u3002\u66F4\u6539\u4F1A\u7ACB\u5373\u9884\u89C8\u3002", "Choisissez la langue de l'interface de Caelum. Les changements sont pr\u00E9visualis\u00E9s imm\u00E9diatement."),
            ["Settings.LanguageLabel"] = ("Display language", "\u663E\u793A\u8BED\u8A00", "Langue d'affichage"),
            ["Settings.Subtitle"] = ("Customize utilities and language.", "\u8C03\u6574\u5DE5\u5177\u529F\u80FD\u4E0E\u8BED\u8A00\u3002", "Personnalisez les utilitaires et la langue."),
            ["Settings.Title"] = ("Settings", "\u8BBE\u7F6E", "Param\u00E8tres"),
            ["Settings.UtilityHint"] = ("On the Home page, Select mode enables multi-select. In document tabs, it switches back to text selection.", "\u5728\u4E3B\u9875\u4E2D\uFF0C\u9009\u62E9\u6A21\u5F0F\u53EF\u542F\u7528\u591A\u9009\u3002\u5728\u6587\u6863\u9009\u9879\u5361\u4E2D\uFF0C\u5B83\u4F1A\u5207\u56DE\u6587\u672C\u9009\u62E9\u3002", "Sur l'accueil, le mode S\u00E9lection active la s\u00E9lection multiple. Dans les onglets de document, il r\u00E9active la s\u00E9lection de texte."),
            ["Settings.UtilityLabel"] = ("Utility modes", "\u5DE5\u5177\u6A21\u5F0F", "Modes utilitaires")
        };

        public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

        public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

        public static IReadOnlyList<LanguageOption> GetLanguageOptions() => LanguageOptions;

        public static void ApplyLanguage(AppLanguage language)
        {
            CurrentLanguage = language;
            CurrentCulture = language switch
            {
                AppLanguage.Chinese => CultureInfo.GetCultureInfo("zh-CN"),
                AppLanguage.French => CultureInfo.GetCultureInfo("fr-FR"),
                _ => CultureInfo.GetCultureInfo("en-US")
            };

            Thread.CurrentThread.CurrentCulture = CurrentCulture;
            Thread.CurrentThread.CurrentUICulture = CurrentCulture;
            CultureInfo.DefaultThreadCurrentCulture = CurrentCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
        }

        public static string Get(string key)
        {
            if (!Strings.TryGetValue(key, out var value))
                return key;

            return CurrentLanguage switch
            {
                AppLanguage.Chinese => value.Chinese,
                AppLanguage.French => value.French,
                _ => value.English
            };
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(CurrentCulture, Get(key), args);
        }
    }
}
