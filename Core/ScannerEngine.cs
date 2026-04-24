using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net.Http;
namespace SteamRipApp.Core
{
    public class GameFolder : System.ComponentModel.INotifyPropertyChanged
    {
        private string? _localImagePath;
        private int? _steamAppId;
        private bool _isSteamIntegrated;
        private bool _isGoldbergInitialized;
        private bool _isRunning;
        public string Title { get; set; } = "";
        public string RootPath { get; set; } = "";
        public string? GameSubFolderPath { get; set; }
        public string? ExecutablePath { get; set; }
        public long SizeBytes { get; set; }
        public bool IsRedistMissing { get; set; }
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
        public string? ImageUrl { get; set; }
        public List<RedistFile> MissingRedists { get; set; } = new List<RedistFile>();
        public bool HasMissingRedists => MissingRedists != null && MissingRedists.Any(r => !r.IsInstalled);
        public string Drive => string.IsNullOrEmpty(RootPath) ? "?" : Path.GetPathRoot(RootPath)?.Replace("\\", "") ?? "?";
        public int? SteamAppId 
        { 
            get => _steamAppId; 
            set { 
                _steamAppId = value; 
                NotifyAll();
            } 
        }
        public bool HasAppId => SteamAppId.HasValue;
        public bool IsSteamIntegrated 
        { 
            get => _isSteamIntegrated; 
            set { 
                _isSteamIntegrated = value; 
                NotifyAll();
            } 
        }
        public bool IsEmulatorApplied
        {
            get => _isGoldbergInitialized;
            set {
                _isGoldbergInitialized = value;
                NotifyAll();
            }
        }
        public bool IsRunning
        {
            get => _isRunning;
            set {
                _isRunning = value;
                NotifyAll();
            }
        }
        public string LaunchButtonText => IsRunning ? "STOP" : "LAUNCH";
        public bool ShowResolveButton => !IsSteamIntegrated && !HasAppId;
        public bool ShowImportButton => !IsSteamIntegrated && HasAppId;
        public bool ShowIntegratedGroup => IsSteamIntegrated;
        public bool ShowPatchWarning => IsSteamIntegrated && !IsEmulatorApplied;
        private void NotifyAll()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SteamAppId)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSteamIntegrated)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasAppId)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowResolveButton)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowImportButton)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowIntegratedGroup)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowPatchWarning)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsEmulatorApplied)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasMissingRedists)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MissingRedists)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LaunchButtonText)));
        }
        public string? LocalImagePath 
        { 
            get => _localImagePath; 
            set { 
                _localImagePath = value; 
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LocalImagePath))); 
            } 
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    public static class ScannerEngine
    {
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly Regex MarkerRegex = new Regex(@"STEAMRIP.*Free Pre-installed Steam Games\.url", RegexOptions.IgnoreCase);
        public static List<GameFolder> FoundGames { get; private set; } = new List<GameFolder>();
        public static async Task<List<GameFolder>> GetTrackedGamesAsync()
        {
            var paths = GlobalSettings.Library.Select(l => l.LocalPath).ToList();
            FoundGames = await ScanDirectoriesAsync(paths);
            return FoundGames;
        }
        public static async Task<List<GameFolder>> ScanDirectoriesAsync(List<string> directories, IProgress<string>? progress = null)
        {
            var results = new List<GameFolder>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scanRoot in directories)
            {
                if (!Directory.Exists(scanRoot)) continue;
                progress?.Report($"Scanning {scanRoot}...");
                Logger.Log($"Starting scan of root: {scanRoot}");
                try {
                    var subDirs = Directory.GetDirectories(scanRoot);
                    foreach (var dir in subDirs)
                    {
                        try {
                            var di = new DirectoryInfo(dir);
                            if ((di.Attributes & FileAttributes.Hidden) != 0 || (di.Attributes & FileAttributes.System) != 0) continue;
                            var files = di.GetFiles();
                            bool hasMarker  = files.Any(f => MarkerRegex.IsMatch(f.Name));
                            bool hasReadme  = files.Any(f => f.Name.Equals("Read_Me_Instructions.txt", StringComparison.OrdinalIgnoreCase));
                            bool hasRedist  = Directory.Exists(Path.Combine(di.FullName, "_CommonRedist"));
                            bool inLibrary  = GlobalSettings.Library.Any(m =>
                                m.LocalPath.Equals(di.FullName, StringComparison.OrdinalIgnoreCase));
                            if (!inLibrary && (!hasMarker || !hasReadme || !hasRedist)) continue;
                            if (seenPaths.Add(di.FullName))
                            {
                                var game = await ProcessGameFolder(di);
                                if (game != null) results.Add(game);
                            }
                        } catch (Exception ex) {
                            Logger.LogError($"Scanner_Folder_{dir}", ex);
                        }
                    }
                    var rootDi = new DirectoryInfo(scanRoot);
                    bool rootInLibrary = GlobalSettings.Library.Any(m =>
                        m.LocalPath.Equals(scanRoot, StringComparison.OrdinalIgnoreCase));
                    if (rootInLibrary && seenPaths.Add(scanRoot))
                    {
                        var game = await ProcessGameFolder(rootDi);
                        if (game != null) results.Add(game);
                    }
                } catch (Exception ex) {
                    Logger.LogError($"Scanner_Root_{scanRoot}", ex);
                }
            }
            FoundGames = results;
            return results;
        }
        public static string CleanTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            string[] clutter = { 
                "Free Download", "SteamRIP.com", "SteamRIP", "Free Pre-installed", 
                "Steam Games", "Free Direct Download", "Build", "Version" 
            };
            string result = title;
            foreach (var phrase in clutter)
            {
                int index = result.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    result = result.Substring(0, index);
                }
            }
            result = result.Replace("-", " ").Replace("_", " ").Replace(".", " ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
            if (result.Length > 0 && result.All(c => char.IsLower(c) || !char.IsLetter(c)))
                result = char.ToUpper(result[0]) + result.Substring(1);
            return string.IsNullOrEmpty(result) ? title : result;
        }
        private static async Task<GameFolder?> ProcessGameFolder(DirectoryInfo mainDir)
        {
            if (mainDir.Name.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) || 
                mainDir.Name.Equals("Redist", StringComparison.OrdinalIgnoreCase))
                return null;
            var game = new GameFolder
            {
                RootPath = mainDir.FullName
            };
            var files = mainDir.GetFiles();
            bool hasMarker = files.Any(f => MarkerRegex.IsMatch(f.Name));
            bool hasReadme = files.Any(f => f.Name.Equals("Read_Me_Instructions.txt", StringComparison.OrdinalIgnoreCase));
            bool hasRedist = Directory.Exists(Path.Combine(mainDir.FullName, "_CommonRedist"));
            bool inLibraryMetadata = GlobalSettings.Library.Any(m => m.LocalPath.Equals(mainDir.FullName, StringComparison.OrdinalIgnoreCase));
            if (!inLibraryMetadata && (!hasMarker || !hasReadme || !hasRedist)) return null;
            game.MissingRedists = RedistService.GetRequiredRedists(mainDir.FullName);
            game.IsRedistMissing = game.HasMissingRedists;
            var cleanPath = mainDir.FullName.TrimEnd(Path.DirectorySeparatorChar);
            var metadata = GlobalSettings.Library.FirstOrDefault(m => 
                m.LocalPath.TrimEnd(Path.DirectorySeparatorChar).Equals(cleanPath, StringComparison.OrdinalIgnoreCase));
            if (metadata != null)
            {
                if (!string.IsNullOrEmpty(metadata.Title)) game.Title = CleanTitle(metadata.Title);
                if (metadata.ManualSteamAppId.HasValue)
                {
                    game.SteamAppId = metadata.ManualSteamAppId;
                }
                else
                {
                    game.SteamAppId = metadata.SteamAppId;
                }
                game.IsSteamIntegrated = metadata.IsSteamIntegrated;
                if (!game.IsSteamIntegrated && game.SteamAppId.HasValue)
                {
                    string? steamPath = SteamManager.GetSteamPath();
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        string acfPath = Path.Combine(steamPath, "steamapps", $"appmanifest_{game.SteamAppId}.acf");
                        if (File.Exists(acfPath))
                        {
                            game.IsSteamIntegrated = true;
                            metadata.IsSteamIntegrated = true; 
                            GlobalSettings.Save();
                            Logger.Log($"[Scanner] Corrected integration state for '{game.Title}' (Found manifest)");
                        }
                    }
                }
                string legacyReceipt = Path.Combine(mainDir.FullName, "GBinitTimeStamp.txt");
                string newReceipt = Path.Combine(mainDir.FullName, "PatchLog.txt");
                if (File.Exists(legacyReceipt) || File.Exists(newReceipt))
                {
                    game.IsEmulatorApplied = true;
                    if (metadata != null) metadata.IsEmulatorApplied = true;
                }
                else if (metadata != null)
                {
                    game.IsEmulatorApplied = metadata.IsEmulatorApplied;
                }
            }
            else
            {
                game.Title = CleanTitle(mainDir.Name);
            }
            var imgFiles = mainDir.GetFiles().Where(f => f.Name.Equals("folder.jpg", StringComparison.OrdinalIgnoreCase) || 
                                                       f.Name.Equals("folder.png", StringComparison.OrdinalIgnoreCase) ||
                                                       f.Name.Equals("icon.png", StringComparison.OrdinalIgnoreCase)).ToList();
            if (imgFiles.Any()) {
                game.LocalImagePath = imgFiles.First().FullName;
                Logger.Log($"[Scanner] Found local image for {game.Title}: {game.LocalImagePath}");
            }
            game.SizeBytes = await Task.Run(() => GetDirectorySize(mainDir));
            var subDirs = mainDir.GetDirectories();
            game.GameSubFolderPath = subDirs.FirstOrDefault(d => 
                !d.Name.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) && 
                !d.Name.Equals("Redist", StringComparison.OrdinalIgnoreCase))?.FullName;
            game.ExecutablePath = FindExecutable(mainDir.FullName, game.GameSubFolderPath);
            if (!game.SteamAppId.HasValue)
            {
                game.SteamAppId = ResolveAppIdFromFiles(mainDir.FullName);
                if (game.SteamAppId.HasValue)
                {
                    Logger.Log($"[Scanner] Resolved AppID {game.SteamAppId} from files for {game.Title}");
                    if (metadata != null) metadata.SteamAppId = game.SteamAppId;
                }
                else
                {
                    _ = Task.Run(async () => {
                        var resolvedId = await SteamManager.LookupAppIdAsync(game.Title);
                        if (resolvedId.HasValue)
                        {
                            game.SteamAppId = resolvedId;
                            if (metadata != null) metadata.SteamAppId = resolvedId;
                            GlobalSettings.Save();
                            if (string.IsNullOrEmpty(game.LocalImagePath))
                            {
                                string imgUrl = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{resolvedId}/header.jpg";
                                await DownloadGameImageAsync(imgUrl, game.RootPath);
                                game.LocalImagePath = Path.Combine(game.RootPath, "folder.jpg");
                            }
                        }
                    });
                }
            }
            return game;
        }
        public static void UpdateAppIdInFiles(string rootPath, int appId)
        {
            try
            {
                if (!Directory.Exists(rootPath)) return;
                var iniFiles = Directory.GetFiles(rootPath, "steam_emu.ini", SearchOption.AllDirectories);
                foreach (var iniPath in iniFiles)
                {
                    var lines = File.ReadAllLines(iniPath).ToList();
                    bool found = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].Trim().StartsWith("AppId=", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = $"AppId={appId}";
                            found = true;
                        }
                    }
                    if (!found)
                    {
                        lines.Insert(0, $"AppId={appId}");
                    }
                    File.WriteAllLines(iniPath, lines);
                    Logger.Log($"[Scanner] Updated AppID in file: {iniPath} -> {appId}");
                }
                var idFiles = Directory.GetFiles(rootPath, "steam_appid.txt", SearchOption.AllDirectories);
                foreach (var idPath in idFiles)
                {
                    File.WriteAllText(idPath, appId.ToString());
                    Logger.Log($"[Scanner] Synced Goldberg steam_appid.txt: {idPath} -> {appId}");
                }
                string[] receipts = { "GBinitTimeStamp.txt", "PatchLog.txt" };
                foreach (var rName in receipts)
                {
                    string rPath = Path.Combine(rootPath, rName);
                    if (File.Exists(rPath))
                    {
                        string content = $"Game: {Path.GetFileName(rootPath)}\n" +
                                       $"AppID: {appId}\n" +
                                       $"InitTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (Updated)\n" +
                                       $"Path: {rootPath}\n" +
                                       $"Status: SUCCESS";
                        File.WriteAllText(rPath, content);
                        Logger.Log($"[Scanner] Updated receipt: {rName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("UpdateAppIdInFiles", ex);
            }
        }
        public static int? ResolveAppIdFromFiles(string rootPath)
        {
            try
            {
                if (!Directory.Exists(rootPath)) return null;
                var iniFiles = Directory.GetFiles(rootPath, "steam_emu.ini", SearchOption.AllDirectories);
                foreach (var iniPath in iniFiles)
                {
                    var lines = File.ReadAllLines(iniPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("AppId=", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = trimmed.Substring(6).Trim();
                            if (int.TryParse(val, out int id)) return id;
                        }
                        if (trimmed.Contains("CODEX", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(@"\"))
                        {
                            var parts = trimmed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                var lastPart = parts.Last().Trim();
                                if (int.TryParse(lastPart, out int id)) return id;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ResolveAppIdFromFiles", ex);
            }
            return null;
        }
        public static async Task DownloadGameImageAsync(string imageUrl, string gameFolderPath)
        {
            try {
                if (string.IsNullOrEmpty(imageUrl)) return;
                var destPath = Path.Combine(gameFolderPath, "folder.jpg");
                if (File.Exists(destPath))
                {
                    Logger.Log("[Scanner] Overwriting existing image cover.");
                    File.Delete(destPath);
                }
                Logger.Log($"[Scanner] Downloading game image to: {destPath}");
                var bytes = await client.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(destPath, bytes);
                Logger.Log("[Scanner] Image download complete.");
            } catch (Exception ex) {
                Logger.LogError("DownloadGameImage", ex);
            }
        }
        private static long GetDirectorySize(DirectoryInfo d)
        {
            long size = 0;
            try {
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
                size += d.GetFiles().Sum(f => f.Length);
                foreach (var di in d.GetDirectories()) size += GetDirectorySize(di);
            } catch { }
            return size;
        }
        public static string? FindExecutable(string rootPath, string? gameSubPath = null)
        {
            string searchPath = gameSubPath ?? rootPath;
            try {
                if (!Directory.Exists(searchPath)) return null;
                var allExes = Directory.GetFiles(searchPath, "*.exe", SearchOption.AllDirectories)
                                       .Where(f => !f.ToLower().Contains("redist") && 
                                                   !f.ToLower().Contains("_commonredist") &&
                                                   !f.ToLower().Contains("crash") && 
                                                   !f.ToLower().Contains("unity") && 
                                                   !f.ToLower().Contains("engine"))
                                       .ToList();
                if (allExes.Count == 0) return null;
                var launcher = allExes.FirstOrDefault(f => Path.GetFileName(f).Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase));
                if (launcher != null) return launcher;
                var launcherLike = allExes.FirstOrDefault(f => f.Contains("Launcher", StringComparison.OrdinalIgnoreCase));
                if (launcherLike != null) return launcherLike;
                return allExes.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            } catch { return null; }
        }
    }
}

