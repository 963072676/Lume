using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;
namespace Lume.Desktop;
public sealed partial class MainWindow
{
    private void Capture(string path)
    {
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private async Task SmokeAsync()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "smoke"); Directory.CreateDirectory(folder);
        try
        {
            await Task.Delay(250);
            Navigate("桌面");
            boardSelection.SelectAll();
            if (boardSelection.Model.Selected.Count != boardSelection.Model.Visible.Count) throw new InvalidOperationException("全选数量与当前视图渲染数量不一致。");
            boardSelection.Clear();
            Capture(Path.Combine(folder, "desktop.png"));
            var checks = new List<string>();
            void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
            var notifications = 0; void Changed() => notifications++;
            DataChanged += Changed;
            Navigate("收件箱"); boardSelection.SelectAll();
            Check(boardSelection.Model.Selected.All(p => organizer.State.Assignments[p] == "inbox"), "收件箱全选只包含收件箱");
            search.Text = "不存在的关键词"; Check(boardSelection.Model.Selected.Count == 0, "筛选自动清除不可见选择");
            Capture(Path.Combine(folder, "empty.png"));
            Navigate("设置"); Render(); Render();
            foreach (var section in new[] { "常规", "外观", "分区", "目录", "AI 与数据" })
            {
                settingsSection = section; RenderSettingsSection(); UpdateLayout();
                Capture(Path.Combine(folder, "settings-" + section + ".png"));
            }
            Check(notifications == 0, "导航和搜索不会重建桌面分区"); DataChanged -= Changed;
            VerifyThemes(folder, Check);
            Width = MinWidth; Height = MinHeight; Navigate("桌面"); Capture(Path.Combine(folder, "compact.png"));
            Width = 1000; Height = 720;
            var fixtureRoot = organizer.State.Configuration.Roots[0];
            var many = Enumerable.Range(0, 5000).Select(i => new DesktopFile(Path.Combine(fixtureRoot, $"批量_{i:D5}.data"), $"批量_{i:D5}.data", ".data", 1, DateTime.UtcNow, DateTime.UtcNow, false, "验证")).ToList();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var loadStore = new StateStore(Path.Combine(folder, "load-state.json"));
            var loadOrganizer = new Organizer(loadStore, AppState.Create([fixtureRoot])); loadOrganizer.ApplyScan(new ScanResult(many, []));
            var loadWindow = new MainWindow(loadOrganizer, loadStore, true, false);
            loadWindow.Render(); loadWindow.Measure(new Size(1000, 720)); loadWindow.Arrange(new Rect(0, 0, 1000, 720));
            Check(loadWindow.boardSelection.Model.Visible.Count == 80, "5000 个文件只创建当前页 80 项");
            loadWindow.boardSelection.SelectAll(); loadWindow.filePage = 1; loadWindow.Render();
            Check(loadWindow.boardSelection.Model.Selected.Count == 0 && loadWindow.boardSelection.Model.Visible.Count == 80, "下一页保留访问能力并清除旧选择");
            checks.Add($"5000 项扫描与两页渲染 {clock.ElapsedMilliseconds} ms");
            loadWindow.Close();
            var sentinel = Path.Combine(organizer.State.Configuration.Roots[0], "监控验收_" + Guid.NewGuid().ToString("N") + ".md");
            await File.WriteAllTextAsync(sentinel, "watcher verification");
            var observed = false;
            for (var i = 0; i < 40; i++) { await Task.Delay(150); if (organizer.Files.Any(f => f.Path == sentinel)) { observed = true; break; } }
            if (!observed) throw new InvalidOperationException("FileSystemWatcher 未在 6 秒内更新视图。");
            if (organizer.State.Assignments[sentinel] != "work") throw new InvalidOperationException("新文件自动归类不正确。");
            organizer.Assign(sentinel, "inbox"); organizer.Undo();
            if (organizer.State.Assignments[sentinel] != "work") throw new InvalidOperationException("手动归类撤销未恢复。");
            File.Delete(sentinel);
            for (var i = 0; i < 40 && organizer.Files.Any(f => f.Path == sentinel); i++) await Task.Delay(150);
            if (organizer.Files.Any(f => f.Path == sentinel)) throw new InvalidOperationException("删除事件未更新视图。");
            Navigate("智能规则"); Capture(Path.Combine(folder, "rules.png"));
            var dialog = new RuleDialog(this, organizer, organizer.State.Configuration.Rules[0]) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, ShowActivated = false };
            dialog.Show(); await Task.Delay(100); dialog.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(folder, "rule-editor.png"))) encoder.Save(stream); dialog.Close();
            settingsSection = "常规"; Navigate("设置"); Capture(Path.Combine(folder, "settings.png")); Navigate("整理历史"); Capture(Path.Combine(folder, "history.png"));
            if (Archives != null)
            {
                var archiveDialog = new ArchiveDialog(this, organizer, Archives, null, Path.GetDirectoryName(store.Path)) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, ShowActivated = false };
                archiveDialog.Show(); await archiveDialog.VerifyAsync(folder); archiveDialog.UpdateLayout();
                var archiveBitmap = new RenderTargetBitmap((int)archiveDialog.ActualWidth, (int)archiveDialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); archiveBitmap.Render(archiveDialog);
                var archiveEncoder = new PngBitmapEncoder(); archiveEncoder.Frames.Add(BitmapFrame.Create(archiveBitmap)); using (var archiveStream = File.Create(Path.Combine(folder, "archive.png"))) archiveEncoder.Save(archiveStream); archiveDialog.Close();
            }
            checks.AddRange(["WPF startup", "real watcher create", "automatic classify", "manual assign undo", "real watcher delete", "responsive UI renders", "archive dialog preview execute restore"]);
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed = true, files = organizer.Files.Count, checks }));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(folder, "error.txt"), ex.ToString()); VerificationExitCode = 1; }
        finally { Close(); }
    }
}
