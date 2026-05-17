using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace SteamRipApp.Core
{
    public class CachedPageWrapper
    {
        public DateTime Timestamp { get; set; }
        public SearchResultPage Page { get; set; } = new SearchResultPage();
    }

    public static class DiskCacheService
    {
        public static void ClearCache()
        {
            try {
                var dir = Path.Combine(Path.GetTempPath(), "SteamRipApp_Cache");
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    Logger.Log("[DiskCache] Cleared all cached pages on startup.");
                }
            } catch (Exception ex) { Logger.Log($"[DiskCache] Clear error: {ex.Message}"); }
        }

        private static string GetCacheDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "SteamRipApp_Cache");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string GetCachePath(string key)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
            var filename = BitConverter.ToString(hash).Replace("-", "") + ".json";
            return Path.Combine(GetCacheDir(), filename);
        }

        public static async Task<SearchResultPage?> GetCachedPageAsync(string key)
        {
            try {
                var path = GetCachePath(key);
                if (!File.Exists(path)) return null;

                var json = await File.ReadAllTextAsync(path);
                var wrapper = JsonSerializer.Deserialize<CachedPageWrapper>(json);
                if (wrapper != null && (DateTime.UtcNow - wrapper.Timestamp).TotalMinutes <= 30)
                {
                    Logger.Log($"[DiskCache] Cache hit for: {key}");
                    return wrapper.Page;
                }
                else if (wrapper != null)
                {
                    Logger.Log($"[DiskCache] Cache expired for: {key}");
                    File.Delete(path);
                }
            } catch (Exception ex) { Logger.Log($"[DiskCache] Get error: {ex.Message}"); }
            return null;
        }

        public static async Task SaveCachedPageAsync(string key, SearchResultPage page)
        {
            try {
                var path = GetCachePath(key);
                var wrapper = new CachedPageWrapper { Timestamp = DateTime.UtcNow, Page = page };
                var json = JsonSerializer.Serialize(wrapper);
                await File.WriteAllTextAsync(path, json);
                Logger.Log($"[DiskCache] Saved cache for: {key}");
            } catch (Exception ex) { Logger.Log($"[DiskCache] Save error: {ex.Message}"); }
        }
    }

    public class SearchResult : INotifyPropertyChanged
    {
        private string _title = "";
        private string _url = "";
        private string _imageUrl = "";
        private string _dateString = "";
        private bool _isBuzzheavierAvailable = false;
        private string _buzzheavierUrl = "";
        private bool _isGoFileAvailable = false;
        private string _goFileUrl = "";

        private bool _isDownloading = false;
        private double _downloadProgress = 0;
        private string _downloadStatus = "Preparing...";

        private string _downloadButtonText = "Download";
        private string? _installedPath = null;

        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        public string Url { get => _url; set { _url = value; OnPropertyChanged(); } }
        public string ImageUrl { get => _imageUrl; set { _imageUrl = value; OnPropertyChanged(); } }
        public string DateString { get => _dateString; set { _dateString = value; OnPropertyChanged(); } }
        public bool IsBuzzheavierAvailable { get => _isBuzzheavierAvailable; set { _isBuzzheavierAvailable = value; OnPropertyChanged(); } }
        public string BuzzheavierUrl { get => _buzzheavierUrl; set { _buzzheavierUrl = value; OnPropertyChanged(); } }
        public bool IsGoFileAvailable { get => _isGoFileAvailable; set { _isGoFileAvailable = value; OnPropertyChanged(); } }
        public string GoFileUrl { get => _goFileUrl; set { _goFileUrl = value; OnPropertyChanged(); } }
        public bool HasAnyDownload => IsBuzzheavierAvailable || IsGoFileAvailable;

        public bool IsDownloading { get => _isDownloading; set { _isDownloading = value; OnPropertyChanged(); } }
        public double DownloadProgress { get => _downloadProgress; set { _downloadProgress = value; OnPropertyChanged(); } }
        public string DownloadStatus { get => _downloadStatus; set { _downloadStatus = value; OnPropertyChanged(); } }

        public string DownloadButtonText { get => _downloadButtonText; set { _downloadButtonText = value; OnPropertyChanged(); } }
        public string? InstalledPath { get => _installedPath; set { _installedPath = value; OnPropertyChanged(); } }

        public void CheckInstalledState()
        {
            var cleanTargetTitle = ScannerEngine.CleanTitle(Title);
            var installedMatch = GlobalSettings.Library.FirstOrDefault(m =>
                (!string.IsNullOrEmpty(m.Url) && m.Url.Equals(Url, StringComparison.OrdinalIgnoreCase)) ||
                ScannerEngine.CleanTitle(m.Title).Equals(cleanTargetTitle, StringComparison.OrdinalIgnoreCase)
            );
            if (installedMatch != null)
            {
                DownloadButtonText = "Reinstall";
                InstalledPath = installedMatch.LocalPath;
            }
            else
            {
                DownloadButtonText = "Download";
                InstalledPath = null;
            }
        }

        public bool HasNote => true;
        private string _noteContent = "";
        public string NoteContent { get => _noteContent; set { _noteContent = value; OnPropertyChanged(); } }

        private bool _hasCoopTag;
        public bool HasCoopTag { get => _hasCoopTag; set { _hasCoopTag = value; OnPropertyChanged(); } }
        private bool _hasMultiplayerTag;
        public bool HasMultiplayerTag { get => _hasMultiplayerTag; set { _hasMultiplayerTag = value; OnPropertyChanged(); } }
        public bool IsOnline => HasCoopTag || HasMultiplayerTag;

        public System.Collections.Generic.List<string>? GoFileDirectLinks { get; set; }

        private string _downloadsTagText = "";
        public string DownloadsTagText { get => _downloadsTagText; set { _downloadsTagText = value; OnPropertyChanged(); } }
        public bool HasDownloadsTag => !string.IsNullOrEmpty(DownloadsTagText);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class GameGroup
    {
        public string Title { get; set; } = "";
        public bool HasTitle => !string.IsNullOrEmpty(Title);
        public System.Collections.Generic.List<SearchResult> Games { get; set; } = new System.Collections.Generic.List<SearchResult>();
    }

    public class SearchResultPage
    {
        public System.Collections.Generic.List<GameGroup> Groups { get; set; } = new System.Collections.Generic.List<GameGroup>();
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
    }

    public class GameDetails
    {
        public string LatestVersion { get; set; } = "Unknown";
        public string Genre { get; set; } = "";
        public string Developer { get; set; } = "";
        public string Platform { get; set; } = "";
        public string GameSize { get; set; } = "";
        public string ReleasedBy { get; set; } = "";
        public bool PreInstalled { get; set; }
        public Dictionary<string, string> SystemRequirements { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> GameInfo { get; set; } = new Dictionary<string, string>();
        public string? HowToRunNote { get; set; }
    }

    public class DownloadHost
    {
        public string Name { get; set; } = "";
        public string Link { get; set; } = "";
    }

    public static class SteamRipScraper
    {
        private static readonly HttpClient client;
        private const string BaseUrl = "https://steamrip.com/";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        static SteamRipScraper()
        {
            client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
            client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
            client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        }

        public static async Task<GameDetails> GetGameDetailsAsync(string pageUrl)
        {
            var details = new GameDetails();
            try {
                var html = await client.GetStringAsync(pageUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var infoList = doc.DocumentNode.SelectNodes("//div[contains(@class, 'tie-list-shortcode')]//ul/li");
                if (infoList != null)
                {
                    foreach (var li in infoList)
                    {
                        var strong = li.SelectSingleNode(".//strong");
                        if (strong != null)
                        {
                            var key = strong.InnerText.Replace(":", "").Trim();
                            var val = System.Net.WebUtility.HtmlDecode(
                                li.InnerText.Replace(strong.InnerText, "").Replace(":", "").Trim());
                            details.GameInfo[key] = val;

                            switch (key.ToLower())
                            {
                                case "version": details.LatestVersion = val; break;
                                case "genre": details.Genre = val; break;
                                case "developer": details.Developer = val; break;
                                case "platform": details.Platform = val; break;
                                case "game size": details.GameSize = val; break;
                                case "released by": details.ReleasedBy = val; break;
                            }
                        }
                        else
                        {
                            var text = li.InnerText.Trim();
                            if (text.Contains("Pre-Installed", StringComparison.OrdinalIgnoreCase))
                                details.PreInstalled = true;
                            if (!string.IsNullOrEmpty(text))
                                details.GameInfo[text] = "✔";
                        }
                    }
                }

                if (details.LatestVersion == "Unknown")
                {
                    var tagMeta = doc.DocumentNode.SelectNodes("//span[contains(@class, 'tagmetafield')]");
                    if (tagMeta != null && tagMeta.Count > 0)
                        details.LatestVersion = tagMeta[0].InnerText.Trim();
                }

                var reqList = doc.DocumentNode.SelectNodes("//div[contains(@class, 'checklist')]//ul/li");
                if (reqList != null)
                {
                    foreach (var li in reqList)
                    {
                        var strong = li.SelectSingleNode(".//strong");
                        if (strong != null)
                        {
                            var key = System.Net.WebUtility.HtmlDecode(strong.InnerText.Replace(":", "").Trim());
                            var val = System.Net.WebUtility.HtmlDecode(li.InnerText.Replace(strong.InnerText, "").Replace(":", "").Trim());
                            details.SystemRequirements[key] = val;
                        }
                    }
                }

                var noteNode = doc.DocumentNode.SelectSingleNode("//blockquote[contains(@class, 'quote-light')] | //blockquote[contains(@class, 'aligncenter')] | //blockquote[contains(@class, 'wp-block-quote')] | //blockquote[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'how to run')] | //div[contains(@class, 'wp-block-quote')] | //div[contains(@class, 'checklist')]//following-sibling::blockquote");
                if (noteNode != null)
                {
                    var noteHtml = noteNode.InnerHtml;
                    noteHtml = Regex.Replace(noteHtml, @"<p[^>]*><strong>HOW TO RUN</strong></p>", "", RegexOptions.IgnoreCase);
                    details.HowToRunNote = System.Net.WebUtility.HtmlDecode(noteHtml).Trim();
                }

                var warningNodes = doc.DocumentNode.SelectNodes("//p[strong[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'launch the game') or contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'important') or contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'warning') or contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'note')]] | //div[contains(@class, 'warning')] | //blockquote[contains(@class, 'warning')] | //p[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'launch the game via')]");
                string warningsHtml = "";
                if (warningNodes != null)
                {
                    foreach (var wNode in warningNodes)
                    {
                        var wText = wNode.InnerText.Trim();
                        if (!string.IsNullOrEmpty(wText) && (details.HowToRunNote == null || !details.HowToRunNote.Contains(wText)))
                        {
                            warningsHtml += $"<p style=\"color: #ff4a4a; font-weight: bold;\">{wNode.InnerHtml}</p>";
                        }
                    }
                }
                if (!string.IsNullOrEmpty(warningsHtml))
                {
                    if (!string.IsNullOrEmpty(details.HowToRunNote))
                        details.HowToRunNote = warningsHtml + "<hr style=\"border-color: #333; margin: 12px 0;\"/>" + details.HowToRunNote;
                    else
                        details.HowToRunNote = warningsHtml;
                }
            } catch (Exception ex) {
                Logger.LogError("GetGameDetails", ex);
            }
            return details;
        }

        public static async Task<List<SearchResult>> SearchAsync(string query)
        {
            var page = await SearchPageAsync(query, 1);
            return page.Groups.SelectMany(g => g.Games).ToList();
        }

        public static async Task<SearchResultPage> SearchPageAsync(string query, int page = 1)
        {
            var searchUrl = $"{BaseUrl}page/{page}/?s={Uri.EscapeDataString(query)}";
            if (page == 1) searchUrl = $"{BaseUrl}?s={Uri.EscapeDataString(query)}";
            var resultPage = await GetGamesPageAsync(searchUrl);

            if (page == 1)
            {
                var localResults = JsonGameEntry.Search(query);
                if (localResults.Count > 0)
                {
                    var group = resultPage.Groups.FirstOrDefault();
                    if (group == null)
                    {
                        group = new GameGroup { Title = $"Search Results for '{query}'" };
                        resultPage.Groups.Add(group);
                    }
                    var existingUrls = new HashSet<string>(group.Games.Select(g => g.Url), StringComparer.OrdinalIgnoreCase);
                    foreach (var jg in localResults)
                    {
                        if (!existingUrls.Contains(jg.link))
                        {
                            var sr = new SearchResult {
                                Title = jg.name,
                                Url = jg.link,
                                ImageUrl = jg.cover_image,
                                DateString = jg.version,
                                DownloadsTagText = GameDatabaseService.GetDownloadsTagText(jg.link, jg.name)
                            };
                            sr.CheckInstalledState();
                            group.Games.Add(sr);
                            existingUrls.Add(jg.link);
                        }
                    }
                }
            }
            return resultPage;
        }

        private static Dictionary<string, int> _categoryTotalPagesCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static async Task<SearchResultPage> GetGamesPageAsync(string targetUrl)
        {
            var cached = await DiskCacheService.GetCachedPageAsync(targetUrl);
            if (cached != null) return cached;

            var resultPage = new SearchResultPage();
            try {
                Logger.Log($"[Scraper] Fetching games from: {targetUrl}");

                var response = await client.GetAsync(targetUrl);
                Logger.Log($"[Scraper] HTTP Response: {response.StatusCode}");

                if (!response.IsSuccessStatusCode) {
                    Logger.LogError("GetGamesListAsync", new Exception($"HTTP Error: {response.StatusCode}"));
                    return resultPage;
                }

                var html = await response.Content.ReadAsStringAsync();
                Logger.Log($"[Scraper] HTML Length: {html.Length} characters.");
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var seenPageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var allMainNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'main-content') and contains(@class,'tie-col-md')]");
                var mainNode = allMainNodes?.LastOrDefault() ?? doc.DocumentNode;

                Logger.Log($"[Scraper] mainNode class=\"{mainNode.GetAttributeValue("class", "")}\" children={mainNode.ChildNodes.Count}");

                var topLevelBlocks = mainNode.SelectNodes(".//div[contains(@class,'big-posts-box')]");

                Logger.Log($"[Scraper] big-posts-box blocks: {topLevelBlocks?.Count ?? 0}");

                if (topLevelBlocks != null && topLevelBlocks.Count > 0)
                {
                    foreach (var box in topLevelBlocks)
                    {
                        var groupTitleNode = box.SelectSingleNode(".//div[contains(@class,'mag-box-title')]//h3");
                        string groupTitle = groupTitleNode?.InnerText.Trim() ?? "";
                        Logger.Log($"[Scraper] Group \"{groupTitle}\"");

                        var group = new GameGroup { Title = groupTitle };
                        var postsList = box.SelectNodes(".//li[contains(@class,'post-item')]");
                        Logger.Log($"[Scraper] → {postsList?.Count ?? 0} items");
                        if (postsList != null) ParsePostsIntoGroup(postsList, group.Games, seenPageUrls);

                        if (group.Games.Count > 0) resultPage.Groups.Add(group);
                    }
                }
                else
                {

                    var group = new GameGroup { Title = "" };

                    var postElements = mainNode.SelectNodes(".//div[contains(@class,'post-element')]");
                    Logger.Log($"[Scraper] Search mode post-elements: {postElements?.Count ?? 0}");
                    if (postElements != null && postElements.Count > 0)
                        ParsePostsIntoGroup(postElements, group.Games, seenPageUrls);

                    if (group.Games.Count == 0)
                    {
                        var postItems = mainNode.SelectNodes(".//li[contains(@class,'post-item')]");
                        Logger.Log($"[Scraper] Fallback post-items: {postItems?.Count ?? 0}");
                        if (postItems != null && postItems.Count > 0)
                            ParsePostsIntoGroup(postItems, group.Games, seenPageUrls);
                    }

                    if (group.Games.Count == 0)
                    {
                        var h2Links = mainNode.SelectNodes(".//h2[contains(@class,'post-title')]/a[@href] | .//h2/a[@href]");
                        if (h2Links != null) ParsePostsIntoGroup(h2Links, group.Games, seenPageUrls);
                    }

                    if (group.Games.Count > 0) resultPage.Groups.Add(group);
                }

                var currentPageNode =
                    doc.DocumentNode.SelectSingleNode("//span[contains(@class,'page-numbers') and contains(@class,'current')]") ??
                    doc.DocumentNode.SelectSingleNode("//*[@aria-current='page']") ??
                    doc.DocumentNode.SelectSingleNode("//li[contains(@class,'current')]/span");

                if (currentPageNode != null && int.TryParse(currentPageNode.InnerText.Trim(), out int curPage))
                {
                    resultPage.CurrentPage = curPage;
                }
                else
                {

                    var pageMatch = Regex.Match(targetUrl, @"/page/(\d+)/");
                    if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, out int urlPage))
                        resultPage.CurrentPage = urlPage;
                }

                var allPageNodes = doc.DocumentNode.SelectNodes(
                    "//a[contains(@class, 'pages-nav-item') or contains(@class, 'page-numbers') or contains(@class, 'page-link') or contains(@class, 'pagelink')] | " +
                    "//div[contains(@class, 'pagination')]//a | //div[contains(@class, 'nav-links')]//a");
                if (allPageNodes != null)
                {
                    int maxPage = resultPage.CurrentPage;
                    foreach (var pNode in allPageNodes)
                    {
                        if (int.TryParse(pNode.InnerText.Trim(), out int pNum) && pNum > maxPage)
                        {
                            maxPage = pNum;
                        }

                        var titleAttr = pNode.GetAttributeValue("title", "");
                        if (titleAttr == "Last" || pNode.InnerText.Trim() == "Last")
                        {
                            var href = pNode.GetAttributeValue("href", "");
                            var match = Regex.Match(href, @"page/(\d+)");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int lpNum) && lpNum > maxPage)
                                maxPage = lpNum;
                        }
                    }
                    resultPage.TotalPages = maxPage;
                }

                if (resultPage.TotalPages < resultPage.CurrentPage) resultPage.TotalPages = resultPage.CurrentPage;

                string baseCatUrl = Regex.Replace(targetUrl, @"/page/\d+/?", "").TrimEnd('/') + "/";
                if (_categoryTotalPagesCache.TryGetValue(baseCatUrl, out int cachedTotal) && cachedTotal > resultPage.TotalPages)
                {
                    resultPage.TotalPages = cachedTotal;
                }
                else
                {
                    _categoryTotalPagesCache[baseCatUrl] = resultPage.TotalPages;
                }

                await DiskCacheService.SaveCachedPageAsync(targetUrl, resultPage);

            } catch (Exception ex) {
                Logger.LogError("GetGamesListAsync", ex);
            }
            Logger.Log($"[Scraper] Completed. Returning {resultPage.Groups.Count} groups, Page {resultPage.CurrentPage}/{resultPage.TotalPages}.");
            return resultPage;
        }

        private static void ParsePostsIntoGroup(IEnumerable<HtmlNode> posts, List<SearchResult> results, HashSet<string> seenUrls)
        {
            foreach (var post in posts)
            {
                var titleNode = post.Name == "a" ? post : post.SelectSingleNode(".//h2//a | .//h3//a | .//h4//a | .//div[contains(@class, 'post-title')]//a");
                if (titleNode == null) titleNode = post.SelectSingleNode(".//a[contains(@class, 'all-over-thumb-link')] | .//a[1]");
                var imgNode = post.SelectSingleNode(".//img");
                var slideNode = post.SelectSingleNode(".//div[contains(@class, 'slide')]");
                var dateNode = post.SelectSingleNode(".//time | .//span[contains(@class, 'date')]");

                if (titleNode != null)
                {
                    var rawTitle = System.Net.WebUtility.HtmlDecode(titleNode.InnerText.Trim());
                    if (string.IsNullOrEmpty(rawTitle))
                    {
                        var h2Title = post.SelectSingleNode(".//h2[contains(@class, 'the-post-title')] | .//h2[contains(@class, 'thumb-title')] | .//h3[contains(@class, 'post-title')]");
                        if (h2Title != null) rawTitle = System.Net.WebUtility.HtmlDecode(h2Title.InnerText.Trim());
                    }
                    rawTitle = rawTitle.Replace("’", "'").Replace("`", "'").Replace("‘", "'");

                    var urlString = titleNode.GetAttributeValue("href", "");
                    if (string.IsNullOrEmpty(urlString) || urlString.Contains("/category/") || urlString.EndsWith(".com/") || urlString.EndsWith(".com") || string.IsNullOrEmpty(rawTitle)) continue;

                    if (!urlString.StartsWith("http")) urlString = BaseUrl.TrimEnd('/') + "/" + urlString.TrimStart('/');

                    if (!seenUrls.Add(urlString)) continue;

                    var imgUrl = ExtractImageUrl(post);

                    if (string.IsNullOrEmpty(imgUrl))
                    {
                        imgUrl = "https://steamrip.com/wp-content/uploads/2021/06/Site-logo3.png";
                    }
                    else if (!imgUrl.StartsWith("http") && !imgUrl.StartsWith("data:"))
                    {
                        imgUrl = BaseUrl.TrimEnd('/') + "/" + imgUrl.TrimStart('/');
                    }

                    string cleanTitle = rawTitle;
                    string tagValue = dateNode?.InnerText.Trim() ?? "Recently";
                    bool hasCoop = false;
                    bool hasMp = false;

                    var buildMatch = Regex.Match(rawTitle, @"\(([^)]+)\)");
                    if (buildMatch.Success)
                    {
                        string bracketContent = buildMatch.Groups[1].Value;
                        tagValue = bracketContent;
                        if (bracketContent.Contains("Co-op", StringComparison.OrdinalIgnoreCase)) hasCoop = true;
                        if (bracketContent.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase) || bracketContent.Contains("MP", StringComparison.OrdinalIgnoreCase) || bracketContent.Contains("Online", StringComparison.OrdinalIgnoreCase)) hasMp = true;
                        cleanTitle = cleanTitle.Replace(buildMatch.Groups[0].Value, "").Trim();
                    }

                    int fdIndex = cleanTitle.IndexOf("Free Download", StringComparison.OrdinalIgnoreCase);
                    if (fdIndex == -1) fdIndex = cleanTitle.IndexOf("freedownload", StringComparison.OrdinalIgnoreCase);
                    if (fdIndex > 0) {
                        cleanTitle = cleanTitle.Substring(0, fdIndex).Trim();
                    }

                    results.Add(new SearchResult
                    {
                        Title = cleanTitle,
                        Url = urlString,
                        ImageUrl = imgUrl,
                        DateString = tagValue,
                        HasCoopTag = hasCoop,
                        HasMultiplayerTag = hasMp,
                        DownloadsTagText = GameDatabaseService.GetDownloadsTagText(urlString, cleanTitle)
                    });
                }
            }
        }

        public static string ExtractImageUrl(HtmlNode postNode)
        {
            var imgNode = postNode.SelectSingleNode(".//img");
            var slideNode = postNode.SelectSingleNode(".//div[contains(@class, 'slide')]");

            string url = slideNode?.GetAttributeValue("data-back", "") ?? "";
            if (string.IsNullOrEmpty(url) && imgNode != null)
            {

                url = imgNode.GetAttributeValue("data-src", "");
                if (string.IsNullOrEmpty(url)) url = imgNode.GetAttributeValue("data-lazy-src", "");

                if (string.IsNullOrEmpty(url))
                {
                    string srcVal = imgNode.GetAttributeValue("src", "");
                    if (!srcVal.StartsWith("data:")) url = srcVal;
                }

                if (string.IsNullOrEmpty(url))
                {
                    var noscript = postNode.SelectSingleNode(".//noscript");
                    if (noscript != null)
                    {
                        var noscriptDoc = new HtmlDocument();
                        noscriptDoc.LoadHtml(noscript.InnerHtml);
                        var noscriptImg = noscriptDoc.DocumentNode.SelectSingleNode("//img");
                        if (noscriptImg != null)
                        {
                            var fallback = noscriptImg.GetAttributeValue("src", "");
                            if (!string.IsNullOrEmpty(fallback) && !fallback.StartsWith("data:"))
                                url = fallback;
                        }
                    }
                }
            }
            return url;
        }

        public static async Task<string?> GetFeaturedImageAsync(string pageUrl)
        {
            try {
                var html = await client.GetStringAsync(pageUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var featured = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'post-thumb')] | //div[contains(@class, 'featured-image')] | //img[contains(@class, 'wp-post-image')]");
                if (featured != null)
                {
                    string url = featured.Name == "img" ? featured.GetAttributeValue("src", "") : ExtractImageUrl(featured);
                    if (!string.IsNullOrEmpty(url))
                    {
                        if (url.StartsWith("//")) url = "https:" + url;
                        if (!url.StartsWith("http")) url = BaseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
                        return url;
                    }
                }
            } catch { }
            return null;
        }

        public static async Task<(bool found, string url)> CheckBuzzheavierAsync(string pageUrl)
        {
            try {
                Logger.Log($"[Scraper] Checking for Buzzheavier on: {pageUrl}");
                var html = await client.GetStringAsync(pageUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var buzzLink = doc.DocumentNode.SelectNodes("//a[contains(@href, 'bzzhr.to') or contains(@href, 'buzzheavier') or contains(text(), 'Buzzheavier')]")?.FirstOrDefault();

                if (buzzLink == null)
                {
                    var allAnchors = doc.DocumentNode.SelectNodes("//a[@href]");
                    if (allAnchors != null)
                    {
                        foreach (var a in allAnchors)
                        {
                            var href = a.GetAttributeValue("href", "");
                            if (href.Contains("bzzhr.to") || href.Contains("buzzheavier"))
                            {
                                buzzLink = a;
                                break;
                            }
                        }
                    }
                }

                if (buzzLink != null)
                {
                    var href = buzzLink.GetAttributeValue("href", "");
                    if (href.StartsWith("//")) href = "https:" + href;
                    Logger.Log($"[Scraper] Buzzheavier link found: {href}");
                    return (true, href);
                }
            } catch (Exception ex) {
                Logger.LogError("CheckBuzzheavier", ex);
            }
            return (false, "");
        }

        public static async Task<(bool found, string pageUrl, List<string> directLinks)> CheckGoFileAsync(string steamRipPageUrl)
        {
            return await GoFileClient.CheckAndResolveAsync(steamRipPageUrl);
        }

        public static async Task<string> ExtractBuzzheavierDirectUrlAsync(string bzzhrUrl)
        {
            try {
                Logger.Log($"[Scraper] Extracting direct URL from: {bzzhrUrl}");

                var htmlResponse = await client.GetAsync(bzzhrUrl);
                if (!htmlResponse.IsSuccessStatusCode) return "";

                var landingHtml = await htmlResponse.Content.ReadAsStringAsync();
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(landingHtml);

                var dlBtn = doc.DocumentNode.SelectSingleNode("//a[contains(@hx-get, 'download')]")
                         ?? doc.DocumentNode.SelectSingleNode("//button[contains(@hx-get, 'download')]");

                if (dlBtn == null)
                {
                    Logger.Log("[Scraper] No HTMX download button found. Trying legacy fallback...");
                    dlBtn = doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'link-button')]");
                }

                if (dlBtn != null)
                {
                    var href = dlBtn.GetAttributeValue("hx-get", "");
                    if (string.IsNullOrEmpty(href)) href = dlBtn.GetAttributeValue("href", "");

                    if (!string.IsNullOrEmpty(href)) {
                        string baseUrl = bzzhrUrl.Contains("bzzhr.to") ? "https://bzzhr.to" : "https://buzzheavier.com";
                        if (href.StartsWith("//")) href = "https:" + href;
                        if (href.StartsWith("/")) href = baseUrl + href;

                        Logger.Log($"[Scraper] Resolving HTMX download: {href}");

                        using (var request = new HttpRequestMessage(HttpMethod.Get, href))
                        {
                            request.Headers.Add("Referer", bzzhrUrl);
                            request.Headers.Add("HX-Request", "true");
                            request.Headers.Add("Accept", "*/*");

                            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                            if (response.Headers.TryGetValues("hx-redirect", out var values))
                            {
                                var directUrl = values.FirstOrDefault();
                                if (!string.IsNullOrEmpty(directUrl))
                                {
                                    Logger.Log($"[Scraper] Successfully extracted direct URL (HX-Redirect): {directUrl}");
                                    return directUrl;
                                }
                            }

                            if (response.Headers.Location != null) {
                                Logger.Log($"[Scraper] Successfully extracted direct URL (Location): {response.Headers.Location}");
                                return response.Headers.Location.ToString();
                            }

                            if (response.StatusCode == System.Net.HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType?.Contains("text/html") == false)
                            {
                                return href;
                            }
                        }
                    }
                }
                Logger.Log("[Scraper] Buzzheavier error: Could not find direct URL.");
            } catch (Exception ex) {
                Logger.LogError("ExtractBuzzheavier", ex);
            }
            return "";
        }

        public static async Task<string> SearchUrlByFolderNameAsync(string folderName)
        {
            try {

                string cleanQuery = folderName
                    .Replace("-SteamRIP.com", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(".SteamRIP.com", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Free Download", "", StringComparison.OrdinalIgnoreCase);

                cleanQuery = Regex.Replace(cleanQuery, @"\(Build\s+\d+\)", "", RegexOptions.IgnoreCase);
                cleanQuery = Regex.Replace(cleanQuery, @"Build\s+\d+", "", RegexOptions.IgnoreCase);
                cleanQuery = Regex.Replace(cleanQuery, @"v\d+[\d\.]+", "", RegexOptions.IgnoreCase);

                cleanQuery = cleanQuery.Replace("-", " ").Replace("_", " ").Replace(".", " ");
                cleanQuery = Regex.Replace(cleanQuery, @"\s+", " ").Trim();

                Logger.Log($"[Scraper] Recovering URL for: {cleanQuery}");
                var results = await SearchAsync(cleanQuery);
                if (results.Any())
                {
                    var bestMatch = results.FirstOrDefault(r =>
                        r.Title.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)) ?? results.First();
                    return bestMatch.Url;
                }
            } catch (Exception ex) {
                Logger.LogError("SearchUrlByFolder", ex);
            }
            return "";
        }

        public static async Task<string> GetLatestVersionAsync(string pageUrl)
        {
            try {
                var html = await client.GetStringAsync(pageUrl);
                var match = Regex.Match(html, @"Version:\s*([^\n\r<]+)", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
            } catch { }
            return "Unknown";
        }
        public static async Task<List<DownloadHost>> GetDownloadHostsAsync(string pageUrl)
        {
            var hosts = new List<DownloadHost>();
            try {
                var html = await client.GetStringAsync(pageUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
                if (anchors != null)
                {
                    foreach (var a in anchors)
                    {
                        var href = a.GetAttributeValue("href", "");
                        if (href.Contains("bzzhr.to") || href.Contains("buzzheavier"))
                        {
                            if (href.StartsWith("//")) href = "https:" + href;
                            hosts.Add(new DownloadHost { Name = "Buzzheavier", Link = href });
                        }
                        else if (href.Contains("gofile.io"))
                        {
                            if (href.StartsWith("//")) href = "https:" + href;
                            hosts.Add(new DownloadHost { Name = "GoFile", Link = href });
                        }
                        else if (href.Contains("vikingfile") || href.Contains("vik1ngfile"))
                        {
                            if (href.StartsWith("//")) href = "https:" + href;
                            hosts.Add(new DownloadHost { Name = "Viking File", Link = href });
                        }
                    }
                }
            } catch { }
            return hosts;
        }

        public static async Task<List<DownloadHost>> GetDirectLinksAsync(string pageUrl) => await GetDownloadHostsAsync(pageUrl);

        private static List<JsonGameEntry> _allJsonGames = new List<JsonGameEntry>();
        private static bool _jsonLoaded = false;

        public static async Task LoadJsonGamesAsync()
        {
            if (_jsonLoaded) return;
            try {
                var path = Path.Combine(AppContext.BaseDirectory, "Redist", "Deps", "steamrip_full_games-may-2026.json");
                if (!File.Exists(path))
                {
                    path = Path.Combine(Directory.GetCurrentDirectory(), "Redist", "Deps", "steamrip_full_games-may-2026.json");
                }
                if (File.Exists(path))
                {
                    var json = await File.ReadAllTextAsync(path);
                    _allJsonGames = JsonSerializer.Deserialize<List<JsonGameEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<JsonGameEntry>();
                    _jsonLoaded = true;
                }
            } catch (Exception ex) {
                Logger.LogError("LoadJsonGames", ex);
            }
        }

        public static Regex BuildSearchRegex(string query)
        {
            var terms = query.Split(new[] { ' ', '-', '_', '.', ',', ':', ';', '\'', '’', '`', '‘' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return new Regex(Regex.Escape(query), RegexOptions.IgnoreCase);

            var patternBuilder = new System.Text.StringBuilder("^");
            foreach (var term in terms)
            {
                var termPattern = string.Join(@"[\W_]*", term.Select(c => Regex.Escape(c.ToString())));
                patternBuilder.Append($"(?=.*?{termPattern})");
            }
            return new Regex(patternBuilder.ToString(), RegexOptions.IgnoreCase);
        }

        public static HashSet<string> GetShortForms(string title)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(title)) return results;

            string cleanTitle = System.Net.WebUtility.HtmlDecode(title);
            var buildMatch = Regex.Match(cleanTitle, @"\(([^)]+)\)");
            if (buildMatch.Success)
            {
                cleanTitle = cleanTitle.Replace(buildMatch.Groups[0].Value, "").Trim();
            }
            int fdIndex = cleanTitle.IndexOf("Free Download", StringComparison.OrdinalIgnoreCase);
            if (fdIndex == -1) fdIndex = cleanTitle.IndexOf("freedownload", StringComparison.OrdinalIgnoreCase);
            if (fdIndex > 0) {
                cleanTitle = cleanTitle.Substring(0, fdIndex).Trim();
            }

            cleanTitle = cleanTitle.Replace("'", "").Replace("’", "").Replace("`", "").Replace("‘", "");

            string ReplaceRoman(string text)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "I", "1" }, { "II", "2" }, { "III", "3" }, { "IV", "4" }, { "V", "5" },
                    { "VI", "6" }, { "VII", "7" }, { "VIII", "8" }, { "IX", "9" }, { "X", "10" },
                    { "XI", "11" }, { "XII", "12" }
                };
                var words = text.Split(new[] { ' ', '-', '_', '.', ',', ';', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < words.Length; i++)
                {
                    if (map.TryGetValue(words[i], out var num))
                    {
                        words[i] = num;
                    }
                }
                return string.Join(" ", words);
            }

            var variations = new List<string> { cleanTitle, ReplaceRoman(cleanTitle) };

            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "of", "the", "and", "a", "an", "in", "on", "at", "to", "for", "with", "by"
            };

            void AddAcronyms(string phrase)
            {
                var words = phrase.Split(new[] { ' ', '-', '_', '.', ',', ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length <= 1) return;

                var allFirsts = new System.Text.StringBuilder();
                foreach (var w in words) if (w.Length > 0) allFirsts.Append(w[0]);
                if (allFirsts.Length > 1) results.Add(allFirsts.ToString());

                var noStops = new System.Text.StringBuilder();
                foreach (var w in words)
                {
                    if (w.Length > 0 && !stopWords.Contains(w)) noStops.Append(w[0]);
                }
                if (noStops.Length > 1) results.Add(noStops.ToString());
            }

            foreach (var varTitle in variations)
            {
                AddAcronyms(varTitle);

                var parts = varTitle.Split(new[] { ':', '–' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    foreach (var part in parts)
                    {
                        AddAcronyms(part);
                    }
                }
            }

            return results;
        }

        public static List<JsonGameEntry> SearchJsonGames(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<JsonGameEntry>();
            try {
                var regex = BuildSearchRegex(query);
                var cleanQuery = Regex.Replace(query.Replace("'", "").Replace("’", "").Replace("`", ""), @"[\W_]+", " ").Trim();
                var solidQuery = query.Replace(" ", "").Replace("-", "").Replace(".", "").Replace("_", "").Trim();

                var matches = _allJsonGames
                    .Where(g => {
                        if (g.name == null) return false;
                        if (regex.IsMatch(g.name)) return true;
                        if (solidQuery.Length >= 2)
                        {
                            var shortForms = GetShortForms(g.name);
                            if (shortForms.Any(sf => sf.StartsWith(solidQuery, StringComparison.OrdinalIgnoreCase))) return true;
                        }
                        return false;
                    })
                    .OrderBy(g => {
                        var cleanRawName = Regex.Replace(g.name.Replace("'", "").Replace("’", "").Replace("`", ""), @"[\W_]+", " ").Trim();
                        if (cleanRawName.StartsWith(cleanQuery, StringComparison.OrdinalIgnoreCase)) return 0;
                        if (solidQuery.Length >= 2)
                        {
                            var shortForms = GetShortForms(g.name);
                            if (shortForms.Any(sf => sf.StartsWith(solidQuery, StringComparison.OrdinalIgnoreCase))) return 0;
                        }
                        return 1;
                    })
                    .ThenBy(g => g.name.Length)
                    .Take(10)
                    .ToList();
                return matches.Take(4).ToList();
            } catch {
                return new List<JsonGameEntry>();
            }
        }
    }

    public class JsonGameEntry
    {
        private string _name = "";
        public string name { get => _name; set => _name = System.Net.WebUtility.HtmlDecode(value ?? ""); }
        public string link { get; set; } = "";
        public string version { get; set; } = "";
        public string cover_image { get; set; } = "";
        public List<string> requirements { get; set; } = new List<string>();
        public string game_info { get; set; } = "";
        public string upload_date_text { get; set; } = "";
        public string downloads_count { get; set; } = "";
        public string how_to_run { get; set; } = "";
        public List<string> warnings { get; set; } = new List<string>();
        public string game_size { get; set; } = "";
        public List<string> genre { get; set; } = new List<string>();
        public string developer { get; set; } = "";
        public string publisher { get; set; } = "";
        public string platform { get; set; } = "";

        [JsonIgnore]
        public string genre_string => genre != null ? string.Join(", ", genre) : "";
        [JsonIgnore]
        public string req_string => requirements != null ? string.Join("\n", requirements) : "";
        [JsonIgnore]
        public string warn_string => warnings != null ? string.Join("\n", warnings) : "";

        public static List<JsonGameEntry> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || GameDatabaseService.Games == null)
                return new List<JsonGameEntry>();

            query = query.Trim();
            var regex = SteamRipScraper.BuildSearchRegex(query);
            var cleanQuery = Regex.Replace(query.Replace("'", "").Replace("’", "").Replace("`", ""), @"[\W_]+", " ").Trim();
            var solidQuery = query.Replace(" ", "").Replace("-", "").Replace(".", "").Replace("_", "").Trim();

            var prefix   = new List<JsonGameEntry>();
            var contains = new List<JsonGameEntry>();

            foreach (var node in GameDatabaseService.Games)
            {
                if (prefix.Count + contains.Count >= 50) break;

                string? rawName = node?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(rawName)) continue;
                rawName = System.Net.WebUtility.HtmlDecode(rawName);

                bool isRegexMatch = regex.IsMatch(rawName);
                bool isShortFormMatch = false;

                if (!isRegexMatch && solidQuery.Length >= 2)
                {
                    var shortForms = SteamRipScraper.GetShortForms(rawName);
                    if (shortForms.Any(sf => sf.StartsWith(solidQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        isShortFormMatch = true;
                    }
                }

                if (!isRegexMatch && !isShortFormMatch) continue;

                bool isPrefix = false;
                if (isShortFormMatch)
                {
                    isPrefix = true;
                }
                else if (!string.IsNullOrEmpty(cleanQuery))
                {
                    var cleanRawName = Regex.Replace(rawName.Replace("'", "").Replace("’", "").Replace("`", ""), @"[\W_]+", " ").Trim();
                    isPrefix = cleanRawName.StartsWith(cleanQuery, StringComparison.OrdinalIgnoreCase);
                }

                try
                {
                    var entry = System.Text.Json.JsonSerializer.Deserialize<JsonGameEntry>(
                        node!.ToJsonString(),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry != null)
                    {
                        if (isPrefix) prefix.Add(entry);
                        else          contains.Add(entry);
                    }
                }
                catch {  }
            }

            return prefix.Concat(contains).Take(12).ToList();
        }
    }
}