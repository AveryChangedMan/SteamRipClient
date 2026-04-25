using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System.Linq;

namespace SteamRipApp
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            
            SetAppIcon();

            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(HomePage));
            NavView.Header = "Home";
            
            this.Activated += Window_FirstActivated;
            CleanupActiveDownloads();
            NativeBridgeService.Start();
            UpdateAdvancedTabsVisibility();
            StartHashingProgressTimer();
        }

        private void StartHashingProgressTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => {
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
            timer.Start();
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
                SteamNavItem.Visibility = GlobalSettings.IsAdvancedModeEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (IntegrationNavItem != null)
                IntegrationNavItem.Visibility = GlobalSettings.IsAdvancedModeEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetAppIcon()
        {
            try {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                
                
                
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                }
                else
                {
                    
                    iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_icon.png");
                    if (System.IO.File.Exists(iconPath)) appWindow.SetIcon(iconPath);
                }
            } catch (Exception ex) {
                Logger.Log($"[MainWindow] Failed to set window icon: {ex.Message}");
            }
        }

        private void CleanupActiveDownloads()
        {
            
            ScanForOrphanedDownloads();

            
            CleanupLibrary();

            
            
            int removed = GlobalSettings.ActiveDownloads.RemoveAll(d => string.IsNullOrEmpty(d.Title));
            
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


        private async void Window_FirstActivated(object sender, WindowActivatedEventArgs e)
        {
            this.Activated -= Window_FirstActivated; 
            
            await System.Threading.Tasks.Task.Delay(300);
            await CheckFirstRunSetupAsync();
        }

        private async System.Threading.Tasks.Task CheckFirstRunSetupAsync()
        {
            if (GlobalSettings.HasSelectedDownloadDirectory) return;

            
            var xamlRoot = ContentFrame.XamlRoot;
            if (xamlRoot == null)
            {
                Logger.Log("[FirstRun] XamlRoot not ready, skipping dialog this launch.");
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "📁 Set Download Directory",
                Content = "Before downloading any games, please choose a default folder where files will be saved. You can change this anytime in Settings.",
                PrimaryButtonText = "Choose Directory",
                SecondaryButtonText = "Skip for now",
                XamlRoot = xamlRoot
            };

            try {
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var picker = new Windows.Storage.Pickers.FolderPicker();
                    picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

                    var folder = await picker.PickSingleFolderAsync();
                    if (folder != null)
                    {
                        GlobalSettings.DownloadDirectory = folder.Path;
                        GlobalSettings.HasSelectedDownloadDirectory = true;
                        if (!GlobalSettings.ScanDirectories.Contains(folder.Path))
                            GlobalSettings.ScanDirectories.Add(folder.Path);
                        GlobalSettings.Save();
                        Logger.Log($"[FirstRun] Default download dir set: {folder.Path}");
                    }
                }
                else
                {
                    Logger.Log("[FirstRun] User skipped directory selection. Will prompt again on next launch.");
                }
            } catch (Exception ex) {
                Logger.LogError("CheckFirstRunSetup", ex);
            }
        }

        public void UpdateGlobalProgress(double pct, bool visible = true)
        {
            GlobalProgressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            GlobalProgressBar.Value = pct;
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
                        NavView.Header = "Steam Integration";
                        break;
                    case "downloads":
                        ContentFrame.Navigate(typeof(DownloadsPage));
                        NavView.Header = "Downloads";
                        break;
                    case "library":
                        ContentFrame.Navigate(typeof(LibraryPage));
                        NavView.Header = "My Library";
                        break;
                    case "integration":
                        ContentFrame.Navigate(typeof(NativeBridgePage));
                        NavView.Header = "Steam Integration";
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

            if (string.IsNullOrEmpty(config.ManualExePath))
            {
                
                string? detectedExe = exePath ?? ScannerEngine.FindExecutable(path, contentPath);
                if (!string.IsNullOrEmpty(detectedExe))
                {
                    config.ManualExePath = detectedExe;
                    config.WorkingDir = System.IO.Path.GetDirectoryName(detectedExe) ?? path;
                    Logger.Log($"[UI] Auto-detected EXE for '{title}': {detectedExe}");
                }
            }

            ConfigTitle.Text = $"⚙️ {title}";
            
            
            bool hasMap = File.Exists(Path.Combine(path, ".rip_map.json"));
            HardRepairBtn.Visibility = Visibility.Visible;

            ConfigOverlay.Visibility = Visibility.Visible;
            
            ConfigOptionsList.Children.Clear();

            
            AddConfigText("Manual EXE Path", config.ManualExePath ?? "", (val) => config.ManualExePath = val);

            
            string cleanPath = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var meta = GlobalSettings.Library.FirstOrDefault(m => 
                m.LocalPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                .Equals(cleanPath, StringComparison.OrdinalIgnoreCase));
            
            if (meta == null)
            {
                meta = new GameMetadata { LocalPath = path, Title = title };
                GlobalSettings.Library.Add(meta);
            }

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
                        await NativeBridgeService.IntegrateGame(path, id, title, oldId);
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
            
            AddConfigText("Working Directory", config.WorkingDir ?? "", (val) => config.WorkingDir = val);
            
            AddConfigToggle("Run as Administrator", config.RunAsAdmin, (val) => config.RunAsAdmin = val);
            
            AddConfigToggle("Disable Fullscreen Optimizations", config.DisableFullscreenOptimizations, (val) => config.DisableFullscreenOptimizations = val);
            
            AddConfigToggle("High DPI Scaling Override", config.HighDpiScaling, (val) => config.HighDpiScaling = val);
            
            AddConfigToggle("Suppress Game Overlays", config.SuppressOverlays, (val) => config.SuppressOverlays = val);
            
            AddConfigNumber("Launch Delay (seconds)", config.LaunchDelaySeconds, (val) => config.LaunchDelaySeconds = val);
            
            AddConfigCombo("CPU Priority", new[] { "Low", "Normal", "High", "Realtime" }, config.CpuPriority, (val) => config.CpuPriority = val);
            
            
            AddConfigCombo("Compatibility Mode", new[] { "None", "Windows 7", "Windows 8" }, 0, (val) => { });

            
            ConfigOptionsList.Children.Add(new TextBlock { 
                Text = "Goldberg Patch (Compatibility)", 
                Margin = new Thickness(0, 16, 0, 8), 
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)
            });
            ConfigOptionsList.Children.Add(new TextBlock { 
                Text = "Essential for most SteamRIP games. Replaces Steam API DLLs with an emulator.", 
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) 
            });
            
            var patchBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            
            var gbBtn = new Button { 
                Content = meta.IsEmulatorApplied ? "✅ Goldberg Patched" : "Apply Goldberg Patch",
                IsEnabled = !meta.IsEmulatorApplied,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Padding = new Thickness(12, 6, 12, 6)
            };
            
            var revBtn = new Button {
                Content = "Reverse Goldberg Patch",
                IsEnabled = meta.IsEmulatorApplied,
                Padding = new Thickness(12, 6, 12, 6)
            };

            patchBtnRow.Children.Add(gbBtn);
            patchBtnRow.Children.Add(revBtn);
            ConfigOptionsList.Children.Add(patchBtnRow);
            
            gbBtn.Click += async (s, e) => {
                string appIdStr = appIdBox.Text;
                if (int.TryParse(appIdStr, out int appId))
                {
                    NativeBridgeService.Log($"Applying Goldberg patch for {title} (AppID: {appId})...", "PATCH");
                    await NativeBridgeService.ApplyGoldbergPatchAsync(path, appId);
                    meta.IsEmulatorApplied = true;
                    GlobalSettings.Save();
                    
                    gbBtn.Content = "✅ Goldberg Patched";
                    gbBtn.IsEnabled = false;
                    revBtn.IsEnabled = true;
                    RefreshLibrary();
                }
                else
                {
                    NativeBridgeService.Log($"Patch failed: Invalid AppID '{appIdStr}'", "PATCH");
                }
            };

            revBtn.Click += async (s, e) => {
                NativeBridgeService.Log($"Reversing Goldberg patch for {title}...", "PATCH");
                await NativeBridgeService.ReverseGoldbergPatchAsync(path);
                meta.IsEmulatorApplied = false;
                GlobalSettings.Save();
                
                gbBtn.Content = "Apply Goldberg Patch";
                gbBtn.IsEnabled = true;
                revBtn.IsEnabled = false;
                RefreshLibrary();
            };

            
            if (GlobalSettings.IsHardRepairEnabled)
            {
                ConfigOptionsList.Children.Add(new TextBlock { 
                    Text = "Integrity Management", 
                    Margin = new Thickness(0, 20, 0, 8), 
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange)
                });

                var integrityBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0,0,0,12) };
                
                var backupBtn = new Button { 
                    Content = "📦 Trigger Manual Backup",
                    Padding = new Thickness(12, 6, 12, 6)
                };
                backupBtn.Click += (s, e) => {
                    RepairService.TriggerManualBackup(path, contentPath ?? path);
                    ConfigOverlay.Visibility = Visibility.Collapsed;
                };

                var integrityResetBtn = new Button {
                    Content = "🗑 Reset Integrity State",
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                    Padding = new Thickness(12, 6, 12, 6)
                };
                integrityResetBtn.Click += (s, e) => {
                    RepairService.StopHashingForGame(path);
                    ConfigOverlay.Visibility = Visibility.Collapsed;
                };

                integrityBtnRow.Children.Add(backupBtn);
                integrityBtnRow.Children.Add(integrityResetBtn);
                ConfigOptionsList.Children.Add(integrityBtnRow);
            }
        }

        private void AddConfigText(string label, string value, Action<string> onUpdate) {
            ConfigOptionsList.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var tb = new TextBox { Text = value };
            tb.TextChanged += (s, e) => onUpdate(tb.Text);
            ConfigOptionsList.Children.Add(tb);
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

        private async Task<string> ResolveRepairUrlAsync(string targetUrl, string gameTitle)
        {
            Logger.Log($"[Repair-Resolve] Starting resolution for '{gameTitle}' (Initial URL: {targetUrl})");
            List<DownloadHost> hosts = new List<DownloadHost>();

            
            if (!string.IsNullOrEmpty(targetUrl) && targetUrl.Contains("steamrip.com"))
            {
                Logger.Log($"[Repair-Resolve] Probing existing SteamRIP URL...");
                hosts = await SteamRipScraper.GetDirectLinksAsync(targetUrl);
                Logger.Log($"[Repair-Resolve] Hosts found on existing page: {hosts.Count}");
            }

            
            if (hosts.Count == 0)
            {
                Logger.Log($"[Repair-Resolve] No hosts found on current URL. Opening manual selector...");
                string manualPageUrl = await ShowManualPageSelectionDialogAsync(gameTitle);
                
                if (!string.IsNullOrEmpty(manualPageUrl))
                {
                    targetUrl = manualPageUrl;
                    Logger.Log($"[Repair-Resolve] User selected page: {targetUrl}. Updating library metadata...");
                    
                    var meta = GlobalSettings.Library.FirstOrDefault(m => m.Title.Equals(gameTitle, StringComparison.OrdinalIgnoreCase));
                    if (meta != null) { meta.Url = targetUrl; GlobalSettings.Save(); }
                    
                    hosts = await SteamRipScraper.GetDirectLinksAsync(targetUrl);
                    Logger.Log($"[Repair-Resolve] Hosts found on selected page: {hosts.Count}");
                }
                else
                {
                    Logger.Log("[Repair-Resolve] Manual page selection was cancelled or returned no results.");
                }
            }

            if (hosts.Count == 0)
            {
                Logger.Log("[Repair-Resolve] FAILED: No download hosts (Buzzheavier/GoFile) could be found for this game.");
                return "";
            }

            
            string selectedUrl = "";
            if (hosts.Count > 1)
            {
                Logger.Log("[Repair-Resolve] Multiple hosts detected. Prompting user for choice...");
                selectedUrl = await ShowHostSelectionDialogAsync(hosts);
                Logger.Log($"[Repair-Resolve] User selected host link: {selectedUrl}");
            }
            else
            {
                selectedUrl = hosts[0].Link;
                Logger.Log($"[Repair-Resolve] Single host detected: {hosts[0].Name} ({selectedUrl})");
            }

            if (string.IsNullOrEmpty(selectedUrl)) 
            {
                Logger.Log("[Repair-Resolve] FAILED: No host URL was selected or user cancelled host selection.");
                return "";
            }

            
            string finalUrl = "";
            
            Logger.Log($"[Repair-Resolve] Attempting API-based resolution for: {selectedUrl}");
            finalUrl = await UrlResolver.ResolveDirectUrlAsync(selectedUrl);

            if (string.IsNullOrEmpty(finalUrl))
            {
                Logger.Log("[Repair-Resolve] API resolution failed. Falling back to Interceptor...");
                finalUrl = await InterceptRepairUrlAsync(selectedUrl);
            }
            
            if (string.IsNullOrEmpty(finalUrl))
            {
                Logger.Log("[Repair-Resolve] FAILED: Could not extract a direct download link automatically.");
                
                var dialog = new ContentDialog {
                    Title = "URL Resolution Failed",
                    Content = "We could not automatically extract a download link from this host. Would you like to search for the game page manually?",
                    PrimaryButtonText = "Search Manually",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };
                
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    Logger.Log("[Repair-Resolve] Falling back to manual search...");
                    var manualPageUrl = await ShowManualPageSelectionDialogAsync(gameTitle);
                    if (!string.IsNullOrEmpty(manualPageUrl))
                    {
                        
                        return await ResolveRepairUrlAsync(manualPageUrl, gameTitle);
                    }
                }
            }
            else
            {
                Logger.Log("[Repair-Resolve] SUCCESS: Direct link extracted.");
            }

            return finalUrl;
        }

        private async Task<string> ShowManualPageSelectionDialogAsync(string gameTitle)
        {
            Logger.Log($"[Repair-UI] Searching SteamRIP for manual page selection: '{gameTitle}'");
            var results = await SteamRipScraper.SearchAsync(gameTitle);
            if (results.Count == 0) 
            {
                Logger.Log("[Repair-UI] No search results found for manual selection.");
                return "";
            }

            Logger.Log($"[Repair-UI] Showing selection dialog with {results.Count} results.");
            var listView = new ListView {
                ItemsSource = results,
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 400,
                Padding = new Thickness(0, 8, 0, 8)
            };

            
            listView.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <Grid Padding=""8"">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width=""Auto""/>
                            <ColumnDefinition Width=""*""/>
                        </Grid.ColumnDefinitions>
                        <Border CornerRadius=""4"" Background=""{ThemeResource SystemControlBackgroundBaseLowBrush}"" Width=""80"" Height=""45"">
                            <Image Stretch=""UniformToFill"" Source=""{Binding ImageUrl}""/>
                        </Border>
                        <StackPanel Grid.Column=""1"" VerticalAlignment=""Center"" Margin=""12,0,0,0"" Spacing=""2"">
                            <TextBlock Text=""{Binding Title}"" FontWeight=""SemiBold"" TextWrapping=""Wrap""/>
                            <TextBlock Text=""{Binding DateString}"" FontSize=""11"" Opacity=""0.6""/>
                        </StackPanel>
                    </Grid>
                </DataTemplate>");

            var dialog = new ContentDialog {
                Title = "Select Game Page",
                Content = new StackPanel { 
                    Spacing = 8,
                    Children = { 
                        new TextBlock { Text = $"Multiple or no pages were found for '{gameTitle}'. Please select the correct one:", TextWrapping = TextWrapping.Wrap },
                        listView 
                    } 
                },
                PrimaryButtonText = "Link & Repair",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && listView.SelectedItem is SearchResult selected)
            {
                Logger.Log($"[Repair-UI] User selected page: {selected.Url}");
                return selected.Url;
            }
            Logger.Log("[Repair-UI] Manual selection cancelled.");
            return "";
        }

        private async Task<string> ShowHostSelectionDialogAsync(List<DownloadHost> hosts)
        {
            var bz = hosts.FirstOrDefault(h => h.Name.Equals("Buzzheavier", StringComparison.OrdinalIgnoreCase));
            var gf = hosts.FirstOrDefault(h => h.Name.Equals("GoFile", StringComparison.OrdinalIgnoreCase));

            if (bz != null && gf != null)
            {
                Logger.Log("[Repair-UI] Detected both Buzzheavier and GoFile. Showing selection dialog.");
                var selectDialog = new ContentDialog {
                    Title = "Select Repair Source",
                    Content = "Multiple download sources found. Which one would you like to use for surgical repair?",
                    PrimaryButtonText = "Buzzheavier",
                    SecondaryButtonText = "GoFile (Fastest)",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await selectDialog.ShowAsync();
                if (result == ContentDialogResult.Primary) { Logger.Log("[Repair-UI] User selected Buzzheavier."); return bz.Link; }
                if (result == ContentDialogResult.Secondary) { Logger.Log("[Repair-UI] User selected GoFile."); return gf.Link; }
                Logger.Log("[Repair-UI] User cancelled host selection.");
                return "";
            }

            var fallback = hosts.FirstOrDefault()?.Link ?? "";
            Logger.Log($"[Repair-UI] Only one host or unexpected combo found. Falling back to: {fallback}");
            return fallback;
        }

        private async Task<string> InterceptRepairUrlAsync(string targetUrl)
        {
            Logger.Log($"[Repair-Interceptor] Starting for host URL: {targetUrl}");
            _repairUrlTcs = new TaskCompletionSource<string>();

            try {
                if (!_repairInterceptorReady)
                {
                    Logger.Log("[Repair-Interceptor] Initializing CoreWebView2...");
                    await RepairInterceptor.EnsureCoreWebView2Async();
                    RepairInterceptor.CoreWebView2.DownloadStarting += (s, e) => {
                        e.Cancel = true;
                        Logger.Log($"[Repair-Interceptor] INTERCEPTED DOWNLOAD: {e.DownloadOperation.Uri}");
                        _repairUrlTcs?.TrySetResult(e.DownloadOperation.Uri);
                    };
                    RepairInterceptor.NavigationCompleted += async (s, args) => {
                        if (!args.IsSuccess) { Logger.Log($"[Repair-Interceptor] NAVIGATION FAILED: {args.WebErrorStatus}"); return; }
                        var url = s.Source?.ToString() ?? "";
                        Logger.Log($"[Repair-Interceptor] Loaded: {url}");

                        if (!url.Contains("bzzhr.to") && !url.Contains("buzzheavier") && !url.Contains("gofile.io")) {
                             Logger.Log("[Repair-Interceptor] URL is not a known host page. Skipping script injection.");
                             return;
                        }

                        Logger.Log("[Repair-Interceptor] Injecting click automation script...");
                        await Task.Delay(2000);
                        var result = await s.ExecuteScriptAsync(@"
                            (function() {
                                // Buzzheavier / HTMX
                                var bzBtn = document.querySelector('a[hx-get*=""download""], .link-button, button[hx-get*=""download""]');
                                if (bzBtn) { bzBtn.click(); return 'clicked-buzzheavier'; }

                                // GoFile / Generic
                                var gfBtn = document.querySelector('button[id*=""download""], .downloadButton, button[class*=""download""], #downloadButton');
                                if (gfBtn) { gfBtn.click(); return 'clicked-gofile'; }
                                
                                // Fallback any anchor with ""download""
                                var anyDl = document.querySelector('a[href*=""/download/""], a[href*=""download""]');
                                if (anyDl) { anyDl.click(); return 'clicked-fallback'; }
                                
                                return 'no-button-found';
                            })();
                        ");
                        Logger.Log($"[Repair-Interceptor] Script Result: {result}");
                    };
                    _repairInterceptorReady = true;
                    Logger.Log("[Repair-Interceptor] Ready.");
                }

                string absoluteUrl = targetUrl;
                if (absoluteUrl.StartsWith("//")) absoluteUrl = "https:" + absoluteUrl;
                if (!absoluteUrl.StartsWith("http")) absoluteUrl = "https://" + absoluteUrl.TrimStart('/');
                
                RepairInterceptor.Source = new Uri(absoluteUrl);
                Logger.Log($"[Repair-Interceptor] Navigating to: {absoluteUrl}");

                var completedTask = await Task.WhenAny(_repairUrlTcs.Task, Task.Delay(45000));
                if (completedTask == _repairUrlTcs.Task)
                    return await _repairUrlTcs.Task;

                Logger.Log("[Repair-Interceptor] TIMEOUT: No download was intercepted within 45 seconds.");
                return "";
            } catch (Exception ex) {
                Logger.LogError("[Repair-Interceptor] Fatal Error", ex);
                return "";
            }
        }

        private async void HardRepair_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentConfigPath)) return;
            
            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(_currentConfigPath, StringComparison.OrdinalIgnoreCase));
            if (meta == null) return;

            string pageUrl = GlobalSettings.GamePageLinks.TryGetValue(_currentConfigPath, out var u) ? u : "";
            if (string.IsNullOrEmpty(pageUrl)) pageUrl = meta.Url ?? "";

            var confirm = new ContentDialog
            {
                Title = "🛡️ Full Integrity Repair",
                Content = $"This will analyze every file in {meta.Title} and compare hashes.\n\n" +
                          $"Game: {meta.Title}\nSource: {(string.IsNullOrEmpty(pageUrl) ? "Automatic Search" : pageUrl)}\n\n" +
                          "Confirm scan details:",
                PrimaryButtonText = "Analyze & Repair",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            HardRepairBtn.IsEnabled = false;
            HardRepairBtn.Content = "Analyzing...";

            
            HashingStatusArea.Visibility = Visibility.Visible;
            HashingProgressBar.IsIndeterminate = false;

            var report = await RepairService.AnalyzeGameAsync(_currentConfigPath, _currentConfigPath, (status, progress) => {
                DispatcherQueue.TryEnqueue(() => {
                    HashingStatusText.Text = status;
                    HashingProgressBar.Value = progress;
                });
            });
            
            HashingStatusArea.Visibility = Visibility.Collapsed;
            HardRepairBtn.IsEnabled = true;
            HardRepairBtn.Content = "🛠 Hard Repair";

            if (report.Error != null)
            {
                await new ContentDialog { Title = "Error", Content = report.Error, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            if (!report.HasIssues)
            {
                await new ContentDialog { Title = "Integrity Perfect", Content = "All files are present and match their original hashes.", CloseButtonText = "Great!", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            string issueDetails = "";
            if (report.MissingFiles.Any()) issueDetails += $"❌ Missing Files: {report.MissingFiles.Count}\n";
            if (report.CorruptedFiles.Any()) issueDetails += $"⚠️ Corrupted Files: {report.CorruptedFiles.Count}\n";

            string FormatBytes(long bytes)
            {
                string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
                int i = 0;
                double dblSByte = bytes;
                while (dblSByte >= 1024 && i < suffixes.Length - 1)
                {
                    dblSByte /= 1024;
                    i++;
                }
                return $"{dblSByte:0.##} {suffixes[i]}";
            }

            var repairConfirm = new ContentDialog
            {
                Title = "Issues Found",
                Content = $"{issueDetails}\nEstimated Download: {FormatBytes(report.EstimatedDownloadBytes)}\n\nWould you like to start a Full Repair? This will re-download only missing or corrupted files.",
                PrimaryButtonText = "Start Repair",
                SecondaryButtonText = "Show Details",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await repairConfirm.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                string details = "Missing:\n" + string.Join("\n", report.MissingFiles.Take(10)) + (report.MissingFiles.Count > 10 ? "\n..." : "") +
                                 "\n\nCorrupted:\n" + string.Join("\n", report.CorruptedFiles.Take(10)) + (report.CorruptedFiles.Count > 10 ? "\n..." : "");
                await new ContentDialog { Title = "Detail Report", Content = new ScrollViewer { Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap } }, CloseButtonText = "Back", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            if (result == ContentDialogResult.Primary)
            {
                
                string directUrl = "";
                try {
                    directUrl = await ResolveRepairUrlAsync(pageUrl, meta.Title);
                } catch (Exception ex) {
                    await new ContentDialog { Title = "Repair Failed", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                    return;
                }

                if (string.IsNullOrEmpty(directUrl)) return; 

                var progressDialog = new ContentDialog
                {
                    Title = "Repairing Game",
                    PrimaryButtonText = "Abort",
                    XamlRoot = this.Content.XamlRoot
                };

                var sp = new StackPanel { Spacing = 10 };
                var pb = new ProgressBar { Maximum = 100, Value = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                var tb = new TextBlock { Text = "Extracting files...", TextWrapping = TextWrapping.Wrap };
                sp.Children.Add(tb);
                sp.Children.Add(pb);
                progressDialog.Content = sp;

                var cts = new CancellationTokenSource();
                progressDialog.PrimaryButtonClick += (s, args) => {
                    if (progressDialog.PrimaryButtonText == "Abort")
                    {
                        args.Cancel = true; 
                        tb.Text = "Aborting...";
                        cts.Cancel();
                    }
                };

                _ = progressDialog.ShowAsync();
                
                try {
                    await RepairService.PerformSurgicalRepairAsync(_currentConfigPath, _currentConfigPath, report, directUrl, (status, progress) => {
                        DispatcherQueue.TryEnqueue(() => {
                            tb.Text = status;
                            pb.Value = progress;
                        });
                    }, cts.Token);

                    progressDialog.Title = "Repair Complete";
                    tb.Text = "The repair process finished successfully.";
                    progressDialog.PrimaryButtonText = "Okay";
                }
                catch (OperationCanceledException) {
                    progressDialog.Hide();
                }
                catch (Exception ex) {
                    progressDialog.Hide();
                    await new ContentDialog { Title = "Repair Failed", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                }
            }
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e) 
        { 
            GlobalSettings.Save();
            ConfigOverlay.Visibility = Visibility.Collapsed; 
        }
        private void CancelConfig_Click(object sender, RoutedEventArgs e) { ConfigOverlay.Visibility = Visibility.Collapsed; }

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
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                IsPrimaryButtonEnabled = false
            };

            var stack = new StackPanel { Spacing = 12, Width = 400 };
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

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && resultsList.SelectedItem is SteamAppIdResult selected)
            {
                return selected.id;
            }
            return null;
        }
    }
}
