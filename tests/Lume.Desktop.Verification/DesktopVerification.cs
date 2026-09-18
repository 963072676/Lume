using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Lume.Core;
using Forms = System.Windows.Forms;

namespace Lume.Desktop;

internal static class DesktopVerification
{
    public static async Task RunAsync(DesktopSurface surface, MainWindow settings, string data, Organizer organizer)
    {
        await Task.Delay(700);
        var folder = Path.Combine(data, "desktop-verification"); Directory.CreateDirectory(folder);
        if (!surface.Attached || surface.Cards.Count != 5) throw new InvalidOperationException("未创建 5 个可见的桌面子分区。");
        if (surface.Cards.Any(c => (DesktopNative.GetStyle(c.Handle, -16).ToInt64() & 0x40000000L) == 0)) throw new InvalidOperationException("分区不具有 WS_CHILD 样式。");
        if (DesktopNative.IsWindowVisible(DesktopNative.FindDesktop().Icons)) throw new InvalidOperationException("原图标未隐藏。");
        if (settings.IsVisible) throw new InvalidOperationException("设置中心不应在启动时显示。");
        var originalTheme = organizer.State.Desktop.Theme;
        IEnumerable<System.Windows.Controls.Border> Borders(DependencyObject node)
        {
            if (node is System.Windows.Controls.Border border) yield return border;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var child in Borders(System.Windows.Media.VisualTreeHelper.GetChild(node, i))) yield return child;
        }
        try
        {
            foreach (var palette in ThemePalette.All)
            {
                organizer.SetTheme(palette.Id); Tokens.ApplyTheme(palette.Id); surface.Refresh();
                var expected = Tokens.Alpha(Tokens.Glass, organizer.State.Desktop.GlassOpacity).Color;
                var windows = surface.Cards.Cast<Window>().ToList();
                if (surface.SystemEntries != null) windows.Add(surface.SystemEntries);
                foreach (var card in windows)
                {
                    card.UpdateLayout();
                    if (!Borders(card).Any(b => b.Background is System.Windows.Media.SolidColorBrush brush && brush.Color == expected))
                        throw new InvalidOperationException(palette.Name + "未更新桌面分区玻璃底色：" + card.Title);
                }
            }
        }
        finally { organizer.SetTheme(originalTheme); Tokens.ApplyTheme(originalTheme); surface.Refresh(); }
        var toggled = false;
        var foregroundAnchor = new Window { Title = "Lume · 桌面显示验收", Width = 240, Height = 100, ShowInTaskbar = false };
        try
        {
            // 从明确的前台窗口进入 Win+D，避免上次已处于桌面时这次反而恢复所有窗口。
            foregroundAnchor.Show(); foregroundAnchor.Activate(); await Task.Delay(250);
            DesktopNative.ShowDesktopKeys(); toggled = true; await Task.Delay(1500);
            if (!surface.Attached) throw new InvalidOperationException("Win+D 后桌面分区不可见。");
            if (SystemDesktopWindow.EnabledEntries().Count > 0 && (surface.SystemEntries == null || DesktopNative.GetParent(surface.SystemEntries.Handle) != DesktopNative.FindDesktop().View || !DesktopNative.IsWindowVisible(surface.SystemEntries.Handle)))
                throw new InvalidOperationException("Windows 系统入口没有随分区嵌入桌面或 Win+D 后消失。");
            foreach (var card in surface.Cards)
            {
                DesktopNative.GetWindowRect(card.Handle, out var bounds);
                var hit = DesktopNative.WindowFromPoint(new DesktopNative.Point { X = bounds.Left + 80, Y = bounds.Top + 25 });
                DesktopNative.GetWindowThreadProcessId(hit, out var pid);
                if (pid != Environment.ProcessId) throw new InvalidOperationException($"分区被其他桌面层遮挡，不能实际接收鼠标操作。class={DesktopNative.ClassName(hit)}, pid={pid}");
            }
            var first = surface.Cards[0]; DesktopNative.GetWindowRect(first.Handle, out var before);
            var mouseDowns = 0; var mouseMoves = 0; first.PreviewMouseDown += (_, _) => mouseDowns++; first.PreviewMouseMove += (_, _) => mouseMoves++;
            Capture(Path.Combine(folder, "before-drag.png"));
            var previousDesktop = StateStore.Clone(organizer.State.Desktop);
            DesktopNative.GetCursorPos(out var oldCursor);
            try
            {
                if (surface.SystemEntries is { } system)
                {
                    DesktopNative.GetWindowRect(system.Handle, out var original);
                    var home = new CardPlacement(original.Left, original.Top, original.Right - original.Left, 160);
                    organizer.SavePlacement("__windows-system", home with { Y = original.Top > 100 ? original.Top - 40 : original.Top + 40 });
                    surface.Refresh();
                    DesktopNative.GetWindowRect(system.Handle, out var shifted);
                    if (shifted.Top == original.Top) throw new InvalidOperationException("系统入口未应用更新的位置。");
                    var restored = StateStore.Clone(previousDesktop);
                    restored.Positions["__windows-system"] = home;
                    organizer.RestoreDesktop(restored); surface.Refresh();
                    DesktopNative.GetWindowRect(system.Handle, out var restoredBounds);
                    if (restoredBounds.Top != original.Top || restoredBounds.Left != original.Left) throw new InvalidOperationException("还原布局未恢复系统入口位置。");
                }
                async Task Drag(int fromX, int fromY, int toX, int toY)
                {
                    if (!DesktopNative.SetCursorPos(fromX, fromY)) throw new InvalidOperationException("输入桌面不接受鼠标定位。"); await Task.Delay(100); DesktopNative.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                    DesktopNative.SetCursorPos(toX, toY); await Task.Delay(150); DesktopNative.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
                }
                DesktopNative.Rect moved = before;
                for (var attempt = 0; attempt < 3 && moved.Left == before.Left && moved.Top == before.Top; attempt++)
                {
                    await Drag(before.Left + 65, before.Top + 25, before.Left + 95, before.Top + 45);
                    DesktopNative.GetWindowRect(first.Handle, out moved);
                }
                if (moved.Left == before.Left && moved.Top == before.Top) throw new InvalidOperationException($"真实鼠标拖动未移动分区。down={mouseDowns}, moves={mouseMoves}, placement={first.Placement}, before={before.Left},{before.Top}");
                await Drag(moved.Left + 65, moved.Top + 25, before.Left + 65, before.Top + 25);
                DesktopNative.GetWindowRect(first.Handle, out var initial);
                await Drag(initial.Right - 3, initial.Bottom - 3, initial.Right + 47, initial.Bottom + 37);
                DesktopNative.GetWindowRect(first.Handle, out var resized);
                if (resized.Right - resized.Left <= initial.Right - initial.Left || resized.Bottom - resized.Top <= initial.Bottom - initial.Top) throw new InvalidOperationException("真实鼠标缩放未改变分区大小。");
                organizer.SetOptions(first.CollectionId, organizer.Options(first.CollectionId) with { Locked = true }); surface.Refresh();
                await Drag(resized.Left + 65, resized.Top + 25, resized.Left + 115, resized.Top + 65);
                DesktopNative.GetWindowRect(first.Handle, out var locked);
                if (locked.Left != resized.Left || locked.Top != resized.Top) throw new InvalidOperationException("锁定后仍可拖动。");
                organizer.SetOptions(first.CollectionId, organizer.Options(first.CollectionId) with { Locked = false, Collapsed = true }); surface.Refresh();
                DesktopNative.GetWindowRect(first.Handle, out var collapsed);
                if (collapsed.Bottom - collapsed.Top != 58) throw new InvalidOperationException("折叠后没有收起文件区域。");
                organizer.SetAllCollapsed(true); surface.Refresh();
                if (surface.Cards.Any(c => { DesktopNative.GetWindowRect(c.Handle, out var rect); return rect.Bottom - rect.Top != 58; })) throw new InvalidOperationException("全部折叠未作用于所有桌面分区。");
                organizer.SetAllCollapsed(false); surface.Refresh();
                if (surface.Cards.Any(c => { DesktopNative.GetWindowRect(c.Handle, out var rect); return rect.Bottom - rect.Top <= 58; })) throw new InvalidOperationException("全部展开未恢复文件区域。");
                IEnumerable<System.Windows.Controls.Button> Buttons(DependencyObject parent)
                {
                    if (parent is System.Windows.Controls.Button b) yield return b;
                    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                        foreach (var child in Buttons(System.Windows.Media.VisualTreeHelper.GetChild(parent, i))) yield return child;
                }
                var toggle = Buttons(first).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ToggleCollectionCollapsed");
                var point = toggle.PointToScreen(new System.Windows.Point(toggle.ActualWidth / 2, toggle.ActualHeight / 2));
                DesktopNative.SetCursorPos((int)point.X, (int)point.Y); DesktopNative.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80); DesktopNative.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
                if (!organizer.Options(first.CollectionId).Collapsed) throw new InvalidOperationException("标题栏折叠按钮真实鼠标点击未生效。");
                DesktopNative.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80); DesktopNative.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
                if (organizer.Options(first.CollectionId).Collapsed) throw new InvalidOperationException("标题栏展开按钮真实鼠标点击未生效。");
                var lockToggle = Buttons(first).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ToggleCollectionLocked");
                var lockPoint = lockToggle.PointToScreen(new System.Windows.Point(lockToggle.ActualWidth / 2, lockToggle.ActualHeight / 2));
                for (var i = 0; i < 2; i++)
                {
                    DesktopNative.SetCursorPos((int)lockPoint.X, (int)lockPoint.Y); DesktopNative.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80); DesktopNative.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
                    if (organizer.Options(first.CollectionId).Locked != (i == 0)) throw new InvalidOperationException("标题栏锁定按钮点击未切换锁定状态。");
                }
                organizer.SetAllIconSize(26); surface.Refresh();
                if (surface.Cards.Any(c => organizer.Options(c.CollectionId).IconSize != 26)) throw new InvalidOperationException("全局图标大小没有应用。");
                var orderFiles = organizer.CollectionFiles(first.CollectionId).ToArray();
                await first.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (orderFiles.Length >= 2)
                {
                    var source = Buttons(first).Single(b => b.ToolTip is string tip && tip.Contains(orderFiles[1].Path));
                    var target = Buttons(first).Single(b => b.ToolTip is string tip && tip.Contains(orderFiles[0].Path));
                    var startPoint = source.PointToScreen(new System.Windows.Point(source.ActualWidth / 2, source.ActualHeight / 2));
                    var endPoint = target.PointToScreen(new System.Windows.Point(target.ActualWidth / 2, target.ActualHeight / 2));
                    DesktopNative.SetCursorPos((int)startPoint.X, (int)startPoint.Y); await Task.Delay(100); DesktopNative.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                    for (var step = 1; step <= 8; step++) { DesktopNative.SetCursorPos((int)(startPoint.X + (endPoint.X - startPoint.X) * step / 8), (int)(startPoint.Y + (endPoint.Y - startPoint.Y) * step / 8)); await Task.Delay(80); }
                    DesktopNative.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(250);
                    if (organizer.CollectionFiles(first.CollectionId)[0].Path != orderFiles[1].Path) throw new InvalidOperationException("分区内图标真实拖拽排序未生效。");
                }
                organizer.RestoreDesktop(previousDesktop); surface.Refresh();
                DesktopNative.GetWindowRect(first.Handle, out initial);
                DesktopNative.GetWindowRect(surface.Cards[1].Handle, out var peer);
                DesktopNative.SetCursorPos(initial.Left + 65, initial.Top + 25); await Task.Delay(100); DesktopNative.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                DesktopNative.SetCursorPos(initial.Left + 65, peer.Bottom + 28); await Task.Delay(200);
                if (surface.VisibleGuideCount == 0) throw new InvalidOperationException("拖动对齐时未创建可见的桌面对齐线。");
                Capture(Path.Combine(folder, "alignment-guides.png"));
                DesktopNative.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                if (surface.VisibleGuideCount != 0) throw new InvalidOperationException("拖动结束后对齐线未清除。");
                organizer.RestoreDesktop(previousDesktop); surface.Refresh();
            }
            finally { DesktopNative.mouse_event(4, 0, 0, 0, UIntPtr.Zero); DesktopNative.SetCursorPos(oldCursor.X, oldCursor.Y); organizer.RestoreDesktop(previousDesktop); surface.Refresh(); }
            var previewFile = organizer.Files.First(f => f.Extension == ".txt");
            var embeddedPreview = new FilePreviewWindow(previewFile, true);
            try
            {
                embeddedPreview.Show(); await embeddedPreview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); await embeddedPreview.Loading!;
                var h = new WindowInteropHelper(embeddedPreview).Handle;
                DesktopNative.GetWindowRect(h, out var rect);
                if ((DesktopNative.GetStyle(h, -16).ToInt64() & 0x40000000L) == 0 || DesktopNative.GetParent(h) != DesktopNative.FindDesktop().View || !Forms.Screen.PrimaryScreen!.WorkingArea.Contains(rect.Left + 40, rect.Top + 40)) throw new InvalidOperationException("预览未正确嵌入可见桌面。");
                Capture(Path.Combine(folder, "embedded-preview.png"));
            }
            finally { embeddedPreview.Close(); }
            var screen = Forms.Screen.PrimaryScreen!.Bounds;
            using var bitmap = new Bitmap(screen.Width, screen.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size);
            bitmap.Save(Path.Combine(folder, "embedded-desktop.png"), ImageFormat.Png);
            var details = surface.Cards.Select(c => { DesktopNative.GetWindowRect(c.Handle, out var rect); return new { c.CollectionId, hwnd = c.Handle.ToInt64(), parent = DesktopNative.ClassName(DesktopNative.GetParent(c.Handle)), visible = DesktopNative.IsWindowVisible(c.Handle), glassRendered = c.GlassApplied, nativeGlassApiAccepted = c.NativeGlassApplied, rectangle = new { rect.Left, rect.Top, rect.Right, rect.Bottom } }; }).ToList();
            if (surface.Cards.Any(c => !c.GlassApplied)) throw new InvalidOperationException("毛玻璃既未由系统启用，也未成功生成壁纸模糊层。");
            await surface.ToggleAsync();
            if (!DesktopNative.IsWindowVisible(DesktopNative.FindDesktop().Icons)) throw new InvalidOperationException("暂停后未恢复原图标。");
            await surface.ToggleAsync(); await Task.Delay(300);
            if (!surface.Attached) throw new InvalidOperationException("恢复后挂载失败。");
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = new[] { "WS_CHILD desktop ancestry", "Win+D persistence", "mouse hit-test reaches cards", "real header drag", "real corner resize", "locked cards reject dragging", "collapse to title", "visible alignment guides during drag", "guides removed after drag", "file preview embedded in desktop", "wallpaper glass rendered", "settings hidden", "original icons hidden", "pause restores icons", "resume attaches again" }, cards = details }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (toggled) DesktopNative.ShowDesktopKeys(); foregroundAnchor.Close(); }
    }
    private static void Capture(string path)
    {
        var screen = Forms.Screen.PrimaryScreen!.Bounds; using var bitmap = new Bitmap(screen.Width, screen.Height); using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size); bitmap.Save(path, ImageFormat.Png);
    }
}
