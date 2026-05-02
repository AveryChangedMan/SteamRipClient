using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace SteamRipApp.Core
{
    public enum ThemeMode { SyncWithWindows, Dark, Light }
    public enum ExtractionMethod { UnRarDLL, WinRAR, SevenZip, Windows }
    public enum SpeedUnit { Bits, Bytes }

    public class GameConfig
    {
        public bool RunAsAdmin { get; set; }
        public string ManualExePath { get; set; } = "";
        public string LaunchArguments { get; set; } = "";
        public string WorkingDir { get; set; } = "";
        public int CpuPriority { get; set; } = 2;
        public bool DisableFullscreenOptimizations { get; set; }
        public string CompatibilityMode { get; set; } = "None";
        public bool HighDpiScaling { get; set; }
        public bool SuppressOverlays { get; set; }
        public int LaunchDelaySeconds { get; set; }
    }

    public class GameMetadata
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string Version { get; set; } = "";
        public string Hash { get; set; } = "";
        public DateTime DownloadDate { get; set; }
        public bool IsSteamIntegrated { get; set; }

        [JsonPropertyName("IsEmulatorApplied")]
        public bool IsEmulatorApplied { get; set; }

        public int? SteamAppId { get; set; }
        public int? ManualSteamAppId { get; set; }
        public long SizeBytes { get; set; }
        public long FileTime { get; set; }
        public string FileId { get; set; } = "";
        public string LocalImagePath { get; set; } = "";
        public bool IsRedistMissing { get; set; }
    }

    public class ActiveDownloadMetadata : System.ComponentModel.INotifyPropertyChanged
    {
        private string _title = "";
        private string _url = "";
        private string _version = "";
        private string _imageUrl = "";
        private string _destPath = "";
        private string _pageUrl = "";
        private string _steamRipUrl = "";
        private double _percentage;
        private string _status = "Downloading";
        private string _phase = "Downloading";

        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        public string SourceUrl { get => _url; set { _url = value; OnPropertyChanged(); } }
        public string Version { get => _version; set { _version = value; OnPropertyChanged(); } }
        public string ImageUrl { get => _imageUrl; set { _imageUrl = value; OnPropertyChanged(); } }
        public string DestPath { get => _destPath; set { _destPath = value; OnPropertyChanged(); } }
        public string PageUrl { get => _pageUrl; set { _pageUrl = value; OnPropertyChanged(); } }
        public string SteamRipUrl { get => _steamRipUrl; set { _steamRipUrl = value; OnPropertyChanged(); } }
        public double Percentage { get => _percentage; set { _percentage = value; OnPropertyChanged(); } }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public string Phase { get => _phase; set { _phase = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowLoadingBar)); } }
        public string Source { get => _source; set { _source = value; OnPropertyChanged(); } }
        public int ThreadCount { get => _threadCount; set { _threadCount = value; OnPropertyChanged(); } }
        public string SizeLabel { get => _sizeLabel; set { _sizeLabel = value; OnPropertyChanged(); } }

        private bool _isPaused = true;
        public bool IsPaused { get => _isPaused; set { _isPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(PauseSymbol)); } }
        public string PauseSymbol => IsPaused ? "Play" : "Pause";

        private string _source = "Unknown";
        private int _threadCount;
        private string _sizeLabel = "";
        private bool _isInLibrary;

        public bool IsInLibrary { get => _isInLibrary; set { _isInLibrary = value; NotifyAll(); } }

        public bool ShowLoadingBar => Phase != "Done" && Phase != "Failed";
        public bool ShowControlButtons => Phase != "Done" && Phase != "Failed";
        public bool ShowLibraryButton => Phase == "Done" && IsInLibrary;
        public bool ShowDownloadAgainButton => Phase == "Done" && !IsInLibrary;

        public void NotifyAll()
        {
            OnPropertyChanged(nameof(Phase));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Percentage));
            OnPropertyChanged(nameof(IsInLibrary));
            OnPropertyChanged(nameof(ShowControlButtons));
            OnPropertyChanged(nameof(ShowLibraryButton));
            OnPropertyChanged(nameof(ShowDownloadAgainButton));
            OnPropertyChanged(nameof(ShowLoadingBar));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class MoveOperation
    {
        public string SourcePath { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string GameTitle { get; set; } = "";
        public List<string> CopiedFiles { get; set; } = new List<string>();
        public bool IsCopyFinished { get; set; }
        public DateTime StartTime { get; set; }
    }

    public class UserData
    {
        public string DownloadDirectory { get; set; } = "";
        public bool AutoDeleteArchive { get; set; }
        public bool IsSetupCompleted { get; set; }
        public bool HasSelectedDownloadDirectory { get; set; }
        public string DefaultDownloadSource { get; set; } = "Buzzheavier";
        public List<string> ScanDirectories { get; set; } = new List<string>();
        public List<GameMetadata> Library { get; set; } = new List<GameMetadata>();
        public ObservableCollection<ActiveDownloadMetadata> ActiveDownloads { get; set; } = new ObservableCollection<ActiveDownloadMetadata>();
        public Dictionary<string, GameConfig> GameConfigs { get; set; } = new Dictionary<string, GameConfig>();
        public Dictionary<string, string> MemoryTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> GamePageLinks { get; set; } = new Dictionary<string, string>();
        public MoveOperation? CurrentMove { get; set; }
        public DateTime LastSpaceWarningTime { get; set; } = DateTime.MinValue;
        public ThemeMode AppThemeMode { get; set; } = ThemeMode.SyncWithWindows;
        public bool CloseAppOnLaunch { get; set; }
        public bool? RemoveFromSteamPreference { get; set; }
        public string SelectedSteamAccountId { get; set; } = "";
        public ExtractionMethod PreferredExtractionMethod { get; set; } = ExtractionMethod.UnRarDLL;
        public SpeedUnit DownloadSpeedUnit { get; set; } = SpeedUnit.Bits;
        public bool IsSteamIntegrationEnabled { get; set; } = true;
        public bool IsAdvancedModeEnabled { get; set; } = false;
        public bool IsHardRepairEnabled { get; set; } = false;
        public bool IsBackgroundHashingEnabled { get; set; } = false;
        public bool IsMultiThreadedHashingEnabled { get; set; } = false;
        public int HashingSpeedCapMB { get; set; } = 0;
        public bool AlwaysCreateRarMap { get; set; } = true;
        public bool UseRamForHashing { get; set; } = false;
        public bool AntivirusExclusionAdded { get; set; }
        public bool IsAdvancedRepairVisible { get; set; } = false;
        public bool IsHddModeEnabled { get; set; } = false;
        public string AppVersion { get; set; } = "1.4.1.0";
        public string? HashingProgress { get; set; }
        public bool IsSteamUpdateRequired { get; set; }
        public string RepairLogicVersion { get; set; } = "1.2.1";
    }

    public static class GlobalSettings
    {
        public static string DownloadDirectory { get; set; } = "";
        public static bool AutoDeleteArchive { get; set; } = true;
        public static bool IsSetupCompleted { get; set; }
        public static bool HasSelectedDownloadDirectory { get; set; }
        public static string DefaultDownloadSource { get; set; } = "Buzzheavier";
        public static List<string> ScanDirectories { get; set; } = new List<string>();
        public static List<GameMetadata> Library { get; set; } = new List<GameMetadata>();
        public static ObservableCollection<ActiveDownloadMetadata> ActiveDownloads { get; set; } = new ObservableCollection<ActiveDownloadMetadata>();
        public static Dictionary<string, GameConfig> GameConfigs { get; set; } = new Dictionary<string, GameConfig>();
        public static Dictionary<string, string> MemoryTable { get; set; } = new Dictionary<string, string>();
        public static Dictionary<string, string> GamePageLinks { get; set; } = new Dictionary<string, string>();
        public static MoveOperation? CurrentMove { get; set; }
        public static DateTime LastSpaceWarningTime { get; set; } = DateTime.MinValue;
        public static ThemeMode AppThemeMode { get; set; } = ThemeMode.SyncWithWindows;
        public static bool CloseAppOnLaunch { get; set; }
        public static bool? RemoveFromSteamPreference { get; set; }
        public static string SelectedSteamAccountId { get; set; } = "";
        public static SpeedUnit DownloadSpeedUnit { get; set; } = SpeedUnit.Bits;
        public static ExtractionMethod PreferredExtractionMethod { get; set; } = ExtractionMethod.WinRAR;
        public static bool IsSteamIntegrationEnabled { get; set; }
        public static bool IsAdvancedModeEnabled { get; set; }
        public static bool IsHardRepairEnabled { get; set; } = false;
        public static bool IsBackgroundHashingEnabled { get; set; } = false;
        public static bool IsMultiThreadedHashingEnabled { get; set; } = false;
        public static int HashingSpeedCapMB { get; set; } = 0;
        public static bool AlwaysCreateRarMap { get; set; } = true;
        public static bool UseRamForHashing { get; set; } = false;
        public static bool AntivirusExclusionAdded { get; set; }
        public static string? HashingProgress { get; set; }
        public static double HashingProgressValue { get; set; }
        public static bool IsSteamUpdateRequired { get; set; }
        public static bool IsAdvancedRepairVisible { get; set; } = false;
        public static bool IsHddModeEnabled { get; set; } = false;
        public static string AppVersion { get; set; } = "1.4.8.6";
        public static string RepairLogicVersion { get; set; } = "1.2.1";

        private static readonly string SavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp", "settings.json");
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public static string GetGameHash(string title, string url)
        {
            if (string.IsNullOrEmpty(title)) title = "Unknown";
            string raw = (title + (url ?? "")).ToLowerInvariant();
            using var sha = System.Security.Cryptography.SHA1.Create();
            byte[] bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexStringLower(bytes).Substring(0, 20);
        }

        public static void Save()
        {
            try {
                if (!Directory.Exists(Path.GetDirectoryName(SavePath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);

                var data = new UserData {
                    DownloadDirectory = DownloadDirectory,
                    AutoDeleteArchive = AutoDeleteArchive,
                    IsSetupCompleted = IsSetupCompleted,
                    HasSelectedDownloadDirectory = HasSelectedDownloadDirectory,
                    DefaultDownloadSource = DefaultDownloadSource,
                    ScanDirectories = ScanDirectories,
                    Library = Library,
                    ActiveDownloads = ActiveDownloads,
                    GameConfigs = GameConfigs,
                    MemoryTable = MemoryTable,
                    GamePageLinks = GamePageLinks,
                    CurrentMove = CurrentMove,
                    LastSpaceWarningTime = LastSpaceWarningTime,
                    AppThemeMode = AppThemeMode,
                    CloseAppOnLaunch = CloseAppOnLaunch,
                    RemoveFromSteamPreference = RemoveFromSteamPreference,
                    SelectedSteamAccountId = SelectedSteamAccountId,
                    PreferredExtractionMethod = PreferredExtractionMethod,
                    DownloadSpeedUnit = DownloadSpeedUnit,
                    IsSteamIntegrationEnabled = IsSteamIntegrationEnabled,
                    IsAdvancedModeEnabled = IsAdvancedModeEnabled,
                    IsHardRepairEnabled = IsHardRepairEnabled,
                    IsBackgroundHashingEnabled = IsBackgroundHashingEnabled,
                    IsMultiThreadedHashingEnabled = IsMultiThreadedHashingEnabled,
                    HashingSpeedCapMB = HashingSpeedCapMB,
                    AlwaysCreateRarMap = AlwaysCreateRarMap,
                    UseRamForHashing = UseRamForHashing,
                    AntivirusExclusionAdded = AntivirusExclusionAdded,
                    HashingProgress = HashingProgress,
                    IsSteamUpdateRequired = IsSteamUpdateRequired,
                    IsAdvancedRepairVisible = IsAdvancedRepairVisible,
                    IsHddModeEnabled = IsHddModeEnabled,
                    AppVersion = AppVersion,
                    RepairLogicVersion = RepairLogicVersion
                };
                string json = JsonSerializer.Serialize(data, _jsonOptions);
                File.WriteAllText(SavePath, json);
            } catch { }
        }

        public static void Load()
        {
            try {
                if (File.Exists(SavePath)) {

                    try {
                        string backupDir = Path.Combine(Path.GetDirectoryName(SavePath)!, "Backups");
                        if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
                        string backupPath = Path.Combine(backupDir, $"settings_pre_1.4.1.0_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                        File.Copy(SavePath, backupPath, true);
                    } catch { }

                    string json = File.ReadAllText(SavePath);
                    var data = JsonSerializer.Deserialize<UserData>(json, _jsonOptions);
                    if (data != null) {
                        DownloadDirectory = data.DownloadDirectory;
                        AutoDeleteArchive = data.AutoDeleteArchive;
                        IsSetupCompleted = data.IsSetupCompleted;
                        HasSelectedDownloadDirectory = data.HasSelectedDownloadDirectory;
                        DefaultDownloadSource = data.DefaultDownloadSource ?? "Buzzheavier";
                        if (!HasSelectedDownloadDirectory && !string.IsNullOrEmpty(DownloadDirectory))
                            HasSelectedDownloadDirectory = true;
                        ScanDirectories = data.ScanDirectories ?? new List<string>();
                        Library = data.Library ?? new List<GameMetadata>();

                        if (data.ActiveDownloads != null)
                        {
                            foreach (var diskDl in data.ActiveDownloads)
                            {
                                if (!ActiveDownloads.Any(a => a.SourceUrl == diskDl.SourceUrl))
                                {
                                    ActiveDownloads.Add(diskDl);
                                }
                            }
                        }

                        GameConfigs = data.GameConfigs ?? new Dictionary<string, GameConfig>();
                        MemoryTable = data.MemoryTable ?? new Dictionary<string, string>();
                        GamePageLinks = data.GamePageLinks ?? new Dictionary<string, string>();
                        CurrentMove = data.CurrentMove;
                        LastSpaceWarningTime = data.LastSpaceWarningTime;
                        AppThemeMode = data.AppThemeMode;
                        CloseAppOnLaunch = data.CloseAppOnLaunch;
                        RemoveFromSteamPreference = data.RemoveFromSteamPreference;
                        SelectedSteamAccountId = data.SelectedSteamAccountId ?? "";
                        PreferredExtractionMethod = data.PreferredExtractionMethod;
                        DownloadSpeedUnit = data.DownloadSpeedUnit;
                        IsSteamIntegrationEnabled = data.IsSteamIntegrationEnabled;
                        IsAdvancedModeEnabled = data.IsAdvancedModeEnabled;
                        IsHardRepairEnabled = data.IsHardRepairEnabled;
                        IsBackgroundHashingEnabled = data.IsBackgroundHashingEnabled;
                        IsMultiThreadedHashingEnabled = data.IsMultiThreadedHashingEnabled;
                        HashingSpeedCapMB = data.HashingSpeedCapMB;
                        AlwaysCreateRarMap = data.AlwaysCreateRarMap;
                        UseRamForHashing = data.UseRamForHashing;
                        AntivirusExclusionAdded = data.AntivirusExclusionAdded;
                        HashingProgress = data.HashingProgress;
                        IsSteamUpdateRequired = data.IsSteamUpdateRequired;
                        IsAdvancedRepairVisible = data.IsAdvancedRepairVisible;
                        AppVersion = data.AppVersion ?? "1.4.1.0";
                        RepairLogicVersion = data.RepairLogicVersion ?? "1.2.1";
                        IsHddModeEnabled = data.IsHddModeEnabled;

                        HashingProgress = null;
                        HashingProgressValue = 0;
                    }
                }
            } catch { }
        }

        public static void ResetSettings()
        {

            AutoDeleteArchive = true;
            DefaultDownloadSource = "Buzzheavier";
            AppThemeMode = ThemeMode.SyncWithWindows;
            CloseAppOnLaunch = false;
            RemoveFromSteamPreference = null;
            SelectedSteamAccountId = "";
            PreferredExtractionMethod = ExtractionMethod.UnRarDLL;
            DownloadSpeedUnit = SpeedUnit.Bits;
            IsSteamIntegrationEnabled = false;
            IsAdvancedModeEnabled = false;
            IsHardRepairEnabled = false;
            IsBackgroundHashingEnabled = false;
            IsMultiThreadedHashingEnabled = false;
            HashingSpeedCapMB = 0;
            AlwaysCreateRarMap = true;
            UseRamForHashing = false;
            IsAdvancedRepairVisible = false;
            IsSetupCompleted = false;
            Save();
        }
        public static HashSet<string> GetTrustedFiles(string storagePath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(storagePath, ".mods.json");
            if (File.Exists(path))
            {
                try {
                    var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), _jsonOptions);
                    if (list != null) foreach (var f in list) set.Add(f);
                } catch { }
            }
            return set;
        }
    }
}