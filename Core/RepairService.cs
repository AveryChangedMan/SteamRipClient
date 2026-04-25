using System;
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
using System.Collections.Concurrent;

namespace SteamRipApp.Core
{
    public class ArchiveMap
    {
        public string ArchiveName { get; set; } = "";
        public string PrefixBase64 { get; set; } = "";
        public long PrefixSize { get; set; }
        public bool IsSolid { get; set; }
        public List<ArchiveEntry> Entries { get; set; } = new List<ArchiveEntry>();
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
    }

    public class FileSkeleton
    {
        public List<SkeletonFile> Files { get; set; } = new List<SkeletonFile>();
    }

    public class SkeletonFile
    {
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Size { get; set; }
        public long LastWriteTime { get; set; } 
    }

    public class RepairReport
    {
        public List<string> MissingFiles { get; set; } = new List<string>();
        public List<string> CorruptedFiles { get; set; } = new List<string>();
        public bool HasIssues => MissingFiles.Count > 0 || CorruptedFiles.Count > 0;
        public string? Error { get; set; }
        public long EstimatedDownloadBytes { get; set; }
    }

    public static class RepairService
    {
        private const string MapFileName = ".rip_map.json";
        private const string SkeletonFileName = ".rip_skeleton.json";
        private static readonly HttpClient _httpClient;
        private const string CommonUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private static CancellationTokenSource? _hashingCts;
        private static bool _isHashingActive = false;
        private static readonly Dictionary<string, (string ContentPath, bool Force)> _pendingHashing = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

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
            _isHashingActive = false;
            Logger.Log("[Repair] Background hashing loop stopped.");
        }

