using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
namespace SteamRipApp.Core
{
    public class CdpTarget
    {
        public string id { get; set; } = "";
        public string title { get; set; } = "";
        public string type { get; set; } = "";
        public string url { get; set; } = "";
        public string webSocketDebuggerUrl { get; set; } = "";
    }
    public static class BridgeInjector
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly HashSet<string> _injectedUrls = new HashSet<string>();
        public static async Task<bool> InjectAsync(string script)
        {
            try {
                string json;
                try {
                    json = await _http.GetStringAsync("http://localhost:8080/json/list");
                } catch {
                    return false; 
                }
                var targets = JsonSerializer.Deserialize<List<CdpTarget>>(json);
                if (targets == null || targets.Count < 1) return false;
                var filteredTargets = targets.Where(t => 
                    !t.title.Contains("notificationtoasts", StringComparison.OrdinalIgnoreCase) &&
                    !t.title.Contains("SharedJSContext", StringComparison.OrdinalIgnoreCase) &&
                    !t.url.StartsWith("devtools://")
                ).ToList();
                var steamTarget = filteredTargets.FirstOrDefault(t => t.title.Contains("Big Picture", StringComparison.OrdinalIgnoreCase))
                               ?? filteredTargets.FirstOrDefault(t => t.title.Equals("Steam", StringComparison.OrdinalIgnoreCase) || t.title.Contains("Steam Library", StringComparison.OrdinalIgnoreCase) || t.url.Contains("steamloopback.host"))
                               ?? filteredTargets.FirstOrDefault(t => t.type == "page");
                if (steamTarget == null || string.IsNullOrEmpty(steamTarget.webSocketDebuggerUrl)) return false;
                string wsUrl = steamTarget.webSocketDebuggerUrl;
                if (_injectedUrls.Contains(wsUrl)) return true;
                await ExecuteScript(wsUrl, script);
                _injectedUrls.Add(wsUrl);
                NativeBridgeService.Log($"[Injection] Bridge successfully initialized in '{steamTarget.title}'", "NETWORK");
                return true;
            } catch (Exception ex) {
                Logger.LogError("BridgeInjection", ex);
            }
            return false;
        }
        private static async Task ExecuteScript(string wsUrl, string script)
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            var command = new {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression = script, userGesture = true, awaitPromise = false }
            };
            string json = JsonSerializer.Serialize(command);
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, true, cts.Token);
            byte[] buf = new byte[8192];
            await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
    }
}

