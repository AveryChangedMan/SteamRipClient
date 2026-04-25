using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace SteamRipApp.Core
{
    
    
    
    
    public static class GoFileClient
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private const string TokenSalt = "5d4f7g8sd45fsd";

        private static readonly HttpClient _http;
        private static string? _accountToken = null;
        public static string? AccountToken => _accountToken;

        static GoFileClient()
        {
            var handler = new HttpClientHandler {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.Add("Origin", "https://gofile.io");
            _http.DefaultRequestHeaders.Add("Referer", "https://gofile.io/");
            _http.DefaultRequestHeaders.Add("Accept", "*/*");
            _http.Timeout = TimeSpan.FromSeconds(20);
        }

        
        
        
        
        private static string GenerateWebsiteToken(string accountToken = "")
        {
            long timeSlot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 14400;
            string raw = $"{UserAgent}::en-US::{accountToken}::{timeSlot}::{TokenSalt}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        
        
        
        
        private static async Task<string?> CreateGuestAccountAsync()
        {
            try {
                string wt = GenerateWebsiteToken();
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.gofile.io/accounts");
                req.Headers.Add("X-Website-Token", wt);
                req.Headers.Add("X-BL", "en-US");
                req.Content = new StringContent("", Encoding.UTF8, "application/json");

                var resp = await _http.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();
                Logger.Log($"[GoFile] CreateAccount response: {json.Substring(0, Math.Min(json.Length, 300))}");

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetProperty("status").GetString() == "ok")
                {
                    var token = doc.RootElement.GetProperty("data").GetProperty("token").GetString();
                    Logger.Log($"[GoFile] Guest token obtained: {token?[..Math.Min(token.Length, 8)]}...");
                    return token;
                }
            } catch (Exception ex) {
                Logger.LogError("GoFile.CreateGuestAccount", ex);
            }
            return null;
        }

        
        
        
        
        private static async Task<JsonElement?> GetContentsAsync(string contentId, string accountToken)
        {
            try {
                string wt = GenerateWebsiteToken(accountToken);
                string url = $"https://api.gofile.io/contents/{contentId}?cache=true&sortField=createTime&sortDirection=1";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Website-Token", wt);
                req.Headers.Add("X-BL", "en-US");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
                req.Headers.Add("Cookie", $"accountToken={accountToken}");

                var resp = await _http.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();
                Logger.Log($"[GoFile] GetContents [{contentId}] status={resp.StatusCode} body={json.Substring(0, Math.Min(json.Length, 500))}");

                using var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("status", out var statusProp) &&
                    statusProp.GetString() == "ok")
                {
                    
                    return doc.RootElement.GetProperty("data").Clone();
                }
                else {
                    var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
                    Logger.Log($"[GoFile] GetContents failed: status={status}");
                }
            } catch (Exception ex) {
                Logger.LogError("GoFile.GetContents", ex);
            }
            return null;
        }

        
        
        
        
        private static List<string> ExtractLinks(JsonElement data)
        {
            var links = new List<string>();
            try {
                string type = data.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

                if (type == "file")
                {
                    if (data.TryGetProperty("link", out var link))
                    {
                        var linkStr = link.GetString();
                        if (!string.IsNullOrEmpty(linkStr)) links.Add(linkStr);
                    }
                }
                else if (type == "folder" && data.TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateObject())
                        links.AddRange(ExtractLinks(child.Value));
                }
            } catch (Exception ex) {
                Logger.LogError("GoFile.ExtractLinks", ex);
            }
            return links;
        }

        
        
        
        
        public static string? ExtractContentId(string goFileUrl)
        {
            try {
                var uri = new Uri(goFileUrl);
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length >= 2 && parts[^2] == "d")
                    return parts[^1];
            } catch { }
            return null;
        }

        
        
        
        
        public static async Task<List<string>> GetDirectLinksAsync(string goFileUrl)
        {
            Logger.Log($"[GoFile] GetDirectLinks for: {goFileUrl}");

            var contentId = ExtractContentId(goFileUrl);
            if (string.IsNullOrEmpty(contentId))
            {
                Logger.Log($"[GoFile] Could not parse content ID from URL: {goFileUrl}");
                return new List<string>();
            }

            
            if (string.IsNullOrEmpty(_accountToken))
                _accountToken = await CreateGuestAccountAsync();

            if (string.IsNullOrEmpty(_accountToken))
            {
                Logger.Log("[GoFile] Failed to obtain account token.");
                return new List<string>();
            }

            
            var data = await GetContentsAsync(contentId, _accountToken);
            if (data == null)
            {
                
                Logger.Log("[GoFile] Retrying with fresh account token...");
                _accountToken = await CreateGuestAccountAsync();
                if (_accountToken != null)
                    data = await GetContentsAsync(contentId, _accountToken);
            }

            if (data == null)
            {
                Logger.Log("[GoFile] GetContents returned null after retry.");
                return new List<string>();
            }

            
            var links = ExtractLinks(data.Value);
            Logger.Log($"[GoFile] Found {links.Count} direct link(s): {string.Join(", ", links)}");
            return links;
        }

        
        
        
        
        public static async Task<(bool found, string pageUrl, List<string> directLinks)> CheckAndResolveAsync(string steamRipPageUrl)
        {
            try {
                Logger.Log($"[GoFile] Checking SteamRIP page: {steamRipPageUrl}");
                var html = await _http.GetStringAsync(steamRipPageUrl);
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                
                var goFileNode = doc.DocumentNode
                    .SelectNodes("//a[contains(@href, 'gofile.io')] | //a[contains(text(), 'GOFILE')]")
                    ?.FirstOrDefault();

                if (goFileNode == null)
                {
                    
                    var allLinks = doc.DocumentNode.SelectNodes("//a[@href]");
                    if (allLinks != null)
                    {
                        foreach (var a in allLinks)
                        {
                            var h = a.GetAttributeValue("href", "");
                            if (h.Contains("gofile.io"))
                            {
                                goFileNode = a;
                                break;
                            }
                        }
                    }
                }

                string href = "";
                if (goFileNode == null)
                {
                    
                    var match = Regex.Match(html, @"href\s*=\s*[""']?([^""' >]*gofile\.io[^""' >]*)[""']?", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        href = match.Groups[1].Value;
                    }
                    else
                    {
                        Logger.Log("[GoFile] No GoFile link found on SteamRIP page via DOM or Regex fallback.");
                        return (false, "", new List<string>());
                    }
                }
                else
                {
                    href = goFileNode.GetAttributeValue("href", "");
                }

                if (href.StartsWith("//")) href = "https:" + href;
                if (!href.StartsWith("http") && href.Contains("gofile.io")) href = "https://" + href.TrimStart('/');
                
                Logger.Log($"[GoFile] Found GoFile page link: {href}");

                
                var directLinks = await GetDirectLinksAsync(href);
                
                
                return (!string.IsNullOrEmpty(href), href, directLinks);
            } catch (Exception ex) {
                Logger.LogError("GoFile.CheckAndResolve", ex);
                return (false, "", new List<string>());
            }
        }
    }
}
