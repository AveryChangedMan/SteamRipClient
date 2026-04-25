using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{
    
    
    
    
    
    
    
    
    
    public static class PostDownloadProcessor
    {
        
        
        
        
        
        
        
        
        
        
        
        
        public static async Task<GameFolder?> RunAsync(
            string archivePath,
            string extractToDir,
            string gameTitle,
            string steamRipPageUrl,
            string imageUrl,
            string version,
            Action<string>? onStatus = null,
            Action<double>? onProgress = null,
            Func<long, long, Task<bool>>? confirmSpace = null,
            Func<string, Task<bool>>? confirmMap = null)
        {
            Logger.Log($"[PostDownload] Starting post-download for: {gameTitle}");
            
            
            string cleanTitle = ScannerEngine.CleanTitle(gameTitle);
            string safeFolderName = System.Text.RegularExpressions.Regex.Replace(
                cleanTitle, @"[\\/:*?""<>|]", "").Replace(" ", "_").Trim('_');
            string gameHash   = GlobalSettings.GetGameHash(cleanTitle, steamRipPageUrl);
            GlobalSettings.MemoryTable[gameHash] = cleanTitle;

            Logger.Log($"[PostDownload] Hash ID: {gameHash}");

            
            try {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(extractToDir))!);
                var archiveFile = new FileInfo(archivePath);
                long archiveSize = archiveFile.Exists ? archiveFile.Length : 0;
                long requiredSpace = (long)(archiveSize * 2.2);
                long freeSpace = drive.AvailableFreeSpace;

                if (freeSpace <= 5L * 1024 * 1024 * 1024)
                {
                    Logger.Log($"[PostDownload] 🛑 CRITICAL HALT: Drive {drive.Name} has less than 5GB free. Aborting.");
                    onStatus?.Invoke("🛑 HALTED: Less than 5GB of free space. Extraction cancelled.");
                    return null;
                }

                if (freeSpace < requiredSpace)
                {
                    Logger.Log($"[PostDownload] ⚠️ SPACE WARNING: {gameTitle} needs ~{requiredSpace / 1024 / 1024}MB (220% rule), but only {freeSpace / 1024 / 1024}MB is free.");
                    if (confirmSpace != null)
                    {
                        bool proceed = await confirmSpace(freeSpace, requiredSpace);
                        if (!proceed)
                        {
                            onStatus?.Invoke("❌ Installation cancelled by user due to space warning.");
                            return null;
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Log($"[PostDownload] Space check failed: {ex.Message}");
            }

            Logger.Log($"[PostDownload] Archive: {archivePath}");
            Logger.Log($"[PostDownload] ExtractTo: {extractToDir}");

            
            
            var gameExtractionDir = Path.Combine(extractToDir, safeFolderName);

            Directory.CreateDirectory(gameExtractionDir);

            Logger.Log($"[PostDownload] Game extraction dir (Real Name): {gameExtractionDir}");
            onStatus?.Invoke("📦 Extracting archive...");

            
            bool shouldMap = true;
            if (confirmMap != null) shouldMap = await confirmMap(gameTitle);

            bool extracted = await ArchiveExtractor.ExtractAsync(
                archivePath,
                gameExtractionDir,
                onProgress: onProgress,
                onStatus: onStatus,
                deleteAfter: false 
            );

            if (!extracted)
            {
                Logger.Log("[PostDownload] Extraction failed.");
                onStatus?.Invoke("❌ Extraction failed — game not added to library.");
                return null;
            }

            
            var extractedGameDir = FindExtractedGameDir(gameExtractionDir, archivePath) ?? gameExtractionDir;

            
            onStatus?.Invoke("🗺️ Generating archive map...");
            await RepairService.GenerateArchiveMapAsync(archivePath, gameExtractionDir);

            
            if (GlobalSettings.AutoDeleteArchive)
            {
                try {
                    onStatus?.Invoke("🧹 Cleaning up archives...");
                    if (File.Exists(archivePath)) File.Delete(archivePath);
                    
                    
                    var otherRars = Directory.GetFiles(gameExtractionDir, "*.rar", SearchOption.AllDirectories);
                    foreach (var rar in otherRars)
                    {
                        try { File.Delete(rar); } catch { }
                    }
                    Logger.Log($"[PostDownload] Cleaned up archives.");
                } catch (Exception ex) {
                    Logger.Log($"[PostDownload] Failed to cleanup archive: {ex.Message}");
                }
            }

            
            var redistPath = Path.Combine(gameExtractionDir, "_CommonRedist");
            if (Directory.Exists(redistPath))
            {
                RedistService.UpdateRedistManifest(redistPath);
            }

            var secondaryRedistPath = Path.Combine(extractedGameDir, "_CommonRedist");
            if (Directory.Exists(secondaryRedistPath) && secondaryRedistPath != redistPath)
            {
                RedistService.UpdateRedistManifest(secondaryRedistPath);
            }

            Logger.Log($"[PostDownload] Extracted game content dir: {extractedGameDir}");
            onStatus?.Invoke("📚 Finalizing library registration...");

            
            RepairService.TriggerManualBackup(gameExtractionDir, extractedGameDir, false);

            
            onStatus?.Invoke("🖼 Copying cover image...");
            string? localImagePath = null;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                await ScannerEngine.DownloadGameImageAsync(imageUrl, gameExtractionDir);
                var expectedPath = Path.Combine(gameExtractionDir, "folder.jpg");
                if (File.Exists(expectedPath))
                {
                    localImagePath = expectedPath;
                    Logger.Log($"[PostDownload] Cover image saved to: {localImagePath}");
                }
            }

            
            onStatus?.Invoke("🔍 Scanning game folder...");
            var scanDirs = new List<string> { Path.GetDirectoryName(extractedGameDir) ?? extractedGameDir };
            var scanResults = await ScannerEngine.ScanDirectoriesAsync(scanDirs, progress: null);

            
            var gameFolder = scanResults
                .FirstOrDefault(g => g.RootPath.Equals(extractedGameDir, StringComparison.OrdinalIgnoreCase))
                ?? scanResults.FirstOrDefault(); 

            if (gameFolder == null)
            {
                
                Logger.Log("[PostDownload] ScanEngine found no result — creating bare GameFolder entry.");
                gameFolder = new GameFolder
                {
                    Title = cleanTitle,
                    RootPath = extractedGameDir,
                    Version = version,
                    Url = steamRipPageUrl,
                    ImageUrl = imageUrl,
                    LocalImagePath = localImagePath,
                    SteamAppId = ScannerEngine.ResolveAppIdFromFiles(extractedGameDir)
                };
            }
            else
            {
                
                gameFolder.Title = cleanTitle;
                gameFolder.Version = version;
                gameFolder.Url = steamRipPageUrl;
                gameFolder.ImageUrl = imageUrl;
            }

            
            onStatus?.Invoke("📚 Adding to library...");

            
            var existingGame = GlobalSettings.Library.FirstOrDefault(m => 
                !string.IsNullOrEmpty(m.Url) && m.Url.Equals(steamRipPageUrl, StringComparison.OrdinalIgnoreCase));

            if (existingGame != null)
            {
                Logger.Log($"[PostDownload] Found existing library entry for this game. Migrating from {existingGame.LocalPath}");
                
                
                if (!existingGame.LocalPath.Equals(extractedGameDir, StringComparison.OrdinalIgnoreCase))
                {
                    
                    if (GlobalSettings.GameConfigs.TryGetValue(existingGame.LocalPath, out var oldConfig))
                    {
                        GlobalSettings.GameConfigs[extractedGameDir] = oldConfig;
                        GlobalSettings.GameConfigs.Remove(existingGame.LocalPath);
                        Logger.Log("[PostDownload] Migrated GameConfig to new path.");
                    }

                    
                    if (GlobalSettings.GamePageLinks.ContainsKey(existingGame.LocalPath))
                        GlobalSettings.GamePageLinks.Remove(existingGame.LocalPath);
                }

                
                GlobalSettings.Library.Remove(existingGame);
            }

            
            GlobalSettings.Library.RemoveAll(m => m.LocalPath.Equals(extractedGameDir, StringComparison.OrdinalIgnoreCase));

            
            long folderSize = 0;
            try {
                folderSize = Directory.GetFiles(extractedGameDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            } catch { }

            GlobalSettings.Library.Add(new GameMetadata
            {
                Title       = cleanTitle,
                Url         = steamRipPageUrl,
                ImageUrl    = imageUrl,
                LocalPath   = gameExtractionDir,
                Version     = version,
                Hash        = gameHash,
                DownloadDate = DateTime.Now,
                SizeBytes   = folderSize
            });

            
            if (!GlobalSettings.GameConfigs.ContainsKey(gameExtractionDir))
            {
                var config = new GameConfig();
                config.ManualExePath = ScannerEngine.FindExecutable(gameExtractionDir, extractedGameDir);
                if (!string.IsNullOrEmpty(config.ManualExePath))
                {
                    config.WorkingDir = Path.GetDirectoryName(config.ManualExePath) ?? gameExtractionDir;
                    Logger.Log($"[PostDownload] Auto-detected EXE: {config.ManualExePath}");
                }
                GlobalSettings.GameConfigs[gameExtractionDir] = config;
            }

            
            if (!string.IsNullOrEmpty(steamRipPageUrl))
                GlobalSettings.GamePageLinks[gameExtractionDir] = steamRipPageUrl;

            
            if (!GlobalSettings.ScanDirectories.Contains(extractToDir, StringComparer.OrdinalIgnoreCase))
                GlobalSettings.ScanDirectories.Add(extractToDir);

            GlobalSettings.Save();

            Logger.Log($"[PostDownload] ✅ '{gameTitle}' added to library at: {extractedGameDir}");
            
            onStatus?.Invoke($"✅ '{gameTitle}' added to library!");

            return gameFolder;
        }

        

        
        
        
        
        private static string? FindExtractedGameDir(string extractToDir, string archivePath)
        {
            if (!Directory.Exists(extractToDir)) return null;

            var archiveBaseName = Path.GetFileNameWithoutExtension(archivePath);
            
            var cleanName = System.Text.RegularExpressions.Regex.Replace(
                archiveBaseName, @"\.part\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            
            var exactMatch = Path.Combine(extractToDir, cleanName);
            if (Directory.Exists(exactMatch)) return exactMatch;

            
            var allDirs = Directory.GetDirectories(extractToDir);
            var steamRipDir = allDirs.FirstOrDefault(d =>
                Path.GetFileName(d).Contains("SteamRIP", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(d).Contains("SteamRip", StringComparison.OrdinalIgnoreCase));
            if (steamRipDir != null) return steamRipDir;

            
            foreach (var dir in allDirs)
            {
                try {
                    if (Directory.GetFiles(dir, "STEAMRIP*.url", SearchOption.TopDirectoryOnly).Any())
                        return dir;
                } catch { }
            }

            
            var newestDir = allDirs
                .Select(d => new DirectoryInfo(d))
                .Where(di => di.Exists)
                .OrderByDescending(di => di.CreationTime)
                .FirstOrDefault();
            if (newestDir != null) return newestDir.FullName;

            
            var exeFiles = Directory.GetFiles(extractToDir, "*.exe", SearchOption.TopDirectoryOnly);
            if (exeFiles.Any()) return extractToDir;

            return null;
        }
    }
}
