using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Lume.Desktop;

internal static class ReliabilityVerification
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("comctl32.dll")] private static extern void InitCommonControls();
    internal static int Run()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "reliability-verification", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var root = @"Software\Lume\ReliabilityTests\" + Guid.NewGuid().ToString("N");
        var checks = new List<string>(); IntPtr window = IntPtr.Zero;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        void Result(object result)
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(folder, "result.json"), json);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(folder)!, "latest-result.json"), json);
        }
        try
        {
            Check(RuntimeIdentity.Version.StartsWith(typeof(Program).Assembly.GetName().Version!.ToString(3), StringComparison.Ordinal) && !RuntimeIdentity.Version.Contains('+'), "运行时显示实际程序集版本");
            using (var key = Registry.CurrentUser.CreateSubKey(root))
            {
                const string name = "Lume.DesktopOrganizer";
                var old = StartupRegistration.CommandFor(@"D:\旧版本 中文\Lume.exe"); var current = StartupRegistration.CommandFor(@"D:\新版本 中文\Lume.exe");
                Check(StartupRegistration.Owned(old) && !StartupRegistration.Owned("\"Lume.exe\" --action show") && !StartupRegistration.Owned(StartupRegistration.CommandFor(@"D:\Other.exe")), "识别自身绝对路径启动项并拒绝未知命令");
                Check(!StartupRegistration.MigrateOwned(key, current, folder) && key.GetValue(name) == null, "关闭的启动项不自动启用");
                key.SetValue(name, old); Check(StartupRegistration.MigrateOwned(key, current, folder) && key.GetValue(name) as string == current, "旧版本启动项迁移到新路径");
                var backup = Directory.GetFiles(folder, "startup-before-*.json").Single();
                using (var json = JsonDocument.Parse(File.ReadAllText(backup))) Check(json.RootElement.GetProperty("command").GetString() == old && json.RootElement.GetProperty("replacement").GetString() == current, "启动项原件和替换值持久化备份");
                Check(!StartupRegistration.MigrateOwned(key, current, folder) && Directory.GetFiles(folder, "startup-before-*.json").Length == 1, "启动项迁移重复执行幂等");
                key.SetValue(name, "foreign command"); var rejected = false;
                try { StartupRegistration.MigrateOwned(key, current, folder); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && key.GetValue(name) as string == "foreign command", "未知启动项原样保留");
                key.SetValue(name, old); var blocked = Path.Combine(folder, "blocked"); File.WriteAllText(blocked, "not-a-directory"); rejected = false;
                try { StartupRegistration.MigrateOwned(key, current, blocked); } catch (IOException) { rejected = true; }
                Check(rejected && key.GetValue(name) as string == old, "启动项备份失败不修改注册表");
            }
            InitCommonControls();
            window = CreateWindowExW(0x08000080, "SysListView32", "Lume isolated lease test", 0x80000000, -16000, 0, 50, 50, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Check(window != IntPtr.Zero, "创建独立屏幕外图层测试窗口");
            var valid = new OverlayLease(window.ToInt64(), (uint)Environment.ProcessId, "SysListView32", true);
            Check(DesktopRecovery.MatchesWindow(valid) && !DesktopRecovery.MatchesWindow(valid with { Pid = 1 }) && !DesktopRecovery.MatchesWindow(valid with { ClassName = "TXMiniSkin" }), "图层操作同时校验句柄进程与类名");
            var invalid = Path.Combine(folder, "wrong-pid.json");
            File.WriteAllText(invalid, JsonSerializer.Serialize(new DesktopLease(window.ToInt64(), 1, true)));
            Check(!DesktopRecovery.HideManagedLayers(invalid), "错误进程身份不隐藏窗口");
            DesktopRecovery.Restore(invalid); Check(!DesktopNative.IsWindowVisible(window), "错误身份不恢复窗口可见性");
            var leasePath = Path.Combine(folder, "valid.json"); File.WriteAllText(leasePath, JsonSerializer.Serialize(new DesktopLease(window.ToInt64(), (uint)Environment.ProcessId, true)));
            Check(DesktopRecovery.HideManagedLayers(leasePath), "有效图层租约可缓存并隐藏");
            DesktopRecovery.Restore(leasePath); Check(DesktopNative.IsWindowVisible(window) && !File.Exists(leasePath), "有效图层租约恢复初始可见性并清理");
            var corrupt = Path.Combine(folder, "corrupt.json"); File.WriteAllText(corrupt, "{broken-original");
            Check(!DesktopRecovery.HideManagedLayers(corrupt), "损坏租约不执行隐藏"); DesktopRecovery.Restore(corrupt);
            Check(File.ReadAllText(corrupt) == "{broken-original" && DesktopNative.IsWindowVisible(window), "损坏租约保留原件且不改变窗口");
            Result(new { passed = true, checks }); return 0;
        }
        catch (Exception ex)
        {
            Result(new { passed = false, checks, error = ex.ToString() }); return 1;
        }
        finally { if (window != IntPtr.Zero) DestroyWindow(window); Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }
}
