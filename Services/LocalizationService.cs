using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using WindowsNotesApp.Models;

namespace WindowsNotesApp.Services
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
            ["Common.Save"] = ("Save", "\u4FDD\u5B58", "Enregistrer"),
            ["Editor.AutoSaved"] = ("Auto-saved", "\u5DF2\u81EA\u52A8\u4FDD\u5B58", "Enregistr\u00E9 automatiquement"),
            ["Editor.DeleteTooltip"] = ("Delete", "\u5220\u9664", "Supprimer"),
            ["Editor.Loading"] = ("Loading...", "\u52A0\u8F7D\u4E2D...", "Chargement..."),
            ["Editor.RedoTooltip"] = ("Redo (Ctrl+Y)", "\u91CD\u505A (Ctrl+Y)", "R\u00E9tablir (Ctrl+Y)"),
            ["Editor.SaveTooltip"] = ("Save", "\u4FDD\u5B58", "Enregistrer"),
            ["Editor.UndoTooltip"] = ("Undo (Ctrl+Z)", "\u64A4\u9500 (Ctrl+Z)", "Annuler (Ctrl+Z)"),
            ["Editor.ZoomEditTooltip"] = ("Click to set zoom", "\u70B9\u51FB\u8BBE\u7F6E\u7F29\u653E", "Cliquer pour r\u00E9gler le zoom"),
            ["Editor.ZoomInTooltip"] = ("Zoom in", "\u653E\u5927", "Zoom avant"),
            ["Editor.ZoomOutTooltip"] = ("Zoom out", "\u7F29\u5C0F", "Zoom arri\u00E8re"),
            ["Home.Context.CopyPath"] = ("Copy path", "\u590D\u5236\u8DEF\u5F84", "Copier le chemin"),
            ["Home.Context.Export"] = ("Export copy", "\u5BFC\u51FA\u526F\u672C", "Exporter une copie"),
            ["Home.Context.Open"] = ("Open", "\u6253\u5F00", "Ouvrir"),
            ["Home.Context.OpenFolder"] = ("Open folder", "\u6253\u5F00\u6240\u5728\u6587\u4EF6\u5939", "Ouvrir le dossier"),
            ["Home.Context.Remove"] = ("Remove from recent", "\u4ECE\u6700\u8FD1\u8BB0\u5F55\u4E2D\u79FB\u9664", "Retirer des r\u00E9cents"),
            ["Home.Context.Rename"] = ("Rename", "\u91CD\u547D\u540D", "Renommer"),
            ["Home.Context.Select"] = ("Select", "\u9009\u62E9", "S\u00E9lection"),
            ["Home.ErrorAccessDenied"] = ("File not found or access denied.", "\u627E\u4E0D\u5230\u6587\u4EF6\u6216\u65E0\u6743\u8BBF\u95EE\u3002", "Fichier introuvable ou acc\u00E8s refus\u00E9."),
            ["Home.ErrorFileNotFound"] = ("File not found.", "\u627E\u4E0D\u5230\u6587\u4EF6\u3002", "Fichier introuvable."),
            ["Home.ErrorUnsupportedType"] = ("Unsupported file type.", "\u4E0D\u652F\u6301\u7684\u6587\u4EF6\u7C7B\u578B\u3002", "Type de fichier non pris en charge."),
            ["Home.ExportFailed"] = ("Failed to export: {0}", "\u5BFC\u51FA\u5931\u8D25\uFF1A{0}", "\u00C9chec de l'export : {0}"),
            ["Home.ExportSucceeded"] = ("Exported copy saved.", "\u5DF2\u4FDD\u5B58\u5BFC\u51FA\u526F\u672C\u3002", "Copie export\u00E9e enregistr\u00E9e."),
            ["Home.ExportTitle"] = ("Export PDF Copy", "\u5BFC\u51FA PDF \u526F\u672C", "Exporter une copie PDF"),
            ["Home.Info.Pages"] = ("{0} pages", "{0} \u9875", "{0} pages"),
            ["Home.OpenPdfTitle"] = ("Open PDF File", "\u6253\u5F00 PDF \u6587\u4EF6", "Ouvrir un fichier PDF"),
            ["Home.PdfFilter"] = ("PDF Files (*.pdf)|*.pdf", "PDF \u6587\u4EF6 (*.pdf)|*.pdf", "Fichiers PDF (*.pdf)|*.pdf"),
            ["Home.RenameAction"] = ("Rename", "\u91CD\u547D\u540D", "Renommer"),
            ["Home.RenameFailed"] = ("Failed to rename: {0}", "\u91CD\u547D\u540D\u5931\u8D25\uFF1A{0}", "\u00C9chec du renommage : {0}"),
            ["Home.RenamePrompt"] = ("Enter a new name for this file:", "\u8F93\u5165\u6587\u4EF6\u7684\u65B0\u540D\u79F0\uFF1A", "Saisissez un nouveau nom pour ce fichier :"),
            ["Home.RenameTitle"] = ("Rename File", "\u91CD\u547D\u540D\u6587\u4EF6", "Renommer le fichier"),
            ["Main.About"] = ("About", "\u5173\u4E8E", "\u00C0 propos"),
            ["Main.AboutMessage"] = ("Caelum\nThe Modern Digital Ink Notetaker for Windows", "Caelum\n\u9762\u5411 Windows \u7684\u73B0\u4EE3\u6570\u5B57\u58A8\u8FF9\u7B14\u8BB0\u5DE5\u5177", "Caelum\nLe carnet d'encre num\u00E9rique moderne pour Windows"),
            ["Main.AboutTitle"] = ("About", "\u5173\u4E8E", "\u00C0 propos"),
            ["Main.CloseTabTooltip"] = ("Close tab", "\u5173\u95ED\u9009\u9879\u5361", "Fermer l'onglet"),
            ["Main.FileAutoSaved"] = ("File auto-saved", "\u6587\u4EF6\u5DF2\u81EA\u52A8\u4FDD\u5B58", "Fichier enregistr\u00E9 automatiquement"),
            ["Main.HomeTabTitle"] = ("Home", "\u4E3B\u9875", "Accueil"),
            ["Main.NewTabTooltip"] = ("New tab (Ctrl+T)", "\u65B0\u5EFA\u9009\u9879\u5361 (Ctrl+T)", "Nouvel onglet (Ctrl+T)"),
            ["Main.SearchPlaceholder"] = ("Search recent files", "\u641C\u7D22\u6700\u8FD1\u6587\u4EF6", "Rechercher les fichiers r\u00E9cents"),
            ["Main.Select"] = ("Select", "\u9009\u62E9", "S\u00E9lection"),
            ["Main.SelectionDisabled"] = ("Select mode disabled", "\u9009\u62E9\u6A21\u5F0F\u5DF2\u5173\u95ED", "Mode s\u00E9lection d\u00E9sactiv\u00E9"),
            ["Main.SelectionEnabled"] = ("Select mode enabled", "\u9009\u62E9\u6A21\u5F0F\u5DF2\u542F\u7528", "Mode s\u00E9lection activ\u00E9"),
            ["Main.Settings"] = ("Settings", "\u8BBE\u7F6E", "Param\u00E8tres"),
            ["Main.SettingsSaved"] = ("Settings saved", "\u8BBE\u7F6E\u5DF2\u4FDD\u5B58", "Param\u00E8tres enregistr\u00E9s"),
            ["Main.SortByDate"] = ("Sort by date", "\u6309\u65E5\u671F\u6392\u5E8F", "Trier par date"),
            ["Main.SortByName"] = ("Sort by name", "\u6309\u540D\u79F0\u6392\u5E8F", "Trier par nom"),
            ["Settings.LanguageHint"] = ("Choose the interface language for Caelum. Changes apply immediately.", "\u9009\u62E9 Caelum \u7684\u754C\u9762\u8BED\u8A00\u3002\u4FDD\u5B58\u540E\u4F1A\u7ACB\u5373\u751F\u6548\u3002", "Choisissez la langue de l'interface de Caelum. Les changements s'appliquent imm\u00E9diatement."),
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
