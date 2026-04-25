using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI;
using SteamRipApp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;

namespace SteamRipApp
{
    public sealed partial class CleanupView : Page
    {
        private List<GameFolder> _games = new List<GameFolder>();
        private ObservableCollection<RedistCleanupItem> _redistItems = new ObservableCollection<RedistCleanupItem>();
        private GameFolder? _selectedGameFromChart;
        private MenuFlyout _pieContextFlyout;

        public CleanupView()
        {
            this.InitializeComponent();
            _pieContextFlyout = CreatePieContextFlyout();
            RedistList.ItemsSource = _redistItems;
            Loaded += async (s, e) => await RefreshAll();
        }

        private MenuFlyout CreatePieContextFlyout()
        {
            var flyout = new MenuFlyout();
            var openItem = new MenuFlyoutItem { Text = "Open Folder", Icon = new SymbolIcon(Symbol.Folder) };
            openItem.Click += OpenFolderMenu_Click;
            var uninstallItem = new MenuFlyoutItem { Text = "Uninstall Game", Icon = new SymbolIcon(Symbol.Delete) };
            uninstallItem.Click += UninstallMenu_Click;
            
            flyout.Items.Add(openItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(uninstallItem);
            return flyout;
        }

        private async Task RefreshAll()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            _games = GlobalSettings.Library
                .Where(m => !string.IsNullOrEmpty(m.LocalPath) && Directory.Exists(m.LocalPath))
                .GroupBy(m => m.LocalPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).ToLowerInvariant())
                .Select(g => g.First())
                .Select(m => new GameFolder { 
                    Title = m.Title, 
                    RootPath = m.LocalPath, 
                    SizeBytes = m.SizeBytes 
                }).ToList();

            
            bool sizeUpdated = false;
            foreach (var g in _games.Where(x => x.SizeBytes == 0))
            {
                try {
                    if (Directory.Exists(g.RootPath))
                    {
                        g.SizeBytes = Directory.GetFiles(g.RootPath, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                        var libEntry = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == g.RootPath);
                        if (libEntry != null) libEntry.SizeBytes = g.SizeBytes;
                        sizeUpdated = true;
                    }
                } catch { }
            }
            if (sizeUpdated) GlobalSettings.Save();

            UpdateSpaceInfo();
            UpdateRedistInfo();
            DrawPieChart();
            await Task.CompletedTask;
        }

        private void UpdateRedistInfo()
        {
            _redistItems.Clear();
            long totalRedistSize = 0;

            foreach (var game in _games)
            {
                string redistPath = System.IO.Path.Combine(game.RootPath, "_CommonRedist");
                if (Directory.Exists(redistPath))
                {
                    var allRedists = RedistService.GetRequiredRedists(game.RootPath);
                    var missing = allRedists.Where(r => !r.IsInstalled).ToList();
                    
                    
                    
                    if (missing.Count == 0)
                    {
                        long size = GetExeOnlySize(redistPath);
                        if (size > 0)
                        {
                            totalRedistSize += size;
                            _redistItems.Add(new RedistCleanupItem { 
                                GameTitle = game.Title, 
                                FolderPath = redistPath, 
                                SizeBytes = size 
                            });
                            Logger.Log($"[Cleanup] Found cleanable redist in {game.Title}: {size} bytes");
                        }
                    }
                    else
                    {
                        Logger.Log($"[Cleanup] Skipping {game.Title} redist: {missing.Count} requirements not met yet.");
                    }
                }
            }

            RedistCleanupText.Text = $"Clean Redist (~{(totalRedistSize / 1024.0 / 1024.0):F1} MB)";
            RedistCleanupButton.IsEnabled = _redistItems.Count > 0;
        }

        private long GetExeOnlySize(string path)
        {
            long size = 0;
            try {
                foreach (string file in Directory.GetFiles(path, "*.exe", SearchOption.AllDirectories))
                    size += new FileInfo(file).Length;
            } catch { }
            return size;
        }

