using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{
    public static class UnrarEngine
    {
        private const string UnrarDll = "UnRAR64.dll";

        [DllImport(UnrarDll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr RAROpenArchiveEx(ref RAROpenArchiveDataEx archiveData);

        [DllImport(UnrarDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RARCloseArchive(IntPtr hArchive);

        [DllImport(UnrarDll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RARReadHeaderEx(IntPtr hArchive, ref RARHeaderDataEx headerData);

        [DllImport(UnrarDll, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RARProcessFile(IntPtr hArchive, int operation, string? destPath, string? destName);

        [DllImport(UnrarDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RARSetCallback(IntPtr hArchive, UNRARCALLBACK callback, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UNRARCALLBACK(uint msg, IntPtr userData, IntPtr p1, IntPtr p2);

        private const int RAR_OM_EXTRACT = 0;
        private const int RAR_SKIP = 0;
        private const int RAR_TEST = 1;
        private const int RAR_EXTRACT = 2;

        private const uint UCM_PROCESSDATA = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RAROpenArchiveDataEx
        {
            public IntPtr ArcName;
            public IntPtr ArcNameW;
            public uint OpenMode;
            public uint OpenResult;
            public IntPtr CmtBuf;
            public uint CmtBufSize;
            public uint CmtSize;
            public uint CmtState;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public uint[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct RARHeaderDataEx
        {
            public fixed byte ArcName[1024];
            public fixed char ArcNameW[1024];
            public fixed byte FileName[1024];
            public fixed char FileNameW[1024];
            public uint Flags;
            public uint PackSize;
            public uint PackSizeHigh;
            public uint UnpSize;
            public uint UnpSizeHigh;
            public uint HostOS;
            public uint FileCRC;
            public uint FileTime;
            public uint UnpVer;
            public uint Method;
            public uint FileAttr;
            public IntPtr CmtBuf;
            public uint CmtBufSize;
            public uint CmtSize;
            public uint CmtState;
            public fixed uint Reserved[1024];
        }

        private static UNRARCALLBACK? _callback;

        public static async Task<bool> ExtractAsync(
            string archivePath,
            string outputDir,
            Action<double>? onProgress = null,
            Action<string>? onStatus = null,
            CancellationToken ct = default,
            Action<string, long, long>? onFileProgress = null)
        {

            string dllDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps");
            SetDllDirectory(dllDir);

            return await Task.Run(() => ExtractInternal(archivePath, outputDir, onProgress, onStatus, ct, onFileProgress));
        }

        private static unsafe bool ExtractInternal(
            string archivePath,
            string outputDir,
            Action<double>? onProgress,
            Action<string>? onStatus,
            CancellationToken ct,
            Action<string, long, long>? onFileProgress = null)
        {
            var data = new RAROpenArchiveDataEx
            {
                ArcNameW = Marshal.StringToHGlobalUni(archivePath),
                OpenMode = RAR_OM_EXTRACT,
                Reserved = new uint[32]
            };

            IntPtr hArchive = IntPtr.Zero;
            try
            {
                hArchive = RAROpenArchiveEx(ref data);
                if (hArchive == IntPtr.Zero || data.OpenResult != 0)
                {
                    Logger.Log($"[UnrarEngine] Failed to open archive: {archivePath} (Error {data.OpenResult})");
                    return false;
                }

                long totalUnpackedSize = 0;
                long currentUnpackedSize = 0;
                int fileCount = 0;

                onStatus?.Invoke("🔍 Analyzing archive structure...");
                var header = new RARHeaderDataEx();
                while (RARReadHeaderEx(hArchive, ref header) == 0)
                {
                    if (ct.IsCancellationRequested || GlobalSettings.IsShuttingDown) return false;
                    totalUnpackedSize += ((long)header.UnpSizeHigh << 32) | header.UnpSize;
                    fileCount++;
                    RARProcessFile(hArchive, RAR_SKIP, null, null);
                }

                if (totalUnpackedSize == 0 && fileCount == 0)
                {
                    Logger.Log("[UnrarEngine] Archive appears empty or could not be read.");
                    return false;
                }

                RARCloseArchive(hArchive);

                data.OpenResult = 0;
                data.CmtState = 0;

                hArchive = RAROpenArchiveEx(ref data);
                if (hArchive == IntPtr.Zero || data.OpenResult != 0)
                {
                    Logger.Log($"[UnrarEngine] Failed to re-open archive for extraction (Error {data.OpenResult})");
                    return false;
                }

                onStatus?.Invoke($"📦 Extracting {fileCount} files...");

                string _currentFileName = "";
                long _currentFileSize   = 0;
                long _currentFileBytes  = 0;

                _callback = (msg, userData, p1, p2) =>
                {
                    if (ct.IsCancellationRequested || GlobalSettings.IsShuttingDown) return -1;

                    if (msg == UCM_PROCESSDATA)
                    {
                        long chunk = p2.ToInt64();

                        long overall = Interlocked.Add(ref currentUnpackedSize, chunk);

                        _currentFileBytes += chunk;

                        if (totalUnpackedSize > 0)
                        {

                            double blended = (double)overall / totalUnpackedSize * 100.0;
                            onProgress?.Invoke(Math.Min(blended, 99.9));
                        }

                        if (onFileProgress != null && !string.IsNullOrEmpty(_currentFileName))
                            onFileProgress(_currentFileName, _currentFileBytes, _currentFileSize);
                    }
                    return 0;
                };

                RARSetCallback(hArchive, _callback, IntPtr.Zero);

                int extractedCount = 0;
                while (RARReadHeaderEx(hArchive, ref header) == 0)
                {
                    if (ct.IsCancellationRequested || GlobalSettings.IsShuttingDown) return false;

                    _currentFileName  = Marshal.PtrToStringUni((IntPtr)header.FileNameW) ?? "";
                    _currentFileSize  = ((long)header.UnpSizeHigh << 32) | header.UnpSize;
                    _currentFileBytes = 0;

                    string fileName = _currentFileName.Length > 0 ? _currentFileName : "unknown";
                    int result = RARProcessFile(hArchive, RAR_EXTRACT, outputDir, null);
                    if (result != 0)
                    {
                        Logger.Log($"[UnrarEngine] Error extracting {fileName}: {result}");
                        return false;
                    }
                    extractedCount++;
                }

                return extractedCount > 0;
            }
            catch (Exception ex)
            {
                Logger.LogError("UnrarEngine.Extract", ex);
                return false;
            }
            finally
            {
                if (hArchive != IntPtr.Zero) {
                    RARSetCallback(hArchive, null!, IntPtr.Zero);
                    RARCloseArchive(hArchive);
                }
                if (data.ArcNameW != IntPtr.Zero) Marshal.FreeHGlobal(data.ArcNameW);
                if (data.ArcName != IntPtr.Zero) Marshal.FreeHGlobal(data.ArcName);
                _callback = null;
            }
        }

        public class ArchiveEntryInfo
        {
            public string FileName { get; set; } = "";
            public ulong UnpackedSize { get; set; }
            public uint FileCRC { get; set; }
            public uint Method { get; set; }
            public long HeaderOffset { get; set; }
            public long DataOffset { get; set; }
            public long PackedSize { get; set; }
            public bool IsDirectory { get; set; }
        }

        public static async Task<List<ArchiveEntryInfo>> GetArchiveEntriesAsync(string archivePath)
        {
            string dllDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps");
            SetDllDirectory(dllDir);

            return await Task.Run(() =>
            {
                unsafe
                {
                var list = new List<ArchiveEntryInfo>();
                var data = new RAROpenArchiveDataEx
                {
                    ArcNameW = Marshal.StringToHGlobalUni(archivePath),
                    OpenMode = 1,
                    Reserved = new uint[32]
                };

                IntPtr hArchive = RAROpenArchiveEx(ref data);
                if (hArchive == IntPtr.Zero || data.OpenResult != 0)
                {
                    if (data.ArcNameW != IntPtr.Zero) Marshal.FreeHGlobal(data.ArcNameW);
                    return list;
                }

                try
                {
                    var header = new RARHeaderDataEx();
                    while (RARReadHeaderEx(hArchive, ref header) == 0)
                    {
                        bool isDirectory = (header.FileAttr & 0x10) != 0 || (header.UnpSize == 0 && header.UnpSizeHigh == 0 && header.PackSize == 0 && header.PackSizeHigh == 0);

                        list.Add(new ArchiveEntryInfo
                        {
                            FileName = Marshal.PtrToStringUni((IntPtr)header.FileNameW) ?? "",
                            UnpackedSize = ((ulong)header.UnpSizeHigh << 32) | header.UnpSize,
                            FileCRC = header.FileCRC,
                            Method = header.Method,
                            IsDirectory = isDirectory
                        });
                        RARProcessFile(hArchive, RAR_SKIP, null, null);
                    }
                }
                catch { }
                finally
                {
                    RARCloseArchive(hArchive);
                    if (data.ArcNameW != IntPtr.Zero) Marshal.FreeHGlobal(data.ArcNameW);
                }
                    return list;
                }
            });
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}