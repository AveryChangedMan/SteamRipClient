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
        public static List<RedistFile> GetRequiredRedists(string gameRootPath)
        {
            var required = new List<RedistFile>();
            string redistPath = Path.Combine(gameRootPath, "_CommonRedist");
            if (!Directory.Exists(redistPath)) return required;
            try
            {
                var files = Directory.GetFiles(redistPath, "*.exe", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(redistPath, "*.msi", SearchOption.AllDirectories));
                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);
                    if (MasterRedistList.Any(m => string.Equals(m, fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        var result = _engine.ValidateInstallation(file);
                        required.Add(new RedistFile
                        {
                            FileName = fileName,
                            FullPath = file,
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
        public static bool CheckIfInstalled(string fileNameOrPath)
        {
            var result = _engine.ValidateInstallation(fileNameOrPath);
            return result.IsInstalled;
        }
    }
}

