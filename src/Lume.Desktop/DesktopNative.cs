using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Lume.Desktop;

internal static class DesktopNative
{
    internal delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetStyle(IntPtr window, int index, IntPtr style);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    internal static void ShowDesktopKeys() { keybd_event(0x5B, 0, 0, UIntPtr.Zero); keybd_event(0x44, 0, 0, UIntPtr.Zero); keybd_event(0x44, 0, 2, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero); }

    internal static string ClassName(IntPtr window) { var name = new StringBuilder(256); GetClassName(window, name, name.Capacity); return name.ToString(); }
    internal static (IntPtr View, IntPtr Icons) FindDesktop()
    {
        IntPtr view = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (ClassName(h) is "Progman" or "WorkerW")
            {
                var found = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (found != IntPtr.Zero) { view = found; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return (view, view == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(view, IntPtr.Zero, "SysListView32", null));
    }
    internal static void Attach(IntPtr window, IntPtr view)
    {
        var style = GetStyle(window, -16).ToInt64();
        SetStyle(window, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L));
        var ex = GetStyle(window, -20).ToInt64(); SetStyle(window, -20, new IntPtr((ex & ~0x40000L) | 0x80L));
        Marshal.SetLastPInvokeError(0); SetParent(window, view);
        if (GetParent(window) != view) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法附着到 Windows 桌面。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accent { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);
    internal static bool Acrylic(IntPtr window, byte opacity)
    {
        var color = Tokens.Glass;
        var accent = new Accent { State = 4, Flags = 2, Color = ((uint)opacity << 24) | ((uint)color.B << 16) | ((uint)color.G << 8) | color.R };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Accent>());
        try { Marshal.StructureToPtr(accent, pointer, false); var data = new CompositionData { Attribute = 19, Data = pointer, Size = Marshal.SizeOf<Accent>() }; return SetWindowCompositionAttribute(window, ref data) != 0; }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
