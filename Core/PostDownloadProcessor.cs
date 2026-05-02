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
                string extractRoot = Path.GetPathRoot(Path.GetFullPath(extractToDir))!;
                string archiveRoot = Path.GetPathRoot(Path.GetFullPath(archivePath))!;
                var drive = new DriveInfo(extractRoot);
                var archiveFile = new FileInfo(archivePath);

                long archiveSize = archiveFile.Exists ? archiveFile.Length : 0;

                bool isSameDrive = extractRoot.Equals(archiveRoot, StringComparison.OrdinalIgnoreCase);
                double multiplier = isSameDrive ? 1.2 : 2.2;

                long requiredSpace = (long)(archiveSize * multiplier);
                long freeSpace = drive.AvailableFreeSpace;
                string ruleName = isSameDrive ? "120% local rule" : "220% cross-drive rule";

                if (freeSpace <= 5L * 1024 * 1024 * 1024)
                {
                    Logger.Log($"[PostDownload] 🛑 CRITICAL HALT: Drive {drive.Name} has less than 5GB free. Aborting.");
                    onStatus?.Invoke("🛑 HALTED: Less than 5GB of free space. Extraction cancelled.");
                    return null;
                }

                if (freeSpace < requiredSpace)
                {
                    Logger.Log($"[PostDownload] ⚠️ SPACE WARNING: {gameTitle} needs ~{requiredSpace / 1024 / 1024}MB ({ruleName}), but only {freeSpace / 1024 / 1024}MB is free.");
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

            var session = new DownloadSessionMetadata
            {
                GameTitle = gameTitle,
                SteamRipUrl = steamRipPageUrl,
                ArchivePath = archivePath,
                Version = version,
                ImageUrl = imageUrl,
                SafeFolderName = safeFolderName,
                DownloadDir = extractToDir
            };
            await session.SaveAsync();

            Logger.Log($"[PostDownload] Archive: {archivePath}");
            Logger.Log($"[PostDownload] ExtractTo: {extractToDir}");

            var gameExtractionDir = Path.Combine(extractToDir, safeFolderName);
            Directory.CreateDirectory(gameExtractionDir);

            Logger.Log($"[PostDownload] Game extraction dir (Real Name): {gameExtractionDir}");
            onStatus?.Invoke("📦 Extracting archive...");

            bool shouldMap = GlobalSettings.AlwaysCreateRarMap;
            if (!shouldMap && confirmMap != null) shouldMap = await confirmMap(gameTitle);

            bool extracted = await ArchiveExtractor.ExtractAsync(
                archivePath,
                gameExtractionDir,
                onProgress: onProgress,
                onStatus: onStatus,
                deleteAfter: false,
                gameTitle: gameTitle
            );

            if (!extracted)
            {
                Logger.Log("[PostDownload] Extraction failed.");
                onStatus?.Invoke("❌ Extraction failed — game not added to library.");
                return null;
            }

            var extractedGameDir = FindExtractedGameDir(gameExtractionDir, archivePath) ?? gameExtractionDir;

            if (shouldMap)
            {
                onStatus?.Invoke("🗺️ Generating archive map...");
                await RepairService.GenerateArchiveMapAsync(archivePath, gameExtractionDir);
            }

            if (GlobalSettings.AutoDeleteArchive)
            {
                try {

                    var extractedFiles = Directory.GetFiles(gameExtractionDir, "*", SearchOption.AllDirectories);
                    if (extractedFiles.Length == 0)
                    {
                        Logger.Log($"[PostDownload] ❌ SAFETY BLOCK: Extraction folder '{gameExtractionDir}' is EMPTY. Aborting archive deletion to prevent data loss.");
                        onStatus?.Invoke("⚠️ Extraction seems to have failed. Archives preserved.");
                    }
                    else
                    {
                        onStatus?.Invoke("🧹 Cleaning up archives...");
                        if (File.Exists(archivePath)) File.Delete(archivePath);

                        var otherRars = Directory.GetFiles(gameExtractionDir, "*.rar", SearchOption.AllDirectories);
                        foreach (var rar in otherRars)
                        {
                            try { File.Delete(rar); } catch { }
                        }
                        Logger.Log($"[PostDownload] Cleaned up archives ({extractedFiles.Length} files extracted successfully).");
                    }
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

            await RepairService.RunInitialHashAsync(gameExtractionDir, extractedGameDir);

            onStatus?.Invoke("🖼 Copying cover image...");
            string? localImagePath = null;
            if (!string.IsNullOrEmpty(imageUrl))
            {

                await ScannerEngine.DownloadGameImageAsync(imageUrl, gameExtractionDir);
                var expectedPath = Path.Combine(gameExtractionDir, "folder.jpg");
                if (File.Exists(expectedPath))
                {
                    localImagePath = expectedPath;
                    Logger.Log($"[PostDownload] Cover image saved to Main folder: {localImagePath}");
                }
            }

            onStatus?.Invoke("🔍 Scanning game folder...");
            var scanDirs = new List<string> { gameExtractionDir };
            var scanResults = await ScannerEngine.ScanDirectoriesAsync(scanDirs, progress: null);

            var gameFolder = scanResults
                .FirstOrDefault(g => g.RootPath.Equals(gameExtractionDir, StringComparison.OrdinalIgnoreCase))
                ?? scanResults.FirstOrDefault();

            if (gameFolder == null)
            {

                Logger.Log("[PostDownload] ScanEngine found no result — creating bare GameFolder entry.");
                gameFolder = new GameFolder
                {
                    Title = cleanTitle,
                    RootPath = gameExtractionDir,
                    GameSubFolderPath = extractedGameDir,
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

                if (!existingGame.LocalPath.Equals(gameExtractionDir, StringComparison.OrdinalIgnoreCase))
                {

                    if (GlobalSettings.GameConfigs.TryGetValue(existingGame.LocalPath, out var oldConfig))
                    {
                        GlobalSettings.GameConfigs[gameExtractionDir] = oldConfig;
                        GlobalSettings.GameConfigs.Remove(existingGame.LocalPath);
                        Logger.Log("[PostDownload] Migrated GameConfig to new path.");
                    }

                    if (GlobalSettings.GamePageLinks.ContainsKey(existingGame.LocalPath))
                        GlobalSettings.GamePageLinks.Remove(existingGame.LocalPath);
                }

                GlobalSettings.Library.Remove(existingGame);
            }

            GlobalSettings.Library.RemoveAll(m => m.LocalPath.Equals(gameExtractionDir, StringComparison.OrdinalIgnoreCase));

            long folderSize = 0;
            try {
                folderSize = Directory.GetFiles(gameExtractionDir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase))
                    .Sum(f => new FileInfo(f).Length);
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
                SizeBytes   = folderSize,
                LocalImagePath = localImagePath ?? ""
            });

            if (!string.IsNullOrEmpty(steamRipPageUrl))
            {
                string normPath = gameExtractionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                GlobalSettings.GamePageLinks[normPath] = steamRipPageUrl;
            }

            if (!GlobalSettings.ScanDirectories.Contains(extractToDir, StringComparer.OrdinalIgnoreCase))
                GlobalSettings.ScanDirectories.Add(extractToDir);

            GlobalSettings.Save();

            Logger.Log($"[PostDownload] ✅ '{gameTitle}' added to library at: {gameExtractionDir}");

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

            var allDirs = Directory.GetDirectories(extractToDir)
                .Where(d => !Path.GetFileName(d).Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(d).Equals("Redist", StringComparison.OrdinalIgnoreCase))
                .ToList();
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