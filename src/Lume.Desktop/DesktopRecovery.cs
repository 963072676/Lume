using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Lume.Desktop;

internal sealed record OverlayLease(long Handle, uint Pid, string ClassName, bool WasVisible);
internal sealed record DesktopLease(long Icons, uint ExplorerPid, bool WasVisible, List<OverlayLease>? Overlays = null);

internal static class DesktopRecovery
{
    public static Process Launch(params string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("无法启动桌面恢复保护进程。");
    }
    public static void SaveLease(string path, IntPtr icons)
    {
        DesktopNative.GetWindowThreadProcessId(icons, out var pid);
        var overlays = new List<OverlayLease>();
        var view = DesktopNative.FindDesktop().View;
        DesktopNative.EnumChildWindows(view, (h, _) =>
        {
            if (DesktopNative.ClassName(h) == "TXMiniSkin")
            {
                DesktopNative.GetWindowThreadProcessId(h, out var overlayPid);
                overlays.Add(new(h.ToInt64(), overlayPid, "TXMiniSkin", DesktopNative.IsWindowVisible(h)));
            }
            return true;
        }, IntPtr.Zero);
        var lease = new DesktopLease(icons.ToInt64(), pid, DesktopNative.IsWindowVisible(icons), overlays);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(lease)); File.Move(path + ".tmp", path, true);
    }
    public static void Restore(string path)
    {
        if (!File.Exists(path)) return;
        var lease = JsonSerializer.Deserialize<DesktopLease>(File.ReadAllText(path));
        if (lease != null)
        {
            var icons = new IntPtr(lease.Icons); DesktopNative.GetWindowThreadProcessId(icons, out var pid);
            if (pid == lease.ExplorerPid && DesktopNative.ClassName(icons) == "SysListView32") DesktopNative.ShowWindow(icons, lease.WasVisible ? 5 : 0);
            foreach (var overlay in lease.Overlays ?? [])
            {
                var handle = new IntPtr(overlay.Handle); DesktopNative.GetWindowThreadProcessId(handle, out var owner);
                if (owner == overlay.Pid && DesktopNative.ClassName(handle) == overlay.ClassName) DesktopNative.ShowWindow(handle, overlay.WasVisible ? 5 : 0);
            }
        }
        File.Delete(path);
    }
    public static void HideManagedLayers(string path)
    {
        var lease = JsonSerializer.Deserialize<DesktopLease>(File.ReadAllText(path))!;
        DesktopNative.ShowWindow(new IntPtr(lease.Icons), 0);
        foreach (var overlay in lease.Overlays ?? []) DesktopNative.ShowWindow(new IntPtr(overlay.Handle), 0);
    }
    public static int Guard(string[] args)
    {
        var path = args[3];
        try
        {
            var process = Process.GetProcessById(int.Parse(args[1]));
            if (process.StartTime.ToUniversalTime().Ticks != long.Parse(args[2])) return 1;
            File.WriteAllText(path + ".ready", "ready");
            process.WaitForExit(); Restore(path); return 0;
        }
        catch (ArgumentException) { Restore(path); return 0; }
        finally { if (File.Exists(path + ".ready")) File.Delete(path + ".ready"); }
    }
}
