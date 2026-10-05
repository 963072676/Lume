using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal static class ReferenceRetentionVerification
{
    internal static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "retention-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        app.Startup += async (_, _) =>
        {
            Window? owner = null; ReferenceCleanupWindow? window = null;
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
                var files = Path.Combine(fixture, "files"); Directory.CreateDirectory(files);
                var keep = Path.Combine(files, "保留.txt"); File.WriteAllText(keep, "完整原始内容");
                var hidden = Path.Combine(files, "隐藏.txt"); File.WriteAllText(hidden, "hidden"); File.SetAttributes(hidden, FileAttributes.Hidden);
                var gone = Path.Combine(files, "已删除.txt");
                var probes = WindowsReferenceProbe.Read(files, [Path.GetFileName(keep), Path.GetFileName(hidden), Path.GetFileName(gone)]);
                Check(probes.ScopeIdentity != null && probes.Entries.Length == 3, "本地固定磁盘提供卷目录身份");
                Check(probes.Entries.Count(e => e.Presence == ReferencePresence.Present) == 2 && probes.Entries.Single(e => e.Name == Path.GetFileName(gone)).Presence == ReferencePresence.Missing, "隐藏文件仍判定为存在，只有真正缺失文件参与清理");
                Check(WindowsReferenceProbe.Read(Path.Combine(fixture, "unavailable"), ["missing.txt"]).Entries.Single().Presence == ReferencePresence.Unavailable, "失联目录返回未知而非文件已删除");
                Check(WindowsReferenceProbe.Read(@"\\localhost\not-a-share", ["missing.txt"]).ScopeIdentity == null, "网络路径拒绝作为清理证据");
                Check(WindowsReferenceProbe.Read(files, ["..", "../outside.txt"]).Entries.Length == 0, "路径穿越请求被拒绝");
                Check(WindowsReferenceProbe.Read(files, ["a.txt", "A.TXT"]).Entries.Length == 0, "重复检测文件名被拒绝");
                using (var worker = new ShellWorkerClient())
                {
                    var values = await WindowsReferenceProbe.ReadAsync(worker, [keep, hidden, gone], [files], CancellationToken.None);
                    Check(values.Count == 3 && values.Count(v => v.Presence == ReferencePresence.Present) == 2 && values.Single(v => v.Path == gone).ScopeIdentity == probes.ScopeIdentity, "真实工作进程经有界 IPC 返回完整元数据证据");
                    var unmonitored = await WindowsReferenceProbe.ReadAsync(worker, [gone], [], CancellationToken.None);
                    Check(unmonitored.Single().Presence == ReferencePresence.Unavailable, "未关注目录不会启动补充检测");
                }
                Directory.Move(files, files + ".old"); Directory.CreateDirectory(files);
                Check(WindowsReferenceProbe.Read(files, [Path.GetFileName(gone)]).ScopeIdentity != probes.ScopeIdentity, "同路径目录重建后身份变化，旧等待期失效");
                var newKeep = Path.Combine(files, "保留.txt"); File.WriteAllText(newKeep, "完整原始内容");
                var identity = WindowsReferenceProbe.Read(files, [Path.GetFileName(gone)]).ScopeIdentity!;
                var store = new StateStore(Path.Combine(fixture, "data", "state.json")); var state = AppState.Create([files]);
                state.Assignments[gone] = "work"; state.Configuration.Samples.Add(new(gone, ".txt", "work"));
                state.Assignments[newKeep] = "work"; state.Configuration.Overrides[newKeep] = "work";
                state.MissingReferences[gone] = new(DateTime.UtcNow.AddDays(-31), DateTime.UtcNow.AddDays(-1), identity); store.Save(state);
                var organizer = new Organizer(store, state); var notifications = 0;
                owner = new Window { Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false }; owner.Show();
                window = new(owner, organizer, store, () => notifications++) { Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false }; window.Show();
                var review = Find<Button>(window).Single(b => b.Content as string == "检测并预览");
                var clean = Find<Button>(window).Single(b => b.Content as string == "备份并清理");
                var confirm = Find<CheckBox>(window).Single(); var days = Find<ComboBox>(window).Single(); var list = Find<ListView>(window).Single();
                async Task Review()
                {
                    review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var i = 0; i < 600 && !review.IsEnabled; i++) await Task.Delay(25);
                    Check(review.IsEnabled, "预览检测在期限内结束");
                }
                Check((int)days.SelectedItem == 30 && !clean.IsEnabled, "默认等待30天，尚未检测时不可清理");
                await Review(); Check(list.Items.Count == 1 && !clean.IsEnabled && organizer.State.Assignments.ContainsKey(gone), "真实预览只列缺失项且不删除记录");
                days.SelectedItem = 90; Check(list.Items.Count == 0 && !clean.IsEnabled, "更改等待期使旧预览失效");
                await Review(); Check(list.Items.Count == 0, "90天等待期保护31天缺失记录");
                days.SelectedItem = 30; await Review(); confirm.IsChecked = true; Check(clean.IsEnabled && list.Items.Count == 1, "检查预览并确认后允许清理");
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var image = File.Create(Path.Combine(fixture, "预览.png"))) encoder.Save(image);
                clean.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var i = 0; i < 600 && !review.IsEnabled; i++) await Task.Delay(25);
                Check(review.IsEnabled && !organizer.State.Assignments.ContainsKey(gone) && organizer.State.Configuration.Samples.Count == 0 && notifications == 1, "真实清理按钮生成备份并原子移除过期记录");
                var backup = Directory.GetFiles(RuntimeIdentity.BackupDirectory(Path.GetDirectoryName(store.Path)!), "before-reference-cleanup-*.lume-backup.zip").Single();
                Check(DataBackup.Inspect(backup).Collections == 5 && store.Load([]).Version == 3, "清理前备份可验证，数据升级并保护旧版撤销边界");
                var undo = Find<Button>(window).Single(b => b.Content as string == "撤销本次清理"); Check(undo.IsEnabled, "清理后提供撤销入口");
                undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(organizer.State.Assignments[gone] == "work" && organizer.State.Configuration.Samples.Count == 1 && notifications == 2, "真实撤销按钮恢复归属与学习样本");
                Check(File.ReadAllText(newKeep) == "完整原始内容" && organizer.State.Configuration.Overrides[newKeep] == "work", "原文件内容与手动固定保持完整");
                var result = new { passed = true, fixture, checks, desktopTakeover = false, inputSimulation = false };
                File.WriteAllText(Path.Combine(fixture, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(root, "latest-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { exit = 1; File.WriteAllText(Path.Combine(fixture, "error.txt"), ex.ToString()); }
            finally { window?.Close(); owner?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
