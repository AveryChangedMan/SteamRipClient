using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

            
            var method = GlobalSettings.PreferredExtractionMethod ?? ExtractionMethod.WinRAR;
            if (method == ExtractionMethod.WinRAR) ExtractWinRarRadio.IsChecked = true;
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
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).m_window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                GlobalSettings.DownloadDirectory = folder.Path;
                GlobalSettings.HasSelectedDownloadDirectory = true;

                if (!GlobalSettings.ScanDirectories.Contains(folder.Path))
                {
                    GlobalSettings.ScanDirectories.Add(folder.Path);
                    ScanDirs.Add(folder.Path);
                }

                GlobalSettings.Save();
                DownloadDirLabel.Text = folder.Path;
                Logger.Log($"[Settings] Default download dir changed to: {folder.Path}");
            }
        }

        private async void AddDir_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");

            
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).m_window);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                if (!GlobalSettings.ScanDirectories.Contains(folder.Path))
                {
                    GlobalSettings.ScanDirectories.Add(folder.Path);
                    ScanDirs.Add(folder.Path);
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
                
                await Task.Run(async () => {
                    await ScannerEngine.ScanDirectoriesAsync(GlobalSettings.ScanDirectories);
                });

                btn.IsEnabled = true;
            }
            ContentDialog dialog = new ContentDialog
            {
                Title = "Sync Complete",
                Content = "Local library has been synchronized with the latest scan data.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        private void AdvancedModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AdvancedModeToggle.IsOn != GlobalSettings.IsAdvancedModeEnabled)
            {
                GlobalSettings.IsAdvancedModeEnabled = AdvancedModeToggle.IsOn;
                GlobalSettings.Save();
                
                
                var mw = (Application.Current as App)?.m_window as MainWindow;
                mw?.UpdateAdvancedTabsVisibility();
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

                if (GlobalSettings.IsSteamIntegrationEnabled)
                {
                    (Application.Current as App)?.EnsureWorkerRunning();
                }
                else
                {
                    
                    Logger.Log("[Settings] Steam Integration disabled. Background worker will shut down shortly.");
                }
            }
        }
    }
}
