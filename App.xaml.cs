using Microsoft.UI.Xaml;
using SteamRipApp.Core;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

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
            try
            {
                var cmdLine = System.Environment.GetCommandLineArgs();
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

                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError("OnLaunched_CRASH", ex);
            }
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
        public static Window? MainWindowInstance => (Application.Current as App)?.m_window;
        public static bool IsDialogShowing { get; set; }

        private static readonly System.Threading.SemaphoreSlim _uiSemaphore = new System.Threading.SemaphoreSlim(1, 1);

        [System.Diagnostics.DebuggerHidden]
        public static async System.Threading.Tasks.Task<T> RunModalSafeAsync<T>(System.Func<System.Threading.Tasks.Task<T>> action)
        {
            await _uiSemaphore.WaitAsync();
            IsDialogShowing = true;
            try {

                if (MainWindowInstance != null && !MainWindowInstance.Visible)
                    MainWindowInstance.Activate();

                return await action();
            } catch (System.Exception ex) {
                Logger.Log($"[App] Modal operation (T) failed: {ex.Message}");
                return default!;
            } finally {
                IsDialogShowing = false;

                await System.Threading.Tasks.Task.Delay(500);
                _uiSemaphore.Release();
            }
        }

        [System.Diagnostics.DebuggerHidden]
        public static async System.Threading.Tasks.Task RunModalSafeAsync(System.Func<System.Threading.Tasks.Task> action)
        {
            await _uiSemaphore.WaitAsync();
            IsDialogShowing = true;
            try {
                if (MainWindowInstance != null && !MainWindowInstance.Visible)
                    MainWindowInstance.Activate();

                await action();
            } catch (System.Exception ex) {
                Logger.Log($"[App] Modal operation failed: {ex.Message}");
            } finally {
                IsDialogShowing = false;
                await System.Threading.Tasks.Task.Delay(500);
                _uiSemaphore.Release();
            }
        }

        [System.Diagnostics.DebuggerHidden]
        public static async System.Threading.Tasks.Task<ContentDialogResult> ShowDialogSafeAsync(ContentDialog dialog)
        {
            return await RunModalSafeAsync(async () => {

                var window = (Application.Current as App)?.m_window;
                if (window?.Content != null)
                {
                    dialog.XamlRoot = window.Content.XamlRoot;
                }

                try {
                    return await dialog.ShowAsync();
                } catch (System.Exception ex) when (ex.HResult == unchecked((int)0x8000000E)) {

                    await System.Threading.Tasks.Task.Delay(500);
                    return await dialog.ShowAsync();
                }
            });
        }
    }
}