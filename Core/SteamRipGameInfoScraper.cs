using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace SteamRipApp.Core
{

    public sealed class ScrapedHiddenInformation
    {
        public int    WordCount    { get; set; }
        public string DateModified { get; set; } = "";
    }

    public sealed class ScrapedGame
    {
        public string                    Name              { get; set; } = "";
        public string                    Link              { get; set; } = "";
        public string                    Version           { get; set; } = "";
        public string                    CoverImage        { get; set; } = "";
        public List<string>              Requirements      { get; set; } = new();
        public string                    GameInfo          { get; set; } = "";
        public string                    UploadDate        { get; set; } = "";
        public string                    UploadDateText    { get; set; } = "";
        public string                    DownloadsCount    { get; set; } = "";
        public string                    HowToRun          { get; set; } = "";
        public List<string>              Warnings          { get; set; } = new();
        public string                    GameSize          { get; set; } = "";
        public List<string>              Genre             { get; set; } = new();
        public string                    Developer         { get; set; } = "";
        public string                    Publisher         { get; set; } = "";
        public string                    Platform          { get; set; } = "";
        public ScrapedHiddenInformation  HiddenInformation { get; set; } = new();
    }

    public static class SteamRipGameInfoScraper
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36";

        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer  = 200
        })
        { Timeout = TimeSpan.FromSeconds(20) };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented        = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        static SteamRipGameInfoScraper()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        public static async Task<List<ScrapedGame>> ScrapeAllSearchPagesAsync(
            Action<string, double>? onProgress = null,
            CancellationToken ct = default)
        {
            const string BaseUrl     = "https://steamrip.com/";
            const string SearchQuery = "Free download";

            onProgress?.Invoke("Fetching page 1 to detect page count…", 0);
            string firstHtml = await FetchWithRetryAsync(BaseUrl, ct, ("s", SearchQuery));
            if (string.IsNullOrEmpty(firstHtml)) return new();

            var firstDoc = ParseHtml(firstHtml);
            int maxPage  = DetectMaxPage(firstDoc);
            onProgress?.Invoke($"Detected {maxPage} search pages.", 2);

            var allGames = new ConcurrentBag<(int Page, ScrapedGame Game)>();
            foreach (var g in ParseSearchPage(firstDoc))
                allGames.Add((1, g));

            var semaphore = new SemaphoreSlim(16);
            int done = 1;

            var tasks = Enumerable.Range(2, maxPage - 1).Select(async page =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    string url  = $"https://steamrip.com/page/{page}/";
                    string html = await FetchWithRetryAsync(url, ct, ("s", SearchQuery));
                    if (!string.IsNullOrEmpty(html))
                        foreach (var g in ParseSearchPage(ParseHtml(html)))
                            allGames.Add((page, g));

                    int n = Interlocked.Increment(ref done);
                    onProgress?.Invoke($"Page {n}/{maxPage}", n * 100.0 / maxPage);
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            onProgress?.Invoke($"Phase 1 complete — {allGames.Count} games", 100);
            return allGames.OrderBy(x => x.Page).Select(x => x.Game).ToList();
        }

        private static int DetectMaxPage(HtmlDocument doc)
        {
            var links = doc.DocumentNode.SelectNodes("//ul[contains(@class,'pages-numbers')]//a");
            if (links is null) return 200;
            int max = 1;
            foreach (var a in links)
                if (int.TryParse(a.InnerText.Trim(), out int n) && n > max)
                    max = n;
            return max;
        }

        private static List<ScrapedGame> ParseSearchPage(HtmlDocument doc)
        {
            var posts = doc.DocumentNode.SelectNodes("//*[contains(@class,'post-element')]");
            if (posts is null) return new();

            var result = new List<ScrapedGame>();
            foreach (var post in posts)
            {
                var aElem = post.SelectSingleNode(".//*[contains(@class,'all-over-thumb-link')]")
                         ?? post.SelectSingleNode(".//h2//a");
                string href = aElem?.GetAttributeValue("href", "") ?? "";
                if (string.IsNullOrEmpty(href)) continue;
                if (href.StartsWith('/')) href = "https://steamrip.com" + href;
                else if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) href = "https://steamrip.com/" + href.TrimStart('/');

                var titleElem = post.SelectSingleNode(".//*[contains(@class,'thumb-title')]")
                             ?? post.SelectSingleNode(".//*[contains(@class,'the-post-title')]")
                             ?? aElem;
                string rawTitle = HtmlEntity.DeEntitize(titleElem?.InnerText.Trim() ?? "").Replace("’", "'").Replace("`", "'").Replace("‘", "'");
                (string name, string version) = ParseTitleVersion(rawTitle);

                string cover = "";
                var slide = post.SelectSingleNode(".//*[contains(@class,'slide')]");
                if (slide is not null)
                {
                    cover = slide.GetAttributeValue("data-back", "")
                         ?? slide.GetAttributeValue("data-back-webp", "");
                }
                if (string.IsNullOrEmpty(cover))
                {
                    var img = post.SelectSingleNode(".//img");
                    if (img is not null)
                    {
                        cover = img.GetAttributeValue("src", "")
                             ?? img.GetAttributeValue("data-src", "")
                             ?? img.GetAttributeValue("data-lazy-src", "");
                    }
                }
                if (!string.IsNullOrEmpty(cover) && cover.StartsWith('/'))
                    cover = "https://steamrip.com" + cover;
                if (!string.IsNullOrEmpty(cover) && !cover.StartsWith("http"))
                    cover = "https://steamrip.com/" + cover.TrimStart('/');

                result.Add(new ScrapedGame { Name = name, Link = href, Version = version, CoverImage = cover });
            }
            return result;
        }

        public static async Task<List<ScrapedGame>> EnrichAllGamesAsync(
            List<ScrapedGame> stubs,
            Action<string, double>? onProgress = null,
            CancellationToken ct = default)
        {
            var results   = new ScrapedGame[stubs.Count];
            var semaphore = new SemaphoreSlim(100);
            int done      = 0;
            int total     = stubs.Count;

            var tasks = stubs.Select(async (stub, i) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    try   { results[i] = await FetchGameDetailsAsync(stub, ct); }
                    catch { results[i] = stub; }

                    int n = Interlocked.Increment(ref done);
                    if (n % 50 == 0 || n == total)
                        onProgress?.Invoke($"Detailed {n}/{total} games…", n * 100.0 / total);
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            return results.Where(g => g is not null).ToList();
        }

        private static async Task<ScrapedGame> FetchGameDetailsAsync(ScrapedGame game, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(game.Link)) return game;

            string html = await FetchWithRetryAsync(game.Link, ct);
            if (string.IsNullOrEmpty(html)) return game;

            var doc = ParseHtml(html);

            var ldScript = doc.DocumentNode.SelectSingleNode("//script[@type='application/ld+json']");
            if (ldScript is not null)
            {
                try
                {
                    var root  = JsonNode.Parse(ldScript.InnerText);
                    var graph = root?["@graph"]?.AsArray();
                    if (graph is not null)
                    {
                        foreach (var node in graph)
                        {
                            if (node?["@type"]?.GetValue<string>() == "Article")
                            {
                                game.UploadDate = node["datePublished"]?.GetValue<string>() ?? "";
                                game.HiddenInformation.WordCount     = node["wordCount"]?.GetValue<int>() ?? 0;
                                game.HiddenInformation.DateModified  = node["dateModified"]?.GetValue<string>() ?? "";

                                var sectionNode = node["articleSection"];
                                if (sectionNode is JsonArray sArr)
                                    game.Genre = sArr.Select(x => x?.GetValue<string>() ?? "").Where(s => s != "").ToList();
                                else if (sectionNode?.GetValue<string>() is string s && s != "")
                                    game.Genre = new() { s };

                                var keywordsNode = node["keywords"];
                                if (keywordsNode is JsonArray kArr)
                                    game.Publisher = string.Join(", ", kArr.Select(x => x?.GetValue<string>() ?? "").Where(s => s != ""));
                                else if (keywordsNode?.GetValue<string>() is string k && k != "")
                                    game.Publisher = k;
                            }
                        }
                    }
                }
                catch {  }
            }

            var metaBox = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'single-post-meta')]");
            if (metaBox is not null)
            {
                var dateSpan  = metaBox.SelectSingleNode(".//*[contains(@class,'date')]");
                var viewsSpan = metaBox.SelectSingleNode(".//*[contains(@class,'meta-views')]");
                if (dateSpan  is not null) game.UploadDateText = dateSpan.InnerText.Trim();
                if (viewsSpan is not null) game.DownloadsCount = viewsSpan.InnerText.Trim();
            }

            var reqPrefixes  = new[] { "OS:", "Processor:", "Memory:", "Graphics:", "DirectX:", "Storage:", "Sound Card:", "Network:" };
            var sidebarNodes = doc.DocumentNode.SelectNodes("//li | //p");
            if (sidebarNodes is not null)
            {
                foreach (var node in sidebarNodes)
                {
                    string text = HtmlEntity.DeEntitize(node.InnerText.Trim());
                    if (node.Name == "li" && reqPrefixes.Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !game.Requirements.Contains(text))
                        game.Requirements.Add(text);
                    if (text.Contains("Game Size:", StringComparison.OrdinalIgnoreCase))
                        game.GameSize = text.Replace("Game Size:", "", StringComparison.OrdinalIgnoreCase).Trim();
                    else if (text.Contains("Developer:", StringComparison.OrdinalIgnoreCase))
                        game.Developer = text.Replace("Developer:", "", StringComparison.OrdinalIgnoreCase).Trim();
                    else if (text.Contains("Publisher:", StringComparison.OrdinalIgnoreCase))
                        game.Publisher = text.Replace("Publisher:", "", StringComparison.OrdinalIgnoreCase).Trim();
                    else if (text.Contains("Platform:", StringComparison.OrdinalIgnoreCase))
                        game.Platform = text.Replace("Platform:", "", StringComparison.OrdinalIgnoreCase).Trim();
                    else if (text.Contains("Genre:", StringComparison.OrdinalIgnoreCase) && game.Genre.Count == 0)
                    {
                        string genres = text.Replace("Genre:", "", StringComparison.OrdinalIgnoreCase).Trim();
                        game.Genre = genres.Split(',').Select(g => g.Trim()).Where(g => g != "").ToList();
                    }
                }
            }

            var blockquotes = doc.DocumentNode.SelectNodes("//blockquote");
            if (blockquotes is not null)
            {
                foreach (var bq in blockquotes)
                {
                    string bqText = HtmlEntity.DeEntitize(bq.InnerText.Trim());
                    if (bqText.Contains("HOW TO RUN", StringComparison.OrdinalIgnoreCase))
                        game.HowToRun = bqText;
                    else if (ContainsWarningKeyword(bqText) && !game.Warnings.Contains(bqText))
                        game.Warnings.Add(bqText);
                }
            }
            var warnNodes = doc.DocumentNode.SelectNodes("//p | //div | //strong");
            if (warnNodes is not null)
            {
                foreach (var node in warnNodes)
                {
                    string t = HtmlEntity.DeEntitize(node.InnerText.Trim());
                    if (StartsWithWarningKeyword(t) && !game.Warnings.Contains(t) && t != game.HowToRun)
                        game.Warnings.Add(t);
                }
            }

            var entry = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'entry-content')]");
            if (entry is not null)
            {
                var skip  = new[] { "download here", "steamrip is the arena", "only trust the official site", "mega.nz", "gofile", "megadb", "buzzheavier", "1fichier" };
                var paras = new List<string>();
                foreach (var p in entry.SelectNodes("./p") ?? Enumerable.Empty<HtmlNode>())
                {
                    string pText = HtmlEntity.DeEntitize(p.InnerText.Trim());
                    if (string.IsNullOrEmpty(pText)) continue;
                    if (skip.Any(k => pText.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                    paras.Add(pText);
                }
                game.GameInfo = string.Join("\n\n", paras);
            }

            return game;
        }

        public static string WriteOutput(List<ScrapedGame> games, string folder)
        {
            Directory.CreateDirectory(folder);
            string dateTag = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string path    = Path.Combine(folder, $"steamrip_full_games_{dateTag}.json");

            var root = new { generated_at = DateTime.UtcNow.ToString("o"), total_games = games.Count, games };
            File.WriteAllText(path, JsonSerializer.Serialize(root, JsonOpts), Encoding.UTF8);
            return path;
        }

        private static async Task<string> FetchWithRetryAsync(string url, CancellationToken ct,
            params (string Key, string Value)[] queryParams)
        {
            if (queryParams.Length > 0)
            {
                string qs = string.Join("&", queryParams.Select(p =>
                    $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
                url = url + (url.Contains('?') ? "&" : "?") + qs;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var resp = await Http.GetAsync(url, ct);
                    if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
                        continue;
                    }
                    if (resp.IsSuccessStatusCode)
                        return await resp.Content.ReadAsStringAsync(ct);
                    return "";
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException)       {  }
            }
            return "";
        }

        private static HtmlDocument ParseHtml(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc;
        }

        private static (string Name, string Version) ParseTitleVersion(string raw)
        {
            var parts = Regex.Split(raw, @"\s*Free Down[l]?oad\s*", RegexOptions.IgnoreCase);
            if (parts.Length > 1)
            {
                string name      = parts[0].Trim();
                string remainder = parts[1].Trim();
                var bracket = Regex.Match(remainder, @"[(\[](.*?)[)\]]");
                string version = bracket.Success
                    ? bracket.Groups[1].Value.Trim()
                    : remainder.Replace("(", "").Replace(")", "").Trim();
                return (name, version);
            }
            var bracketOnly = Regex.Match(raw, @"^(.*?)\s*[(\[](.*?)[)\]]$");
            if (bracketOnly.Success)
                return (bracketOnly.Groups[1].Value.Trim(), bracketOnly.Groups[2].Value.Trim());
            return (raw.Trim(), "");
        }

        private static bool ContainsWarningKeyword(string text) =>
            text.Contains("warning",   StringComparison.OrdinalIgnoreCase) ||
            text.Contains("note",      StringComparison.OrdinalIgnoreCase) ||
            text.Contains("important", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("notice",    StringComparison.OrdinalIgnoreCase) ||
            text.Contains("antivirus", StringComparison.OrdinalIgnoreCase);

        private static bool StartsWithWarningKeyword(string text)
        {
            var prefixes = new[] { "warning:", "note:", "important:", "notice:" };
            return prefixes.Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }
    }
}