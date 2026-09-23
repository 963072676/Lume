using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace Lume.Desktop;

internal readonly record struct ShellIconChange(uint EventId, string? Path, string? NewPath = null);

/// <summary>Receives Shell namespace changes while the resident main window is hidden.</summary>
internal sealed class ShellIconChanges : IDisposable
{
    internal const uint AssociationChanged = 0x08000000;
    internal const uint ImageChanged = 0x00008000;
    private const uint EventsWithoutPidls = AssociationChanged | ImageChanged | 0x04000000; // extended event
    private const int Message = 0x8000 + 0x31A;
    private const int Sources = 0x0001 | 0x0002 | 0x8000; // interrupt, Shell, shared-memory delivery
    // Item mutations, icon-list changes and file associations can affect a visible icon.
    // Exclude unrelated free-space, media and network notifications from the resident listener.
    private const int IconEvents = 0x0802B81F;
    private HwndSource? source;
    private uint registration;
    private bool disposed;
    internal bool Registered => registration != 0;
    internal static bool HasPidlPayload(uint eventId) => (eventId & EventsWithoutPidls) == 0;
    internal static event Action<ShellIconChange>? Changed;

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyEntry { public IntPtr Pidl; public int Recursive; }

    [DllImport("shell32.dll")] private static extern int SHGetSpecialFolderLocation(IntPtr owner, int folder, out IntPtr pidl);
    [DllImport("shell32.dll")] private static extern uint SHChangeNotifyRegister(IntPtr window, int sources, int events, uint message, int count, ref NotifyEntry entry);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotifyDeregister(uint id);
    [DllImport("shell32.dll")] private static extern IntPtr SHChangeNotification_Lock(IntPtr change, uint processId, out IntPtr pidls, out int eventId);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotification_Unlock(IntPtr locked);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

    internal ShellIconChanges()
    {
        // The resident management window stays hidden until the user opens it.
        // A message-only HWND receives changes for the entire resident lifetime.
        var parameters = new HwndSourceParameters("Lume Shell icon changes")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0x40000000, // WS_CHILD
            Width = 0,
            Height = 0
        };
        source = new HwndSource(parameters);
        source.AddHook(WndProc);
        Attach(source.Handle);
    }
    private void Attach(IntPtr handle)
    {
        if (disposed || handle == IntPtr.Zero) return;
        var pidl = IntPtr.Zero;
        try
        {
            if (SHGetSpecialFolderLocation(IntPtr.Zero, 0, out pidl) < 0 || pidl == IntPtr.Zero) return;
            var entry = new NotifyEntry { Pidl = pidl, Recursive = 1 };
            registration = SHChangeNotifyRegister(handle, Sources, IconEvents, Message, 1, ref entry);
        }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != Message) return IntPtr.Zero;
        handled = true;
        var locked = SHChangeNotification_Lock(wParam, unchecked((uint)lParam.ToInt64()), out var pidls, out var eventId);
        if (locked == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            string? path = null; string? newPath = null;
            // UPDATEIMAGE carries DWORDs, not PIDLs. Never pass those values to SHGetPathFromIDListW.
            if (HasPidlPayload(unchecked((uint)eventId)) && pidls != IntPtr.Zero)
            {
                var pidl = Marshal.ReadIntPtr(pidls);
                if (pidl != IntPtr.Zero)
                {
                    var buffer = new StringBuilder(32768);
                    if (SHGetPathFromIDListW(pidl, buffer)) path = buffer.ToString();
                }
                var second = Marshal.ReadIntPtr(pidls, IntPtr.Size);
                if (second != IntPtr.Zero)
                {
                    var buffer = new StringBuilder(32768);
                    if (SHGetPathFromIDListW(second, buffer)) newPath = buffer.ToString();
                }
            }
            var change = new ShellIconChange(unchecked((uint)eventId), path, newPath);
            ShellIcons.OnShellChange(change);
            Changed?.Invoke(change);
        }
        finally { SHChangeNotification_Unlock(locked); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registration != 0) { SHChangeNotifyDeregister(registration); registration = 0; }
        source?.RemoveHook(WndProc);
        source?.Dispose();
        source = null;
    }
}