        public static void TriggerManualBackup(string storagePath, string contentPath, bool force = true)
        {
            
            string targetDir = contentPath;
            if (File.Exists(contentPath))
            {
                targetDir = Path.GetDirectoryName(contentPath) ?? contentPath;
            }

            Logger.Log($"[Repair] Queueing Manual Backup for: {targetDir}");
            
            lock (_pendingHashing)
            {
                _pendingHashing[storagePath] = (targetDir, force);
            }

            
            StartBackgroundHashing();
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
                bool force = false;
                lock (_pendingHashing)
                {
                    var first = _pendingHashing.FirstOrDefault();
                    if (first.Key != null)
                    {
                        storage = first.Key;
                        content = first.Value.ContentPath;
                        force = first.Value.Force;
                        _pendingHashing.Remove(storage);
                    }
                }

                if (storage != null && content != null && Directory.Exists(content))
                {
                    string skelPath = Path.Combine(storage, SkeletonFileName);
                    if (!force && File.Exists(skelPath))
                    {
                        Logger.Log($"[Repair-Hashing] Skipping auto-hashing for {storage} (Skeleton already exists)");
                    }
                    else
                    {
                        await RunHashingProcessAsync(storage, content, ct);
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

        [System.Runtime.InteropServices.DllImport("NativeHash.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.StdCall, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int XXH64_HashFile(string filePath, out ulong outHash);

        private static async Task RunHashingProcessAsync(string storagePath, string contentPath, CancellationToken ct)
        {
            try
            {
                Logger.Log($"[Repair-Hashing] ⚡ STARTING INCREMENTAL SCAN: {contentPath}");
                
                
                var existingSkeleton = new FileSkeleton();
                string skeletonPath = Path.Combine(storagePath, SkeletonFileName);
                if (File.Exists(skeletonPath))
                {
                    try {
                        var json = File.ReadAllText(skeletonPath);
                        existingSkeleton = JsonSerializer.Deserialize<FileSkeleton>(json) ?? new FileSkeleton();
                    } catch { }
                }

                var skeleton = new FileSkeleton();
                var files = Directory.GetFiles(contentPath, "*", SearchOption.AllDirectories)
                                    .Where(f => !f.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase))
                                    .Where(f => !Path.GetFileName(f).StartsWith(".rip_"))
                                    .ToList();

                int total = files.Count;
                int processed = 0;
                int skipped = 0;
                Logger.Log($"[Repair-Hashing] Found {total} files. Checking for changes...");

                var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
                var concurrentFiles = new ConcurrentBag<SkeletonFile>();

                await Task.Run(() => Parallel.ForEach(files, options, (file) =>
                {
                    if (ct.IsCancellationRequested) return;

                    string rel = Path.GetRelativePath(contentPath, file);
                    var fileInfo = new FileInfo(file);
                    long fileSize = fileInfo.Length;
                    long lastWrite = fileInfo.LastWriteTimeUtc.Ticks;

                    int currentIdx = Interlocked.Increment(ref processed);

                    
                    var existing = existingSkeleton.Files.FirstOrDefault(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && existing.Size == fileSize && existing.LastWriteTime == lastWrite)
                    {
                        concurrentFiles.Add(existing);
                        Interlocked.Increment(ref skipped);
                        return; 
                    }

                    GlobalSettings.HashingProgress = $"Scanning: {Path.GetFileName(file)} ({currentIdx}/{total})";
                    GlobalSettings.HashingProgressValue = (currentIdx * 100.0) / total;

                    ulong hashVal;
                    
                    int result = XXH64_HashFile(file, out hashVal);

                    if (result == 0)
                    {
                        
                        byte[] bytes = BitConverter.GetBytes(hashVal);
                        string hash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                        
                        concurrentFiles.Add(new SkeletonFile
                        {
                            Path = rel,
                            Hash = hash,
                            Size = fileSize,
                            LastWriteTime = lastWrite
                        });
                        Logger.Log($"[Repair-Hashing] [{currentIdx}/{total}] {rel} -> {hash}");
                    }
                    else
                    {
                        Logger.Log($"[Repair-Hashing] ❌ Scan error {result} for: {rel}");
                    }
                }), ct);

                if (!ct.IsCancellationRequested)
                {
                    skeleton.Files.AddRange(concurrentFiles.OrderBy(f => f.Path));
                    string outPath = Path.GetFullPath(Path.Combine(storagePath, SkeletonFileName));
                    File.WriteAllText(outPath, JsonSerializer.Serialize(skeleton, new JsonSerializerOptions { WriteIndented = true }));
                    Logger.Log($"[Repair-Hashing] ✅ SCAN COMPLETE: {outPath} (Processed {total}, Skipped {skipped})");
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
            }
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
                                RarVersion = "RAR5"
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

        public static async Task<RepairReport> AnalyzeGameAsync(string storagePath, string contentPath, Action<string, double>? onProgress = null)
        {
            var report = new RepairReport();
            try
            {
                string mapPath = Path.Combine(storagePath, MapFileName);
                string skelPath = Path.Combine(storagePath, SkeletonFileName);

                if (!File.Exists(mapPath))
                {
                    report.Error = "No archive map found. Repair is only possible for games with an active .rip_map.json.";
                    return report;
                }

                var map = JsonSerializer.Deserialize<ArchiveMap>(File.ReadAllText(mapPath));
                FileSkeleton? skeleton = null;
                if (File.Exists(skelPath))
                {
                    skeleton = JsonSerializer.Deserialize<FileSkeleton>(File.ReadAllText(skelPath));
                }

                var entries = map!.Entries;
                
                
                string? commonRoot = null;
                if (entries.Count > 0)
                {
                    var firstParts = entries[0].Path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (firstParts.Length > 1)
                    {
                        string possibleRoot = firstParts[0] + "\\";
                        if (entries.All(e => e.Path.StartsWith(possibleRoot, StringComparison.OrdinalIgnoreCase)))
                        {
                            commonRoot = possibleRoot;
                            Logger.Log($"[Repair-Analyze] Detected common root wrapper '{commonRoot}'. It will be ignored for local path alignment.");
                        }
                    }
                }

                int total = entries.Count;
                int processed = 0;

                
                await Task.Run(() =>
                {
                    var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
                    Parallel.ForEach(entries, options, (entry) =>
                    {
                        int current = Interlocked.Increment(ref processed);
                        if (current % 10 == 0 || current == total) 
                        {
                            onProgress?.Invoke($"Verifying: {entry.Path} ({current}/{total})", (current * 100.0) / total);
                        }

                        string relPath = entry.Path;
                        
                        
                        if (relPath.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase)) return;
                        
                        string fullPath = Path.Combine(contentPath, relPath);

                        
                        
                        if (!File.Exists(fullPath) && commonRoot != null && relPath.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            string strippedRel = relPath.Substring(commonRoot.Length);
                            string strippedFullPath = Path.Combine(contentPath, strippedRel);
                            
                            
                            if (File.Exists(strippedFullPath))
                            {
                                relPath = strippedRel;
                                fullPath = strippedFullPath;
                            }
                        }
                        
                        
                        if (!File.Exists(fullPath))
                        {
                            
                            if (!relPath.Contains("\\") && !relPath.Contains("/")) return;

                            lock (report.MissingFiles) report.MissingFiles.Add(relPath);
                            lock (report) report.EstimatedDownloadBytes += entry.PackedSize;
                            return;
                        }
                        else if (skeleton != null)
                        {
                            var skelFile = skeleton.Files.FirstOrDefault(f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                            if (skelFile != null)
                            {
                                
                                ulong hashVal;
                                int res = XXH64_HashFile(fullPath, out hashVal);
                                if (res == 0)
                                {
                                    byte[] bytes = BitConverter.GetBytes(hashVal);
                                    string currentHash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                                    
                                    if (currentHash != skelFile.Hash)
                                    {
                                        lock (report.CorruptedFiles) report.CorruptedFiles.Add(entry.Path);
                                    }
                                }
                                else
                                {
                                    Logger.Log($"[Repair-Analyze] Scan error {res} on {entry.Path}");
                                }
                            }
                        }
                    });
                });

                
                long totalBytes = 0;
                foreach (var relPath in report.MissingFiles.Concat(report.CorruptedFiles))
                {
                    var entry = map.Entries.FirstOrDefault(e => e.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        totalBytes += entry.PackedSize;
                    }
                }
                report.EstimatedDownloadBytes = totalBytes;
            }
            catch (Exception ex)
            {
                report.Error = $"Analysis failed: {ex.Message}";
                Logger.LogError("AnalyzeGame", ex);
            }
            return report;
        }

        public static async Task PerformSurgicalRepairAsync(string storagePath, string contentPath, RepairReport report, string downloadUrl, Action<string, double> onProgress, CancellationToken ct = default)
        {
            string mapPath = Path.Combine(storagePath, MapFileName);
            var map = JsonSerializer.Deserialize<ArchiveMap>(File.ReadAllText(mapPath))!;
            byte[] prefix = Convert.FromBase64String(map.PrefixBase64);

            var toRepair = report.MissingFiles.Concat(report.CorruptedFiles).ToList();
            int total = toRepair.Count;
            int current = 0;

            string? commonRoot = null;
            if (map.Entries.Count > 0)
            {
                var firstParts = map.Entries[0].Path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (firstParts.Length > 1)
                {
                    string possibleRoot = firstParts[0] + "\\";
                    if (map.Entries.All(e => e.Path.StartsWith(possibleRoot, StringComparison.OrdinalIgnoreCase)))
                    {
                        commonRoot = possibleRoot;
                    }
                }
            }

            foreach (var relPath in toRepair)
            {
                ct.ThrowIfCancellationRequested();
                current++;
                onProgress?.Invoke($"Extracting {Path.GetFileName(relPath)}...", (current * 100.0) / total);

                var entry = map.Entries.FirstOrDefault(e => e.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;

                string targetPath = relPath;
                string fullPath = Path.Combine(contentPath, targetPath);

                
                if (!File.Exists(fullPath) && commonRoot != null && targetPath.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string stripped = targetPath.Substring(commonRoot.Length);
                    if (File.Exists(Path.Combine(contentPath, stripped)) || !File.Exists(fullPath)) 
                    {
                        targetPath = stripped;
                    }
                }

                
                if (prefix == null || prefix.Length < 7)
                {
                    Logger.Log($"[Repair-Surgical] Prefix is missing or invalid. Auto-repairing prefix from CDN...");
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
                        File.WriteAllText(mapPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
                        Logger.Log($"[Repair-Surgical] Prefix repaired! Size: {prefix.Length} bytes. First File @ {firstOff}");
                    }
                }
                

                var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                    
                    
                    long rangeStart = entry.HeaderOffset;
                    long rangeEnd = entry.DataOffset + entry.PackedSize - 1;

                    
                    
                    var sortedEntries = map.Entries.OrderBy(e => e.HeaderOffset).ToList();
                    var nextEntry = sortedEntries.FirstOrDefault(e => e.HeaderOffset > entry.HeaderOffset);
                    if (nextEntry != null)
                    {
                        rangeEnd = nextEntry.HeaderOffset - 1;
                    }
                    else if (map.TotalArchiveSize > 0)
                    {
                        rangeEnd = map.TotalArchiveSize - 1;
                    }

                    
                    long safetyCap = entry.DataOffset + entry.PackedSize + (1024 * 1024 * 10);
                    if (rangeEnd > safetyCap) rangeEnd = safetyCap;

                    request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);
                    if (downloadUrl.Contains("gofile.io") && !string.IsNullOrEmpty(GoFileClient.AccountToken))
                        request.Headers.Add("Cookie", $"accountToken={GoFileClient.AccountToken}");

                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent && response.StatusCode != System.Net.HttpStatusCode.OK)
                        {
                            Logger.Log($"[Repair-Surgical] ERROR: Server returned {response.StatusCode} for {relPath}. Range: {rangeStart}-{rangeEnd}");
                            continue;
                        }

                    byte[] chunkData = await response.Content.ReadAsByteArrayAsync(ct);

                    
                    if (chunkData.Length > 15 && Encoding.ASCII.GetString(chunkData, 0, 15).ToLowerInvariant().Contains("<!doctype html"))
                    {
                        Logger.Log($"[Repair-Surgical] ❌ ERROR: Server returned HTML instead of RAR data for {relPath}. This usually means the download link has expired or is blocked by a challenge (e.g., Cloudflare/DDoS protection). Please try resolving a new link.");
                        continue;
                    }
                    

                    using (var ms = new MemoryStream())
                    {
                        
                        
                        if (entry.HeaderOffset == 0)
                        {
                            ms.Write(chunkData, 0, chunkData.Length);
                        }
                        else
                        {
                            ms.Write(prefix, 0, prefix.Length);
                            ms.Write(chunkData, 0, chunkData.Length);
                        }
                        ms.Position = 0;

                        try 
                        {
                            using (var reader = RarReader.Open(ms))
                            {
                                if (reader.MoveToNextEntry())
                                {
                                    string outDir = Path.GetDirectoryName(Path.Combine(contentPath, targetPath))!;
                                    Directory.CreateDirectory(outDir);
                                    reader.WriteEntryToDirectory(outDir, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                                    Logger.Log($"[Repair-Surgical] ✅ Successfully extracted {relPath}");
                                }
                                else
                                {
                                    throw new Exception("No entries found in micro-RAR.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            
                            Logger.Log($"[Repair-Surgical] Primary extraction failed for {relPath}. Attempting fallback WITHOUT prefix...");
                            try {
                                ms.SetLength(0);
                                ms.Write(chunkData, 0, chunkData.Length);
                                ms.Position = 0;
                                using (var fallbackReader = RarReader.Open(ms))
                                {
                                    if (fallbackReader.MoveToNextEntry())
                                    {
                                        string outDir = Path.GetDirectoryName(Path.Combine(contentPath, targetPath))!;
                                        Directory.CreateDirectory(outDir);
                                        fallbackReader.WriteEntryToDirectory(outDir, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                                        Logger.Log($"[Repair-Surgical] ✅ Fallback successful for {relPath}");
                                        continue; 
                                    }
                                }
                            } catch { }

                            Logger.LogError($"[Repair-Surgical] RarReader failed for {relPath} (Offset: {entry.HeaderOffset}, Chunk: {chunkData.Length} bytes)", ex);
                            throw;
                        }
                    }
                }
            }
            
            
            TriggerManualBackup(storagePath, contentPath);
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
                    long headerSize = ReadVInt(data, ref pos);
                    int afterSizePos = pos;
                    long headerType = ReadVInt(data, ref pos);

                    if (headerType == 1) 
                    {
                        pos = (int)(afterSizePos + headerSize);
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

        private static long ReadVInt(byte[] data, ref int pos)
        {
            long result = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }
    }

    public class PositionTrackingStream : Stream
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
}
