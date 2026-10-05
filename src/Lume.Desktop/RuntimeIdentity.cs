using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace Lume.Desktop;

internal static class RuntimeIdentity
{
    private static string Information => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    internal static string Version => Information.Split('+')[0];
    internal static string Commit => Information.Contains('+') ? Information[(Information.IndexOf('+') + 1)..] : "本地开发构建";
    internal static string BackupDirectory(string data) => Path.Combine(Path.GetDirectoryName(data)!, "Lume-backups");
    internal static void NoticeOtherInstance()
    {
        var path = Environment.ProcessPath;
        foreach (var process in Process.GetProcessesByName("Lume"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var module = process.MainModule; var other = module?.FileName;
                    if (other == null || string.Equals(other, path, StringComparison.OrdinalIgnoreCase)) continue;
                    MessageBox.Show($"另一个目录的 Lume 正在运行：\n{other}\n运行版本：{module?.FileVersionInfo.ProductVersion ?? "未知"}\n\n当前启动的版本：{Version}\n请先从托盘退出原版本并确认桌面恢复，再运行当前版本。数据保存在同一位置，升级前会自动备份。", "Lume 版本切换", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
            }
        }
    }
    internal static void WaitForRestoreParent(string[] args)
    {
        var index = Array.IndexOf(args, "--restore-wait"); if (index < 0) return;
        if (index + 2 >= args.Length || !int.TryParse(args[index + 1], out var id) || !long.TryParse(args[index + 2], out var ticks)) throw new IOException("恢复等待参数无效。");
        try
        {
            using var parent = Process.GetProcessById(id);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks) throw new IOException("恢复等待进程身份不一致。");
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复等待进程不是当前版本的 Lume。");
            if (!parent.WaitForExit(30000)) throw new IOException("Lume 尚未退出，数据未恢复。请退出后重试。");
        }
        catch (ArgumentException) { }
    }
}
