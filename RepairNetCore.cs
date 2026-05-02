using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;

namespace RarSurgicalRepair
{
    public class RarMapEntry
    {
        public string FileName { get; set; } = string.Empty;
        public long HeaderOffset { get; set; }
        public long DataOffset { get; set; }
        public long CompressedSize { get; set; }
        public long UncompressedSize { get; set; }
        public string RarVersion { get; set; } = string.Empty;
    }

    public class RarSurgicalRepairer
    {
        private byte[] _archivePrefix = Array.Empty<byte>();
        private readonly HttpClient _httpClient;

        public RarSurgicalRepairer(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();

            _httpClient.DefaultRequestHeaders.ConnectionClose = false;
        }

        public async Task<List<RarMapEntry>> MapRemoteArchiveAsync(string url, string tempPath = "temp_mapping.rar", IProgress<double>? progress = null)
        {
            try
            {

                await DownloadFileWithProgressAsync(url, tempPath, progress);

                var mapping = MapLocalArchive(tempPath);

                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                return mapping;
            }
            catch (Exception ex)
            {
                throw new Exception($"Mapping failed: {ex.Message}", ex);
            }
        }

        public List<RarMapEntry> MapLocalArchive(string filePath)
        {
            var mapping = new List<RarMapEntry>();

            using (Stream stream = File.OpenRead(filePath))
            {

                using (var reader = RarReader.Open(stream))
                {
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory) continue;

                        long dataOffset = stream.Position;
                        long compressedSize = reader.Entry.CompressedSize;

                    }
                }
            }

            return PerformDeepMapping(filePath, out _archivePrefix);
        }

        private List<RarMapEntry> PerformDeepMapping(string filePath, out byte[] prefix)
        {
            var entries = new List<RarMapEntry>();
            long firstFileOffset = -1;

            using (Stream stream = File.OpenRead(filePath))
            {

                using (var reader = RarReader.Open(stream))
                {
                    while (true)
                    {
                        long headerOffset = stream.Position;
                        if (!reader.MoveToNextEntry()) break;

                        if (reader.Entry.IsDirectory) continue;

                        long dataOffset = stream.Position;

                        if (firstFileOffset == -1 || headerOffset < firstFileOffset)
                        {
                            firstFileOffset = headerOffset;
                        }

                        entries.Add(new RarMapEntry
                        {
                            FileName = reader.Entry.Key ?? "Unknown",
                            HeaderOffset = headerOffset,
                            DataOffset = dataOffset,
                            CompressedSize = reader.Entry.CompressedSize,
                            UncompressedSize = reader.Entry.Size,
                            RarVersion = reader.Entry.ToString().Contains("Rar5") ? "RAR5" : "RAR4"
                        });
                    }
                }

                if (firstFileOffset > 0)
                {
                    stream.Position = 0;
                    prefix = new byte[firstFileOffset];
                    int read = 0;
                    while (read < firstFileOffset)
                    {
                        int r = stream.Read(prefix, read, (int)firstFileOffset - read);
                        if (r == 0) break;
                        read += r;
                    }
                }
                else
                {
                    prefix = Array.Empty<byte>();
                }
            }

            return entries;
        }

        public async Task DownloadFileSurgicallyAsync(string url, RarMapEntry entry, string destinationPath, CancellationToken ct = default)
        {

            long rangeStart = entry.HeaderOffset;
            long rangeEnd = entry.DataOffset + entry.CompressedSize - 1;

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);

            using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var chunkData = await response.Content.ReadAsByteArrayAsync();

                using (var ms = new MemoryStream())
                {
                    ms.Write(_archivePrefix, 0, _archivePrefix.Length);
                    ms.Write(chunkData, 0, chunkData.Length);
                    ms.Position = 0;

                    using (var reader = RarReader.Open(ms))
                    {
                        while (reader.MoveToNextEntry())
                        {

                            string? destDir = Path.GetDirectoryName(destinationPath);
                            if (destDir != null)
                            {
                                reader.WriteEntryToDirectory(destDir, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                            }
                            break;
                        }
                    }
                }
            }
        }

        private async Task DownloadFileWithProgressAsync(string url, string destPath, IProgress<double>? progress)
        {
            using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalSize = response.Content.Headers.ContentLength ?? -1L;

                using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var buffer = new byte[128 * 1024];
                    long totalRead = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        if (totalSize != -1)
                        {
                            progress?.Report((double)totalRead / totalSize);
                        }
                    }
                }
            }
        }
    }
}