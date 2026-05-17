using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace SteamRipApp
{
    public sealed partial class SettingsPage : Page
    {
        public ObservableCollection<string> ScanDirs { get; } = new ObservableCollection<string>();
        private bool _isLoading = true;

        public SettingsPage()
        {
            this.InitializeComponent();
            LoadSettings();
            _isLoading = false;

            GameDatabaseService.ProgressChanged += OnDbProgress;
            GameDatabaseService.DatabaseSwapped += OnDatabaseSwapped;
            UpdateDbStatus();
            UpdateDbChangesButton();
        }

        private void UpdateDbStatus()
        {
            bool hasFile = !string.IsNullOrEmpty(GameDatabaseService.ActiveFilePath);
            if (hasFile)
            {
                bool isBase = GameDatabaseService.IsBaseFile;
                string age  = GameDatabaseService.ActiveFileAge == DateTime.MinValue
                    ? "unknown age"
                    : $"{(DateTime.UtcNow - GameDatabaseService.ActiveFileAge).TotalHours:F1} h old";

                string lastStatus = GameDatabaseService.LastRefreshStatus;
                if (lastStatus.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    DbStatusLabel.Text = "⚠️ Refresh cancelled — existing database still active.";
                }
                else if (lastStatus.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    DbStatusLabel.Text = $"❌ {lastStatus}";
                }
                else
                {
                    DbStatusLabel.Text = isBase
                        ? $"✅ BASE database loaded ({age})"
                        : $"✅ Fresh database loaded ({age})";
                }

                DbFileLabel.Text = Path.GetFileName(GameDatabaseService.ActiveFilePath)
                    + $"  ·  {GameDatabaseService.Games?.Count ?? 0} games";
            }
            else
            {
                DbStatusLabel.Text = "⚠️ No database found";
                DbFileLabel.Text   = "A refresh will download the full game list.";
            }

            bool running = GameDatabaseService.IsRunning;
            DbRefreshBtn.IsEnabled     = !running;
            DbCancelBtn.Visibility     = running ? Visibility.Visible : Visibility.Collapsed;
            DbProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDbChangesButton()
        {
            var diff = GameDatabaseService.LastDiff;
            if (diff == null || !diff.HasChanges)
            {
                DbViewChangesBtn.Visibility = Visibility.Collapsed;
                return;
            }
            DbViewChangesBtn.Visibility = Visibility.Visible;
            DbViewChangesBtnLabel.Text =
                $"View Changes  (+{diff.Added.Count} / -{diff.Removed.Count} / 🔄 {diff.Modified.Count})";
        }

        private void OnDatabaseSwapped(Core.DatabaseDiff diff)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateDbStatus();
                UpdateDbChangesButton();
            });
        }

        private void OnDbProgress(string message, double pct)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (pct < 0)
                {
                    UpdateDbStatus();
                    return;
                }

                DbProgressBar.Value   = pct;
                DbProgressLabel.Text  = message;

                bool running = GameDatabaseService.IsRunning;
                DbRefreshBtn.IsEnabled  = !running;
                DbCancelBtn.Visibility  = running ? Visibility.Visible : Visibility.Collapsed;
                DbProgressPanel.Visibility = Visibility.Visible;

                if (!running || pct >= 100)
                {

                    UpdateDbStatus();
                    if (pct >= 100)
                    {
                        DbProgressPanel.Visibility = Visibility.Collapsed;
                        DbCancelBtn.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }

        private void LoadSettings()
        {
            ScanDirs.Clear();
            foreach (var dir in GlobalSettings.ScanDirectories) ScanDirs.Add(dir);
            DirList.ItemsSource = ScanDirs;

            DownloadDirLabel.Text = string.IsNullOrEmpty(GlobalSettings.DownloadDirectory)
                ? "Not set — you will be prompted on first download."
                : GlobalSettings.DownloadDirectory;

            if (GlobalSettings.DownloadSpeedUnit == SpeedUnit.Bits) SpeedBitsRadio.IsChecked = true;
            else SpeedBytesRadio.IsChecked = true;

            var method = GlobalSettings.PreferredExtractionMethod;
            if (method == ExtractionMethod.UnRarDLL) ExtractUnRarDLLRadio.IsChecked = true;
            else if (method == ExtractionMethod.WinRAR) ExtractWinRarRadio.IsChecked = true;
            else if (method == ExtractionMethod.SevenZip) ExtractSevenZipRadio.IsChecked = true;
            else ExtractWindowsRadio.IsChecked = true;

            AdvancedModeToggle.IsOn = GlobalSettings.IsAdvancedModeEnabled;
            HardRepairToggle.IsOn = GlobalSettings.IsHardRepairEnabled;
            MultiThreadedHashingToggle.IsOn = GlobalSettings.IsMultiThreadedHashingEnabled;
            SteamIntegrationToggle.IsOn = GlobalSettings.IsSteamIntegrationEnabled;

            UpdateToolStatus();
        }

        private void UpdateToolStatus()
        {
            var unrarDll = ArchiveExtractor.FindUnRarDLL();
            UnRarDllStatus.Text = unrarDll != null ? "✅ Internal - Ready" : "❌ Internal Binary Missing";
            UnRarDllStatus.Opacity = unrarDll != null ? 1.0 : 0.7;

            var winrar = ArchiveExtractor.FindWinRar();
            WinRarStatus.Text = winrar != null ? "✅ Installed" : "❌ Not Found";
            WinRarStatus.Opacity = winrar != null ? 1.0 : 0.7;

            var sevenZip = ArchiveExtractor.FindSevenZip();
            SevenZipStatus.Text = sevenZip != null ? "✅ Installed" : "❌ Not Found";
            SevenZipStatus.Opacity = sevenZip != null ? 1.0 : 0.7;
        }

        private void SpeedUnit_Checked(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (SpeedBitsRadio.IsChecked == true) GlobalSettings.DownloadSpeedUnit = SpeedUnit.Bits;
            else GlobalSettings.DownloadSpeedUnit = SpeedUnit.Bytes;
            GlobalSettings.Save();
        }

        private void ExtractMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is RadioButton rb && rb.Tag != null)
            {
                if (Enum.TryParse<ExtractionMethod>(rb.Tag.ToString(), out var method))
                {
                    GlobalSettings.PreferredExtractionMethod = method;
                    GlobalSettings.Save();
                }
            }
        }

        private async void DownloadWinRar_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.win-rar.com/download.html"));
        }

        private async void DownloadSevenZip_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.7-zip.org/download.html"));
        }

        private async void ChangeDownloadDir_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(path))
            {
                GlobalSettings.DownloadDirectory = path;
                GlobalSettings.HasSelectedDownloadDirectory = true;

                if (!GlobalSettings.ScanDirectories.Contains(path))
                {
                    GlobalSettings.ScanDirectories.Add(path);
                    ScanDirs.Add(path);
                }

                GlobalSettings.Save();
                DownloadDirLabel.Text = path;
                Logger.Log($"[Settings] Default download dir changed to: {path}");
            }
        }

        private async void AddDir_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(path))
            {
                if (!GlobalSettings.ScanDirectories.Contains(path))
                {
                    GlobalSettings.ScanDirectories.Add(path);
                    ScanDirs.Add(path);
                    GlobalSettings.Save();
                }
            }
        }

        private void RemoveDir_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                GlobalSettings.ScanDirectories.Remove(path);
                ScanDirs.Remove(path);
                GlobalSettings.Save();
            }
        }

        private async void RepairSync_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.IsEnabled = false;
                await Task.Run(async () => { await ScannerEngine.ScanDirectoriesAsync(GlobalSettings.ScanDirectories); });
                btn.IsEnabled = true;
            }
            ContentDialog dialog = new ContentDialog
            {
                Title = "Sync Complete",
                Content = "Local library has been synchronized with the latest scan data.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await App.ShowDialogSafeAsync(dialog);
        }

        private async void DbRefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            DbRefreshBtn.IsEnabled     = false;
            DbCancelBtn.Visibility     = Visibility.Visible;
            DbProgressPanel.Visibility = Visibility.Visible;
            DbProgressBar.Value        = 0;
            DbProgressLabel.Text       = "Starting…";
            DbStatusLabel.Text         = "🔄 Refreshing database…";

            await Task.Run(() => GameDatabaseService.RefreshNowAsync());

            UpdateDbStatus();
        }

        private void DbCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            GameDatabaseService.CancelRefresh();
            DbCancelBtn.Visibility     = Visibility.Collapsed;
            DbProgressPanel.Visibility = Visibility.Collapsed;
            DbRefreshBtn.IsEnabled     = true;
            DbStatusLabel.Text         = "⚠️ Refresh cancelled — existing database still active.";
        }

        private async void DbViewChangesBtn_Click(object sender, RoutedEventArgs e)
        {
            var diff = GameDatabaseService.LastDiff;
            if (diff == null) return;

            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var addedPanel = new StackPanel { Spacing = 4 };
            addedPanel.Children.Add(new TextBlock
            {
                Text       = $"✅ Added ({diff.Added.Count})",
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 8)
            });
            foreach (var link in diff.Added)
            {
                string slug = link.TrimEnd('/').Split('/').LastOrDefault() ?? link;
                slug = slug.Replace("-free-download", "", StringComparison.OrdinalIgnoreCase)
                           .Replace("-", " ");
                addedPanel.Children.Add(new TextBlock
                {
                    Text         = slug,
                    FontSize     = 12,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                });
            }
            if (diff.Added.Count == 0)
                addedPanel.Children.Add(new TextBlock { Text = "(none)", Opacity = 0.5, FontSize = 12 });

            var removedPanel = new StackPanel { Spacing = 4 };
            removedPanel.Children.Add(new TextBlock
            {
                Text       = $"❌ Removed ({diff.Removed.Count})",
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 8)
            });
            foreach (var link in diff.Removed)
            {
                string slug = link.TrimEnd('/').Split('/').LastOrDefault() ?? link;
                slug = slug.Replace("-free-download", "", StringComparison.OrdinalIgnoreCase)
                           .Replace("-", " ");
                removedPanel.Children.Add(new TextBlock
                {
                    Text         = slug,
                    FontSize     = 12,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                });
            }
            if (diff.Removed.Count == 0)
                removedPanel.Children.Add(new TextBlock { Text = "(none)", Opacity = 0.5, FontSize = 12 });

            var modifiedPanel = new StackPanel { Spacing = 4 };
            modifiedPanel.Children.Add(new TextBlock
            {
                Text       = $"🔄 Modified ({diff.Modified.Count})",
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 8)
            });
            foreach (var mod in diff.Modified)
            {
                var textBlock = new TextBlock
                {
                    Text         = mod.GameName,
                    FontSize     = 12,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                };
                ToolTipService.SetToolTip(textBlock, $"{mod.OldLink}\n↓\n{mod.NewLink}");
                modifiedPanel.Children.Add(textBlock);
            }
            if (diff.Modified.Count == 0)
                modifiedPanel.Children.Add(new TextBlock { Text = "(none)", Opacity = 0.5, FontSize = 12 });

            var addedScroll    = new ScrollViewer { Content = addedPanel,    MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var removedScroll  = new ScrollViewer { Content = removedPanel,  MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var modifiedScroll = new ScrollViewer { Content = modifiedPanel, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            Grid.SetColumn(addedScroll,    0);
            Grid.SetColumn(removedScroll,  1);
            Grid.SetColumn(modifiedScroll, 2);
            grid.Children.Add(addedScroll);
            grid.Children.Add(removedScroll);
            grid.Children.Add(modifiedScroll);

            string subtitle = $"{diff.PreviousFile}  →  {diff.NewFile}\n"
                            + $"Computed {diff.ComputedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

            var dialog = new ContentDialog
            {
                Title          = "Database Changes",
                Content        = new StackPanel
                {
                    Spacing  = 12,
                    Children =
                    {
                        new TextBlock { Text = subtitle, FontSize = 11, Opacity = 0.6, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                        grid
                    }
                },
                CloseButtonText = "Close",
                DefaultButton   = ContentDialogButton.Close,
                XamlRoot        = this.XamlRoot,
                MinWidth        = 800
            };

            await App.ShowDialogSafeAsync(dialog);
        }
        private void AdvancedModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (AdvancedModeToggle.IsOn != GlobalSettings.IsAdvancedModeEnabled)
            {
                GlobalSettings.IsAdvancedModeEnabled = AdvancedModeToggle.IsOn;
                GlobalSettings.Save();
            }
        }

        private void HardRepairToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            GlobalSettings.IsHardRepairEnabled = HardRepairToggle.IsOn;
            GlobalSettings.Save();
        }

        private void MultiThreadedHashingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (MultiThreadedHashingToggle.IsOn != GlobalSettings.IsMultiThreadedHashingEnabled)
            {
                GlobalSettings.IsMultiThreadedHashingEnabled = MultiThreadedHashingToggle.IsOn;
                GlobalSettings.Save();
            }
        }

        private void SteamIntegrationToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (SteamIntegrationToggle.IsOn != GlobalSettings.IsSteamIntegrationEnabled)
            {
                GlobalSettings.IsSteamIntegrationEnabled = SteamIntegrationToggle.IsOn;
                GlobalSettings.Save();

                var mw = (Application.Current as App)?.m_window as MainWindow;
                mw?.UpdateAdvancedTabsVisibility();

                if (GlobalSettings.IsSteamIntegrationEnabled)
                {
                    Logger.Log("[Settings] Steam Integration Tab shown.");
                }
                else
                {
                    Logger.Log("[Settings] Steam Integration Tab hidden.");
                }
            }
        }

        private async void SetupBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Reset to Initial Setup?",
                Content = "This will reset all your settings and preferences. Your library and downloaded games will NOT be deleted. The app will restart to show the setup screen.",
                PrimaryButtonText = "Reset & Restart",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var result = await App.ShowDialogSafeAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                GlobalSettings.ResetSettings();

                Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
            }
        }
    }
}