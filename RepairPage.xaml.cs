using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRipApp
{
    public sealed partial class RepairPage : Page
    {
        public ObservableCollection<GameFolder> Games { get; } = new ObservableCollection<GameFolder>();
        private CancellationTokenSource? _repairCts;

        public RepairPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            this.Loaded += async (s, e) => await LoadGames();
        }

        private async Task LoadGames()
        {
            LoadingRing.IsActive = true;
            Games.Clear();
            var results = await ScannerEngine.GetTrackedGamesAsync();
            foreach (var g in results)
            {
                Games.Add(g);
            }
            RepairGameGrid.ItemsSource = Games;
            LoadingRing.IsActive = false;
        }

        private async void RepairGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string path) return;

            var gf = Games.FirstOrDefault(g => g.RootPath == path);
            if (gf == null) return;

            string snapshotName = "Official Rip Map";
            Logger.Log($"[RepairTab] Repair Game requested for '{gf.Title}'");

            btn.IsEnabled = false;
            btn.Content = "Analyzing...";

            RepairReport report;
            try
            {
                report = await Task.Run(() => RepairService.AnalyzeGameAsync(path, path, snapshotName, (status, pct) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        GlobalSettings.HashingProgress = status;
                        GlobalSettings.HashingProgressValue = pct;
                    });
                }));
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "🛠 Repair Game";
            }

            if (report.Error != null)
            {
                await App.ShowDialogSafeAsync(new ContentDialog
                {
                    Title = "Analysis Error",
                    Content = report.Error,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                });
                return;
            }

            if (!report.HasIssues)
            {
                var ok = new ContentDialog
                {
                    Title = "✅ Integrity Perfect",
                    Content = $"All files in \"{gf.Title}\" are present and match the snapshot \"{snapshotName}\".",
                    CloseButtonText = "Great!",
                    XamlRoot = this.XamlRoot
                };
                await App.ShowDialogSafeAsync(ok);
                return;
            }

            string issues = "";
            if (report.MissingFiles.Any()) issues += $"❌ Missing files: {report.MissingFiles.Count}\n";
            if (report.CorruptedFiles.Any()) issues += $"⚠️ Corrupted files: {report.CorruptedFiles.Count}\n";

            var confirm = new ContentDialog
            {
                Title = "Issues Found",
                Content = $"{issues}\nUser-created files and mods are ignored.\n\nProceed with repair? Only affected files will be re-downloaded.",
                PrimaryButtonText = "Repair",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            if (await App.ShowDialogSafeAsync(confirm) != ContentDialogResult.Primary) return;

            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == path);
            string url = meta?.Url ?? GlobalSettings.GamePageLinks.GetValueOrDefault(path, "");

            if (string.IsNullOrEmpty(url))
            {
                _ = App.ShowDialogSafeAsync(new ContentDialog
                {
                    Title = "No Source URL",
                    Content = "No download URL is linked to this game. Please link the game page in the Library via the Properties menu first.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                });
                return;
            }

            btn.IsEnabled = false;
            btn.Content = "Repairing...";
            _repairCts = new CancellationTokenSource();

            try
            {
                await Task.Run(async () => await RepairService.PerformIntegrityRepairAsync(path, path, report, url,
                    (status, pct) =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            GlobalSettings.HashingProgress = status;
                            GlobalSettings.HashingProgressValue = pct;
                        });
                    }, _repairCts.Token));

                await App.ShowDialogSafeAsync(new ContentDialog
                {
                    Title = "✅ Repair Complete",
                    Content = $"\"{gf.Title}\" has been restored successfully.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                });
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"[RepairTab] Repair cancelled for {gf.Title}.");
            }
            catch (Exception ex)
            {
                Logger.LogError("RepairPage.RepairGame", ex);
                await App.ShowDialogSafeAsync(new ContentDialog
                {
                    Title = "Repair Failed",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                });
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "🛠 Repair Game";
                _repairCts = null;
                GlobalSettings.HashingProgress = null;
                GlobalSettings.HashingProgressValue = 0;
            }
        }

    }
}