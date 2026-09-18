using System.IO;
using System.Windows;
using Lume.Core;
using System.Diagnostics;
using System.Windows.Threading;

namespace Lume.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { if (PackageIsolation.RelaunchIfNeeded(args)) return 0; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lume 启动环境修复失败"); return 1; }
        if (args.FirstOrDefault() == "--guard") return DesktopRecovery.Guard(args);
#if VERIFICATION
        if (Environment.GetEnvironmentVariable("LUME_VERIFY_SOFTWARE_RENDERING") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (args.Contains("--performance-self-test")) return MainWindow.RunPerformanceVerification(args);
        if (args.Contains("--menu-self-test")) return DesktopMenuVerification.Run();
        if (args.Contains("--icons-self-test")) return IconVerification.Run();
        if (args.Contains("--features-self-test")) return FeatureVerification.Run(!args.Contains("--no-input"), args.Contains("--no-media"));
#else
        if (args.Any(arg => arg.Contains("self-test", StringComparison.Ordinal) || arg.Contains("smoke", StringComparison.Ordinal))) return 2;
#endif
        if (args.Contains("--stop")) { if (EventWaitHandle.TryOpenExisting("Local\\Lume.Stop", out var stop)) { using (stop) stop.Set(); } return 0; }
        var desktopSmoke = args.Contains("--desktop-smoke");
        var demo = args.Contains("--demo") || args.Contains("--smoke") || desktopSmoke;
        string? requestedAction = null;
        var actionIndex = Array.IndexOf(args, "--action");
        if (actionIndex >= 0)
        {
            if (actionIndex + 1 >= args.Length || !DesktopMenu.IsCommand(args[actionIndex + 1])) return 2;
            requestedAction = args[actionIndex + 1];
            if (DesktopCommandSignals.Send(requestedAction, demo)) return 0;
            if (requestedAction is "exit" or "pause") return 0;
        }
        var data = demo ? Path.Combine(AppContext.BaseDirectory, "demo-data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lume");
        using var mutex = new Mutex(true, demo ? "Local\\Lume.Desktop.Demo" : "Local\\Lume.Desktop", out var first);
        if (!first)
        {
            if (requestedAction != null)
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (DesktopCommandSignals.Send(requestedAction, demo)) return 0;
                    Thread.Sleep(50);
                }
                return 3;
            }
            if (EventWaitHandle.TryOpenExisting("Local\\Lume.Activate", out var signal)) { using (signal) signal.Set(); }
            return 0;
        }
        try
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }.Where(Directory.Exists).ToList();
            if (demo)
            {
                var demoRoot = Path.Combine(data, "示例桌面");
                Directory.CreateDirectory(demoRoot);
                foreach (var name in new[] { "季度规划.md", "品牌设计.txt", "会议记录.txt", "Screenshot_0910.png", "微信图片_0910.jpg", "灵感参考.png", "素材备份.zip", "项目打包.7z", "待处理.data", "新想法.note", "阅读清单.md" })
                    if (!File.Exists(Path.Combine(demoRoot, name))) File.WriteAllText(Path.Combine(demoRoot, name), "Lume 隔离演示文件，仅用于界面和分类验证。");
                Directory.CreateDirectory(Path.Combine(demoRoot, "正在进行的项目"));
                roots = [demoRoot];
            }
            using var diagnostics = new RuntimeDiagnostics(Path.Combine(data, "diagnostics"));
            diagnostics.Record(DiagnosticKind.Startup);
            var store = new StateStore(Path.Combine(data, "state.json"));
            var importIndex = Array.IndexOf(args, "--import-packaged-data");
            if (!demo && importIndex >= 0)
            {
                if (importIndex + 1 >= args.Length || PackageIsolation.IsPackaged) throw new IOException("旧数据迁移参数或进程环境无效。");
                PackageIsolation.ImportData(args[importIndex + 1], data);
            }
            AppState state;
            try { state = store.Load(roots); }
            catch (Exception ex) when (!demo && ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                var backup = store.InspectBackup();
                var message = $"配置读取失败。已找到通过校验的备份：{backup.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}，{backup.Collections} 个分区，{backup.HistoryEntries} 条历史。\n\n恢复后继续启动？原配置会另行保留。";
                if (MessageBox.Show(message, "恢复 Lume 配置", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return 1;
                state = store.RestoreBackup();
            }
            var organizer = new Organizer(store, state);
            var app = new Application();
            Tokens.ApplyTheme(organizer.State.Desktop.Theme);
            Ui.InstallStyles(app);
            app.DispatcherUnhandledException += (_, e) =>
            {
                diagnostics.Record(DiagnosticKind.Unhandled, error: e.Exception);
                MessageBox.Show(e.Exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            var window = new MainWindow(organizer, store, demo, args.Contains("--smoke"), diagnostics);
            window.Archives = new ArchiveService(Path.Combine(data, "archive-journal"));
            if (args.Contains("--smoke")) { app.Run(window); return window.VerificationExitCode; }
            using var surfaceMutex = new Mutex(true, "Local\\Lume.Desktop.Surface", out var surfaceFirst);
            if (!surfaceFirst) throw new InvalidOperationException("已有 Lume 正在管理桌面，请先从托盘退出后再启动另一模式。");
            using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Lume.Activate");
            using var exit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Lume.Stop");
            using var commands = new DesktopCommandSignals(demo);
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown; app.MainWindow = window; window.Resident = true;
            using var surface = new DesktopSurface(organizer, data, window.BuildFileTile, window.RefreshView, window.ShowSettings, id => window.OpenArchive(id));
            window.IsDesktopPaused = () => surface.Paused;
            void Quit() { surface.Dispose(); window.Exiting = true; window.Close(); app.Shutdown(); }
            using var tray = new TrayService(window.ShowSettings, async () => await surface.ToggleAsync(), () => window.OpenArchive(), Quit);
            surface.Error += message => { diagnostics.Record(DiagnosticKind.SurfaceError); tray.Notify(message); }; window.DataChanged += surface.Refresh;
            Microsoft.Win32.PowerModeChangedEventHandler powerChanged = (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) app.Dispatcher.BeginInvoke(() => { diagnostics.Record(DiagnosticKind.Resume); window.RequestEnvironmentalRefresh(); surface.RequestRebuild(); });
            };
            EventHandler displaysChanged = (_, _) => app.Dispatcher.BeginInvoke(() => { diagnostics.Record(DiagnosticKind.DisplayChanged); window.RequestEnvironmentalRefresh(); surface.RequestRebuild(); });
            Microsoft.Win32.SystemEvents.PowerModeChanged += powerChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displaysChanged;
            async Task DispatchAsync(string command)
            {
                switch (command)
                {
                    case "settings": window.ShowPage("设置"); break;
                    case "new-collection": window.CreateCollectionFromMenu(); break;
                    case "refresh": window.RefreshView(); break;
                    case "map-folder": window.MapFolderFromMenu(); break;
                    case "recent": window.RecentFromMenu(); break;
                    case "arrange": window.ArrangeFromMenu(); break;
                    case "backup-layout": window.BackupLayoutFromMenu(); break;
                    case "restore-layout": window.RestoreLayoutFromMenu(); break;
                    case "rules": window.ShowPage("智能规则"); break;
                    case "ai-organize": window.OpenAiFromMenu(); break;
                    case "collapse-all": window.SetAllCollapsedFromMenu(true); break;
                    case "expand-all": window.SetAllCollapsedFromMenu(false); break;
                    case "history": window.ShowPage("整理历史"); break;
                    case "undo": window.UndoFromMenu(); break;
                    case "archive": window.OpenArchive(); break;
                    case "mode-all": window.SetViewFromMenu(0); break;
                    case "mode-work": window.SetViewFromMenu(1); break;
                    case "mode-presentation": window.SetViewFromMenu(2); break;
                    case "show": if (surface.Paused) await surface.ToggleAsync(); break;
                    case "pause": if (!surface.Paused) await surface.ToggleAsync(); break;
                    case "exit": Quit(); break;
                }
            }
            // 命令面板的暂停/启用/退出经由同一条命令通道，避免两套分发逻辑走偏。
            window.GlobalCommand = DispatchAsync;
            var menuSignals = commands.Signals;
            using var signals = new DesktopSignalPump(app.Dispatcher, new WaitHandle[] { exit, activate }.Concat(menuSignals.Select(s => s.Signal)).ToList(), async selected =>
            {
                try
                {
                    if (selected == 0) { Quit(); return; }
                    if (selected == 1) window.ShowSettings();
                    else await DispatchAsync(menuSignals[selected - 2].Command);
                }
                catch (Exception ex) { tray.Notify(ex.Message); }
            });
            var code = 0;
            app.Startup += async (_, _) =>
            {
                try
                {
                    await Task.Run(() => window.Archives.RecoverAsync()); await window.StartMonitoringAsync(); await surface.StartAsync();
                    signals.Start();
                    if (!demo)
                    {
                        try { if (organizer.State.Desktop.ContextMenuEnabled) DesktopMenu.Register(); else DesktopMenu.Unregister(); }
                        catch (Exception ex) { tray.Notify("桌面右键菜单未更新：" + ex.Message); }
                    }
                    if (requestedAction != null) await DispatchAsync(requestedAction);
#if VERIFICATION
                    if (desktopSmoke) { await DesktopVerification.RunAsync(surface, window, data, organizer); Quit(); }
                    else
#endif
                    tray.Notify("桌面分区已启用。双击托盘图标打开设置，右键可归档或退出恢复桌面。");
                }
                catch (Exception ex)
                {
                    signals.Start();
                    code = 1;
                    if (desktopSmoke) { Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "desktop-error.txt"), ex.ToString()); Quit(); }
                    else { tray.Notify(ex.Message); window.ShowSettings(); }
                }
            };
            app.Exit += (_, _) => { Microsoft.Win32.SystemEvents.PowerModeChanged -= powerChanged; Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displaysChanged; surface.Dispose(); signals.Dispose(); };
            app.Run(); return code;
        }
        catch (Exception ex)
        {
            if (args.Contains("--smoke")) { Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "smoke-error.txt"), ex.ToString()); }
            else MessageBox.Show(ex.Message, "Lume 无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
