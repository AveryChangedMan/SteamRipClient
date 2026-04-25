using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SteamRipApp
{
    public sealed partial class LibraryPage : Page
    {
        public ObservableCollection<GameFolder> FoundGames { get; } = new ObservableCollection<GameFolder>();
        public ObservableCollection<SearchResult> DialogResults { get; } = new ObservableCollection<SearchResult>();
        public ObservableCollection<SearchResult> LinkResults { get; } = new ObservableCollection<SearchResult>();
        
        private static readonly System.Threading.SemaphoreSlim _scanLock = new System.Threading.SemaphoreSlim(1, 1);
        private bool _isRefreshing = false;
        private GameFolder? _activeGameForImage;
        private GameFolder? _activeGameForLink;
        private System.Threading.CancellationTokenSource? _moveCts;
        private DispatcherTimer? _processPollTimer;
        private DispatcherTimer? _redistPollTimer;
        private GameFolder? _activeRedistGame;

        public LibraryPage()
        {
            this.InitializeComponent();
            DialogResultsList.ItemsSource = DialogResults;
            LinkResultsList.ItemsSource = LinkResults;
            this.Loaded += async (s, e) => { 
                if (FoundGames.Count == 0 && !_isRefreshing) await RefreshLibrary(); 
                StartProcessPolling();
            };
            this.Unloaded += (s, e) => StopProcessPolling();
        }

        private void StartProcessPolling()
        {
            if (_processPollTimer == null)
            {
                _processPollTimer = new DispatcherTimer();
                _processPollTimer.Interval = TimeSpan.FromSeconds(2);
                _processPollTimer.Tick += (s, e) => PollProcesses();
            }
            _processPollTimer.Start();
        }

        private void StopProcessPolling() => _processPollTimer?.Stop();

        private void PollProcesses()
        {
            if (_isRefreshing) return;

            foreach (var game in FoundGames)
            {
                game.IsRunning = IsGameRunning(game);
            }
        }

        private bool IsGameRunning(GameFolder game)
        {
            if (string.IsNullOrEmpty(game.ExecutablePath)) return false;

            string exeName = Path.GetFileNameWithoutExtension(game.ExecutablePath);
            var procs = Process.GetProcessesByName(exeName);
            if (procs.Length > 0) return true;

            
            if (exeName.Contains("Launcher", StringComparison.OrdinalIgnoreCase))
            {
                string? dir = Path.GetDirectoryName(game.ExecutablePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var otherExes = Directory.GetFiles(dir, "*.exe");
                    foreach (var other in otherExes)
                    {
                        string otherName = Path.GetFileNameWithoutExtension(other);
                        if (otherName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Process.GetProcessesByName(otherName).Length > 0) return true;
                    }
                }
            }

            return false;
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            await RefreshLibrary();
        }

        public async Task RefreshAsync() => await RefreshLibrary();

        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string rootPath)
            {
                
                RepairService.TriggerManualBackup(rootPath, rootPath);
                
                
                var mw = (Application.Current as App)?.m_window as MainWindow;
                if (mw != null)
                {
                    
                    
                }
            }
        }

        private async Task RefreshLibrary()
        {
            if (!await _scanLock.WaitAsync(0)) return;

            try {
                _isRefreshing = true;
                LoadingRing.IsActive = true;
                RefreshBtn.IsEnabled = false;
                FoundGames.Clear();
                StatusLabel.Text = "Scanning your directories...";
                Logger.Log("[Library] refresh started.");

                var progress = new Progress<string>(msg => {
                    StatusLabel.Text = msg;
                    Logger.Log($"[Library] Progress: {msg}");
                });

                var results = await ScannerEngine.ScanDirectoriesAsync(GlobalSettings.ScanDirectories, progress);
                Logger.Log($"[Library] scan completed. Found {results.Count} candidates.");

                
                GlobalSettings.Library.RemoveAll(m => !Directory.Exists(m.LocalPath));

                var scannedPaths = new HashSet<string>(
                    results.Select(g => g.RootPath), StringComparer.OrdinalIgnoreCase);

                foreach (var game in results)
                {
                    
                    if (game.Title.Contains("Free Download", StringComparison.OrdinalIgnoreCase))
                    {
                        int index = game.Title.IndexOf("Free Download", StringComparison.OrdinalIgnoreCase);
                        game.Title = game.Title.Substring(0, index).TrimEnd('-', ' ', '.');
                        Logger.Log($"[Library] Repaired title: {game.Title}");
                    }

                    if (GlobalSettings.GamePageLinks.TryGetValue(game.RootPath, out var savedUrl) && !string.IsNullOrEmpty(savedUrl))
                        game.Url = savedUrl;
                    
                    
                    if (GlobalSettings.CurrentMove != null && 
                        (GlobalSettings.CurrentMove.SourcePath.Equals(game.RootPath, StringComparison.OrdinalIgnoreCase) ||
                         GlobalSettings.CurrentMove.TargetPath.Equals(game.RootPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        game.IsMoveInterrupted = true;
                        Logger.Log($"[Library] Detected interrupted move for: {game.Title}");
                    }

                    var metadata = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(game.RootPath, StringComparison.OrdinalIgnoreCase));
                    if (metadata != null)
                    {
                        if (string.IsNullOrEmpty(game.Version)) game.Version = metadata.Version;
                        if (string.IsNullOrEmpty(game.Url))     game.Url = metadata.Url;
                        if (string.IsNullOrEmpty(game.ImageUrl)) game.ImageUrl = metadata.ImageUrl;
                        metadata.Title = game.Title; 
                    }
                    else
                    {
                        
                        GlobalSettings.Library.Add(new GameMetadata {
                            Title = game.Title,
                            LocalPath = game.RootPath,
                            Version = game.Version,
                            Url = game.Url,
                            ImageUrl = game.ImageUrl ?? string.Empty
                        });
                    }
                    FoundGames.Add(game);
                }

                foreach (var meta in GlobalSettings.Library)
                {
                    if (scannedPaths.Contains(meta.LocalPath)) continue; 
                    if (scannedPaths.Any(p => 
                        meta.LocalPath.StartsWith(p, StringComparison.OrdinalIgnoreCase) || 
                        p.StartsWith(meta.LocalPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.Log($"[Library] Skipping duplicate/nested metadata entry: {meta.LocalPath}");
                        continue;
                    }

                    if (!Directory.Exists(meta.LocalPath)) continue;

                    Logger.Log($"[Library] Adding from metadata (not found by scanner): {meta.LocalPath}");

                    var localImage = Path.Combine(meta.LocalPath, "folder.jpg");
                    if (!File.Exists(localImage)) localImage = Path.Combine(meta.LocalPath, "folder.png");

                    var fallback = new GameFolder
                    {
                        Title          = ScannerEngine.CleanTitle(meta.Title),
                        RootPath       = meta.LocalPath,
                        Version        = meta.Version,
                        Url            = meta.Url,
                        ImageUrl       = meta.ImageUrl,
                        LocalImagePath = File.Exists(localImage) ? localImage : null,
                        SizeBytes      = 0,
                        IsEmulatorApplied = meta.IsEmulatorApplied
                    };

                    if (GlobalSettings.GamePageLinks.TryGetValue(meta.LocalPath, out var lUrl) && !string.IsNullOrEmpty(lUrl))
                        fallback.Url = lUrl;

                    FoundGames.Add(fallback);
                }

                StatusLabel.Text = $"Found {FoundGames.Count} game(s).";
                GameGrid.ItemsSource = FoundGames;
            } catch (Exception ex) {
                Logger.LogError("RefreshLibrary", ex);
                StatusLabel.Text = "Scan failed. Check logs.";
            } finally {
                LoadingRing.IsActive = false;
                RefreshBtn.IsEnabled = true;
                _isRefreshing = false;
                _scanLock.Release();
            }
        }

        private void ConfigBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string rootPath)
            {
                var gf = FoundGames.FirstOrDefault(g => g.RootPath == rootPath);
                if (gf != null)
                {
                    var window = (Application.Current as App)?.m_window as MainWindow;
                    window?.OpenConfig(gf.RootPath, gf.Title, gf.GameSubFolderPath, gf.ExecutablePath);
                }
            }
        }

        private async void Properties_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string rootPath)
            {
                var gf = FoundGames.FirstOrDefault(g => g.RootPath == rootPath);
                if (gf == null) return;

                if (string.IsNullOrEmpty(gf.Url))
                {
                    StatusLabel.Text = "Recovering game URL...";
                    gf.Url = await SteamRipScraper.SearchUrlByFolderNameAsync(gf.Title);
                    if (string.IsNullOrEmpty(gf.Url)) {
                        StatusLabel.Text = "Could not find game page. Use Manual Link in Properties.";
                    }
                }

                _activeGameForLink = gf;

                PropertiesOverlayDimmer.Visibility = Visibility.Visible;
                PropertiesDialog.Visibility = Visibility.Visible;
                RequirementsLoading.IsActive = true;
                RequirementsList.Children.Clear();
                GameInfoList.Children.Clear();

                var infoPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                infoPanel.Children.Add(new TextBlock {
                    Text = "Installed",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Width = 110,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)
                });
                infoPanel.Children.Add(new TextBlock { Text = !string.IsNullOrEmpty(gf.Version) ? gf.Version : "Unknown" });
                GameInfoList.Children.Add(infoPanel);

                if (!string.IsNullOrEmpty(gf.Url))
                {
                    try {
                        LiveChatWebView.Source = new Uri(gf.Url);
                        var details = await SteamRipScraper.GetGameDetailsAsync(gf.Url);
                        foreach (var info in details.GameInfo)
                        {
                            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                            panel.Children.Add(new TextBlock {
                                Text = info.Key,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                Width = 110,
                                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)
                            });
                            panel.Children.Add(new TextBlock { Text = info.Value, TextWrapping = TextWrapping.Wrap });
                            GameInfoList.Children.Add(panel);
                        }

                        var localSpecs = HardwareSpecsEngine.GetLocalSpecs();
                        foreach (var req in details.SystemRequirements)
                        {
                            var result = HardwareSpecsEngine.EvaluateRequirement(req.Key, req.Value, localSpecs, gf.RootPath);
                            var icon = result == true ? "✅" : (result == false ? "❌" : "➖");
                            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                            panel.Children.Add(new TextBlock { Text = icon, Width = 20 });
                            panel.Children.Add(new TextBlock { Text = $"{req.Key}:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Width = 100 });
                            panel.Children.Add(new TextBlock { Text = req.Value, TextWrapping = TextWrapping.Wrap });
                            RequirementsList.Children.Add(panel);
                        }
                    } catch (Exception ex) {
                        Logger.LogError("LibraryPropsUI", ex);
                    }
                }
                else
                {
                    var noUrlPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    noUrlPanel.Children.Add(new TextBlock {
                        Text = "⚠ No URL linked — use Manual Link below.",
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange)
                    });
                    GameInfoList.Children.Add(noUrlPanel);
                }
                RequirementsLoading.IsActive = false;
            }
        }

        private async void LiveChatWebView_NavigationCompleted(WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                await Task.Delay(1500);
                await sender.ExecuteScriptAsync(@"
                    (function() {
                        var style = document.createElement('style');
                        style.textContent = `
                            body { background: #1a1a2e !important; color: #e0e0e0 !important; }
                            a[target='_blank'] { pointer-events: none !important; }
                            .popup, .modal, .overlay, [class*='popup'], [class*='modal'],
                            .adsbygoogle, ins.adsbygoogle, iframe[src*='ads'],
                            .cookie-notice, .gdpr, #cookie { display: none !important; }
                            header, .site-header, nav, .navigation, .menu-container,
                            footer, .site-footer, .sidebar, #sidebar,
                            .social-share, .related-posts, .post-navigation { display: none !important; }
                        `;
                        document.head.appendChild(style);

                        document.querySelectorAll('a[href]').forEach(function(a) {
                            var href = a.getAttribute('href');
                            if (href && (href.startsWith('http') && !href.includes('steamrip.com'))) {
                                a.removeAttribute('href');
                                a.style.pointerEvents = 'none';
                                a.style.opacity = '0.5';
                            }
                        });

                        document.querySelectorAll('.popup, .modal, [class*=""popup""], [class*=""modal""]').forEach(function(el) {
                            el.remove();
                        });
                    })();
                ");
            }
        }

        private void ManualLinkBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameForLink == null) return;
            LinkSearchBox.Text = _activeGameForLink.Title.Replace("SteamRIP.com", "").Replace("-", " ").Trim();
            LinkResults.Clear();
            LinkConfirmBtn.IsEnabled = false;
            LinkSearchOverlay.Visibility = Visibility.Visible;
            LinkSearchDialog.Visibility = Visibility.Visible;
        }

        private async void LinkSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            try {
                LinkLoadingRing.IsActive = true;
                LinkResults.Clear();
                var results = await SteamRipScraper.SearchAsync(args.QueryText);
                foreach (var res in results) LinkResults.Add(res);
            } catch (Exception ex) {
                Logger.LogError("LinkSearch", ex);
            } finally {
                LinkLoadingRing.IsActive = false;
            }
        }

        private void LinkResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LinkConfirmBtn.IsEnabled = LinkResultsList.SelectedItem != null;
        }

        private void LinkConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameForLink != null && LinkResultsList.SelectedItem is SearchResult selected)
            {
                _activeGameForLink.Url = selected.Url;
                GlobalSettings.GamePageLinks[_activeGameForLink.RootPath] = selected.Url;
                var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == _activeGameForLink.RootPath);
                if (meta != null) meta.Url = selected.Url;
                GlobalSettings.Save();
                Logger.Log($"[Library] Linked '{_activeGameForLink.Title}' -> {selected.Url}");

                LinkSearchOverlay.Visibility = Visibility.Collapsed;
                LinkSearchDialog.Visibility = Visibility.Collapsed;
                PropertiesOverlayDimmer.Visibility = Visibility.Collapsed;
                PropertiesDialog.Visibility = Visibility.Collapsed;
                StatusLabel.Text = $"Linked to: {selected.Title}";

                var fakeBtn = new Button { Tag = _activeGameForLink.RootPath };
                Properties_Click(fakeBtn, new RoutedEventArgs());
            }
        }

        private void LinkCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            LinkSearchOverlay.Visibility = Visibility.Collapsed;
            LinkSearchDialog.Visibility = Visibility.Collapsed;
        }

        private void PropsCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            PropertiesOverlayDimmer.Visibility = Visibility.Collapsed;
            PropertiesDialog.Visibility = Visibility.Collapsed;
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string rootPath) return;

            var gf = FoundGames.FirstOrDefault(g => g.RootPath == rootPath);
            if (gf == null) return;

            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == rootPath);
            bool isSteamIntegrated = meta?.IsSteamIntegrated ?? false;
            bool shouldRemoveFromSteam = false;

            if (isSteamIntegrated)
            {
                if (GlobalSettings.RemoveFromSteamPreference.HasValue)
                {
                    shouldRemoveFromSteam = GlobalSettings.RemoveFromSteamPreference.Value;
                }
                else
                {
                    var checkBox = new CheckBox { Content = "Do not ask again (remember choice)", Margin = new Thickness(0, 20, 0, 0) };
                    var stack = new StackPanel();
                    stack.Children.Add(new TextBlock { Text = $"This game is currently integrated with your Steam Library.\n\nWould you like to remove it from Steam as well?", TextWrapping = TextWrapping.Wrap });
                    stack.Children.Add(checkBox);

                    var steamDialog = new ContentDialog
                    {
                        Title = "Steam Integration Detected",
                        Content = stack,
                        PrimaryButtonText = "Remove from both",
                        SecondaryButtonText = "PC Only",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot
                    };

                    var steamResult = await steamDialog.ShowAsync();
                    if (steamResult == ContentDialogResult.None) return;

                    shouldRemoveFromSteam = (steamResult == ContentDialogResult.Primary);
                    if (checkBox.IsChecked == true)
                    {
                        GlobalSettings.RemoveFromSteamPreference = shouldRemoveFromSteam;
                        GlobalSettings.Save();
                    }
                }
            }

            var dialog = new ContentDialog
            {
                Title = $"Uninstall \"{gf.Title}\"?",
                Content = $"This will permanently delete:\n{rootPath}\n\nThis cannot be undone.",
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            try {
                Logger.Log($"[Library] Uninstalling: {rootPath}");
                StatusLabel.Text = $"Uninstalling {gf.Title}... (Deleting files)";
                
                
                RepairService.StopHashingForGame(rootPath);

                if (shouldRemoveFromSteam && !string.IsNullOrEmpty(gf.ExecutablePath))
                {
                    await SteamManager.RemoveGameFromSteam(gf.Title, gf.ExecutablePath);
                    GlobalSettings.IsSteamUpdateRequired = true;
                }

                await Task.Run(() => {
                    if (Directory.Exists(rootPath))
                        Directory.Delete(rootPath, recursive: true);
                });

                if (meta != null) GlobalSettings.Library.Remove(meta);
                GlobalSettings.GamePageLinks.Remove(rootPath);
                GlobalSettings.Save();

                FoundGames.Remove(gf);
                StatusLabel.Text = $"Uninstalled: {gf.Title}";
            } catch (Exception ex) {
                Logger.LogError("Uninstall", ex);
                StatusLabel.Text = "Uninstall failed — check logs.";
            }
        }

        private void FindImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string rootPath)
            {
                var gf = FoundGames.FirstOrDefault(g => g.RootPath == rootPath);
                if (gf == null) return;
                _activeGameForImage = gf;
                DialogSearchBox.Text = gf.Title.Replace("SteamRIP.com", "").Replace("-", " ").Trim();
                DialogResults.Clear();
                DialogSetCoverBtn.IsEnabled = false;
                ImageSearchOverlay.Visibility = Visibility.Visible;
                ImageSearchDialog.Visibility = Visibility.Visible;
            }
        }

        private async void DialogSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            try {
                DialogLoadingRing.IsActive = true;
                DialogResults.Clear();
                var results = await SteamRipScraper.SearchAsync(args.QueryText);
                foreach (var res in results) DialogResults.Add(res);
            } catch (Exception ex) {
                Logger.LogError("DialogSearch", ex);
            } finally {
                DialogLoadingRing.IsActive = false;
            }
        }

        private void DialogResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DialogSetCoverBtn.IsEnabled = DialogResultsList.SelectedItem != null;
        }

        private async void DialogSetCoverBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameForImage != null && DialogResultsList.SelectedItem is SearchResult selected)
            {
                var gf = _activeGameForImage;
                ImageSearchOverlay.Visibility = Visibility.Collapsed;
                ImageSearchDialog.Visibility = Visibility.Collapsed;
                await ScannerEngine.DownloadGameImageAsync(selected.ImageUrl, gf.RootPath);
                gf.LocalImagePath = null;
                gf.LocalImagePath = Path.Combine(gf.RootPath, "folder.jpg");
                var index = FoundGames.IndexOf(gf);
                if (index != -1)
                {
                    FoundGames.RemoveAt(index);
                    FoundGames.Insert(index, gf);
                }
            }
        }

        private void DialogCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            ImageSearchOverlay.Visibility = Visibility.Collapsed;
            ImageSearchDialog.Visibility = Visibility.Collapsed;
        }


        private void InstallRedist_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                var gf = FoundGames.FirstOrDefault(g => g.RootPath == path);
                if (gf != null)
                {
                    if (gf.MissingRedists == null || gf.MissingRedists.Count == 0)
                    {
                        gf.MissingRedists = RedistService.GetRequiredRedists(gf.RootPath);
                    }

                    _activeRedistGame = gf;
                    var missing = gf.MissingRedists.Where(r => !r.IsInstalled).ToList();
                    
                    RedistMissingList.ItemsSource = missing;
                    RedistOverlay.Visibility = Visibility.Visible;
                    RedistPanel.Visibility = Visibility.Visible;

                    if (_redistPollTimer == null)
                    {
                        _redistPollTimer = new DispatcherTimer();
                        _redistPollTimer.Interval = TimeSpan.FromSeconds(2);
                        _redistPollTimer.Tick += (s, args) => PollRedistStatus();
                    }
                    _redistPollTimer.Start();
                }
            }
        }

        private void PollRedistStatus()
        {
            if (_activeRedistGame == null) return;

            bool anyChanged = false;
            foreach (var redist in _activeRedistGame.MissingRedists)
            {
                if (!redist.IsInstalled)
                {
                    bool nowInstalled = RedistService.CheckIfInstalled(redist.FileName);
                    if (nowInstalled)
                    {
                        redist.IsInstalled = true;
                        anyChanged = true;
                    }
                }
            }

            if (anyChanged)
            {
                RedistMissingList.ItemsSource = _activeRedistGame.MissingRedists.Where(r => !r.IsInstalled).ToList();
                _activeRedistGame.IsRedistMissing = _activeRedistGame.HasMissingRedists;
                
                if (!_activeRedistGame.HasMissingRedists)
                {
                    _redistPollTimer?.Stop();
                    RedistPanelClose_Click(new object(), new RoutedEventArgs());
                }
            }
        }

        private void OpenRedistInstaller_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string fullPath)
            {
                try {
                    Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
                } catch (Exception ex) {
                    Logger.LogError("LaunchRedist", ex);
                }
            }
        }

        private void RedistPanelClose_Click(object sender, RoutedEventArgs e)
        {
            RedistOverlay.Visibility = Visibility.Collapsed;
            RedistPanel.Visibility = Visibility.Collapsed;
            _redistPollTimer?.Stop();
        }

        private void GameGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is GameFolder gf)
            {
                var window = (Application.Current as App)?.m_window as MainWindow;
                
                window?.OpenConfig(gf.RootPath, gf.Title, gf.GameSubFolderPath, gf.ExecutablePath);
            }
        }

        private void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                var gf = FoundGames.FirstOrDefault(g => g.RootPath == path);
                if (gf == null) return;

                if (gf.IsRunning)
                {
                    StopGame(gf);
                    return;
                }

                if (!GlobalSettings.GameConfigs.ContainsKey(path))
                    GlobalSettings.GameConfigs[path] = new GameConfig();

                var config = GlobalSettings.GameConfigs[path];
                var exe = config.ManualExePath;

                if (string.IsNullOrEmpty(exe))
                {
                    NativeBridgeService.Log($"No manual executable set for '{path}'. Attempting auto-detection...", "LAUNCHER");
                    var di = new DirectoryInfo(path);
                    var allExes = di.GetFiles("*.exe", SearchOption.AllDirectories);
                    exe = allExes.OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
                }

                if (string.IsNullOrEmpty(exe))
                {
                    NativeBridgeService.Log($"Launch failed: No executable detected in directory '{path}'", "LAUNCHER");
                    return;
                }

                try {
                    NativeBridgeService.Log($"Manual launch initiated: {path}", "LAUNCHER");
                    var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == path);
                    if (meta != null && meta.IsSteamIntegrated && (meta.ManualSteamAppId ?? meta.SteamAppId).HasValue)
                    {
                        int appId = (meta.ManualSteamAppId ?? meta.SteamAppId)!.Value;
                        NativeBridgeService.Log($"Integration active. Launching via Steam (ID: {appId})...", "LAUNCHER");
                        Process.Start(new ProcessStartInfo { FileName = $"steam://rungameid/{appId}", UseShellExecute = true });
                        if (GlobalSettings.CloseAppOnLaunch) Application.Current.Exit();
                        return;
                    }

                    NativeBridgeService.Log($"Direct launch: {exe}", "LAUNCHER");
                    var startInfo = new ProcessStartInfo {
                        FileName = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe),
                        Arguments = config.LaunchArguments,
                        UseShellExecute = true
                    };

                    if (config.RunAsAdmin) {
                        NativeBridgeService.Log("Elevation requested.", "LAUNCHER");
                        startInfo.Verb = "runas";
                    }

                    var proc = Process.Start(startInfo);
                    if (proc != null) NativeBridgeService.Log($"Process started (PID: {proc.Id})", "LAUNCHER");
                    if (GlobalSettings.CloseAppOnLaunch) Application.Current.Exit();
                } catch (Exception ex) {
                    Logger.LogError("Launch", ex);
                    NativeBridgeService.Log($"Launch error: {ex.Message}", "LAUNCHER");
                }
            }
        }

        private async void RepairMove_Click(object sender, RoutedEventArgs e)
        {
            if (GlobalSettings.CurrentMove == null) return;
            
            var dialog = new ContentDialog {
                Title = "Repair Interrupted Move",
                Content = $"An interrupted move for \"{GlobalSettings.CurrentMove.GameTitle}\" was detected.\n\nWould you like to resume the process?",
                PrimaryButtonText = "Resume Move",
                SecondaryButtonText = "Delete Partially Moved Files",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await PerformMoveAsync(GlobalSettings.CurrentMove.SourcePath, GlobalSettings.CurrentMove.TargetPath, GlobalSettings.CurrentMove.GameTitle, true);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                try {
                    if (Directory.Exists(GlobalSettings.CurrentMove.TargetPath))
                        Directory.Delete(GlobalSettings.CurrentMove.TargetPath, true);
                    GlobalSettings.CurrentMove = null;
                    GlobalSettings.Save();
                    await RefreshLibrary();
                } catch (Exception ex) {
                    Logger.LogError("RepairMove_Cleanup", ex);
                }
            }
        }

        private async void MoveInstall_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not HyperlinkButton btn || btn.Tag is not string currentPath) return;

            if (GlobalSettings.CurrentMove != null)
            {
                var busyDialog = new ContentDialog {
                    Title = "Move in Progress",
                    Content = $"Another move operation is currently active for \"{GlobalSettings.CurrentMove.GameTitle}\". Please wait for it to finish.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await busyDialog.ShowAsync();
                return;
            }

            var gf = FoundGames.FirstOrDefault(g => g.RootPath == currentPath);
            if (gf == null) return;

            
            var availableTargets = GlobalSettings.ScanDirectories
                .Where(d => !currentPath.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (availableTargets.Count == 0) {
                var noTargetsDialog = new ContentDialog {
                    Title = "No Destination Available",
                    Content = "Add another scan location in Settings to move games between drives.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await noTargetsDialog.ShowAsync();
                return;
            }

            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 16, 0, 0), ItemsSource = availableTargets, SelectedIndex = 0 };
            var dialog = new ContentDialog {
                Title = $"Move \"{gf.Title}\"",
                Content = new StackPanel { Children = { new TextBlock { Text = "Select the scan location to move this game to:" }, combo } },
                PrimaryButtonText = "Move",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || combo.SelectedItem == null) return;

            string targetRoot = combo.SelectedItem.ToString()!;
            string targetPath = Path.Combine(targetRoot, Path.GetFileName(currentPath));

            if (Directory.Exists(targetPath)) {
                var errorDialog = new ContentDialog { Title = "Move Failed", Content = $"Destination folder already exists.", CloseButtonText = "OK", XamlRoot = this.XamlRoot };
                await errorDialog.ShowAsync();
                return;
            }

            await PerformMoveAsync(currentPath, targetPath, gf.Title, false);
        }

        private async Task PerformMoveAsync(string sourcePath, string targetPath, string title, bool isResume)
        {
            _moveCts = new System.Threading.CancellationTokenSource();
            var window = (Application.Current as App)?.m_window as MainWindow;
            string movingSuffix = "-moving";
            string tempTargetPath = targetPath.EndsWith(movingSuffix) ? targetPath : targetPath + movingSuffix;
            
            try {
                if (!isResume) {
                    GlobalSettings.CurrentMove = new MoveOperation {
                        SourcePath = sourcePath,
                        TargetPath = tempTargetPath,
                        GameTitle = title,
                        StartTime = DateTime.Now
                    };
                    GlobalSettings.Save();
                }

                var game = FoundGames.FirstOrDefault(g => g.RootPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
                if (game != null) game.IsMoving = true;

                window?.SetGlobalMoveStatus($"Moving {title}...", true);
                window?.UpdateGlobalProgress(0, true);

                await Task.Run(async () => {
                    var sourceInfo = new DirectoryInfo(sourcePath);
                    
                    var files = sourceInfo.GetFiles("*", SearchOption.AllDirectories)
                                          .OrderBy(f => f.FullName)
                                          .ToList();
                    
                    int totalFiles = files.Count;
                    if (totalFiles == 0) return;

                    Directory.CreateDirectory(tempTargetPath);

                    int chunkSize = 20;
                    int totalChunks = (int)Math.Ceiling(totalFiles / (double)chunkSize);

                    for (int i = 0; i < totalFiles; i += chunkSize)
                    {
                        if (_moveCts.Token.IsCancellationRequested) break;

                        int currentChunk = (i / chunkSize) + 1;
                        var chunkFiles = files.Skip(i).Take(chunkSize).ToList();

                        DispatcherQueue.TryEnqueue(() => {
                            window?.SetGlobalMoveStatus($"Moving {title} (Chunk {currentChunk}/{totalChunks})");
                        });

                        foreach (var file in chunkFiles)
                        {
                            if (_moveCts.Token.IsCancellationRequested) break;

                            string relative = Path.GetRelativePath(sourcePath, file.FullName);
                            string dest = Path.Combine(tempTargetPath, relative);

                            if (GlobalSettings.CurrentMove!.CopiedFiles.Contains(relative) && File.Exists(dest))
                                continue;

                            string? destDir = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                            
                            using (var sourceStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                            using (var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                            {
                                await sourceStream.CopyToAsync(destStream, 81920, _moveCts.Token);
                            }

                            GlobalSettings.CurrentMove.CopiedFiles.Add(relative);
                        }

                        
                        GlobalSettings.Save();

                        double pct = (Math.Min(i + chunkSize, totalFiles) * 100.0) / totalFiles;
                        DispatcherQueue.TryEnqueue(() => {
                            window?.UpdateGlobalProgress(pct);
                        });
                    }

                    if (_moveCts?.Token.IsCancellationRequested == true) throw new OperationCanceledException();

                    if (GlobalSettings.CurrentMove != null) GlobalSettings.CurrentMove.IsCopyFinished = true;
                    GlobalSettings.Save();

                    
                    int deletedCount = 0;
                    DispatcherQueue.TryEnqueue(() => window?.SetGlobalMoveStatus("Cleaning up...", true));
                    
                    foreach (var file in files)
                    {
                        if (_moveCts?.IsCancellationRequested == true) break;
                        try {
                            File.Delete(file.FullName);
                            deletedCount++;
                            if (deletedCount % 20 == 0)
                            {
                                double pct = (deletedCount * 100.0) / totalFiles;
                                DispatcherQueue.TryEnqueue(() => {
                                    window?.UpdateGlobalProgress(pct);
                                });
                            }
                        } catch { }
                    }

                    
                    if (_moveCts?.IsCancellationRequested == false)
                    {
                        DispatcherQueue.TryEnqueue(() => window?.SetGlobalMoveStatus("Finalizing...", true));
                        
                        string finalPath = tempTargetPath.EndsWith(movingSuffix) 
                            ? tempTargetPath.Substring(0, tempTargetPath.Length - movingSuffix.Length) 
                            : tempTargetPath;

                        if (Directory.Exists(finalPath) && finalPath != tempTargetPath) 
                            Directory.Delete(finalPath, true);
                        
                        Directory.Move(tempTargetPath, finalPath);

                        DispatcherQueue.TryEnqueue(() => {
                            UpdateSettingsPaths(sourcePath, finalPath);
                            
                            try { if (Directory.Exists(sourcePath)) Directory.Delete(sourcePath, true); } catch { }
                            
                            GlobalSettings.CurrentMove = null;
                            GlobalSettings.Save();
                        });
                    }
                }, _moveCts.Token);

                if (game != null) game.IsMoving = false;
                window?.SetGlobalMoveStatus("", false);
                window?.UpdateGlobalProgress(0, false);
                await RefreshLibrary();
            }
            catch (OperationCanceledException) {
                var game = FoundGames.FirstOrDefault(g => g.RootPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
                if (game != null) game.IsMoving = false;
                window?.SetGlobalMoveStatus("Move Cancelled", true);
                window?.UpdateGlobalProgress(0, false);
            }
            catch (Exception ex) {
                Logger.LogError("PerformMove", ex);
                var game = FoundGames.FirstOrDefault(g => g.RootPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
                if (game != null) game.IsMoving = false;
                window?.SetGlobalMoveStatus("Error during move", true);
            }
            finally {
                _moveCts?.Dispose();
                _moveCts = null;
            }
        }

        public void CancelMove()
        {
            _moveCts?.Cancel();
            var window = (Application.Current as App)?.m_window as MainWindow;
            window?.SetGlobalMoveStatus("Cancelling...");
        }

        private void CancelMoveBtn_Click(object sender, RoutedEventArgs e)
        {
            CancelMove();
        }

        private void UpdateSettingsPaths(string oldPath, string newPath)
        {
            
            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
            if (meta != null)
            {
                
                var existing = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(newPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null && existing != meta)
                {
                    GlobalSettings.Library.Remove(meta);
                }
                else
                {
                    meta.LocalPath = newPath;
                }
            }

            
            if (GlobalSettings.GameConfigs.TryGetValue(oldPath, out var config))
            {
                GlobalSettings.GameConfigs[newPath] = config;
                GlobalSettings.GameConfigs.Remove(oldPath);
                
                
                if (!string.IsNullOrEmpty(config.ManualExePath) && config.ManualExePath.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    config.ManualExePath = config.ManualExePath.Replace(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
                }
            }

            
            if (GlobalSettings.GamePageLinks.TryGetValue(oldPath, out var link))
            {
                GlobalSettings.GamePageLinks[newPath] = link;
                GlobalSettings.GamePageLinks.Remove(oldPath);
            }

            
            
            
            
            
            GlobalSettings.Save();
            Logger.Log($"[Library] Settings paths updated from {oldPath} to {newPath}");
        }

        private async void StopGame(GameFolder gf)
        {
            string? exePath = gf.ExecutablePath;
            if (string.IsNullOrEmpty(exePath)) return;
            string exeName = Path.GetFileNameWithoutExtension(exePath);
            
            
            var dialog = new ContentDialog
            {
                Title = "Stop Process?",
                Content = "Are you sure you want to force close this game?\n\nWARNING: This May Cause Data Loss. Use at own risk.",
                PrimaryButtonText = "STOP",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            try {
                
                foreach (var p in Process.GetProcessesByName(exeName)) p.Kill();

                
                if (exeName.Contains("Launcher", StringComparison.OrdinalIgnoreCase))
                {
                    string? dir = Path.GetDirectoryName(gf.ExecutablePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        var otherExes = Directory.GetFiles(dir, "*.exe");
                        foreach (var other in otherExes)
                        {
                            string oName = Path.GetFileNameWithoutExtension(other);
                            if (oName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                            foreach (var p in Process.GetProcessesByName(oName)) p.Kill();
                        }
                    }
                }
                NativeBridgeService.Log($"[Library] Force stopped process for {gf.Title}", "SYSTEM");
            } catch (Exception ex) {
                Logger.LogError("StopGame", ex);
            }
        }
    }
}
