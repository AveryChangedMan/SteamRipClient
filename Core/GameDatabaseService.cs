using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{

    public sealed class DatabaseModification
    {
        public string GameName { get; init; } = "";
        public string OldLink  { get; init; } = "";
        public string NewLink  { get; init; } = "";
    }

    public sealed class DatabaseDiff
    {

        public List<string> Added   { get; init; } = new();

        public List<string> Removed { get; init; } = new();

        public List<DatabaseModification> Modified { get; init; } = new();
        public string PreviousFile { get; init; } = "";
        public string NewFile      { get; init; } = "";
        public DateTime ComputedAt { get; init; } = DateTime.UtcNow;
        public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0;
    }

    public static class GameDatabaseService
    {

        private const string BaseName       = "BASE";
        private const string FreshPrefix    = "steamrip_full_games_";
        private const string FreshExtension = ".json";
        private const long   MinValidBytes  = 3 * 1024 * 1024;
        private const double RefreshHours   = 7.0;

        private static string DbFolder => Path.Combine(AppContext.BaseDirectory, "Redist", "Deps");

        public static event Action<string, double>?  ProgressChanged;

        public static event Action<DatabaseDiff>?    DatabaseSwapped;

        private static CancellationTokenSource? _runningCts;
        public  static bool     IsRunning      => _runningCts != null;

        public static JsonArray? Games          { get; private set; }
        public static string     ActiveFilePath { get; private set; } = "";
        public static bool       IsBaseFile     { get; private set; }
        public static DateTime   ActiveFileAge  { get; private set; } = DateTime.MinValue;
        public static string     LastRefreshStatus { get; private set; } = "Not yet refreshed.";

        public static DatabaseDiff? LastDiff { get; private set; }

        public static async Task InitialiseAsync()
        {
            LoadBestDatabase();

            if (NeedsRefresh())
            {
                Logger.Log("[GameDB] Database is stale (>7 h). Starting background refresh.");
                _ = RefreshNowAsync();
            }
            await Task.CompletedTask;
        }

        public static bool NeedsRefresh() =>
            ActiveFileAge == DateTime.MinValue ||
            (DateTime.UtcNow - ActiveFileAge).TotalHours >= RefreshHours;

        public static async Task RefreshNowAsync(CancellationToken externalCt = default)
        {
            if (_runningCts != null) return;
            _runningCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var ct = _runningCts.Token;

            try
            {
                Report("Starting…", 0);
                Logger.Log("[GameDB] Refresh started.");

                Report("Phase 1 – collecting game list…", 2);
                var stubs = await SteamRipGameInfoScraper.ScrapeAllSearchPagesAsync(
                    (msg, pct) => { if (!ct.IsCancellationRequested) Report(msg, pct * 0.40); }, ct);
                Report($"Phase 1 complete — {stubs.Count} games found.", 40);
                ct.ThrowIfCancellationRequested();

                Report("Phase 2 – fetching game details…", 42);
                var enriched = await SteamRipGameInfoScraper.EnrichAllGamesAsync(stubs,
                    (msg, pct) => { if (!ct.IsCancellationRequested) Report(msg, 40 + pct * 0.45); }, ct);
                Report($"Phase 2 complete — {enriched.Count} games enriched.", 85);
                ct.ThrowIfCancellationRequested();

                Report("Writing database…", 86);
                string newPath = SteamRipGameInfoScraper.WriteOutput(enriched, DbFolder);
                Report($"Saved → {Path.GetFileName(newPath)}", 90);

                Report("Rotating backup…", 91);
                RotateBackup(newPath);

                Report("Applying new database…", 96);
                var diff = SwapAndDiff(newPath);
                LastDiff = diff;
                DatabaseSwapped?.Invoke(diff);

                Report("Done!", 100);
                LastRefreshStatus = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm}  ·  {enriched.Count} games";
                Logger.Log($"[GameDB] Refresh complete. Active: {ActiveFilePath}  Added: {diff.Added.Count}  Removed: {diff.Removed.Count}");
            }
            catch (OperationCanceledException)
            {

                Logger.Log("[GameDB] Refresh cancelled. Previous database still in use.");
                LastRefreshStatus = "Refresh cancelled — existing database still active.";
                ProgressChanged?.Invoke("Cancelled", -1);
            }
            catch (Exception ex)
            {
                Logger.LogError("GameDatabaseService.Refresh", ex);
                LastRefreshStatus = $"Refresh failed: {ex.Message[..Math.Min(80, ex.Message.Length)]}";
                ProgressChanged?.Invoke($"Error: {ex.Message}", -2);
            }
            finally
            {
                _runningCts?.Dispose();
                _runningCts = null;
            }
        }

        public static void CancelRefresh() => _runningCts?.Cancel();

        private static void LoadBestDatabase()
        {
            string? fresh    = FindFreshFile();
            string? baseFile = FindBaseFile();

            if (fresh != null && new FileInfo(fresh).Length >= MinValidBytes)
                LoadFile(fresh, isBase: false);
            else if (baseFile != null)
            {
                Logger.Log($"[GameDB] Fresh DB missing/too small. Using BASE: {Path.GetFileName(baseFile)}");
                LoadFile(baseFile, isBase: true);
            }
            else
            {
                Logger.Log("[GameDB] No database file found — will download on refresh.");
                Games          = null;
                ActiveFilePath = "";
            }
        }

        private static DatabaseDiff SwapAndDiff(string newPath)
        {

            var prevLinks = SnapshotLinks(Games);
            string prevFile = ActiveFilePath;

            LoadFile(newPath, isBase: false);

            var newLinks = SnapshotLinks(Games);

            var rawAdded   = newLinks.Except(prevLinks,  StringComparer.OrdinalIgnoreCase).ToList();
            var rawRemoved = prevLinks.Except(newLinks,  StringComparer.OrdinalIgnoreCase).ToList();

            var added    = new List<string>();
            var removed  = new List<string>();
            var modified = new List<DatabaseModification>();

            static string GetSlug(string link)
            {
                string s = link.TrimEnd('/').Split('/').LastOrDefault() ?? link;
                return s.Replace("-free-download", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("-", " ")
                        .Trim();
            }

            var removedBySlug = rawRemoved.GroupBy(GetSlug, StringComparer.OrdinalIgnoreCase)
                                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var newLink in rawAdded)
            {
                string slug = GetSlug(newLink);
                if (removedBySlug.TryGetValue(slug, out var oldLinks) && oldLinks.Count > 0)
                {
                    string oldLink = oldLinks[0];
                    oldLinks.RemoveAt(0);
                    modified.Add(new DatabaseModification
                    {
                        GameName = slug,
                        OldLink  = oldLink,
                        NewLink  = newLink
                    });
                }
                else
                {
                    string? bestMatchSlug = removedBySlug.Keys.FirstOrDefault(k =>
                        k.StartsWith(slug, StringComparison.OrdinalIgnoreCase) ||
                        slug.StartsWith(k, StringComparison.OrdinalIgnoreCase));

                    if (bestMatchSlug != null && removedBySlug.TryGetValue(bestMatchSlug, out var oldFuzzyLinks) && oldFuzzyLinks.Count > 0)
                    {
                        string oldLink = oldFuzzyLinks[0];
                        oldFuzzyLinks.RemoveAt(0);
                        modified.Add(new DatabaseModification
                        {
                            GameName = slug,
                            OldLink  = oldLink,
                            NewLink  = newLink
                        });
                    }
                    else
                    {
                        added.Add(newLink);
                    }
                }
            }

            foreach (var kvp in removedBySlug)
            {
                removed.AddRange(kvp.Value);
            }

            added.Sort(StringComparer.OrdinalIgnoreCase);
            removed.Sort(StringComparer.OrdinalIgnoreCase);
            modified.Sort((a, b) => string.Compare(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase));

            return new DatabaseDiff
            {
                Added        = added,
                Removed      = removed,
                Modified     = modified,
                PreviousFile = Path.GetFileName(prevFile),
                NewFile      = Path.GetFileName(newPath),
                ComputedAt   = DateTime.UtcNow
            };
        }

        private static HashSet<string> SnapshotLinks(JsonArray? games)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (games == null) return set;
            foreach (var node in games)
            {
                string? link = node?["link"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(link)) set.Add(link);
            }
            return set;
        }

        private static void LoadFile(string path, bool isBase)
        {
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path));
                Games          = json as JsonArray ?? json?["games"]?.AsArray();
                ActiveFilePath = path;
                IsBaseFile     = isBase;
                ActiveFileAge  = File.GetLastWriteTimeUtc(path);
                Logger.Log($"[GameDB] Loaded {Games?.Count ?? 0} games from {Path.GetFileName(path)} (base={isBase})");
            }
            catch (Exception ex)
            {
                Logger.LogError($"GameDatabaseService.LoadFile({Path.GetFileName(path)})", ex);
            }
        }

        private static string? FindFreshFile()
        {
            if (!Directory.Exists(DbFolder)) return null;
            return Directory
                .EnumerateFiles(DbFolder, $"{FreshPrefix}*.json", SearchOption.TopDirectoryOnly)
                .Where(f => {
                    string name = Path.GetFileName(f);
                    return !name.Contains(BaseName, StringComparison.OrdinalIgnoreCase)
                        && name.StartsWith(FreshPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(FreshExtension, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
        }

        private static string? FindBaseFile()
        {
            if (!Directory.Exists(DbFolder)) return null;
            return Directory
                .EnumerateFiles(DbFolder, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).Contains(BaseName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
        }

        private static void RotateBackup(string newlyWrittenPath)
        {
            try
            {
                string? prev = Directory
                    .EnumerateFiles(DbFolder, $"{FreshPrefix}*.json", SearchOption.TopDirectoryOnly)
                    .Where(f => {
                        string name = Path.GetFileName(f);
                        return !name.Contains(BaseName, StringComparison.OrdinalIgnoreCase)
                            && name.StartsWith(FreshPrefix, StringComparison.OrdinalIgnoreCase)
                            && !f.Equals(newlyWrittenPath, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();

                if (prev == null) return;

                string bakPath = Path.Combine(DbFolder, "steamrip_full_games_prev.bak");
                if (File.Exists(bakPath)) File.Delete(bakPath);

                using (var src  = File.OpenRead(prev))
                using (var dst  = File.OpenWrite(bakPath))
                using (var gzip = new GZipStream(dst, CompressionLevel.Optimal))
                    src.CopyTo(gzip);

                File.Delete(prev);
                Logger.Log($"[GameDB] Archived previous DB → {Path.GetFileName(bakPath)}");
            }
            catch (Exception ex)
            {
                Logger.LogError("GameDatabaseService.RotateBackup", ex);
            }
        }

        private static void Report(string msg, double pct) =>
            ProgressChanged?.Invoke(msg, Math.Clamp(pct, 0, 100));

        public static JsonObject? FindByUrl(string url)
        {
            if (Games == null || string.IsNullOrEmpty(url)) return null;
            foreach (var node in Games)
                if (node?["link"]?.GetValue<string>()?.Equals(url, StringComparison.OrdinalIgnoreCase) == true)
                    return node?.AsObject();
            return null;
        }

        public static JsonObject? FindByTitle(string title)
        {
            if (Games == null || string.IsNullOrEmpty(title)) return null;
            foreach (var node in Games)
            {
                string? name = node?["name"]?.GetValue<string>();
                if (name?.Contains(title, StringComparison.OrdinalIgnoreCase) == true)
                    return node?.AsObject();
            }
            return null;
        }

        public static string GetDownloadsTagText(string link, string title)
        {
            if (Games == null) return "";

            JsonNode? matchedNode = null;
            foreach (var node in Games)
            {
                string? nodeLink = node?["link"]?.GetValue<string>();
                if (string.Equals(nodeLink, link, StringComparison.OrdinalIgnoreCase))
                {
                    matchedNode = node;
                    break;
                }
            }

            if (matchedNode == null && !string.IsNullOrEmpty(title))
            {
                foreach (var node in Games)
                {
                    string? nodeName = node?["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(nodeName) && (
                        title.StartsWith(nodeName, StringComparison.OrdinalIgnoreCase) ||
                        nodeName.StartsWith(title, StringComparison.OrdinalIgnoreCase) ||
                        title.Contains(nodeName, StringComparison.OrdinalIgnoreCase) ||
                        nodeName.Contains(title, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchedNode = node;
                        break;
                    }
                }
            }

            if (matchedNode != null)
            {
                string? dlStr = matchedNode["downloads_count"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(dlStr))
                {
                    string cleanDl = dlStr.Replace(",", "").Replace(".", "").Trim();
                    if (long.TryParse(cleanDl, out long count))
                    {
                        if (count >= 1000000)
                        {
                            double m = count / 1000000.0;
                            return $"{m:0.#}m";
                        }
                        else if (count >= 1000)
                        {
                            double k = count / 1000.0;
                            return $"{k:0.#}k";
                        }
                        else if (count > 0)
                        {
                            return $"{count}";
                        }
                    }
                    else
                    {
                        return dlStr;
                    }
                }
            }

            return "";
        }
    }
}