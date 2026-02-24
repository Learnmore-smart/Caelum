using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace WindowsNotesApp.Services
{
    /// <summary>
    /// Lightweight recent-files tracker that stores paths in a local JSON file.
    /// Works in unpackaged WinUI 3 apps (no package identity required).
    /// </summary>
    public static class RecentFilesService
    {
        private static readonly string _filePath;
        private static readonly object _lock = new();
        private const int MaxEntries = 25;

        static RecentFilesService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsNotesApp");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "recent_files.json");
        }

        /// <summary>
        /// Returns the list of recent file paths (most recent first).
        /// Invalid entries are pruned automatically.
        /// </summary>
        public static List<string> GetRecentFiles()
        {
            lock (_lock)
            {
                var entries = Load();
                // Prune missing files
                int removed = entries.RemoveAll(p => !File.Exists(p));
                if (removed > 0) Save(entries);
                return new List<string>(entries);
            }
        }

        /// <summary>
        /// Adds or promotes a file path to the top of the recent list.
        /// </summary>
        public static void AddOrPromote(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            lock (_lock)
            {
                var entries = Load();
                // Remove existing entry (case-insensitive on Windows)
                entries.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, filePath);

                // Trim
                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

                Save(entries);
            }
        }

        /// <summary>
        /// Removes a specific path from the recent list.
        /// </summary>
        public static void Remove(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            lock (_lock)
            {
                var entries = Load();
                entries.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
                Save(entries);
            }
        }

        private static List<string> Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return new List<string>();
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void Save(List<string> entries)
        {
            try
            {
                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RecentFilesService] Save failed: {ex.Message}");
            }
        }
    }
}
