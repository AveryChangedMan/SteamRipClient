using Microsoft.UI.Xaml;
using SteamRipApp.Core;

namespace SteamRipApp
{
    public partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Logger.LogError("GLOBAL_CRASH", e.Exception);
            e.Handled = false; 
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            Logger.LogError("TASK_CRASH", e.Exception);
            e.SetObserved();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try {
                var cmdLine = System.Environment.GetCommandLineArgs();
                bool isWorker = cmdLine.Any(a => a.Equals("--worker", System.StringComparison.OrdinalIgnoreCase));

                if (isWorker)
                {
                    Logger.Log("--- WORKER SESSION STARTED ---");
                    
                    
                    int parentPid = -1;
                    int pidIdx = System.Array.IndexOf(cmdLine, "--parent");
                    if (pidIdx != -1 && pidIdx + 1 < cmdLine.Length)
                    {
                        int.TryParse(cmdLine[pidIdx + 1], out parentPid);
                    }

                    GlobalSettings.Load();
                    NativeBridgeService.Start();

                    
                    while (true)
                    {
                        await System.Threading.Tasks.Task.Delay(5000);
                        
                        
                        GlobalSettings.Load(); 
                        if (!GlobalSettings.IsSteamIntegrationEnabled)
                        {
                            Logger.Log("[Worker] Steam Integration disabled in settings. Shutting down.");
                            break;
                        }

                        
                        if (parentPid != -1)
                        {
                            try {
                                var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                                if (parent == null || parent.HasExited) 
                                {
                                    Logger.Log("[Worker] Parent process exited. Shutting down.");
                                    break;
                                }
                            } catch {
                                Logger.Log("[Worker] Parent process lost. Shutting down.");
                                break;
                            }
                        }
                    }

                    NativeBridgeService.Stop();
                    return;
                }

                Logger.Log("--- GUI SESSION STARTED ---");
                GlobalSettings.Load();

                
                if (cmdLine.Length > 1 && cmdLine[1].StartsWith("steamrip://"))
                {
                    HandleProtocolLaunch(cmdLine[1]);
                }

                m_window = new MainWindow();
                m_window.Activate();

                if (GlobalSettings.IsSteamIntegrationEnabled)
                {
                    EnsureWorkerRunning();
                }
                else
                {
                    Logger.Log("[App] Steam Integration is disabled by default. Background worker skipped.");
                }
            } catch (System.Exception ex) {
                Logger.LogError("OnLaunched_CRASH", ex);
            }
        }

        internal void EnsureWorkerRunning()
        {
            System.Threading.Tasks.Task.Run(async () => {
                try {
                    
                    using var tcp = new System.Net.Sockets.TcpClient();
                    var connectTask = tcp.ConnectAsync("localhost", 8081);
                    if (await System.Threading.Tasks.Task.WhenAny(connectTask, System.Threading.Tasks.Task.Delay(500)) == connectTask && tcp.Connected)
                    {
                        Logger.Log("[Worker] Background service already active.");
                        return;
                    }
                    
                    Logger.Log("[Worker] No active service detected. Starting background worker...");
                    string? exe = System.Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        int myPid = System.Environment.ProcessId;
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                            FileName = exe,
                            Arguments = $"--worker --parent {myPid}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                    }
                } catch { }
            });
        }

        private async void HandleProtocolLaunch(string uri)
        {
            try {
                Logger.Log($"[App] Protocol Activation: {uri}");
                
                string decoded = System.Net.WebUtility.UrlDecode(uri);
                if (decoded.Contains("://run/"))
                {
                    string appName = decoded.Split("://run/")[1].TrimEnd('/');
                    Logger.Log($"[App] Protocol Launch Request for: {appName}");
                    
                    var games = await ScannerEngine.GetTrackedGamesAsync();
                    var target = games.FirstOrDefault(g => g.Title.Equals(appName, System.StringComparison.OrdinalIgnoreCase));
                    
                    if (target != null && !string.IsNullOrEmpty(target.ExecutablePath)) {
                        Logger.Log($"[App] Launching {target.Title} via Protocol...");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                            FileName = target.ExecutablePath,
                            WorkingDirectory = System.IO.Path.GetDirectoryName(target.ExecutablePath),
                            UseShellExecute = true
                        });
                    } else {
                        Logger.Log($"[App] ERROR: Game '{appName}' not found in local library or missing executable.");
                    }
                }
            } catch (System.Exception ex) {
                Logger.LogError("ProtocolLaunch", ex);
            }
        }

        internal Window? m_window;
    }
}
