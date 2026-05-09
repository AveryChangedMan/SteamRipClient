using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;

namespace SteamRipApp.Core
{
    public static class Logger
    {
        private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp", "SteamRipApp.log");
        private static readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>();
        private static readonly CancellationTokenSource _cts = new CancellationTokenSource();

        static Logger()
        {
            try {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Task.Factory.StartNew(ProcessQueue, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                Log("=================================================");
                Log($"[SESSION] Started at {DateTime.Now}");
                Log("=================================================");
            } catch { }
        }

        private static void ProcessQueue()
        {
            foreach (var line in _logQueue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    File.AppendAllText(LogPath, line);
                }
                catch { }
            }
        }

        public static void Log(string message)
        {
            try {
                string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                _logQueue.Add(logLine);

                System.Diagnostics.Debug.WriteLine(logLine);
            } catch { }
        }

        public static void LogError(string context, Exception? ex)
        {
            if (ex == null)
            {
                Log($"[ERROR] {context}: Unknown exception (null)");
                return;
            }
            Log($"[ERROR] {context}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }

        public static void Shutdown()
        {
            _cts.Cancel();
            _logQueue.CompleteAdding();
        }
    }
}