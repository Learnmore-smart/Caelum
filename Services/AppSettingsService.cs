using System;
using System.IO;
using System.Text.Json;
using WindowsNotesApp.Models;

namespace WindowsNotesApp.Services
{
    public static class AppSettingsService
    {
        private static readonly object SyncRoot = new object();
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static readonly string SettingsPath;
        private static AppSettings _cachedSettings;

        static AppSettingsService()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsNotesApp");
            Directory.CreateDirectory(folder);
            SettingsPath = Path.Combine(folder, "settings.json");
        }

        public static AppSettings Load()
        {
            lock (SyncRoot)
            {
                _cachedSettings ??= ReadSettingsCore();
                return Clone(_cachedSettings);
            }
        }

        public static AppSettings Save(AppSettings settings)
        {
            lock (SyncRoot)
            {
                _cachedSettings = Sanitize(settings);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_cachedSettings, SerializerOptions));
                return Clone(_cachedSettings);
            }
        }

        private static AppSettings ReadSettingsCore()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                var json = File.ReadAllText(SettingsPath);
                if (string.IsNullOrWhiteSpace(json))
                    return new AppSettings();

                return Sanitize(JsonSerializer.Deserialize<AppSettings>(json));
            }
            catch
            {
                return new AppSettings();
            }
        }

        private static AppSettings Sanitize(AppSettings settings)
        {
            var sanitized = settings ?? new AppSettings();
            if (!Enum.IsDefined(typeof(AppLanguage), sanitized.Language))
                sanitized.Language = AppLanguage.English;

            return new AppSettings
            {
                Language = sanitized.Language
            };
        }

        private static AppSettings Clone(AppSettings settings)
        {
            return new AppSettings
            {
                Language = settings.Language
            };
        }
    }
}
