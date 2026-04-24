using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace SteamRipApp
{
    public sealed partial class SteamPage : Page
    {
        public ObservableCollection<GameFolder> LibraryGames { get; } = new ObservableCollection<GameFolder>();
        public ObservableCollection<SteamAppIdResult> SteamSearchResults { get; } = new ObservableCollection<SteamAppIdResult>();
        private GameFolder? _activeGameForAppId;
        public SteamPage()
        {
            this.InitializeComponent();
            SteamSearchResultsList.ItemsSource = SteamSearchResults;
            this.Loaded += OnPageLoaded;
        }
        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            await RefreshLibrary();
        }
        private async Task RefreshLibrary()
        {
            try {
                LoadingRing.IsActive = true;
                LibraryGames.Clear();
                Logger.Log("[SteamPage] Starting library refresh for Steam integration.");
                var results = await ScannerEngine.ScanDirectoriesAsync(GlobalSettings.ScanDirectories, null);
                Logger.Log($"[SteamPage] Scanner found {results.Count} candidates.");
                var scannedPaths = new HashSet<string>(results.Select(g => g.RootPath), StringComparer.OrdinalIgnoreCase);
                foreach (var game in results)
                {
                    if (GlobalSettings.GamePageLinks.TryGetValue(game.RootPath, out var savedUrl) && !string.IsNullOrEmpty(savedUrl))
                        game.Url = savedUrl;
                    if (!game.IsSteamIntegrated && game.SteamAppId.HasValue)
                    {
                        string? steamPath = SteamManager.GetSteamPath();
                        if (!string.IsNullOrEmpty(steamPath) && File.Exists(Path.Combine(steamPath, "steamapps", $"appmanifest_{game.SteamAppId}.acf")))
                        {
                            game.IsSteamIntegrated = true;
                            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == game.RootPath);
                            if (meta != null)
                            {
                                meta.IsSteamIntegrated = true;
                                GlobalSettings.Save();
                            }
                        }
                    }
                    DispatcherQueue.TryEnqueue(() => LibraryGames.Add(game));
                }
                foreach (var meta in GlobalSettings.Library)
                {
                    if (scannedPaths.Contains(meta.LocalPath)) continue; 
                    if (scannedPaths.Any(p => 
                        meta.LocalPath.StartsWith(p, StringComparison.OrdinalIgnoreCase) || 
                        p.StartsWith(meta.LocalPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    if (!Directory.Exists(meta.LocalPath)) continue;
                    var gf = new GameFolder
                    {
                        Title = ScannerEngine.CleanTitle(meta.Title),
                        RootPath = meta.LocalPath,
                        Url = meta.Url,
                        Version = meta.Version,
                        ImageUrl = meta.ImageUrl,
                        SteamAppId = meta.ManualSteamAppId ?? meta.SteamAppId,
                        IsSteamIntegrated = meta.IsSteamIntegrated,
                        IsEmulatorApplied = meta.IsEmulatorApplied
                    };
                    if (!gf.IsSteamIntegrated && gf.SteamAppId.HasValue)
                    {
                        string? steamPath = SteamManager.GetSteamPath();
                        if (!string.IsNullOrEmpty(steamPath) && File.Exists(Path.Combine(steamPath, "steamapps", $"appmanifest_{gf.SteamAppId}.acf")))
                        {
                            gf.IsSteamIntegrated = true;
                            meta.IsSteamIntegrated = true;
                            GlobalSettings.Save();
                        }
                    }
                    var localImage = Path.Combine(meta.LocalPath, "folder.jpg");
                    if (File.Exists(localImage)) gf.LocalImagePath = localImage;
                    Logger.Log($"[SteamPage] Adding manual library entry: {gf.Title}");
                    DispatcherQueue.TryEnqueue(() => LibraryGames.Add(gf));
                }
                DispatcherQueue.TryEnqueue(() => SteamGameGrid.ItemsSource = LibraryGames);
                Logger.Log($"[SteamPage] Library refresh complete. Items count: {results.Count}");
            } catch (Exception ex) {
                Logger.LogError("SteamPageRefresh", ex);
            } finally {
                DispatcherQueue.TryEnqueue(() => LoadingRing.IsActive = false);
            }
        }
        private void SelectAll_Click(object sender, RoutedEventArgs e) => SteamGameGrid.SelectAll();
        private void DeselectAll_Click(object sender, RoutedEventArgs e) => SteamGameGrid.SelectedItems.Clear();
        private async void ImportSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = SteamGameGrid.SelectedItems.Cast<GameFolder>().ToList();
            if (selected.Count == 0) return;
            ImportSelectedBtn.IsEnabled = false;
            try
            {
                var steamPath = SteamManager.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return;
                var linksToCreate = new List<(string source, string target)>();
                foreach (var gf in selected)
                {
                    if (gf.HasAppId && !string.IsNullOrEmpty(gf.ExecutablePath))
                    {
                        string cleanExe = gf.ExecutablePath.Trim('\"');
                        string exeFolder = Path.GetDirectoryName(cleanExe) ?? "";
                        string targetDir = Path.Combine(steamPath, "steamapps", "common", gf.Title);
                        linksToCreate.Add((gf.RootPath, targetDir));
                    }
                }
                if (linksToCreate.Count > 0)
                {
                    StatusLabel.Text = "Requesting permission for filesystem links...";
                    NativeBridgeService.BatchSymlinks(linksToCreate);
                }
                int successCount = 0;
                for (int i = 0; i < selected.Count; i++)
                {
                    var gf = selected[i];
                    if (!gf.HasAppId)
                    {
                        Logger.Log($"[SteamPage] Skipping '{gf.Title}' - missing AppID.");
                        continue;
                    }
                    StatusLabel.Text = $"[{i + 1}/{selected.Count}] Processing '{gf.Title}'...";
                    bool success = await PerformImport(gf);
                    if (success) successCount++;
                }
                StatusLabel.Text = $"✅ Finished! {successCount}/{selected.Count} games integrated.";
                await new ContentDialog {
                    Title = "Bulk Import Complete",
                    Content = $"Successfully integrated {successCount} out of {selected.Count} selected games into Steam.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                }.ShowAsync();
            }
            finally
            {
                ImportSelectedBtn.IsEnabled = true;
            }
        }
        private async void ImportToSteam_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string rootPath)
            {
                var gf = LibraryGames.FirstOrDefault(g => g.RootPath == rootPath);
                if (gf != null)
                {
                    bool success = await PerformImport(gf);
                    if (success)
                    {
                        await new ContentDialog {
                            Title = "Game Integrated",
                            Content = $"'{gf.Title}' has been added to your Steam library with professional artwork.",
                            CloseButtonText = "OK",
                            XamlRoot = this.XamlRoot
                        }.ShowAsync();
                    }
                }
            }
        }
        private async Task<bool> PerformImport(GameFolder gf)
        {
            if (!gf.HasAppId)
            {
                StatusLabel.Text = $"❌ '{gf.Title}' missing AppID. Click ENTER APPID first.";
                return false;
            }
            if (string.IsNullOrEmpty(gf.ExecutablePath))
            {
                StatusLabel.Text = $"❌ No executable for '{gf.Title}'. Configure it in Library first.";
                return false;
            }
            try
            {
                StatusLabel.Text = $"Adding '{gf.Title}' to Steam...";
                bool success = await SteamManager.ImportGameToSteam(
                    gf.Title,
                    gf.ExecutablePath,
                    gf.SteamAppId
                );
                if (success)
                {
                    var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == gf.RootPath);
                    if (meta != null)
                    {
                        meta.IsSteamIntegrated = true;
                        GlobalSettings.Save();
                    }
                    gf.IsSteamIntegrated = true;
                    StatusLabel.Text = $"✅ '{gf.Title}' integrated.";
                    return true;
                }
                else
                {
                    StatusLabel.Text = $"❌ Failed: '{gf.Title}'.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"SteamImport_{gf.Title}", ex);
                StatusLabel.Text = $"❌ Error: '{gf.Title}'.";
                return false;
            }
        }
        private async void RemoveFromSteam_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string rootPath)
            {
                var gf = LibraryGames.FirstOrDefault(g => g.RootPath == rootPath);
                if (gf == null) return;
                var dialog = new ContentDialog
                {
                    Title = "Remove from Steam?",
                    Content = $"Are you sure you want to remove '{gf.Title}' from your Steam Library? This will delete the manifest and artwork, but keep the game files on your PC.",
                    PrimaryButtonText = "Remove",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    StatusLabel.Text = $"Removing '{gf.Title}' from Steam...";
                    bool success = await SteamManager.RemoveGameFromSteam(gf.Title, gf.ExecutablePath ?? "");
                    if (success)
                    {
                        var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == gf.RootPath);
                        if (meta != null)
                        {
                            meta.IsSteamIntegrated = false;
                            GlobalSettings.Save();
                        }
                        gf.IsSteamIntegrated = false;
                        StatusLabel.Text = $"🗑 '{gf.Title}' removed from Steam.";
                    }
                    else
                    {
                        StatusLabel.Text = $"❌ Failed to remove '{gf.Title}'.";
                    }
                }
            }
        }
        private void ResolveAppId_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string rootPath)
            {
                var gf = LibraryGames.FirstOrDefault(g => g.RootPath == rootPath);
                if (gf == null) return;
                _activeGameForAppId = gf;
                AppIdDialogTitle.Text = $"Resolve AppID: {gf.Title}";
                SteamSearchBox.Text = gf.Title;
                SteamSearchResults.Clear();
                ManualAppIdInput.Text = "";
                AppIdConfirmBtn.IsEnabled = false;
                AppIdSearchOverlay.Visibility = Visibility.Visible;
                AppIdSearchDialog.Visibility = Visibility.Visible;
            }
        }
        private async void SteamSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            try {
                SteamSearchLoading.IsActive = true;
                SteamSearchResults.Clear();
                string term = args.QueryText;
                string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US";
                using var client = new System.Net.Http.HttpClient();
                string json = await client.GetStringAsync(url);
                var response = System.Text.Json.JsonSerializer.Deserialize<SteamStoreSearchResponse>(json);
                if (response?.items != null)
                {
                    foreach (var item in response.items)
                        SteamSearchResults.Add(item);
                }
            } catch (Exception ex) {
                Logger.LogError("SteamSearch", ex);
            } finally {
                SteamSearchLoading.IsActive = false;
            }
        }
        private void SteamSearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppIdConfirmBtn.IsEnabled = SteamSearchResultsList.SelectedItem != null;
        }
        private void AppIdConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameForAppId != null && SteamSearchResultsList.SelectedItem is SteamAppIdResult selected)
            {
                ApplyAppId(_activeGameForAppId, selected.id);
            }
        }
        private void AssignManualId_Click(object sender, RoutedEventArgs e)
        {
            if (_activeGameForAppId != null && int.TryParse(ManualAppIdInput.Text, out int id))
            {
                ApplyAppId(_activeGameForAppId, id);
            }
        }
        private void ApplyAppId(GameFolder gf, int appId)
        {
            string cleanPath = gf.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var meta = GlobalSettings.Library.FirstOrDefault(m => 
                m.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(cleanPath, StringComparison.OrdinalIgnoreCase));
            if (meta != null)
            {
                string? oldIdStr = meta.ManualSteamAppId?.ToString() ?? meta.SteamAppId?.ToString();
                if (meta.IsSteamIntegrated && !string.IsNullOrEmpty(oldIdStr) && oldIdStr != appId.ToString())
                {
                    if (GlobalSettings.MemoryTable.TryGetValue(oldIdStr, out var shortcutId))
                    {
                        GlobalSettings.MemoryTable.Remove(oldIdStr);
                        GlobalSettings.MemoryTable[appId.ToString()] = shortcutId;
                        Logger.Log($"[SteamPage] Universally migrated MemoryTable entry for '{gf.Title}': {oldIdStr} -> {appId}");
                    }
                }
                meta.ManualSteamAppId = appId;
                GlobalSettings.Save();
                Logger.Log($"[SteamPage] Universally updated metadata for '{gf.Title}' to AppID {appId}");
                ScannerEngine.UpdateAppIdInFiles(gf.RootPath, appId);
            }
            else
            {
                var newMeta = new GameMetadata { LocalPath = gf.RootPath, Title = gf.Title, ManualSteamAppId = appId };
                GlobalSettings.Library.Add(newMeta);
                GlobalSettings.Save();
                Logger.Log($"[SteamPage] Created universal metadata for '{gf.Title}' with AppID {appId}");
            }
            gf.SteamAppId = appId;
            var index = LibraryGames.IndexOf(gf);
            if (index != -1)
            {
                LibraryGames.RemoveAt(index);
                LibraryGames.Insert(index, gf);
            }
            AppIdSearchOverlay.Visibility = Visibility.Collapsed;
            AppIdSearchDialog.Visibility = Visibility.Collapsed;
        }
        private void AppIdCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            AppIdSearchOverlay.Visibility = Visibility.Collapsed;
            AppIdSearchDialog.Visibility = Visibility.Collapsed;
        }
    }
    public class BooleanToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            (bool)value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            (Microsoft.UI.Xaml.Visibility)value == Microsoft.UI.Xaml.Visibility.Visible;
    }
    public class InvertedBooleanToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            !(bool)value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            (Microsoft.UI.Xaml.Visibility)value != Microsoft.UI.Xaml.Visibility.Visible;
    }
}

