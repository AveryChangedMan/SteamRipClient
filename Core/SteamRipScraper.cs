using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace SteamRipApp.Core
{
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
        public System.Collections.Generic.List<string>? GoFileDirectLinks { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
    }
    public static class SteamRipScraper
    {
        private static readonly HttpClient client;
        private const string BaseUrl = "https://steamrip.com/";
        static SteamRipScraper()
        {
            client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
        }
        public static async Task<GameDetails> GetGameDetailsAsync(string pageUrl)
        {
            var details = new GameDetails();
            try {
                var html = await client.GetStringAsync(pageUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var infoList = doc.DocumentNode.SelectNodes("
                if (infoList != null)
                {
                    foreach (var li in infoList)
                    {
                        var strong = li.SelectSingleNode(".
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
                    var tagMeta = doc.DocumentNode.SelectNodes("
                    if (tagMeta != null && tagMeta.Count > 0)
                        details.LatestVersion = tagMeta[0].InnerText.Trim();
                }
                var reqList = doc.DocumentNode.SelectNodes("
                if (reqList != null)
                {
                    foreach (var li in reqList)
                    {
                        var strong = li.SelectSingleNode(".
                        if (strong != null)
                        {
                            var key = strong.InnerText.Replace(":", "").Trim();
                            var val = li.InnerText.Replace(strong.InnerText, "").Replace(":", "").Trim();
                            details.SystemRequirements[key] = val;
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.LogError("GetGameDetails", ex);
            }
            return details;
        }
        public static async Task<List<SearchResult>> SearchAsync(string query)
        {
            var results = new List<SearchResult>();
            try {
                Logger.Log($"[Search] Initializing search for query: {query}");
                var searchUrl = $"{BaseUrl}?s={Uri.EscapeDataString(query)}";
                var response = await client.GetAsync(searchUrl);
                Logger.Log($"[Search] HTTP Response: {response.StatusCode}");
                if (!response.IsSuccessStatusCode) {
                    Logger.LogError("SearchAsync", new Exception($"HTTP Error: {response.StatusCode}"));
                    return results;
                }
                var html = await response.Content.ReadAsStringAsync();
                Logger.Log($"[Search] HTML Length: {html.Length} characters.");
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var postNodes = doc.DocumentNode.SelectNodes("
                IEnumerable<HtmlNode> posts = postNodes?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>();
                if (postNodes == null) {
                    posts = doc.DocumentNode.SelectNodes("
                }
                if (posts != null)
                {
                    Logger.Log($"[Search] Found {posts.Count()} potential nodes.");
                    var seenUrls = new HashSet<string>();
                    foreach (var post in posts)
                    {
                        var titleNode = post.Name == "a" ? post : post.SelectSingleNode(".
                        var imgNode = post.SelectSingleNode(".
                        var slideNode = post.SelectSingleNode(".
                        var dateNode = post.SelectSingleNode(".
                        if (titleNode != null)
                        {
                            var title = titleNode.InnerText.Trim();
                            if (string.IsNullOrEmpty(title)) 
                            {
                                var h2Title = post.SelectSingleNode(".
                                if (h2Title != null) title = h2Title.InnerText.Trim();
                            }
                            var urlString = titleNode.GetAttributeValue("href", "");
                            if (string.IsNullOrEmpty(urlString) || urlString.Contains("/category/") || urlString.EndsWith(".com/") || urlString.EndsWith(".com") || string.IsNullOrEmpty(title)) continue;
                            if (!urlString.StartsWith("http")) urlString = BaseUrl.TrimEnd('/') + "/" + urlString.TrimStart('/');
                            if (!seenUrls.Add(urlString)) continue; 
                            var imgUrl = slideNode?.GetAttributeValue("data-back", "") ?? imgNode?.GetAttributeValue("src", "") ?? "";
                            if (string.IsNullOrEmpty(imgUrl) && imgNode != null) {
                                imgUrl = imgNode.GetAttributeValue("data-lazy-src", "") != "" ? imgNode.GetAttributeValue("data-lazy-src", "") : imgNode.GetAttributeValue("data-src", "");
                            }
                            if (string.IsNullOrEmpty(imgUrl))
                            {
                                imgUrl = "https://steamrip.com/wp-content/uploads/2021/06/Site-logo3.png";
                            }
                            else if (!imgUrl.StartsWith("http"))
                            {
                                imgUrl = BaseUrl.TrimEnd('/') + "/" + imgUrl.TrimStart('/');
                            }
                            results.Add(new SearchResult
                            {
                                Title = title,
                                Url = urlString,
                                ImageUrl = imgUrl,
                                DateString = dateNode?.InnerText.Trim() ?? "Recently"
                            });
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.LogError("SearchAsync", ex);
            }
            Logger.Log($"[Search] Completed. Returning {results.Count} results.");
            return results;
        }
        public static async Task<(bool found, string url)> CheckBuzzheavierAsync(string pageUrl)
        {
            try {
                Logger.Log($"[Scraper] Checking for Buzzheavier on: {pageUrl}");
                var html = await client.GetStringAsync(pageUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var buzzLink = doc.DocumentNode.SelectNodes("
                if (buzzLink == null)
                {
                    var allAnchors = doc.DocumentNode.SelectNodes("
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
                    if (href.StartsWith("
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
                var downloadApi = bzzhrUrl.TrimEnd('/') + "/download";
                using (var request = new HttpRequestMessage(HttpMethod.Get, downloadApi))
                {
                    request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    Logger.Log($"[Scraper] Buzzheavier API Status: {response.StatusCode}");
                    foreach(var h in response.Headers) {
                        Logger.Log($"[Scraper] Header: {h.Key} = {string.Join(", ", h.Value)}");
                    }
                    if (response.Headers.TryGetValues("hx-redirect", out var values))
                    {
                        var directUrl = values.FirstOrDefault();
                        if (!string.IsNullOrEmpty(directUrl))
                        {
                            Logger.Log($"[Scraper] Successfully extracted direct URL (HX): {directUrl}");
                            return directUrl;
                        }
                    }
                    if (response.Headers.Location != null) {
                        Logger.Log($"[Scraper] Successfully extracted direct URL (Location): {response.Headers.Location}");
                        return response.Headers.Location.ToString();
                    }
                    var html = await response.Content.ReadAsStringAsync();
                    Logger.Log($"[Scraper] Buzzheavier HTML Snippet: {html.Substring(0, Math.Min(html.Length, 500))}");
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    var dlBtn = doc.DocumentNode.SelectNodes("
                    if (dlBtn != null)
                    {
                        var href = dlBtn.GetAttributeValue("href", "");
                        if (!string.IsNullOrEmpty(href)) {
                            if (href.StartsWith("
                            Logger.Log($"[Scraper] Found fallback direct URL via HTML: {href}");
                            return href;
                        }
                    }
                    Logger.Log("[Scraper] Potential Buzzheavier error: No direct URL found in headers or HTML.");
                }
            } catch (Exception ex) {
                Logger.LogError("ExtractBuzzheavier", ex);
            }
            return "";
        }
        public static async Task<string> SearchUrlByFolderNameAsync(string folderName)
        {
            try {
                string cleanQuery = folderName.Replace("-SteamRIP.com", "", StringComparison.OrdinalIgnoreCase)
                                              .Replace(".SteamRIP.com", "", StringComparison.OrdinalIgnoreCase)
                                              .Replace("-", " ")
                                              .Replace(".", " ")
                                              .Trim();
                Logger.Log($"[Scraper] Recovering URL for: {cleanQuery}");
                var results = await SearchAsync(cleanQuery);
                if (results.Any()) return results.First().Url;
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
    }
}

