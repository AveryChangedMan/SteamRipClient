using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{
    public class DownloadSessionMetadata
    {
        public string GameTitle { get; set; } = "";
        public string SteamRipUrl { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public string Version { get; set; } = "";
        public string SafeFolderName { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string DownloadDir { get; set; } = "";
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        public static string GetSessionFilePath(string archivePath)
        {
            return archivePath + ".session.json";
        }

        public async Task SaveAsync()
        {
            try
            {
                string path = GetSessionFilePath(ArchivePath);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                Logger.LogError("DownloadSessionMetadata.Save", ex);
            }
        }

        public static DownloadSessionMetadata? Load(string archivePath)
        {
            try
            {
                string path = GetSessionFilePath(archivePath);
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<DownloadSessionMetadata>(json);
            }
            catch
            {
                return null;
            }
        }

        public void Delete()
        {
            try
            {
                string path = GetSessionFilePath(ArchivePath);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}