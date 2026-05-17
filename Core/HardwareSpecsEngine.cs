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

                using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
                {
                    string bestGpu = "Unknown Graphics";
                    long maxRam = -1;

                    foreach (var obj in searcher.Get())
                    {
                        string gpuName = obj["Name"]?.ToString() ?? "";
                        long.TryParse(obj["AdapterRAM"]?.ToString(), out long ram);

                        bool isDiscrete = gpuName.Contains("NVIDIA") || gpuName.Contains("AMD") || gpuName.Contains("RTX") || gpuName.Contains("GTX") || gpuName.Contains("Radeon");
                        bool isIntegrated = gpuName.Contains("Intel") || gpuName.Contains("UHD") || gpuName.Contains("Iris") || gpuName.Contains("Vega") || gpuName.Contains("Graphics");

                        if (isDiscrete || (maxRam == -1 && !isIntegrated))
                        {
                            bestGpu = gpuName;
                            maxRam = ram;
                            if (isDiscrete) break;
                        }
                        else if (bestGpu == "Unknown Graphics")
                        {
                            bestGpu = gpuName;
                        }
                    }
                    specs.VideoCard = bestGpu;
                }

                specs.DirectXVersion = DetectDirectXVersion();

            } catch (Exception ex) {
                Logger.LogError("HardwareSpecs", ex);
            }
            return specs;
        }

        private static int DetectDirectXVersion()
        {
            try
            {
                if (File.Exists(Path.Combine(Environment.SystemDirectory, "d3d12.dll"))) return 12;
                if (File.Exists(Path.Combine(Environment.SystemDirectory, "d3d11.dll"))) return 11;
                if (File.Exists(Path.Combine(Environment.SystemDirectory, "d3d10.dll"))) return 10;
                if (File.Exists(Path.Combine(Environment.SystemDirectory, "d3d9.dll"))) return 9;
            }
            catch { }
            return 11;
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

                var parts = reqLower.Split(new string[] { "/", "|", " or ", ",", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);

                if (requireName.Contains("storage") || requireName.Contains("disk") || requireName.Contains("space"))
                {
                    var match = Regex.Match(reqLower, @"(\d+)\s*(gb|mb)");
                    if (match.Success)
                    {
                        if (double.TryParse(match.Groups[1].Value, out double reqSpace))
                        {
                            if (match.Groups[2].Value == "mb") reqSpace = reqSpace / 1024.0;
                            if (reqSpace < 0.1) reqSpace = 1;

                            int available = GetDriveFreeSpaceGB(gamePath ?? AppDomain.CurrentDomain.BaseDirectory);
                            return available >= reqSpace;
                        }
                    }
                }

                if (requireName.Contains("memory") || requireName.Contains("ram"))
                {
                    var match = Regex.Match(reqLower, @"(\d+)\s*(gb|mb)");
                    if (match.Success)
                    {
                        if (double.TryParse(match.Groups[1].Value, out double reqMem))
                        {
                            if (match.Groups[2].Value == "mb") reqMem = reqMem / 1024.0;
                            if (reqMem < 0.1) reqMem = 1;
                            return local.MemoryGB >= reqMem;
                        }
                    }
                }

                if (requireName.Contains("os") || requireName.Contains("windows"))
                {
                    if (reqLower.Contains("11") && !local.OperatingSystem.Contains("11")) return false;
                    if (reqLower.Contains("64") && !Environment.Is64BitOperatingSystem) return false;
                    return true;
                }

                if (requireName.Contains("processor") || requireName.Contains("cpu"))
                {
                    var localScore = GetHardwareScore(local.Processor, false);
                    if (localScore == 0) return null;

                    bool anyPass = false;
                    bool hadValidScore = false;
                    foreach (var part in parts)
                    {
                        var reqScore = GetHardwareScore(part, false);
                        if (reqScore > 0)
                        {
                            hadValidScore = true;
                            if (localScore >= reqScore) anyPass = true;
                        }
                    }
                    return hadValidScore ? anyPass : (bool?)null;
                }

                if (requireName.Contains("graphics") || requireName.Contains("gpu") || requireName.Contains("video card"))
                {
                    var localScore = GetHardwareScore(local.VideoCard, true);
                    if (localScore <= 0) return null;

                    int bestReqScore = -1;
                    foreach (var part in parts)
                    {
                        var reqScore = GetHardwareScore(part, true);
                        if (reqScore > bestReqScore) bestReqScore = reqScore;
                    }

                    if (bestReqScore > 0)
                    {
                        return localScore >= bestReqScore;
                    }
                }

                if (requireName.Contains("network"))
                {
                    return true;
                }

                if (requireName.Contains("directx") || requireName.Contains("dx"))
                {
                    var match = Regex.Match(reqLower, @"(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int reqDX))
                    {
                        return local.DirectXVersion >= reqDX;
                    }
                }

                return null;
            } catch {
                return null;
            }
        }

        public static int GetRankDiff(string requireName, string requireValue, LocalMachineSpecs local)
        {
            try
            {
                if (string.IsNullOrEmpty(requireValue)) return 0;
                bool isGpu = requireName.Contains("graphics") || requireName.Contains("gpu") || requireName.Contains("video") || requireName.Contains("card");
                bool isCpu = requireName.Contains("processor") || requireName.Contains("cpu");

                if (!isGpu && !isCpu) return 0;

                var localScore = GetHardwareScore(isGpu ? local.VideoCard : local.Processor, isGpu);
                if (localScore <= 0) return 0;

                var parts = requireValue.ToLowerInvariant().Split(new string[] { "/", "|", " or ", ",", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                int bestReqScore = -1;
                foreach (var part in parts)
                {
                    var reqScore = GetHardwareScore(part, isGpu);
                    if (reqScore > bestReqScore) bestReqScore = reqScore;
                }

                if (bestReqScore <= 0) return 0;

                return (localScore - bestReqScore) / 1000;
            }
            catch { return 0; }
        }

        private static Dictionary<string, int>? _gpuTierMap = null;
        private static Dictionary<string, int>? _cpuTierMap = null;
        private static readonly object _dbLock = new object();

        private static int GetHardwareScore(string name, bool isGpu)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            name = name.ToLowerInvariant();

            lock (_dbLock)
            {
                if (isGpu) { if (_gpuTierMap == null) LoadTierList(true); }
                else { if (_cpuTierMap == null) LoadTierList(false); }

                var map = isGpu ? _gpuTierMap : _cpuTierMap;
                if (map != null)
                {
                    string cleanName = CleanHardwareName(name);

                    if (map.TryGetValue(cleanName, out int score)) return score;

                    foreach (var kvp in map)
                    {
                        if (cleanName.Contains(kvp.Key) || kvp.Key.Contains(cleanName))
                            return kvp.Value;
                    }
                }
            }

            int gen = 0;
            int tier = 0;

            if (isGpu)
            {

                if (name.Contains("rtx"))
                {
                    var m = Regex.Match(name, @"rtx\s*(\d)(\d)(\d)0");
                    if (m.Success)
                    {
                        gen = int.Parse(m.Groups[1].Value) * 10;
                        tier = int.Parse(m.Groups[2].Value) * 10 + int.Parse(m.Groups[3].Value);
                        return gen * 100 + tier;
                    }
                }
                if (name.Contains("gtx"))
                {
                    var m = Regex.Match(name, @"gtx\s*(\d+)(\d)0");
                    if (m.Success)
                    {
                        gen = int.Parse(m.Groups[1].Value);
                        tier = int.Parse(m.Groups[2].Value);
                        return gen * 10 + tier;
                    }
                }

                if (name.Contains("rx"))
                {
                    var m = Regex.Match(name, @"rx\s*(\d)(\d)00");
                    if (m.Success)
                    {
                        gen = int.Parse(m.Groups[1].Value) * 10;
                        tier = int.Parse(m.Groups[2].Value);
                        return gen * 100 + tier;
                    }
                }
            }
            else
            {

                var mi = Regex.Match(name, @"i(\d)-(\d+)");
                if (mi.Success)
                {
                    tier = int.Parse(mi.Groups[1].Value);
                    gen = int.Parse(mi.Groups[2].Value);
                    if (gen > 100) gen = gen / 100;
                    return gen * 10 + tier;
                }

                var mr = Regex.Match(name, @"ryzen\s*(\d)\s*(\d)");
                if (mr.Success)
                {
                    tier = int.Parse(mr.Groups[1].Value);
                    gen = int.Parse(mr.Groups[2].Value);
                    return gen * 10 + tier;
                }
            }

            return 0;
        }

        private static string CleanHardwareName(string name)
        {
            string clean = name.ToLowerInvariant();

            clean = Regex.Replace(clean, @"[@™®\(\)]", "");
            clean = Regex.Replace(clean, @"\d+(\.\d+)?\s*ghz", "");
            clean = Regex.Replace(clean, @"\d+\s*(gb|mb)", "");
            clean = Regex.Replace(clean, @"(nvidia|geforce|amd|radeon|intel|arc|graphics|video|card|oc|edition|founders|processor|core|cpu|tm|r|dual-core|quad-core|octa-core|ati|desktop|laptop|mobile|oem|series)", "").Trim();
            clean = Regex.Replace(clean, @"\s+", " ");
            return clean;
        }

        private static void LoadTierList(bool isGpu)
        {
            var map = new Dictionary<string, int>();
            try
            {
                string depsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps");
                string fileName = isGpu ? "gpu.csv" : "cpu.csv";
                string csvPath = Path.Combine(depsPath, fileName);

                if (File.Exists(csvPath))
                {
                    var lines = File.ReadAllLines(csvPath);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var cols = line.Split(',');
                        if (cols.Length < 2) continue;

                        string hardwareName = CleanHardwareName(cols[0]);

                        if (cols.Length > 1 && double.TryParse(cols[1], out double score))
                        {
                            map[hardwareName] = (int)score;
                        }
                    }
                    if (isGpu) _gpuTierMap = map; else _cpuTierMap = map;
                    Logger.Log($"[Hardware] Loaded {map.Count} {(isGpu ? "GPUs" : "CPUs")} from local database.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("TierListLoad", ex);
            }
        }
    }

    public class LocalMachineSpecs
    {
        public string OperatingSystem { get; set; } = "Windows";
        public string Processor { get; set; } = "Unknown Processor";
        public int MemoryGB { get; set; } = 8;
        public string VideoCard { get; set; } = "Unknown Graphics";
        public int DirectXVersion { get; set; } = 11;
    }
}