using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Lume.Desktop;

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Lume.DesktopOrganizer";
    private static string Command => CommandFor(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"));
    internal static string CommandFor(string executable) => $"\"{executable}\" --action show";
    internal static bool Owned(string command) => command.Length > 15 && command.StartsWith('"') && command.EndsWith("\" --action show", StringComparison.Ordinal)
        && Path.IsPathFullyQualified(command[1..^15])
        && Path.GetFileName(command[1..^15]).Equals("Lume.exe", StringComparison.OrdinalIgnoreCase);
    public static string? RegisteredCommand { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) as string; } }
    public static bool Enabled => string.Equals(RegisteredCommand, Command, StringComparison.OrdinalIgnoreCase);
    public static void Set(bool enabled)
    {
        ValidateExecutable();
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        var current = key.GetValue(Name) as string;
        if (current != null && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("启动项指向其他位置，请先迁移到当前版本。");
        if (enabled) key.SetValue(Name, Command); else key.DeleteValue(Name, false);
    }
    private static void ValidateExecutable()
    {
        if (Environment.ProcessPath == null || Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请在发布版本中设置开机启动。");
    }
    public static bool MigrateOwned(string backupDirectory)
    {
        var previous = RegisteredCommand;
        if (previous == null || string.Equals(previous, Command, StringComparison.OrdinalIgnoreCase)) return false;
        ValidateExecutable();
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        return MigrateOwned(key, Command, backupDirectory);
    }
    internal static bool MigrateOwned(RegistryKey key, string command, string backupDirectory)
    {
        var previous = key.GetValue(Name) as string;
        if (previous == null || string.Equals(previous, command, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Owned(previous)) throw new InvalidOperationException("Lume 启动项内容无法识别，已保留，请在设置中核查。");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, "startup-before-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, new { command = previous, replacement = command }); stream.Flush(true); }
        if (key.GetValue(Name) as string != previous) throw new IOException("启动项已发生变化，未覆盖，请重试。");
        key.SetValue(Name, command); return true;
    }
}
