namespace WindowsNotesApp.Models
{
    public sealed class AppSettings
    {
        public AppLanguage Language { get; set; } = AppLanguage.English;
    }

    public sealed class LanguageOption
    {
        public LanguageOption(AppLanguage language, string displayName)
        {
            Language = language;
            DisplayName = displayName;
        }

        public AppLanguage Language { get; }

        public string DisplayName { get; }
    }
}
