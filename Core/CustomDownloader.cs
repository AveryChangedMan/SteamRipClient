using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{
    public enum DownloadState { Idle, Downloading, Paused, Cancelled, Failed, Completed }

    public class DownloadProgressStats
    {
        public double Percentage { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedMBps { get; set; }
        public TimeSpan ETA { get; set; }
        public int ActiveThreads { get; set; }
    }

    
    
    
    
    public class ChunkState
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long Downloaded { get; set; }
    }

    public class DownloadManifest
    {
        public string DownloadUrl { get; set; } = "";
        public string BuzzheavierPageUrl { get; set; } = "";
        public long TotalBytes { get; set; }
        public int ThreadCount { get; set; }
        public List<ChunkState> Chunks { get; set; } = new();
    }

    public class CustomDownloader
    {
        public string DownloadUrl { get; private set; }
        public string DestinationPath { get; }
        public string BuzzheavierPageUrl { get; set; } = "";
        public int ThreadCount { get; set; } = 12;
        public string? GoFileToken { get; set; }
        public const int MaxThreads = 4; 

        public event EventHandler<DownloadProgressStats>? ProgressChanged;
        public event EventHandler? DownloadCompleted;
        public event EventHandler<string>? DownloadFailed;
        
        public event Func<string, Task<string>>? LinkExpired;

        private CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _pauseEvent = new(true); 
        private DownloadState _state = DownloadState.Idle;
        private string ManifestPath => DestinationPath + ".progress";

        
        public static Dictionary<string, CustomDownloader> ActiveInstances { get; } = new();

        public CustomDownloader(string url, string destPath)
        {
            DownloadUrl = url;
            DestinationPath = destPath;
        }

        public void Pause()
        {
            _state = DownloadState.Paused;
            _pauseEvent.Reset(); 
            SaveManifest();
            Logger.Log("[Downloader] Paused.");
        }

        public void Resume()
        {
            _state = DownloadState.Downloading;
            _pauseEvent.Set(); 
            Logger.Log("[Downloader] Resumed.");
        }

        public void Cancel()
        {
            _state = DownloadState.Cancelled;
            _pauseEvent.Set(); 
            _cts.Cancel();
            Logger.Log("[Downloader] Cancelled.");
        }

        public DownloadState State => _state;

        private DownloadManifest? _manifest;
        private long[] _chunkBytesDownloaded = Array.Empty<long>();

        public async Task StartDownloadAsync()
        {
            ActiveInstances[DestinationPath] = this;
            _state = DownloadState.Downloading;
            
            if (!string.IsNullOrEmpty(GoFileToken)) ThreadCount = Math.Min(ThreadCount, MaxThreads);

            try
            {
                
                if (DownloadUrl.Contains("gofile.io/d/") || DownloadUrl.Contains("bzzhr.to") || DownloadUrl.Contains("buzzheavier"))
                {
                    Logger.Log($"[Downloader] Host page detected ({DownloadUrl}). Resolving direct link...");
                    var resolved = await UrlResolver.ResolveDirectUrlAsync(DownloadUrl);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        Logger.Log($"[Downloader] Successfully resolved to: {resolved}");
                        if (DownloadUrl.Contains("bzzhr.to") || DownloadUrl.Contains("buzzheavier"))
                            BuzzheavierPageUrl = DownloadUrl; 
                        DownloadUrl = resolved;
                    }
                }

                
                _manifest = LoadManifest();

                if (_manifest != null && File.Exists(DestinationPath))
                {
                    Logger.Log($"[Downloader] Resuming from manifest. {_manifest.Chunks.Count} chunks, {_manifest.TotalBytes} total bytes.");
                    
                    if (_manifest.DownloadUrl != DownloadUrl)
                    {
                        Logger.Log($"[Downloader] Download URL updated in manifest.");
                        _manifest.DownloadUrl = DownloadUrl;
                    }
                }
                else
                {
                    
                    var (totalBytes, supportsRanges) = await ProbeServerAsync(DownloadUrl, GoFileToken);

                    if (totalBytes <= 0 || !supportsRanges)
                    {
                        Logger.Log($"[Downloader] Server doesn't support ranges or unknown size ({totalBytes}). Falling back to single-thread.");
                        await SingleThreadDownloadAsync();
                        return;
                    }

                    Logger.Log($"[Downloader] File size: {totalBytes} bytes ({totalBytes / (1024.0 * 1024):F1} MB). Ranges supported. Using {ThreadCount} threads.");

                    
                    using (var fs = new FileStream(DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        fs.SetLength(totalBytes);
                    }

                    
                    _manifest = new DownloadManifest
                    {
                        DownloadUrl = DownloadUrl,
                        BuzzheavierPageUrl = BuzzheavierPageUrl,
                        TotalBytes = totalBytes,
                        ThreadCount = ThreadCount,
                        Chunks = BuildChunks(totalBytes, ThreadCount)
                    };
                    SaveManifest();
                }

                _chunkBytesDownloaded = new long[_manifest.Chunks.Count];
                for (int i = 0; i < _manifest.Chunks.Count; i++)
                    _chunkBytesDownloaded[i] = _manifest.Chunks[i].Downloaded;

                
                var sw = Stopwatch.StartNew();
                var tasks = new List<Task>();
                var semaphore = new SemaphoreSlim(ThreadCount);

                for (int i = 0; i < _manifest.Chunks.Count; i++)
                {
                    var chunk = _manifest.Chunks[i];
                    if (chunk.Downloaded >= (chunk.End - chunk.Start + 1))
                    {
                        
                        continue;
                    }
                    int idx = i;
                    tasks.Add(Task.Run(() => DownloadChunkAsync(idx, chunk, semaphore, sw)));
                }

                
                var progressTask = Task.Run(async () =>
                {
                    long lastBytes = 0;
                    double lastTime = 0;
                    var speedSamples = new Queue<double>();
                    const int maxSamples = 10; 

                    while (_state == DownloadState.Downloading || _state == DownloadState.Paused)
                    {
                        await Task.Delay(300);
                        var totalDownloaded = _chunkBytesDownloaded.Sum();
                        var elapsed = sw.Elapsed.TotalSeconds;
                        var delta = totalDownloaded - lastBytes;
                        var dt = elapsed - lastTime;
                        
                        
                        var instantSpeed = dt > 0 ? (delta / (1024.0 * 1024.0)) / dt : 0;
                        
                        
                        speedSamples.Enqueue(instantSpeed);
                        if (speedSamples.Count > maxSamples) speedSamples.Dequeue();
                        var averageSpeed = speedSamples.Average();

                        var pct = _manifest.TotalBytes > 0 ? (totalDownloaded * 100.0 / _manifest.TotalBytes) : 0;
                        var eta = averageSpeed > 0 && _manifest.TotalBytes > 0
                            ? TimeSpan.FromSeconds((_manifest.TotalBytes - totalDownloaded) / (averageSpeed * 1024 * 1024))
                            : TimeSpan.Zero;

                        var activeThreads = ThreadCount - (int)semaphore.CurrentCount;
                        if (activeThreads < 0) activeThreads = 0;

                        
                        try {
                            string? root = Path.GetPathRoot(Path.GetFullPath(DestinationPath));
                            if (!string.IsNullOrEmpty(root))
                            {
                                var drive = new DriveInfo(root);
                                if (drive.AvailableFreeSpace <= 5L * 1024 * 1024 * 1024)
                                {
                                    Logger.Log($"[Downloader] 🛑 CRITICAL HALT: Drive {root} reached 5GB limit.");
                                    _state = DownloadState.Failed;
                                    _cts.Cancel();
                                    DownloadFailed?.Invoke(this, "HALTED: Disk space critically low (5GB remaining). Please free up space.");
                                    break;
                                }
                            }
                        } catch { }

                        ProgressChanged?.Invoke(this, new DownloadProgressStats
                        {
                            Percentage = Math.Min(pct, 100),
                            BytesReceived = totalDownloaded,
                            TotalBytes = _manifest.TotalBytes,
                            SpeedMBps = averageSpeed,
                            ETA = eta,
                            ActiveThreads = activeThreads
                        });

                        lastBytes = totalDownloaded;
                        lastTime = elapsed;

                        if (totalDownloaded >= _manifest.TotalBytes) break;
                        if (_state == DownloadState.Cancelled || _state == DownloadState.Failed) break;
                    }
                });

                await Task.WhenAll(tasks);
                await progressTask;

                if (_state == DownloadState.Cancelled)
                {
                    DownloadFailed?.Invoke(this, "Cancelled");
                    return;
                }

                if (_state == DownloadState.Paused)
                {
                    
                    SaveManifest();
                    return;
                }

                
                var totalDone = _chunkBytesDownloaded.Sum();
                if (totalDone >= _manifest.TotalBytes)
                {
                    _state = DownloadState.Completed;
                    CleanupManifest();
                    DownloadCompleted?.Invoke(this, EventArgs.Empty);
                    Logger.Log($"[Downloader] ✅ Download complete. {totalDone} bytes.");
                }
                else
                {
                    _state = DownloadState.Failed;
                    SaveManifest();
                    DownloadFailed?.Invoke(this, $"Incomplete: {totalDone}/{_manifest.TotalBytes}");
                }
            }
            catch (Exception ex)
            {
                _state = DownloadState.Failed;
                Logger.LogError("[Downloader] Fatal error", ex);
                DownloadFailed?.Invoke(this, ex.Message);
            }
            finally
            {
                ActiveInstances.Remove(DestinationPath);
            }
        }

        private async Task DownloadChunkAsync(int index, ChunkState chunk, SemaphoreSlim sem, Stopwatch sw)
        {
            await sem.WaitAsync(_cts.Token);
            try
            {
                while (true)
                {
                    var chunkSize = chunk.End - chunk.Start + 1;
                    var startPos = chunk.Start + chunk.Downloaded;
                    var remaining = chunkSize - chunk.Downloaded;
                    
                    if (remaining <= 0) break; 

                    Logger.Log($"[Downloader][T{index}] Starting/Resuming chunk {startPos}-{chunk.End} ({remaining} bytes remaining)");

                    try
                    {
                        using var client = CreateHttpClient();
                        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startPos, chunk.End);

                        HttpResponseMessage response;
                        try
                        {
                            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                        }
                        catch (Exception ex) when (ex is HttpRequestException || ex is TimeoutException || ex is TaskCanceledException)
                        {
                            bool isForbidden = (ex as HttpRequestException)?.StatusCode == System.Net.HttpStatusCode.Forbidden;
                            bool isTimeout = ex is TimeoutException || (ex is TaskCanceledException && !_cts.IsCancellationRequested);

                            if (isForbidden || isTimeout)
                            {
                                Logger.Log($"[Downloader][T{index}] Connection issue (Forbidden={isForbidden}, Timeout={isTimeout}). Attempting renewal...");
                                
                                if (LinkExpired != null && !string.IsNullOrEmpty(BuzzheavierPageUrl))
                                {
                                    var newUrl = await LinkExpired.Invoke(BuzzheavierPageUrl);
                                    if (!string.IsNullOrEmpty(newUrl))
                                    {
                                        DownloadUrl = newUrl;
                                        if (_manifest != null) _manifest.DownloadUrl = newUrl;
                                        SaveManifest();
                                        
                                        await Task.Delay(2000, _cts.Token); 
                                        continue; 
                                    }
                                }
                            }
                            throw;
                        }

                        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        {
                            Logger.Log($"[Downloader][T{index}] Error: {response.StatusCode}");
                            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                            {
                                throw new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden);
                            }
                            throw new Exception($"HTTP Error {response.StatusCode}");
                        }

                        using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
                        using var fs = new FileStream(DestinationPath, FileMode.Open, FileAccess.Write, FileShare.Write);
                        fs.Seek(startPos, SeekOrigin.Begin);

                        var buffer = new byte[131072]; 
                        while (true)
                        {
                            _pauseEvent.Wait(_cts.Token);

                            var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                            readCts.CancelAfter(TimeSpan.FromSeconds(15)); 
                            
                            int bytesRead = 0;
                            try {
                                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                            } catch (OperationCanceledException) when (!_cts.IsCancellationRequested) {
                                throw new TimeoutException("Read timeout");
                            }

                            if (bytesRead <= 0) break;

                            fs.Write(buffer, 0, bytesRead);
                            chunk.Downloaded += bytesRead;
                            Interlocked.Add(ref _chunkBytesDownloaded[index], bytesRead);
                            
                            if (chunk.Downloaded % (1024 * 1024 * 10) == 0) SaveManifest(); 
                        }

                        if (chunk.Downloaded >= chunkSize) break; 
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Log($"[Downloader][T{index}] Cancelled.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[Downloader][T{index}] Chunk error", ex);
                        Logger.Log($"[Downloader][T{index}] Retrying chunk in 5s...");
                        await Task.Delay(5000, _cts.Token);
                    }
                }
                
                Logger.Log($"[Downloader][T{index}] Chunk complete. Downloaded {chunk.Downloaded} bytes.");
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"[Downloader][T{index}] Task Cancelled.");
            }
            finally
            {
                sem.Release();
            }
        }

        private async Task SingleThreadDownloadAsync()
        {
            Logger.Log("[Downloader] Single-thread fallback mode.");
            using var client = CreateHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
            using var fs = new FileStream(DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);

            var buffer = new byte[131072];
            long totalRead = 0;
            int bytesRead;
            var sw = Stopwatch.StartNew();
            long lastBytes = 0;
            double lastTime = 0;
            var speedSamples = new Queue<double>();
            const int maxSamples = 10; 

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
            {
                _pauseEvent.Wait(_cts.Token);
                await fs.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                totalRead += bytesRead;

                var elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed - lastTime >= 0.3)
                {
                    var delta = totalRead - lastBytes;
                    var dt = elapsed - lastTime;
                    
                    var instantSpeed = dt > 0 ? (delta / (1024.0 * 1024.0)) / dt : 0;
                    
                    speedSamples.Enqueue(instantSpeed);
                    if (speedSamples.Count > maxSamples) speedSamples.Dequeue();
                    var averageSpeed = speedSamples.Average();

                    var pct = totalBytes > 0 ? (totalRead * 100.0 / totalBytes) : 0;
                    if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0;

                    var eta = averageSpeed > 0 && totalBytes > 0
                        ? TimeSpan.FromSeconds((totalBytes - totalRead) / (averageSpeed * 1024 * 1024))
                        : TimeSpan.Zero;

                    ProgressChanged?.Invoke(this, new DownloadProgressStats
                    {
                        Percentage = Math.Clamp(pct, 0, 100),
                        BytesReceived = totalRead,
                        TotalBytes = totalBytes,
                        SpeedMBps = averageSpeed,
                        ETA = eta,
                        ActiveThreads = 1
                    });
                    lastBytes = totalRead;
                    lastTime = elapsed;
                }
            }

            _state = DownloadState.Completed;
            ProgressChanged?.Invoke(this, new DownloadProgressStats { Percentage = 100, BytesReceived = totalRead, TotalBytes = totalRead, SpeedMBps = 0, ETA = TimeSpan.Zero, ActiveThreads = 0 });
            DownloadCompleted?.Invoke(this, EventArgs.Empty);
            Logger.Log($"[Downloader] Single-thread download complete. {totalRead} bytes.");
        }

        public static async Task<(long totalBytes, bool supportsRanges)> ProbeServerAsync(string url, string? goFileToken = null)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            if (!string.IsNullOrEmpty(goFileToken))
            {
                client.DefaultRequestHeaders.Add("Cookie", $"accountToken={goFileToken}");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", goFileToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            try
            {
                var response = await client.SendAsync(request);
                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                var acceptRanges = response.Headers.Contains("Accept-Ranges")
                    && response.Headers.GetValues("Accept-Ranges").Any(v => v.Contains("bytes"));
                
                if (!acceptRanges && totalBytes > 0)
                {
                    using var rangeReq = new HttpRequestMessage(HttpMethod.Get, url);
                    rangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    var rangeResp = await client.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead);
                    acceptRanges = rangeResp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                    rangeResp.Dispose();
                }
                return (totalBytes, acceptRanges);
            }
            catch (Exception ex)
            {
                Logger.LogError("[Downloader] Probe failed", ex);
                return (-1, false);
            }
        }

        private static List<ChunkState> BuildChunks(long totalBytes, int threadCount)
        {
            var chunks = new List<ChunkState>();
            var chunkSize = totalBytes / threadCount;
            for (int i = 0; i < threadCount; i++)
            {
                var start = i * chunkSize;
                var end = (i == threadCount - 1) ? totalBytes - 1 : (start + chunkSize - 1);
                chunks.Add(new ChunkState { Start = start, End = end, Downloaded = 0 });
            }
            return chunks;
        }

        private void SaveManifest()
        {
            try
            {
                if (_manifest == null) return;
                var json = JsonSerializer.Serialize(_manifest, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ManifestPath, json);
            }
            catch (Exception ex) { Logger.LogError("[Downloader] SaveManifest", ex); }
        }

        private DownloadManifest? LoadManifest()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return null;
                var json = File.ReadAllText(ManifestPath);
                return JsonSerializer.Deserialize<DownloadManifest>(json);
            }
            catch (Exception ex)
            {
                Logger.LogError("[Downloader] LoadManifest", ex);
                return null;
            }
        }

        private void CleanupManifest()
        {
            try { if (File.Exists(ManifestPath)) File.Delete(ManifestPath); }
            catch { }
        }

        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromHours(4) };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            if (!string.IsNullOrEmpty(GoFileToken))
            {
                client.DefaultRequestHeaders.Add("Cookie", $"accountToken={GoFileToken}");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GoFileToken);
            }

            return client;
        }
    }
}
