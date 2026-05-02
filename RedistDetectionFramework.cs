using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamRipApp.Core
{
    public class RedistDetectionEngine
    {
        private Dictionary<string, string> _installedSoftware = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public RedistDetectionEngine()
        {
            RefreshInstalledSoftware();
        }

        public void RefreshInstalledSoftware()
        {

            _installedSoftware = GetAllInstalledSoftware();
        }

        #region Public API

        public (bool IsInstalled, string Version) ValidateInstallation(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return (false, "Invalid Input");
            string lowerInput = input.ToLower();

            if (lowerInput.EndsWith(".msi"))
            {
                var msiInfo = GetMsiInfo(input);
                if (msiInfo.Name != null)
                {
                    foreach (var entry in _installedSoftware)
                    {
                        if (entry.Key.IndexOf(msiInfo.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                            return (true, entry.Value);
                    }
                }

                if (lowerInput.Contains("gfwl")) return CheckMsiGuid("{F350798C-D8D8-44A5-8022-834F941914CB}");
                if (lowerInput.Contains("msxml4")) return CheckMsiGuid("{716E0306-8318-4364-8B35-29E385A92844}");
            }

            if (lowerInput.Contains("dotnet") || lowerInput.Contains("ndp"))
            {
                var dotNetResult = CheckDotNet(lowerInput);
                if (dotNetResult.IsInstalled) return dotNetResult;
            }

            var pathResult = CheckPathBasedComponents(lowerInput);
            if (pathResult.IsInstalled) return pathResult;

            var vcResult = CheckVisualCpp(input);
            if (vcResult.IsInstalled) return vcResult;

            if (lowerInput.Contains("aio"))
            {
                if (_installedSoftware.Keys.Any(k => k.Contains("2015-2022") || k.Contains("VisualCppRedist AIO")))
                    return (true, "Verified via Components");
            }

            string cleanName = Path.GetFileNameWithoutExtension(input).Replace("_", " ").Replace("-", " ").Replace("setup", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (cleanName.Length > 3)
            {
                foreach (var entry in _installedSoftware)
                {
                    if (entry.Key.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return (true, entry.Value);
                }
            }

            return (false, "Not Found");
        }

        #endregion

        #region Internal Audit Logic

        private (bool IsInstalled, string Version) CheckPathBasedComponents(string input)
        {
            string windir = Environment.GetEnvironmentVariable("windir") ?? @"C:\Windows";
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            var paths = new Dictionary<string, string[]>
            {
                { "dx", new[] { Path.Combine(windir, @"System32\d3dx9_43.dll"), Path.Combine(windir, @"SysWOW64\d3dx9_43.dll") } },
                { "msxml6", new[] { Path.Combine(windir, @"System32\msxml6.dll") } },
                { "msxml4", new[] { Path.Combine(windir, @"SysWOW64\msxml4.dll") } },
                { "vdf", new[] { Path.Combine(windir, @"System32\drivers\vdf.sys") } },
                { "vulkan", new[] { Path.Combine(windir, @"System32\vulkan-1.dll") } },
                { "xna", new[] {
                    Path.Combine(windir, @"Microsoft.NET\assembly\GAC_MSIL\Microsoft.Xna.Framework"),
                    Path.Combine(windir, @"assembly\GAC_32\Microsoft.Xna.Framework"),
                    Path.Combine(windir, @"assembly\GAC_MSIL\Microsoft.Xna.Framework")
                } },
                { "physx", new[] { Path.Combine(pf86, @"NVIDIA Corporation\PhysX\Common\PhysXUpdateLoader.dll") } },
                { "rockstar", new[] { Path.Combine(pf, @"Rockstar Games\Launcher\Launcher.exe") } },
                { "uplay", new[] { Path.Combine(pf86, @"Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe") } }
            };

            if (input.Contains("oal") || input.Contains("openal"))
            {

                if (_installedSoftware.Keys.Any(k => k.Contains("OpenAL", StringComparison.OrdinalIgnoreCase)))
                    return (true, "Verified via Uninstall List");

                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\OpenAL"))
                {
                    if (key != null && key.GetValue("") != null) return (true, "Verified via Registry");
                }
            }

            if (input.Contains("xna"))
            {
                string[] xnaKeys = {
                    @"SOFTWARE\Microsoft\XNA\Framework\v4.0",
                    @"SOFTWARE\Microsoft\XNA\Framework\v3.1",
                    @"SOFTWARE\Microsoft\XNA\Framework\v3.0",
                    @"SOFTWARE\Microsoft\XNA\Framework\v2.0",
                    @"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v4.0",
                    @"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v3.1",
                    @"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v3.0",
                    @"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v2.0"
                };
                foreach (var k in xnaKeys)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(k))
                    {
                        if (key != null && (key.GetValue("Installed")?.ToString() == "1" || key.GetValue("Install")?.ToString() == "1"))
                            return (true, "Verified via Registry");
                    }
                }

                if (_installedSoftware.Keys.Any(k => k.Contains("XNA Framework", StringComparison.OrdinalIgnoreCase)))
                    return (true, "Verified via Uninstall List");
            }

            foreach (var check in paths)
            {
                if (input.Contains(check.Key))
                {
                    foreach (var path in check.Value)
                    {
                        if (File.Exists(path) || Directory.Exists(path)) return (true, "Verified at Path");
                    }
                }
            }
            return (false, "Not Found");
        }

        private (bool IsInstalled, string Version) CheckVisualCpp(string input)
        {
            string lowerInput = input.ToLower();
            string arch = lowerInput.Contains("x64") ? "x64" : "x86";
            string? year = null;

            string ver = GetFileVersion(input);
            if (ver != null) year = VersionToYear(ver);

            if (year == null)
            {
                var match = Regex.Match(input, @"20\d{2}");
                if (match.Success) year = match.Value;
            }

            if (year == null && !lowerInput.Contains("vcredist") && !lowerInput.Contains("vc_redist")) return (false, "Not Found");

            foreach (var entry in _installedSoftware)
            {
                string s = entry.Key.ToLower();
                if (s.Contains("visual c++") && (year == null || s.Contains(year)))
                {
                    bool is64 = s.Contains("x64") || s.Contains("(x64)");
                    if (arch == "x64" && is64) return (true, entry.Value);
                    if (arch == "x86" && !is64) return (true, entry.Value);
                }
            }

            if (year != null && int.TryParse(year.Split('-')[0], out int y) && y >= 2015)
            {
                foreach (var entry in _installedSoftware)
                {
                    string s = entry.Key.ToLower();
                    if (s.Contains("visual c++") && (s.Contains("2015-2022") || s.Contains("2015-2019") || s.Contains("2022")))
                    {
                        bool is64 = s.Contains("x64") || s.Contains("(x64)");
                        if (arch == "x64" && is64) return (true, entry.Value);
                        if (arch == "x86" && !is64) return (true, entry.Value);
                    }
                }
            }

            return (false, "Not Found");
        }

        private (bool IsInstalled, string Version) CheckDotNet(string input)
        {
            try
            {

                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("Version")?.ToString();
                        if (v != null && v.StartsWith("4.")) return (true, "v" + v + " (Built-in)");
                    }
                }

                string dotnetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\shared\Microsoft.WindowsDesktop.App");
                if (Directory.Exists(dotnetPath))
                {
                    var dirs = Directory.GetDirectories(dotnetPath);
                    if (dirs.Any(d => Path.GetFileName(d).StartsWith("6.0"))) return (true, "v6.0 (Installed)");
                }
            } catch { }
            return (false, "Not Found");
        }

        #endregion

        #region Utilities

        private Dictionary<string, string> GetAllInstalledSoftware()
        {
            var software = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var baseKey in keys)
                {
                    try {
                        using (var rk = hive.OpenSubKey(baseKey))
                        {
                            if (rk == null) continue;
                            foreach (var skName in rk.GetSubKeyNames())
                            {
                                using (var sk = rk.OpenSubKey(skName))
                                {
                                    var name = sk?.GetValue("DisplayName")?.ToString();
                                    if (name != null)
                                    {
                                        var ver = sk?.GetValue("DisplayVersion")?.ToString() ?? "Unknown";
                                        if (!software.ContainsKey(name)) software.Add(name, ver);
                                    }
                                }
                            }
                        }
                    } catch { }
                }
            }
            return software;
        }

        private string GetFileVersion(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            try {
                var info = FileVersionInfo.GetVersionInfo(path);
                return info.ProductVersion ?? string.Empty;
            } catch { return string.Empty; }
        }

        private string? VersionToYear(string? ver)
        {
            if (string.IsNullOrEmpty(ver)) return null;
            string major = ver.Split('.')[0];
            switch (major)
            {
                case "8": return "2005";
                case "9": return "2008";
                case "10": return "2010";
                case "11": return "2012";
                case "12": return "2013";
                case "14": return "2015-2022";
                default: return null;
            }
        }

        private (bool IsInstalled, string Version) CheckMsiGuid(string guid)
        {
            foreach (var baseKey in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using (var k = Registry.LocalMachine.OpenSubKey(baseKey + "\\" + guid))
                {
                    if (k != null) return (true, k.GetValue("DisplayVersion")?.ToString() ?? "Registered");
                }
            }
            return (false, "Not Found");
        }

        #endregion

        #region MSI API (P/Invoke)

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out IntPtr phDatabase);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiDatabaseOpenView(IntPtr hDatabase, string szQuery, out IntPtr phView);

        [DllImport("msi.dll")]
        private static extern uint MsiViewExecute(IntPtr hView, IntPtr hRecord);

        [DllImport("msi.dll")]
        private static extern uint MsiViewFetch(IntPtr hView, out IntPtr phRecord);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiRecordGetString(IntPtr hRecord, uint iField, [Out] StringBuilder szValueBuf, ref uint pcchValueBuf);

        [DllImport("msi.dll")]
        private static extern uint MsiCloseHandle(IntPtr hAny);

        private (string Name, string Version) GetMsiInfo(string path)
        {
            if (!File.Exists(path)) return ("Unknown", "Unknown");
            IntPtr hDb = IntPtr.Zero;
            try {
                if (MsiOpenDatabase(path, IntPtr.Zero, out hDb) != 0) return ("Unknown", "Unknown");
                return (GetMsiProp(hDb, "ProductName") ?? "Unknown", GetMsiProp(hDb, "ProductVersion") ?? "Unknown");
            } finally { if (hDb != IntPtr.Zero) MsiCloseHandle(hDb); }
        }

        private string GetMsiProp(IntPtr hDb, string prop)
        {
            IntPtr hView = IntPtr.Zero;
            try {
                string query = $"SELECT `Value` FROM `Property` WHERE `Property` = '{prop}'";
                if (MsiDatabaseOpenView(hDb, query, out hView) != 0) return string.Empty;
                MsiViewExecute(hView, IntPtr.Zero);
                IntPtr hRec = IntPtr.Zero;
                if (MsiViewFetch(hView, out hRec) == 0)
                {
                    uint size = 1024;
                    StringBuilder sb = new StringBuilder((int)size);
                    MsiRecordGetString(hRec, 1, sb, ref size);
                    MsiCloseHandle(hRec);
                    return sb.ToString();
                }
            } finally { if (hView != IntPtr.Zero) MsiCloseHandle(hView); }
            return string.Empty;
        }

        #endregion
    }
}