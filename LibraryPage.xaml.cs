using System.Text.Json;
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
        public ObservableCollection<GameFolder> FoundGames { get; } = [];
        public ObservableCollection<SearchResult> DialogResults { get; } = [];
        public ObservableCollection<SearchResult> LinkResults { get; } = [];

        private static readonly System.Threading.SemaphoreSlim _scanLock = new(1, 1);
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private bool _isRefreshing = false;
        private GameFolder? _activeGameForImage;
        private GameFolder? _activeGameForLink;
        private System.Threading.CancellationTokenSource? _moveCts;
        private DispatcherTimer? _processPollTimer;
        private DispatcherTimer? _redistPollTimer;
        private GameFolder? _activeRedistGame;
        private static string? _persistedStatusText = null;

        public LibraryPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            DialogResultsList.ItemsSource = DialogResults;
            LinkResultsList.ItemsSource = LinkResults;
            this.Loaded += async (s, e) => {
                if (!string.IsNullOrEmpty(_persistedStatusText)) StatusLabel.Text = _persistedStatusText;
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

        private static bool IsGameRunning(GameFolder game)
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

        private void Backup_Click(object sender, RoutedEventArgs _)
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
            GlobalSettings.Load();
            if (!await _scanLock.WaitAsync(0)) return;

            try {
                _isRefreshing = true;
                LoadingRing.IsActive = true;
                RefreshBtn.IsEnabled = false;
                StatusLabel.Text = "Scanning your directories...";
                _persistedStatusText = StatusLabel.Text;
                Logger.Log("[Library] refresh started.");

                var progress = new Progress<string>(msg => {
                    StatusLabel.Text = msg;
                    _persistedStatusText = msg;
                    Logger.Log($"[Library] Progress: {msg}");
                });

                var results = await ScannerEngine.ScanDirectoriesAsync(GlobalSettings.ScanDirectories, progress);
                Logger.Log($"[Library] scan completed. Found {results.Count} candidates.");

                GlobalSettings.Library.RemoveAll(m => {
                    string? root = Path.GetPathRoot(m.LocalPath);
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
                    return !Directory.Exists(m.LocalPath);
                });

                var scannedPaths = new HashSet<string>(
                    results.Select(g => g.RootPath), StringComparer.OrdinalIgnoreCase);

                var existingGames = FoundGames.ToDictionary(g => g.RootPath, g => g, StringComparer.OrdinalIgnoreCase);
                var newGamesList = new List<GameFolder>();

                foreach (var game in results)
                {
                    try {
                        game.Title = ScannerEngine.CleanTitle(game.Title);
                        if (GlobalSettings.GamePageLinks.TryGetValue(game.RootPath, out var savedUrl) && !string.IsNullOrEmpty(savedUrl))
                            game.Url = savedUrl;

                        if (GlobalSettings.CurrentMove != null &&
                            (GlobalSettings.CurrentMove.SourcePath.Equals(game.RootPath, StringComparison.OrdinalIgnoreCase) ||
                             GlobalSettings.CurrentMove.TargetPath.Equals(game.RootPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            game.IsMoveInterrupted = true;
                        }

                        var metadata = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(game.RootPath, StringComparison.OrdinalIgnoreCase));
                        if (metadata != null)
                        {
                            if (string.IsNullOrEmpty(game.Version)) game.Version = metadata.Version ?? "1.0.0";
                            if (string.IsNullOrEmpty(game.Url))     game.Url = metadata.Url ?? "";
                            if (string.IsNullOrEmpty(game.ImageUrl)) game.ImageUrl = metadata.ImageUrl ?? "";
                            metadata.Title = game.Title;
                        }
                        else
                        {
                            GlobalSettings.Library.Add(new GameMetadata {
                                Title = game.Title,
                                LocalPath = game.RootPath,
                                Version = game.Version ?? "1.0.0",
                                Url = game.Url ?? "",
                                ImageUrl = game.ImageUrl ?? string.Empty
                            });
                        }

                        if (existingGames.TryGetValue(game.RootPath, out var existing))
                        {

                            existing.Title = game.Title;
                            existing.Version = game.Version ?? "";
                            existing.Url = game.Url ?? "";
                            existing.ImageUrl = game.ImageUrl ?? "";
                            existing.ExecutablePath = game.ExecutablePath ?? "";
                            existing.IsMoveInterrupted = game.IsMoveInterrupted;
                            newGamesList.Add(existing);
                        }
                        else
                        {
                            newGamesList.Add(game);
                        }
                    } catch (Exception ex) {
                        Logger.LogError($"RefreshLibrary_Loop_Found", ex);
                    }
                }

                FoundGames.Clear();
                foreach (var g in newGamesList) FoundGames.Add(g);

                foreach (var meta in GlobalSettings.Library)
                {
                    try {
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
                            Version        = meta.Version ?? "1.0.0",
                            Url            = meta.Url ?? "",
                            ImageUrl       = meta.ImageUrl ?? "",
                            LocalImagePath = File.Exists(localImage) ? localImage : null,
                            SizeBytes      = 0,
                            IsEmulatorApplied = meta.IsEmulatorApplied
                        };

                        if (GlobalSettings.GamePageLinks.TryGetValue(meta.LocalPath, out var lUrl) && !string.IsNullOrEmpty(lUrl))
                            fallback.Url = lUrl;

                        FoundGames.Add(fallback);
                    } catch (Exception ex) {
                        Logger.LogError($"RefreshLibrary_Loop_Meta", ex);
                    }
                }

                StatusLabel.Text = $"Found {FoundGames.Count} game(s).";
                GameGrid.ItemsSource = FoundGames;

                var gamesSnapshot = FoundGames.ToList();
                _ = Task.Run(async () =>
                {
                    using var semaphore = new System.Threading.SemaphoreSlim(8);
                    var tasks = gamesSnapshot.Select(async game =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            string expectedImg = Path.Combine(game.RootPath, "folder.jpg");
                            if (File.Exists(expectedImg)) return;

                            string? imgUrl = game.ImageUrl;
                            if (string.IsNullOrEmpty(imgUrl))
                            {
                                var meta = GlobalSettings.Library.FirstOrDefault(m =>
                                    m.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    .Equals(game.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                            StringComparison.OrdinalIgnoreCase));
                                imgUrl = meta?.ImageUrl;
                            }

                            if (string.IsNullOrEmpty(imgUrl)) return;

                            await ScannerEngine.DownloadGameImageAsync(imgUrl, game.RootPath);

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (File.Exists(expectedImg))
                                {
                                    game.LocalImagePath = expectedImg;
                                    Logger.Log($"[Library] Cover image recovered for {game.Title}");
                                }
                            });
                        }
                        catch { }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tasks);
                });
            } catch (Exception ex) {
                Logger.LogError("RefreshLibrary", ex);
                StatusLabel.Text = $"Error: {ex.GetType().Name} - {ex.Message}";

                if (ex is FormatException)
                {
                    Logger.Log($"[CRITICAL] FormatException caught in RefreshLibrary. StackTrace: {ex.StackTrace}");
                }
            } finally {
                LoadingRing.IsActive = false;
                RefreshBtn.IsEnabled = true;
                _isRefreshing = false;
                _scanLock.Release();
            }
        }

        private void ConfigBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string rootPath)
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
            if (sender is FrameworkElement fe && fe.Tag is string rootPath)
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

                if (!string.IsNullOrEmpty(gf.Url) && gf.Url.StartsWith("http"))
                {
                    try {
                        LiveChatWebView.Source = new Uri(gf.Url);
                    } catch (Exception ex) {
                        Logger.Log($"[Library] WebView2 navigation failed: {ex.Message}");
                    }
                    try {
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

        private async void ManualLinkBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameForLink == null) return;
            string query = _activeGameForLink.Title.Replace("SteamRIP.com", "").Replace("-", " ").Trim();
            LinkSearchBox.Text = query;
            LinkDialogSubtitle.Text = $"Searching for \"{_activeGameForLink.Title}\"...";
            LinkResults.Clear();
            LinkConfirmBtn.IsEnabled = false;
            LinkSearchOverlay.Visibility = Visibility.Visible;
            LinkSearchDialog.Visibility = Visibility.Visible;

            try {
                LinkLoadingRing.IsActive = true;
                var results = await SteamRipScraper.SearchAsync(query);
                foreach (var res in results) LinkResults.Add(res);
                LinkDialogSubtitle.Text = results.Count > 0
                    ? $"Found {results.Count} result(s). Select the correct one below."
                    : "No results found. Try a different search term.";
            } catch (Exception ex) {
                Logger.LogError("LinkAutoSearch", ex);
                LinkDialogSubtitle.Text = "Search failed. Try again.";
            } finally {
                LinkLoadingRing.IsActive = false;
            }
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
                string normPath = _activeGameForLink.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                GlobalSettings.GamePageLinks[normPath] = selected.Url;
                var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == _activeGameForLink.RootPath);
                if (meta != null) meta.Url = selected.Url;

                _ = Task.Run(async () => {
                    try {
                        var details = await SteamRipScraper.GetGameDetailsAsync(selected.Url);
                        _activeGameForLink.LatestVersion = details.LatestVersion;
                    } catch { }
                });
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
            string? rootPath = null;
            if (sender is FrameworkElement fe && fe.Tag is string path) rootPath = path;
            if (rootPath == null) return;

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

                    var steamResult = await App.ShowDialogSafeAsync(steamDialog);
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

            var result = await App.ShowDialogSafeAsync(dialog);
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

                try
                {
                    string oldJpg = Path.Combine(gf.RootPath, "folder.jpg");
                    string oldPng = Path.Combine(gf.RootPath, "folder.png");
                    if (File.Exists(oldJpg)) File.Delete(oldJpg);
                    if (File.Exists(oldPng)) File.Delete(oldPng);
                }
                catch (Exception ex) { Logger.Log($"[Library] Could not delete old cover: {ex.Message}"); }

                await ScannerEngine.DownloadGameImageAsync(selected.ImageUrl, gf.RootPath, overwrite: true);

                var meta = GlobalSettings.Library.FirstOrDefault(m =>
                    m.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(gf.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                if (meta != null) meta.ImageUrl = selected.ImageUrl;
                GlobalSettings.Save();

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
                window?.OpenConfig(gf.RootPath, gf.Title, gf.ExecutablePath);
            }
        }

        private async void Launch_Click(object sender, RoutedEventArgs e)
        {
            string? path = null;
            if (sender is FrameworkElement fe && fe.Tag is string t) path = t;
            if (path == null) return;

            var gf = FoundGames.FirstOrDefault(g => g.RootPath == path);
            if (gf == null) return;

            if (gf.IsRepairRequired)
            {
                Repair_Click(sender, e);
                return;
            }

            if (gf.IsRepairable && !gf.IsRunning && !gf.IsMoving)
            {
                StatusLabel.Text = $"Verifying {gf.Title}...";
                var report = await RepairService.AnalyzeGameAsync(gf.RootPath, gf.GameSubFolderPath ?? gf.RootPath, null, null, false, true);
                Logger.Log($"[Launch-Check] {gf.Title}: {report.MissingFiles.Count} missing, {report.CorruptedFiles.Count} corrupted, {report.AddedFiles.Count} mods.");

                if (report.MissingFiles.Count > 0)
                    Logger.Log($"[Launch-Check] Missing files for {gf.Title}: {string.Join(", ", report.MissingFiles.Take(10))}{(report.MissingFiles.Count > 10 ? "..." : "")}");
                if (report.CorruptedFiles.Count > 0)
                    Logger.Log($"[Launch-Check] Corrupted files for {gf.Title}: {string.Join(", ", report.CorruptedFiles.Take(10))}{(report.CorruptedFiles.Count > 10 ? "..." : "")}");

                StatusLabel.Text = "";

                if (report.HasIntegrityIssues)
                {
                    Logger.Log($"[Launch-Check] {gf.Title} BLOCKED: Integrity issues found.");
                    await App.ShowDialogSafeAsync(new ContentDialog {
                        Title = "Integrity Issue",
                        Content = "Issues found with game files. Please perform a repair to ensure the game runs correctly.",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    });
                    gf.IsRepairRequired = true;
                    return;
                }

                var trustedFiles = RepairService.LoadModsManifest(gf.RootPath);
                var newMods = report.AddedFiles.Where(f => !trustedFiles.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
                if (newMods.Count > 0)
                {
                    _ = DispatcherQueue.TryEnqueue(async () => {
                        var dialog = new ContentDialog {
                            Title = "New Files Detected",
                            Content = $"New files/mods were detected in '{gf.Title}'. Do you want to trust them?",
                            PrimaryButtonText = "Trust",
                            SecondaryButtonText = "Ignore",
                            XamlRoot = this.XamlRoot
                        };
                        if (await App.ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary) {
                            RepairService.UpdateModsManifest(gf.RootPath, trustedFiles.Concat(newMods).ToList());
                        }
                    });
                }
            }

            if (gf.HasMissingRedists)
            {
                InstallRedist_Click(sender, e);
                return;
            }

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
                    Logger.Log($"No manual executable set for '{path}'. Attempting auto-detection...");
                    var candidates = ScannerEngine.GetExecutableCandidates(path, gf.Title);

                    if (candidates.Count > 1)
                    {

                        var combo = new ComboBox
                        {
                            Header = "Possible executables found:",
                            ItemsSource = candidates.Select(c => Path.GetFileName(c)).ToList(),
                            SelectedIndex = 0,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Margin = new Thickness(0, 10, 0, 0)
                        };

                        var dialog = new ContentDialog
                        {
                            Title = "Select Executable",
                            Content = new StackPanel
                            {
                                Spacing = 8,
                                Children = {
                                    new TextBlock { Text = $"Multiple possible executables were found for '{gf.Title}'. Select one to launch (you can change this later in the game configuration menu).", TextWrapping = TextWrapping.Wrap },
                                    combo
                                }
                            },
                            PrimaryButtonText = "Launch",
                            CloseButtonText = "Cancel",
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = this.XamlRoot
                        };

                        if (await App.ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary && combo.SelectedIndex != -1)
                        {
                            exe = candidates[combo.SelectedIndex];
                            config.ManualExePath = exe;
                            GlobalSettings.Save();
                        }
                    }
                    else if (candidates.Count == 1)
                    {
                        exe = candidates[0];
                    }
                }

                if (string.IsNullOrEmpty(exe))
                {
                    Logger.Log($"Launch failed: No executable detected automatically in '{path}'");

                    var pickDialog = new ContentDialog
                    {
                        Title = "Select Executable",
                        Content = $"No executable was found automatically for '{gf.Title}'. Please select the game's executable manually.",
                        PrimaryButtonText = "Browse...",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.XamlRoot
                    };

                    if (await App.ShowDialogSafeAsync(pickDialog) == ContentDialogResult.Primary)
                    {
                        var picked = await PickerService.PickFileAsync(path);
                        if (!string.IsNullOrEmpty(picked))
                        {
                            exe = picked;
                            config.ManualExePath = picked;
                            GlobalSettings.Save();
                            Logger.Log($"User manually selected executable for '{gf.Title}': {picked}");
                        }
                    }
                }

                if (string.IsNullOrEmpty(exe)) return;

                try {
                    Logger.Log($"Direct launch initiated: {exe}");
                    var startInfo = new ProcessStartInfo {
                        FileName = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe),
                        Arguments = config.LaunchArguments,
                        UseShellExecute = true
                    };

                    if (config.RunAsAdmin) {
                        Logger.Log("Elevation requested.");
                        startInfo.Verb = "runas";
                    }

                    var proc = Process.Start(startInfo);
                    if (proc != null) Logger.Log($"Process started (PID: {proc.Id})");
                    if (GlobalSettings.CloseAppOnLaunch) Application.Current.Exit();
                }
                catch (Exception ex)
                {
                    Logger.LogError("Launch", ex);
                    Logger.Log($"Launch error: {ex.Message}");
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

            var result = await App.ShowDialogSafeAsync(dialog);
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
            if (sender is not FrameworkElement fe || fe.Tag is not string currentPath) return;

            if (GlobalSettings.CurrentMove != null)
            {
                var busyDialog = new ContentDialog {
                    Title = "Move in Progress",
                    Content = $"Another move operation is currently active for \"{GlobalSettings.CurrentMove.GameTitle}\". Please wait for it to finish.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await App.ShowDialogSafeAsync(busyDialog);
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
                await App.ShowDialogSafeAsync(noTargetsDialog);
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

            if (await App.ShowDialogSafeAsync(dialog) != ContentDialogResult.Primary || combo.SelectedItem == null) return;

            string targetRoot = combo.SelectedItem.ToString()!;
            string targetPath = Path.Combine(targetRoot, Path.GetFileName(currentPath));

            if (Directory.Exists(targetPath)) {
                var errorDialog = new ContentDialog { Title = "Move Failed", Content = $"Destination folder already exists.", CloseButtonText = "OK", XamlRoot = this.XamlRoot };
                await App.ShowDialogSafeAsync(errorDialog);
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

            var result = await App.ShowDialogSafeAsync(dialog);
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
                Logger.Log($"[Library] Force stopped process for {gf.Title}");
            } catch (Exception ex) {
                Logger.LogError("StopGame", ex);
            }
        }
        private void OpenDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string path)
            {
                try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
                catch (Exception ex) { Logger.LogError("OpenDirectory", ex); }
            }
        }

        private async void CreateShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string path)
            {
                var gf = FoundGames.FirstOrDefault(g => g.RootPath == path);
                if (gf == null || string.IsNullOrEmpty(gf.ExecutablePath)) return;

                try
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string shortcutPath = Path.Combine(desktop, $"{gf.Title}.url");

                    using (StreamWriter writer = new StreamWriter(shortcutPath))
                    {
                        writer.WriteLine("[InternetShortcut]");
                        writer.WriteLine("URL=file:///" + gf.ExecutablePath.Replace('\\', '/'));
                        writer.WriteLine("IconIndex=0");
                        writer.WriteLine("IconFile=" + gf.ExecutablePath.Replace('\\', '/'));
                    }
                    StatusLabel.Text = "Shortcut created on desktop.";
                }
                catch (Exception ex) { Logger.LogError("CreateShortcut", ex); }
            }
        }

        private async void AddToSteam_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string path)
            {
                var gf = FoundGames.FirstOrDefault(g => g.RootPath == path);
                if (gf == null || string.IsNullOrEmpty(gf.ExecutablePath)) return;

                try
                {
                    bool success = await SteamManager.AddNonSteamGame(gf.Title, gf.ExecutablePath);
                    if (success)
                    {
                        var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == path);
                        if (meta != null) { meta.IsSteamIntegrated = true; GlobalSettings.Save(); }
                        gf.IsSteamIntegrated = true;
                        StatusLabel.Text = $"Added \"{gf.Title}\" to Steam.";
                        GlobalSettings.IsSteamUpdateRequired = true;
                    }
                    else
                    {
                        StatusLabel.Text = "Failed to add to Steam. Is Steam running?";
                    }
                }
                catch (Exception ex) { Logger.LogError("AddToSteam", ex); }
            }
        }

        private async void Repair_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string path) return;

            var gf = FoundGames.FirstOrDefault(g => g.RootPath == path);
            if (gf == null) return;

            var startConfirm = new ContentDialog
            {
                Title = "🛠 Repair Game - Advanced Verification",
                Content = "Analyze files and compare with SteamRIP original hashes? This may take several minutes.",
                PrimaryButtonText = "Analyze & Repair",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            if (await App.ShowDialogSafeAsync(startConfirm) != ContentDialogResult.Primary) return;

            var versionStatus = RepairService.CheckVersionFile(path);
            if (versionStatus == RepairService.VersionStatus.NotDownloadedWithApp)
            {
                var externalConfirm = new ContentDialog
                {
                    Title = "External Game Detected",
                    Content = "This game wasn't downloaded with the app. To enable repairs, we can scan the remote archive to build a repair map. This uses minimal data.\n\nWould you like to scan and enable repair for this game?",
                    PrimaryButtonText = "Scan Remote Archive",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.XamlRoot
                };

                if (await App.ShowDialogSafeAsync(externalConfirm) == ContentDialogResult.Primary)
                {
                    try {
                        string? scanUrl = gf.Url;
                        if (string.IsNullOrEmpty(scanUrl))
                        {
                            var results = await SteamRipScraper.SearchAsync(gf.Title);
                            if (results.Count > 0)
                            {
                                var hosts = await SteamRipScraper.GetDownloadHostsAsync(results[0].Url);
                                var bestHost = hosts.FirstOrDefault();
                                if (bestHost != null)
                                {
                                    if (bestHost.Name == "Buzzheavier") scanUrl = await SteamRipScraper.ExtractBuzzheavierDirectUrlAsync(bestHost.Link);
                                    else if (bestHost.Name == "GoFile") {
                                        var directLinks = await GoFileClient.GetDirectLinksAsync(bestHost.Link);
                                        scanUrl = directLinks?.FirstOrDefault();
                                    }
                                    else scanUrl = bestHost.Link;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(scanUrl)) throw new Exception("Could not resolve a download link for this game automatically.");

                        StatusLabel.Text = "⏳ Scanning remote archive (Leap Scan)...";
                        var map = await RepairService.ScanRemoteArchiveAsync(scanUrl);
                        if (map != null)
                        {
                            string mapPath = Path.Combine(path, RepairService.MapFileName);
                            File.WriteAllText(mapPath, JsonSerializer.Serialize(map, _jsonOptions));

                            StatusLabel.Text = "✅ Repair map generated! Running initial hash...";
                            await RepairService.RunInitialHashAsync(path, path);
                            StatusLabel.Text = "✅ Repair map and integrity map generated.";
                        }
                        else throw new Exception("Remote scan failed.");
                    } catch (Exception ex) {
                        _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "Scan Failed", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.XamlRoot });
                        return;
                    }
                }
                else return;
            }
            if (versionStatus == RepairService.VersionStatus.Incompatible)
            {
                _ = App.ShowDialogSafeAsync(new ContentDialog
                {
                    Title = "Incompatible Repair Data",
                    Content = "This game was downloaded with a much older or different version of this app's repair logic. The current repair system cannot safely verify these files.\n\nYou may need to re-download the game to enable modern repair features.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
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
                    XamlRoot = this.XamlRoot
                };
                if (await App.ShowDialogSafeAsync(upgradeConfirm) == ContentDialogResult.Primary)
                {
                    RepairService.TriggerManualBackup(path, path);
                    StatusLabel.Text = "⏳ Updating metadata in background...";
                }
                return;
            }

            StatusLabel.Text = $"Analyzing \"{gf.Title}\"...";

            RepairReport report;
            try
            {
                report = await RepairService.AnalyzeGameAsync(path, path, null, (status, pct) =>
                {
                    DispatcherQueue.TryEnqueue(() => StatusLabel.Text = status);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("LibraryRepair.Analyze", ex);
                StatusLabel.Text = "Analysis failed.";
                return;
            }

            if (report.MetadataMissing)
            {

                StatusLabel.Text = "⏳ Generating integrity metadata... (Hashing files)";
                await RepairService.RunInitialHashAsync(path, path);

                StatusLabel.Text = "🔍 Re-analyzing after hashing...";
                report = await RepairService.AnalyzeGameAsync(path, path, null, (status, pct) => {
                    DispatcherQueue.TryEnqueue(() => StatusLabel.Text = status);
                });

                if (report.MetadataMissing)
                {
                    StatusLabel.Text = "Analysis failed: Could not generate metadata.";
                    return;
                }
            }

            if (report.Error != null)
            {
                StatusLabel.Text = report.Error;
                _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "Cannot Repair", Content = report.Error, CloseButtonText = "OK", XamlRoot = this.XamlRoot });
                return;
            }

            if (!report.HasIntegrityIssues)
            {
                if (report.AddedFiles.Count > 0)
                {
                    StatusLabel.Text = "✅ Integrity Perfect. New files detected.";
                    await CheckForNewFilesAsync(gf);
                    return;
                }

                StatusLabel.Text = "✅ No issues found. Game is intact.";
                _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "✅ Integrity Perfect", Content = $"All files in \"{gf.Title}\" are present and match their original hashes.", CloseButtonText = "Great!", XamlRoot = this.XamlRoot });
                return;
            }

            if (gf.HasVersionUpdate)
            {
                _ = App.ShowDialogSafeAsync(new ContentDialog {
                    Title = "Update Required",
                    Content = "A game update is available. You must update the game before performing a repair, as the remote repair data for this version may no longer be available.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                });
                return;
            }

            string issues = "";
            if (report.MissingFiles.Count > 0) issues += $"❌ Missing: {report.MissingFiles.Count}\n";
            if (report.CorruptedFiles.Count > 0) issues += $"⚠️ Corrupted: {report.CorruptedFiles.Count}\n";

            var confirm = new ContentDialog
            {
                Title = "🛠 Repair Game",
                Content = $"{issues}\nOnly affected files will be re-downloaded. User files (mods/saves) are untouched.\n\nProceed?",
                PrimaryButtonText = "Repair",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            if (await App.ShowDialogSafeAsync(confirm) != ContentDialogResult.Primary) return;

            var meta = GlobalSettings.Library.FirstOrDefault(m =>
                m.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

            string url = "";
            string normPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var matchedKey = GlobalSettings.GamePageLinks.Keys.FirstOrDefault(k =>
                k.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(normPath, StringComparison.OrdinalIgnoreCase));
            if (matchedKey != null) url = GlobalSettings.GamePageLinks[matchedKey];
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(meta?.Url)) url = meta!.Url;
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(gf.Url)) url = gf.Url;

            if (string.IsNullOrEmpty(url))
            {
                _ = App.ShowDialogSafeAsync(new ContentDialog
                {
                    Title = "No Source URL",
                    Content = "No SteamRIP page URL was found for this game. Please link the game's SteamRIP page via Properties → Manual Link first.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                });
                return;
            }

            StatusLabel.Text = "Repairing...";
            try
            {
                var window = (Application.Current as App)?.m_window as MainWindow;
                string directUrl = await window!.ResolveRepairUrlAsync(url, gf.Title);
                if (string.IsNullOrEmpty(directUrl)) { StatusLabel.Text = "Could not resolve download URL."; return; }

                gf.IsInProgress = true;
                gf.ProgressPhase = "Repairing...";

                await RepairService.PerformIntegrityRepairAsync(path, path, report, directUrl,
                    (status, pct) => DispatcherQueue.TryEnqueue(() => {
                        StatusLabel.Text = status;
                        _persistedStatusText = status;

                    }));

                gf.IsInProgress = false;
                gf.ProgressPhase = "";
                gf.IsRepairRequired = false;

                StatusLabel.Text = $"\"{gf.Title}\" repaired successfully.";

                gf.IsRepairRequired = false;
                gf.IsInProgress = false;
                gf.ProgressPhase = "";
                gf.ProgressPercentage = 0;
                gf.ProgressDetails = "";

                _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "✅ Repair Complete", Content = $"\"{gf.Title}\" has been restored.", CloseButtonText = "OK", XamlRoot = this.XamlRoot });
            }
            catch (Exception ex)
            {
                gf.IsInProgress = false;
                Logger.LogError("LibraryRepair.Perform", ex);
                StatusLabel.Text = "Repair failed.";
                _ = App.ShowDialogSafeAsync(new ContentDialog { Title = "Repair Failed", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.XamlRoot });
            }
        }
        private async Task CheckForNewFilesAsync(GameFolder game)
        {
            try
            {

                string storagePath = game.RootPath;
                string contentPath = game.GameSubFolderPath ?? game.RootPath;
                string skelPath = Path.Combine(storagePath, RepairService.SkeletonFileName);

                string mapPath = Path.Combine(storagePath, RepairService.MapFileName);
                if (!File.Exists(skelPath) || !File.Exists(mapPath)) return;

                var trustedFiles = RepairService.LoadModsManifest(storagePath);

                var report = await RepairService.AnalyzeGameAsync(storagePath, contentPath, null, null, true);

                DispatcherQueue.TryEnqueue(() => {
                    game.IsRepairRequired = report.MissingFiles.Count > 0 || report.CorruptedFiles.Count > 0;
                });

                var newFiles = report.AddedFiles.Where(f => !trustedFiles.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

                if (newFiles.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "New Files Detected",
                            Content = new StackPanel { Spacing = 8, Children = {
                                new TextBlock { Text = $"New files were detected in '{game.Title}'. These may be mods, patches, or game saves.", TextWrapping = TextWrapping.Wrap },
                                new TextBlock { Text = "Was this done by you?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                                new ItemsControl { ItemsSource = newFiles.Take(5), Margin = new Thickness(12, 0, 0, 0) },
                                new TextBlock { Text = newFiles.Count > 5 ? $"...and {newFiles.Count - 5} more." : "", FontStyle = Windows.UI.Text.FontStyle.Italic }
                            }},
                            PrimaryButtonText = "Yes, I trust them",
                            SecondaryButtonText = "No, quarantine them",
                            CloseButtonText = "Ignore for now",
                            XamlRoot = this.XamlRoot
                        };

                        var result = await App.ShowDialogSafeAsync(dialog);
                        if (result == ContentDialogResult.Primary)
                        {

                            RepairService.UpdateModsManifest(storagePath, trustedFiles.Concat(newFiles).ToList());
                            Logger.Log($"[Library] User trusted {newFiles.Count} new files in {game.Title}");
                        }
                        else if (result == ContentDialogResult.Secondary)
                        {

                            var qDialog = new ContentDialog
                            {
                                Title = "Confirm Quarantine",
                                Content = "This will rename the files to include a timestamp and move them to a _Quarantine folder. Some files might be game saves—are you sure?",
                                PrimaryButtonText = "Quarantine",
                                CloseButtonText = "Cancel",
                                XamlRoot = this.XamlRoot
                            };

                            if (await App.ShowDialogSafeAsync(qDialog) == ContentDialogResult.Primary)
                            {
                                RepairService.QuarantineFiles(storagePath, contentPath, newFiles);
                                Logger.Log($"[Library] Quarantined {newFiles.Count} files in {game.Title}");
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"CheckNewFiles_{game.Title}", ex);
            }
        }
        private async void UpdateGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string path) return;

            var game = FoundGames.FirstOrDefault(g => g.RootPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (game == null) return;

            try {
                game.IsInProgress = true;
                game.ProgressPhase = "Updating...";
                game.ProgressPercentage = 0;
                game.ProgressDetails = "Fetching download link...";

                var hosts = await SteamRipScraper.GetDownloadHostsAsync(game.Url);
                var sortedHosts = hosts.OrderByDescending(h => h.Name == "Buzzheavier" ? 2 : (h.Name == "GoFile" ? 1 : 0)).ToList();
                bool updateStarted = false;
                bool overallSuccess = false;

                foreach (var host in sortedHosts)
                {
                    try {
                        string? newUrl = null;
                        if (host.Name == "Buzzheavier") {
                            game.ProgressDetails = "Resolving Buzzheavier link...";
                            newUrl = await SteamRipScraper.ExtractBuzzheavierDirectUrlAsync(host.Link);
                        }
                        else if (host.Name == "GoFile") {
                            game.ProgressDetails = "Resolving GoFile link...";
                            var directLinks = await GoFileClient.GetDirectLinksAsync(host.Link);
                            newUrl = directLinks?.FirstOrDefault();
                        }
                        else {
                            newUrl = host.Link;
                        }

                        if (string.IsNullOrEmpty(newUrl)) continue;

                        updateStarted = true;
                        bool success = await RepairService.PerformSmartUpdateAsync(path, newUrl, (status, pct) => {
                            DispatcherQueue.TryEnqueue(() => {
                                game.ProgressPhase = "Updating...";
                                game.ProgressPercentage = pct;
                                game.ProgressDetails = status;
                            });
                        }, game.LatestVersion);

                        if (success) {
                            Logger.Log($"[Update] Successfully updated {game.Title} using {host.Name}");
                            overallSuccess = true;
                            break;
                        } else {
                            Logger.Log($"[Update] Failed to update {game.Title} using {host.Name}, trying next host...");
                        }
                    } catch (Exception ex) {
                        Logger.Log($"[Update] Host {host.Name} failed: {ex.Message}");
                    }
                }

                if (overallSuccess)
                {
                    game.Version = game.LatestVersion;

                    var metadata = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(game.RootPath, StringComparison.OrdinalIgnoreCase));
                    if (metadata != null)
                    {
                        metadata.Version = game.Version;
                    }

                    GlobalSettings.Save();

                    await App.ShowDialogSafeAsync(new ContentDialog {
                        Title = "Update Complete",
                        Content = $"{game.Title} has been updated to {game.Version}.",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    });
                }
                else
                {
                    if (!updateStarted)
                        throw new Exception("No working download links could be resolved.");
                    else
                        throw new Exception("Update failed across all available hosts. Please try again later.");
                }
            } catch (Exception ex) {
                Logger.LogError("UpdateClick", ex);
                _ = App.ShowDialogSafeAsync(new ContentDialog {
                    Title = "Update Failed",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                });
            } finally {
                game.IsInProgress = false;
                await RefreshLibrary();
            }
        }
    }
}