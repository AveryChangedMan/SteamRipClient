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

        private static readonly string[] WinRarPaths = {
            @"C:\Program Files\WinRAR\WinRAR.exe",
            @"C:\Program Files (x86)\WinRAR\WinRAR.exe",
            @"C:\Program Files\WinRAR\Rar.exe",
            @"C:\Program Files (x86)\WinRAR\Rar.exe",
        };

        private static readonly string[] SevenZipPaths = {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
        };

        private static readonly string[] UnrarPaths = {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps", "unrar.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps", "unrar64.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unrar.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unrar64.exe"),
        };

        private static readonly Regex ProgressRegex = new Regex(@"\s*(\d{1,3})%", RegexOptions.Compiled);

        public static string? FindWinRar()
        {
            foreach (var path in WinRarPaths)
                if (File.Exists(path)) return path;
            return null;
        }

        public static string? FindSevenZip()
        {
            foreach (var path in SevenZipPaths)
                if (File.Exists(path)) return path;
            return null;
        }

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

        public static async Task<bool> ExtractAsync(
            string archivePath,
            string outputDir,
            Action<double>? onProgress = null,
            Action<string>? onStatus = null,
            bool deleteAfter = false,
            string? gameTitle = null)
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

            var method = GlobalSettings.PreferredExtractionMethod;
            var unrarDll = FindUnRarDLL();
            var unrarCli = FindUnRarCLI();

            bool success = false;

            var startTime = DateTime.Now;
            try {

                if (method == ExtractionMethod.WinRAR)
                {
                    string? winrarPath = FindWinRar();
                    if (winrarPath != null)
                    {
                        Logger.Log($"[Extract] User specified System WinRAR: {winrarPath}");
                        success = await ExtractWithWinRarAsync(winrarPath, archivePath, outputDir, onProgress, onStatus, cts.Token, gameTitle);
                    }
                    else
                        Logger.Log("[Extract] User specified WinRAR, but it wasn't found. Falling back to built-in UnRAR.");
                }
                else if (method == ExtractionMethod.SevenZip)
                {
                    string? sevenZipPath = FindSevenZip();
                    if (sevenZipPath != null)
                    {
                        Logger.Log($"[Extract] User specified System 7-Zip: {sevenZipPath}");
                        success = await ExtractWithSevenZipAsync(sevenZipPath, archivePath, outputDir, onProgress, onStatus, cts.Token, gameTitle);
                    }
                    else
                        Logger.Log("[Extract] User specified 7-Zip, but it wasn't found. Falling back to built-in UnRAR.");
                }

                if (!success)
                {
                    if (unrarCli != null)
                    {
                        Logger.Log($"[Extract] Using Primary UnRAR CLI: {unrarCli}");
                        success = await ExtractWithUnRarCLIAsync(unrarCli, archivePath, outputDir, onProgress, onStatus, cts.Token, gameTitle);
                    }
                    else if (unrarDll != null)
                    {
                        Logger.Log($"[Extract] Last resort: UnRAR DLL...");
                        success = await UnrarEngine.ExtractAsync(archivePath, outputDir, onProgress, onStatus, cts.Token);
                    }
                    else
                    {
                        Logger.Log("[ArchiveExtractor] CRITICAL: No extraction tools found! Please check Redist/Deps.");
                        onStatus?.Invoke("❌ No extraction tool available.");
                    }
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

        private static async Task<bool> ExtractWithSevenZipAsync(
            string sevenZipPath,
            string archivePath,
            string outputDir,
            Action<double>? onProgress,
            Action<string>? onStatus,
            System.Threading.CancellationToken ct,
            string? gameTitle = null)
        {

            string args = $"x \"{archivePath}\" -o\"{outputDir.TrimEnd('\\')}\" -y";

            Logger.Log($"[Extract] 7-Zip command: \"{sevenZipPath}\" {args}");
            onStatus?.Invoke("📦 Extracting with 7-Zip...");

            try {
                var psi = new ProcessStartInfo(sevenZipPath, args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var tcs = new TaskCompletionSource<int>();
                proc.Exited += (s, e) => tcs.TrySetResult(proc.ExitCode);

                proc.OutputDataReceived += (s, e) => {
                    if (e.Data == null) return;

                    var match = ProgressRegex.Match(e.Data);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double pct))
                    {
                        onProgress?.Invoke(pct);

                        if (!string.IsNullOrEmpty(gameTitle))
                        {
                            var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.Title.Equals(gameTitle, StringComparison.OrdinalIgnoreCase));
                            if (gf != null)
                            {
                                gf.IsInProgress = true;
                                gf.ProgressPhase = "Installing...";
                                gf.ProgressPercentage = pct;
                            }
                        }
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();

                using (ct.Register(() => { try { proc.Kill(); } catch { } }))
                {
                    int exitCode = await tcs.Task;
                    if (exitCode == 0)
                    {
                        onProgress?.Invoke(100);
                        onStatus?.Invoke("✅ Extraction complete");
                        return true;
                    }
                    return false;
                }
            } catch (Exception ex) {
                Logger.LogError("SevenZip.Extract", ex);
                return false;
            }
        }

        private static async Task<bool> ExtractWithWinRarAsync(
            string winrarPath,
            string archivePath,
            string outputDir,
            Action<double>? onProgress,
            Action<string>? onStatus,
            System.Threading.CancellationToken ct,
            string? gameTitle = null)
        {

            string args = $"x -y -o+ -ibck -p- \"{archivePath}\" \"{outputDir.TrimEnd('\\')}\\\"";

            Logger.Log($"[Extract] WinRAR command: \"{winrarPath}\" {args}");
            onStatus?.Invoke("📦 Extracting with WinRAR...");

            try {
                var psi = new ProcessStartInfo(winrarPath, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(archivePath) ?? ""
                };

                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var tcs = new TaskCompletionSource<int>();
                proc.Exited += (s, e) => tcs.TrySetResult(proc.ExitCode);

                proc.OutputDataReceived += (s, e) => {
                    if (e.Data == null) return;
                    Logger.Log($"[WinRAR] {e.Data}");
                    var match = ProgressRegex.Match(e.Data);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double pct))
                    {
                        onProgress?.Invoke(pct);

                        if (!string.IsNullOrEmpty(gameTitle))
                        {
                            var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.Title.Equals(gameTitle, StringComparison.OrdinalIgnoreCase));
                            if (gf != null)
                            {
                                gf.IsInProgress = true;
                                gf.ProgressPhase = "Installing...";
                                gf.ProgressPercentage = pct;
                            }
                        }
                    }
                };
                proc.ErrorDataReceived += (s, e) => {
                    if (e.Data != null) Logger.Log($"[WinRAR-ERR] {e.Data}");
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using (ct.Register(() => { try { proc.Kill(); } catch { } }))
                {
                    int exitCode = await tcs.Task;
                    Logger.Log($"[Extract] WinRAR exit code: {exitCode}");

                    if (ct.IsCancellationRequested) return false;

                    if (exitCode <= 1)
                    {
                        onProgress?.Invoke(100);
                        onStatus?.Invoke("✅ Extraction complete");
                        return true;
                    }

                    Logger.Log($"[Extract] WinRAR failed with exit code {exitCode}");
                    onStatus?.Invoke($"❌ WinRAR error (code {exitCode})");
                    return false;
                }
            }
            catch (Exception ex) {
                Logger.LogError("WinRAR.Extract", ex);
                onStatus?.Invoke($"❌ WinRAR exception: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> ExtractWithUnRarDLLAsync(
            string unrarDllPath,
            string archivePath,
            string outputDir,
            Action<double>? onProgress,
            Action<string>? onStatus,
            System.Threading.CancellationToken ct,
            string? gameTitle = null)
        {
            Logger.Log($"[Extract] Using UnRAR DLL engine for: {archivePath}");
            onStatus?.Invoke("📦 Decompressing with High-Precision Engine...");

            try {
                bool result = await UnrarEngine.ExtractAsync(archivePath, outputDir, pct => {
                    onProgress?.Invoke(pct);

                    if (!string.IsNullOrEmpty(gameTitle))
                    {
                        var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.Title.Equals(gameTitle, StringComparison.OrdinalIgnoreCase));
                        if (gf != null)
                        {
                            gf.IsInProgress = true;
                            gf.ProgressPhase = "Installing...";
                            gf.ProgressPercentage = pct;
                        }
                    }
                }, onStatus, ct);

                if (result)
                {
                    onProgress?.Invoke(100);
                    onStatus?.Invoke("✅ Extraction complete");
                }
                return result;
            } catch (Exception ex) {
                Logger.LogError("UnRarDLL.Extract", ex);
                return false;
            }
        }

        private static async Task<bool> ExtractWithUnRarCLIAsync(
            string unrarPath, string archivePath, string outputDir,
            Action<double>? onProgress, Action<string>? onStatus, CancellationToken ct, string? gameTitle)
        {
            try
            {

                string args = $"x -y -o+ -p- \"{archivePath}\" \"{outputDir.TrimEnd('\\')}\\\"";
                Logger.Log($"[Extract] UnRAR CLI command: \"{unrarPath}\" {args}");

                bool result = await RunProcessWithProgressAsync(unrarPath, args, (pct) => {
                    onProgress?.Invoke(pct);
                    if (gameTitle != null)
                    {
                        var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.Title == gameTitle);
                        if (gf != null) { gf.IsInProgress = true; gf.ProgressPercentage = pct; }
                    }
                }, onStatus, ct);

                return result;
            }
            catch (Exception ex) { Logger.LogError("UnRarCLI.Extract", ex); return false; }
        }

        private static async Task<bool> RunProcessWithProgressAsync(
            string exePath, string args, Action<double> onPct, Action<string>? onStatus, CancellationToken ct)
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