using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Linq;
using System.Security.Principal;
using System.Diagnostics;
namespace SteamRipApp.Core
{
    public class SteamAppIdResult
    {
        public int id { get; set; }
        public string name { get; set; } = "";
    }
    public class SteamStoreSearchResponse
    {
        public int total { get; set; }
        public List<SteamAppIdResult> items { get; set; } = new List<SteamAppIdResult>();
    }
    public class SteamUser
    {
        public string Steam64Id { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string AccountId { get; set; } = ""; 
        public bool IsMostRecent { get; set; }
    }
    public static class SteamManager
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        public static string? GetSteamPath()
        {
            try {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                return key?.GetValue("SteamPath")?.ToString()?.Replace("/", "\\");
            } catch { return null; }
        }
        public static List<SteamUser> GetSteamUsers()
        {
            var users = new List<SteamUser>();
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return users;
            string loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersPath)) return users;
            try {
                string content = File.ReadAllText(loginUsersPath);
                var lines = content.Split('\n');
                SteamUser? currentUser = null;
                foreach (var line in lines)
                {
                    string trimmed = line.Trim().Replace("\"", "");
                    if (trimmed.Length == 17 && long.TryParse(trimmed, out _))
                    {
                        currentUser = new SteamUser { Steam64Id = trimmed };
                        if (ulong.TryParse(trimmed, out ulong s64))
                        {
                            currentUser.AccountId = (s64 & 0xFFFFFFFF).ToString();
                        }
                        users.Add(currentUser);
                    }
                    else if (currentUser != null)
                    {
                        if (trimmed.StartsWith("AccountName")) currentUser.AccountName = trimmed.Replace("AccountName", "").Trim();
                        if (trimmed.StartsWith("PersonaName")) currentUser.PersonaName = trimmed.Replace("PersonaName", "").Trim();
                        if (trimmed.StartsWith("MostRecent")) currentUser.IsMostRecent = trimmed.Contains("1");
                    }
                }
            } catch (Exception ex) {
                Logger.LogError("GetSteamUsers", ex);
            }
            return users;
        }
        public static List<string> GetSteamUserIds(string steamPath)
        {
            var userIds = new List<string>();
            var userDataPath = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userDataPath))
            {
                foreach (var dir in Directory.GetDirectories(userDataPath))
                {
                    string name = Path.GetFileName(dir);
                    if (long.TryParse(name, out _)) userIds.Add(name);
                }
            }
            return userIds;
        }
        public static bool IsSteamRunning()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return false;
            string targetExe = Path.Combine(steamPath, "steam.exe").ToLower();
            var processes = Process.GetProcessesByName("steam");
            foreach (var p in processes)
            {
                try {
                    if (p.MainModule?.FileName.ToLower() == targetExe) return true;
                } catch { }
            }
            return false;
        }
        public static void KillSteamProcess()
        {
            try {
                var processes = Process.GetProcessesByName("steam");
                foreach (var p in processes)
                {
                    try {
                        p.Kill();
                        p.WaitForExit(3000);
                    } catch { }
                }
            } catch { }
        }
        public static uint CalculateAppId(string exePath, string appName)
        {
            string input = exePath + appName;
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            uint crc = Force.Crc32.Crc32Algorithm.Compute(bytes);
            return (crc | 0x80000000);
        }
        public static ulong CalculateLongId(uint appid)
        {
            return ((ulong)appid << 32) | 0x02000000;
        }
        public static async Task<int?> LookupAppIdAsync(string gameTitle)
        {
            try {
                NativeBridgeService.Log($"Searching Steam Store for: '{gameTitle}'", "SYSTEM");
                string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(gameTitle)}&l=english&cc=US";
                string json = await _httpClient.GetStringAsync(url);
                var response = JsonSerializer.Deserialize<SteamStoreSearchResponse>(json);
                if (response?.items != null && response.items.Count > 0)
                {
                    string cleanTitle = Regex.Replace(gameTitle, @"[^a-zA-Z0-9\s]", "").ToLower();
                    string[] titleWords = cleanTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var bestMatch = response.items[0];
                    int maxMatches = 0;
                    foreach (var item in response.items)
                    {
                        string cleanItemName = Regex.Replace(item.name, @"[^a-zA-Z0-9\s]", "").ToLower();
                        int matchCount = titleWords.Count(w => cleanItemName.Contains(w));
                        if (matchCount > maxMatches)
                        {
                            maxMatches = matchCount;
                            bestMatch = item;
                        }
                        else if (matchCount == maxMatches && Math.Abs(item.name.Length - gameTitle.Length) < Math.Abs(bestMatch.name.Length - gameTitle.Length))
                        {
                            bestMatch = item;
                        }
                    }
                    NativeBridgeService.Log($"Matched: {bestMatch.name} (ID: {bestMatch.id}) with {maxMatches} word matches", "SYSTEM");
                    return bestMatch.id;
                }
            } catch { }
            return null;
        }
        public static async Task DownloadAssetsAsync(int appId, uint shortcutId, string gridPath)
        {
            if (!Directory.Exists(gridPath)) Directory.CreateDirectory(gridPath);
            var assets = new[] {
                (Url: $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg", File: $"{shortcutId}p.jpg"),
                (Url: $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_hero.jpg", File: $"{shortcutId}_hero.jpg"),
                (Url: $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/logo.png", File: $"{shortcutId}_logo.png"),
                (Url: $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg", File: $"{shortcutId}.jpg")
            };
            foreach (var asset in assets)
            {
                try {
                    var bytes = await _httpClient.GetByteArrayAsync(asset.Url);
                    await File.WriteAllBytesAsync(Path.Combine(gridPath, asset.File), bytes);
                } catch { }
            }
        }
        public static bool IsRunningAsAdmin()
        {
            try {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            } catch { return false; }
        }
        private static string FindGameRoot(string startPath)
        {
            string current = startPath;
            string[] noise = { 
                "binaries", "bin", "win64", "win32", "shipping", "x64", "x86", "helios", "engine", 
                "content", "plugins", "redist", "commonredist", "thirdparty", "steamworks", 
                "steamv132", "steamv153", "steamv147", "steamv157" 
            };
            while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            {
                string name = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLower();
                bool isNoise = noise.Contains(name);
                bool hasSubNoise = Directory.Exists(Path.Combine(current, "Binaries")) || Directory.Exists(Path.Combine(current, "Content"));
                string parent = Path.GetDirectoryName(current) ?? "";
                bool parentHasEngine = !string.IsNullOrEmpty(parent) && Directory.Exists(Path.Combine(parent, "Engine"));
                if (isNoise || parentHasEngine || name.EndsWith("ue5") || name.EndsWith("ue4")) 
                { 
                    current = parent; 
                    continue; 
                }
                break;
            }
            return current;
        }
        public static void RelaunchSteam(string steamPath)
        {
            try {
                string steamExe = Path.Combine(steamPath, "steam.exe");
                if (File.Exists(steamExe)) Process.Start(new ProcessStartInfo { FileName = steamExe, UseShellExecute = true });
            } catch { }
        }
        public static async Task<bool> ImportGameToSteam(string gameTitle, string exePath, int? steamAppId = null)
        {
            NativeBridgeService.Log($"Integrating: {gameTitle}", "SYSTEM");
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return false;
            bool wasSteamOpen = IsSteamRunning();
            if (wasSteamOpen) 
            {
                NativeBridgeService.Log("Steam is running. Restarting to apply integration changes...", "SYSTEM");
                KillSteamProcess();
            }
            var allUserIds = GetSteamUserIds(steamPath);
            var userIds = !string.IsNullOrEmpty(GlobalSettings.SelectedSteamAccountId)
                          ? new List<string> { GlobalSettings.SelectedSteamAccountId }
                          : allUserIds;
            if (userIds.Count == 0) return false;
            string cleanExe = exePath.Trim('\"');
            string exeFolder = Path.GetDirectoryName(cleanExe) ?? "";
            string gameFolder = FindGameRoot(exeFolder);
            uint shortcutAppId = CalculateAppId(cleanExe, gameTitle);
            if (!steamAppId.HasValue) steamAppId = await LookupAppIdAsync(gameTitle);
            if (steamAppId.HasValue)
            {
                NativeBridgeService.Log($"ID {steamAppId.Value} found. Linking...", "SYSTEM");
                await NativeBridgeService.IntegrateGame(gameFolder, steamAppId.Value, gameTitle);
            }
            foreach (var userId in userIds)
            {
                string configPath = Path.Combine(steamPath, "userdata", userId, "config");
                string vdfPath = Path.Combine(configPath, "shortcuts.vdf");
                string gridPath = Path.Combine(configPath, "grid");
                var shortcuts = VdfUtility.ReadShortcuts(vdfPath);
                var toRemove = shortcuts.Where(s => s.AppName.Equals(gameTitle, StringComparison.OrdinalIgnoreCase) || s.AppID == shortcutAppId).ToList();
                foreach (var s in toRemove) shortcuts.Remove(s);
                string workerName = gameTitle;
                shortcuts.Add(new SteamShortcut {
                    AppID = shortcutAppId, AppName = workerName, Exe = cleanExe, StartDir = exeFolder, Icon = cleanExe, IsHidden = false, LaunchOptions = "--worker"
                });
                VdfUtility.WriteShortcuts(vdfPath, shortcuts);
                if (steamAppId.HasValue)
                {
                    ulong longId = CalculateLongId(shortcutAppId);
                    GlobalSettings.MemoryTable[steamAppId.Value.ToString()] = longId.ToString();
                    GlobalSettings.Save();
                    NativeBridgeService.Log($"Mapped AppID {steamAppId.Value} to Shortcut {longId}", "CONFIG");
                }
                if (steamAppId.HasValue) await DownloadAssetsAsync(steamAppId.Value, shortcutAppId, gridPath);
            }
            NativeBridgeService.HideWorkerShortcuts();
            if (wasSteamOpen) RelaunchSteam(steamPath);
            NativeBridgeService.Log($"Completed: {gameTitle} is now integrated.", "SYSTEM");
            return true;
        }
        public static async Task<bool> RemoveGameFromSteam(string gameTitle, string exePath)
        {
            try {
                var steamPath = GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return false;
                bool wasSteamOpen = IsSteamRunning();
                if (wasSteamOpen) 
                {
                    NativeBridgeService.Log("Steam is running. Restarting to remove shortcuts...", "SYSTEM");
                    KillSteamProcess();
                }
                var userIds = GetSteamUserIds(steamPath);
                uint appid = CalculateAppId(exePath.Trim('\"'), gameTitle);
                foreach (var userId in userIds)
                {
                    string configPath = Path.Combine(steamPath, "userdata", userId, "config");
                    string vdfPath = Path.Combine(configPath, "shortcuts.vdf");
                    string gridPath = Path.Combine(configPath, "grid");
                    if (File.Exists(vdfPath))
                    {
                        var shortcuts = VdfUtility.ReadShortcuts(vdfPath);
                        var toRemove = shortcuts.Where(s => s.AppName.Equals(gameTitle, StringComparison.OrdinalIgnoreCase) || s.AppID == appid).ToList();
                        foreach (var s in toRemove) shortcuts.Remove(s);
                        VdfUtility.WriteShortcuts(vdfPath, shortcuts);
                    }
                    if (Directory.Exists(gridPath))
                    {
                        string[] patterns = { $"{appid}p.jpg", $"{appid}_hero.jpg", $"{appid}_logo.png", $"{appid}.jpg" };
                        foreach (var pattern in patterns)
                        {
                            string file = Path.Combine(gridPath, pattern);
                            if (File.Exists(file)) File.Delete(file);
                        }
                    }
                }
                if (wasSteamOpen) RelaunchSteam(steamPath);
                return true;
            } catch { return false; }
        }
    }
}

