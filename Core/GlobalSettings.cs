using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace SteamRipApp.Core
{
    public enum ThemeMode { SyncWithWindows, Dark, Light }
    public enum ExtractionMethod { WinRAR, SevenZip, Windows }
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
        
        [JsonPropertyName("IsGoldbergInitialized")]
        public bool IsEmulatorApplied { get; set; }

        public int? SteamAppId { get; set; }
        public int? ManualSteamAppId { get; set; }
        public long SizeBytes { get; set; }
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
        public string Phase { get => _phase; set { _phase = value; OnPropertyChanged(); } }
        public string Source { get => _source; set { _source = value; OnPropertyChanged(); } }
        public int ThreadCount { get => _threadCount; set { _threadCount = value; OnPropertyChanged(); } }
        public string SizeLabel { get => _sizeLabel; set { _sizeLabel = value; OnPropertyChanged(); } }

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
        public List<ActiveDownloadMetadata> ActiveDownloads { get; set; } = new List<ActiveDownloadMetadata>();
        public Dictionary<string, GameConfig> GameConfigs { get; set; } = new Dictionary<string, GameConfig>();
        public Dictionary<string, string> MemoryTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> GamePageLinks { get; set; } = new Dictionary<string, string>();
        public MoveOperation? CurrentMove { get; set; }
        public DateTime LastSpaceWarningTime { get; set; } = DateTime.MinValue;
        public ThemeMode AppThemeMode { get; set; } = ThemeMode.SyncWithWindows;
        public bool CloseAppOnLaunch { get; set; }
        public bool? RemoveFromSteamPreference { get; set; }
        public string SelectedSteamAccountId { get; set; } = "";
        public ExtractionMethod? PreferredExtractionMethod { get; set; }
        public SpeedUnit DownloadSpeedUnit { get; set; } = SpeedUnit.Bits;
        public bool IsSteamIntegrationEnabled { get; set; }
        public bool IsAdvancedModeEnabled { get; set; } = false;
        public bool IsHardRepairEnabled { get; set; } = true;
        public bool IsBackgroundHashingEnabled { get; set; } = false; 
        public bool IsMultiThreadedHashingEnabled { get; set; } = true;
        public string? HashingProgress { get; set; }
        public bool IsSteamUpdateRequired { get; set; }
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
        public static List<ActiveDownloadMetadata> ActiveDownloads { get; set; } = new List<ActiveDownloadMetadata>();
        public static Dictionary<string, GameConfig> GameConfigs { get; set; } = new Dictionary<string, GameConfig>();
        public static Dictionary<string, string> MemoryTable { get; set; } = new Dictionary<string, string>();
        public static Dictionary<string, string> GamePageLinks { get; set; } = new Dictionary<string, string>();
        public static MoveOperation? CurrentMove { get; set; }
        public static DateTime LastSpaceWarningTime { get; set; } = DateTime.MinValue;
        public static ThemeMode AppThemeMode { get; set; } = ThemeMode.SyncWithWindows;
        public static bool CloseAppOnLaunch { get; set; }
        public static bool? RemoveFromSteamPreference { get; set; }
        public static string SelectedSteamAccountId { get; set; } = "";
        public static ExtractionMethod? PreferredExtractionMethod { get; set; }
        public static SpeedUnit DownloadSpeedUnit { get; set; } = SpeedUnit.Bits;
        public static bool IsSteamIntegrationEnabled { get; set; } = false;
        public static bool IsAdvancedModeEnabled { get; set; } = false;
        public static bool IsHardRepairEnabled { get; set; } = true;
        public static bool IsBackgroundHashingEnabled { get; set; } = false;
        public static bool IsMultiThreadedHashingEnabled { get; set; } = true;
        public static string? HashingProgress { get; set; }
        public static double HashingProgressValue { get; set; }
        public static bool IsSteamUpdateRequired { get; set; }

        private static readonly string SavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp", "settings.json");

        public static string GetGameHash(string title, string url)
        {
            if (string.IsNullOrEmpty(title)) title = "Unknown";
            string raw = (title + (url ?? "")).ToLowerInvariant();
            using var sha = System.Security.Cryptography.SHA1.Create();
            byte[] bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant().Substring(0, 20);
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
                    HashingProgress = HashingProgress,
                    IsSteamUpdateRequired = IsSteamUpdateRequired
                };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SavePath, json);
            } catch { }
        }

        public static void Load()
        {
            try {
                if (File.Exists(SavePath)) {
                    string json = File.ReadAllText(SavePath);
                    var data = JsonSerializer.Deserialize<UserData>(json);
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
                        ActiveDownloads = data.ActiveDownloads ?? new List<ActiveDownloadMetadata>();
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
                        HashingProgress = data.HashingProgress;
                        IsSteamUpdateRequired = data.IsSteamUpdateRequired;
                    }
                }
            } catch { }
        }
    }
}
