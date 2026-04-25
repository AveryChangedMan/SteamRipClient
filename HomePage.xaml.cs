using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using SteamRipApp.Core;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace SteamRipApp
{
    public sealed partial class HomePage : Page
    {
        public ObservableCollection<SearchResult> Results { get; } = new ObservableCollection<SearchResult>();
        private TaskCompletionSource<string>? _downloadUrlTcs;
        private bool _interceptorReady = false;

        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
        }

        private async void SearchBtn_Click(object sender, RoutedEventArgs e)
        {
            await RunSearch(SearchBox.Text);
        }

        private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            await RunSearch(args.QueryText);
        }

        private async Task RunSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            try {
                LoadingRing.IsActive = true;
                Results.Clear();

                var searchResults = await SteamRipScraper.SearchAsync(query);
                foreach (var res in searchResults) Results.Add(res);
                ResultsGrid.ItemsSource = Results;

                
                _ = Task.Run(async () => {
                    try {
                        foreach (var item in searchResults)
                        {
                            
                            var bzTask = SteamRipScraper.CheckBuzzheavierAsync(item.Url);
                            var gfTask = SteamRipScraper.CheckGoFileAsync(item.Url);
                            await Task.WhenAll(bzTask, gfTask);

                            var (bzFound, bzUrl) = await bzTask;
                            var (gfFound, gfPageUrl, gfDirectLinks) = await gfTask;

                            this.DispatcherQueue.TryEnqueue(() => {
                                item.BuzzheavierUrl = bzUrl;
                                item.IsBuzzheavierAvailable = bzFound;
                                
                                item.GoFileUrl = gfPageUrl;
                                item.GoFileDirectLinks = gfDirectLinks;
                                item.IsGoFileAvailable = gfFound;
                            });
                        }
                    } catch { }
                });
            } catch (Exception ex) {
                Logger.LogError("HomeUI", ex);
            } finally {
                LoadingRing.IsActive = false;
            }
        }        
        private async void QuickDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var item = btn.DataContext as SearchResult;
            if (item == null || item.IsDownloading) return;

            try {
                item.IsDownloading = true;
                
                
                GlobalProgressPanel.Visibility = Visibility.Visible;
                GlobalProgressBar.Value = 0;
                GlobalStatusText.Text = "Initializing...";

                
                _ = Task.Run(async () => {
                    while (item.IsDownloading && GlobalProgressBar.Value < 90) {
                        await Task.Delay(200);
                        this.DispatcherQueue.TryEnqueue(() => {
                            if (GlobalProgressBar.Value < 30) GlobalProgressBar.Value += 2;
                            else if (GlobalProgressBar.Value < 60) GlobalProgressBar.Value += 1;
                            else GlobalProgressBar.Value += 0.5;
                        });
                    }
                });

                GlobalStatusText.Text = "Scanning for sources...";
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
                    GlobalProgressPanel.Visibility = Visibility.Collapsed;
                    var dialog = new ContentDialog {
                        Title = "Sources Not Found",
                        Content = "Sorry this game doesn't have our sources, try a manual installation. You can still add it to our launcher after its completed",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                    return;
                }

                
                if (bzFound && gfFound)
                {
                    GlobalStatusText.Text = "Select Source...";
                    var selectDialog = new ContentDialog {
                        Title = "Select Download Source",
                        Content = "Both Gofile and SteamRip (Buzzheavier) are available. Which would you prefer?",
                        PrimaryButtonText = "SteamRip (Buzzheavier)",
                        SecondaryButtonText = "Gofile (Fastest)",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.XamlRoot
                    };

                    var result = await selectDialog.ShowAsync();
                    if (result == ContentDialogResult.Primary) {
                        await StartBuzzheavierDownload(item);
                    }
                    else if (result == ContentDialogResult.Secondary) {
                        await StartGoFileDownload(item);
                    }
                    else {
                        item.IsDownloading = false;
                        GlobalProgressPanel.Visibility = Visibility.Collapsed;
                        return;
                    }
                }
                else if (gfFound)
                {
                    GlobalStatusText.Text = "Using Gofile...";
                    await StartGoFileDownload(item);
                }
                else if (bzFound)
                {
                    GlobalStatusText.Text = "Using Buzzheavier...";
                    await StartBuzzheavierDownload(item);
                }

                GlobalProgressBar.Value = 100;
                GlobalStatusText.Text = "Success!";
                await Task.Delay(1500);
            } catch (Exception ex) {
                Logger.LogError("QuickDownload", ex);
                GlobalStatusText.Text = "Error occurred.";
            } finally {
                item.IsDownloading = false;
                GlobalProgressPanel.Visibility = Visibility.Collapsed;
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

            
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle((Application.Current as App)?.m_window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return null;

            GlobalSettings.DownloadDirectory = folder.Path;
            GlobalSettings.HasSelectedDownloadDirectory = true;

            if (!GlobalSettings.ScanDirectories.Contains(folder.Path))
                GlobalSettings.ScanDirectories.Add(folder.Path);

            GlobalSettings.Save();
            Logger.Log($"[Download] Default download dir set to: {folder.Path}");
            return folder.Path;
        }

        
        private async Task StartBuzzheavierDownload(SearchResult item)
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
                    Logger.Log("[QuickDownload-Buzz] API resolution failed. Falling back to Interceptor...");
                    directUrl = await InterceptDownloadUrlAsync(bzzhrUrl);
                }

                if (string.IsNullOrEmpty(directUrl)) { Logger.Log("[QuickDownload-Buzz] No URL found."); return; }

                var fileName = ExtractFileNameFromUrl(directUrl) ?? (MakeSafeFileName(item.Title) + ".rar");
                var destPath = Path.Combine(savePath, fileName);

                await StartDownloadWithMetadata(item, destPath, directUrl, bzzhrUrl, "Buzzheavier");
            } catch (Exception ex) {
                Logger.LogError("QuickDownload-Buzz", ex);
            }
        }

        
        private async Task StartGoFileDownload(SearchResult item)
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
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.GoFileUrl) { UseShellExecute = true });
                    }
                    ResetDownloadButton(item);
                    return;
                }

                var fileName = ExtractFileNameFromUrl(directUrl) ?? (MakeSafeFileName(item.Title) + ".rar");
                var destPath = Path.Combine(savePath, fileName);

                await StartDownloadWithMetadata(item, destPath, directUrl, item.GoFileUrl ?? "", "Gofile", isGoFile: true);
            } catch (Exception ex) {
                Logger.LogError("QuickDownload-GoFile", ex);
            }
        }

        private async Task StartDownloadWithMetadata(SearchResult item, string destPath, string directUrl, string pageUrl, string sourceName, bool isGoFile = false)
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
                            await dialog.ShowAsync();
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
                            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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
            downloader.ThreadCount = 12;
            if (isGoFile) downloader.GoFileToken = GoFileClient.AccountToken;

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
                    metadata.Status = $"{speedVal:F1} {speedUnit}  {sizeStr}  {etaStr}  [{stats.ActiveThreads}t]";
                });
            };
            downloader.DownloadCompleted += async (s, e) =>
            {
                
                this.DispatcherQueue.TryEnqueue(() => {
                    metadata.Phase = "Extracting";
                    metadata.Status = "📦 Download complete — starting extraction...";
                    metadata.Percentage = 0;
                });

                
                var archivePath = destPath;
                var extractDir  = Path.GetDirectoryName(destPath) ?? GlobalSettings.DownloadDirectory;

                
                string version = "";
                try {
                    if (!string.IsNullOrEmpty(item.Url))
                        version = (await SteamRipScraper.GetGameDetailsAsync(item.Url)).LatestVersion;
                } catch { }

                
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
                            if (msg.Contains("CRITICAL SPACE LIMIT"))
                            {
                                if (DateTime.Now - GlobalSettings.LastSpaceWarningTime > TimeSpan.FromHours(2))
                                {
                                    _ = HandleCriticalSpaceWarning();
                                }
                            }
                        });
                    },
                    onProgress: pct => this.DispatcherQueue.TryEnqueue(() => metadata.Percentage = Math.Round(pct, 1)),
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
                            var result = await dialog.ShowAsync();
                            tcs.SetResult(result == ContentDialogResult.Primary);
                        });
                        return await tcs.Task;
                    },
                    confirmMap: async (title) => {
                        var tcs = new TaskCompletionSource<bool>();
                        this.DispatcherQueue.TryEnqueue(async () => {
                            try {
                                var dialog = new ContentDialog {
                                    Title = "Create Repair Map?",
                                    Content = $"Would you like to generate a byte-map for {title}? This allows the 'Hard Repair' system to fix corrupted files without re-downloading the entire game later.",
                                    PrimaryButtonText = "Create Map (Recommended)",
                                    CloseButtonText = "Skip",
                                    XamlRoot = this.XamlRoot
                                };
                                var result = await dialog.ShowAsync();
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
                    if (gameFolder != null)
                    {
                        metadata.Phase = "Done";
                        metadata.Status = "✅ Extraction completed";
                        metadata.Percentage = 100;
                    }
                    else
                    {
                        metadata.Phase = "Failed";
                        metadata.Status = "❌ Extraction failed — verify extractor in Settings.";
                    }

                    GlobalSettings.Save();

                    
                    var mw = (Application.Current as App)?.m_window as MainWindow;
                    mw?.RefreshLibrary();
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
                    
                    var btn = document.querySelector('a.link-button[hx-get*=""download""]');
                    if (btn) { btn.click(); return 'clicked-buzzheavier'; }

                    
                    var gfBtn = document.querySelector('button[id*=""download""], .downloadButton, button[class*=""download""]');
                    if (gfBtn) { gfBtn.click(); return 'clicked-gofile'; }

                    
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

        private async void Preview_Click(object sender, RoutedEventArgs e)
        {
            string? url = null;
            if (sender is Button btn)
                url = (btn.DataContext as SearchResult)?.Url ?? btn.Tag as string;
            if (string.IsNullOrEmpty(url)) return;

            PropertiesOverlayDimmer.Visibility = Visibility.Visible;
            PropertiesDialog.Visibility = Visibility.Visible;
            RequirementsLoading.IsActive = true;
            RequirementsList.Children.Clear();
            GameInfoList.Children.Clear();

            try {
                LiveChatWebView.Source = new Uri(url);
                var details = await SteamRipScraper.GetGameDetailsAsync(url);

                foreach (var info in details.GameInfo)
                {
                    var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 2) };
                    panel.Children.Add(new TextBlock {
                        Text = info.Key + ":",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Width = 100,
                        TextWrapping = TextWrapping.NoWrap,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)
                    });
                    panel.Children.Add(new TextBlock { Text = info.Value, TextWrapping = TextWrapping.Wrap });
                    GameInfoList.Children.Add(panel);
                }

                var localSpecs = HardwareSpecsEngine.GetLocalSpecs();
                foreach (var req in details.SystemRequirements)
                {
                    var result = HardwareSpecsEngine.EvaluateRequirement(req.Key, req.Value, localSpecs,
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                    var icon = result == true ? "✅" : (result == false ? "❌" : "➖");
                    var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    panel.Children.Add(new TextBlock { Text = icon, Width = 20 });
                    panel.Children.Add(new TextBlock { Text = req.Key + ":", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Width = 90 });
                    panel.Children.Add(new TextBlock { Text = req.Value, TextWrapping = TextWrapping.Wrap });
                    RequirementsList.Children.Add(panel);
                }
            } catch (Exception ex) {
                Logger.LogError("PreviewUI", ex);
            } finally {
                RequirementsLoading.IsActive = false;
            }
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
            await dialog.ShowAsync();
            GlobalSettings.LastSpaceWarningTime = DateTime.Now;
            GlobalSettings.Save();
        }
    }
}
