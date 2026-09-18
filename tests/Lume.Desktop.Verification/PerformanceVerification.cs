using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Lume.Core;

namespace Lume.Desktop;

public sealed partial class MainWindow
{
    internal static int RunPerformanceVerification(string[] args)
    {
        var minutes = 0;
        var index = Array.IndexOf(args, "--soak-minutes");
        if (index >= 0 && (index + 1 >= args.Length || !int.TryParse(args[index + 1], out minutes) || minutes is < 1 or > 1440)) return 2;
        var folder = Path.Combine(AppContext.BaseDirectory, "performance-verification", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var fixture = Path.Combine(folder, "files"); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "first.txt"), "first");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        using var diagnostics = new RuntimeDiagnostics(Path.Combine(folder, "diagnostics"));
        var store = new StateStore(Path.Combine(folder, "state.json")); var organizer = new Organizer(store, AppState.Create([fixture]));
        var window = new MainWindow(organizer, store, true, false, diagnostics)
            { Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        var checks = new List<string>(); var exit = 0; var activeSeconds = 0d; var gaps = 0; var iterations = 0;
        void Result(bool passed, string? error = null) => File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed, checks, error, minutes, activeSeconds, gaps, iterations }, new JsonSerializerOptions { WriteIndented = true }));
        app.Startup += async (_, _) =>
        {
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
                async Task Until(Func<bool> ok, int seconds = 30)
                {
                    var clock = Stopwatch.StartNew(); while (!ok() && clock.Elapsed.TotalSeconds < seconds) await Task.Delay(100);
                    if (!ok()) throw new TimeoutException("界面监控未在预期时间收敛");
                }
                window.Show(); await Until(() => organizer.Files.Count == 1 && !window.refreshing);
                using (var command = new AutoResetEvent(false))
                {
                    var delivered = 0; var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    DesktopSignalPump? pump = null;
                    try
                    {
                        pump = new DesktopSignalPump(window.Dispatcher, [command], _ =>
                        {
                            delivered++;
                            if (delivered == 2) { pump!.Dispose(); complete.TrySetResult(); }
                            return Task.CompletedTask;
                        });
                        command.Set(); await Task.Delay(100); Check(delivered == 0, "命令在初始化完成前保持等待");
                        pump.Start(); await Until(() => delivered == 1); command.Set();
                        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Check(delivered == 2, "事件命令唤醒且命令内部退出不会死锁");
                    }
                    finally { pump?.Dispose(); }
                }
                var notifications = 0; window.DataChanged += () => notifications++;
                var content = window.content.Content;
                await window.RefreshAsync(false);
                Check(notifications == 0 && ReferenceEquals(content, window.content.Content) && window.scanner.LastScannedDirectories == 0, "无变化同步不扫描目录不通知桌面不重建页面");
                await Task.Run(() => { for (var i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(fixture, $"storm-{i:D4}.txt"), "fixture"); });
                await Until(() => organizer.Files.Count == 1001 && !window.refreshing);
                Check(window.boardSelection.Model.Visible.Count <= 80, "真实文件事件风暴收敛且分页可见项不超过80");
                await Task.Delay(700); await Until(() => !window.refreshing);
                var baseline = notifications; var history = organizer.State.History.Count;
                await window.RefreshAsync(false);
                Check(notifications == baseline && organizer.State.History.Count == history, "事件风暴后空闲同步无重复通知或历史");
                var moved = Path.Combine(folder, "files-unavailable");
                if (!Path.GetFullPath(moved).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试目录越界");
                Directory.Move(fixture, moved); await window.RefreshAsync();
                Check(organizer.Files.Count == 0 && organizer.Warnings.Count > 0, "目录失联显示诊断");
                Directory.Move(moved, fixture); window.RequestEnvironmentalRefresh();
                await Until(() => organizer.Files.Count == 1001 && !window.refreshing);
                Check(organizer.Warnings.Count == 0 && window.watchers.Count == 1, "目录恢复后扫描与监控重新建立");
                window.settingsSection = "外观"; window.Navigate("设置");
                content = window.content.Content; baseline = notifications;
                await window.RefreshAsync(false);
                Check(ReferenceEquals(content, window.content.Content) && notifications == baseline, "空闲同步保留设置控件树");
                var started = DateTime.UtcNow; var previousSample = started;
                do
                {
                    await window.RefreshAsync(false); diagnostics.Heartbeat(); iterations++;
                    var now = DateTime.UtcNow; var interval = Math.Max(0, (now - previousSample).TotalSeconds); previousSample = now;
                    // Sleep or a frozen dispatcher cannot count as hours of successful active observation.
                    if (interval > 45) gaps++;
                    activeSeconds += Math.Min(30, interval);
                    var end = now.AddSeconds(Math.Max(0, minutes * 60 - activeSeconds));
                    using var process = Process.GetCurrentProcess();
                    File.WriteAllText(Path.Combine(folder, "progress.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, started, utc = now, end, iterations, activeSeconds, gaps,
                        files = organizer.Files.Count, privateBytes = process.PrivateMemorySize64, handles = process.HandleCount, notifications, state = "running" }));
                    if (activeSeconds >= minutes * 60) break;
                    await Task.Delay(TimeSpan.FromSeconds(30));
                } while (true);
                Check(organizer.Files.Count == 1001 && organizer.Warnings.Count == 0, "持续同步结束时数据一致");
                Result(true);
            }
            catch (Exception ex) { exit = 1; Result(false, ex.ToString()); }
            finally { window.Exiting = true; window.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }
}
