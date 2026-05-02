using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;
using SharpCompress.Common;
using System.Text;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace SteamRipApp.Core
{
    public class ArchiveMap
    {
        public string ArchiveName { get; set; } = "";
        public string PrefixBase64 { get; set; } = "";
        public long PrefixSize { get; set; }
        public bool IsSolid { get; set; }
        public List<ArchiveEntry> Entries { get; set; } = [];
        public long TotalArchiveSize { get; set; }
    }

    public class ArchiveEntry
    {
        public string Path { get; set; } = "";
        public long HeaderOffset { get; set; }
        public long DataOffset { get; set; }
        public long PackedSize { get; set; }
        public long Size { get; set; }
        public string RarVersion { get; set; } = "RAR5";
        public uint Crc32 { get; set; }
        public byte Method { get; set; }
    }

    public class FileSkeleton
    {
        public List<SkeletonFile> Files { get; set; } = [];
    }

    public class SkeletonFile
    {
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Size { get; set; }
        public long LastWriteTime { get; set; }
        public long FileTime { get; set; }
        public string FileId { get; set; } = "";
    }

    public class RepairReport
    {
        public List<string> MissingFiles { get; set; } = [];
        public List<string> CorruptedFiles { get; set; } = [];
        public List<string> AddedFiles { get; set; } = [];
        public bool HasIntegrityIssues => MissingFiles.Count > 0 || CorruptedFiles.Count > 0;
        public bool HasIssues => HasIntegrityIssues || AddedFiles.Count > 0;
        public bool MetadataMissing { get; set; }
        public string? Error { get; set; }
        public long EstimatedDownloadBytes { get; set; }
    }

    public static partial class RepairService
    {
        public const string MapFileName = ".rip_map.json";
        public const string SkeletonFileName = ".rip_skeleton.json";
        public const string VersionFileName = ".rip_version.json";
        private static readonly HttpClient _httpClient;
        private const string CommonUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private static CancellationTokenSource? _hashingCts;
        private static bool _isHashingActive = false;
        private static readonly Dictionary<string, (string ContentPath, bool Force, string? SnapshotName)> _pendingHashing = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

        public static void StartBackgroundHashing()
        {
            if (_isHashingActive) return;
            _isHashingActive = true;
            _hashingCts = new CancellationTokenSource();
            _ = Task.Run(() => HashingLoop(_hashingCts.Token));
            Logger.Log("[Repair] Background hashing loop started.");
        }

        public static void StopBackgroundHashing()
        {
            _hashingCts?.Cancel();
            _hashingCts?.Dispose();
            _hashingCts = null;
            _isHashingActive = false;
            Logger.Log("[Repair] Background hashing loop stopped.");
        }

        public static void TriggerManualBackup(string storagePath, string contentPath, string? snapshotName = null, bool force = true)
        {

            string targetDir = contentPath;
            if (File.Exists(contentPath))
            {
                targetDir = Path.GetDirectoryName(contentPath) ?? contentPath;
            }

            Logger.Log($"[Repair] Queueing Manual Backup for: {targetDir} (Snapshot: {snapshotName ?? "Default"})");

            lock (_pendingHashing)
            {
                _pendingHashing[storagePath] = (targetDir, force, snapshotName);
            }

            StartBackgroundHashing();
        }

        public static async Task RunInitialHashAsync(string storagePath, string contentPath)
        {
            Logger.Log($"[Repair] Running Initial Hash for: {contentPath}");
            lock (_pendingHashing)
            {
                _pendingHashing.Remove(storagePath);
            }
            await RunHashingProcessAsync(storagePath, contentPath, CancellationToken.None);

            WriteVersionFile(storagePath);

            try
            {

                string mapPath = Path.Combine(storagePath, MapFileName);
                if (File.Exists(mapPath))
                {
                    var map = JsonSerializer.Deserialize<ArchiveMap>(File.ReadAllText(mapPath));
                    if (map != null && !string.IsNullOrEmpty(map.ArchiveName))
                    {

                        string archivePath = Path.Combine(storagePath, map.ArchiveName);
                        if (!File.Exists(archivePath))
                        {
                            string parent = Path.GetDirectoryName(storagePath.TrimEnd(Path.DirectorySeparatorChar))!;
                            archivePath = Path.Combine(parent, map.ArchiveName);
                        }

                        var session = DownloadSessionMetadata.Load(archivePath);
                        session?.Delete();
                    }
                }
            }
            catch { }
        }

        public enum VersionStatus { Current, NeedsPatch, Incompatible, NoRipFiles, NotDownloadedWithApp }

        private static uint ComputeCRC32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                crc ^= b;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0) crc = (crc >> 1) ^ 0xEDB88320;
                    else crc >>= 1;
                }
            }
            return ~crc;
        }

        private static readonly char[] _invalidPathChars = Path.GetInvalidPathChars().Concat(new[] { '\0' }).Distinct().ToArray();

        private static string SanitizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "unknown_file";

            if (path.Length > 1024) path = path.Substring(0, 1024);
            string cleaned = path.Replace("\0", "").Trim();
            foreach (var c in _invalidPathChars) cleaned = cleaned.Replace(c, '_');
            return cleaned.Replace('/', '\\');
        }

        public static string ReadVersionFile(string storagePath)
        {
            try
            {
                string versionPath = Path.Combine(storagePath, VersionFileName);
                if (!File.Exists(versionPath)) return "";

                var json = File.ReadAllText(versionPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("GameVersion", out var gv))
                    return gv.GetString() ?? "";

                return json.Split('\n')[0].Trim();
            }
            catch { return ""; }
        }

        public static VersionStatus CheckVersionFile(string storagePath)
        {
            string versionPath = Path.Combine(storagePath, VersionFileName);
            if (!File.Exists(versionPath))
            {
                bool hasMap = File.Exists(Path.Combine(storagePath, MapFileName));
                return hasMap ? VersionStatus.Incompatible : VersionStatus.NotDownloadedWithApp;
            }

            try
            {
                var json = File.ReadAllText(versionPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("RepairLogicVersion", out var prop))
                {
                    string fileVer = prop.GetString() ?? "1.0.0";
                    string currentVer = GlobalSettings.RepairLogicVersion;

                    int[] ParseSafe(string v) => v.Split('.')
                        .Select(s => int.TryParse(s, out int i) ? i : 0)
                        .ToArray();

                    var fileParts = ParseSafe(fileVer);
                    var currParts = ParseSafe(currentVer);

                    if (fileParts.Length > 0 && currParts.Length > 0 && fileParts[0] != currParts[0])
                        return VersionStatus.Incompatible;

                    if (fileParts.Length > 1 && currParts.Length > 1 && fileParts[1] < currParts[1])
                        return VersionStatus.NeedsPatch;

                    return VersionStatus.Current;
                }
            }
            catch { }

            return VersionStatus.NoRipFiles;
        }

        public static void WriteVersionFile(string storagePath, string? gameVersion = null)
        {
            try
            {
                var info = new
                {
                    RepairLogicVersion = GlobalSettings.RepairLogicVersion,
                    AppVersion = GlobalSettings.AppVersion,
                    GameVersion = gameVersion ?? "",
                    GeneratedAt = DateTime.UtcNow.ToString("o"),
                    MachineName = Environment.MachineName
                };
                string versionPath = Path.Combine(storagePath, VersionFileName);
                File.WriteAllText(versionPath, System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Logger.Log($"[Repair] Version stamp written to: {versionPath} (Game: {gameVersion ?? "N/A"})");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Repair] Could not write version stamp: {ex.Message}");
            }
        }

        public static void StopHashingForGame(string storagePath)
        {
            lock (_pendingHashing)
            {
                _pendingHashing.Remove(storagePath);
            }
            try
            {
                string skelPath = Path.Combine(storagePath, SkeletonFileName);
                if (File.Exists(skelPath)) File.Delete(skelPath);
                Logger.Log($"[Repair] Integrity state reset for: {storagePath}");
            }
            catch (Exception ex)
            {
                Logger.LogError("ResetBackup", ex);
            }
        }

        private static async Task HashingLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string? storage = null;
                string? content = null;
                string? snapshot = null;
                bool force = false;
                lock (_pendingHashing)
                {
                    var first = _pendingHashing.FirstOrDefault();
                    if (first.Key != null)
                    {
                        storage = first.Key;
                        content = first.Value.ContentPath;
                        force = first.Value.Force;
                        snapshot = first.Value.SnapshotName;
                        _pendingHashing.Remove(storage);
                    }
                }

                if (storage != null && content != null && Directory.Exists(content))
                {
                    string suffix = string.IsNullOrEmpty(snapshot) || snapshot == "Official Rip Map" ? "" : snapshot + ".";
                    string skelFile = suffix + SkeletonFileName;
                    string datFile = suffix + "rip_skeleton.dat";
                    string skelPath = Path.Combine(storage, skelFile);
                    string datPath = Path.Combine(storage, datFile);

                    VerifyMetadataSync(skelPath, datPath);

                    if (!force && File.Exists(skelPath))
                    {
                        Logger.Log($"[Repair-Hashing] Skipping auto-hashing for {storage} (Snapshot {snapshot ?? "Default"} already exists)");
                    }
                    else
                    {
                        await RunHashingProcessAsync(storage, content, ct, snapshot);
                    }
                }
                else
                {
                    await Task.Delay(5000, ct);
                }
            }
        }

        static RepairService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", CommonUserAgent);
            _httpClient.DefaultRequestHeaders.ConnectionClose = false;

            try
            {

                string localRedistDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp", "Redist");
                Directory.CreateDirectory(localRedistDir);

                string bundleRedistDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist");

                if (Directory.Exists(bundleRedistDir))
                {
                    foreach (var file in Directory.GetFiles(bundleRedistDir))
                    {
                        string fileName = Path.GetFileName(file);
                        string localPath = Path.Combine(localRedistDir, fileName);

                        bool needsCopy = !File.Exists(localPath) ||
                                        File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(localPath);

                        if (needsCopy)
                        {
                            File.Copy(file, localPath, true);
                            Logger.Log($"[Repair] Deployed {fileName} to local storage.");
                        }
                    }
                }

                string localDllPath = Path.Combine(localRedistDir, "NativeHash.dll");

                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                    typeof(RepairService).Assembly,
                    (libraryName, assembly, searchPath) => {
                        if (libraryName == "NativeHash.dll")
                        {
                            if (File.Exists(localDllPath))
                                return System.Runtime.InteropServices.NativeLibrary.Load(localDllPath);
                        }
                        return IntPtr.Zero;
                    }
                );

                var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
                _httpClient = new HttpClient(handler);
                _httpClient.DefaultRequestHeaders.Add("User-Agent", CommonUserAgent);
                _httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
                _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
                _httpClient.Timeout = TimeSpan.FromSeconds(60);
            }
            catch (Exception ex)
            {
                Logger.LogError("NativeInit", ex);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("NativeHash.dll", CharSet = CharSet.Unicode)]
        private static extern int XXH64_HashFile(string filePath, out ulong outHash);

        private static string GetFileFingerprint(string path, out long fileTime)
        {
            fileTime = 0;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (GetFileInformationByHandle(fs.SafeFileHandle.DangerousGetHandle(), out var info))
                {
                    fileTime = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
                    return $"{info.VolumeSerialNumber}-{info.FileIndexHigh}-{info.FileIndexLow}";
                }
            }
            catch { }
            return "";
        }

        public static async Task CreateCustomSnapshotAsync(string path, string name)
        {
            await RunHashingProcessAsync(path, path, CancellationToken.None, name);
        }

        private static async Task RunHashingProcessAsync(string storagePath, string contentPath, CancellationToken ct, string? snapshotName = null)
        {
            try
            {
                string suffix = string.IsNullOrEmpty(snapshotName) || snapshotName == "Official Rip Map" ? "" : snapshotName + ".";
                string skeletonPath = Path.Combine(storagePath, suffix + SkeletonFileName);
                string datPath = Path.Combine(storagePath, suffix + "rip_skeleton.dat");

                Logger.Log($"[Repair-Hashing] 🔍 STARTING INTEGRITY SCAN: {contentPath} (Snapshot: {snapshotName ?? "Default"})");

                var existingSkeleton = new FileSkeleton();
                if (File.Exists(skeletonPath))
                {
                    try {
                        var json = File.ReadAllText(skeletonPath);
                        existingSkeleton = JsonSerializer.Deserialize<FileSkeleton>(json) ?? new FileSkeleton();
                    } catch { }
                }

                var allFiles = Directory.GetFiles(contentPath, "*", SearchOption.AllDirectories)
                                     .Where(f => !Path.GetFileName(f).StartsWith(".rip_", StringComparison.OrdinalIgnoreCase) && !f.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase))
                                     .ToList();

                int total = allFiles.Count;
                int processed = 0;
                int skipped = 0;
                var concurrentFiles = new ConcurrentBag<SkeletonFile>();

                var smallFiles = allFiles.Where(f => new FileInfo(f).Length < 10 * 1024 * 1024).ToList();
                var largeFiles = allFiles.Where(f => new FileInfo(f).Length >= 10 * 1024 * 1024).ToList();

                bool useMulti = GlobalSettings.IsMultiThreadedHashingEnabled;
                bool isHdd = IsMechanicalDrive(contentPath);
                int maxDegree = useMulti ? Math.Max(1, Environment.ProcessorCount / 2) : 1;

                if (isHdd)
                {
                    Logger.Log($"[Repair-Hashing] 💿 HDD Detected. Throttling to sequential hashing to prevent thrashing.");
                    maxDegree = 1;
                }

                Logger.Log($"[Repair-Hashing] Found {total} files ({largeFiles.Count} large, {smallFiles.Count} small). Mode: {(maxDegree > 1 ? "Multi" : "Sequential")}");

                var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = ct };

                await Task.Run(() =>
                {

                    Parallel.ForEach(largeFiles, options, (file) =>
                    {
                        if (ct.IsCancellationRequested) return;
                        ProcessSingleFile(file, contentPath, existingSkeleton, concurrentFiles, ref processed, ref skipped, total, useMulti, ct);
                    });

                    var groups = new List<List<string>>();
                    var currentGroup = new List<string>();
                    long currentGroupSize = 0;
                    foreach (var f in smallFiles)
                    {
                        long size = new FileInfo(f).Length;
                        if (currentGroupSize + size > 10 * 1024 * 1024 && currentGroup.Count > 0)
                        {
                            groups.Add(currentGroup);
                            currentGroup = new List<string>();
                            currentGroupSize = 0;
                        }
                        currentGroup.Add(f);
                        currentGroupSize += size;
                    }
                    if (currentGroup.Count > 0) groups.Add(currentGroup);

                    Parallel.ForEach(groups, options, (group) =>
                    {
                        foreach (var file in group)
                        {
                            if (ct.IsCancellationRequested) return;
                            ProcessSingleFile(file, contentPath, existingSkeleton, concurrentFiles, ref processed, ref skipped, total, useMulti, ct);
                        }
                    });
                }, ct);

                if (!ct.IsCancellationRequested)
                {
                    var finalFiles = concurrentFiles.OrderBy(f => f.Path).ToList();
                    var skeleton = new FileSkeleton { Files = finalFiles };

                    File.WriteAllText(skeletonPath, JsonSerializer.Serialize(skeleton, new JsonSerializerOptions { WriteIndented = true }));

                    using (var ms = new MemoryStream())
                    using (var writer = new BinaryWriter(ms))
                    {
                        writer.Write(finalFiles.Count);
                        foreach (var f in finalFiles)
                        {
                            writer.Write(f.Path);
                            writer.Write(f.Hash);
                            writer.Write(f.Size);
                            writer.Write(f.FileTime);
                            writer.Write(f.FileId);
                        }
                        File.WriteAllBytes(datPath, ms.ToArray());
                    }

                    Logger.Log($"[Repair-Hashing] ✅ SCAN COMPLETE: (Processed {total}, Skipped {skipped})");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogError("Repair-Hashing", ex);
            }
            finally
            {
                GlobalSettings.HashingProgress = null;
                GlobalSettings.HashingProgressValue = 0;

                var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.RootPath.Equals(storagePath, StringComparison.OrdinalIgnoreCase));
                if (gf != null)
                {
                    gf.IsInProgress = false;
                    gf.ProgressPhase = "";
                    gf.ProgressPercentage = 100;
                }
            }
        }

        private static void ProcessSingleFile(string file, string contentPath, FileSkeleton existingSkeleton, ConcurrentBag<SkeletonFile> concurrentFiles, ref int processed, ref int skipped, int total, bool useMulti, CancellationToken ct)
        {
            string rel = Path.GetRelativePath(contentPath, file);
            var fileInfo = new FileInfo(file);
            long fileSize = fileInfo.Length;
            string fileId = GetFileFingerprint(file, out long fileTime);
            long lastWrite = fileInfo.LastWriteTimeUtc.Ticks;

            int currentIdx = Interlocked.Increment(ref processed);

            var existing = existingSkeleton.Files.FirstOrDefault(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.Size == fileSize && existing.FileTime == fileTime && existing.FileId == fileId)
            {
                concurrentFiles.Add(existing);
                Interlocked.Increment(ref skipped);
                return;
            }

            GlobalSettings.HashingProgress = $"Hashing: {Path.GetFileName(file)} ({currentIdx}/{total})";
            GlobalSettings.HashingProgressValue = (currentIdx * 100.0) / total;

            var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.RootPath.Equals(contentPath, StringComparison.OrdinalIgnoreCase));
            if (gf != null)
            {
                gf.IsInProgress = true;
                gf.ProgressPhase = "Verifying...";
                gf.ProgressPercentage = GlobalSettings.HashingProgressValue;
                gf.ProgressDetails = $"{currentIdx} / {total} files";
            }

            string hash = "";
            try
            {

                if (GlobalSettings.HashingSpeedCapMB <= 0)
                {
                    int result = XXH64_HashFile(file, out ulong hashValue);
                    if (result == 0)
                    {
                        hash = hashValue.ToString("x16");
                    }
                }

                if (string.IsNullOrEmpty(hash))
                {
                    int bufferSize = (fileSize > 100 * 1024 * 1024) ? 20 * 1024 * 1024 : 10 * 1024 * 1024;
                    byte[] buffer = new byte[bufferSize];
                    var hasher = new System.IO.Hashing.XxHash64();

                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.SequentialScan))
                    {
                        int bytesRead;
                        long bytesProcessedSinceSleep = 0;
                        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (ct.IsCancellationRequested) return;
                            hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            bytesProcessedSinceSleep += bytesRead;

                            if (GlobalSettings.HashingSpeedCapMB > 0)
                            {
                                long capBytes = (long)GlobalSettings.HashingSpeedCapMB * 1024 * 1024;
                                double sleepMs = (bytesRead * 1000.0) / capBytes;
                                if (sleepMs > 1) Thread.Sleep((int)sleepMs);
                            }
                            else if (!useMulti && bytesProcessedSinceSleep >= 50 * 1024 * 1024)
                            {
                                Thread.Sleep(1);
                                bytesProcessedSinceSleep = 0;
                            }
                        }
                    }
                    hash = Convert.ToHexStringLower(hasher.GetCurrentHash());
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Repair-Hashing] ❌ Error hashing {rel}: {ex.Message}");
                return;
            }

            if (!string.IsNullOrEmpty(hash))
            {
                concurrentFiles.Add(new SkeletonFile
                {
                    Path = rel,
                    Hash = hash,
                    Size = fileSize,
                    LastWriteTime = lastWrite,
                    FileTime = fileTime,
                    FileId = fileId
                });
            }
        }

        private static string ComputeXXHash64(string filePath)
        {
            try
            {

                if (GlobalSettings.HashingSpeedCapMB <= 0)
                {
                    int result = XXH64_HashFile(filePath, out ulong hashValue);
                    if (result == 0) return hashValue.ToString("x16");
                }

                var hasher = new System.IO.Hashing.XxHash64();
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int bytesRead;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                    }
                }
                return Convert.ToHexStringLower(hasher.GetCurrentHash());
            }
            catch { return "error"; }
        }

        private static void VerifyMetadataSync(string skelPath, string datPath)
        {
            try
            {
                bool skelExists = File.Exists(skelPath);
                bool datExists = File.Exists(datPath);

                if (skelExists && !datExists)
                {
                    Logger.Log("[Repair-Sync] Recreating .dat from .json reference...");
                    var skel = JsonSerializer.Deserialize<FileSkeleton>(File.ReadAllText(skelPath));
                    if (skel != null) SaveDatFile(datPath, skel.Files);
                }
                else if (!skelExists && datExists)
                {
                    Logger.Log("[Repair-Sync] Recreating .json from .dat reference...");
                    var files = LoadDatFile(datPath);
                    if (files != null) File.WriteAllText(skelPath, JsonSerializer.Serialize(new FileSkeleton { Files = files }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex) { Logger.LogError("MetadataSync", ex); }
        }

        private static void SaveDatFile(string path, List<SkeletonFile> files)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs))
            {
                writer.Write(files.Count);
                foreach (var f in files)
                {
                    writer.Write(f.Path);
                    writer.Write(f.Hash);
                    writer.Write(f.Size);
                    writer.Write(f.FileTime);
                    writer.Write(f.FileId);
                }
            }
        }

        private static bool IsMechanicalDrive(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                if (string.IsNullOrEmpty(root)) return false;

                var drive = new DriveInfo(root);
                if (drive.DriveType != DriveType.Fixed) return false;

                return GlobalSettings.IsHddModeEnabled;
            }
            catch { return false; }
        }

        private static List<SkeletonFile>? LoadDatFile(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    int count = reader.ReadInt32();
                    var list = new List<SkeletonFile>(count);
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(new SkeletonFile {
                            Path = reader.ReadString(),
                            Hash = reader.ReadString(),
                            Size = reader.ReadInt64(),
                            FileTime = reader.ReadInt64(),
                            FileId = reader.ReadString()
                        });
                    }
                    return list;
                }
            }
            catch { return null; }
        }

        public static async Task<ArchiveMap?> GenerateArchiveMapAsync(string archivePath, string storagePath)
        {
            try
            {
                Logger.Log($"[Repair-Mapping] 🗺️ STARTING PRECISION HYBRID MAP GENERATION for: {Path.GetFileName(archivePath)}");
                var map = new ArchiveMap { ArchiveName = Path.GetFileName(archivePath) };
                long tempOffset = 0;

                using (var rawStream = File.OpenRead(archivePath))
                {

                    byte[] headData = new byte[8192];
                    int headRead = await rawStream.ReadAsync(headData, 0, headData.Length);
                    rawStream.Position = 0;

                    long firstFileOffset = 0;
                    var prefix = TrimRarPrefix(headData.Take(headRead).ToArray(), out firstFileOffset);
                    map.PrefixBase64 = Convert.ToBase64String(prefix);
                    map.PrefixSize = firstFileOffset;
                    map.TotalArchiveSize = rawStream.Length;
                    Logger.Log($"[Repair-Mapping] Captured Prefix: {map.PrefixSize} bytes. First File @ {firstFileOffset}");

                    tempOffset = firstFileOffset;
                }

                List<SharpCompress.Common.Rar.RarEntry> sharpEntries = new List<SharpCompress.Common.Rar.RarEntry>();
                using (var archive = SharpCompress.Archives.Rar.RarArchive.Open(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory) sharpEntries.Add(entry);
                    }
                }

                using (var rawStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    rawStream.Position = tempOffset;
                    BinaryReader br = new BinaryReader(rawStream);

                    int entryIndex = 0;
                    while (rawStream.Position < rawStream.Length && entryIndex < sharpEntries.Count)
                    {
                        long headerStartPos = rawStream.Position;

                        if (rawStream.Position + 4 > rawStream.Length) break;
                        uint crc = br.ReadUInt32();

                        long headerSize = ReadVInt(br);
                        long headerContentStart = rawStream.Position;
                        long headerEndPos = headerContentStart + headerSize;

                        long headerType = ReadVInt(br);
                        long headerFlags = ReadVInt(br);

                        if (headerType == 2)
                        {
                            long extraSize = (headerFlags & 0x0001) != 0 ? ReadVInt(br) : 0;
                            long dataSize = (headerFlags & 0x0002) != 0 ? ReadVInt(br) : 0;

                            var sharpEntry = sharpEntries[entryIndex++];

                            map.Entries.Add(new ArchiveEntry
                            {
                                Path = (sharpEntry.Key ?? "Unknown").Replace("/", "\\"),
                                HeaderOffset = headerStartPos,
                                DataOffset = headerEndPos,
                                PackedSize = dataSize,
                                Size = sharpEntry.Size,
                                RarVersion = "RAR5",
                                Crc32 = (uint)sharpEntry.Crc
                            });

                            Logger.Log($"[Repair-Mapping] Mapped: {sharpEntry.Key} (Header: {headerStartPos}, Data: {headerEndPos}, Packed: {dataSize})");

                            rawStream.Position = headerEndPos + dataSize;
                        }
                        else
                        {

                            rawStream.Position = headerEndPos;
                        }
                    }

                    if (map.Entries.Count > 0)
                    {
                        Directory.CreateDirectory(storagePath);
                        string mapPath = Path.Combine(storagePath, MapFileName);
                        File.WriteAllText(mapPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
                        Logger.Log($"[Repair-Mapping] ✅ PRECISION MAP COMPLETE: {mapPath} ({map.Entries.Count} entries)");

                        WriteVersionFile(storagePath);
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Repair-Mapping", ex);
            }
            return null;
        }

        public static async Task<RepairReport> AnalyzeGameAsync(string storagePath, string contentPath, string? snapshotName = null, Action<string, double>? onProgress = null, bool metadataOnly = false, bool earlyExit = false)
        {
            var report = new RepairReport();
            try
            {

                if (File.Exists(contentPath)) contentPath = Path.GetDirectoryName(contentPath) ?? contentPath;
                string suffix = string.IsNullOrEmpty(snapshotName) || snapshotName == "Official Rip Map" ? "" : snapshotName + ".";
                string mapPath = Path.Combine(storagePath, MapFileName);
                string skelPath = Path.Combine(storagePath, suffix + SkeletonFileName);

                if (!File.Exists(mapPath))
                {
                    Logger.Log($"[Repair-Analyze] No map found for {contentPath}. Verification skipped.");
                    return report;
                }

                var map = JsonSerializer.Deserialize<ArchiveMap>(File.ReadAllText(mapPath));
                FileSkeleton? skeleton = null;
                if (File.Exists(skelPath))
                {
                    skeleton = JsonSerializer.Deserialize<FileSkeleton>(File.ReadAllText(skelPath));
                }
                else
                {
                    report.MetadataMissing = true;
                    Logger.Log($"[Repair-Analyze] Metadata missing for {contentPath}. Corruption check will be skipped until re-hashed.");
                }

                var entries = map!.Entries;
                var filteredEntries = entries.Where(e => !e.Path.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase) && !e.Path.StartsWith(".rip_", StringComparison.OrdinalIgnoreCase)).ToList();

                string? commonRoot = null;
                if (filteredEntries.Count > 0)
                {

                    var rootCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in filteredEntries)
                    {
                        var parts = e.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1)
                        {
                            string root = parts[0] + "\\";
                            rootCounts[root] = rootCounts.GetValueOrDefault(root) + 1;
                        }
                    }

                    if (rootCounts.Count > 0)
                    {
                        var mostFrequent = rootCounts.OrderByDescending(kv => kv.Value).First();
                        if (mostFrequent.Value > filteredEntries.Count / 2)
                        {
                            commonRoot = mostFrequent.Key;
                            Logger.Log($"[Repair] Detected common root wrapper '{commonRoot}'. It will be used to scope the scan.");
                        }
                    }
                }

                if (commonRoot != null)
                {
                    int before = filteredEntries.Count;
                    filteredEntries = filteredEntries.Where(e => e.Path.Replace("/", "\\").StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase)).ToList();
                    Logger.Log($"[Repair-Analyze] Scoped scan to '{commonRoot}'. Ignoring {before - filteredEntries.Count} files outside the game directory.");
                }

                int total = filteredEntries.Count;
                int processed = 0;
                var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = CancellationToken.None };

                await Task.Run(() =>
                {
                    Parallel.ForEach(filteredEntries, options, (entry, state) =>
                    {
                        string relPath = entry.Path;
                        string fullPath = Path.Combine(contentPath, relPath);

                        if (!File.Exists(fullPath) && commonRoot != null && relPath.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            string stripped = relPath[commonRoot.Length..];
                            string strippedFullPath = Path.Combine(contentPath, stripped);
                            if (File.Exists(strippedFullPath))
                            {
                                relPath = stripped;
                                fullPath = strippedFullPath;
                            }
                        }

                        if (!File.Exists(fullPath))
                        {

                            string fileName = Path.GetFileName(relPath);
                            try
                            {
                                var found = Directory.GetFiles(contentPath, fileName, SearchOption.AllDirectories)
                                             .FirstOrDefault(f => !f.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase));

                                if (found != null)
                                {
                                    fullPath = found;
                                    string newRel = Path.GetRelativePath(contentPath, found);
                                    Logger.Log($"[Repair-Analyze] Self-Healed: Found shifted file '{fileName}' at {newRel}");
                                    relPath = newRel;
                                }
                            }
                            catch { }
                        }

                        if (!File.Exists(fullPath))
                        {
                            lock (report.MissingFiles) report.MissingFiles.Add(entry.Path);
                            if (earlyExit) { state.Stop(); return; }
                            return;
                        }

                        if (skeleton != null)
                        {
                            var skelFile = skeleton.Files.FirstOrDefault(f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase) || f.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));

                            if (skelFile != null)
                            {

                                long fileSize = 0;
                                long fileTime = 0;
                                string fileId = "";

                                try
                                {
                                    var fileInfo = new FileInfo(fullPath.Replace(@"\\?\", ""));
                                    fileSize = fileInfo.Length;
                                    fileId = GetFileFingerprint(fullPath, out fileTime);

                                    if (skelFile.Size == fileSize && skelFile.FileTime == fileTime && skelFile.FileId == fileId)
                                    {
                                        return;
                                    }

                                    if (metadataOnly)
                                    {
                                        lock (report.CorruptedFiles) report.CorruptedFiles.Add(entry.Path);
                                        if (earlyExit) { state.Stop(); return; }
                                        return;
                                    }
                                }
                                catch { }

                                try
                                {
                                    int result = XXH64_HashFile(fullPath, out ulong hashValue);
                                    if (result == 0)
                                    {
                                        string currentHash = hashValue.ToString("x16");
                                        if (currentHash != skelFile.Hash)
                                        {
                                            lock (report.CorruptedFiles) report.CorruptedFiles.Add(entry.Path);
                                            if (earlyExit) { state.Stop(); return; }
                                        }
                                        else
                                        {
                                            lock (skeleton)
                                            {
                                                skelFile.Size = fileSize;
                                                skelFile.FileTime = fileTime;
                                                skelFile.FileId = fileId;
                                                skelFile.LastWriteTime = DateTime.UtcNow.Ticks;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        int current = Interlocked.Increment(ref processed);
                        if (current % 10 == 0 || current == total)
                        {
                            onProgress?.Invoke($"[{current}/{total}] Verifying: {Path.GetFileName(relPath)}", (current * 100.0) / total);
                        }
                    });
                });

                if (skeleton != null)
                {

                    File.WriteAllText(skelPath, JsonSerializer.Serialize(skeleton, _jsonOptions));
                }

                HashSet<string> knownRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries) knownRelPaths.Add(e.Path.Replace("/", "\\"));
                if (skeleton != null)
                {
                    foreach (var sf in skeleton.Files) knownRelPaths.Add(sf.Path.Replace("/", "\\"));
                }

                var mods = LoadModsManifest(storagePath);
                HashSet<string> trustedPaths = new HashSet<string>(mods, StringComparer.OrdinalIgnoreCase);

                string scanRoot = contentPath;
                if (commonRoot != null)
                {

                    string normalizedCommon = commonRoot.TrimEnd('\\', '/');
                    if (!contentPath.EndsWith(normalizedCommon, StringComparison.OrdinalIgnoreCase))
                    {
                        string possibleScanRoot = Path.Combine(contentPath, normalizedCommon);
                        if (Directory.Exists(possibleScanRoot)) scanRoot = possibleScanRoot;
                    }
                }

                if (!Directory.Exists(scanRoot))
                {
                    Logger.Log($"[Repair-Analyze] ⚠️ Scan root not found: {scanRoot}. Skipping additions check.");
                    return report;
                }

                var currentFiles = Directory.GetFiles(scanRoot, "*", SearchOption.AllDirectories)
                                        .Where(f => !Path.GetFileName(f).StartsWith(".rip_", StringComparison.OrdinalIgnoreCase) && !f.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase));

                foreach (var fullPath in currentFiles)
                {
                    string rel = Path.GetRelativePath(contentPath, fullPath);
                    if (!knownRelPaths.Contains(rel) && !trustedPaths.Contains(rel))
                    {
                        if (!rel.StartsWith("_CommonRedist", StringComparison.OrdinalIgnoreCase))
                        {
                            report.AddedFiles.Add(rel);
                        }
                    }
                }

                Logger.Log($"[Repair-Analyze] Total Added Files detected: {report.AddedFiles.Count}");
                if (report.AddedFiles.Count > 0)
                {
                    Logger.Log($"[Repair-Analyze] Detected Added Files: {string.Join(", ", report.AddedFiles.Take(10))}{(report.AddedFiles.Count > 10 ? "..." : "")}");
                }

                long totalBytes = 0;
                foreach (var relPath in report.MissingFiles.Concat(report.CorruptedFiles))
                {
                    var entry = map.Entries.FirstOrDefault(e => e.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                    if (entry != null) totalBytes += entry.PackedSize;
                }
                report.EstimatedDownloadBytes = totalBytes;
            }
            catch (Exception ex)
            {
                report.Error = $"Analysis failed: {ex.Message}";
                Logger.LogError("AnalyzeGame", ex);
            }

            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath.Equals(storagePath, StringComparison.OrdinalIgnoreCase));
            if (meta != null)
            {
                var redists = RedistService.GetRequiredRedists(contentPath);
                meta.IsRedistMissing = redists.Any(r => !r.IsInstalled);
                GlobalSettings.Save();
            }

            return report;
        }

        public static async Task PerformIntegrityRepairAsync(string storagePath, string contentPath, RepairReport report, string downloadUrl, Action<string, double> onProgress, CancellationToken ct = default)
        {
            string mapPath = Path.Combine(storagePath, MapFileName);
            var map = JsonSerializer.Deserialize<ArchiveMap>(File.ReadAllText(mapPath))!;
            byte[] prefix = Convert.FromBase64String(map.PrefixBase64);

            var toRepair = report.MissingFiles.Concat(report.CorruptedFiles).ToList();

            string? commonRoot = null;
            if (map.Entries.Count > 0)
            {
                var firstParts = map.Entries[0].Path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (firstParts.Length > 1)
                {
                    string possibleRoot = firstParts[0] + "\\";
                    if (map.Entries.All(e => e.Path.StartsWith(possibleRoot, StringComparison.OrdinalIgnoreCase)))
                        commonRoot = possibleRoot;
                }
            }

            if (prefix == null || prefix.Length < 7)
            {
                Logger.Log($"[Repair] Archive prefix is missing. Restoring from CDN...");
                var preReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                preReq.Headers.Range = new RangeHeaderValue(0, 8191);
                if (downloadUrl.Contains("gofile.io") && !string.IsNullOrEmpty(GoFileClient.AccountToken))
                    preReq.Headers.Add("Cookie", $"accountToken={GoFileClient.AccountToken}");

                using (var preResp = await _httpClient.SendAsync(preReq, ct))
                {
                    preResp.EnsureSuccessStatusCode();
                    byte[] headData = await preResp.Content.ReadAsByteArrayAsync();
                    prefix = TrimRarPrefix(headData, out long firstOff);

                    map.PrefixBase64 = Convert.ToBase64String(prefix);
                    map.PrefixSize = firstOff;
                    File.WriteAllText(mapPath, JsonSerializer.Serialize(map, _jsonOptions));
                    Logger.Log($"[Repair] [OK] Prefix restored! Size: {prefix.Length} bytes.");
                }
            }

            string skelPath = Path.Combine(storagePath, SkeletonFileName);
            FileSkeleton? skeleton = null;
            if (File.Exists(skelPath))
            {
                try { skeleton = JsonSerializer.Deserialize<FileSkeleton>(File.ReadAllText(skelPath), _jsonOptions); }
                catch { }
            }

            long totalDownloadSize = 0;
            var repairWork = new List<(string RelPath, ArchiveEntry Entry, long RangeStart, long Length)>();

            var sortedEntries = map.Entries.OrderBy(e => e.HeaderOffset).ToList();
            foreach (var relPath in toRepair)
            {
                var entry = map.Entries.FirstOrDefault(e => e.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;

                long rangeStart = entry.HeaderOffset;
                long rangeEnd = entry.DataOffset + entry.PackedSize - 1;

                var nextEntry = sortedEntries.FirstOrDefault(e => e.HeaderOffset > entry.HeaderOffset);
                if (nextEntry != null) rangeEnd = nextEntry.HeaderOffset - 1;
                else if (map.TotalArchiveSize > 0) rangeEnd = map.TotalArchiveSize - 1;

                long safetyCap = entry.DataOffset + entry.PackedSize + (1024 * 1024 * 10);
                if (rangeEnd > safetyCap) rangeEnd = safetyCap;

                long length = rangeEnd - rangeStart + 1;
                if (length > 0)
                {
                    totalDownloadSize += length;
                    repairWork.Add((relPath, entry, rangeStart, length));
                }
            }

            int totalFiles = repairWork.Count;
            long globalDownloaded = 0;
            long lastUiUpdate = 0;
            long lastDiskUpdate = 0;
            string statePath = Path.Combine(contentPath, ".rip_repair_state.json");

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = map.IsSolid ? 1 : 12, CancellationToken = ct };

            await Parallel.ForEachAsync(repairWork, parallelOptions, async (work, token) =>
            {
                string relPath = work.RelPath;
                var entry = work.Entry;
                long rangeStart = work.RangeStart;
                long length = work.Length;

                var gf = ScannerEngine.FoundGames.FirstOrDefault(g => g.RootPath.Equals(contentPath, StringComparison.OrdinalIgnoreCase));

                string fileName = Path.GetFileName(relPath);
                long lastDownloadedForThisFile = 0;

                string targetPath = relPath;
                string fullPath = Path.Combine(contentPath, targetPath);

                if (!File.Exists(fullPath) && commonRoot != null && targetPath.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string stripped = targetPath[commonRoot.Length..];
                    if (File.Exists(Path.Combine(contentPath, stripped)) || !File.Exists(fullPath))
                    {
                        targetPath = stripped;
                    }
                }

                try
                {
                    bool useTempFile = entry.PackedSize > 30 * 1024 * 1024;
                    Stream extractionStream;
                    string? tempFilePath = null;

                    if (useTempFile)
                    {
                        tempFilePath = Path.Combine(Path.GetTempPath(), $"sr_repair_{Guid.NewGuid()}.tmp");
                        extractionStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
                    }
                    else
                    {
                        extractionStream = new MemoryStream();
                    }

                    try
                    {
                        await extractionStream.WriteAsync(prefix.AsMemory(), token);

                        await DownloadRangeToStreamWithProgressAsync(downloadUrl, rangeStart, length, extractionStream, (downloadedInThisFile) => {
                            long delta = downloadedInThisFile - lastDownloadedForThisFile;
                            lastDownloadedForThisFile = downloadedInThisFile;

                            long totalSoFar = Interlocked.Add(ref globalDownloaded, delta);

                            long now = Environment.TickCount64;
                            long last = Interlocked.Read(ref lastUiUpdate);
                            if (now - last > 100)
                            {
                                if (Interlocked.CompareExchange(ref lastUiUpdate, now, last) == last)
                                {
                                    double globalPct = (totalSoFar * 100.0) / Math.Max(1, totalDownloadSize);
                                    string progStr = FormatProgressString(totalSoFar, totalDownloadSize);
                                    onProgress?.Invoke($"{progStr} Restoring {fileName}...", globalPct);

                                    if (gf != null)
                                    {
                                        (Application.Current as App)?.m_window?.DispatcherQueue.TryEnqueue(() =>
                                        {
                                            gf.IsInProgress = true;
                                            gf.IsRepairInterrupted = false;
                                            gf.ProgressPercentage = globalPct;
                                            gf.ProgressDetails = $"{progStr}";
                                        });

                                        if (now - lastDiskUpdate > 2000)
                                        {
                                            if (Interlocked.CompareExchange(ref lastDiskUpdate, now, lastDiskUpdate) == (now - (now - lastDiskUpdate)))
                                            {
                                                try {
                                                    var state = new RepairState {
                                                        TotalBytes = totalDownloadSize,
                                                        DownloadedBytes = totalSoFar,
                                                        Percentage = globalPct,
                                                        Status = progStr,
                                                        LastUpdate = DateTime.UtcNow
                                                    };
                                                    File.WriteAllText(statePath, JsonSerializer.Serialize(state, _jsonOptions));
                                                } catch { }
                                            }
                                        }
                                    }
                                }
                            }
                        }, token);

                        extractionStream.Position = 0;
                        using (var reader = SharpCompress.Readers.Rar.RarReader.Open(extractionStream))
                        {
                            if (reader.MoveToNextEntry())
                            {
                                string outDir = Path.GetDirectoryName(Path.Combine(contentPath, targetPath))!;
                                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                                reader.WriteEntryToDirectory(outDir, new SharpCompress.Common.ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                                Logger.Log($"[Repair] [OK] Restored: {relPath}");

                                if (skeleton != null)
                                {
                                    lock (skeleton)
                                    {
                                        var skelFile = skeleton.Files.FirstOrDefault(sf => sf.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                                        if (skelFile == null)
                                        {
                                            skelFile = new SkeletonFile { Path = relPath };
                                            skeleton.Files.Add(skelFile);
                                        }
                                        skelFile.Size = entry.Size;
                                        try {
                                            var fi = new FileInfo(Path.Combine(contentPath, relPath));
                                            if (fi.Exists) {
                                                skelFile.FileTime = fi.LastWriteTimeUtc.Ticks;
                                                skelFile.LastWriteTime = DateTime.UtcNow.Ticks;
                                            }
                                        } catch {}
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Repair] [Error] Extraction failed for {relPath}: {ex.Message}");
                    }
                    finally
                    {
                        await extractionStream.DisposeAsync();
                        if (tempFilePath != null && File.Exists(tempFilePath))
                        {
                            try { File.Delete(tempFilePath); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Repair] [Error] Task failed for {relPath}: {ex.Message}");
                }
            });

            Logger.Log("[Repair] [OK] Restoration complete.");
            try { if (File.Exists(statePath)) File.Delete(statePath); } catch { }

            if (skeleton != null)
            {
                try {
                    File.WriteAllText(skelPath, JsonSerializer.Serialize(skeleton, _jsonOptions));
                    Logger.Log("[Repair] [OK] Updated manifest (skeleton) with restored files.");
                } catch (Exception ex) {
                    Logger.LogError("Repair-ManifestUpdate", ex);
                }
            }

            var finalGf = ScannerEngine.FoundGames.FirstOrDefault(g => g.RootPath.Equals(contentPath, StringComparison.OrdinalIgnoreCase));
            if (finalGf != null)
            {
                finalGf.IsInProgress = false;
                finalGf.ProgressPhase = "";
                finalGf.ProgressPercentage = 100;
            }
        }

        private static byte[] TrimRarPrefix(byte[] data, out long firstFileOffset)
        {
            firstFileOffset = 0;
            if (data.Length < 7) return data;

            int markerPos = -1;
            for (int i = 0; i < data.Length - 8; i++)
            {
                if (data[i] == 'R' && data[i + 1] == 'a' && data[i + 2] == 'r' && data[i + 3] == '!')
                {
                    markerPos = i;
                    break;
                }
            }

            if (markerPos != -1)
            {
                int pos = markerPos + 8;
                while (pos < data.Length)
                {
                    int headerStart = pos;
                    if (pos + 4 > data.Length) break;

                    pos += 4;
                    ulong headerSize = ReadVInt(data, ref pos);
                    int afterSizePos = pos;
                    ulong headerType = ReadVInt(data, ref pos);

                    if (headerType == 1)
                    {
                        pos = afterSizePos + (int)headerSize;
                        continue;
                    }

                    firstFileOffset = headerStart;
                    return data.Take(headerStart).ToArray();
                }
            }
            else if (data.Length >= 7 && data[0] == 0x52 && data[1] == 0x61 && data[2] == 0x72 && data[3] == 0x21 && data[4] == 0x1A && data[5] == 0x07 && data[6] == 0x00)
            {

                int pos = 7;
                while (pos + 7 < data.Length)
                {
                    int headerStart = pos;
                    byte type = data[pos + 2];
                    ushort size = BitConverter.ToUInt16(data, pos + 5);

                    if (type == 0x73)
                    {
                        pos += size;
                        continue;
                    }

                    if (type == 0x74)
                    {
                        firstFileOffset = headerStart;
                        return data.Take(headerStart).ToArray();
                    }
                    pos += size;
                }

                firstFileOffset = 20;
                return data.Take(20).ToArray();
            }

            return data;
        }

        public static async Task ApplyRepairAsync(string storagePath, string contentPath, string title, CancellationToken ct)
        {
            var report = await AnalyzeGameAsync(storagePath, contentPath);
            if (!report.HasIssues)
            {
                Logger.Log($"[Repair] '{title}' is already verified and clean.");
                return;
            }

            var meta = GlobalSettings.Library.FirstOrDefault(m => m.LocalPath == storagePath);
            string url = meta?.Url ?? "";

            if (string.IsNullOrEmpty(url))
            {
                Logger.Log($"[Repair] No source URL found for {title}. Please link the game page in configuration first.");
                return;
            }

            await PerformIntegrityRepairAsync(storagePath, contentPath, report, url, (msg, pct) => {
                GlobalSettings.HashingProgress = msg;
                GlobalSettings.HashingProgressValue = pct;
            }, ct);
        }

        private static long ReadVInt(BinaryReader br)
        {
            long result = 0;
            int shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        private static ulong ReadVInt(byte[] data, ref int pos)
        {
            ulong result = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        public static List<string> LoadModsManifest(string storagePath)
        {
            var path = Path.Combine(storagePath, ".mods.json");
            if (!File.Exists(path)) return new List<string>();
            try
            {
                var json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        public static void UpdateModsManifest(string storagePath, List<string> files)
        {
            var path = Path.Combine(storagePath, ".mods.json");
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(files.Distinct().ToList());
                File.WriteAllText(path, json);
            }
            catch (Exception ex) { Logger.Log($"[Repair] Failed to save mods manifest: {ex.Message}"); }
        }

        public static void QuarantineFiles(string storagePath, string contentPath, List<string> relativePaths)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string quarantineDir = Path.Combine(storagePath, "_Quarantine");

            foreach (var rel in relativePaths)
            {
                try
                {
                    string fullPath = Path.Combine(contentPath, rel);
                    if (File.Exists(fullPath))
                    {
                        string targetPath = Path.Combine(quarantineDir, rel + "." + ts + ".bak");
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        File.Move(fullPath, targetPath, overwrite: true);
                        Logger.Log($"[Repair-Quarantine] Quarantined: {rel} (Structural)");
                    }
                    else if (Directory.Exists(fullPath))
                    {

                        string targetPath = Path.Combine(quarantineDir, rel + "." + ts + ".dir.bak");
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        Directory.Move(fullPath, targetPath);
                        Logger.Log($"[Repair-Quarantine] Quarantined Directory: {rel}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Quarantine_{rel}", ex);
                }
            }
        }
        public static void UnquarantineFiles(string storagePath, string gameContentPath)
        {
            try
            {
                var qDir = Path.Combine(storagePath, "_Quarantine");
                if (!Directory.Exists(qDir)) return;

                var files = Directory.GetFiles(qDir, "*.bak", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        var relPathInQ = Path.GetRelativePath(qDir, file);

                        string targetRelPath = QuarantineSuffixRegex().Replace(relPathInQ, "");

                        string targetPath = Path.Combine(gameContentPath, SanitizePath(targetRelPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                        if (file.EndsWith(".dir.bak"))
                        {
                            if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);
                            Directory.Move(file, targetPath);
                        }
                        else
                        {
                            File.Move(file, targetPath, overwrite: true);
                        }
                        Logger.Log($"[Repair] Restored: {targetRelPath}");
                    }
                    catch (Exception ex) { Logger.Log($"[Repair] Failed to restore {file}: {ex.Message}"); }
                }

                CleanupEmptyDirs(qDir);
                if (Directory.Exists(qDir) && Directory.GetFileSystemEntries(qDir).Length == 0) Directory.Delete(qDir);
            }
            catch (Exception ex) { Logger.LogError("Unquarantine", ex); }
        }

        private static void CleanupEmptyDirs(string path)
        {
            foreach (var directory in Directory.GetDirectories(path))
            {
                CleanupEmptyDirs(directory);
                if (Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory, false);
                }
            }
        }

        public static List<(string DisplayName, string OriginalRelPath, string FullQPath, bool IsDirectory)> GetQuarantinedFiles(string storagePath)
        {
            var results = new List<(string, string, string, bool)>();
            string qDir = Path.Combine(storagePath, "_Quarantine");
            if (!Directory.Exists(qDir)) return results;

            var files = Directory.GetFiles(qDir, "*.bak", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                string relPathInQ = Path.GetRelativePath(qDir, f);
                string originalRelPath = QuarantineSuffixRegex().Replace(relPathInQ, "");
                results.Add((originalRelPath, originalRelPath, f, f.EndsWith(".dir.bak")));
            }
            return results;
        }

        public static void DeleteAddedFiles(string gameContentPath, List<string> relativePaths)
        {
            foreach (var rel in relativePaths)
            {
                try
                {
                    var fullPath = Path.Combine(gameContentPath, rel);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                    Logger.Log($"[Repair] Deleted added file: {rel}");
                }
                catch (Exception ex) { Logger.Log($"[Repair] Failed to delete {rel}: {ex.Message}"); }
            }
        }
        public static async Task<bool> PerformSmartUpdateAsync(string rootPath, string newUrl, Action<string, double> onProgress, string? newVersion = null, CancellationToken ct = default)
        {
            try {
                Logger.Log($"[Update] Starting smart update for {rootPath} to {newUrl}");

                onProgress?.Invoke("Scanning remote archive...", 5);
                var newMap = await ScanRemoteArchiveAsync(newUrl);
                if (newMap == null || newMap.Entries.Count == 0)
                {
                    Logger.Log($"[Update] ❌ Error: Remote scan returned {newMap?.Entries.Count ?? 0} files. Aborting to prevent data loss.");
                    throw new Exception("Remote archive scan failed or returned no files. This could be due to a temporary server error.");
                }

                string mapPath = Path.Combine(rootPath, MapFileName);
                if (!File.Exists(mapPath)) throw new Exception("Local repair map missing. Cannot perform smart update.");
                var oldMap = JsonSerializer.Deserialize<ArchiveMap>(File.ReadAllText(mapPath), _jsonOptions);
                if (oldMap == null) throw new Exception("Failed to load local repair map.");

                string skelPath = Path.Combine(rootPath, SkeletonFileName);
                FileSkeleton? skeleton = File.Exists(skelPath) ? JsonSerializer.Deserialize<FileSkeleton>(File.ReadAllText(skelPath), _jsonOptions) : new FileSkeleton();
                if (skeleton == null) skeleton = new FileSkeleton();

                var toDownload = new List<ArchiveEntry>();
                var toDelete = new List<string>();

                Logger.Log($"[Update] Smart Update: Archive is {(newMap.IsSolid ? "SOLID" : "NON-SOLID")}");
                if (newMap.IsSolid)
                {
                    Logger.Log("[Update] ⚠️ WARNING: Solid archive detected. Individual file updates may fail if dictionary state is lost.");
                }

                foreach (var newEntry in newMap.Entries)
                {
                    string fullPath = Path.Combine(rootPath, SanitizePath(newEntry.Path));
                    var oldEntry = oldMap.Entries.FirstOrDefault(e => e.Path.Equals(newEntry.Path, StringComparison.OrdinalIgnoreCase));
                    var skelFile = skeleton.Files.FirstOrDefault(f => f.Path.Equals(newEntry.Path, StringComparison.OrdinalIgnoreCase));

                    bool needsDownload = false;

                    if (oldEntry == null || oldEntry.Crc32 != newEntry.Crc32)
                    {
                        needsDownload = true;
                    }
                    else if (!File.Exists(fullPath))
                    {
                        needsDownload = true;
                    }
                    else if (skelFile == null)
                    {
                        needsDownload = true;
                    }
                    else
                    {

                        var fi = new FileInfo(fullPath);
                        if (fi.Length != newEntry.Size)
                        {
                            needsDownload = true;
                        }
                    }

                    if (needsDownload)
                    {
                        toDownload.Add(newEntry);
                    }
                }

                foreach (var oldEntry in oldMap.Entries)
                {
                    if (!newMap.Entries.Any(e => e.Path.Equals(oldEntry.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        toDelete.Add(oldEntry.Path);
                    }
                }

                Logger.Log($"[Update] Plans: {toDownload.Count} to update/download, {toDelete.Count} to delete.");

                foreach (var relPath in toDelete)
                {
                    string fullPath = Path.Combine(rootPath, SanitizePath(relPath));
                    if (File.Exists(fullPath))
                    {
                        Logger.Log($"[Update] Deleting removed file: {relPath}");
                        File.Delete(fullPath);
                    }
                    skeleton.Files.RemoveAll(f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                }

                int total = toDownload.Count;
                int current = 0;
                object progressLock = new object();

                if (newMap.IsSolid)
                {
                    Logger.Log("[Update] Using single-pass sequential extraction for SOLID archive...");
                    using (var rarStream = new StitchedRarStream(newUrl, ReadRangeAsync))
                    {
                        rarStream.AddRemote(0, newMap.TotalArchiveSize);
                        using (var reader = RarReader.Open(rarStream, new ReaderOptions { Password = "steamrip.com" }))
                        {
                            while (reader.MoveToNextEntry())
                            {
                                if (ct.IsCancellationRequested) break;

                                var entry = toDownload.FirstOrDefault(e => e.Path.Equals(reader.Entry.Key, StringComparison.OrdinalIgnoreCase));
                                if (entry != null)
                                {
                                    string fullPath = Path.Combine(rootPath, SanitizePath(entry.Path));
                                    try {
                                        string outDir = Path.GetDirectoryName(fullPath)!;
                                        Directory.CreateDirectory(outDir);

                                        onProgress?.Invoke($"Extracting {entry.Path}...", 20 + (current * 70 / total));

                                        using (var fs = File.OpenWrite(fullPath))
                                        {
                                            reader.WriteEntryTo(fs);
                                        }

                                        string newHash = await Task.Run(() => ComputeXXHash64(fullPath));
                                        lock (progressLock) {
                                            var skelFile = skeleton.Files.FirstOrDefault(f => f.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
                                            if (skelFile == null) {
                                                skelFile = new SkeletonFile { Path = entry.Path };
                                                skeleton.Files.Add(skelFile);
                                            }
                                            skelFile.Hash = newHash;
                                            skelFile.Size = entry.Size;
                                        }
                                    } catch (Exception ex) {
                                        Logger.Log($"[Update-Extract] ❌ Failed to extract {entry.Path}: {ex.Message}");
                                    }

                                    current++;
                                }
                            }
                        }
                    }
                }
                else
                {

                    byte[] prefix = Convert.FromBase64String(newMap.PrefixBase64);
                    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct };

                    await Parallel.ForEachAsync(toDownload, parallelOptions, async (entry, token) =>
                    {
                        string fullPath = Path.Combine(rootPath, SanitizePath(entry.Path));

                        long rangeStart = entry.HeaderOffset;
                        long rangeEnd = entry.DataOffset + entry.PackedSize;
                        int chunkLen = (int)(rangeEnd - rangeStart);

                        bool useTempFile = entry.PackedSize > 30 * 1024 * 1024;
                        Stream extractionStream;
                        string? tempPath = null;

                        if (useTempFile)
                        {
                            tempPath = Path.Combine(Path.GetTempPath(), $"sr_update_{Guid.NewGuid()}.tmp");
                            extractionStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
                        }
                        else
                        {
                            extractionStream = new MemoryStream();
                        }

                        try
                        {
                            await extractionStream.WriteAsync(prefix, 0, prefix.Length, token);

                            string fileName = Path.GetFileName(fullPath);
                            double basePct = (Volatile.Read(ref current) * 100.0) / total;
                            double weight = 100.0 / total;

                            await DownloadRangeToStreamWithProgressAsync(newUrl, rangeStart, chunkLen, extractionStream, (downloaded) => {
                                string progStr = FormatProgressString(downloaded, chunkLen);
                                double finePct = basePct + (weight * ((double)downloaded / chunkLen));
                                onProgress?.Invoke($"{progStr} Restoring: {fileName}", finePct);
                            }, token);

                            extractionStream.Position = 0;

                            using (var reader = RarReader.Open(extractionStream, new ReaderOptions { Password = "steamrip.com" }))
                            {
                                if (reader.MoveToNextEntry())
                                {
                                    string outDir = Path.GetDirectoryName(fullPath)!;
                                    Directory.CreateDirectory(outDir);
                                    reader.WriteEntryToDirectory(outDir, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                                }
                            }
                        }
                        finally
                        {
                            await extractionStream.DisposeAsync();
                            if (tempPath != null && File.Exists(tempPath))
                            {
                                try { File.Delete(tempPath); } catch { }
                            }
                        }

                        string newHash = await Task.Run(() => ComputeXXHash64(fullPath));
                        lock (progressLock) {
                            var skelFile = skeleton.Files.FirstOrDefault(f => f.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
                            if (skelFile == null) {
                                skelFile = new SkeletonFile { Path = entry.Path };
                                skeleton.Files.Add(skelFile);
                            }
                            skelFile.Hash = newHash;
                            skelFile.Size = entry.Size;
                            current++;
                            onProgress?.Invoke($"Restoring: {Path.GetFileName(fullPath)}", (current * 100.0) / total);
                        }
                    });
                }

                File.WriteAllText(mapPath, JsonSerializer.Serialize(newMap, _jsonOptions));
                File.WriteAllText(skelPath, JsonSerializer.Serialize(skeleton, _jsonOptions));

                onProgress?.Invoke("Update complete!", 100);
                WriteVersionFile(rootPath, newVersion);
                Logger.Log($"[Update] Smart update completed successfully for {rootPath}.");
                return true;
            } catch (Exception ex) {
                Logger.LogError("SmartUpdate", ex);
                return false;
            }
        }

        #region Remote RAR Scanning (The "Leap" Logic)

        public static async Task<ArchiveMap?> ScanRemoteArchiveAsync(string url)
        {
            try {
                Logger.Log($"[Repair] Starting remote RAR scan: {url}");
                var map = new ArchiveMap { ArchiveName = Path.GetFileName(new Uri(url).LocalPath) };

                using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                headReq.Headers.UserAgent.ParseAdd(CommonUserAgent);
                if (url.Contains("gofile.io", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(GoFileClient.AccountToken))
                    headReq.Headers.Add("Cookie", $"accountToken={GoFileClient.AccountToken}");

                using var headResp = await _httpClient.SendAsync(headReq);
                map.TotalArchiveSize = headResp.Content.Headers.ContentLength ?? 0;
                Logger.Log($"[Repair] Remote Archive Size: {map.TotalArchiveSize} bytes");

                if (map.TotalArchiveSize < 8)
                {
                    Logger.Log($"[Repair] Archive too small to be valid ({map.TotalArchiveSize} bytes).");
                    return null;
                }

                byte[] sig = await ReadRangeAsync(url, 0, 16);
                Logger.Log($"[Repair] Signature Hex: {BitConverter.ToString(sig).Replace("-", " ")}");

                bool isRar5 = IsRar5Signature(sig);
                bool isRar4 = !isRar5 && IsRar4Signature(sig);

                if (!isRar5 && !isRar4)
                {
                    Logger.Log("[Repair] Remote archive is not a valid RAR4 or RAR5 signature.");
                    return null;
                }

                long offset = isRar5 ? 8 : 7;

                byte[]? buffer = null;
                long bufferStart = -1;

                while (offset < map.TotalArchiveSize)
                {

                    if (buffer == null || offset < bufferStart || offset > bufferStart + buffer.Length - 2048)
                    {
                        int fetchSize = (int)Math.Min(2 * 1024 * 1024, map.TotalArchiveSize - offset);
                        if (fetchSize <= 0) break;
                        buffer = await ReadRangeAsync(url, offset, fetchSize);
                        bufferStart = offset;
                    }

                    int headPtr = (int)(offset - bufferStart);

                    if (isRar5)
                    {

                        if (buffer == null || offset < bufferStart || offset >= bufferStart + buffer.Length - 1024)
                        {
                            int fetchSize = 16 * 1024 * 1024;
                            fetchSize = (int)Math.Min(fetchSize, map.TotalArchiveSize - offset);
                            if (fetchSize <= 0) break;

                            Logger.Log($"[Leap-Scan] Fetching 16MB chunk at {offset}...");
                            buffer = await ReadRangeAsync(url, offset, fetchSize);
                            bufferStart = offset;
                        }
                        headPtr = (int)(offset - bufferStart);

                        if (headPtr + 4 > buffer.Length) break;
                        uint blockCrc = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                        int sizeStart = headPtr;
                        ulong blockSize = ReadVInt(buffer, ref headPtr);
                        int sizeLen = headPtr - sizeStart;

                        if (blockSize > 65536)
                        {
                            Logger.Log($"[Repair] [RAR5] Malformed header size ({blockSize}) at {offset}. Stopping.");
                            break;
                        }

                        if (headPtr + (int)blockSize <= buffer.Length)
                        {
                            uint actualCrc = ComputeCRC32(buffer, sizeStart, sizeLen + (int)blockSize);
                            if (actualCrc != blockCrc)
                            {
                                Logger.Log($"[Repair] [RAR5] Integrity failure at {offset}. Expected CRC {blockCrc:X8}, got {actualCrc:X8}. Stopping.");
                                break;
                            }
                        }
                        else
                        {

                            break;
                        }

                        ulong blockType = ReadVInt(buffer, ref headPtr);
                        ulong blockFlags = ReadVInt(buffer, ref headPtr);

                        if (blockType > 10)
                        {
                            Logger.Log($"[Repair] [RAR5] Invalid block type {blockType} at {offset}. Stopping.");
                            break;
                        }

                        ulong extraSize = (blockFlags & 0x01) != 0 ? ReadVInt(buffer, ref headPtr) : 0;
                        ulong dataSize = (blockFlags & 0x02) != 0 ? ReadVInt(buffer, ref headPtr) : 0;

                        Logger.Log($"[Leap-Trace] [RAR5] Block Type {blockType} at {offset}, HeadContent={blockSize}, Data={dataSize}");

                        if (offset < bufferStart || offset + (long)blockSize + 32 >= bufferStart + buffer.Length)
                        {
                            int fetchSize = (int)Math.Max(16 * 1024 * 1024, (long)blockSize + 4096);
                            fetchSize = (int)Math.Min(fetchSize, map.TotalArchiveSize - offset);
                            if (fetchSize <= 0) break;

                            Logger.Log($"[Leap-Scan] Fetching 16MB chunk at {offset} (Block Overrun)...");
                            buffer = await ReadRangeAsync(url, offset, fetchSize);
                            bufferStart = offset;
                            headPtr = 0;

                            uint bCrc = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                            ReadVInt(buffer, ref headPtr);
                            ReadVInt(buffer, ref headPtr);
                            ReadVInt(buffer, ref headPtr);
                            if ((blockFlags & 0x01) != 0) ReadVInt(buffer, ref headPtr);
                            if ((blockFlags & 0x02) != 0) ReadVInt(buffer, ref headPtr);
                        }
                        else
                        {

                            headPtr = (int)(offset - bufferStart);

                            headPtr += 4;
                            ReadVInt(buffer, ref headPtr);
                            ReadVInt(buffer, ref headPtr);
                            ReadVInt(buffer, ref headPtr);
                            if ((blockFlags & 0x01) != 0) ReadVInt(buffer, ref headPtr);
                            if ((blockFlags & 0x02) != 0) ReadVInt(buffer, ref headPtr);
                        }

                        if (blockType == 1)
                        {
                            map.PrefixSize = offset + 4 + sizeLen + (long)blockSize;
                            map.PrefixBase64 = Convert.ToBase64String(await ReadRangeAsync(url, 0, (int)map.PrefixSize));

                            int archiveFlagsPtr = headPtr;
                            ulong archiveFlags = ReadVInt(buffer, ref archiveFlagsPtr);
                            map.IsSolid = (archiveFlags & 0x04) != 0;
                        }
                        else if (blockType == 2)
                        {
                            ulong fileFlags = ReadVInt(buffer, ref headPtr);
                            ulong unpackedSize = ReadVInt(buffer, ref headPtr);
                            ulong attributes = ReadVInt(buffer, ref headPtr);

                            if ((blockFlags & 0x04) != 0) headPtr += 4;
                            uint fileCrc = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;

                            ulong compInfo = ReadVInt(buffer, ref headPtr);
                            byte method = (byte)(compInfo & 0x3F);
                            ReadVInt(buffer, ref headPtr);

                            ulong nameLen = ReadVInt(buffer, ref headPtr);

                            int safeNameLen = Math.Min((int)nameLen, (int)blockSize);
                            safeNameLen = Math.Min(safeNameLen, buffer.Length - headPtr);

                            if (safeNameLen > 0)
                            {
                                string name = SanitizePath(System.Text.Encoding.UTF8.GetString(buffer, headPtr, safeNameLen));

                                if (!((attributes & 0x10) != 0 || (dataSize == 0 && unpackedSize == 0)))
                                {
                                    map.Entries.Add(new ArchiveEntry {
                                        Path = name.Replace('/', '\\'),
                                        HeaderOffset = offset,
                                        DataOffset = offset + 4 + sizeLen + (long)blockSize,
                                        PackedSize = (long)dataSize,
                                        Size = (long)unpackedSize,
                                        Crc32 = fileCrc,
                                        Method = method,
                                        RarVersion = "RAR5"
                                    });
                                }
                            }
                        }

                        offset += 4 + sizeLen + (long)blockSize + (long)dataSize;
                        if (blockSize == 0 && blockType != 2) break;
                    }
                    else
                    {
                        if (buffer == null || offset < bufferStart || offset + 1024 > bufferStart + buffer.Length)
                        {
                            int fetchSize = (int)Math.Min(16 * 1024 * 1024, map.TotalArchiveSize - offset);
                            if (fetchSize <= 0) break;
                            buffer = await ReadRangeAsync(url, offset, fetchSize);
                            bufferStart = offset;
                        }
                        headPtr = (int)(offset - bufferStart);

                        if (headPtr + 7 > buffer.Length) break;
                        uint blockCrc = BitConverter.ToUInt16(buffer, headPtr); headPtr += 2;
                        byte blockType = buffer[headPtr++];
                        ushort blockFlags = BitConverter.ToUInt16(buffer, headPtr); headPtr += 2;
                        ushort headSize = BitConverter.ToUInt16(buffer, headPtr); headPtr += 2;

                        if (blockType < 0x72 || blockType > 0x7B)
                        {
                             Logger.Log($"[Repair] [RAR4] Sanity failure at {offset}. Type 0x{blockType:X2}, HeadSize {headSize}. Stopping.");
                             break;
                        }

                        long dataSize = 0;
                        if (blockType == 0x74)
                        {
                            long pSize = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                            long unpSize = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                            byte hostOS = buffer[headPtr++];
                            uint fileCrc = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                            uint fileTime = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                            byte dictSize = buffer[headPtr++];
                            byte method = buffer[headPtr++];
                            ushort nameSize = BitConverter.ToUInt16(buffer, headPtr); headPtr += 2;
                            uint attr = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;

                            if ((blockFlags & 0x100) != 0)
                            {
                                long packHigh = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                                long unpHigh = BitConverter.ToUInt32(buffer, headPtr); headPtr += 4;
                                pSize |= (packHigh << 32);
                                unpSize |= (unpHigh << 32);
                            }

                            int safeNameLen = Math.Min((int)nameSize, (int)headSize);
                            safeNameLen = Math.Min(safeNameLen, buffer.Length - headPtr);

                            if (headPtr + safeNameLen > buffer.Length)
                            {
                                int fetchSize = (int)Math.Max(2 * 1024 * 1024, (long)headSize + 4096);
                                fetchSize = (int)Math.Min(fetchSize, map.TotalArchiveSize - offset);
                                if (fetchSize > 0)
                                {
                                    buffer = await ReadRangeAsync(url, offset, fetchSize);
                                    bufferStart = offset;
                                    headPtr = 7 + 25;
                                    if ((blockFlags & 0x100) != 0) headPtr += 8;
                                }
                            }

                            safeNameLen = Math.Min((int)nameSize, buffer.Length - headPtr);
                            if (safeNameLen > 0)
                            {
                                string name = SanitizePath(System.Text.Encoding.UTF8.GetString(buffer, headPtr, safeNameLen));
                                bool isDir = (blockFlags & 0xE0) == 0xE0 || (attr & 0x10) != 0;
                                if (!isDir)
                                {
                                    map.Entries.Add(new ArchiveEntry {
                                        Path = name.Replace('/', '\\'),
                                        HeaderOffset = offset,
                                        DataOffset = offset + headSize,
                                        PackedSize = pSize,
                                        Size = unpSize,
                                        Crc32 = fileCrc,
                                        RarVersion = "RAR4"
                                    });
                                }
                            }
                            dataSize = pSize;
                        }

                        Logger.Log($"[Leap-Trace] [RAR4] Block Type 0x{blockType:X2} at {offset}, HeadSize={headSize}, DataSize={dataSize}");

                        offset += headSize + dataSize;
                        if (headSize == 0 && blockType != 0x73) break;
                    }
                }

                Logger.Log($"[Repair] Remote scan complete ({ (isRar5 ? "RAR5" : "RAR4") }). Found {map.Entries.Count} entries.");
                return map;
            } catch (Exception ex) {
                Logger.LogError("ScanRemoteArchive", ex);
                return null;
            }
        }

        private static string FormatProgressString(long current, long total)
        {
            if (total <= 0) return $"{(current / 1024.0 / 1024.0):F1} MB";

            if (total < 1024 * 1024)
                return $"{(current / 1024.0):F1}/{(total / 1024.0):F1} KB";

            return $"{(current / 1024.0 / 1024.0):F1}/{(total / 1024.0 / 1024.0):F1} MB";
        }

        private static async Task<byte[]> ReadRangeAsync(string url, long start, long length)
        {
            using var ms = new MemoryStream();
            await DownloadRangeToStreamAsync(url, start, length, ms, CancellationToken.None);
return ms.ToArray();
        }

        private static async Task DownloadRangeToStreamAsync(string url, long start, long length, Stream destination, CancellationToken ct)
        {
            await DownloadRangeToStreamWithProgressAsync(url, start, length, destination, null, ct);
        }

        private static async Task DownloadRangeToStreamWithProgressAsync(string url, long start, long length, Stream destination, Action<long>? onProgress, CancellationToken ct)
        {
            const long MIN_PARALLEL_SIZE = 32 * 1024 * 1024;
            const int CHUNK_SIZE = 16 * 1024 * 1024;

            if (length > MIN_PARALLEL_SIZE && destination.CanSeek)
            {
                int numChunks = (int)Math.Ceiling((double)length / CHUNK_SIZE);
                long downloaded = 0;
                long destBasePos = destination.Position;

                await Parallel.ForEachAsync(Enumerable.Range(0, numChunks), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (i, token) =>
                {
                    long chunkStart = i * (long)CHUNK_SIZE;
                    long chunkLen = Math.Min(CHUNK_SIZE, length - chunkStart);

                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start + chunkStart, start + chunkStart + chunkLen - 1);
                    request.Headers.UserAgent.ParseAdd(CommonUserAgent);
                    if (url.Contains("gofile.io") && !string.IsNullOrEmpty(GoFileClient.AccountToken))
                        request.Headers.Add("Cookie", $"accountToken={GoFileClient.AccountToken}");

                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var stream = await response.Content.ReadAsStreamAsync(token))
                        {
                            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                            try {
                                int read;
                                long chunkPos = 0;
                                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                                {
                                    lock (destination)
                                    {
                                        destination.Position = destBasePos + chunkStart + chunkPos;
                                        destination.Write(buffer, 0, read);
                                    }
                                    chunkPos += read;
                                    long total = Interlocked.Add(ref downloaded, read);
                                    onProgress?.Invoke(total);
                                }
                            } finally {
                                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                    }
                });

                destination.Position = destBasePos + length;
                return;
            }

            var singleReq = new HttpRequestMessage(HttpMethod.Get, url);
            singleReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + length - 1);
            singleReq.Headers.UserAgent.ParseAdd(CommonUserAgent);
            if (url.Contains("gofile.io") && !string.IsNullOrEmpty(GoFileClient.AccountToken))
                singleReq.Headers.Add("Cookie", $"accountToken={GoFileClient.AccountToken}");

            using (var response = await _httpClient.SendAsync(singleReq, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync(ct))
                {
                    await DownloadRangeToStreamWithProgressAsync(stream, length, destination, onProgress, ct);
                }
            }
        }

        private static async Task DownloadRangeToStreamWithProgressAsync(Stream source, long length, Stream destination, Action<long>? onProgress, CancellationToken ct)
        {
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await destination.WriteAsync(buffer, 0, read, ct);
                totalRead += read;
                onProgress?.Invoke(totalRead);
            }
        }

        private static bool IsRar5Signature(byte[] sig)
        {
            return sig.Length >= 8 &&
                   sig[0] == 0x52 && sig[1] == 0x61 && sig[2] == 0x72 && sig[3] == 0x21 &&
                   sig[4] == 0x1a && sig[5] == 0x07 && sig[6] == 0x01 && sig[7] == 0x00;
        }

        private static bool IsRar4Signature(byte[] sig)
        {
            return sig.Length >= 7 &&
                   sig[0] == 0x52 && sig[1] == 0x61 && sig[2] == 0x72 && sig[3] == 0x21 &&
                   sig[4] == 0x1a && sig[5] == 0x07 && sig[6] == 0x00;
        }

        #endregion

        [GeneratedRegex(@"\.\d{8}_\d{6}\.(dir\.)?bak$")]
        private static partial System.Text.RegularExpressions.Regex QuarantineSuffixRegex();
    }

    public partial class PositionTrackingStream : Stream
    {
        private readonly Stream _baseStream;
        private long _position;

        public PositionTrackingStream(Stream baseStream)
        {
            _baseStream = baseStream;
            _position = baseStream.Position;
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;

        public override long Position
        {
            get => _position;
            set { _baseStream.Position = value; _position = value; }
        }

        public override void Flush() => _baseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _baseStream.Read(buffer, offset, count);
            _position += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _baseStream.ReadAsync(buffer, cancellationToken);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long pos = _baseStream.Seek(offset, origin);
            _position = pos;
            return pos;
        }

        public override void SetLength(long value) => _baseStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);
    }

    public class RepairState
    {
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public double Percentage { get; set; }
        public string Status { get; set; } = "";
        public DateTime LastUpdate { get; set; }
    }
}