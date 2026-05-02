using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace SteamRipApp.Core
{
    public static class PickerService
    {

        [System.Diagnostics.DebuggerHidden]
        public static async Task<string?> PickFolderAsync(string? initialFolder = null)
        {
            if (!string.IsNullOrEmpty(initialFolder) && File.Exists(initialFolder))
                initialFolder = Path.GetDirectoryName(initialFolder);
            return await App.RunModalSafeAsync(async () => {

                if (IsRunningAsAdmin() || !string.IsNullOrEmpty(initialFolder))
                {
                    Logger.Log("[Picker] Using Win32 picker for better directory control or Admin compatibility.");
                    return await PickFolderWin32Async(initialFolder);
                }

                try {
                    var folderPicker = new FolderPicker();
                    var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
                    InitializeWithWindow.Initialize(folderPicker, hwnd);

                    folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                    folderPicker.FileTypeFilter.Add("*");

                    var folder = await folderPicker.PickSingleFolderAsync();
                    if (folder != null) return folder.Path;
                }
                catch (System.Runtime.InteropServices.COMException ex) {
                    Logger.Log($"[Picker] WinRT FolderPicker failed: {ex.Message}. Using Win32 fallback.");
                    return await PickFolderWin32Async(initialFolder);
                }
                catch (Exception) {
                    Logger.Log($"[Picker] FolderPicker error. Using Win32 fallback.");
                    return await PickFolderWin32Async(initialFolder);
                }
                return null;
            });
        }

        [System.Diagnostics.DebuggerHidden]
        public static async Task<string?> PickFileAsync(string? initialFolder = null, string filter = "*.exe;*.bat;*.cmd")
        {
            if (!string.IsNullOrEmpty(initialFolder) && File.Exists(initialFolder))
                initialFolder = Path.GetDirectoryName(initialFolder);
            return await App.RunModalSafeAsync(async () => {
                if (IsRunningAsAdmin() || !string.IsNullOrEmpty(initialFolder))
                {
                    Logger.Log("[Picker] Using Win32 picker for better directory control or Admin compatibility.");
                    return await PickFileWin32Async(initialFolder);
                }

                try {
                    var filePicker = new FileOpenPicker();
                    var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
                    InitializeWithWindow.Initialize(filePicker, hwnd);

                    filePicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                    filePicker.FileTypeFilter.Add(".exe");
                    filePicker.FileTypeFilter.Add(".bat");
                    filePicker.FileTypeFilter.Add(".cmd");

                    var file = await filePicker.PickSingleFileAsync();
                    if (file != null) return file.Path;
                }
                catch (System.Runtime.InteropServices.COMException) {
                    return await PickFileWin32Async(initialFolder);
                }
                catch {
                    return await PickFileWin32Async(initialFolder);
                }
                return null;
            });
        }

        private static bool IsRunningAsAdmin()
        {
            try {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            } catch { return false; }
        }

        #region Win32 Fallbacks (For Administrator Mode)

        private static Task<string?> PickFolderWin32Async(string? initialFolder = null)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
                dialog.SetOptions(0x00000020 | 0x00000040);
                dialog.SetTitle("Select a folder");

                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref Guid_IShellItem, out var item) == 0)
                    {
                        dialog.SetFolder(item);
                    }
                }

                if (dialog.Show(hwnd) == 0) {
                    dialog.GetResult(out var item);
                    item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                    return Task.FromResult<string?>(path);
                }
            } finally {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
            return Task.FromResult<string?>(null);
        }

        private static Task<string?> PickFileWin32Async(string? initialFolder = null)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
                dialog.SetOptions(0x00000040);
                dialog.SetTitle("Select a file");

                if (!string.IsNullOrEmpty(initialFolder))
                {
                    string dir = Directory.Exists(initialFolder) ? initialFolder : Path.GetDirectoryName(initialFolder) ?? "";
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        if (SHCreateItemFromParsingName(dir, IntPtr.Zero, ref Guid_IShellItem, out var item) == 0)
                        {
                            dialog.SetFolder(item);
                        }
                    }
                }

                if (dialog.Show(hwnd) == 0) {
                    dialog.GetResult(out var item);
                    item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                    return Task.FromResult<string?>(path);
                }
            } finally {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
            return Task.FromResult<string?>(null);
        }

        #endregion

        #region COM Interfaces
        [ComImport]
        [Guid("DC1C5A9C-2D8A-4DDE-A5F1-606C750A0710")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(); void SetFileTypeIndex(); void GetFileTypeIndex(); void Advise(); void Unadvise();
            void SetOptions(uint options);
            void GetOptions(); void SetDefaultFolder(); void SetFolder(IShellItem item); void GetFolder(); void GetCurrentSelection();
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel(); void SetFileNameLabel();
            void GetResult(out IShellItem item);
            void AddPlace(); void SetDefaultExtension(); void Close(); void SetClientGuid(); void ClearClientData();
            void SetFilter(); void GetResults(); void GetSelectedItems();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName([In, MarshalAs(UnmanagedType.LPWStr)] string pszPath, [In] IntPtr pbc, [In] ref Guid riid, [Out, MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        private static Guid Guid_IShellItem = new Guid("43826492-430A-42C4-BD13-D609E271201D");

        [ComImport]
        [Guid("43826492-430A-42C4-BD13-D609E271201D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(); void GetParent();
            void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(); void Compare();
        }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        private class FileOpenDialog { }

        private enum SIGDN : uint { SIGDN_FILESYSPATH = 0x80058000 }
        #endregion
    }
}