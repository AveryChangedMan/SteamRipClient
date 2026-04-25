using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SteamRipApp.Core
{
    public static class NativeBridgeService
    {
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static bool _isRunning;
        private static readonly List<string> _logs = new List<string>();
        private static readonly List<WebSocket> _activeClients = new List<WebSocket>();
        public static event Action<string, string>? OnLog; 
        public static bool IsRunning => _isRunning;

        public static void Start()
        {
            if (_isRunning) return;
            if (!GlobalSettings.IsSteamIntegrationEnabled) {
                Log("Cannot start: Steam Integration is disabled in settings.", "SYSTEM");
                return;
            }
            _isRunning = true;
            _cts = new CancellationTokenSource();
            
            Log("Starting Steam Integration Service...", "SYSTEM");
            
            
            Task.Run(() => RunWebSocketHub(_cts.Token));

            
            Task.Run(() => RunServiceMonitor(_cts.Token));
        }

        public static void Stop()
        {
            Log("Stopping Steam Integration Service...", "SYSTEM");
            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();

            lock (_activeClients)
            {
                foreach (var client in _activeClients)
                {
                    if (client.State == WebSocketState.Open)
                    {
                        _ = client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service Stopped", CancellationToken.None);
                    }
                }
                _activeClients.Clear();
            }
        }

        public static void Log(string message, string category = "GENERAL")
        {
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] [{category}] {message}";
            lock (_logs) {
                _logs.Add(timestamped);
                if (_logs.Count > 500) _logs.RemoveAt(0);
            }
            OnLog?.Invoke(timestamped, category);
            Logger.Log(timestamped);
            
            
            _ = Task.Run(async () => {
                try { await BroadcastLog(timestamped, category); } catch { }
            });
        }

        private static async Task BroadcastLog(string log, string category)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { action = "log", message = log, category = category }));
            WebSocket[] clients;
            lock (_activeClients) clients = _activeClients.ToArray();
            
            foreach (var client in clients)
            {
                if (client.State == WebSocketState.Open)
                {
                    try {
                        await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    } catch { }
                }
            }
        }

        private static void SetupResources()
        {
            try {
                string localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp");
                string[] folders = { "JS", "Redist" };
                
                foreach (var folder in folders) {
                    string targetDir = Path.Combine(localAppData, folder);
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    string sourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder);
                    if (Directory.Exists(sourceDir)) {
                        foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories)) {
                            string relativePath = Path.GetRelativePath(sourceDir, file);
                            string destFile = Path.Combine(targetDir, relativePath);
                            string? destDir = Path.GetDirectoryName(destFile);
                            if (destDir != null && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                            
                            if (!File.Exists(destFile) || File.GetLastWriteTime(file) > File.GetLastWriteTime(destFile)) {
                                File.Copy(file, destFile, true);
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.LogError("SetupResources", ex);
            }
        }

        private static string GetDynamicScript()
        {
            string userJs = "";
            try {
                string localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp");
                string jsPath = Path.Combine(localAppData, "JS", "Button.js");
                
                if (!File.Exists(jsPath)) jsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JS", "Button.js");
                
                if (File.Exists(jsPath)) {
                    userJs = File.ReadAllText(jsPath);
                }
            } catch { }

            return "(function() {\n" +
                   "    if (window._steamIntegrationInjected) return;\n" +
                   "    window._steamIntegrationInjected = true;\n" +
                   "    let cmdEl = document.getElementById('steam-integration-commands');\n" +
                   "    if (!cmdEl) {\n" +
                   "        cmdEl = document.createElement('div');\n" +
                   "        cmdEl.id = 'steam-integration-commands';\n" +
                   "        cmdEl.style.position = 'fixed';\n" +
                   "        cmdEl.style.bottom = '0';\n" +
                   "        cmdEl.style.right = '0';\n" +
                   "        cmdEl.style.width = '1px';\n" +
                   "        cmdEl.style.height = '1px';\n" +
                   "        cmdEl.style.opacity = '0.01';\n" +
                   "        cmdEl.style.zIndex = '9999';\n" +
                   "        cmdEl.style.pointerEvents = 'none';\n" +
                   "        document.body.appendChild(cmdEl);\n" +
                   "    }\n" +
                   "    let _consecutiveFailures = 0;\n" +
                   "    function checkConnectivity() {\n" +
                   "        const testWs = new WebSocket('ws://localhost:8081/');\n" +
                   "        testWs.onopen = () => { _consecutiveFailures = 0; testWs.close(); };\n" +
                   "        testWs.onerror = () => {\n" +
                   "            _consecutiveFailures++;\n" +
                   "            if (_consecutiveFailures >= 5) {\n" +
                   "                console.log('[Steam Bridge] Service lost. Reverting UI.');\n" +
                   "                if (window.__steamPlayHijackerObserver) window.__steamPlayHijackerObserver.disconnect();\n" +
                   "                document.querySelectorAll('[data-hijacked]').forEach(el => {\n" +
                   "                    el.removeAttribute('data-hijacked');\n" +
                   "                    location.reload(); // Hard reset is safest to restore Steam UI state\n" +
                   "                });\n" +
                   "                window._steamIntegrationInjected = false;\n" +
                   "                clearInterval(_mainInterval);\n" +
                   "            }\n" +
                   "        };\n" +
                   "    }\n" +
                   "    const _mainInterval = setInterval(() => {\n" +
                   "        checkConnectivity();\n" +
                   "        const txt = cmdEl.innerText.trim();\n" +
                   "        if (txt) {\n" +
                   "            const match = txt.match(/^Play\\s+(\\d+)/i);\n" +
                   "            if (match) {\n" +
                   "                const ws = new WebSocket('ws://localhost:8081/');\n" +
                   "                ws.onopen = () => {\n" +
                   "                    ws.send(JSON.stringify({ action: 'launch', appId: match[1], url: window.location.href, title: document.title }));\n" +
                   "                    setTimeout(() => ws.close(), 500);\n" +
                   "                };\n" +
                   "            }\n" +
                   "            cmdEl.innerText = '';\n" +
                   "        }\n" +
                   "    }, 1000);\n" +
                   userJs + "\n" +
                   "})();";
        }

        private static async Task RunServiceMonitor(CancellationToken token)
        {
            SetupResources();
            int tick = 0;
            while (!token.IsCancellationRequested && _isRunning)
            {
                try {
                    var steamProcesses = Process.GetProcessesByName("steam");
                    if (steamProcesses.Length > 0)
                    {
                        var steam = steamProcesses[0];
                        string? cmdLine = GetCommandLine(steam);
                        
                        if (cmdLine != null && !cmdLine.Contains("-cef-enable-debugging"))
                        {
                            Log("Steam debugging disabled. Relaunching with debug mode...", "SYSTEM");
                            string? steamPath = SteamManager.GetSteamPath();
                            if (!string.IsNullOrEmpty(steamPath))
                            {
                                SteamManager.KillSteamProcess();
                                await Task.Delay(1000);
                                Process.Start(new ProcessStartInfo {
                                    FileName = Path.Combine(steamPath, "steam.exe"),
                                    Arguments = "-cef-enable-debugging",
                                    UseShellExecute = true
                                });
                                Log("Steam relaunched in debug mode.", "SYSTEM");
                            }
                        }
                        else {
                            if (await BridgeInjector.InjectAsync(GetDynamicScript()))
                            {
                                if (tick % 12 == 0) Log("Integration active on Steam UI.", "SYSTEM");
                            }
                        }
                    }
                    else {
                        if (tick % 12 == 0) Log("Waiting for Steam...", "PROCESS");
                    }
                } catch (Exception ex) {
                    Logger.LogError("ServiceMonitor", ex);
                    Log($"Monitor error: {ex.Message}", "PROCESS");
                }
                
                tick++;
                await Task.Delay(1000, token);
            }
        }

        private static async Task RunWebSocketHub(CancellationToken token)
        {
            try {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://localhost:8081/");
                _listener.Start();
            } catch (HttpListenerException ex) when (ex.ErrorCode == 183 || ex.ErrorCode == 32) {
                Log("Bridge port 8081 is already in use. Relying on existing background instance.", "SYSTEM");
                _isRunning = false; 
                return;
            } catch (Exception ex) {
                Logger.LogError("ServiceStart", ex);
                _isRunning = false;
                return;
            }

            while (!token.IsCancellationRequested && _isRunning)
            {
                try {
                    var context = await _listener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        _ = HandleConnection(wsContext.WebSocket, token);
                    }
                    else {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                } catch (Exception ex) when (!(ex is HttpListenerException)) {
                    Logger.LogError("ServiceWS", ex);
                }
            }
        }

        private static async Task HandleConnection(WebSocket socket, CancellationToken token)
        {
            lock (_activeClients) _activeClients.Add(socket);
            byte[] buffer = new byte[1024 * 4];
            try {
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        try {
                            using var doc = JsonDocument.Parse(msg);
                            if (doc.RootElement.TryGetProperty("action", out var actionProp))
                            {
                                string action = actionProp.GetString() ?? "";
                                if (action == "log")
                                {
                                    string logMsg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                                    Log($"{logMsg}", "INTERFACE");
                                    continue;
                                }
                                
                                if (action == "launch")
                                {
                                    string steamPath = SteamManager.GetSteamPath() ?? "";
                                    string title = "";
                                    if (doc.RootElement.TryGetProperty("title", out var titleProp)) title = titleProp.GetString() ?? "";
                                    string url = "";
                                    if (doc.RootElement.TryGetProperty("url", out var urlProp)) url = urlProp.GetString() ?? "";
                                    string appId = "";
                                    if (doc.RootElement.TryGetProperty("appId", out var appIdProp)) appId = appIdProp.GetString() ?? "";
                            Log($"Launch request: {title} (AppID: {appId})", "LAUNCHER");

                            
                            if (string.IsNullOrEmpty(title) || title == "Steam" || title == "Steam Big Picture Mode")
                            {
                                var matchApp = System.Text.RegularExpressions.Regex.Match(url, @"/(app|details)/([0-9]+)");
                                if (matchApp.Success)
                                {
                                    string appIdStr = matchApp.Groups[2].Value;
                                    var game = GlobalSettings.Library.FirstOrDefault(g => 
                                        (g.Url != null && g.Url.Contains(appIdStr)) || 
                                        (g.LocalPath != null && g.LocalPath.Contains(appIdStr)));
                                    
                                    if (game != null) title = game.Title;
                                    else {
                                        string acfPath = Path.Combine(steamPath, "steamapps", $"appmanifest_{appIdStr}.acf");
                                        if (File.Exists(acfPath))
                                        {
                                            string acf = File.ReadAllText(acfPath);
                                            var nameMatch = System.Text.RegularExpressions.Regex.Match(acf, @"""name""\s+""([^""]+)""");
                                            if (nameMatch.Success) title = nameMatch.Groups[1].Value;
                                        }
                                    }
                                }
                            }
                            
                            if ((string.IsNullOrEmpty(title) || title == "Steam") && !string.IsNullOrEmpty(appId))
                            {
                                var libGame = GlobalSettings.Library.FirstOrDefault(g => 
                                    (g.ManualSteamAppId?.ToString() ?? g.SteamAppId?.ToString()) == appId);
                                if (libGame != null) title = libGame.Title;
                            }

                            bool mappingFound = false;
                            ulong mappedLongId = 0;
                            if (!string.IsNullOrEmpty(appId) && GlobalSettings.MemoryTable.TryGetValue(appId, out var mappedIdStr))
                            {
                                if (ulong.TryParse(mappedIdStr, out mappedLongId))
                                {
                                    Log($"Mapping found: {appId} -> {mappedLongId}", "LAUNCHER");
                                    mappingFound = true;
                                }
                            }

                            Log($"Launching '{title}' (AppID: {appId})...", "LAUNCHER");
                            string userId = GlobalSettings.SelectedSteamAccountId;
                            if (string.IsNullOrEmpty(userId)) userId = SteamManager.GetSteamUserIds(steamPath).FirstOrDefault() ?? "";
                            string vdfPath = Path.Combine(steamPath, "userdata", userId, "config", "shortcuts.vdf");
                            bool launched = false;

                            if (mappingFound)
                            {
                                string targetTitle = title;
                                var mappedGame = GlobalSettings.Library.FirstOrDefault(g => 
                                    (g.ManualSteamAppId?.ToString() ?? g.SteamAppId?.ToString()) == appId);
                                
                                if (mappedGame != null) 
                                {
                                    targetTitle = mappedGame.Title;
                                    bool titleMatch = title.Contains(targetTitle, StringComparison.OrdinalIgnoreCase) || 
                                                     targetTitle.Contains(title, StringComparison.OrdinalIgnoreCase) ||
                                                     (title == "Steam") || (title == "Steam Big Picture Mode");

                                    if (!titleMatch)
                                    {
                                        Log($"Conflict detected: {title} vs {targetTitle}. Skipping mapping.", "LAUNCHER");
                                        mappingFound = false;
                                    }
                                }

                                if (mappingFound)
                                {
                                    Log($"Launching via Steam ID: {mappedLongId}", "LAUNCHER");
                                    Process.Start(new ProcessStartInfo {
                                        FileName = $"steam://rungameid/{mappedLongId}",
                                        UseShellExecute = true
                                    });
                                    launched = true;
                                }
                            }
                            
                            if (!launched && !string.IsNullOrEmpty(title) && title != "Steam" && File.Exists(vdfPath))
                            {
                                var shortcuts = VdfUtility.ReadShortcuts(vdfPath);
                                var match = shortcuts.FirstOrDefault(s => string.Equals(s.AppName, title, StringComparison.OrdinalIgnoreCase));
                                if (match == null) match = shortcuts.FirstOrDefault(s => s.AppName?.StartsWith(title, StringComparison.OrdinalIgnoreCase) == true);
                                if (match == null) match = shortcuts.FirstOrDefault(s => !string.IsNullOrEmpty(s.AppName) && (title.Contains(s.AppName, StringComparison.OrdinalIgnoreCase) || s.AppName.Contains(title, StringComparison.OrdinalIgnoreCase)));

                                if (match != null)
                                {
                                    ulong longId = SteamManager.CalculateLongId(match.AppID);
                                    Log($"Shortcut found in config. Launching: {longId}", "CONFIG");
                                    
                                    Process.Start(new ProcessStartInfo {
                                        FileName = $"steam://rungameid/{longId}",
                                        UseShellExecute = true
                                    });
                                    launched = true;
                                }
                            }

                                if (!launched)
                                {
                                    Log($"Unable to find shortcut for '{title}'. Please add it manually to Steam.", "CONFIG");
                                }
                            }
                        }
                    } catch (Exception ex) {
                        Logger.LogError("LaunchRedirect", ex);
                    }
                }
            }
            } catch { }
            finally {
                lock (_activeClients) _activeClients.Remove(socket);
                if (socket.State == WebSocketState.Open) {
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
                }
            }
        }

        private static string? GetCommandLine(Process process)
        {
            try {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
                using var objects = searcher.Get();
                return objects.Cast<System.Management.ManagementBaseObject>().FirstOrDefault()?["CommandLine"]?.ToString();
            } catch { return null; }
        }

        public static async Task<bool> IntegrateGame(string localPath, int appId, string title, int? oldAppId = null)
        {
            try {
                string? steamPath = SteamManager.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return false;

                string commonPath = Path.Combine(steamPath, "steamapps", "common");
                string steamappsPath = Path.Combine(steamPath, "steamapps");
                string targetDir = Path.Combine(commonPath, title);

                if (oldAppId.HasValue && oldAppId.Value != appId)
                {
                    Log($"Updating identity: {oldAppId} -> {appId}", "SYSTEM");
                    string oldKey = oldAppId.Value.ToString();
                    if (GlobalSettings.MemoryTable.TryGetValue(oldKey, out var mappedId))
                    {
                        GlobalSettings.MemoryTable.Remove(oldKey);
                        GlobalSettings.MemoryTable[appId.ToString()] = mappedId;
                    }
                    RemoveManifest(oldAppId.Value);
                }

                Log($"Linking folder: {title}...", "DISK");
                if (!EstablishSymlink(localPath, targetDir)) return false;

                Log($"Registering game with ID {appId}...", "CONFIG");
                WriteSteamManifest(steamappsPath, appId, title);

                Log($"Applying Goldberg patch for ID {appId}...", "PATCH");
                await ApplyGoldbergPatchAsync(localPath, appId);

                return true;
            } catch (Exception ex) {
                Logger.LogError("IntegrateGame", ex);
                return false;
            }
        }

        public static bool EstablishSymlink(string source, string target)
        {
            return BatchSymlinks(new List<(string, string)> { (source, target) });
        }

        public static bool BatchSymlinks(List<(string source, string target)> links)
        {
            try {
                var linksToCreate = new List<(string source, string target)>();
                foreach (var link in links)
                {
                    if (Directory.Exists(link.target)) {
                        var attr = File.GetAttributes(link.target);
                        if (attr.HasFlag(FileAttributes.ReparsePoint)) continue;
                        Log($"Conflict: '{link.target}' already exists as a real folder.", "DISK");
                        continue;
                    }
                    linksToCreate.Add(link);
                }

                if (linksToCreate.Count == 0) return true;

                var sb = new StringBuilder();
                foreach (var link in linksToCreate) sb.Append($"New-Item -ItemType SymbolicLink -Path '{link.target}' -Target '{link.source}'; ");

                var startInfo = new ProcessStartInfo {
                    FileName = "powershell.exe",
                    Arguments = $"-Command \"{sb}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                if (!SteamManager.IsRunningAsAdmin()) startInfo.Verb = "runas";

                var proc = Process.Start(startInfo);
                proc?.WaitForExit();

                return linksToCreate.All(l => Directory.Exists(l.target));
            } catch (Exception ex) {
                Logger.LogError("BatchSymlink", ex);
                return false;
            }
        }

        private static void WriteSteamManifest(string steamappsPath, int appId, string title)
        {
            string manifestPath = Path.Combine(steamappsPath, $"appmanifest_{appId}.acf");
            string content = $@"""AppState""
{{
	""appid""		""{appId}""
	""Universe""		""1""
	""name""		""{title}""
	""StateFlags""		""4""
	""installdir""		""{title}""
	""LastUpdated""		""{DateTimeOffset.Now.ToUnixTimeSeconds()}""
	""UpdateResult""		""0""
	""SizeOnDisk""		""1""
	""buildid""		""0""
	""AllowSync""		""0""
	""ScheduledAutoUpdate""		""0""
}}";
            File.WriteAllText(manifestPath, content);
        }

        public static void RemoveManifest(int appId)
        {
            try {
                string? steamPath = SteamManager.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return;
                string manifestPath = Path.Combine(steamPath, "steamapps", $"appmanifest_{appId}.acf");
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                    Log($"Removed manifest: {appId}", "SYSTEM");
                }
            } catch { }
        }

        public static async Task ApplyGoldbergPatchAsync(string gamePath, int appId)
        {
            try {
                Log($"Scanning for Steam API files...", "PATCH");
                string source64 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Emulator", "steam_api64.dll");
                string source32 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Emulator", "steam_api.dll");

                if (!Directory.Exists(gamePath)) return;

                var enumOptions = new EnumerationOptions { 
                    IgnoreInaccessible = true, 
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                };

                var files = await Task.Run(() => Directory.GetFiles(gamePath, "steam_api*.dll", enumOptions).ToList());
                if (files.Count == 0)
                {
                    string parent = Path.GetDirectoryName(gamePath) ?? "";
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        files = await Task.Run(() => Directory.GetFiles(parent, "steam_api*.dll", enumOptions).ToList());
                }

                foreach (var dll in files)
                {
                    string fileName = Path.GetFileName(dll).ToLower();
                    string? dir = Path.GetDirectoryName(dll);
                    if (string.IsNullOrEmpty(dir)) continue; 
                    
                    string bak = dll + ".bak";
                    
                    try {
                        if (!File.Exists(bak)) File.Move(dll, bak);

                        string source = fileName.Contains("64") ? source64 : source32;
                        if (File.Exists(source))
                        {
                            Log($"Patching {fileName}...", "PATCH");
                            File.Copy(source, dll, true);
                            await File.WriteAllTextAsync(Path.Combine(dir, "steam_appid.txt"), appId.ToString());
                            
                            string receiptPath = Path.Combine(gamePath, "PatchLog.txt");
                            string receiptContent = $"Title: {Path.GetFileName(gamePath)}\nID: {appId}\nDate: {DateTime.Now}\nStatus: Patched";
                            await File.WriteAllTextAsync(receiptPath, receiptContent);
                        }
                    } catch (Exception ex) {
                        Logger.LogError($"Patch_DLL_{fileName}", ex);
                    }
                }
            } catch (Exception ex) {
                Logger.LogError("ApplyGoldbergPatch", ex);
            }
        }

        public static async Task ReverseGoldbergPatchAsync(string gamePath)
        {
            try {
                Log($"Scanning for patched files to reverse...", "PATCH");
                if (!Directory.Exists(gamePath)) return;

                var enumOptions = new EnumerationOptions { 
                    IgnoreInaccessible = true, 
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                };

                var files = await Task.Run(() => Directory.GetFiles(gamePath, "steam_api*.dll.bak", enumOptions).ToList());
                
                foreach (var bak in files)
                {
                    string dll = bak.Substring(0, bak.Length - 4);
                    string? dir = Path.GetDirectoryName(bak);
                    if (string.IsNullOrEmpty(dir)) continue;

                    try {
                        if (File.Exists(dll)) File.Delete(dll);
                        File.Move(bak, dll);
                        Log($"Restored {Path.GetFileName(dll)}", "PATCH");

                        string appIdFile = Path.Combine(dir, "steam_appid.txt");
                        if (File.Exists(appIdFile)) File.Delete(appIdFile);
                    } catch (Exception ex) {
                        Logger.LogError($"Reverse_Patch_DLL_{Path.GetFileName(bak)}", ex);
                    }
                }

                string receiptPath = Path.Combine(gamePath, "PatchLog.txt");
                if (File.Exists(receiptPath)) File.Delete(receiptPath);
                
                string legacyReceipt = Path.Combine(gamePath, "GBinitTimeStamp.txt");
                if (File.Exists(legacyReceipt)) File.Delete(legacyReceipt);

                Log("Goldberg patch reversed successfully.", "PATCH");
            } catch (Exception ex) {
                Logger.LogError("ReverseGoldbergPatch", ex);
            }
        }

        public static void HideWorkerShortcuts()
        {
            try {
                string? steamPath = SteamManager.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return;

                var userIds = string.IsNullOrEmpty(GlobalSettings.SelectedSteamAccountId)
                              ? SteamManager.GetSteamUserIds(steamPath)
                              : new List<string> { GlobalSettings.SelectedSteamAccountId };

                foreach (var userId in userIds)
                {
                    string vdfPath = Path.Combine(steamPath, "userdata", userId, "config", "shortcuts.vdf");
                    if (!File.Exists(vdfPath)) continue;

                    var shortcuts = VdfUtility.ReadShortcuts(vdfPath);
                    foreach (var s in shortcuts)
                    {
                        if (s.LaunchOptions.Contains("--worker", StringComparison.OrdinalIgnoreCase) && !s.IsHidden)
                            VdfUtility.FlipIsHidden(vdfPath, s.AppName, hidden: true);
                    }
                }
            } catch { }
        }

        public static List<string> GetLogs()
        {
            lock (_logs) return new List<string>(_logs);
        }
    }
}
