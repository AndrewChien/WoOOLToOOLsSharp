using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

internal static class WindowsNativeFileDialog
{
    private const int HrCancelled = unchecked((int)0x800704C7);

    public static bool TryShow(
        SimpleFileDialogMode mode,
        string title,
        string? startDirectory,
        string? defaultFilename,
        out SimpleFileDialogResult result,
        out string selectedPath)
    {
        result = SimpleFileDialogResult.None;
        selectedPath = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        SimpleFileDialogMode effectiveMode = mode;
        if (mode == SimpleFileDialogMode.OpenFileOrFolder)
        {
            SimpleFileDialogMode? chosen = PromptFileOrFolder(title);
            if (chosen is null)
            {
                result = SimpleFileDialogResult.Cancel;
                return true;
            }

            effectiveMode = chosen.Value;
        }

        // IFileDialog 需要 STA；调用方通常已在 STA 线程中。
        int hrInit = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.CoinitApartmentThreaded);
        bool comInited = hrInit == 0 || hrInit == 1; // S_OK / S_FALSE

        try
        {
            switch (effectiveMode)
            {
                case SimpleFileDialogMode.OpenFolder:
                    return TryShowOpenDialog(pickFolders: true, title, startDirectory, out result, out selectedPath);

                case SimpleFileDialogMode.OpenFile:
                    return TryShowOpenDialog(pickFolders: false, title, startDirectory, out result, out selectedPath);

                case SimpleFileDialogMode.SaveFile:
                    return TryShowSaveDialog(title, startDirectory, defaultFilename, out result, out selectedPath);

                default:
                    return false;
            }
        }
        catch
        {
            result = SimpleFileDialogResult.None;
            selectedPath = string.Empty;
            return false;
        }
        finally
        {
            if (comInited)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    private static SimpleFileDialogMode? PromptFileOrFolder(string title)
    {
        IntPtr owner = NativeMethods.GetForegroundWindow();

        string caption = string.IsNullOrWhiteSpace(title) ? "选择路径" : title;
        string text = "请选择要选择的类型：\n\n“是” = 选择文件\n“否” = 选择文件夹\n“取消” = 取消";

        int button = NativeMethods.MessageBoxW(
            owner,
            text,
            caption,
            NativeMethods.MbYesNoCancel | NativeMethods.MbIconQuestion | NativeMethods.MbSetForeground);

        return button switch
        {
            NativeMethods.IdYes => SimpleFileDialogMode.OpenFile,
            NativeMethods.IdNo => SimpleFileDialogMode.OpenFolder,
            _ => null,
        };
    }

    private static bool TryShowOpenDialog(
        bool pickFolders,
        string title,
        string? startDirectory,
        out SimpleFileDialogResult result,
        out string selectedPath)
    {
        result = SimpleFileDialogResult.None;
        selectedPath = string.Empty;

        IFileOpenDialog? dialog = null;
        IShellItem? folderItem = null;
        IShellItem? resultItem = null;

        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogRCW();

            int hr = dialog.GetOptions(out uint options);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            FileOpenDialogOptions desired = FileOpenDialogOptions.ForceFileSystem
                                           | FileOpenDialogOptions.NoChangeDir
                                           | FileOpenDialogOptions.DontAddToRecent
                                           | FileOpenDialogOptions.PathMustExist;

            if (pickFolders)
            {
                desired |= FileOpenDialogOptions.PickFolders;
            }
            else
            {
                desired |= FileOpenDialogOptions.FileMustExist;
            }

            hr = dialog.SetOptions(options | (uint)desired);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                hr = dialog.SetTitle(title);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }

            if (TryNormalizeExistingDirectory(startDirectory, out string normalizedStart))
            {
                Guid shellItemGuid = typeof(IShellItem).GUID;
                hr = NativeMethods.SHCreateItemFromParsingName(normalizedStart, IntPtr.Zero, ref shellItemGuid, out folderItem);
                if (hr >= 0 && folderItem is not null)
                {
                    dialog.SetFolder(folderItem);
                }
            }

            IntPtr owner = NativeMethods.GetForegroundWindow();
            hr = dialog.Show(owner);
            if (hr == HrCancelled)
            {
                result = SimpleFileDialogResult.Cancel;
                return true;
            }

            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = dialog.GetResult(out resultItem);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = resultItem.GetDisplayName(SigDn.FileSysPath, out IntPtr pszName);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                selectedPath = Marshal.PtrToStringUni(pszName) ?? string.Empty;
            }
            finally
            {
                if (pszName != IntPtr.Zero)
                {
                    NativeMethods.CoTaskMemFree(pszName);
                }
            }

