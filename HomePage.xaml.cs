using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using SteamRipApp.Core;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamRipApp
{
    public class InfoItem { public string Key { get; set; } = ""; public string Value { get; set; } = ""; }
    public class ReqItem {
        public string Icon { get; set; } = "";
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public int RankDiff { get; set; } = 0;
        public bool HasRankDiff { get => RankDiff != 0; }
        public string RankText { get => RankDiff > 0 ? $"+{RankDiff} ranks above req" : $"{RankDiff} ranks below req"; }
    }

    public sealed partial class HomePage : Page
    {
        public ObservableCollection<GameGroup> Groups { get; } = new ObservableCollection<GameGroup>();
        private TaskCompletionSource<string>? _downloadUrlTcs;
        private bool _interceptorReady = false;

        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            ResultsGrid.ItemsSource = Groups;
            if (Groups.Count == 0)
            {
                _ = LoadGamesListFromUrlAsync("https://steamrip.com/");
            }

        }

        private async void SearchBtn_Click(object sender, RoutedEventArgs e)
        {
            await RunSearch(SearchBox.Text);
        }

        private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            await RunSearch(args.QueryText);
        }

        private string _lastSearchQuery = "";
        private string _lastUrl = "";
        private bool _isSearch = false;
        private int _currentRequestId = 0;

        private async Task RunSearch(string query, int page = 1)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            int reqId = ++_currentRequestId;
            try {
                _isSearch = true;
                _lastSearchQuery = query;
                LoadingRing.IsActive = true;

                var searchResultPage = await SteamRipScraper.SearchPageAsync(query, page);
                if (reqId != _currentRequestId) return;

                Groups.Clear();
                await ProcessSearchResultsAsync(searchResultPage);
            } catch (Exception ex) {
                Logger.LogError("HomeUI", ex);
            } finally {
                if (reqId == _currentRequestId) LoadingRing.IsActive = false;
            }
        }

        private async void NavHome_Click(object sender, RoutedEventArgs e) => await LoadGamesListFromUrlAsync("https://steamrip.com/");

        private async void NavCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string category)
                await LoadGamesListFromUrlAsync($"https://steamrip.com/category/{category}/");
        }

        private async void NavTopGames_Click(object sender, RoutedEventArgs e) => await LoadGamesListFromUrlAsync("https://steamrip.com/top-games/");

        private void NavFaq_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag)
            {
                var url = tag == "how-to-run-games" ? "https://steamrip.com/how-to-run-games/" : "https://steamrip.com/faq/";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
        }

        private void NavDiscord_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.com/invite/steamrip") { UseShellExecute = true });
        }

        private async Task LoadGamesListFromUrlAsync(string url, int page = 1)
        {
            int reqId = ++_currentRequestId;
            try {
                _isSearch = false;
                _lastUrl = url;
                LoadingRing.IsActive = true;
                SearchBox.Text = "";

                string urlToFetch = url;
                if (page > 1) {
                    urlToFetch = url.TrimEnd('/') + $"/page/{page}/";
                }

                var searchResultPage = await SteamRipScraper.GetGamesPageAsync(urlToFetch);
                if (reqId != _currentRequestId) return;

                Groups.Clear();
                await ProcessSearchResultsAsync(searchResultPage);
            } catch (Exception ex) {
                Logger.LogError("HomeLoadList", ex);
            } finally {
                if (reqId == _currentRequestId) LoadingRing.IsActive = false;
            }
        }

        private void UpdatePaginationUI(int current, int total)
        {
            PaginationPanel.Children.Clear();
            if (total <= 1) return;

            var pages = new System.Collections.Generic.List<int?>();
            pages.Add(1);

            if (total <= 7)
            {
                for (int i = 2; i <= total; i++) pages.Add(i);
            }
            else
            {
                int start = Math.Max(2, current - 2);
                int end = Math.Min(total - 1, current + 2);

                if (start == 2) end = Math.Min(total - 1, 6);
                if (end == total - 1) start = Math.Max(2, total - 5);

                if (start > 2) pages.Add(null);

                for (int i = start; i <= end; i++)
                {
                    pages.Add(i);
                }

                if (end < total - 1) pages.Add(null);
                pages.Add(total);
            }

            foreach (var p in pages)
            {
                if (p == null)
                {
                    var text = new TextBlock { Text = "...", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4,0,4,0) };
                    PaginationPanel.Children.Add(text);
                }
                else
                {
                    var btn = new Button { Content = p.ToString(), Margin = new Thickness(2,0,2,0) };
                    if (p == current)
                    {
                        btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                        btn.IsEnabled = false;
                    }
                    int targetPage = p.Value;
                    btn.Click += async (s, e) => {
                        if (_isSearch) await RunSearch(_lastSearchQuery, targetPage);
                        else await LoadGamesListFromUrlAsync(_lastUrl, targetPage);
                    };
                    PaginationPanel.Children.Add(btn);
                }
            }
        }

        private async Task ProcessSearchResultsAsync(SearchResultPage searchResultPage)
        {
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleanGroups = new System.Collections.Generic.List<GameGroup>();

            foreach (var g in searchResultPage.Groups)
            {
                var cleanGroup = new GameGroup { Title = g.Title };
                foreach (var game in g.Games)
                {
                    if (seenUrls.Add(game.Url))
                    {
                        game.CheckInstalledState();
                        cleanGroup.Games.Add(game);
                    }
                }
                if (cleanGroup.Games.Count > 0)
                {
                    cleanGroups.Add(cleanGroup);
                }
            }

            foreach (var cg in cleanGroups)
            {
                Groups.Add(cg);
            }

            UpdatePaginationUI(searchResultPage.CurrentPage, searchResultPage.TotalPages);
        }

        private async void QuickDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var item = btn.DataContext as SearchResult;
            if (item == null || item.IsDownloading) return;

            item.CheckInstalledState();
            if (!string.IsNullOrEmpty(item.InstalledPath) && Directory.Exists(item.InstalledPath))
            {
                var repairDialog = new ContentDialog
                {
                    Title = "Game Already Installed",
                    Content = "This game is already installed on your PC.\n\nInstead of re-downloading the entire game from scratch, you can use the Repair option in your Library to verify and fix any corrupted or missing files much faster.\n\nWould you like to continue re-installing anyway? (This will delete your existing game folder and re-download it).",
                    PrimaryButtonText = "Continue Anyway",
                    SecondaryButtonText = "Cancel (Use Repair)",
                    DefaultButton = ContentDialogButton.Secondary,
                    XamlRoot = this.XamlRoot
                };

                var result = await App.ShowDialogSafeAsync(repairDialog);
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }

                try {
                    Logger.Log($"[QuickDownload] Deleting existing game folder for re-install: {item.InstalledPath}");
                    RepairService.StopHashingForGame(item.InstalledPath);
                    var pathToDelete = item.InstalledPath;
                    await Task.Run(() => {
                        if (Directory.Exists(pathToDelete))
                            Directory.Delete(pathToDelete, recursive: true);
                    });
                    var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(pathToDelete, StringComparison.OrdinalIgnoreCase));
                    if (meta != null) GlobalSettings.Library.Remove(meta);
                    GlobalSettings.GamePageLinks.Remove(pathToDelete);
                    GlobalSettings.Save();
                } catch (Exception ex) {
                    Logger.LogError("ReInstallDelete", ex);
                }
            }

            GlobalTask? task = null;
            try {
                item.IsDownloading = true;

                var mainWindow = (Application.Current as App)?.m_window as MainWindow;
                task = mainWindow?.ShowGlobalOverlay($"Quick Download: {item.Title}", "Scanning for available sources...");

                await Task.Delay(500);

                var bzTask = SteamRipScraper.CheckBuzzheavierAsync(item.Url);
                var gfTask = SteamRipScraper.CheckGoFileAsync(item.Url);
                await Task.WhenAll(bzTask, gfTask);

                var (bzFound, bzUrl) = await bzTask;
                var (gfFound, gfPageUrl, gfDirectLinks) = await gfTask;

                item.BuzzheavierUrl = bzUrl;
                item.IsBuzzheavierAvailable = bzFound;
                item.GoFileUrl = gfPageUrl;
                item.GoFileDirectLinks = gfDirectLinks;
                item.IsGoFileAvailable = gfFound;

                if (!bzFound && !gfFound)
                {
                    item.IsDownloading = false;
                    if (task != null) mainWindow?.HideGlobalOverlay(task);
                    var dialog = new ContentDialog {
                        Title = "Sources Not Found",
                        Content = "Sorry this game doesn't have our sources, try a manual installation. You can still add it to our launcher after its completed",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await App.ShowDialogSafeAsync(dialog);
                    return;
                }

                if (bzFound && gfFound)
                {
                    if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Select your preferred source...");
                    var selectDialog = new ContentDialog {
                        Title = "Select Download Source",
                        Content = "Both Gofile and SteamRip (Buzzheavier) are available. Which would you prefer?",
                        PrimaryButtonText = "SteamRip (Buzzheavier)",
                        SecondaryButtonText = "Gofile (Fastest)",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.XamlRoot
                    };

                    var result = await App.ShowDialogSafeAsync(selectDialog);
                    if (result == ContentDialogResult.Primary) {
                        await StartBuzzheavierDownload(item, task);
                    }
                    else if (result == ContentDialogResult.Secondary) {
                        await StartGoFileDownload(item, task);
                    }
                    else {
                        item.IsDownloading = false;
                        if (task != null) mainWindow?.HideGlobalOverlay(task);
                        return;
                    }
                }
                else if (gfFound)
                {
                    if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Using Gofile...");
                    await StartGoFileDownload(item, task);
                }
                else if (bzFound)
                {
                    if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Using Buzzheavier...");
                    await StartBuzzheavierDownload(item, task);
                }

                if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Success!", 100);
                await Task.Delay(1500);
                if (task != null) mainWindow?.HideGlobalOverlay(task);
            } catch (Exception ex) {
                Logger.LogError("QuickDownload", ex);
                var mainWindow = (Application.Current as App)?.m_window as MainWindow;
                if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Error occurred. Please try again.");
                await Task.Delay(2000);
                if (task != null) mainWindow?.HideGlobalOverlay(task);
            } finally {
                item.IsDownloading = false;
            }
        }

        private async void FlyoutBuzz_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem mfi && mfi.DataContext is SearchResult item)
                await StartBuzzheavierDownload(item);
        }

        private async void FlyoutGoFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem mfi && mfi.DataContext is SearchResult item)
                await StartGoFileDownload(item);
        }

        private async Task<string?> PickOrGetSaveFolderAsync()
        {

            if (GlobalSettings.HasSelectedDownloadDirectory && !string.IsNullOrEmpty(GlobalSettings.DownloadDirectory)
                && Directory.Exists(GlobalSettings.DownloadDirectory))
            {
                return GlobalSettings.DownloadDirectory;
            }

            var path = await PickerService.PickFolderAsync();
            if (string.IsNullOrEmpty(path)) return null;

            GlobalSettings.DownloadDirectory = path;
            GlobalSettings.HasSelectedDownloadDirectory = true;

            if (!GlobalSettings.ScanDirectories.Contains(path))
                GlobalSettings.ScanDirectories.Add(path);

            GlobalSettings.Save();
            Logger.Log($"[Download] Default download dir set to: {path}");
            return path;
        }

        private async Task StartBuzzheavierDownload(SearchResult item, GlobalTask? task = null)
        {
            if (string.IsNullOrEmpty(item.BuzzheavierUrl)) return;
            var bzzhrUrl = item.BuzzheavierUrl;

            try {
                Logger.Log($"[QuickDownload-Buzz] Starting for: {item.Title}");

                var savePath = await PickOrGetSaveFolderAsync();
                if (savePath == null) { Logger.Log("[QuickDownload-Buzz] Cancelled."); return; }

                var directUrl = await UrlResolver.ResolveDirectUrlAsync(bzzhrUrl);
                if (string.IsNullOrEmpty(directUrl))
                {
                    var mainWindow = (Application.Current as App)?.m_window as MainWindow;
                    if (task != null) mainWindow?.UpdateGlobalOverlay(task, "API failed, falling back to browser interceptor...");
                    Logger.Log("[QuickDownload-Buzz] API resolution failed. Falling back to Interceptor...");
                    directUrl = await InterceptDownloadUrlAsync(bzzhrUrl);
                }

                if (string.IsNullOrEmpty(directUrl)) {
                    Logger.Log("[QuickDownload-Buzz] No URL found.");
                    return;
                }

                var fileName = ExtractFileNameFromUrl(directUrl) ?? (MakeSafeFileName(item.Title) + ".rar");
                var destPath = Path.Combine(savePath, fileName);

                string version = "";
                try {
                    if (!string.IsNullOrEmpty(item.Url))
                        version = (await SteamRipScraper.GetGameDetailsAsync(item.Url)).LatestVersion;
                } catch { }

                await StartDownloadWithMetadata(item, destPath, directUrl, bzzhrUrl, "Buzzheavier", version: version, task: task);
            } catch (Exception ex) {
                Logger.LogError("QuickDownload-Buzz", ex);
            }
        }

        private async Task StartGoFileDownload(SearchResult item, GlobalTask? task = null)
        {
            try {
                Logger.Log($"[QuickDownload-GoFile] Starting for: {item.Title}");

                var savePath = await PickOrGetSaveFolderAsync();
                if (savePath == null) { Logger.Log("[QuickDownload-GoFile] Cancelled."); return; }

                var directLinks = item.GoFileDirectLinks;

                if (directLinks == null || directLinks.Count == 0)
                {
                    Logger.Log("[QuickDownload-GoFile] Direct links not pre-resolved, resolving via API...");
                    var resolved = await UrlResolver.ResolveDirectUrlAsync(item.GoFileUrl);
                    if (!string.IsNullOrEmpty(resolved)) directLinks = new List<string> { resolved };
                }

                var directUrl = directLinks?.FirstOrDefault();
                if (string.IsNullOrEmpty(directUrl))
                {

                    var dialog = new ContentDialog
                    {
                        Title = "Automation Failed",
                        Content = "We found the GoFile link, but could not resolve a direct download URL automatically. Would you like to open the GoFile page in your browser?",
                        PrimaryButtonText = "Open in Browser",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.XamlRoot
                    };
                    if (await App.ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.GoFileUrl) { UseShellExecute = true });
                    }
                    ResetDownloadButton(item);
                    return;
                }

                var fileName = ExtractFileNameFromUrl(directUrl) ?? (MakeSafeFileName(item.Title) + ".rar");
                var destPath = Path.Combine(savePath, fileName);

                string version = "";
                try {
                    if (!string.IsNullOrEmpty(item.Url))
                        version = (await SteamRipScraper.GetGameDetailsAsync(item.Url)).LatestVersion;
                } catch { }

                await StartDownloadWithMetadata(item, destPath, directUrl, item.GoFileUrl ?? "", "Gofile", isGoFile: true, version: version, task: task);
            } catch (Exception ex) {
                Logger.LogError("QuickDownload-GoFile", ex);
            }
        }

        private async Task StartDownloadWithMetadata(SearchResult item, string destPath, string directUrl, string pageUrl, string sourceName, bool isGoFile = false, string version = "", GlobalTask? task = null)
        {

            try {
                var (totalBytes, _) = await CustomDownloader.ProbeServerAsync(directUrl, isGoFile ? GoFileClient.AccountToken : null);
                if (totalBytes > 0)
                {
                    string? root = Path.GetPathRoot(Path.GetFullPath(destPath));
                    if (!string.IsNullOrEmpty(root))
                    {
                        var drive = new DriveInfo(root);
                        long requiredSpace = (long)(totalBytes * 2.2);
                        long freeSpace = drive.AvailableFreeSpace;

                        if (freeSpace <= 5L * 1024 * 1024 * 1024)
                        {
                            var dialog = new ContentDialog {
                                Title = "🛑 Storage Error",
                                Content = $"Drive {root} is critically low on space (under 5GB). Please free up space before downloading.",
                                CloseButtonText = "OK",
                                XamlRoot = this.XamlRoot
                            };
                            await App.ShowDialogSafeAsync(dialog);
                            ResetDownloadButton(item);
                            return;
                        }

                        if (freeSpace < requiredSpace)
                        {
                            var dialog = new ContentDialog {
                                Title = "⚠️ Low Storage Warning",
                                Content = $"This download requires ~{(requiredSpace / 1024 / 1024 / 1024.0):F1} GB to extract reliably (220% rule).\n\nYou only have {(freeSpace / 1024 / 1024 / 1024.0):F1} GB free on this drive.\n\nDo you want to continue anyway?",
                                PrimaryButtonText = "Continue Anyway",
                                SecondaryButtonText = "Cancel",
                                XamlRoot = this.XamlRoot
                            };
                            if (await App.ShowDialogSafeAsync(dialog) != ContentDialogResult.Primary)
                            {
                                ResetDownloadButton(item);
                                return;
                            }
                        }
                    }
                }
            } catch { }

            var metadata = new ActiveDownloadMetadata {
                Title = item.Title,
                SourceUrl = directUrl,
                ImageUrl = item.ImageUrl,
                DestPath = destPath,
                PageUrl = pageUrl,
                SteamRipUrl = item.Url,
                Percentage = 0,
                Status = "Starting...",
                Source = sourceName
            };
            GlobalSettings.ActiveDownloads.Add(metadata);
            GlobalSettings.Save();

            var mainWindow = (Application.Current as App)?.m_window as MainWindow;
            mainWindow?.NavigateToDownloads();

            var downloader = new CustomDownloader(directUrl, destPath);
            downloader.BuzzheavierPageUrl = pageUrl;
            downloader.SteamRipPageUrl = item.Url;
            downloader.ThreadCount = 12;
            if (isGoFile) downloader.GoFileToken = GoFileClient.AccountToken;

            _ = new DownloadSessionMetadata
            {
                GameTitle = item.Title,
                SteamRipUrl = item.Url,
                ArchivePath = destPath,
                Version = version,
                ImageUrl = item.ImageUrl,
                DownloadDir = GlobalSettings.DownloadDirectory
            }.SaveAsync();

            downloader.ProgressChanged += (s, stats) => {
                this.DispatcherQueue.TryEnqueue(() => {
                    metadata.Percentage = Math.Round(stats.Percentage, 1);
                    var etaStr = stats.ETA.TotalSeconds > 0 ? $"ETA {stats.ETA:mm\\:ss}" : "";

                    double speedVal = stats.SpeedMBps;
                    string speedUnit = "MB/s";
                    if (GlobalSettings.DownloadSpeedUnit == SpeedUnit.Bits)
                    {
                        speedVal *= 8;
                        speedUnit = "Mbps";
                    }

                    var sizeStr = stats.TotalBytes > 0
                        ? $"{stats.BytesReceived / (1024.0 * 1024):F0}/{stats.TotalBytes / (1024.0 * 1024):F0} MB"
                        : "";
                    string statusText = $"{speedVal:F1} {speedUnit}  {sizeStr}  {etaStr}  [{stats.ActiveThreads}t]";
                    metadata.Status = statusText;

                    var mw = (Application.Current as App)?.m_window as MainWindow;
                    if (task != null) mw?.UpdateGlobalOverlay(task, "Downloading Game...", stats.Percentage, statusText);
                    else mw?.UpdateGlobalOverlay("Downloading Game...", stats.Percentage, statusText);
                });
            };
            downloader.DownloadCompleted += async (s, e) =>
            {

                var mainWindow = (Application.Current as App)?.m_window as MainWindow;
                this.DispatcherQueue.TryEnqueue(() => {
                    metadata.Phase = "Extracting";
                    metadata.Status = "📦 Download complete — starting extraction...";
                    metadata.Percentage = 0;
                    if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Extracting Game...", 0, "Decompressing and verifying files...");
                    else mainWindow?.UpdateGlobalOverlay("Extracting Game...", 0, "Decompressing and verifying files...");
                });

                var archivePath = destPath;
                var extractDir  = Path.GetDirectoryName(destPath) ?? GlobalSettings.DownloadDirectory;

                metadata.Title = ScannerEngine.CleanTitle(metadata.Title);

                var gameFolder = await PostDownloadProcessor.RunAsync(
                    archivePath: archivePath,
                    extractToDir: extractDir,
                    gameTitle: item.Title,
                    steamRipPageUrl: item.Url,
                    imageUrl: item.ImageUrl,
                    version: version,
                    onStatus: msg => {
                        this.DispatcherQueue.TryEnqueue(() => {
                            metadata.Status = msg;
                            if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Extracting Game...", null, msg);
                            else mainWindow?.UpdateGlobalOverlay("Extracting Game...", null, msg);
                            if (msg.Contains("CRITICAL SPACE LIMIT"))
                            {
                                if (DateTime.Now - GlobalSettings.LastSpaceWarningTime > TimeSpan.FromHours(2))
                                {
                                    _ = HandleCriticalSpaceWarning();
                                }
                            }
                        });
                    },
                    onProgress: pct => this.DispatcherQueue.TryEnqueue(() => {
                        metadata.Percentage = Math.Round(pct, 1);
                        if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Extracting Game...", pct);
                        else mainWindow?.UpdateGlobalOverlay("Extracting Game...", pct);
                    }),
                    onFileProgress: (fileName, fileBytes, fileTotalBytes) => this.DispatcherQueue.TryEnqueue(() => {

                        string truncName = fileName.Length > 32 ? fileName[..29] + "..." : fileName;

                        string dots = ((Environment.TickCount64 / 400) % 3) switch { 0 => ".", 1 => "..", _ => "..." };
                        string fileSub = fileTotalBytes > 0
                            ? $"{fileBytes / (1024.0 * 1024):F1} / {fileTotalBytes / (1024.0 * 1024):F1} MB"
                            : "";
                        string fileStatus = string.IsNullOrEmpty(fileSub)
                            ? $"Extracting {truncName}{dots}"
                            : $"Extracting {truncName}{dots}  {fileSub}";
                        metadata.Status = fileStatus;
                        if (task != null) mainWindow?.UpdateGlobalOverlay(task, "Extracting Game...", null, fileStatus);
                        else mainWindow?.UpdateGlobalOverlay("Extracting Game...", null, fileStatus);
                    }),
                    confirmSpace: async (free, req) => {
                        var tcs = new TaskCompletionSource<bool>();
                        this.DispatcherQueue.TryEnqueue(async () => {
                            var dialog = new ContentDialog
                            {
                                Title = "⚠️ Low Storage Warning",
                                Content = $"This game requires ~{(req / 1024 / 1024 / 1024.0):F1} GB to extract reliably (220% rule).\n\nYou only have {(free / 1024 / 1024 / 1024.0):F1} GB free on this drive.\n\nDo you want to continue anyway?",
                                PrimaryButtonText = "Continue Anyway",
                                SecondaryButtonText = "Cancel Installation",
                                XamlRoot = this.XamlRoot
                            };
                            var result = await App.ShowDialogSafeAsync(dialog);
                            tcs.SetResult(result == ContentDialogResult.Primary);
                        });
                        return await tcs.Task;
                    },
                    confirmMap: async (title) => {
                        if (GlobalSettings.AlwaysCreateRarMap) return true;
                        var tcs = new TaskCompletionSource<bool>();
                        this.DispatcherQueue.TryEnqueue(async () => {
                            try {
                                var dialog = new ContentDialog {
                                    Title = "Create Repair Map?",
                                    Content = $"Would you like to generate a byte-map for {title}? This allows the 'Repair' system to fix corrupted files without re-downloading the entire game later.",
                                    PrimaryButtonText = "Create Map (Recommended)",
                                    CloseButtonText = "Skip",
                                    XamlRoot = this.XamlRoot
                                };
                                var result = await App.ShowDialogSafeAsync(dialog);
                                tcs.SetResult(result == ContentDialogResult.Primary);
                            } catch (Exception ex) {
                                Logger.LogError("MapPrompt", ex);
                                tcs.SetResult(false);
                            }
                        });
                        return await tcs.Task;
                    }
                );

                this.DispatcherQueue.TryEnqueue(() => {
                    var mw = (Application.Current as App)?.m_window as MainWindow;
                    if (gameFolder != null)
                    {
                        metadata.Phase = "Done";
                        metadata.Status = "✅ Extraction completed";
                        metadata.Percentage = 100;
                        if (task != null) mw?.UpdateGlobalOverlay(task, "Installation Successful!", 100, "Game is ready to play.");
                        else mw?.UpdateGlobalOverlay("Installation Successful!", 100, "Game is ready to play.");
                    }
                    else
                    {
                        metadata.Phase = "Failed";
                        metadata.Status = "❌ Extraction failed — verify extractor in Settings.";
                        if (task != null) mw?.UpdateGlobalOverlay(task, "Installation Failed", 0, "Check logs for details.");
                        else mw?.UpdateGlobalOverlay("Installation Failed", 0, "Check logs for details.");
                    }

                    GlobalSettings.Save();
                    mw?.RefreshLibrary();

                    _ = Task.Run(async () => {
                        await Task.Delay(3000);
                        this.DispatcherQueue.TryEnqueue(() => {
                            if (task != null) mw?.HideGlobalOverlay(task);
                            else mw?.HideGlobalOverlay();
                        });
                    });
                });
            };
            downloader.DownloadFailed += (s, msg) => {
                this.DispatcherQueue.TryEnqueue(() => metadata.Status = $"❌ {msg}");
            };

            downloader.LinkExpired += async (expiredPageUrl) => {
                if (isGoFile && !string.IsNullOrEmpty(item.GoFileUrl))
                {
                    Logger.Log("[Download] GoFile link expired — re-resolving via API...");
                    var freshLinks = await GoFileClient.GetDirectLinksAsync(item.GoFileUrl);
                    if (freshLinks.Count > 0) return freshLinks[0];

                    Logger.Log("[Download] GoFile renewal failed, falling back to Buzzheavier interceptor...");
                    if (!string.IsNullOrEmpty(item.BuzzheavierUrl))
                    {
                        var tcs = new TaskCompletionSource<string>();
                        this.DispatcherQueue.TryEnqueue(async () => {
                            try { tcs.SetResult(await InterceptDownloadUrlAsync(item.BuzzheavierUrl)); }
                            catch { tcs.SetResult(""); }
                        });
                        return await tcs.Task;
                    }
                    return "";
                }
                else
                {

                    var tcs = new TaskCompletionSource<string>();
                    this.DispatcherQueue.TryEnqueue(async () => {
                        try { tcs.SetResult(await InterceptDownloadUrlAsync(expiredPageUrl)); }
                        catch { tcs.SetResult(""); }
                    });
                    return await tcs.Task;
                }
            };

            Logger.Log($"[Download] [{(isGoFile ? "GoFile" : "Buzzheavier")}] Starting {downloader.ThreadCount}-thread download to: {destPath}");
            await downloader.StartDownloadAsync();
        }

        private async Task<string> InterceptDownloadUrlAsync(string targetUrl)
        {
            Logger.Log($"[Interceptor] Starting for: {targetUrl}");
            _downloadUrlTcs = new TaskCompletionSource<string>();

            try {
                if (!_interceptorReady)
                {
                    await DownloadInterceptor.EnsureCoreWebView2Async();
                    DownloadInterceptor.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
                    DownloadInterceptor.NavigationCompleted += Interceptor_NavigationCompleted;
                    _interceptorReady = true;
                    Logger.Log("[Interceptor] WebView2 ready.");
                }

                DownloadInterceptor.Source = new Uri(targetUrl);
                Logger.Log("[Interceptor] Navigating...");

                var completedTask = await Task.WhenAny(_downloadUrlTcs.Task, Task.Delay(45000));
                if (completedTask == _downloadUrlTcs.Task)
                    return await _downloadUrlTcs.Task;

                Logger.Log("[Interceptor] Timeout.");
                return "";
            } catch (Exception ex) {
                Logger.LogError("[Interceptor]", ex);
                return "";
            }
        }

        private async void Interceptor_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess) { Logger.Log($"[Interceptor] Nav failed: {args.WebErrorStatus}"); return; }

            var url = sender.Source?.ToString() ?? "";
            Logger.Log($"[Interceptor] Page loaded: {url}");

            if (!url.Contains("bzzhr.to") && !url.Contains("buzzheavier") && !url.Contains("gofile.io")) return;

            await Task.Delay(1500);

            var result = await sender.ExecuteScriptAsync(@"
                (function() {
                    // Buzzheavier: hx-get download anchor
                    var btn = document.querySelector('a.link-button[hx-get*=""download""]');
                    if (btn) { btn.click(); return 'clicked-buzzheavier'; }

                    // GoFile: download button
                    var gfBtn = document.querySelector('button[id*=""download""], .downloadButton, button[class*=""download""]');
                    if (gfBtn) { gfBtn.click(); return 'clicked-gofile'; }

                    // Generic fallback
                    var all = document.querySelectorAll('a, button');
                    for (var i = 0; i < all.length; i++) {
                        if (all[i].textContent.trim().toLowerCase() === 'download') {
                            all[i].click(); return 'clicked-generic';
                        }
                    }
                    return 'no-button-found';
                })();
            ");
            Logger.Log($"[Interceptor] JS: {result}");
        }

        private void CoreWebView2_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        {
            var url = args.DownloadOperation.Uri;
            Logger.Log($"[Interceptor] Intercepted: {url}");
            args.Cancel = true;
            _downloadUrlTcs?.TrySetResult(url);
        }

        private async Task EnsureWebViewReady(WebView2 wv)
        {
            try {
                if (wv.CoreWebView2 == null)
                {
                    await wv.EnsureCoreWebView2Async();
                }
            } catch (Exception ex) {
                Logger.Log($"[WebView] Initialization failed: {ex.Message}");
            }
        }

        private async void ShowNote_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is SearchResult item)
            {
                if (string.IsNullOrEmpty(item.NoteContent)) return;

                var html = $@"<html><head><style>
                    body {{ font-family: 'Segoe UI', sans-serif; font-size: 14px; color: #e0e0e0; background: #1a1a2e; padding: 16px; margin: 0; }}
                    strong {{ color: #4facfe; }}
                    ul {{ padding-left: 20px; }}
                    li {{ margin-bottom: 8px; }}
                </style></head><body>{item.NoteContent}</body></html>";

                var wv = new WebView2 { Height = 400, Width = 500 };

                var dialog = new ContentDialog {
                    Title = "How to Run: " + item.Title,
                    Content = wv,
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot
                };

                var dialogTask = App.ShowDialogSafeAsync(dialog);

                await EnsureWebViewReady(wv);
                if (wv.CoreWebView2 != null) wv.NavigateToString(html);

                await dialogTask;
            }
        }

        private async void Preview_Click(object sender, RoutedEventArgs e)
        {
            SearchResult? searchItem = null;
            string? url = null;

            if (sender is Button btn)
            {
                searchItem = btn.DataContext as SearchResult;
                url = searchItem?.Url ?? btn.Tag as string;
            }

            if (string.IsNullOrEmpty(url)) return;

            PropertiesOverlayDimmer.Visibility = Visibility.Visible;
            PropertiesDialog.Visibility = Visibility.Visible;
            RequirementsLoading.IsActive = true;

            PropsTitle.Text = searchItem?.Title ?? "Game Properties";
            try {
                if (searchItem != null && !string.IsNullOrEmpty(searchItem.ImageUrl) && Uri.TryCreate(searchItem.ImageUrl, UriKind.Absolute, out Uri? imgUri))
                {
                    PropsHeaderImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(imgUri);
                }
            } catch {  }

            GameInfoList.ItemsSource = null;
            RequirementsList.ItemsSource = null;

            try {
                try {
                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? webUri))
                        LiveChatWebView.Source = webUri;
                } catch (Exception ex) {
                    Logger.Log($"[Home] WebView2 navigation failed: {ex.Message}");
                }
                var details = await SteamRipScraper.GetGameDetailsAsync(url);
                if (details == null) {
                    Logger.Log("[Home] Failed to load game details.");
                    return;
                }

                if (!string.IsNullOrEmpty(details.HowToRunNote))
                {
                    HowToRunSection.Visibility = Visibility.Visible;
                    var html = $@"<html><head><style>
                        body {{ font-family: 'Segoe UI', sans-serif; font-size: 14px; color: #e0e0e0; background: transparent; padding: 0; margin: 0; overflow-x: hidden; }}
                        strong {{ color: #4facfe; }}
                        ul {{ padding-left: 20px; }}
                        li {{ margin-bottom: 8px; }}
                    </style></head><body>{details.HowToRunNote}</body></html>";

                    try {
                        await EnsureWebViewReady(HowToRunWebView);
                        if (HowToRunWebView.CoreWebView2 != null)
                            HowToRunWebView.NavigateToString(html);
                    } catch (Exception ex) { Logger.Log($"[Home] Inline WebView2 fail: {ex.Message}"); }
                }
                else
                {
                    HowToRunSection.Visibility = Visibility.Collapsed;
                }

                var infoItems = new System.Collections.Generic.List<InfoItem>();
                foreach (var info in details.GameInfo)
                {
                    infoItems.Add(new InfoItem { Key = info.Key, Value = info.Value });
                }
                GameInfoList.ItemsSource = infoItems;

                var localSpecs = HardwareSpecsEngine.GetLocalSpecs();
                var reqItems = new System.Collections.Generic.List<ReqItem>();
                foreach (var req in details.SystemRequirements)
                {
                    var result = HardwareSpecsEngine.EvaluateRequirement(req.Key, req.Value, localSpecs,
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                    var icon = result == true ? "✅" : "➖";

                    int diff = HardwareSpecsEngine.GetRankDiff(req.Key, req.Value, localSpecs);

                    reqItems.Add(new ReqItem {
                        Icon = icon,
                        Key = req.Key,
                        Value = req.Value,
                        RankDiff = diff
                    });
                }
                RequirementsList.ItemsSource = reqItems;

            } catch (Exception ex) {
                Logger.LogError("PreviewUI", ex);
            } finally {
                RequirementsLoading.IsActive = false;
            }
        }

        private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LiveChatWebView.Source != null)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = LiveChatWebView.Source.ToString(),
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex) { Logger.LogError("OpenBrowser", ex); }
        }

        private async void LiveChatWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess) return;
            await Task.Delay(1200);
            await sender.ExecuteScriptAsync(@"
                (function() {
                    var s = document.createElement('style');
                    s.textContent = `
                        body { background: #1a1a2e !important; color: #e0e0e0 !important; }
                        header, .site-header, nav, .navigation, footer, .site-footer,
                        .sidebar, #sidebar, .social-share, .related-posts,
                        .post-navigation, .adsbygoogle, ins.adsbygoogle,
                        .cookie-notice, .gdpr, #cookie,
                        .popup, .modal, .overlay { display: none !important; }
                    `;
                    document.head.appendChild(s);
                    document.querySelectorAll('a[href]').forEach(function(a) {
                        var h = a.getAttribute('href');
                        if (h && h.startsWith('http') && !h.includes('steamrip.com')) {
                            a.removeAttribute('href');
                            a.style.pointerEvents = 'none';
                            a.style.opacity = '0.5';
                        }
                    });
                })();
            ");
        }

        private void PropsCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            PropertiesOverlayDimmer.Visibility = Visibility.Collapsed;
            PropertiesDialog.Visibility = Visibility.Collapsed;
        }

        private static string? ExtractFileNameFromUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try {
                var uri = new Uri(url);
                var name = Path.GetFileName(uri.LocalPath);

                if (!string.IsNullOrEmpty(name) && name.Contains('.'))
                {
                    Logger.Log($"[Filename] Extracted from URL: {name}");
                    return name;
                }
            } catch { }
            return null;
        }

        private static string MakeSafeFileName(string name) =>
            System.Text.RegularExpressions.Regex.Replace(name, @"[\\/:*?""<>|]", "").Replace(" ", "_").Trim('_');

        private void ResetDownloadButton(SearchResult item)
        {
            DispatcherQueue.TryEnqueue(() => {
                item.IsDownloading = false;
                item.DownloadStatus = "Download Link Ready";
            });
        }

        private async Task HandleCriticalSpaceWarning()
        {
            var dialog = new ContentDialog
            {
                Title = "🛑 Critical Storage Limit Hit",
                Content = "Extraction was cancelled because your disk space dropped below 10GB.\n\nPlease free up some space before trying again.\n\n(This specific warning will be silenced for 2 hours once acknowledged.)",
                CloseButtonText = "I Understand",
                XamlRoot = this.XamlRoot
            };
            await App.ShowDialogSafeAsync(dialog);
            GlobalSettings.LastSpaceWarningTime = DateTime.Now;
            GlobalSettings.Save();
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var suggestions = Core.JsonGameEntry.Search(sender.Text);
                sender.ItemsSource = suggestions.Count > 0 ? (object)suggestions : null;
            }
        }

        private object? _currentPopupGame = null;

        private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is Core.JsonGameEntry jsonGame)
            {
                sender.Text = jsonGame.name;
                _currentPopupGame = jsonGame;
                ShowGameInfoPopup(jsonGame);
                _ = RunSearch(jsonGame.name);
            }
        }

        private void CoverImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is SearchResult searchResult)
            {
                ShowGameInfoPopup(searchResult);
            }
        }

        private void ShowGameInfoPopup(object gameObj)
        {
            if (App.MainWindowInstance as MainWindow is MainWindow mw)
            {
                mw.ShowGameInfoPopup(gameObj, res => {
                    var dummyButton = new Button { DataContext = res };
                    QuickDownload_Click(dummyButton, new RoutedEventArgs());
                });
            }
        }
    }
}