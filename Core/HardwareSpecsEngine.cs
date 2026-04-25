using System;
using System.Collections.Generic;
using System.Management;
using System.IO;
using System.Text.RegularExpressions;

namespace SteamRipApp.Core
{
    public static class HardwareSpecsEngine
    {
        public static LocalMachineSpecs GetLocalSpecs()
        {
            var specs = new LocalMachineSpecs();
            try {
                
                using (var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        specs.OperatingSystem = obj["Caption"]?.ToString() ?? "Unknown";
                        break;
                    }
                }

                
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        specs.Processor = obj["Name"]?.ToString() ?? "Unknown";
                        break;
                    }
                }

                
                using (var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes))
                        {
                            specs.MemoryGB = (int)(bytes / (1024 * 1024 * 1024));
                        }
                        break;
                    }
                }

                
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        specs.VideoCard = obj["Name"]?.ToString() ?? "Unknown";
                        break;
                    }
                }

            } catch (Exception ex) {
                Logger.LogError("HardwareSpecs", ex);
            }
            return specs;
        }

        public static int GetDriveFreeSpaceGB(string path)
        {
            try {
                string root = Path.GetPathRoot(path) ?? "C:\\";
                var drive = new DriveInfo(root);
                return (int)(drive.AvailableFreeSpace / (1024 * 1024 * 1024));
            } catch { return 0; }
        }

        public static bool? EvaluateRequirement(string requireName, string requireValue, LocalMachineSpecs local, string? gamePath = null)
        {
            try {
                if (string.IsNullOrEmpty(requireValue)) return null;

                requireName = requireName.ToLowerInvariant();
                var reqLower = requireValue.ToLowerInvariant();

                
                if (requireName.Contains("storage") || requireName.Contains("disk") || requireName.Contains("space"))
                {
                    var match = Regex.Match(reqLower, @"(\d+)\s*(gb|mb)");
                    if (match.Success)
                    {
                        int reqSpace = int.Parse(match.Groups[1].Value);
                        if (match.Groups[2].Value == "mb") reqSpace = reqSpace / 1024;
                        if (reqSpace == 0) reqSpace = 1;
                        
                        int available = GetDriveFreeSpaceGB(gamePath ?? AppDomain.CurrentDomain.BaseDirectory);
                        return available >= reqSpace;
                    }
                }

                
                if (requireName.Contains("memory") || requireName.Contains("ram"))
                {
                    var match = Regex.Match(reqLower, @"(\d+)\s*(gb|mb)");
                    if (match.Success)
                    {
                        int reqMem = int.Parse(match.Groups[1].Value);
                        if (match.Groups[2].Value == "mb") reqMem = reqMem / 1024;
                        if (reqMem == 0) reqMem = 1;
                        return local.MemoryGB >= reqMem;
                    }
                }

                
                if (requireName.Contains("os") || requireName.Contains("windows"))
                {
                    if (reqLower.Contains("11") && !local.OperatingSystem.Contains("11")) return false;
                    return true; 
                }

                
                if (requireName.Contains("processor") || requireName.Contains("cpu"))
                {
                    var reqScore = GetHardwareScore(requireValue, false);
                    var localScore = GetHardwareScore(local.Processor, false);
                    if (reqScore > 0 && localScore > 0) return localScore >= reqScore;
                }

                if (requireName.Contains("graphics") || requireName.Contains("gpu") || requireName.Contains("video card"))
                {
                    var reqScore = GetHardwareScore(requireValue, true);
                    var localScore = GetHardwareScore(local.VideoCard, true);
                    if (reqScore > 0 && localScore > 0) return localScore >= reqScore;
                }

                return null;
            } catch {
                return null;
            }
        }

        private static int GetHardwareScore(string name, bool isGpu)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            name = name.ToLowerInvariant();

            int gen = 0;
            int tier = 0;

            if (isGpu) {
                
                if (name.Contains("rtx")) {
                    var m = Regex.Match(name, @"rtx\s*(\d)(\d)(\d)0");
                    if (m.Success) {
                        gen = int.Parse(m.Groups[1].Value) * 10;
                        tier = int.Parse(m.Groups[2].Value) * 10 + int.Parse(m.Groups[3].Value);
                        return gen * 100 + tier;
                    }
                }
                if (name.Contains("gtx")) {
                    var m = Regex.Match(name, @"gtx\s*(\d+)(\d)0");
                    if (m.Success) {
                        gen = int.Parse(m.Groups[1].Value);
                        tier = int.Parse(m.Groups[2].Value);
                        return gen * 10 + tier;
                    }
                }
                
                if (name.Contains("rx")) {
                    var m = Regex.Match(name, @"rx\s*(\d)(\d)00");
                    if (m.Success) {
                        gen = int.Parse(m.Groups[1].Value) * 10;
                        tier = int.Parse(m.Groups[2].Value);
                        return gen * 100 + tier;
                    }
                }
            } else {
                
                var mi = Regex.Match(name, @"i(\d)-(\d+)");
                if (mi.Success) {
                    tier = int.Parse(mi.Groups[1].Value);
                    gen = int.Parse(mi.Groups[2].Value);
                    if (gen > 100) gen = gen / 100; 
                    return gen * 10 + tier;
                }
                
                var mr = Regex.Match(name, @"ryzen\s*(\d)\s*(\d)");
                if (mr.Success) {
                    tier = int.Parse(mr.Groups[1].Value);
                    gen = int.Parse(mr.Groups[2].Value);
                    return gen * 10 + tier;
                }
            }

            return 0;
        }
    }

    public class LocalMachineSpecs
    {
        public string OperatingSystem { get; set; } = "Windows";
        public string Processor { get; set; } = "Unknown Processor";
        public int MemoryGB { get; set; } = 8;
        public string VideoCard { get; set; } = "Unknown Graphics";
    }
}
