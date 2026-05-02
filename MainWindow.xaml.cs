using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRipApp
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            this.Activated += Window_FirstActivated;
            this.Closed += MainWindow_Closed;
            StartHashingProgressTimer();
        }

        private readonly CancellationTokenSource _shutdownCts = new();

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _shutdownCts.Cancel();
            _hashingTimer?.Stop();
            GlobalSettings.Save();
            Logger.Log("--- GUI SESSION CLOSED ---");
        }

        private DispatcherTimer? _hashingTimer;

        private async void Window_FirstActivated(object sender, WindowActivatedEventArgs e)
        {
            this.Activated -= Window_FirstActivated;

            SetAppIcon();

            if (NavView != null && NavView.MenuItems.Count > 0)
            {
                NavView.SelectedItem = NavView.MenuItems[0];
                ContentFrame.Navigate(typeof(HomePage));
                NavView.Header = "Home";
            }

            CleanupActiveDownloads();
            UpdateAdvancedTabsVisibility();

            _ = CheckForUpdatesAsync(_shutdownCts.Token);

            await System.Threading.Tasks.Task.Delay(300);
            await CheckFirstRunSetupAsync();
        }

        public void NavigateTo(Type pageType)
        {
            ContentFrame.Navigate(pageType);
            if (pageType == typeof(HomePage)) NavView.SelectedItem = NavView.MenuItems[0];
            else if (pageType == typeof(LibraryPage)) NavView.SelectedItem = NavView.MenuItems[1];
            else if (pageType == typeof(DownloadsPage)) NavView.SelectedItem = NavView.MenuItems[2];
        }

        private void StartHashingProgressTimer()
        {
            _hashingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hashingTimer.Tick += (s, e) => {
                if (!string.IsNullOrEmpty(GlobalSettings.HashingProgress))
                {
                    HashingStatusArea.Visibility = Visibility.Visible;
                    HashingStatusText.Text = GlobalSettings.HashingProgress;
                    HashingProgressBar.Value = GlobalSettings.HashingProgressValue;
                }
                else
                {
                    HashingStatusArea.Visibility = Visibility.Collapsed;
                }
            };
            _hashingTimer.Start();
        }

        private void StopHashing_Click(object sender, RoutedEventArgs e)
        {
            RepairService.StopBackgroundHashing();
            GlobalSettings.HashingProgress = null;
            GlobalSettings.HashingProgressValue = 0;
            HashingStatusArea.Visibility = Visibility.Collapsed;
        }

        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentConfigPath))
            {
                RepairService.TriggerManualBackup(_currentConfigPath, _currentContentPath ?? _currentConfigPath);
                ConfigOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void ResetBackup_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentConfigPath))
            {
                RepairService.StopHashingForGame(_currentConfigPath);
                ConfigOverlay.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateAdvancedTabsVisibility()
        {
            if (SteamNavItem != null)
                SteamNavItem.Visibility = GlobalSettings.IsSteamIntegrationEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task CheckForUpdatesAsync(CancellationToken ct = default)
        {
            try {
                if (ct.IsCancellationRequested) return;
                Logger.Log("[Update] Checking for app updates...");
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "SteamRipApp");
                string json = await client.GetStringAsync("https://api.github.com/repos/AveryChangedMan/SteamRipClient/releases/latest");
                var doc = JsonDocument.Parse(json);
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                if (string.IsNullOrEmpty(tag)) return;

                string remoteRaw = tag.TrimStart('v');
                string localRaw = GlobalSettings.AppVersion.TrimStart('v');

                if (IsUpdateNeeded(localRaw, remoteRaw))
                {

                    if (GlobalSettings.ActiveDownloads.Any(d => d.Phase is "Downloading" or "Extracting") ||
                        !string.IsNullOrEmpty(GlobalSettings.HashingProgress))
                    {
                        Logger.Log($"[Update] Update available ({remoteRaw}) but deferred — active download/hashing in progress.");
                        return;
                    }

                    string htmlUrl = doc.RootElement.GetProperty("html_url").GetString() ?? "";
                    var dialog = new ContentDialog {
                        Title = "Update Available",
                        Content = $"A new version ({remoteRaw}) is available.\nYou are running {localRaw}.\n\nWould you like to download it?",
                        PrimaryButtonText = "Download",
                        CloseButtonText = "Later",
                        XamlRoot = this.Content.XamlRoot
                    };

                    if (await App.ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrEmpty(htmlUrl))
                        await Windows.System.Launcher.LaunchUriAsync(new Uri(htmlUrl));
                }
            } catch (Exception ex) {
                Logger.Log($"[Update] Check failed: {ex.Message}");
            }
        }

        private static bool IsUpdateNeeded(string localVersion, string remoteVersion)
        {
            try
            {
                var local = ParseVersion(localVersion);
                var remote = ParseVersion(remoteVersion);

                return remote.x1 > local.x1
                    || (remote.x1 == local.x1 && remote.x2 > local.x2)
                    || (remote.x1 == local.x1 && remote.x2 == local.x2 && remote.x3 > local.x3);
            }
            catch { return false; }
        }

        private static (int x1, int x2, int x3, int x4) ParseVersion(string v)
        {
            var parts = v.Split('.');
            static int ParseSafe(string s) => int.TryParse(s, out int i) ? i : 0;

            int x1 = parts.Length > 0 ? ParseSafe(parts[0]) : 0;
            int x2 = parts.Length > 1 ? ParseSafe(parts[1]) : 0;
            int x3 = parts.Length > 2 ? ParseSafe(parts[2]) : 0;
            int x4 = parts.Length > 3 ? ParseSafe(parts[3]) : 0;
            return (x1, x2, x3, x4);
        }

        private void SetAppIcon()
        {
            try {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                
                string[] possiblePaths = {
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.ico"),
                    System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.ico"),
                    System.IO.Path.Combine(Directory.GetCurrentDirectory(), "Assets", "app_icon.ico"),
                    "Assets/app_icon.ico"
                };

                string? iconPath = possiblePaths.FirstOrDefault(p => System.IO.File.Exists(p));

                if (!string.IsNullOrEmpty(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                    
                    // Native fallback for taskbar/Alt-Tab reliability
                    try {
                        IntPtr hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x00000010); // IMAGE_ICON, LR_LOADFROMFILE
                        if (hIcon != IntPtr.Zero)
                        {
                            const uint WM_SETICON = 0x0080;
                            SendMessage(hWnd, WM_SETICON, (IntPtr)0, hIcon); // Small icon
                            SendMessage(hWnd, WM_SETICON, (IntPtr)1, hIcon); // Big icon
                        }
                    } catch { }
                }
            } catch (Exception ex) {
                Logger.Log($"[MainWindow] Failed to set window icon: {ex.Message}");
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        private void CleanupActiveDownloads()
        {

            ScanForOrphanedDownloads();

            CleanupLibrary();

            var toRemove = GlobalSettings.ActiveDownloads.Where(d => string.IsNullOrEmpty(d.Title)).ToList();
            foreach (var d in toRemove) GlobalSettings.ActiveDownloads.Remove(d);
            int removed = toRemove.Count;

            if (removed > 0)
            {
                Logger.Log($"[Cleanup] Pruned {removed} invalid download entries.");
                GlobalSettings.Save();
            }
        }

        private void CleanupLibrary()
        {
            try {
                int removed = GlobalSettings.Library.RemoveAll(m => {
                    if (string.IsNullOrEmpty(m.LocalPath)) return true;
                    string? root = System.IO.Path.GetPathRoot(m.LocalPath);

                    if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    {
                        return !Directory.Exists(m.LocalPath);
                    }
                    return false;
                });

                if (removed > 0)
                {
                    Logger.Log($"[Cleanup] Automatically removed {removed} ghost entries from library.");
                    GlobalSettings.Save();
                }
            } catch { }
        }

        private void ScanForOrphanedDownloads()
        {
            try {
                if (string.IsNullOrEmpty(GlobalSettings.DownloadDirectory) || !Directory.Exists(GlobalSettings.DownloadDirectory)) return;

                var manifests = Directory.GetFiles(GlobalSettings.DownloadDirectory, "*.progress", SearchOption.AllDirectories);
                foreach (var mPath in manifests)
                {
                    string destPath = mPath.Substring(0, mPath.Length - 9);
                    if (!File.Exists(destPath)) continue;

                    if (!GlobalSettings.ActiveDownloads.Any(d => d.DestPath.Equals(destPath, StringComparison.OrdinalIgnoreCase)))
                    {

                        try {
                            var json = File.ReadAllText(mPath);
                            var manifest = System.Text.Json.JsonSerializer.Deserialize<DownloadManifest>(json);
                            if (manifest != null && manifest.Chunks != null && manifest.TotalBytes >= 0)
                            {
                                string title = Path.GetFileNameWithoutExtension(destPath).Replace("_", " ");
                                double totalDownloaded = manifest.Chunks.Sum(c => (double)c.Downloaded);
                                double pct = manifest.TotalBytes > 0 ? (totalDownloaded * 100.0) / manifest.TotalBytes : 0;
                                if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0;

                                var metadata = new ActiveDownloadMetadata {
                                    Title = title,
                                    SourceUrl = manifest.DownloadUrl ?? "",
                                    PageUrl = manifest.BuzzheavierPageUrl ?? "",
                                    DestPath = destPath,
                                    Status = "⏹ Orphaned (Ready to Resume)",
                                    Percentage = Math.Clamp(pct, 0, 100),
                                    Source = (manifest.DownloadUrl?.Contains("gofile") == true) ? "Gofile" : "Buzzheavier"
                                };
                                GlobalSettings.ActiveDownloads.Add(metadata);
                                Logger.Log($"[Recovery] Restored orphaned download: {title} ({pct:F1}%)");
                            }
                        } catch { }
                    }
                }
                GlobalSettings.Save();
            } catch (Exception ex) {
                Logger.LogError("ScanOrphans", ex);
            }
        }

        private async System.Threading.Tasks.Task CheckFirstRunSetupAsync()
        {
            var current = ParseVersion(GlobalSettings.AppVersion);
            var target = ParseVersion("1.4.6.0");

            bool isOldVersion = (current.x1 < target.x1)
                || (current.x1 == target.x1 && current.x2 < target.x2)
                || (current.x1 == target.x1 && current.x2 == target.x2 && current.x3 < target.x3);

            if (GlobalSettings.HasSelectedDownloadDirectory && !isOldVersion) return;
            SetupOverlay.Visibility = Visibility.Visible;
            NavView.IsHitTestVisible = false;
        }

        private async void SetupSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(path))
            {
                GlobalSettings.DownloadDirectory = path;
                Step1Path.Text = path;
                Step1Icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);

                SetupScanBtn.IsEnabled = true;
                SetupScanStep.Opacity = 1.0;

                SetupLegacyBtn.IsEnabled = true;
                SetupLegacyStep.Opacity = 1.0;

                CheckSetupCompletion();
            }
        }

        private async void SetupScanManage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog {
                Title = "Scan Locations",
                Content = "The app will look for games in your Download Directory by default.\n\nYou can add more folders (like your Steam Library or other Game drives) here.",
                PrimaryButtonText = "Add Folder",
                CloseButtonText = "Done",
                XamlRoot = this.Content.XamlRoot
            };

            while (true) {
                var result = await App.ShowDialogSafeAsync(dialog);
                if (result == ContentDialogResult.Primary) {
                    var path = await PickerService.PickFolderAsync();
                    if (!string.IsNullOrEmpty(path) && !GlobalSettings.ScanDirectories.Contains(path)) {
                        GlobalSettings.ScanDirectories.Add(path);
                        Step3Status.Text = $"{GlobalSettings.ScanDirectories.Count} folder(s) configured.";
                    }
                    Step2Icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                } else {
                    Step2Icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                    break;
                }
            }

            Step3Icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            CheckSetupCompletion();
        }

        private async void SetupLegacyCheck_Click(object sender, RoutedEventArgs e)
        {
            Step4Status.Text = "Scanning for legacy emulators...";
            await Task.Run(() => {

                Task.Delay(1000).Wait();
            });
            Step4Icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            Step4Status.Text = "No legacy patches found.";

            SetupFinishStep.Opacity = 1.0;
            Step5Icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            CheckSetupCompletion();
        }

        private void CheckSetupCompletion()
        {
            if (!string.IsNullOrEmpty(GlobalSettings.DownloadDirectory))
            {
                FinishSetupBtn.IsEnabled = true;
            }
        }

        private async void FinishSetup_Click(object sender, RoutedEventArgs e)
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            NavView.IsHitTestVisible = true;

            GlobalSettings.IsHardRepairEnabled = SetupHardRepairCheck.IsChecked ?? true;
            GlobalSettings.IsMultiThreadedHashingEnabled = SetupMultiThreadCheck.IsChecked ?? true;
            GlobalSettings.AlwaysCreateRarMap = SetupRarMapCheck.IsChecked ?? true;
            GlobalSettings.HashingSpeedCapMB = (int)SetupSpeedSlider.Value;
            GlobalSettings.IsSteamIntegrationEnabled = SetupSteamCheck.IsChecked ?? true;
            if (SetupDefenderCheck.IsChecked == true)
            {
                GlobalSettings.AntivirusExclusionAdded = true;

                _ = Task.Run(() => {
                    try {
                        string downloadDir = GlobalSettings.DownloadDirectory;
                        string psCommand = "sc.exe delete SteamRipInjector";
                        if (!string.IsNullOrEmpty(downloadDir))
                        {
                            psCommand += $"; Add-MpPreference -ExclusionPath '{downloadDir}'";
                        }

                        Process.Start(new ProcessStartInfo {
                            FileName = "powershell.exe",
                            Arguments = $"-Command \"{psCommand}\"",
                            CreateNoWindow = true,
                            UseShellExecute = true,
                            Verb = "runas"
                        });
                    } catch { }
                });
            }

            GlobalSettings.HasSelectedDownloadDirectory = true;
            if (!GlobalSettings.ScanDirectories.Contains(GlobalSettings.DownloadDirectory))
                GlobalSettings.ScanDirectories.Add(GlobalSettings.DownloadDirectory);

            GlobalSettings.IsSetupCompleted = true;
            GlobalSettings.AppVersion = "1.4.6.0";
            GlobalSettings.Save();

            Logger.Log($"[Setup] Completed. Download Dir: {GlobalSettings.DownloadDirectory}");

            UpdateAdvancedTabsVisibility();

            try
            {
                if (Directory.Exists(GlobalSettings.DownloadDirectory) &&
                    Directory.GetFileSystemEntries(GlobalSettings.DownloadDirectory).Length > 0 &&
                    GlobalSettings.Library.Count == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            var indexDialog = new ContentDialog
                            {
                                Title = "Existing Games Detected",
                                Content = "Your download directory is not empty. Would you like to automatically index and generate integrity maps for the found games?\n\nThis is recommended for full repair support.",
                                PrimaryButtonText = "Index & Hash",
                                CloseButtonText = "Later",
                                XamlRoot = this.Content.XamlRoot
                            };

                            if (await App.ShowDialogSafeAsync(indexDialog) == ContentDialogResult.Primary)
                            {
                                ContentFrame.Navigate(typeof(LibraryPage));
                                NavView.SelectedItem = NavView.MenuItems[1];

                            }
                        });
                    });
                }
            }
            catch { }

            ContentFrame.Navigate(typeof(HomePage));
            NavView.SelectedItem = NavView.MenuItems[0];
        }

        public void UpdateGlobalProgress(double pct, bool visible = true)
        {
            GlobalProgressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            GlobalProgressBar.Value = pct;
        }

        public void ShowGlobalOverlay(string title, string status)
        {
            NavView.IsHitTestVisible = false;
            GlobalOverlayTitle.Text = title;
            GlobalOverlayStatus.Text = status;
            GlobalOverlaySubStatus.Text = "";
            GlobalOverlayProgressBar.Value = 0;
            GlobalOverlayProgressBar.IsIndeterminate = true;
            GlobalProgressOverlay.Visibility = Visibility.Visible;
        }

        public void UpdateGlobalOverlay(string status, double? progress = null, string? subStatus = null)
        {
            GlobalOverlayStatus.Text = status;
            if (progress.HasValue)
            {
                GlobalOverlayProgressBar.IsIndeterminate = false;
                GlobalOverlayProgressBar.Value = progress.Value;
            }
            if (subStatus != null) GlobalOverlaySubStatus.Text = subStatus;
        }

        public void HideGlobalOverlay()
        {
            NavView.IsHitTestVisible = true;
            GlobalProgressOverlay.Visibility = Visibility.Collapsed;
        }

        private void GlobalOverlayHide_Click(object sender, RoutedEventArgs e)
        {
            HideGlobalOverlay();
        }

        private async void RepairChanges_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentConfigPath)) return;
            ConfigOverlay.Visibility = Visibility.Collapsed;

            Logger.Log($"[Repair-Changes] Analyzing '{_currentConfigPath}' for additions/mods...");
            var report = await RepairService.AnalyzeGameAsync(_currentConfigPath, _currentContentPath ?? _currentConfigPath, null);

            if (report.AddedFiles.Count > 0)
            {
                var listView = new ListView { ItemsSource = report.AddedFiles, SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 300 };

                foreach(var item in report.AddedFiles) listView.SelectedItems.Add(item);

                var dialog = new ContentDialog {
                    Title = "Added Files Detected (Mods/Patches)",
                    Content = new StackPanel { Spacing = 8, Children = {
                        new TextBlock { Text = "The following files are not part of the original game. What would you like to do?", TextWrapping = TextWrapping.Wrap },
                        listView
                    }},
                    PrimaryButtonText = "Quarantine Selected",
                    SecondaryButtonText = "Delete Selected",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await App.ShowDialogSafeAsync(dialog);
                if (result == ContentDialogResult.Primary)
                {
                    var toQuarantine = listView.SelectedItems.Cast<string>().ToList();
                    RepairService.QuarantineFiles(_currentConfigPath, _currentContentPath ?? _currentConfigPath, toQuarantine);
                    Logger.Log($"[Repair-Changes] Quarantined {toQuarantine.Count} files.");
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    var confirm = new ContentDialog {
                        Title = "Confirm Deletion",
                        Content = "Are you sure you want to permanently delete these files? This cannot be undone.",
                        PrimaryButtonText = "Delete",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.Content.XamlRoot
                    };
                    if (await App.ShowDialogSafeAsync(confirm) == ContentDialogResult.Primary)
                    {
                        var toDelete = listView.SelectedItems.Cast<string>().ToList();
                        RepairService.DeleteAddedFiles(_currentContentPath ?? _currentConfigPath, toDelete);
                        Logger.Log($"[Repair-Changes] Deleted {toDelete.Count} files.");
                    }
                }
            }
            else
            {

                var qDir = Path.Combine(_currentContentPath ?? _currentConfigPath, "_Quarantine");
                if (Directory.Exists(qDir) && Directory.GetFileSystemEntries(qDir).Length > 0)
                {
                    var dialog = new ContentDialog {
                        Title = "Quarantined Files Found",
                        Content = "Would you like to unquarantine all files for this game?",
                        PrimaryButtonText = "Unquarantine All",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.Content.XamlRoot
                    };
                    if (await App.ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
                    {
                        RepairService.UnquarantineFiles(_currentConfigPath, _currentContentPath ?? _currentConfigPath);
                    }
                }
                else
                {
                    var dialog = new ContentDialog {
                        Title = "No Changes Detected",
                        Content = "No additional files (mods/patches) or quarantined files were found.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await App.ShowDialogSafeAsync(dialog);
                }
            }
        }

        public void SetGlobalMoveStatus(string status, bool visible = true)
        {
            GlobalMoveStatus.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            GlobalMoveText.Text = status;
        }

        private void GlobalCancelMove_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is LibraryPage libraryPage)
            {
                libraryPage.CancelMove();
            }
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
                NavView.Header = "Settings";
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "home":
                        ContentFrame.Navigate(typeof(HomePage));
                        NavView.Header = "Home";
                        break;
                    case "steam":
                        ContentFrame.Navigate(typeof(SteamPage));
                        NavView.Header = "Steam Library Beta";
                        break;
                    case "downloads":
                        ContentFrame.Navigate(typeof(DownloadsPage));
                        NavView.Header = "Downloads";
                        break;
                    case "library":
                        ContentFrame.Navigate(typeof(LibraryPage));
                        NavView.Header = "My Library";
                        break;
                    case "cleanup":
                        ContentFrame.Navigate(typeof(CleanupView));
                        NavView.Header = "Cleanup & Storage";
                        break;
                    case "help":
                        ContentFrame.Navigate(typeof(HelpPage));
                        NavView.Header = "Help & Documentation";
                        break;
                }
            }
        }

        private string? _currentConfigPath;
        private string? _currentContentPath;

        public void OpenConfig(string path, string title, string? contentPath = null, string? exePath = null)
        {
            _currentConfigPath = path;
            _currentContentPath = contentPath ?? path;
            if (!GlobalSettings.GameConfigs.ContainsKey(path))
                GlobalSettings.GameConfigs[path] = new GameConfig();

            var config = GlobalSettings.GameConfigs[path];

            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (meta == null)
            {
                meta = new GameMetadata { LocalPath = path, Title = title };
                GlobalSettings.Library.Add(meta);
            }

            string currentExe = config.ManualExePath ?? "";
            bool isUtility = !string.IsNullOrEmpty(currentExe) && (currentExe.Contains("crashpad", StringComparison.OrdinalIgnoreCase) || currentExe.Contains("handler", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(config.ManualExePath) || isUtility)
            {
                if (!string.IsNullOrEmpty(exePath) && !exePath.Contains("crashpad", StringComparison.OrdinalIgnoreCase) && !exePath.Contains("handler", StringComparison.OrdinalIgnoreCase))
                    config.ManualExePath = exePath;
                else
                {
                    var exes = Directory.GetFiles(path, "*.exe", SearchOption.AllDirectories)
                                        .Where(f => !f.Contains("crashpad", StringComparison.OrdinalIgnoreCase) && !f.Contains("handler", StringComparison.OrdinalIgnoreCase))
                                        .OrderByDescending(f => new FileInfo(f).Length)
                                        .ToList();
                    if (exes.Count > 0) config.ManualExePath = exes[0];
                }

                if (!string.IsNullOrEmpty(config.ManualExePath))
                    config.WorkingDir = System.IO.Path.GetDirectoryName(config.ManualExePath) ?? string.Empty;
            }

            ConfigTitle.Text = $"⚙️ {title}";

            ConfigOverlay.Visibility = Visibility.Visible;
            NavView.IsHitTestVisible = false;
            ConfigOptionsList.Children.Clear();

            ConfigOptionsList.Children.Add(new TextBlock { Text = "Steam AppID", Margin = new Thickness(0, 12, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var appIdGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            appIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            appIdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var appIdBox = new TextBox { Text = meta.ManualSteamAppId?.ToString() ?? meta.SteamAppId?.ToString() ?? "", PlaceholderText = "Manual ID overrides automatic search" };
            appIdBox.TextChanged += async (s, e) => {
                if (int.TryParse(appIdBox.Text, out int id)) {
                    int? oldId = meta.ManualSteamAppId ?? meta.SteamAppId;

                    if (meta.IsSteamIntegrated && oldId.HasValue && oldId.Value != id)
                    {
                        await SteamManager.ImportGameToSteam(title, path, id);
                    }

                    meta.ManualSteamAppId = id;
                    ScannerEngine.UpdateAppIdInFiles(path, id);
                    GlobalSettings.Save();
                } else if (string.IsNullOrEmpty(appIdBox.Text)) {
                    meta.ManualSteamAppId = null;
                    GlobalSettings.Save();
                }
            };
            appIdGrid.Children.Add(appIdBox);

            var resetBtn = new Button { Content = "Reset to Auto", Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(resetBtn, 1);
            resetBtn.Click += (s, e) => {
                meta.ManualSteamAppId = null;
                appIdBox.Text = meta.SteamAppId?.ToString() ?? "";
            };
            appIdGrid.Children.Add(resetBtn);
            ConfigOptionsList.Children.Add(appIdGrid);

            var searchAppIdBtn = new Button {
                Content = "🔍 Search Steam Store for AppID",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),
                BorderThickness = new Thickness(1)
            };
            searchAppIdBtn.Click += async (s, e) => {
                var selectedId = await ShowAppIdSearchDialog(title);
                if (selectedId.HasValue)
                {
                    appIdBox.Text = selectedId.Value.ToString();
                }
            };
            ConfigOptionsList.Children.Add(searchAppIdBtn);

            AddConfigText("Launch Arguments", config.LaunchArguments ?? "", (val) => config.LaunchArguments = val);

            AddConfigPath("Executable Path", config.ManualExePath ?? "", (val) => config.ManualExePath = val, isFolder: false);

            string displayWorkingDir = config.WorkingDir;
            if (string.IsNullOrEmpty(displayWorkingDir) && !string.IsNullOrEmpty(config.ManualExePath))
            {
                displayWorkingDir = Path.GetDirectoryName(config.ManualExePath) ?? string.Empty;
            }
            AddConfigPath("Working Directory", displayWorkingDir ?? string.Empty, (val) => config.WorkingDir = val, isFolder: true);

            AddConfigToggle("Run as Administrator", config.RunAsAdmin, (val) => config.RunAsAdmin = val);

            AddConfigToggle("Disable Fullscreen Optimizations", config.DisableFullscreenOptimizations, (val) => config.DisableFullscreenOptimizations = val);

            AddConfigToggle("High DPI Scaling Override", config.HighDpiScaling, (val) => config.HighDpiScaling = val);

            AddConfigToggle("Suppress Game Overlays", config.SuppressOverlays, (val) => config.SuppressOverlays = val);

            AddConfigNumber("Launch Delay (seconds)", config.LaunchDelaySeconds, (val) => config.LaunchDelaySeconds = val);

            AddConfigCombo("CPU Priority", new[] { "Low", "Normal", "High", "Realtime" }, config.CpuPriority, (val) => config.CpuPriority = val);

            AddConfigCombo("Compatibility Mode", new[] { "None", "Windows 7", "Windows 8" }, 0, (val) => { });

            if (meta.IsEmulatorApplied)
            {
                ConfigOptionsList.Children.Add(new TextBlock {
                    Text = "Legacy Emulator Detected",
                    Margin = new Thickness(0, 16, 0, 8),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange)
                });
                ConfigOptionsList.Children.Add(new TextBlock {
                    Text = "This game has an older Goldberg patch applied. It is recommended to reverse it for better compatibility with modern Steam features.",
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
                });

                var revBtn = new Button {
                    Content = "🛠 Reverse Legacy Patch",
                    IsEnabled = true,
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                    Padding = new Thickness(12, 6, 12, 6)
                };
                ConfigOptionsList.Children.Add(revBtn);

                revBtn.Click += async (s, e) => {
                    Logger.Log($"Reversing legacy Goldberg patch for {title}...");

                    meta.IsEmulatorApplied = false;
                    GlobalSettings.Save();
                    RefreshLibrary();

                    ConfigOverlay.Visibility = Visibility.Collapsed;
                    OpenConfig(path, title, contentPath, exePath);
                };
            }

                ConfigOptionsList.Children.Add(new TextBlock {
                    Text = "New Files",
                    Margin = new Thickness(0, 12, 0, 8),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold
                });
                var newFilesPanel = new StackPanel {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 0, 0, 12)
                };

                var unqBtn = new Button {
                    Content = "📦 Unquarantine",
                    Padding = new Thickness(12, 6, 12, 6)
                };
                unqBtn.Click += (s, e) => {
                    RepairService.UnquarantineFiles(path, contentPath ?? path);
                    ConfigOverlay.Visibility = Visibility.Collapsed;
                    OpenConfig(path, title, contentPath, exePath);
                };

                var scanAgainBtn = new Button {
                    Content = "🔎 Scan Again",
                    Padding = new Thickness(12, 6, 12, 6)
                };
                scanAgainBtn.Click += async (s, e) => {
                    ConfigOverlay.Visibility = Visibility.Collapsed;
                    await RepairService.AnalyzeGameAsync(path, contentPath ?? path);
                    OpenConfig(path, title, contentPath, exePath);
                };

                newFilesPanel.Children.Add(unqBtn);
                newFilesPanel.Children.Add(scanAgainBtn);
                ConfigOptionsList.Children.Add(newFilesPanel);

                var integrityBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0,0,0,12) };
                var backupBtn = new Button {
                    Content = "💾 Manual Backup [UNSTABLE]",
                    Padding = new Thickness(12, 6, 12, 6)
                };
                backupBtn.Click += async (s, e) => {
                    if (await ShowUnstableWarningAsync("Manual Backup")) {
                        RepairService.TriggerManualBackup(path, contentPath ?? path);
                        ConfigOverlay.Visibility = Visibility.Collapsed;
                    }
                };

                var integrityResetBtn = new Button {
                    Content = "🗑 Reset Integrity [UNSTABLE]",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                    Padding = new Thickness(12, 6, 12, 6)
                };
                integrityResetBtn.Click += async (s, e) => {
                    if (await ShowUnstableWarningAsync("Reset Integrity")) {
                        RepairService.StopHashingForGame(path);
                        ConfigOverlay.Visibility = Visibility.Collapsed;
                    }
                };

                integrityBtnRow.Children.Add(backupBtn);
                integrityBtnRow.Children.Add(integrityResetBtn);
                ConfigOptionsList.Children.Add(integrityBtnRow);
        }

        private async Task<bool> ShowUnstableWarningAsync(string feature)
        {
            var dialog = new ContentDialog
            {
                Title = "⚠️ Unstable Operation",
                Content = $"The '{feature}' feature is currently experimental and may cause data loss or corrupt integrity maps.\n\nPlease refer to the 'Advanced Repair Guidelines' before proceeding.\n\nContinue anyway?",
                PrimaryButtonText = "Proceed",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };
            return await App.ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary;
        }

        private void AddConfigText(string label, string value, Action<string> onUpdate) {
            ConfigOptionsList.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var tb = new TextBox { Text = value };
            tb.TextChanged += (s, e) => onUpdate(tb.Text);
            ConfigOptionsList.Children.Add(tb);
        }

        private void AddConfigPath(string label, string value, Action<string> onUpdate, bool isFolder)
        {
            ConfigOptionsList.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var tb = new TextBox { Text = value, HorizontalAlignment = HorizontalAlignment.Stretch };
            tb.TextChanged += (s, e) => onUpdate(tb.Text);
            grid.Children.Add(tb);

            var openBtn = new Button { Content = "📂", Margin = new Thickness(8, 0, 0, 0) };
            ToolTipService.SetToolTip(openBtn, "Open in Explorer");
            openBtn.Click += (s, e) => {
                try {
                    string target = tb.Text;
                    if (string.IsNullOrEmpty(target)) return;
                    if (File.Exists(target)) target = Path.GetDirectoryName(target) ?? target;
                    if (Directory.Exists(target)) Process.Start("explorer.exe", target);
                } catch { }
            };
            Grid.SetColumn(openBtn, 1);
            grid.Children.Add(openBtn);
            ConfigOptionsList.Children.Add(grid);
        }

        private void AddConfigToggle(string label, bool val, Action<bool> onUpdate) {
            var ts = new ToggleSwitch { Header = label, IsOn = val, Margin = new Thickness(0, 8, 0, 0) };
            ts.Toggled += (s, e) => onUpdate(ts.IsOn);
            ConfigOptionsList.Children.Add(ts);
        }

        private void AddConfigNumber(string label, int val, Action<int> onUpdate) {
            ConfigOptionsList.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var nb = new NumberBox { Value = val, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            nb.ValueChanged += (s, e) => onUpdate((int)nb.Value);
            ConfigOptionsList.Children.Add(nb);
        }

        private void AddConfigCombo(string label, string[] items, int selected, Action<int> onUpdate) {
            ConfigOptionsList.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var item in items) cb.Items.Add(item);
            cb.SelectedIndex = selected;
            cb.SelectionChanged += (s, e) => onUpdate(cb.SelectedIndex);
            ConfigOptionsList.Children.Add(cb);
        }

        private TaskCompletionSource<string>? _repairUrlTcs;
        private bool _repairInterceptorReady;

        public async Task<string> ResolveRepairUrlAsync(string targetUrl, string gameTitle)
        {
            Logger.Log($"[Repair-Resolve] Starting resolution for '{gameTitle}' (Initial URL: {targetUrl})");
            List<DownloadHost> hosts = new List<DownloadHost>();

            if (!string.IsNullOrEmpty(targetUrl) && targetUrl.Contains("steamrip.com"))
            {
                hosts = await SteamRipScraper.GetDirectLinksAsync(targetUrl);
            }

            if (hosts.Count == 0)
            {
                string manualPageUrl = await ShowManualPageSelectionDialogAsync(gameTitle);
                if (!string.IsNullOrEmpty(manualPageUrl))
                {
                    targetUrl = manualPageUrl;
                    var meta = GlobalSettings.Library.FirstOrDefault(m => m.Title.Equals(gameTitle, StringComparison.OrdinalIgnoreCase));
                    if (meta != null) { meta.Url = targetUrl; GlobalSettings.Save(); }
                    hosts = await SteamRipScraper.GetDirectLinksAsync(targetUrl);
                }
            }

            if (hosts.Count == 0) return "";

            string selectedUrl = "";
            if (hosts.Count > 1) selectedUrl = await ShowHostSelectionDialogAsync(hosts);
            else selectedUrl = hosts[0].Link;

            if (string.IsNullOrEmpty(selectedUrl)) return "";

            string finalUrl = await UrlResolver.ResolveDirectUrlAsync(selectedUrl);
            if (string.IsNullOrEmpty(finalUrl)) finalUrl = await InterceptRepairUrlAsync(selectedUrl);

            return finalUrl;
        }

        private async Task<string> ShowManualPageSelectionDialogAsync(string gameTitle)
        {
            var results = await SteamRipScraper.SearchAsync(gameTitle);
            if (results.Count == 0) return "";

            var listView = new ListView {
                ItemsSource = results,
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 400
            };

            var dialog = new ContentDialog {
                Title = "Select Game Page",
                Content = listView,
                PrimaryButtonText = "Link & Repair",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            if (await App.ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary && listView.SelectedItem is SearchResult selected)
            {
                return selected.Url;
            }
            return "";
        }

        private async Task<string> ShowHostSelectionDialogAsync(List<DownloadHost> hosts)
        {
            var bz = hosts.FirstOrDefault(h => h.Name.Equals("Buzzheavier", StringComparison.OrdinalIgnoreCase));
            var gf = hosts.FirstOrDefault(h => h.Name.Equals("GoFile", StringComparison.OrdinalIgnoreCase));

            if (bz != null && gf != null)
            {
                var selectDialog = new ContentDialog {
                    Title = "Select Repair Source",
                    Content = "Multiple download sources found.",
                    PrimaryButtonText = "Buzzheavier",
                    SecondaryButtonText = "GoFile (Fastest)",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await App.ShowDialogSafeAsync(selectDialog);
                if (result == ContentDialogResult.Primary) return bz.Link;
                if (result == ContentDialogResult.Secondary) return gf.Link;
                return "";
            }

            return hosts.FirstOrDefault()?.Link ?? "";
        }

        private async Task<string> InterceptRepairUrlAsync(string targetUrl)
        {
            _repairUrlTcs = new TaskCompletionSource<string>();
            try {
                if (!_repairInterceptorReady)
                {
                    await RepairInterceptor.EnsureCoreWebView2Async();
                    RepairInterceptor.CoreWebView2.DownloadStarting += (s, e) => {
                        e.Cancel = true;
                        _repairUrlTcs?.TrySetResult(e.DownloadOperation.Uri);
                    };
                    _repairInterceptorReady = true;
                }

                RepairInterceptor.Source = new Uri(targetUrl);
                var completedTask = await Task.WhenAny(_repairUrlTcs.Task, Task.Delay(45000));
                if (completedTask == _repairUrlTcs.Task) return await _repairUrlTcs.Task;
                return "";
            } catch { return ""; }
        }

        private async void FixMetadata_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentConfigPath)) return;
            try {
                Logger.Log($"Fixing metadata for {_currentConfigPath}...");
                var results = await ScannerEngine.ScanDirectoriesAsync(new List<string> { _currentConfigPath });
                if (results.Count > 0)
                {
                    RefreshLibrary();
                    await App.ShowDialogSafeAsync(new ContentDialog { Title = "Metadata Updated", Content = "Title, icons, and path mappings have been refreshed.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot });
                }
            } catch (Exception ex) {
                Logger.LogError("FixMetadata", ex);
            }
        }

        private async void RepairGame_Click(object sender, RoutedEventArgs e)
        {
            if (App.IsDialogShowing) return;
            if (string.IsNullOrEmpty(_currentConfigPath)) return;

            try {

                string lookupPath = _currentConfigPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var meta = GlobalSettings.Library.FirstOrDefault(m =>
                    m.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(lookupPath, StringComparison.OrdinalIgnoreCase));

                if (meta == null) return;

                string normLookup = _currentConfigPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string pageUrl = "";
                var matchedKey = GlobalSettings.GamePageLinks.Keys.FirstOrDefault(k =>
                    k.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(normLookup, StringComparison.OrdinalIgnoreCase));
                if (matchedKey != null) pageUrl = GlobalSettings.GamePageLinks[matchedKey];
                if (string.IsNullOrEmpty(pageUrl) && !string.IsNullOrEmpty(meta.Url)) pageUrl = meta.Url;

                var versionStatus = RepairService.CheckVersionFile(_currentConfigPath);
                if (versionStatus == RepairService.VersionStatus.NotDownloadedWithApp)
                {
                    _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "Not Downloaded with SteamRip App", Content = "This game does not appear to have been downloaded with this application. No repair data is available.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot });
                    return;
                }
                if (versionStatus == RepairService.VersionStatus.Incompatible)
                {
                    _ = App.ShowDialogSafeAsync(new ContentDialog
                    {
                        Title = "Incompatible Repair Data",
                        Content = "This game was downloaded with a much older or different version of this app's repair logic. The current repair system cannot safely verify these files.\n\nYou may need to re-download the game to enable modern repair features.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    });
                    return;
                }
                if (versionStatus == RepairService.VersionStatus.NeedsPatch)
                {
                    var upgradeConfirm = new ContentDialog
                    {
                        Title = "Patch Required",
                        Content = "This game's repair data needs a quick update to work with the latest repair logic.\n\nWould you like to re-verify and update the metadata now?",
                        PrimaryButtonText = "Update Metadata",
                        CloseButtonText = "Skip",
                        XamlRoot = this.Content.XamlRoot
                    };
                    if (await App.ShowDialogSafeAsync(upgradeConfirm) == ContentDialogResult.Primary)
                    {
                        await RepairService.RunInitialHashAsync(_currentConfigPath, _currentContentPath ?? _currentConfigPath);
                    }
                    else return;
                }

                var confirm = new ContentDialog
                {
                    Title = "🛠 Repair Game - Advanced Verification",
                    Content = "Analyze files and compare with SteamRIP original hashes? This may take several minutes.",
                    PrimaryButtonText = "Analyze & Repair",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                if (await App.ShowDialogSafeAsync(confirm) != ContentDialogResult.Primary) return;

                string analysisPath = _currentContentPath ?? _currentConfigPath;
                if (string.IsNullOrEmpty(analysisPath)) return;

                var report = await RepairService.AnalyzeGameAsync(_currentConfigPath, analysisPath, null, (msg, pct) => {

                });

                if (report.MetadataMissing)
                {

                    await RepairService.RunInitialHashAsync(_currentConfigPath, analysisPath);

                    report = await RepairService.AnalyzeGameAsync(_currentConfigPath, analysisPath, null, (msg, pct) => {});
                    if (report.MetadataMissing)
                    {
                         _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "Metadata Error", Content = "Could not generate integrity metadata for this game.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot });
                         return;
                    }
                }

                if (!report.HasIssues)
                {
                    _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "Integrity Perfect", Content = "All files are present and match their original hashes.", CloseButtonText = "Great!", XamlRoot = this.Content.XamlRoot });
                    return;
                }

                var repairConfirm = new ContentDialog
                {
                    Title = "Issues Found",
                    Content = $"Found {report.MissingFiles.Count + report.CorruptedFiles.Count} discrepancies.\n\nWould you like to attempt a Repair? This will re-download only the necessary parts from SteamRIP.",
                    PrimaryButtonText = "Start Repair",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                if (await App.ShowDialogSafeAsync(repairConfirm) == ContentDialogResult.Primary)
                {
                    if (string.IsNullOrEmpty(pageUrl))
                    {
                        pageUrl = await ShowManualPageSelectionDialogAsync(meta.Title);
                        if (string.IsNullOrEmpty(pageUrl)) return;
                        meta.Url = pageUrl;
                        string normPath = _currentConfigPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        GlobalSettings.GamePageLinks[normPath] = pageUrl;
                        GlobalSettings.Save();
                    }

                    string directUrl = await ResolveRepairUrlAsync(pageUrl, meta.Title);
                    if (string.IsNullOrEmpty(directUrl)) return;

                    var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.RootPath.Equals(analysisPath, StringComparison.OrdinalIgnoreCase));
                    if (gf != null) { gf.IsInProgress = true; gf.ProgressPhase = "Repairing..."; }

                    await RepairService.PerformIntegrityRepairAsync(_currentConfigPath, analysisPath, report, directUrl, (s, p) => {
                        DispatcherQueue.TryEnqueue(() => {

                        });
                    }, CancellationToken.None);

                    if (gf != null) { gf.IsInProgress = false; gf.ProgressPhase = ""; }
                }
            }
            catch (Exception ex) {
                Logger.LogError("RepairGame_UI", ex);
                try {
                    _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "Repair Error", Content = $"A critical error occurred: {ex.Message}", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot });
                } catch { }
            }
            finally {
                App.IsDialogShowing = false;
            }
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            GlobalSettings.Save();
            ConfigOverlay.Visibility = Visibility.Collapsed;
            NavView.IsHitTestVisible = true;
        }
        private void CancelConfig_Click(object sender, RoutedEventArgs e)
        {
            ConfigOverlay.Visibility = Visibility.Collapsed;
            NavView.IsHitTestVisible = true;
        }

        public void NavigateToDownloads()
        {
            ContentFrame.Navigate(typeof(DownloadsPage));
            NavView.Header = "Downloads";
            UpdateNavSelection("downloads");
        }

        public void NavigateToHome()
        {
            ContentFrame.Navigate(typeof(HomePage));
            NavView.Header = "Home";
            UpdateNavSelection("home");
        }

        public void NavigateToLibrary()
        {
            ContentFrame.Navigate(typeof(LibraryPage));
            NavView.Header = "My Library";
            UpdateNavSelection("library");
        }

        private void UpdateNavSelection(string tag)
        {
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi && nvi.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = nvi;
                    break;
                }
            }
        }

        public void RefreshLibrary()
        {
            if (ContentFrame.Content is LibraryPage libraryPage)
            {
                _ = libraryPage.RefreshAsync();
            }
        }

        private async Task<int?> ShowAppIdSearchDialog(string initialTitle)
        {
            var dialog = new ContentDialog
            {
                Title = "Search Steam Store",
                PrimaryButtonText = "Apply Selection",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot,
                IsPrimaryButtonEnabled = false
            };

            var stack = new StackPanel { Spacing = 12, Width = 400 };
            var checkBoxes = new Dictionary<string, CheckBox>();
            var searchBox = new AutoSuggestBox {
                Text = initialTitle,
                QueryIcon = new SymbolIcon(Symbol.Find),
                PlaceholderText = "Enter game title..."
            };

            var loading = new ProgressRing { IsActive = false, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10) };
            var resultsList = new ListView {
                Height = 300,
                SelectionMode = ListViewSelectionMode.Single,
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                    <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                        <Grid Padding='0,4'>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width='Auto'/>
                                <ColumnDefinition Width='*'/>
                            </Grid.ColumnDefinitions>
                            <Border Background='{ThemeResource SystemControlBackgroundBaseLowBrush}' CornerRadius='4' Padding='6,2' VerticalAlignment='Center'>
                                <TextBlock Text='{Binding id}' FontSize='11' FontWeight='Bold' Foreground='{ThemeResource AccentFillColorDefaultBrush}'/>
                            </Border>
                            <TextBlock Grid.Column='1' Text='{Binding name}' Margin='12,0,0,0' VerticalAlignment='Center' TextWrapping='Wrap'/>
                        </Grid>
                    </DataTemplate>")
            };

            resultsList.SelectionChanged += (s, e) => {
                dialog.IsPrimaryButtonEnabled = resultsList.SelectedItem != null;
            };

            searchBox.QuerySubmitted += async (s, e) => {
                loading.IsActive = true;
                resultsList.Items.Clear();
                try {
                    string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(e.QueryText)}&l=english&cc=US";
                    using var client = new System.Net.Http.HttpClient();
                    string json = await client.GetStringAsync(url);
                    var response = System.Text.Json.JsonSerializer.Deserialize<SteamStoreSearchResponse>(json);
                    if (response?.items != null)
                    {
                        foreach (var item in response.items) resultsList.Items.Add(item);
                    }
                } catch { }
                loading.IsActive = false;
            };

            stack.Children.Add(searchBox);
            stack.Children.Add(loading);
            stack.Children.Add(resultsList);
            dialog.Content = stack;

            var result = await App.ShowDialogSafeAsync(dialog);
            if (result == ContentDialogResult.Primary && resultsList.SelectedItem is SteamAppIdResult selected)
            {
                return selected.id;
            }
            return null;
        }
        private async Task ReviewAdditionalFilesAsync(string storagePath, string contentPath, string title)
        {
            var dialog = new ContentDialog {
                Title = $"File Manager: {title}",
                PrimaryButtonText = "Close",
                XamlRoot = this.Content.XamlRoot,
                MaxWidth = 900
            };

            var mainGrid = new Grid { Width = 800, Height = 550, Margin = new Thickness(0, 10, 0, 0) };
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var diskList = new StackPanel { Spacing = 2 };
            var qList = new StackPanel { Spacing = 4 };

            async Task RefreshData()
            {
                diskList.Children.Clear();
                qList.Children.Clear();

                var report = await RepairService.AnalyzeGameAsync(storagePath, contentPath);
                var quarantined = RepairService.GetQuarantinedFiles(storagePath);
                var trusted = GlobalSettings.GetTrustedFiles(storagePath);

                var diskHeader = new Grid { Margin = new Thickness(0, 0, 10, 10) };
                diskHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                diskHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                diskHeader.Children.Add(new TextBlock {
                    Text = "Unrecognized file(s)",
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (report.AddedFiles.Count > 0) {
                    var bulkQ = new Button { Content = "Quarantine All", Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
                    bulkQ.Click += async (s, e) => {
                        RepairService.QuarantineFiles(storagePath, contentPath, report.AddedFiles);
                        await RefreshData();
                    };
                    Grid.SetColumn(bulkQ, 1);
                    diskHeader.Children.Add(bulkQ);
                }
                diskList.Children.Add(diskHeader);

                foreach (var file in report.AddedFiles.OrderBy(f => f))
                {
                    bool isTrusted = trusted.Contains(file);
                    diskList.Children.Add(CreateFileRow(file, true, isTrusted, async (action) => {
                        if (action == "quarantine") {
                            RepairService.QuarantineFiles(storagePath, contentPath, new List<string> { file });
                        } else if (action == "trust") {
                            trusted.Add(file);
                            RepairService.UpdateModsManifest(storagePath, trusted.ToList());
                        } else if (action == "untrust") {
                            trusted.Remove(file);
                            RepairService.UpdateModsManifest(storagePath, trusted.ToList());
                        } else if (action == "delete") {
                            string full = Path.Combine(contentPath, file);
                            if (File.Exists(full)) File.Delete(full);
                            else if (Directory.Exists(full)) Directory.Delete(full, true);
                        }
                        await RefreshData();
                    }));
                }

                var qHeader = new Grid { Margin = new Thickness(10, 0, 0, 10) };
                qHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                qHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                qHeader.Children.Add(new TextBlock {
                    Text = "Quarantined",
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (quarantined.Count > 0) {
                    var bulkR = new Button { Content = "Restore All", Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
                    bulkR.Click += async (s, e) => {
                        RepairService.UnquarantineFiles(storagePath, contentPath);

                        var qFiles = RepairService.GetQuarantinedFiles(storagePath);
                        foreach(var qf in qFiles) trusted.Add(qf.OriginalRelPath);
                        RepairService.UpdateModsManifest(storagePath, trusted.ToList());
                        await RefreshData();
                    };
                    Grid.SetColumn(bulkR, 1);
                    qHeader.Children.Add(bulkR);
                }
                qList.Children.Add(qHeader);

                foreach (var q in quarantined.OrderBy(f => f.DisplayName))
                {
                    qList.Children.Add(CreateFileRow(q.DisplayName, false, false, async (action) => {
                        if (action == "restore") {
                            string targetPath = Path.Combine(contentPath, q.OriginalRelPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                            if (q.IsDirectory) {
                                if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);
                                Directory.Move(q.FullQPath, targetPath);
                            } else {
                                File.Move(q.FullQPath, targetPath, true);
                            }

                            trusted.Add(q.OriginalRelPath);
                            RepairService.UpdateModsManifest(storagePath, trusted.ToList());
                        } else if (action == "delete") {
                            if (q.IsDirectory) Directory.Delete(q.FullQPath, true);
                            else File.Delete(q.FullQPath);
                        }
                        await RefreshData();
                    }));
                }
            }

            Grid CreateFileRow(string name, bool isOnDisk, bool isTrusted, Func<string, Task> onAction)
            {
                var row = new Grid {
                    Margin = new Thickness(isOnDisk ? 0 : 10, 2, isOnDisk ? 10 : 0, 2),
                    Padding = new Thickness(6),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(isOnDisk && isTrusted ? Windows.UI.Color.FromArgb(30, 0, 255, 0) : Microsoft.UI.Colors.Transparent)
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                row.Children.Add(new TextBlock {
                    Text = name,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = isTrusted ? 0.6 : 1.0,
                    FontStyle = isTrusted ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
                });

                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                Grid.SetColumn(btns, 1);

                if (isOnDisk) {

                    var tBtn = new Button { Content = isTrusted ? "🛡️" : "✅", Width = 28, Height = 28, Padding = new Thickness(0) };
                    ToolTipService.SetToolTip(tBtn, isTrusted ? "Currently Trusted (Click to Untrust)" : "Trust this file (Hide from alerts)");
                    tBtn.Click += async (s, e) => await onAction(isTrusted ? "untrust" : "trust");
                    btns.Children.Add(tBtn);

                    var qBtn = new Button { Content = "📦", Width = 28, Height = 28, Padding = new Thickness(0) };
                    ToolTipService.SetToolTip(qBtn, "Quarantine");
                    qBtn.Click += async (s, e) => await onAction("quarantine");
                    btns.Children.Add(qBtn);
                } else {
                    var rBtn = new Button { Content = "⏪", Width = 28, Height = 28, Padding = new Thickness(0) };
                    ToolTipService.SetToolTip(rBtn, "Restore and Trust");
                    rBtn.Click += async (s, e) => await onAction("restore");
                    btns.Children.Add(rBtn);
                }

                var dBtn = new Button {
                    Content = "🗑",
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                };
                ToolTipService.SetToolTip(dBtn, "Delete Permanently");
                dBtn.Click += async (s, e) => await onAction("delete");
                btns.Children.Add(dBtn);

                row.Children.Add(btns);
                return row;
            }

            var leftScroll = new ScrollViewer { Content = diskList, Margin = new Thickness(0, 0, 4, 0) };
            var rightScroll = new ScrollViewer { Content = qList, Margin = new Thickness(4, 0, 0, 0) };

            Grid.SetColumn(leftScroll, 0);
            Grid.SetColumn(rightScroll, 1);
            mainGrid.Children.Add(leftScroll);
            mainGrid.Children.Add(rightScroll);

            dialog.Content = mainGrid;
            _ = RefreshData();

            await App.ShowDialogSafeAsync(dialog);
        }
    }
}