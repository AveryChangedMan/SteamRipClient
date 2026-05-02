using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamRipApp.Core
{
    public class GameFolder : System.ComponentModel.INotifyPropertyChanged
    {
        private string? _localImagePath;
        private int? _steamAppId;
        private bool _isSteamIntegrated;
        private bool _isEmulatorApplied;
        private bool _isRunning;
        private bool _isRepairRequired;

        public string Title { get; set; } = "";
        public string RootPath { get; set; } = "";
        public string? GameSubFolderPath { get; set; }
        public string? ExecutablePath { get; set; }
        public long SizeBytes { get; set; }
        public bool IsRedistMissing { get; set; }
        public bool IsMoveInterrupted { get; set; }
        public bool IsMoving
        {
            get => _isMoving;
            set { _isMoving = value; NotifyAll(); }
        }
        private bool _isMoving;

        private string _version = "";
        public string Version
        {
            get => _version;
            set { _version = value; NotifyAll(); }
        }
        private string _latestVersion = "";
        public string LatestVersion
        {
            get => _latestVersion;
            set {
                _latestVersion = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LatestVersion)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasVersionUpdate)));
            }
        }
        public bool HasVersionUpdate => !string.IsNullOrEmpty(LatestVersion) && !string.IsNullOrEmpty(Version) && !LatestVersion.Equals(Version, StringComparison.OrdinalIgnoreCase);

        public string DisplayVersion {
            get {
                if (string.IsNullOrEmpty(Version)) return "";
                return Version;
            }
        }

        public string Url { get; set; } = "";
        public string? ImageUrl { get; set; }
        public List<RedistFile> MissingRedists { get; set; } = new List<RedistFile>();
        public bool HasMissingRedists => MissingRedists != null && MissingRedists.Any(r => !r.IsInstalled);

        public bool ShowLaunchButton => !IsMoveInterrupted && !IsMoving;
        public bool ShowMoveButton => !IsMoving && !IsMoveInterrupted;

        public bool IsRepairable => (File.Exists(Path.Combine(RootPath, ".rip_map.json")) ||
                                    (!string.IsNullOrEmpty(GameSubFolderPath) && File.Exists(Path.Combine(GameSubFolderPath, ".rip_map.json")))) &&
                                   (File.Exists(Path.Combine(RootPath, ".rip_skeleton.json")) ||
                                    (!string.IsNullOrEmpty(GameSubFolderPath) && File.Exists(Path.Combine(GameSubFolderPath, ".rip_skeleton.json"))));

        public bool ShowBackupButton => GlobalSettings.IsHardRepairEnabled && !IsMoving && IsRepairable;
        public string Drive => string.IsNullOrEmpty(RootPath) ? "?" : Path.GetPathRoot(RootPath)?.Replace("\\", "") ?? "?";

        public List<string> Snapshots { get; set; } = new List<string>();
        public string? SelectedSnapshot { get; set; }

        public bool IsAdvancedRepairVisible => GlobalSettings.IsAdvancedRepairVisible;

        public void LoadSnapshots()
        {
            Snapshots.Clear();
            Snapshots.Add("Official Rip Map");

            try {
                if (Directory.Exists(RootPath))
                {
                    var files = Directory.GetFiles(RootPath, "*.rip_skeleton.json");
                    foreach (var f in files)
                    {
                        string fileName = Path.GetFileName(f);
                        if (fileName.Equals(".rip_skeleton.json", StringComparison.OrdinalIgnoreCase)) continue;

                        string name = fileName.Replace(".rip_skeleton.json", "");
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        if (!Snapshots.Contains(name)) Snapshots.Add(name);
                    }
                }
            } catch { }

            Snapshots.RemoveAll(string.IsNullOrWhiteSpace);

            if (SelectedSnapshot == null || !Snapshots.Contains(SelectedSnapshot))
                SelectedSnapshot = "Official Rip Map";
        }

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
            get => _isEmulatorApplied;
            set {
                _isEmulatorApplied = value;
                NotifyAll();
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set {
                _isRunning = value;
                NotifyAll();
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LaunchButtonText)));
            }
        }

        public bool IsRepairRequired
        {
            get => _isRepairRequired;
            set {
                _isRepairRequired = value;
                NotifyAll();
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LaunchButtonText)));
            }
        }

        private bool _isRepairInterrupted;
        public bool IsRepairInterrupted
        {
            get => _isRepairInterrupted;
            set {
                _isRepairInterrupted = value;
                NotifyAll();
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LaunchButtonText)));
            }
        }

        public string LaunchButtonText {
            get {
                if (IsRepairInterrupted) return "▶ RESUME";
                if (IsRepairRequired && IsRepairable) return "REPAIR";
                if (HasMissingRedists) return "INSTALL REDIST";
                return IsRunning ? "STOP" : "LAUNCH";
            }
        }

        public bool ShowResolveButton => !IsSteamIntegrated && !HasAppId;
        public bool ShowImportButton => !IsSteamIntegrated && HasAppId;
        public bool ShowIntegratedGroup => IsSteamIntegrated;
        public bool ShowPatchWarning => IsSteamIntegrated && !IsEmulatorApplied;

        private bool _isInProgress;
        private string _progressPhase = "";
        private double _progressPercentage;
        private string _progressDetails = "";
        private string _estimatedTime = "";

        public string EstimatedTime
        {
            get => _estimatedTime;
            set {
                if (_estimatedTime == value) return;
                _estimatedTime = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(EstimatedTime)));
            }
        }

        public bool IsInProgress
        {
            get => _isInProgress;
            set {
                if (_isInProgress == value) return;
                _isInProgress = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsInProgress)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowProgressOverlay)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowLaunchButton)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowBackupButton)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowMoveButton)));
            }
        }

        public string ProgressPhase
        {
            get => _progressPhase;
            set {
                if (_progressPhase == value) return;
                _progressPhase = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProgressPhase)));
            }
        }

        public double ProgressPercentage
        {
            get => _progressPercentage;
            set {
                if (_progressPercentage == value) return;
                _progressPercentage = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProgressPercentage)));
            }
        }

        public string ProgressDetails
        {
            get => _progressDetails;
            set {
                if (_progressDetails == value) return;
                _progressDetails = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProgressDetails)));
            }
        }

        public bool ShowProgressOverlay => IsInProgress || IsRepairInterrupted;

        private void NotifyAll()
        {

            foreach (var prop in new[] {
                nameof(SteamAppId), nameof(IsSteamIntegrated), nameof(HasAppId),
                nameof(ShowResolveButton), nameof(ShowImportButton), nameof(ShowIntegratedGroup),
                nameof(ShowPatchWarning), nameof(IsEmulatorApplied), nameof(HasMissingRedists),
                nameof(MissingRedists), nameof(IsMoveInterrupted), nameof(IsMoving),
                nameof(ShowLaunchButton), nameof(ShowMoveButton), nameof(IsRunning),
                nameof(LaunchButtonText), nameof(DisplayVersion), nameof(IsAdvancedRepairVisible),
                nameof(IsInProgress), nameof(ProgressPhase), nameof(ProgressPercentage),
                nameof(ProgressDetails), nameof(EstimatedTime), nameof(ShowProgressOverlay)
            })
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
            }
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

        private static readonly string[] ExecutableIgnoreList = {
            "remove", "uninstall", "unins", "unin", "crashpad", "crash", "crashpad_handler",
            "handler", "redist", "_commonredist", "unity", "engine", "dotnet", "framework"
        };

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

            var toRemove = GlobalSettings.ActiveDownloads.Where(d => d.Phase == "Failed").ToList();
            foreach (var d in toRemove) GlobalSettings.ActiveDownloads.Remove(d);
            int removedCount = toRemove.Count;

            if (removedCount > 0)
            {
                Logger.Log($"[Scanner] Cleaned up {removedCount} failed entries for re-discovery.");
                GlobalSettings.Save();
            }

            var scanPaths = new List<string>(directories);
            if (!string.IsNullOrEmpty(GlobalSettings.DownloadDirectory) && !scanPaths.Contains(GlobalSettings.DownloadDirectory))
                scanPaths.Add(GlobalSettings.DownloadDirectory);

            foreach (var scanRoot in scanPaths)
            {
                if (!Directory.Exists(scanRoot)) continue;
                try {

                    var progressFiles = Directory.GetFiles(scanRoot, "*.progress", SearchOption.TopDirectoryOnly);
                    foreach (var pFile in progressFiles)
                    {
                        string rarPath = pFile.Substring(0, pFile.Length - 9);
                        if (File.Exists(rarPath))
                        {
                            bool alreadyTracked = GlobalSettings.ActiveDownloads.Any(d => d.DestPath.Equals(rarPath, StringComparison.OrdinalIgnoreCase));
                            if (!alreadyTracked)
                            {
                                var dl = new ActiveDownloadMetadata
                                {
                                    Title = Path.GetFileNameWithoutExtension(rarPath),
                                    DestPath = rarPath,
                                    Status = "Paused (Discovered in scan)",
                                    Phase = "PAUSED",
                                    IsPaused = true
                                };
                                GlobalSettings.ActiveDownloads.Add(dl);
                                GlobalSettings.Save();
                                Logger.Log($"[Scanner] Discovered orphaned download in {scanRoot}: {rarPath}");
                            }
                        }
                    }

                    var allRars = Directory.GetFiles(scanRoot, "*.rar", SearchOption.TopDirectoryOnly);
                    foreach (var rarPath in allRars)
                    {
                        string rarFileName = Path.GetFileNameWithoutExtension(rarPath);

                        if (rarFileName.EndsWith(".part01", StringComparison.OrdinalIgnoreCase))
                            rarFileName = rarFileName.Substring(0, rarFileName.Length - 7);

                        string gameTitle = CleanTitle(rarFileName);

                        string? actualGameDir = null;
                        try {
                            var subDirs = Directory.GetDirectories(scanRoot);
                            foreach (var sdPath in subDirs)
                            {
                                var sdName = Path.GetFileName(sdPath);
                                if (sdName.Equals(rarFileName, StringComparison.OrdinalIgnoreCase) ||
                                    CleanTitle(sdName).Equals(gameTitle, StringComparison.OrdinalIgnoreCase))
                                {
                                    actualGameDir = sdPath;
                                    break;
                                }
                            }
                        } catch { }

                        if (actualGameDir != null)
                        {

                            bool hasMap = File.Exists(Path.Combine(actualGameDir, ".rip_map.json"));
                            bool hasVer = File.Exists(Path.Combine(actualGameDir, ".rip_version.json"));

                            if (!hasMap || !hasVer)
                            {

                                string fullRarPath = Path.GetFullPath(rarPath);
                                var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d =>
                                    !string.IsNullOrEmpty(d.DestPath) &&
                                    Path.GetFullPath(d.DestPath).Equals(fullRarPath, StringComparison.OrdinalIgnoreCase));

                                if (dl == null)
                                {
                                    dl = new ActiveDownloadMetadata
                                    {
                                        Title = gameTitle,
                                        DestPath = fullRarPath,
                                        Status = "📦 Incomplete Extraction",
                                        Phase = "Extracting",
                                        IsPaused = true
                                    };
                                    GlobalSettings.ActiveDownloads.Add(dl);
                                    Logger.Log($"[Scanner] Discovered incomplete extraction: {gameTitle}");
                                }
                                else if (dl.Phase != "Extracting" && dl.Phase != "Done")
                                {
                                    dl.Status = "📦 Finish Extraction";
                                    dl.Phase = "Extracting";
                                    dl.IsPaused = true;
                                    dl.NotifyAll();
                                }
                                GlobalSettings.Save();
                            }
                        }
                        else
                        {

                            string fullRarPath = Path.GetFullPath(rarPath);
                            bool alreadyTracked = GlobalSettings.ActiveDownloads.Any(d =>
                                !string.IsNullOrEmpty(d.DestPath) &&
                                Path.GetFullPath(d.DestPath).Equals(fullRarPath, StringComparison.OrdinalIgnoreCase));

                            if (!alreadyTracked)
                            {
                                var dl = new ActiveDownloadMetadata
                                {
                                    Title = gameTitle,
                                    DestPath = fullRarPath,
                                    Status = "📦 Ready to Extract",
                                    Phase = "Extracting",
                                    IsPaused = true
                                };
                                GlobalSettings.ActiveDownloads.Add(dl);
                                GlobalSettings.Save();
                                Logger.Log($"[Scanner] Discovered new archive: {gameTitle}");
                            }
                        }
                    }
                } catch { }
            }

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

                            if (di.Name.EndsWith("-moving", StringComparison.OrdinalIgnoreCase)) continue;

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
                    if (rootDi.Name.EndsWith("-moving", StringComparison.OrdinalIgnoreCase)) continue;

                    bool rootInLibrary = GlobalSettings.Library.Any(m =>
                        m.LocalPath.Equals(scanRoot, StringComparison.OrdinalIgnoreCase));
                    if (rootInLibrary && seenPaths.Add(scanRoot))
                    {
                        var game = await ProcessGameFolder(rootDi);
                        if (game != null)
                        {
                            CheckForRepairState(game);
                            results.Add(game);
                        }
                    }
                } catch (Exception ex) {
                    Logger.LogError($"ScanDirectoriesAsync_{scanRoot}", ex);
                }
            }

            FoundGames = results;

            _ = UpdateRepairLogicVersions(results);

            return results;
        }

        private static async Task UpdateRepairLogicVersions(List<GameFolder> games)
        {
            await Task.Run(() => {
                foreach (var game in games)
                {
                    try {
                        string versionFilePath = Path.Combine(game.RootPath, ".rip_version.json");
                        if (File.Exists(versionFilePath))
                        {
                            var json = File.ReadAllText(versionFilePath);
                            var info = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                            if (info != null && info.ContainsKey("RepairLogicVersion"))
                            {
                                string currentVer = info["RepairLogicVersion"];
                                if (currentVer != GlobalSettings.RepairLogicVersion)
                                {
                                    info["RepairLogicVersion"] = GlobalSettings.RepairLogicVersion;
                                    info["UpdatedByAppVersion"] = GlobalSettings.AppVersion;
                                    info["UpdatedAt"] = DateTime.UtcNow.ToString("o");
                                    File.WriteAllText(versionFilePath, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
                                    Logger.Log($"[Scanner] Updated repair logic version for {game.Title} to {GlobalSettings.RepairLogicVersion}");
                                }
                            }
                            else
                            {

                                var newInfo = new Dictionary<string, string>
                                {
                                    { "RepairLogicVersion", GlobalSettings.RepairLogicVersion },
                                    { "AppVersion", GlobalSettings.AppVersion },
                                    { "GeneratedAt", DateTime.UtcNow.ToString("o") },
                                    { "MachineName", Environment.MachineName }
                                };
                                File.WriteAllText(versionFilePath, JsonSerializer.Serialize(newInfo, new JsonSerializerOptions { WriteIndented = true }));
                                Logger.Log($"[Scanner] Initialized repair logic version for {game.Title} to {GlobalSettings.RepairLogicVersion}");
                            }
                        }
                    } catch { }
                }
            });
        }

        public static string CleanTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;

            string[] clutter = {
                "Free Download", "SteamRIP.com", "SteamRIP", "Free Pre-installed",
                "Steam Games", "Free Direct Download", "Build", "Version"
            };

            if (string.IsNullOrEmpty(title)) return "Unknown Game";
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
            bool hasRipFile = files.Any(f => f.Name.EndsWith(".rip_map.json", StringComparison.OrdinalIgnoreCase) ||
                                            f.Name.EndsWith(".rip_skeleton.json", StringComparison.OrdinalIgnoreCase));
            bool hasVersionFile = files.Any(f => f.Name.Equals(".rip_version.json", StringComparison.OrdinalIgnoreCase));
            bool inLibraryMetadata = GlobalSettings.Library.Any(m => m.LocalPath.Equals(mainDir.FullName, StringComparison.OrdinalIgnoreCase));

            bool hasExe = mainDir.GetFiles("*.exe", SearchOption.AllDirectories).Any(f => {
                string lower = f.Name.ToLower();
                return !ExecutableIgnoreList.Any(ignore => lower.Contains(ignore));
            });

            if (!inLibraryMetadata && !hasMarker && !hasReadme && !hasRedist && !hasExe && !hasRipFile && !hasVersionFile) return null;

            if (hasRedist)
            {
                RedistService.UpdateRedistManifest(Path.Combine(mainDir.FullName, "_CommonRedist"));
            }

            game.MissingRedists = RedistService.GetRequiredRedists(mainDir.FullName);
            game.IsRedistMissing = game.HasMissingRedists;

            var cleanPath = mainDir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var metadata = GlobalSettings.Library.FirstOrDefault(m =>
                m.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(cleanPath, StringComparison.OrdinalIgnoreCase));

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

            var subDirs = mainDir.GetDirectories().Where(d =>
                !d.Name.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) &&
                !d.Name.Equals("Redist", StringComparison.OrdinalIgnoreCase)).ToList();

            if (subDirs.Count == 1)
            {
                game.GameSubFolderPath = subDirs[0].FullName;
            }
            else if (subDirs.Count > 1)
            {

                FileInfo? bestExe = null;
                DirectoryInfo? bestDir = null;
                foreach (var sd in subDirs)
                {
                    var exes = sd.GetFiles("*.exe", SearchOption.AllDirectories)
                                 .Where(f => {
                                     string lower = f.Name.ToLower();
                                     return !ExecutableIgnoreList.Any(ignore => lower.Contains(ignore));
                                 })
                                 .OrderByDescending(f => f.Length);
                    var top = exes.FirstOrDefault();
                    if (top != null && (bestExe == null || top.Length > bestExe.Length))
                    {
                        bestExe = top;
                        bestDir = sd;
                    }
                }
                if (bestDir != null) game.GameSubFolderPath = bestDir.FullName;
            }

            if (GlobalSettings.GameConfigs.TryGetValue(mainDir.FullName, out var config) && !string.IsNullOrEmpty(config.ManualExePath))
            {
                game.ExecutablePath = config.ManualExePath;
                Logger.Log($"[Scanner] Using manual executable path for '{game.Title}': {game.ExecutablePath}");
            }
            else
            {
                game.ExecutablePath = FindExecutable(mainDir.FullName, game.GameSubFolderPath);

                if (string.IsNullOrEmpty(game.ExecutablePath))
                {
                    game.ExecutablePath = FindExecutable(mainDir.FullName);
                }
            }

            if (!game.SteamAppId.HasValue)
            {
                game.SteamAppId = ResolveAppIdFromFiles(mainDir.FullName);
                if (game.SteamAppId.HasValue)
                {
                    Logger.Log($"[Scanner] Resolved AppID {game.SteamAppId} from files for {game.Title}");
                    if (metadata != null) metadata.SteamAppId = game.SteamAppId;
                }
            }

            string localVer = RepairService.ReadVersionFile(mainDir.FullName);
            if (!string.IsNullOrEmpty(localVer)) game.Version = localVer;

            if (metadata != null)
            {
                if (!string.IsNullOrEmpty(metadata.Url)) game.Url = metadata.Url;
                if (!string.IsNullOrEmpty(metadata.ImageUrl)) game.ImageUrl = metadata.ImageUrl;

                if (!string.IsNullOrEmpty(metadata.Version))
                {
                    game.Version = metadata.Version;

                    if (metadata.Version != localVer)
                    {
                        RepairService.WriteVersionFile(mainDir.FullName, metadata.Version);
                        Logger.Log($"[Scanner] Synced local version file for '{game.Title}' to match settings.json: {metadata.Version}");
                    }
                }
            }

            if (string.IsNullOrEmpty(game.LocalImagePath) || !game.SteamAppId.HasValue)
            {
                _ = Task.Run(async () => {
                    try {

                        if (!game.SteamAppId.HasValue)
                        {
                            var resolvedId = await SteamManager.LookupAppIdAsync(game.Title);
                            if (resolvedId.HasValue)
                            {
                                game.SteamAppId = resolvedId;
                                if (metadata != null) metadata.SteamAppId = resolvedId;
                                GlobalSettings.Save();
                            }
                        }

                        if (string.IsNullOrEmpty(game.LocalImagePath))
                        {
                            string? imgUrl = null;
                            if (!string.IsNullOrEmpty(game.ImageUrl)) imgUrl = game.ImageUrl;
                            else if (game.SteamAppId.HasValue) imgUrl = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{game.SteamAppId}/header.jpg";

                            if (!string.IsNullOrEmpty(imgUrl))
                            {
                                await DownloadGameImageAsync(imgUrl, game.RootPath);
                                game.LocalImagePath = Path.Combine(game.RootPath, "folder.jpg");
                            }
                        }
                    } catch { }
                });
            }

            if (string.IsNullOrEmpty(game.Url) && GlobalSettings.GamePageLinks.TryGetValue(mainDir.FullName, out var storedUrl))
                game.Url = storedUrl;

            if (!string.IsNullOrEmpty(game.Url))
            {
                _ = Task.Run(async () => {
                    try {
                        Logger.Log($"[VersionCheck] Checking {game.Title} at {game.Url}");
                        var details = await SteamRipScraper.GetGameDetailsAsync(game.Url);
                        if (!string.IsNullOrEmpty(details.LatestVersion))
                        {
                            Logger.Log($"[VersionCheck] {game.Title}: Remote={details.LatestVersion}, Local={game.Version}");
                            if (details.LatestVersion != game.Version)
                            {
                                App.MainWindowInstance?.DispatcherQueue?.TryEnqueue(() => {
                                    game.LatestVersion = details.LatestVersion;
                                });
                                Logger.Log($"[VersionCheck] Update flagged for {game.Title}!");
                            }
                        }
                        else {
                            Logger.Log($"[VersionCheck] {game.Title}: Could not find version on page.");
                        }
                    } catch (Exception ex) {
                        Logger.Log($"[VersionCheck] {game.Title}: Failed - {ex.Message}\n{ex.StackTrace}");
                    }
                });
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
                    Logger.Log($"[Scanner] Synced emulator steam_appid.txt: {idPath} -> {appId}");
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

        public static async Task DownloadGameImageAsync(string imageUrl, string gameFolderPath, bool overwrite = false)
        {
            try {
                if (string.IsNullOrEmpty(imageUrl)) return;

                var destPath = Path.Combine(gameFolderPath, "folder.jpg");

                if (File.Exists(destPath))
                {
                    if (!overwrite) return;
                    Logger.Log("[Scanner] Replacing existing cover image (overwrite requested).");
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

        public static string? FindExecutable(string rootPath, string? gameTitle = null)
        {
            var candidates = GetExecutableCandidates(rootPath, gameTitle ?? Path.GetFileName(rootPath));
            return candidates.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        public static List<string> GetExecutableCandidates(string rootPath, string gameTitle)
        {
            try {
                if (!Directory.Exists(rootPath)) return new List<string>();

                var allExes = Directory.GetFiles(rootPath, "*.exe", SearchOption.AllDirectories)
                                       .Where(f => {
                                           string nameLower = Path.GetFileName(f).ToLower();
                                           string fullLower = f.ToLower();
                                           return !ExecutableIgnoreList.Any(ignore => nameLower.Contains(ignore) || fullLower.Contains("\\" + ignore + "\\"));
                                       })
                                       .ToList();

                if (allExes.Count == 0) return new List<string>();

                var launcherExes = allExes.Where(f => Path.GetFileName(f).Contains("Launcher", StringComparison.OrdinalIgnoreCase)).ToList();
                if (launcherExes.Count > 0) return launcherExes;

                var playExes = allExes.Where(f => Path.GetFileName(f).Contains("Play", StringComparison.OrdinalIgnoreCase)).ToList();
                if (playExes.Count > 0) return playExes;

                var gameExes = allExes.Where(f => Path.GetFileName(f).Contains("game", StringComparison.OrdinalIgnoreCase)).ToList();
                if (gameExes.Count > 0) return gameExes;

                var similarExes = allExes.Select(f => new { Path = f, Score = CalculateSimilarity(Path.GetFileNameWithoutExtension(f), gameTitle) })
                                         .Where(x => x.Score > 0)
                                         .OrderByDescending(x => x.Score)
                                         .ThenByDescending(x => new FileInfo(x.Path).Length)
                                         .Select(x => x.Path)
                                         .Take(3)
                                         .ToList();

                if (similarExes.Count > 0) return similarExes;

                return allExes.OrderByDescending(f => new FileInfo(f).Length).Take(1).ToList();
            } catch { return new List<string>(); }
        }

        private static void CheckForRepairState(GameFolder game)
        {
            try {
                string statePath = Path.Combine(game.RootPath, ".rip_repair_state.json");
                if (File.Exists(statePath))
                {
                    var json = File.ReadAllText(statePath);
                    var state = JsonSerializer.Deserialize<RepairState>(json);
                    if (state != null)
                    {
                        game.IsRepairInterrupted = true;
                        game.ProgressPercentage = state.Percentage;
                        game.ProgressDetails = state.Status;
                        game.ProgressPhase = "PAUSED";
                    }
                }
            } catch { }
        }

        private static float CalculateSimilarity(string exeName, string gameTitle)
        {
            if (string.IsNullOrEmpty(exeName) || string.IsNullOrEmpty(gameTitle)) return 0;
            exeName = exeName.ToLower();
            gameTitle = gameTitle.ToLower();

            var cleanTitle = System.Text.RegularExpressions.Regex.Replace(gameTitle, @"\s?v?\d+(\.\d+)*.*$", "").Trim();

            var words = cleanTitle.Split(new[] { ' ', '-', '_', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int matches = 0;
            int totalRelevantWords = 0;

            foreach (var word in words)
            {
                if (word.Length <= 2) continue;
                totalRelevantWords++;
                if (exeName.Contains(word)) matches++;
            }

            if (totalRelevantWords == 0) return exeName.Contains(cleanTitle) ? 1.0f : 0.0f;

            float score = (float)matches / totalRelevantWords;

            if (exeName == cleanTitle || exeName.Contains(cleanTitle)) score += 0.5f;

            return score;
        }
    }
}