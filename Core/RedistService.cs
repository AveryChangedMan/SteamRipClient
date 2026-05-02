using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Diagnostics;

namespace SteamRipApp.Core
{
    public class RedistFile
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsInstalled { get; set; }
        public string FriendlyName { get; set; } = "";
    }

    public static class RedistService
    {
        private static readonly RedistDetectionEngine _engine = new RedistDetectionEngine();
        private static readonly List<string> MasterRedistList = new List<string>();
        private static readonly string MasterListPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps", "_CommonRedist.txt");

        static RedistService()
        {
            LoadMasterList();
        }

        private static void LoadMasterList()
        {
            try
            {
                if (File.Exists(MasterListPath))
                {
                    var lines = File.ReadAllLines(MasterListPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                        {
                            MasterRedistList.Add(trimmed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("LoadMasterRedistList", ex);
            }
        }

        private static string BackupPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRipApp", "RedistBackup");

        public static List<RedistFile> GetRequiredRedists(string gameRootPath)
        {
            _engine.RefreshInstalledSoftware();
            var required = new List<RedistFile>();
            string redistPath = Path.Combine(gameRootPath, "_CommonRedist");
            if (!Directory.Exists(redistPath)) return required;

            try
            {
                var files = Directory.GetFiles(redistPath, "*.exe", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(redistPath, "*.msi", SearchOption.AllDirectories)).ToList();

                string manifestPath = Path.Combine(redistPath, "redists.txt");
                var manifestNames = new List<string>();
                if (File.Exists(manifestPath))
                {
                    manifestNames = File.ReadAllLines(manifestPath).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList();
                }

                var allCandidates = files.Select(f => new { Path = f, Name = Path.GetFileName(f) })
                    .Concat(manifestNames.Select(n => new { Path = "", Name = n }))
                    .GroupBy(x => x.Name)
                    .Select(g => g.First());

                foreach (var candidate in allCandidates)
                {
                    string fileName = candidate.Name;

                    if (MasterRedistList.Any(m => string.Equals(m, fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        string finalPath = candidate.Path;

                        if (File.Exists(finalPath))
                        {
                            BackupRedist(finalPath);
                        }
                        else
                        {

                            string backupFile = Path.Combine(BackupPath, fileName);
                            if (File.Exists(backupFile))
                            {
                                finalPath = backupFile;
                            }
                        }

                        var result = _engine.ValidateInstallation(!string.IsNullOrEmpty(finalPath) ? finalPath : fileName);
                        required.Add(new RedistFile
                        {
                            FileName = fileName,
                            FullPath = finalPath,
                            FriendlyName = GetFriendlyName(fileName),
                            IsInstalled = result.IsInstalled
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("GetRequiredRedists", ex);
            }

            return required;
        }

        private static void BackupRedist(string sourcePath)
        {
            try
            {
                if (!Directory.Exists(BackupPath)) Directory.CreateDirectory(BackupPath);

                string fileName = Path.GetFileName(sourcePath);
                string destPath = Path.Combine(BackupPath, fileName);

                if (!File.Exists(destPath))
                {
                    File.Copy(sourcePath, destPath);
                    Logger.Log($"[Redist] Backed up to AppData: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BackupRedist", ex);
            }
        }

        private static string GetFriendlyName(string fileName)
        {
            string lower = fileName.ToLower();
            if (lower.Contains("vcredist") || lower.Contains("vc_redist"))
            {
                string year = "VC++";
                if (lower.Contains("2005")) year = "VC++ 2005";
                else if (lower.Contains("2008")) year = "VC++ 2008";
                else if (lower.Contains("2010")) year = "VC++ 2010";
                else if (lower.Contains("2012")) year = "VC++ 2012";
                else if (lower.Contains("2013")) year = "VC++ 2013";
                else if (lower.Contains("2015") || lower.Contains("2019") || lower.Contains("2022")) year = "VC++ 2015-2022";

                string arch = lower.Contains("x64") ? " (x64)" : " (x86)";
                return year + arch;
            }
            if (lower.Contains("dxsetup") || lower.Contains("dxwebsetup") || lower.Contains("directx")) return "DirectX Runtime";
            if (lower.Contains("physx")) return "NVIDIA PhysX System Software";
            if (lower.Contains("oalinst") || lower.Contains("openal")) return "OpenAL Audio SDK";
            if (lower.Contains("dotnet")) return ".NET Framework / Runtime";
            if (lower.Contains("xna")) return "Microsoft XNA Framework";
            if (lower.Contains("social-club")) return "Rockstar Social Club";

            return fileName;
        }

        public static void UpdateRedistManifest(string redistFolderPath)
        {
            try
            {
                if (!Directory.Exists(redistFolderPath)) return;

                string manifestPath = Path.Combine(redistFolderPath, "redists.txt");
                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(manifestPath))
                {
                    foreach (var line in File.ReadAllLines(manifestPath))
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed)) existingNames.Add(trimmed);
                    }
                }

                var currentFiles = Directory.GetFiles(redistFolderPath, "*.exe", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(redistFolderPath, "*.msi", SearchOption.AllDirectories))
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Cast<string>();

                bool changed = false;
                foreach (var name in currentFiles)
                {
                    if (existingNames.Add(name))
                    {
                        changed = true;
                    }
                }

                if (changed)
                {
                    File.WriteAllLines(manifestPath, existingNames.OrderBy(n => n));
                    Logger.Log($"[Redist] Updated manifest in: {redistFolderPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("UpdateRedistManifest", ex);
            }
        }

        public static bool CheckIfInstalled(string fileNameOrPath)
        {
            var result = _engine.ValidateInstallation(fileNameOrPath);
            return result.IsInstalled;
        }
    }
}