            result = string.IsNullOrWhiteSpace(selectedPath) ? SimpleFileDialogResult.Cancel : SimpleFileDialogResult.Ok;
            return true;
        }
        finally
        {
            ReleaseComObject(resultItem);
            ReleaseComObject(folderItem);
            ReleaseComObject(dialog);
        }
    }

    private static bool TryShowSaveDialog(
        string title,
        string? startDirectory,
        string? defaultFilename,
        out SimpleFileDialogResult result,
        out string selectedPath)
    {
        result = SimpleFileDialogResult.None;
        selectedPath = string.Empty;

        IFileSaveDialog? dialog = null;
        IShellItem? folderItem = null;
        IShellItem? resultItem = null;

        try
        {
            dialog = (IFileSaveDialog)new FileSaveDialogRCW();

            int hr = dialog.GetOptions(out uint options);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            FileOpenDialogOptions desired = FileOpenDialogOptions.ForceFileSystem
                                           | FileOpenDialogOptions.NoChangeDir
                                           | FileOpenDialogOptions.DontAddToRecent
                                           | FileOpenDialogOptions.PathMustExist
                                           | FileOpenDialogOptions.OverwritePrompt;

            hr = dialog.SetOptions(options | (uint)desired);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                hr = dialog.SetTitle(title);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }

            if (TryNormalizeExistingDirectory(startDirectory, out string normalizedStart))
            {
                Guid shellItemGuid = typeof(IShellItem).GUID;
                hr = NativeMethods.SHCreateItemFromParsingName(normalizedStart, IntPtr.Zero, ref shellItemGuid, out folderItem);
                if (hr >= 0 && folderItem is not null)
                {
                    dialog.SetFolder(folderItem);
                }
            }

            if (!string.IsNullOrWhiteSpace(defaultFilename))
            {
                dialog.SetFileName(defaultFilename);
            }

            IntPtr owner = NativeMethods.GetForegroundWindow();
            hr = dialog.Show(owner);
            if (hr == HrCancelled)
            {
                result = SimpleFileDialogResult.Cancel;
                return true;
            }

            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = dialog.GetResult(out resultItem);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = resultItem.GetDisplayName(SigDn.FileSysPath, out IntPtr pszName);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                selectedPath = Marshal.PtrToStringUni(pszName) ?? string.Empty;
            }
            finally
            {
                if (pszName != IntPtr.Zero)
                {
                    NativeMethods.CoTaskMemFree(pszName);
                }
            }

            result = string.IsNullOrWhiteSpace(selectedPath) ? SimpleFileDialogResult.Cancel : SimpleFileDialogResult.Ok;
            return true;
        }
        finally
        {
            ReleaseComObject(resultItem);
            ReleaseComObject(folderItem);
            ReleaseComObject(dialog);
        }
    }

    private static bool TryNormalizeExistingDirectory(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(full))
            {
                return false;
            }

            normalized = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseComObject(object? obj)
    {
        if (obj is null)
        {
            return;
        }

        try
        {
            // 平台兼容性分析器需要显式的 Windows 守卫。
            if (OperatingSystem.IsWindows())
            {
                Marshal.FinalReleaseComObject(obj);
            }
        }
        catch
        {
            // ignore
        }
    }

    [Flags]
    private enum FileOpenDialogOptions : uint
    {
        OverwritePrompt = 0x00000002,
        NoChangeDir = 0x00000008,
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800,
        FileMustExist = 0x00001000,
        DontAddToRecent = 0x02000000,
    }

    private enum SigDn : uint
    {
        FileSysPath = 0x80058000,
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        [PreserveSig] int SetDefaultFolder(IShellItem psi);
        [PreserveSig] int SetFolder(IShellItem psi);
        [PreserveSig] int GetFolder(out IShellItem ppsi);
        [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetFileName(out IntPtr pszName);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int GetResult(out IShellItem ppsi);
        [PreserveSig] int AddPlace(IShellItem psi, int fdap);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        [PreserveSig] int GetResults(out IntPtr ppenum);
        [PreserveSig] int GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("84bccd23-5fde-4cdb-aea4-af64b83d78ab")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog : IFileDialog
    {
        [PreserveSig] int SetSaveAsItem(IShellItem psi);
        [PreserveSig] int SetProperties(IntPtr pStore);
        [PreserveSig] int SetCollectedProperties(IntPtr pList, bool fAppendDefault);
        [PreserveSig] int GetProperties(out IntPtr ppStore);
        [PreserveSig] int ApplyProperties(IShellItem psi, IntPtr pStore, IntPtr hwnd, IntPtr pSink);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SigDn sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogRCW
    {
    }

    [ComImport]
    [Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileSaveDialogRCW
    {
    }

    private static class NativeMethods
    {
        public const uint CoinitApartmentThreaded = 0x2;

        public const uint MbYesNoCancel = 0x00000003;
        public const uint MbIconQuestion = 0x00000020;
        public const uint MbSetForeground = 0x00010000;

        public const int IdYes = 6;
        public const int IdNo = 7;

        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    }
}
