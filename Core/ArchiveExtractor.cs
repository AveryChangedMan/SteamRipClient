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
        public static async Task<bool> ExtractAsync(
            string archivePath,
            string outputDir,
            Action<double>? onProgress = null,
            Action<string>? onStatus = null,
            bool deleteAfter = false)
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
                } catch (TaskCanceledException) { }
            });
            var method = GlobalSettings.PreferredExtractionMethod ?? ExtractionMethod.WinRAR;
            var winrar = FindWinRar();
            var sevenZip = FindSevenZip();
            bool success = false;
            try {
                if (method == ExtractionMethod.WinRAR && winrar != null)
                {
                    success = await ExtractWithWinRarAsync(winrar, archivePath, outputDir, onProgress, onStatus, cts.Token);
                }
                else if (method == ExtractionMethod.SevenZip && sevenZip != null)
                {
                    success = await ExtractWithSevenZipAsync(sevenZip, archivePath, outputDir, onProgress, onStatus, cts.Token);
                }
                else if (method == ExtractionMethod.Windows)
                {
                    onStatus?.Invoke("📦 Extracting with Windows Explorer...");
                    success = await ExtractWithDotNetZipAsync(archivePath, outputDir, onProgress, cts.Token);
                }
                else
                {
                    if (winrar != null) success = await ExtractWithWinRarAsync(winrar, archivePath, outputDir, onProgress, onStatus, cts.Token);
                    else if (sevenZip != null) success = await ExtractWithSevenZipAsync(sevenZip, archivePath, outputDir, onProgress, onStatus, cts.Token);
                    else success = await ExtractWithDotNetZipAsync(archivePath, outputDir, onProgress, cts.Token);
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
            System.Threading.CancellationToken ct)
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
                        onProgress?.Invoke(pct);
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
            System.Threading.CancellationToken ct)
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
                        onProgress?.Invoke(pct);
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
        private static async Task<bool> ExtractWithDotNetZipAsync(
            string zipPath,
            string outputDir,
            Action<double>? onProgress,
            System.Threading.CancellationToken ct)
        {
            try {
                return await Task.Run(() =>
                {
                    using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
                    double total = archive.Entries.Count;
                    double done = 0;
                    foreach (var entry in archive.Entries)
                    {
                        if (ct.IsCancellationRequested) return false;
                        string dest = Path.GetFullPath(Path.Combine(outputDir, entry.FullName));
                        if (!dest.StartsWith(Path.GetFullPath(outputDir))) continue; 
                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        {
                            Directory.CreateDirectory(dest);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            using var entryStream = entry.Open();
                            using var fileStream  = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                            entryStream.CopyTo(fileStream);
                        }
                        done++;
                        onProgress?.Invoke(done / total * 100.0);
                    }
                    return true;
                });
            } catch (Exception ex) {
                Logger.LogError("DotNetZip.Extract", ex);
                return false;
            }
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