        private void UpdateSpaceInfo()
        {
            if (_games.Count == 0) return;

            long totalLibBytes = _games.Sum(g => g.SizeBytes);
            TotalLibSizeText.Text = $"{(totalLibBytes / 1024.0 / 1024.0 / 1024.0):F2} GB";

            var drives = _games.Select(g => System.IO.Path.GetPathRoot(g.RootPath)).Distinct().ToList();
            var infoLines = new List<string>();
            
            long totalUsedOnAllDrivesByGames = 0;
            long totalCapacityOnAllRelevantDrives = 0;

            foreach (var dRoot in drives)
            {
                if (string.IsNullOrEmpty(dRoot)) continue;
                try {
                    DriveInfo di = new DriveInfo(dRoot);
                    long gameSizeOnDrive = _games.Where(g => System.IO.Path.GetPathRoot(g.RootPath) == dRoot).Sum(g => g.SizeBytes);
                    infoLines.Add($"{dRoot} {FormatSize(di.AvailableFreeSpace)} Free / {FormatSize(di.TotalSize)} Total (Games: {FormatSize(gameSizeOnDrive)})");
                    
                    totalUsedOnAllDrivesByGames += gameSizeOnDrive;
                    totalCapacityOnAllRelevantDrives += di.TotalSize;
                } catch { }
            }

            DriveInfoText.Text = string.Join(" | ", infoLines);
            
            double usedPct = (totalUsedOnAllDrivesByGames * 100.0 / (totalCapacityOnAllRelevantDrives > 0 ? totalCapacityOnAllRelevantDrives : 1));
            UsedPercentageText.Text = $"{usedPct:F1}% Used by Games";
            
            StorageProgressBar.Maximum = totalCapacityOnAllRelevantDrives;
            StorageProgressBar.Value = totalUsedOnAllDrivesByGames;
            StorageProgressBar.Foreground = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
        }

        private string FormatSize(long bytes) => $"{(bytes / 1024.0 / 1024.0 / 1024.0):F1}GB";

        private void DrawPieChart()
        {
            StoragePieCanvas.Children.Clear();
            StorageChartLegend.Children.Clear();

            if (_games.Count == 0) return;

            long totalSize = _games.Sum(g => g.SizeBytes);
            if (totalSize == 0) return;

            double currentAngle = 0;
            var driveGroups = _games.GroupBy(g => System.IO.Path.GetPathRoot(g.RootPath))
                                   .Select(g => new { Drive = g.Key, Games = g.ToList(), Size = g.Sum(x => x.SizeBytes) })
                                   .ToList();

            var driveBaseColors = new List<Color> {
                Color.FromArgb(255, 60, 120, 216),  
                Color.FromArgb(255, 230, 70, 70),   
                Color.FromArgb(255, 60, 180, 75),   
                Color.FromArgb(255, 255, 225, 25),  
                Color.FromArgb(255, 145, 30, 180),  
                Color.FromArgb(255, 70, 240, 240)   
            };

            double centerX = StoragePieCanvas.Width / 2;
            double centerY = StoragePieCanvas.Height / 2;
            double radius = Math.Min(centerX, centerY) - 10;

            foreach (var group in driveGroups)
            {
                Color baseColor = driveBaseColors[driveGroups.IndexOf(group) % driveBaseColors.Count];
                AddLegendItem($"{group.Drive} ({FormatSize(group.Size)})", baseColor, group.Size);

                for (int i = 0; i < group.Games.Count; i++)
                {
                    var game = group.Games[i];
                    double sweepAngle = (game.SizeBytes / (double)totalSize) * 360.0;
                    if (sweepAngle < 0.2) sweepAngle = 0.2;

                    
                    double factor = 1.0;
                    if (group.Games.Count > 1)
                        factor = 0.75 + (0.5 * (i / (double)(group.Games.Count - 1)));

                    Color shadedColor = Color.FromArgb(
                        baseColor.A,
                        (byte)Math.Clamp(baseColor.R * factor, 0, 255),
                        (byte)Math.Clamp(baseColor.G * factor, 0, 255),
                        (byte)Math.Clamp(baseColor.B * factor, 0, 255)
                    );

                    var slice = CreateGameSlice(centerX, centerY, radius, currentAngle, sweepAngle, shadedColor, game);
                    StoragePieCanvas.Children.Add(slice);

                    if (group.Games.Count > 1)
                    {
                        var sep = CreateSeparatorLine(centerX, centerY, radius, currentAngle);
                        StoragePieCanvas.Children.Add(sep);
                    }

                    currentAngle += sweepAngle;
                }
            }
        }

