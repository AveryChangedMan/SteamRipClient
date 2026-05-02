using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace SteamRipApp
{
    public sealed partial class DownloadsPage : Page
    {
        public DownloadsPage()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) => {

                RefreshDownloads();

                await ScannerEngine.ScanDirectoriesAsync(GlobalSettings.ScanDirectories);

                RefreshDownloads();
            };
        }

        private async void RefreshScan_Click(object sender, RoutedEventArgs e)
        {
            if (RefreshScanBtn != null) RefreshScanBtn.IsEnabled = false;
            await ScannerEngine.ScanDirectoriesAsync(GlobalSettings.ScanDirectories);
            RefreshDownloads();
            if (RefreshScanBtn != null) RefreshScanBtn.IsEnabled = true;
        }
        private void RefreshDownloads()
        {
            try {
                if (GlobalSettings.ActiveDownloads == null) return;

                foreach (var dl in GlobalSettings.ActiveDownloads)
                {
                    if (dl == null) continue;
                    dl.IsInLibrary = GlobalSettings.Library.Any(m =>
                        m.Title.Equals(dl.Title, StringComparison.OrdinalIgnoreCase) ||
                        m.Title.Equals(ScannerEngine.CleanTitle(dl.Title), StringComparison.OrdinalIgnoreCase));
                    dl.NotifyAll();
                }

                if (DownloadsList.ItemsSource != GlobalSettings.ActiveDownloads)
                    DownloadsList.ItemsSource = GlobalSettings.ActiveDownloads;

                EmptyLabel.Visibility = GlobalSettings.ActiveDownloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            } catch (Exception ex) {
                Logger.LogError("DownloadsPage.Refresh", ex);
            }
        }

        private void GoToLibrary_Click(object sender, RoutedEventArgs e)
        {
            var mw = (Application.Current as App)?.m_window as MainWindow;
            mw?.NavigateToLibrary();
        }

        private async void DownloadAgain_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string pageUrl) return;

            var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d => d.PageUrl == pageUrl);
            if (dl != null)
            {
                dl.Phase = "Downloading";
                dl.Percentage = 0;
                dl.Status = "🔄 Restarting...";
                ResumeOrphanedDownload(dl);
            }
        }

        private void PauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string destPath) return;

            var icon = FindChild<SymbolIcon>(btn);

            if (CustomDownloader.ActiveInstances.TryGetValue(destPath, out var downloader))
            {
                var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d => d.DestPath == destPath);

                if (downloader.State == DownloadState.Downloading)
                {
                    downloader.Pause();
                    if (dl != null) {
                        dl.IsPaused = true;
                        dl.Status = "⏸ Paused";
                    }
                }
                else if (downloader.State == DownloadState.Paused)
                {
                    downloader.Resume();
                    if (dl != null) {
                        dl.IsPaused = false;
                        dl.Status = "▶ Resuming...";
                    }
                }
            }
            else
            {
                var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d => d.DestPath == destPath);
                if (dl != null)
                {
                    dl.IsPaused = false;
                    dl.Status = "🔄 Resuming...";
                    ResumeOrphanedDownload(dl);
                }
            }
        }

        private async void ResumeOrphanedDownload(ActiveDownloadMetadata dl)
        {
            try {
                if (dl.Phase == "Extracting" || string.IsNullOrEmpty(dl.SourceUrl))
                {
                    _ = RunExtractionOnlyAsync(dl);
                    return;
                }

                var session = DownloadSessionMetadata.Load(dl.DestPath);
                if (session != null)
                {
                    dl.Title = session.GameTitle;
                    dl.SteamRipUrl = session.SteamRipUrl;
                    dl.Version = session.Version;
                    dl.ImageUrl = session.ImageUrl;
                    dl.NotifyAll();
                }

                _ = new DownloadSessionMetadata
                {
                    GameTitle = dl.Title,
                    SteamRipUrl = dl.SteamRipUrl,
                    ArchivePath = dl.DestPath,
                    Version = dl.Version ?? "",
                    ImageUrl = dl.ImageUrl,
                    DownloadDir = GlobalSettings.DownloadDirectory
                }.SaveAsync();

                var downloader = new CustomDownloader(dl.SourceUrl, dl.DestPath);
                downloader.BuzzheavierPageUrl = dl.PageUrl;
                downloader.SteamRipPageUrl = dl.SteamRipUrl;
                downloader.ImageUrl = dl.ImageUrl;
                downloader.ThreadCount = dl.Source == "Gofile" ? CustomDownloader.MaxThreads : 12;
                if (dl.Source == "Gofile") downloader.GoFileToken = GoFileClient.AccountToken;

                downloader.ProgressChanged += (s, stats) => {
                    this.DispatcherQueue.TryEnqueue(() => {
                        dl.Percentage = stats.Percentage;
                        dl.ThreadCount = stats.ActiveThreads;
                        dl.IsPaused = false;

                        double speedVal = stats.SpeedMBps;
                        string speedUnit = "MB/s";
                        if (GlobalSettings.DownloadSpeedUnit == SpeedUnit.Bits) { speedVal *= 8; speedUnit = "Mbps"; }

                        var sizeStr = stats.TotalBytes > 0
                            ? $"{stats.BytesReceived / (1024.0 * 1024):F0}/{stats.TotalBytes / (1024.0 * 1024):F0} MB"
                            : "";
                        var etaStr = stats.ETA.TotalSeconds > 0 ? $"ETA {stats.ETA:mm\\:ss}" : "";

                        dl.Status = $"Downloading {stats.Percentage:F0}% ({speedVal:F1} {speedUnit}) {sizeStr} {etaStr} [{stats.ActiveThreads}t]";
                        dl.NotifyAll();
                    });
                };

                downloader.DownloadCompleted += async (s, e) => {
                    this.DispatcherQueue.TryEnqueue(() => {
                        dl.Phase = "Extracting";
                        dl.Status = "📦 Starting extraction...";
                        dl.Percentage = 0;
                        dl.NotifyAll();
                    });

                    var session = DownloadSessionMetadata.Load(dl.DestPath);
                    string gameTitle = dl.Title ?? "Unknown Game";
                    string steamRipUrl = dl.SteamRipUrl ?? "";
                    string imageUrl = dl.ImageUrl ?? "";
                    string version = dl.Version ?? "";

                    if (session != null)
                    {
                        gameTitle = session.GameTitle ?? gameTitle;
                        steamRipUrl = session.SteamRipUrl ?? steamRipUrl;
                        imageUrl = session.ImageUrl ?? imageUrl;
                        version = session.Version ?? version;

                        dl.Title = gameTitle;
                        dl.Version = version;
                        dl.SteamRipUrl = steamRipUrl;
                        dl.NotifyAll();
                    }

                    var extractDir = Path.GetDirectoryName(dl.DestPath) ?? GlobalSettings.DownloadDirectory;
                    var gameFolder = await PostDownloadProcessor.RunAsync(
                        archivePath: dl.DestPath,
                        extractToDir: extractDir,
                        gameTitle: gameTitle,
                        steamRipPageUrl: steamRipUrl,
                        imageUrl: imageUrl,
                        version: version,
                        onStatus: msg => this.DispatcherQueue.TryEnqueue(() => dl.Status = msg),
                        onProgress: pct => this.DispatcherQueue.TryEnqueue(() => { dl.Percentage = pct; dl.NotifyAll(); }),
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
                        if (gameFolder != null) {
                            dl.Phase = "Done";
                            dl.Status = "✅ Extraction completed";
                            dl.Percentage = 100;
                        } else {
                            dl.Phase = "Failed";
                            dl.Status = "❌ Extraction failed";
                        }
                        dl.NotifyAll();
                        GlobalSettings.Save();
                    });
                };

                downloader.DownloadFailed += (s, msg) => {
                    this.DispatcherQueue.TryEnqueue(() => {
                        dl.Phase = "Failed";
                        dl.Status = $"❌ Error: {msg}";
                        dl.NotifyAll();
                        GlobalSettings.Save();
                    });
                };

                _ = Task.Run(async () => {
                    try {
                        await downloader.StartDownloadAsync();
                    } catch (Exception ex) {
                        this.DispatcherQueue.TryEnqueue(() => {
                            dl.Status = $"❌ Start failed: {ex.Message}";
                            dl.Phase = "Failed";
                        });
                    }
                });
            } catch (Exception ex) {
                Logger.LogError("DownloadsPage.Resume", ex);
                dl.Status = "❌ Failed to start";
            }
        }

        private async void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string destPath) return;

            if (CustomDownloader.ActiveInstances.TryGetValue(destPath, out var downloader))
                downloader.Cancel();

            var dialog = new ContentDialog {
                Title = "Remove from list?",
                Content = "Would you also like to delete the file from your computer?",
                PrimaryButtonText = "Remove & Delete File",
                SecondaryButtonText = "Remove from List Only",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            var result = await App.ShowDialogSafeAsync(dialog);

            if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
            {
                if (result == ContentDialogResult.Primary)
                {
                    try {
                        var parts = ArchiveExtractor.FindArchiveParts(destPath);
                        foreach (var part in parts)
                        {
                            if (File.Exists(part)) File.Delete(part);
                        }
                    } catch { }
                    try { if (File.Exists(destPath + ".progress")) File.Delete(destPath + ".progress"); } catch { }
                }

                var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d => d.DestPath == destPath);
                if (dl != null) GlobalSettings.ActiveDownloads.Remove(dl);
                GlobalSettings.Save();
                RefreshDownloads();
            }
        }

        public void CopyLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                try {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(url);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                } catch { }
            }
        }

        public void RemoveHistory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string title)
            {
                var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d => d.Title == title);
                if (dl != null)
                {
                    GlobalSettings.ActiveDownloads.Remove(dl);
                    GlobalSettings.Save();
                    RefreshDownloads();
                }
            }
        }

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private async Task RunExtractionOnlyAsync(ActiveDownloadMetadata dl)
        {
            try {
                this.DispatcherQueue.TryEnqueue(() => {
                    dl.Phase = "Extracting";
                    dl.Status = "📦 Preparing extraction...";
                    dl.Percentage = 0;
                    dl.IsPaused = false;
                    dl.NotifyAll();
                });

                if (!File.Exists(dl.DestPath))
                {
                    this.DispatcherQueue.TryEnqueue(() => {
                        dl.Phase = "Failed";
                        dl.Status = "❌ Archive missing";
                    });
                    return;
                }

                var session = DownloadSessionMetadata.Load(dl.DestPath);
                string gameTitle = dl.Title;
                string steamRipUrl = dl.SteamRipUrl;
                string imageUrl = dl.ImageUrl;
                string version = dl.Version;

                if (session != null)
                {
                    gameTitle = session.GameTitle;
                    steamRipUrl = session.SteamRipUrl;
                    imageUrl = session.ImageUrl;
                    version = session.Version;

                    dl.Title = gameTitle;
                    dl.Version = version;
                    dl.SteamRipUrl = steamRipUrl;
                    dl.NotifyAll();
                }

                var extractDir = Path.GetDirectoryName(dl.DestPath) ?? GlobalSettings.DownloadDirectory;
                var gameFolder = await PostDownloadProcessor.RunAsync(
                    archivePath: dl.DestPath,
                    extractToDir: extractDir,
                    gameTitle: gameTitle,
                    steamRipPageUrl: steamRipUrl,
                    imageUrl: imageUrl,
                    version: version,
                    onStatus: msg => this.DispatcherQueue.TryEnqueue(() => dl.Status = msg),
                    onProgress: pct => this.DispatcherQueue.TryEnqueue(() => { dl.Percentage = pct; dl.NotifyAll(); }),
                    confirmMap: async (title) => {
                        if (GlobalSettings.AlwaysCreateRarMap) return true;
                        return true;
                    }
                );

                this.DispatcherQueue.TryEnqueue(() => {
                    if (gameFolder != null) {
                        dl.Phase = "Done";
                        dl.Status = "✅ Extraction completed";
                        dl.Percentage = 100;
                    } else {
                        dl.Phase = "Failed";
                        dl.Status = "❌ Extraction failed";
                    }
                    dl.NotifyAll();
                    GlobalSettings.Save();
                });
            } catch (Exception ex) {
                Logger.LogError("DownloadsPage.ExtractOnly", ex);
                this.DispatcherQueue.TryEnqueue(() => {
                    dl.Phase = "Failed";
                    dl.Status = $"❌ Error: {ex.Message}";
                });
            }
        }
    }
}