using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
public class SteamHiddenManager
{
    public static void SetGameHiddenStatus(string steamUserId32, string appId64Str, bool isHidden)
    {
        if (!ulong.TryParse(appId64Str, out ulong appId64))
        {
            Console.WriteLine("Invalid AppID format.");
            return;
        }
        string steamPath = GetSteamPath();
        if (string.IsNullOrEmpty(steamPath))
        {
            Console.WriteLine("Could not locate Steam installation path.");
            return;
        }
        string configDir = Path.Combine(steamPath, "userdata", steamUserId32, "config");
        string shortcutsPath = Path.Combine(configDir, "shortcuts.vdf");
        string localConfigPath = Path.Combine(configDir, "localconfig.vdf");
        KillSteamProcesses();
        if (File.Exists(shortcutsPath))
        {
            PatchShortcutsVdf(shortcutsPath, appId64, isHidden);
        }
        if (File.Exists(localConfigPath))
        {
            PatchLocalConfig(localConfigPath, appId64Str, isHidden);
        }
        ClearSteamWebCache();
        Console.WriteLine($"Successfully updated hidden status for AppID {appId64Str} to {isHidden}!");
    }
    private static void PatchShortcutsVdf(string filePath, ulong appId64, bool isHidden)
    {
        byte[] data = File.ReadAllBytes(filePath);
        uint appId32Unsigned = (uint)(appId64 >> 32);
        int appId32 = (int)appId32Unsigned;
        byte[] idBytes = BitConverter.GetBytes(appId32);
        byte[] searchPattern = { 0x02, (byte)'a', (byte)'p', (byte)'p', (byte)'i', (byte)'d', 0x00, idBytes[0], idBytes[1], idBytes[2], idBytes[3] };
        int appIdx = IndexOf(data, searchPattern, 0);
        if (appIdx == -1)
        {
            Console.WriteLine("AppID not found in shortcuts.vdf.");
            return;
        }
        byte[] hiddenPattern = { 0x02, (byte)'I', (byte)'s', (byte)'H', (byte)'i', (byte)'d', (byte)'d', (byte)'e', (byte)'n', 0x00 };
        int hiddenIdx = IndexOf(data, hiddenPattern, appIdx);
        if (hiddenIdx != -1)
        {
            int valueIdx = hiddenIdx + hiddenPattern.Length;
            data[valueIdx] = isHidden ? (byte)1 : (byte)0;
            File.WriteAllBytes(filePath, data);
            Console.WriteLine("Successfully patched shortcuts.vdf binary.");
        }
    }
    private static void PatchLocalConfig(string localConfigPath, string appId64, bool isHidden)
    {
        string content = File.ReadAllText(localConfigPath);
        string hiddenPayload = isHidden ? 
            $@"""{appId64}""
					{{
						""tags""
						{{
							""0""		""hidden""
						}}
						""Hidden""		""1""
						""visibility""		""0""
					}}" :
            $@"""{appId64}""
					{{
						""Hidden""		""0""
						""visibility""		""1""
					}}";
        int index = content.IndexOf($"\"{appId64}\"");
        if (index != -1)
        {
            int openBrace = content.IndexOf('{', index);
            if (openBrace != -1)
            {
                int braceCount = 0;
                int closeBrace = -1;
                for (int i = openBrace; i < content.Length; i++)
                {
                    if (content[i] == '{') braceCount++;
                    else if (content[i] == '}')
                    {
                        braceCount--;
                        if (braceCount == 0)
                        {
                            closeBrace = i;
                            break;
                        }
                    }
                }
                if (closeBrace != -1)
                {
                    content = content.Remove(index, closeBrace - index + 1).Insert(index, hiddenPayload);
                    Console.WriteLine("AppID found in localconfig.vdf. Patched block.");
                }
            }
        }
        else
        {
            string appsPattern = @"(""apps""\s*\{)";
            if (System.Text.RegularExpressions.Regex.IsMatch(content, appsPattern))
            {
                content = System.Text.RegularExpressions.Regex.Replace(content, appsPattern, $"$1\n\t\t\t\t\t{hiddenPayload}", System.Text.RegularExpressions.RegexOptions.Singleline);
                Console.WriteLine("AppID not found in localconfig.vdf. Injected new block.");
            }
        }
        File.WriteAllText(localConfigPath, content);
    }
    private static void KillSteamProcesses()
    {
        Console.WriteLine("Stopping Steam and SteamWebHelper...");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/F /IM steam.exe", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
            Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/F /IM steamwebhelper.exe", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
            Thread.Sleep(3000); 
        }
        catch { }
    }
    private static void ClearSteamWebCache()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string htmlCachePath = Path.Combine(localAppData, "Steam", "htmlcache");
        if (Directory.Exists(htmlCachePath))
        {
            Console.WriteLine($"Clearing Steam Web Cache...");
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Directory.Delete(htmlCachePath, true);
                    Console.WriteLine("Cache successfully completely wiped.");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Attempt {i+1} failed ({ex.Message}). Retrying...");
                    Thread.Sleep(2000);
                }
            }
        }
        else
        {
            Console.WriteLine("Steam htmlcache already clear.");
        }
    }
    private static string GetSteamPath()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key != null)
                {
                    string steamPath = key.GetValue("SteamPath")?.ToString();
                    if (!string.IsNullOrEmpty(steamPath)) return steamPath.Replace('/', '\\');
                }
            }
        }
        catch { }
        if (Directory.Exists(@"C:\Program Files (x86)\Steam")) return @"C:\Program Files (x86)\Steam";
        return null;
    }
    private static int IndexOf(byte[] source, byte[] pattern, int start)
    {
        for (int i = start; i < source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}