        private Microsoft.UI.Xaml.Shapes.Path CreateGameSlice(double cx, double cy, double r, double startAngle, double sweepAngle, Color color, GameFolder game)
        {
            var path = new Microsoft.UI.Xaml.Shapes.Path();
            var geometry = new PathGeometry();
            var figure = new PathFigure { IsClosed = true, IsFilled = true };
            figure.StartPoint = new Point(cx, cy);

            double startRad = startAngle * Math.PI / 180.0;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180.0;

            var line1 = new LineSegment { Point = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad)) };
            var arc = new ArcSegment {
                Point = new Point(cx + r * Math.Cos(endRad), cy + r * Math.Sin(endRad)),
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = sweepAngle > 180
            };

            figure.Segments.Add(line1);
            figure.Segments.Add(arc);
            geometry.Figures.Add(figure);
            path.Data = geometry;
            path.Fill = new SolidColorBrush(color);
            path.Stroke = new SolidColorBrush(Microsoft.UI.Colors.Black);
            path.StrokeThickness = 0.5;

            
            path.PointerEntered += (s, e) => {
                path.Opacity = 0.8;
                ToolTipService.SetToolTip(path, $"{game.Title}\n{FormatSize(game.SizeBytes)}");
            };
            path.PointerExited += (s, e) => path.Opacity = 1.0;
            
            path.ContextRequested += (s, e) => {
                _selectedGameFromChart = game;
                e.TryGetPosition(path, out Point pos);
                _pieContextFlyout.ShowAt(path, pos);
            };

            return path;
        }

        private Line CreateSeparatorLine(double cx, double cy, double r, double angle)
        {
            double rad = angle * Math.PI / 180.0;
            return new Line {
                X1 = cx, Y1 = cy,
                X2 = cx + r * Math.Cos(rad),
                Y2 = cy + r * Math.Sin(rad),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                StrokeThickness = 1,
                Opacity = 0.3
            };
        }

        private void AddLegendItem(string text, Color color, long size)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = new SolidColorBrush(color), RadiusX = 2, RadiusY = 2 });
            panel.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            StorageChartLegend.Children.Add(panel);
        }

        private void OpenFolderMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGameFromChart != null)
                System.Diagnostics.Process.Start("explorer.exe", _selectedGameFromChart.RootPath);
        }

        private async void UninstallMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGameFromChart == null) return;
            var gf = _selectedGameFromChart;

            var dialog = new ContentDialog
            {
                Title = $"Uninstall \"{gf.Title}\"?",
                Content = $"This will permanently delete the game files at:\n{gf.RootPath}\n\nThis cannot be undone.",
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            try {
                Logger.Log($"[Cleanup] Uninstalling from pie chart: {gf.Title}");
                
                
                var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == gf.RootPath);
                if (meta != null && meta.IsSteamIntegrated)
                {
                    string? exe = ScannerEngine.FindExecutable(gf.RootPath);
                    if (!string.IsNullOrEmpty(exe))
                    {
                        await SteamManager.RemoveGameFromSteam(meta.Title, exe);
                    }
                }

                if (Directory.Exists(gf.RootPath))
                    Directory.Delete(gf.RootPath, true);

                if (meta != null) GlobalSettings.Library.Remove(meta);
                GlobalSettings.Save();
                
                await RefreshAll();
            } catch (Exception ex) {
                Logger.LogError("CleanupUninstall", ex);
            }
        }

        private void CleanRedistBtn_Click(object sender, RoutedEventArgs e)
        {
            try {
                int count = 0;
                long totalSaved = 0;
                foreach (var item in _redistItems)
                {
                    if (Directory.Exists(item.FolderPath))
                    {
                        
                        RedistService.UpdateRedistManifest(item.FolderPath);
                        
                        var exes = Directory.GetFiles(item.FolderPath, "*.exe", SearchOption.AllDirectories)
                            .Concat(Directory.GetFiles(item.FolderPath, "*.msi", SearchOption.AllDirectories));

                        
                        foreach (var exe in exes)
                        {
                            try {
                                var fi = new FileInfo(exe!);
                                totalSaved += fi.Length;
                                File.Delete(exe!);
                                count++;
                            } catch { }
                        }
                    }
                }
                
                Logger.Log($"[Cleanup] Cleaned {count} redist installers, saved {(totalSaved / 1024.0 / 1024.0):F1} MB. Manifests created.");
                _ = RefreshAll();
            } catch (Exception ex) {
                Logger.LogError("RedistCleanup", ex);
            }
        }
    }

    public class RedistCleanupItem
    {
        public string GameTitle { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public long SizeBytes { get; set; }
        public string SizeStr => $"{(SizeBytes / 1024.0 / 1024.0):F1} MB";
    }
}
