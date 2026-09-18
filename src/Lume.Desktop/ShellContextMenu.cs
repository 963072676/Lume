using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Lume.Core;

namespace Lume.Desktop;

internal static class ShellContextMenu
{
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x5000;
    private const uint CmdPreview = 0x7001;
    private const uint CmdRelease = 0x7002;
    private const uint CmdCopyPath = 0x7003;
    private const uint CmdOpenFolder = 0x7004;
    private const uint CmdAssignBase = 0x7100;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, [In] ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214e4-0000-0000-c000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig]
        int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, [MarshalAs(UnmanagedType.LPArray)] byte[] commandstring, uint cch);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214f4-0000-0000-c000-000000000046")]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig]
        new int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig]
        new int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig]
        new int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, [MarshalAs(UnmanagedType.LPArray)] byte[] commandstring, uint cch);
        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719")]
    private interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig]
        new int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig]
        new int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig]
        new int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, [MarshalAs(UnmanagedType.LPArray)] byte[] commandstring, uint cch);
        [PreserveSig]
        new int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public DesktopNative.Point ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHBindToParent(IntPtr pidl, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    public static bool Show(
        IntPtr hwndOwner,
        IReadOnlyList<string> paths,
        DesktopNative.Point screenPos,
        Action<string>? onAssignToCollection = null,
        Action? onPreview = null,
        Action? onReleaseOverride = null,
        IReadOnlyList<Collection>? availableCollections = null,
        bool hasOverride = false)
    {
        if (paths == null || paths.Count == 0) return false;
        var pidls = new List<IntPtr>();
        var childPidls = new List<IntPtr>();
        IShellFolder? parentFolder = null;

        try
        {
            var guidFolder = typeof(IShellFolder).GUID;
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var fullPidl, 0, out _) == 0 && fullPidl != IntPtr.Zero)
                {
                    pidls.Add(fullPidl);
                    if (parentFolder == null)
                    {
                        if (SHBindToParent(fullPidl, ref guidFolder, out var parentObj, out var childPidl) == 0 && parentObj is IShellFolder sf)
                        {
                            parentFolder = sf;
                            childPidls.Add(childPidl);
                        }
                    }
                    else
                    {
                        if (SHBindToParent(fullPidl, ref guidFolder, out _, out var childPidl) == 0)
                            childPidls.Add(childPidl);
                    }
                }
            }

            if (parentFolder == null || childPidls.Count == 0) return false;

            var guidMenu = typeof(IContextMenu).GUID;
            if (parentFolder.GetUIObjectOf(hwndOwner, (uint)childPidls.Count, childPidls.ToArray(), ref guidMenu, IntPtr.Zero, out var menuObj) != 0 || menuObj is not IContextMenu contextMenu)
                return false;

            IntPtr hMenu = CreatePopupMenu();
            try
            {
                uint flags = CMF_NORMAL;
                if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                    flags |= CMF_EXTENDEDVERBS;

                contextMenu.QueryContextMenu(hMenu, 0, CmdFirst, CmdLast, flags);

                if (onPreview != null || onAssignToCollection != null || onReleaseOverride != null)
                {
                    IntPtr hLumeSub = CreatePopupMenu();
                    if (onPreview != null)
                        AppendMenu(hLumeSub, 0, (IntPtr)CmdPreview, "预览文件 (空格)");

                    if (availableCollections != null && availableCollections.Count > 0 && onAssignToCollection != null)
                    {
                        IntPtr hAssignSub = CreatePopupMenu();
                        for (int i = 0; i < availableCollections.Count; i++)
                        {
                            AppendMenu(hAssignSub, 0, (IntPtr)(CmdAssignBase + (uint)i), availableCollections[i].Name);
                        }
                        string assignTitle = paths.Count > 1 ? $"将 {paths.Count} 项归入分区" : "归入分区";
                        AppendMenu(hLumeSub, MF_POPUP, hAssignSub, assignTitle);
                    }

                    if (hasOverride && onReleaseOverride != null)
                        AppendMenu(hLumeSub, 0, (IntPtr)CmdRelease, "恢复自动归类");

                    AppendMenu(hLumeSub, MF_SEPARATOR, IntPtr.Zero, null);
                    AppendMenu(hLumeSub, 0, (IntPtr)CmdCopyPath, "复制完整路径");
                    AppendMenu(hLumeSub, 0, (IntPtr)CmdOpenFolder, "在资源管理器中显示");

                    InsertMenu(hMenu, 0, MF_POPUP, hLumeSub, "Lume 分区整理");
                    InsertMenu(hMenu, 1, MF_SEPARATOR, IntPtr.Zero, null);
                }

                HwndSource? source = hwndOwner != IntPtr.Zero ? HwndSource.FromHwnd(hwndOwner) : null;
                var cm2 = contextMenu as IContextMenu2;
                var cm3 = contextMenu as IContextMenu3;
                HwndSourceHook? hook = null;
                if (cm2 != null || cm3 != null)
                {
                    hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        if (msg is 0x0117 /* WM_INITMENUPOPUP */ or 0x002B /* WM_DRAWITEM */ or 0x002C /* WM_MEASUREITEM */)
                        {
                            if (cm2 != null)
                            {
                                cm2.HandleMenuMsg((uint)msg, wParam, lParam);
                                handled = true;
                                return IntPtr.Zero;
                            }
                        }
                        else if (msg == 0x0120 /* WM_MENUCHAR */)
                        {
                            if (cm3 != null)
                            {
                                cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr lResult);
                                handled = true;
                                return lResult;
                            }
                        }
                        return IntPtr.Zero;
                    };
                    source?.AddHook(hook);
                }

                if (hwndOwner != IntPtr.Zero) DesktopNative.SetForegroundWindow(hwndOwner);

                uint selected = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenPos.X, screenPos.Y, hwndOwner, IntPtr.Zero);

                if (hook != null) source?.RemoveHook(hook);

                if (selected == 0) return true;

                if (selected == CmdPreview)
                {
                    onPreview?.Invoke();
                }
                else if (selected == CmdRelease)
                {
                    onReleaseOverride?.Invoke();
                }
                else if (selected == CmdCopyPath)
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, paths));
                }
                else if (selected == CmdOpenFolder)
                {
                    if (paths.Count > 0)
                        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + paths[0] + "\"") { UseShellExecute = true });
                }
                else if (selected >= CmdAssignBase && availableCollections != null && selected < CmdAssignBase + (uint)availableCollections.Count)
                {
                    int index = (int)(selected - CmdAssignBase);
                    onAssignToCollection?.Invoke(availableCollections[index].Id);
                }
                else if (selected >= CmdFirst && selected <= CmdLast)
                {
                    var ci = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                        fMask = 0x00004000 /* CMIC_MASK_UNICODE */,
                        hwnd = hwndOwner,
                        lpVerb = (IntPtr)(selected - CmdFirst),
                        lpVerbW = (IntPtr)(selected - CmdFirst),
                        nShow = 1 /* SW_SHOWNORMAL */,
                        ptInvoke = screenPos
                    };
                    contextMenu.InvokeCommand(ref ci);
                }
                return true;
            }
            finally
            {
                DestroyMenu(hMenu);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }
}
