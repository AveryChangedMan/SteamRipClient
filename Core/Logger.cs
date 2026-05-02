using System;
using System.IO;

namespace SteamRipApp.Core
{
    public static class Logger
    {
        private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp", "SteamRipApp.log");
        private static readonly object LockObj = new object();

        static Logger()
        {
            try {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Log("=================================================");
                Log($"[SESSION] Started at {DateTime.Now}");
                Log("=================================================");
            } catch { }
        }

        public static void Log(string message)
        {
            lock (LockObj)
            {
                try {
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogPath, logLine);
                } catch { }
            }
        }

        public static void LogError(string context, Exception ex)
        {
            Log($"[ERROR] {context}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }
    }
}