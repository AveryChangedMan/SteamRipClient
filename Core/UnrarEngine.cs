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
            CancellationToken ct = default)
        {

            string dllDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redist", "Deps");
            SetDllDirectory(dllDir);

            return await Task.Run(() => ExtractInternal(archivePath, outputDir, onProgress, ct));
        }

        private static unsafe bool ExtractInternal(
            string archivePath,
            string outputDir,
            Action<double>? onProgress,
            CancellationToken ct)
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

                var header = new RARHeaderDataEx();
                while (RARReadHeaderEx(hArchive, ref header) == 0)
                {
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
                hArchive = RAROpenArchiveEx(ref data);
                if (hArchive == IntPtr.Zero) return false;

                _callback = (msg, userData, p1, p2) =>
                {
                    if (msg == UCM_PROCESSDATA)
                    {
                        long size = p2.ToInt64();
                        Interlocked.Add(ref currentUnpackedSize, size);
                        if (totalUnpackedSize > 0)
                        {
                            double pct = (double)currentUnpackedSize / totalUnpackedSize * 100.0;
                            onProgress?.Invoke(Math.Min(pct, 100));
                        }
                    }
                    return ct.IsCancellationRequested ? -1 : 1;
                };

                RARSetCallback(hArchive, _callback, IntPtr.Zero);

                int extractedCount = 0;
                while (RARReadHeaderEx(hArchive, ref header) == 0)
                {
                    if (ct.IsCancellationRequested) return false;
                    int result = RARProcessFile(hArchive, RAR_EXTRACT, outputDir, null);
                    if (result != 0)
                    {
                        string fileName = Marshal.PtrToStringUni((IntPtr)header.FileNameW) ?? "unknown";
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
                if (hArchive != IntPtr.Zero) RARCloseArchive(hArchive);
                if (data.ArcNameW != IntPtr.Zero) Marshal.FreeHGlobal(data.ArcNameW);
                if (data.ArcName != IntPtr.Zero) Marshal.FreeHGlobal(data.ArcName);
                _callback = null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}