using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{

    public static class ArchiveExtractor
    {

        private static readonly string[] UnrarPaths =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps", "unrar.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps", "unrar64.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unrar.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unrar64.exe"),
        };

        private static readonly Regex ProgressRegex = new Regex(@"\s*(\d{1,3})%", RegexOptions.Compiled);

        public static string? FindUnRarDLL()
        {
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps", "UnRAR64.dll");
            if (File.Exists(localPath)) return localPath;
            return null;
        }

        public static string? FindUnRarCLI()
        {
            foreach (var path in UnrarPaths)
                if (File.Exists(path)) return path;
            return null;
        }

        public static string? FindWinRar()
        {
            string[] paths =
            [
                @"C:\Program Files\WinRAR\WinRAR.exe",
                @"C:\Program Files (x86)\WinRAR\WinRAR.exe",
                @"C:\Program Files\WinRAR\Rar.exe",
                @"C:\Program Files (x86)\WinRAR\Rar.exe",
            ];
            foreach (var p in paths) if (File.Exists(p)) return p;
            return null;
        }

        public static string? FindSevenZip()
        {
            string[] paths =
            [
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            ];
            foreach (var p in paths) if (File.Exists(p)) return p;
            return null;
        }

        public static async Task<bool> ExtractAsync(
            string archivePath,
            string outputDir,
            Action<double>? onProgress = null,
            Action<string>? onStatus = null,
            bool deleteAfter = false,
            string? gameTitle = null,
            Action<string, long, long>? onFileProgress = null)
        {
            if (!File.Exists(archivePath))
            {
                Logger.Log($"[Extract] File not found: {archivePath}");
                return false;
            }

            Directory.CreateDirectory(outputDir);
            Logger.Log($"[Extract] Starting extraction of: {archivePath} → {outputDir}");

            using var cts = new System.Threading.CancellationTokenSource();
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputDir))!);
            const long criticalLimit = 10L * 1024 * 1024 * 1024;

            var spaceMonitorTask = Task.Run(async () => {
                try {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (drive.AvailableFreeSpace < criticalLimit)
                        {
                            Logger.Log($"[Extract] CRITICAL SPACE LIMIT HIT (<10GB) on {drive.Name}. Cancelling extraction.");
                            onStatus?.Invoke("❌ CRITICAL SPACE LIMIT: Extraction cancelled for system safety.");
                            cts.Cancel();
                            break;
                        }
                        await Task.Delay(5000, cts.Token);
                    }
                } catch { }
            });

            var unrarCli = FindUnRarCLI();
            var unrarDll = FindUnRarDLL();

            bool success = false;
            try {

                if (unrarCli != null)
                {
                    Logger.Log($"[Extract] Using bundled UnRAR CLI: {unrarCli}");
                    success = await ExtractWithUnRarCLIAsync(unrarCli, archivePath, outputDir, onProgress, onStatus, cts.Token, gameTitle, onFileProgress);
                }

                if (!success && unrarDll != null)
                {
                    Logger.Log("[Extract] CLI failed or missing — falling back to UnRAR DLL engine.");
                    success = await UnrarEngine.ExtractAsync(archivePath, outputDir, onProgress, onStatus, cts.Token, onFileProgress);
                    if (success) { onProgress?.Invoke(100); onStatus?.Invoke("✅ Extraction complete"); }
                }

                if (!success)
                {
                    Logger.Log("[Extract] CRITICAL: No extraction tools found in Redist/Deps.");
                    onStatus?.Invoke("❌ No extraction tool found. Please reinstall the app.");
                }
            } catch (OperationCanceledException) {
                success = false;
            } finally {
                cts.Cancel();
                await spaceMonitorTask;
            }

            if (success && deleteAfter)
                DeleteArchiveSet(archivePath, onStatus);

            return success;
        }

        private static async Task<bool> ExtractWithUnRarCLIAsync(
            string unrarPath, string archivePath, string outputDir,
            Action<double>? onProgress, Action<string>? onStatus, CancellationToken ct, string? gameTitle,
            Action<string, long, long>? onFileProgress = null)
        {
            try
            {
                string args = $"x -y -o+ -p- \"{archivePath}\" \"{outputDir.TrimEnd('\\')}\\\"";
                Logger.Log($"[Extract] UnRAR CLI command: \"{unrarPath}\" {args}");

                var unrarFileRegex = new Regex(@"^Extracting\s+(.+?)\s+(OK)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                string _lastFileName = "";

                bool result = await RunProcessWithProgressAsync(unrarPath, args, (pct) => {
                    onProgress?.Invoke(pct);
                    if (gameTitle != null)
                    {
                        var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.Title == gameTitle);
                        if (gf != null) { gf.IsInProgress = true; gf.ProgressPercentage = pct; }
                    }
                }, onStatus, ct, lineHandler: line => {
                    var fnMatch = unrarFileRegex.Match(line.Trim());
                    if (fnMatch.Success)
                    {
                        _lastFileName = Path.GetFileName(fnMatch.Groups[1].Value.Trim());
                        if (!string.IsNullOrEmpty(_lastFileName))
                            onFileProgress?.Invoke(_lastFileName, 0, 0);
                    }
                });

                return result;
            }
            catch (Exception ex) { Logger.LogError("UnRarCLI.Extract", ex); return false; }
        }

        private static async Task<bool> RunProcessWithProgressAsync(
            string exePath, string args, Action<double> onPct, Action<string>? onStatus, CancellationToken ct,
            Action<string>? lineHandler = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var readOutputTask = Task.Run(async () =>
            {
                while (true)
                {
                    if (ct.IsCancellationRequested) { try { process.Kill(true); } catch { } break; }
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line == null) break;

                    lineHandler?.Invoke(line);

                    var m = ProgressRegex.Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, out double pct))
                        onPct(pct);
                }
            });

            await Task.WhenAll(readOutputTask, process.WaitForExitAsync(ct));
            return process.ExitCode == 0;
        }

        private static void DeleteArchiveSet(string primaryArchive, Action<string>? onStatus)
        {
            try {
                var dir = Path.GetDirectoryName(primaryArchive)!;
                var name = Path.GetFileNameWithoutExtension(primaryArchive);
                var ext  = Path.GetExtension(primaryArchive).ToLowerInvariant();

                var filesToDelete = new List<string>();

                if (ext == ".rar")
                {

                    filesToDelete.AddRange(Directory.GetFiles(dir, $"{StripPartSuffix(name)}.part*.rar"));
                    filesToDelete.AddRange(Directory.GetFiles(dir, $"{StripPartSuffix(name)}.r[0-9][0-9]"));
                    filesToDelete.AddRange(Directory.GetFiles(dir, $"{StripPartSuffix(name)}.rar"));
                    if (!filesToDelete.Contains(primaryArchive))
                        filesToDelete.Add(primaryArchive);
                }
                else
                {
                    filesToDelete.Add(primaryArchive);
                }

                foreach (var f in filesToDelete)
                {
                    try { File.Delete(f); Logger.Log($"[Extract] Deleted archive: {f}"); }
                    catch (Exception ex) { Logger.Log($"[Extract] Could not delete {f}: {ex.Message}"); }
                }

                onStatus?.Invoke($"🗑 Archive deleted ({filesToDelete.Count} file(s))");
            }
            catch (Exception ex) { Logger.LogError("DeleteArchiveSet", ex); }
        }

        private static string StripPartSuffix(string nameWithoutExt)
        {

            return Regex.Replace(nameWithoutExt, @"\.part\d+$", "", RegexOptions.IgnoreCase);
        }

        public static List<string> FindArchiveParts(string primaryPath)
        {
            var parts = new List<string>();
            try {
                var dir  = Path.GetDirectoryName(primaryPath)!;
                var name = StripPartSuffix(Path.GetFileNameWithoutExtension(primaryPath));
                parts.AddRange(Directory.GetFiles(dir, $"{name}.part*.rar"));
                parts.AddRange(Directory.GetFiles(dir, $"{name}.r[0-9][0-9]"));
                parts.AddRange(Directory.GetFiles(dir, $"{name}.rar"));
                if (parts.Count == 0 && File.Exists(primaryPath)) parts.Add(primaryPath);
            } catch { }
            return parts;
        }
    }
}