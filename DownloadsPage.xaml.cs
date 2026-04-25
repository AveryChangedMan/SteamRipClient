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
        public ObservableCollection<ActiveDownloadMetadata> ActiveDownloads { get; } = new ObservableCollection<ActiveDownloadMetadata>();

        public DownloadsPage()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => RefreshDownloads();
        }

        private void RefreshDownloads()
        {
            try {
                if (GlobalSettings.ActiveDownloads == null) return;

                
                var snapshot = GlobalSettings.ActiveDownloads.ToList();
                
                ActiveDownloads.Clear();
                
                for (int i = snapshot.Count - 1; i >= 0; i--)
                {
                    var dl = snapshot[i];
                    if (dl != null)
                    {
                        dl.Title ??= "Unknown";
                        dl.Status ??= "";
                        dl.Phase ??= "Downloading";
                        dl.ImageUrl ??= "";
                        dl.DestPath ??= "";
                        
                        
                        dl.IsInLibrary = GlobalSettings.Library.Any(m => m.Title.Equals(dl.Title, StringComparison.OrdinalIgnoreCase) || 
                                                                        m.Title.Equals(ScannerEngine.CleanTitle(dl.Title), StringComparison.OrdinalIgnoreCase));
                        dl.NotifyAll();
                        ActiveDownloads.Add(dl);
                    }
                }

                if (DownloadsList.ItemsSource == null)
                    DownloadsList.ItemsSource = ActiveDownloads;

                EmptyLabel.Visibility = ActiveDownloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

            var mw = (Application.Current as App)?.m_window as MainWindow;
            mw?.NavigateToHome();
            
            
            
            
            
            var dl = ActiveDownloads.FirstOrDefault(d => d.PageUrl == pageUrl);
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
                    if (icon != null) icon.Symbol = Symbol.Play;
                    if (dl != null) dl.Status = "⏸ Paused";
                }
                else if (downloader.State == DownloadState.Paused)
                {
                    downloader.Resume();
                    if (icon != null) icon.Symbol = Symbol.Pause;
                    if (dl != null) dl.Status = "▶ Resuming...";
                }
            }
            else 
            {
                var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d => d.DestPath == destPath);
                if (dl != null)
                {
                    if (icon != null) icon.Symbol = Symbol.Pause;
                    dl.Status = "🔄 Resuming...";
                    ResumeOrphanedDownload(dl);
                }
            }
        }

        private async void ResumeOrphanedDownload(ActiveDownloadMetadata dl)
        {
            try {
                var downloader = new CustomDownloader(dl.SourceUrl, dl.DestPath);
                downloader.BuzzheavierPageUrl = dl.PageUrl;
                downloader.ThreadCount = dl.Source == "Gofile" ? CustomDownloader.MaxThreads : 12;
                if (dl.Source == "Gofile") downloader.GoFileToken = GoFileClient.AccountToken;

                downloader.ProgressChanged += (s, stats) => {
                    this.DispatcherQueue.TryEnqueue(() => {
                        dl.Percentage = stats.Percentage;
                        dl.Status = $"Downloading {stats.Percentage:F0}% ({stats.SpeedMBps:F1} Mbps)";
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

                    
                    var extractDir = Path.GetDirectoryName(dl.DestPath) ?? GlobalSettings.DownloadDirectory;
                    var gameFolder = await PostDownloadProcessor.RunAsync(
                        archivePath: dl.DestPath,
                        extractToDir: extractDir,
                        gameTitle: dl.Title,
                        steamRipPageUrl: dl.SteamRipUrl,
                        imageUrl: dl.ImageUrl,
                        version: "", 
                        onStatus: msg => this.DispatcherQueue.TryEnqueue(() => dl.Status = msg),
                        onProgress: pct => this.DispatcherQueue.TryEnqueue(() => { dl.Percentage = pct; dl.NotifyAll(); }),
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

                _ = downloader.StartDownloadAsync();
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
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
            {
                if (result == ContentDialogResult.Primary)
                {
                    try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
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
    }
}
