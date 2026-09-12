using Microsoft.Win32;

namespace Lume.Desktop;

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Lume.DesktopOrganizer";
    private static string Command => $"\"{Environment.ProcessPath}\" --action show";
    public static bool Enabled { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) as string == Command; } }
    public static void Set(bool enabled)
    {
        if (Environment.ProcessPath == null || string.Equals(System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请在发布版本中设置开机启动。");
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        var current = key.GetValue(Name) as string;
        if (current != null && current != Command) throw new InvalidOperationException("该启动项指向其他位置，请先从原版本关闭开机启动。");
        if (enabled) key.SetValue(Name, Command); else key.DeleteValue(Name, false);
    }
}
