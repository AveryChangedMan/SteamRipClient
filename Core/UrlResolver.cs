using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{
    public static class UrlResolver
    {
        public static async Task<string> ResolveDirectUrlAsync(string hostPageUrl, string gameTitle = "")
        {
            if (string.IsNullOrEmpty(hostPageUrl)) return "";

            try
            {
                
                if (hostPageUrl.Contains("gofile.io/d/"))
                {
                    Logger.Log($"[UrlResolver] Resolving GoFile: {hostPageUrl}");
                    var links = await GoFileClient.GetDirectLinksAsync(hostPageUrl);
                    return links.FirstOrDefault() ?? "";
                }

                
                if (hostPageUrl.Contains("buzzheavier") || hostPageUrl.Contains("bzzhr.to"))
                {
                    Logger.Log($"[UrlResolver] Resolving Buzzheavier: {hostPageUrl}");
                    return await SteamRipScraper.ExtractBuzzheavierDirectUrlAsync(hostPageUrl);
                }

                
                if (hostPageUrl.Contains("/download/") || hostPageUrl.EndsWith(".rar") || hostPageUrl.EndsWith(".zip"))
                {
                    return hostPageUrl;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("UrlResolver", ex);
            }

            return "";
        }
    }
}
