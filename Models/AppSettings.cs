namespace Caelum.Models
{
    public sealed class AppSettings
    {
        public AppLanguage Language { get; set; } = AppLanguage.English;
        public bool EnablePressure { get; set; } = true;
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

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
