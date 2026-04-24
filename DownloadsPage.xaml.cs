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
                foreach (var dl in snapshot)
                {
                    if (dl != null)
                    {
                        dl.Title ??= "Unknown";
                        dl.Status ??= "";
                        dl.Phase ??= "Downloading";
                        dl.ImageUrl ??= "";
                        dl.DestPath ??= "";
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
                    Logger.Log($"[Downloads] Repairing orphaned download: {destPath}");
                    if (icon != null) icon.Symbol = Symbol.Pause;
                    dl.Status = "🔄 Repairing...";
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
                        dl.ThreadCount = stats.ActiveThreads;
                        dl.SizeLabel = $"{stats.BytesReceived / (1024 * 1024)}/{stats.TotalBytes / (1024 * 1024)} MB";
                    });
                };
                downloader.DownloadCompleted += (s, e) => {
                    this.DispatcherQueue.TryEnqueue(() => {
                        dl.Status = "✅ Completed";
                        dl.Percentage = 100;
                        GlobalSettings.ActiveDownloads.Remove(dl);
                        GlobalSettings.Save();
                        RefreshDownloads();
                    });
                };
                downloader.DownloadFailed += (s, msg) => {
                    this.DispatcherQueue.TryEnqueue(() => {
                        dl.Status = $"❌ Error: {msg}";
                        GlobalSettings.Save();
                    });
                };
                downloader.LinkExpired += async (pUrl) => {
                    Logger.Log($"[Downloads] Re-intercepting for renewal: {pUrl}");
                    var (found, freshPage, directLinks) = await GoFileClient.CheckAndResolveAsync(pUrl);
                    if (directLinks.Count > 0) return directLinks[0];
                    return "";
                };
                _ = downloader.StartDownloadAsync();
            } catch (Exception ex) {
                Logger.LogError("DownloadsPage.ResumeOrphaned", ex);
                dl.Status = "❌ Repair Failed";
            }
        }
        private async void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string destPath) return;
            if (CustomDownloader.ActiveInstances.TryGetValue(destPath, out var downloader))
                downloader.Cancel();
            var dialog = new ContentDialog
            {
                Title = "Delete file?",
                Content = "Do you also want to delete the partially downloaded file from disk?",
                PrimaryButtonText = "Delete file",
                SecondaryButtonText = "Keep file",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                try { if (File.Exists(destPath + ".progress")) File.Delete(destPath + ".progress"); } catch { }
                Logger.Log($"[Downloads] Deleted file: {destPath}");
            }
            var dl = GlobalSettings.ActiveDownloads.FirstOrDefault(d => d.DestPath == destPath);
            if (dl != null) GlobalSettings.ActiveDownloads.Remove(dl);
            GlobalSettings.Save();
            RefreshDownloads();
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

