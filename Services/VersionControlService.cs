using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Caelum.Models;

namespace Caelum.Services
{
    public class VersionControlService
    {
        private static string GetVersionDir(string filePath)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
            var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            var dir = Path.Combine(appData, "Caelum", "VersionHistory", hash);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static async Task SaveVersionAsync(string filePath, Dictionary<int, PageAnnotation> annotations)
        {
            var dir = GetVersionDir(filePath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var file = Path.Combine(dir, $"{timestamp}.json");

            var json = JsonSerializer.Serialize(annotations);
            await File.WriteAllTextAsync(file, json);
        }

        public static List<string> GetVersions(string filePath)
        {
            var dir = GetVersionDir(filePath);
            if (!Directory.Exists(dir)) return new List<string>();
            var files = Directory.GetFiles(dir, "*.json");
            var list = new List<string>(files);
            list.Sort((a,b) => File.GetCreationTime(b).CompareTo(File.GetCreationTime(a)));
            return list;
        }

        public static async Task<Dictionary<int, PageAnnotation>> LoadVersionAsync(string versionFilePath)
        {
            var json = await File.ReadAllTextAsync(versionFilePath);
            return JsonSerializer.Deserialize<Dictionary<int, PageAnnotation>>(json);
        }
    }
}