using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Lume.Desktop;

internal static class DesktopMenuVerification
{
    public static int Run()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "menu-verification"); Directory.CreateDirectory(folder);
        var root = @"Software\Lume\MenuTests\" + Guid.NewGuid().ToString("N");
        var checks = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        try
        {
            using (var sandbox = Registry.CurrentUser.CreateSubKey(root))
            {
                const string executable = @"D:\含空格 路径\Lume.exe";
                Check(PackageIsolation.Quote(executable) == "\"" + executable + "\"", "包外启动保留中文和空格");
                Check(PackageIsolation.Quote("a\"b") == "\"a\\\"b\"", "包外启动引号转义");
                Check(PackageIsolation.Quote("D:\\end\\") == "\"D:\\end\\\\\"", "包外启动末尾反斜杠转义");
                var migrationRoot = Path.Combine(folder, "migration-" + Guid.NewGuid().ToString("N"));
                var source = Path.Combine(migrationRoot, "source"); var target = Path.Combine(migrationRoot, "target");
                Directory.CreateDirectory(Path.Combine(source, "archive-journal"));
                var originalState = Lume.Core.AppState.Create([]); originalState.Desktop.Positions["work"] = new(-900, 40, 400, 300);
                new Lume.Core.StateStore(Path.Combine(source, "state.json")).Save(originalState);
                File.WriteAllText(Path.Combine(source, "archive-journal", "retained.json"), "{\"retain\":true}");
                File.WriteAllText(Path.Combine(source, "desktop-lease-obsolete.json"), "old lease");
                PackageIsolation.ImportData(source, target);
                Check(File.ReadAllBytes(Path.Combine(source, "state.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(target, "state.json"))) && File.Exists(Path.Combine(target, "archive-journal", "retained.json")), "隔离配置和归档日志无损复制");
                Check(!File.Exists(Path.Combine(target, "desktop-lease-obsolete.json")) && File.Exists(Path.Combine(source, "desktop-lease-obsolete.json")), "迁移不接管旧租约且保留原数据");
                File.WriteAllText(Path.Combine(source, "state.json"), "changed source"); PackageIsolation.ImportData(source, target);
                Check(new Lume.Core.StateStore(Path.Combine(target, "state.json")).Load([]).Desktop.Positions["work"].X == -900, "重复启动不覆盖已迁移配置");
                Check(DesktopMenu.Items.Length == 7, "7 个一级菜单命令（低频动作已收进命令面板）");
                Check(DesktopMenu.Commands.Length == 21, "21 个命令仍在白名单内，命令面板与旧入口可继续调用");
                foreach (var item in DesktopMenu.Items)
                    Check(DesktopMenu.CommandLine(executable, item.Id) == $"\"{executable}\" --action {item.Id}", "中文和含空格启动路径：" + item.Id);
                foreach (var id in DesktopMenu.Commands)
                    Check(DesktopMenu.CommandLine(executable, id) == $"\"{executable}\" --action {id}", "白名单命令仍可生成启动命令行：" + id);
                using (var foreign = sandbox.CreateSubKey("Other")) foreign.SetValue("MUIVerb", "其他程序");
                var rejected = false;
                try { DesktopMenu.Remove(sandbox, "Other"); } catch (IOException) { rejected = true; }
                Check(rejected, "不会移除其他程序菜单");
                using (var foreign = sandbox.CreateSubKey(DesktopMenu.HandlerPath)) foreign.SetValue("", "foreign");
                rejected = false; try { DesktopMenu.WriteNative(sandbox, executable); } catch (IOException) { rejected = true; }
                Check(rejected, "不会覆盖其他程序同名原生菜单");
                rejected = false; try { DesktopMenu.RemoveNative(sandbox); } catch (IOException) { rejected = true; }
                Check(rejected, "不会注销其他程序同名原生菜单");
                sandbox.DeleteSubKeyTree(DesktopMenu.HandlerPath);
                DesktopMenu.WriteNative(sandbox, @"D:\含空格 路径\Lume.Shell.dll");
                DesktopMenu.WriteNative(sandbox, @"D:\含空格 路径\Lume.Shell.dll");
                using (var handler = sandbox.OpenSubKey(DesktopMenu.HandlerPath))
                using (var server = sandbox.OpenSubKey(DesktopMenu.ClassPath + @"\InprocServer32"))
                    Check(handler?.GetValue("") as string == DesktopMenu.ClassId && server?.GetValue("") as string == @"D:\含空格 路径\Lume.Shell.dll", "原生菜单重复注册及中文路径");
                DesktopMenu.RemoveNative(sandbox);
                using (var handler = sandbox.OpenSubKey(DesktopMenu.HandlerPath))
                using (var cls = sandbox.OpenSubKey(DesktopMenu.ClassPath))
                    Check(handler == null && cls == null, "原生菜单注销清除自身处理器及 COM 注册");
            }
            var native = NativeLibrary.Load(DesktopMenu.ShellPath());
            try { Check(Marshal.GetDelegateForFunctionPointer<VerifyNative>(NativeLibrary.GetExport(native, "LumeVerify"))() == 0, "原生 COM 菜单：标签、命令偏移、普通目录拒绝、参数白名单与卸载"); }
            finally { NativeLibrary.Free(native); }
            Check(!DesktopMenu.IsCommand("powershell") && !DesktopMenu.IsCommand("settings & calc"), "拒绝任意命令和参数注入");
            using (var signals = new DesktopCommandSignals(true))
            {
                foreach (var id in DesktopMenu.Commands)
                {
                    Check(DesktopCommandSignals.Send(id, true) && signals.Take() == id, "驻留进程命令传递：" + id);
                    Check(signals.Take() == null, "命令不重复消费：" + id);
                }
            }
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); return 1; }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int VerifyNative();
}
