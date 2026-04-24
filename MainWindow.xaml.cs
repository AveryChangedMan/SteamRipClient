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
            int removed = GlobalSettings.ActiveDownloads.RemoveAll(d => 
                d.Phase == "Done" || 
                (!string.IsNullOrEmpty(d.PageUrl) && GlobalSettings.Library.Any(l => l.Url == d.PageUrl))
            );
            if (removed > 0)
            {
                Logger.Log($"[Cleanup] Pruned {removed} stale/completed downloads.");
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
        public void OpenConfig(string path, string title, string? exePath = null)
        {
            if (!GlobalSettings.GameConfigs.ContainsKey(path))
                GlobalSettings.GameConfigs[path] = new GameConfig();
            var config = GlobalSettings.GameConfigs[path];
            if (string.IsNullOrEmpty(config.ManualExePath) && !string.IsNullOrEmpty(exePath))
            {
                config.ManualExePath = exePath;
                config.WorkingDir = System.IO.Path.GetDirectoryName(exePath) ?? path;
            }
            ConfigTitle.Text = $"⚙️ {title}";
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
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi && nvi.Tag?.ToString() == "downloads")
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

