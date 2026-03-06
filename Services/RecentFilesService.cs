using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WindowsNotesApp.Services
{
    public sealed class RecentFileEntry
    {
        public string Path { get; set; } = string.Empty;

        public int PageCount { get; set; }

        public DateTime? LastModifiedUtc { get; set; }

        public DateTime LastOpenedUtc { get; set; }
    }

    /// <summary>
    /// Lightweight recent-files tracker that stores file metadata in a local JSON file.
    /// </summary>
    public static class RecentFilesService
    {
        private static readonly string _filePath;
        private static readonly string _legacyFilePath;
        private static readonly object _lock = new();
        private const int MaxEntries = 25;

        static RecentFilesService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsNotesApp");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "recent_files.json");
            _legacyFilePath = Path.Combine(dir, "recent_files.txt");
        }

        /// <summary>
        /// Returns the list of recent file entries (most recent first).
        /// </summary>
        public static List<RecentFileEntry> GetRecentEntries()
        {
            lock (_lock)
            {
                var entries = Load();
                if (PruneAndRefreshMetadata(entries))
                    Save(entries);

                return entries.Select(CloneEntry).ToList();
            }
        }

        /// <summary>
        /// Returns the list of recent file paths (most recent first).
        /// </summary>
        public static List<string> GetRecentFiles()
        {
            lock (_lock)
            {
                return GetRecentEntries().Select(entry => entry.Path).ToList();
            }
        }

        /// <summary>
        /// Adds or promotes a file path to the top of the recent list.
        /// </summary>
        public static void AddOrPromote(string filePath)
            => AddOrPromote(filePath, null, null);

        public static void AddOrPromote(string filePath, int? pageCount, DateTime? lastModifiedUtc)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            lock (_lock)
            {
                var entries = Load();
                entries.RemoveAll(entry => string.Equals(entry.Path, filePath, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, CreateEntry(filePath, pageCount, lastModifiedUtc));

                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

                Save(entries);
            }
        }

        /// <summary>
        /// Updates metadata for an existing recent file entry.
        /// </summary>
        public static void UpdateMetadata(string filePath, int? pageCount = null, DateTime? lastModifiedUtc = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            lock (_lock)
            {
                var entries = Load();
                var entry = entries.FirstOrDefault(item => string.Equals(item.Path, filePath, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entries.Insert(0, CreateEntry(filePath, pageCount, lastModifiedUtc));
                }
                else
                {
                    if (pageCount.HasValue)
                        entry.PageCount = pageCount.Value;

                    if (lastModifiedUtc.HasValue)
                        entry.LastModifiedUtc = lastModifiedUtc.Value;
                    else if (File.Exists(filePath))
                        entry.LastModifiedUtc = File.GetLastWriteTimeUtc(filePath);
                }

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
                entries.RemoveAll(entry => string.Equals(entry.Path, filePath, StringComparison.OrdinalIgnoreCase));
                Save(entries);
            }
        }

        private static List<RecentFileEntry> Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return LoadLegacy();

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<RecentFileEntry>();

                try
                {
                    return JsonSerializer.Deserialize<List<RecentFileEntry>>(json) ?? new List<RecentFileEntry>();
                }
                catch (JsonException)
                {
                    var legacyPaths = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                    return legacyPaths.Select(path => CreateEntry(path, null, null)).ToList();
                }
            }
            catch
            {
                return new List<RecentFileEntry>();
            }
        }

        private static List<RecentFileEntry> LoadLegacy()
        {
            try
            {
                if (!File.Exists(_legacyFilePath)) return new List<RecentFileEntry>();

                var content = File.ReadAllText(_legacyFilePath);
                var entries = content
                    .Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Trim())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => CreateEntry(path, null, null))
                    .ToList();

                if (entries.Count > 0)
                    Save(entries);

                return entries;
            }
            catch
            {
                return new List<RecentFileEntry>();
            }
        }

        private static bool PruneAndRefreshMetadata(List<RecentFileEntry> entries)
        {
            bool changed = false;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path) || !File.Exists(entry.Path))
                {
                    entries.RemoveAt(i);
                    changed = true;
                    continue;
                }

                DateTime currentLastModifiedUtc;
                try
                {
                    currentLastModifiedUtc = File.GetLastWriteTimeUtc(entry.Path);
                }
                catch
                {
                    entries.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (!entry.LastModifiedUtc.HasValue || entry.LastModifiedUtc.Value != currentLastModifiedUtc)
                {
                    entry.LastModifiedUtc = currentLastModifiedUtc;
                    if (entry.PageCount > 0)
                        entry.PageCount = 0;
                    changed = true;
                }

                if (entry.LastOpenedUtc == default)
                {
                    entry.LastOpenedUtc = currentLastModifiedUtc;
                    changed = true;
                }
            }

            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                changed = true;
            }

            return changed;
        }

        private static RecentFileEntry CreateEntry(string filePath, int? pageCount, DateTime? lastModifiedUtc)
        {
            DateTime? effectiveLastModifiedUtc = lastModifiedUtc;
            if (!effectiveLastModifiedUtc.HasValue && File.Exists(filePath))
                effectiveLastModifiedUtc = File.GetLastWriteTimeUtc(filePath);

            return new RecentFileEntry
            {
                Path = filePath,
                PageCount = pageCount ?? 0,
                LastModifiedUtc = effectiveLastModifiedUtc,
                LastOpenedUtc = DateTime.UtcNow
            };
        }

        private static RecentFileEntry CloneEntry(RecentFileEntry entry)
        {
            return new RecentFileEntry
            {
                Path = entry.Path,
                PageCount = entry.PageCount,
                LastModifiedUtc = entry.LastModifiedUtc,
                LastOpenedUtc = entry.LastOpenedUtc
            };
        }

        private static void Save(List<RecentFileEntry> entries)
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
