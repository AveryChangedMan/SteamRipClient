using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace SteamRipApp.Core
{
    public class SteamShortcut
    {
        public uint AppID { get; set; }
        public string AppName { get; set; } = "";
        public string Exe { get; set; } = "";
        public string StartDir { get; set; } = "";
        public string Icon { get; set; } = "";
        public string ShortcutPath { get; set; } = "";
        public string LaunchOptions { get; set; } = "";
        public bool IsHidden { get; set; }
        public bool AllowDesktopConfig { get; set; } = true;
        public bool AllowOverlay { get; set; } = true;
        public bool OpenVR { get; set; }
        public bool Devkit { get; set; }
        public string DevkitGameID { get; set; } = "";
        public int LastPlayTime { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }
    public static class VdfUtility
    {
        private const byte TYPE_MAP = 0x00;
        private const byte TYPE_STRING = 0x01;
        private const byte TYPE_INT = 0x02;
        private const byte TYPE_END = 0x08;
        public static List<SteamShortcut> ReadShortcuts(string filePath)
        {
            var shortcuts = new List<SteamShortcut>();
            if (!File.Exists(filePath)) return shortcuts;
            try {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs, Encoding.UTF8);
                if (fs.Length < 10) return shortcuts;
                if (br.ReadByte() != TYPE_MAP) return shortcuts;
                ReadNullTerminatedString(br); 
                while (fs.Position < fs.Length - 1) 
                {
                    byte type = br.ReadByte();
                    if (type == TYPE_END) break; 
                    if (type == TYPE_MAP)
                    {
                        ReadNullTerminatedString(br); 
                        var shortcut = new SteamShortcut();
                        while (true)
                        {
                            byte subType = br.ReadByte();
                            if (subType == TYPE_END) break;
                            string key = ReadNullTerminatedString(br);
                            if (subType == TYPE_STRING)
                            {
                                string val = ReadNullTerminatedString(br);
                                switch (key) {
                                    case "AppName": shortcut.AppName = val; break;
                                    case "exe": shortcut.Exe = val; break;
                                    case "StartDir": shortcut.StartDir = val; break;
                                    case "icon": shortcut.Icon = val; break;
                                    case "ShortcutPath": shortcut.ShortcutPath = val; break;
                                    case "LaunchOptions": shortcut.LaunchOptions = val; break;
                                    case "DevkitGameID": shortcut.DevkitGameID = val; break;
                                }
                            }
                            else if (subType == TYPE_INT)
                            {
                                int val = br.ReadInt32();
                                switch (key) {
                                    case "appid": shortcut.AppID = (uint)val; break;
                                    case "IsHidden": shortcut.IsHidden = val != 0; break;
                                    case "AllowDesktopConfig": shortcut.AllowDesktopConfig = val != 0; break;
                                    case "AllowOverlay": shortcut.AllowOverlay = val != 0; break;
                                    case "OpenVR": shortcut.OpenVR = val != 0; break;
                                    case "Devkit": shortcut.Devkit = val != 0; break;
                                    case "LastPlayTime": shortcut.LastPlayTime = val; break;
                                }
                            }
                            else if (subType == TYPE_MAP && key == "tags")
                            {
                                while (br.ReadByte() != TYPE_END)
                                {
                                    ReadNullTerminatedString(br); 
                                    shortcut.Tags.Add(ReadNullTerminatedString(br));
                                }
                            }
                        }
                        shortcuts.Add(shortcut);
                    }
                }
            } catch (Exception ex) {
                Logger.LogError("ReadVdf", ex);
            }
            return shortcuts;
        }
        public static void WriteShortcuts(string filePath, List<SteamShortcut> shortcuts)
        {
            try {
                using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs, Encoding.UTF8);
                bw.Write(TYPE_MAP);
                WriteNullTerminatedString(bw, "shortcuts");
                for (int i = 0; i < shortcuts.Count; i++)
                {
                    var s = shortcuts[i];
                    bw.Write(TYPE_MAP);
                    WriteNullTerminatedString(bw, i.ToString());
                    WriteIntProperty(bw, "appid", (int)s.AppID);
                    WriteStringProperty(bw, "AppName", s.AppName);
                    WriteStringProperty(bw, "exe", s.Exe);
                    WriteStringProperty(bw, "StartDir", s.StartDir);
                    WriteStringProperty(bw, "icon", s.Icon);
                    WriteStringProperty(bw, "ShortcutPath", s.ShortcutPath);
                    WriteStringProperty(bw, "LaunchOptions", s.LaunchOptions);
                    WriteIntProperty(bw, "IsHidden", s.IsHidden ? 1 : 0);
                    WriteIntProperty(bw, "AllowDesktopConfig", s.AllowDesktopConfig ? 1 : 0);
                    WriteIntProperty(bw, "AllowOverlay", s.AllowOverlay ? 1 : 0);
                    WriteIntProperty(bw, "OpenVR", s.OpenVR ? 1 : 0);
                    WriteIntProperty(bw, "Devkit", s.Devkit ? 1 : 0);
                    WriteStringProperty(bw, "DevkitGameID", s.DevkitGameID);
                    WriteIntProperty(bw, "LastPlayTime", s.LastPlayTime);
                    bw.Write(TYPE_MAP);
                    WriteNullTerminatedString(bw, "tags");
                    for (int j = 0; j < s.Tags.Count; j++)
                    {
                        bw.Write(TYPE_STRING);
                        WriteNullTerminatedString(bw, j.ToString());
                        WriteNullTerminatedString(bw, s.Tags[j]);
                    }
                    bw.Write(TYPE_END); 
                    bw.Write(TYPE_END); 
                }
                bw.Write(TYPE_END); 
                bw.Write(TYPE_END); 
            } catch (Exception ex) {
                Logger.LogError("WriteVdf", ex);
            }
        }
        private static void WriteStringProperty(BinaryWriter bw, string key, string val)
        {
            bw.Write(TYPE_STRING);
            WriteNullTerminatedString(bw, key);
            WriteNullTerminatedString(bw, val);
        }
        private static void WriteIntProperty(BinaryWriter bw, string key, int val)
        {
            bw.Write(TYPE_INT);
            WriteNullTerminatedString(bw, key);
            bw.Write(val);
        }
        private static string ReadNullTerminatedString(BinaryReader br)
        {
            List<byte> bytes = new List<byte>();
            byte b;
            while ((b = br.ReadByte()) != 0x00)
            {
                bytes.Add(b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        private static void WriteNullTerminatedString(BinaryWriter bw, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                bw.Write((byte)0x00);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                bw.Write(bytes);
                bw.Write((byte)0x00);
            }
        }
        public static bool FlipIsHidden(string vdfPath, string targetAppName, bool hidden)
        {
            try {
                byte[] vdfData = File.ReadAllBytes(vdfPath);
                byte[] nameBytes = Encoding.UTF8.GetBytes(targetAppName);
                byte[] hiddenKey = Encoding.UTF8.GetBytes("IsHidden");
                int namePos = FindBytePattern(vdfData, nameBytes);
                if (namePos == -1) return false;
                int hiddenPos = FindBytePattern(vdfData, hiddenKey, namePos);
                if (hiddenPos == -1) return false;
                int valueIndex = hiddenPos + hiddenKey.Length + 1;
                if (valueIndex >= vdfData.Length) return false;
                vdfData[valueIndex] = hidden ? (byte)0x01 : (byte)0x00;
                File.WriteAllBytes(vdfPath, vdfData);
                return true;
            } catch (Exception ex) {
                Logger.LogError("FlipIsHidden", ex);
                return false;
            }
        }
        private static int FindBytePattern(byte[] data, byte[] pattern, int startFrom = 0)
        {
            for (int i = startFrom; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}

