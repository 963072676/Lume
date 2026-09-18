using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;

namespace Lume.Desktop;

internal static class VisualVerification
{
    public static async Task RunAsync(List<string> checks, bool interactive = true, Action<string>? progress = null)
    {
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); progress?.Invoke("PASS " + name); }
        progress?.Invoke("measure menu text");
        var two = new SpacedMenuText { Text = "设置" }; var three = new SpacedMenuText { Text = "收件箱" }; var four = new SpacedMenuText { Text = "智能规则" };
        foreach (var label in new[] { two, three, four }) label.Measure(new Size(500, 40));
        Check(two.DesiredSize.Width == three.DesiredSize.Width && three.DesiredSize.Width == four.DesiredSize.Width && two.Distributed, "两三四字菜单真实字符间距分布后宽度一致");
        Check(new SpacedMenuText { Text = "一键 AI 整理" }.Distributed == false, "长菜单与英文不强行拉开字距");
        var button = Ui.Button("菜单对齐", () => { }); var window = new Window { Content = button, Left = -16000, ShowActivated = false, Width = 400, Height = 100 };
        try
        {
            window.Show(); window.UpdateLayout();
            IEnumerable<ContentPresenter> Find(DependencyObject node)
            {
                if (node is ContentPresenter p) yield return p;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var child in Find(VisualTreeHelper.GetChild(node, i))) yield return child;
            }
            Check(Find(button).First().HorizontalAlignment == HorizontalAlignment.Left, "按钮模板实际遵循左对齐而非仅设置无效属性");
        }
        finally { window.Close(); }
        BitmapSource Art(int padding)
        {
            var visual = new DrawingVisual(); using (var draw = visual.RenderOpen()) draw.DrawRectangle(Brushes.Red, null, new Rect(padding, padding, 64 - padding * 2, 64 - padding * 2));
            var image = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
        }
        Check(ShellIcons.SamePixels(IconArtwork.Normalize(Art(0), false), IconArtwork.Normalize(Art(16), false)), "不同透明留白的图标归一化后实际可见尺寸一致");
        var thumbnail = IconArtwork.Normalize(Art(8), true); Check(thumbnail.PixelWidth == 128 && thumbnail.PixelHeight == 128 && thumbnail.IsFrozen, "媒体缩略图使用统一方形外框且可跨线程");
        foreach (var entry in SystemDesktopWindow.Entries)
        {
            progress?.Invoke("read system icon: " + entry.Name);
            Check(SystemDesktopWindow.ReadIcon(entry) is BitmapSource { PixelWidth: 128 }, "读取 Windows 原生图标：" + entry.Name);
        }
        if (interactive) await CheckButtonStatesAsync(Check);
        else checks.Add("已跳过真实鼠标悬停检查：本次使用 --no-input");
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    internal static double Contrast(Color a, Color b)
    {
        double L(Color c)
        {
            double Linear(byte channel) { var v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }

    private static async Task CheckButtonStatesAsync(Action<bool, string> check)
    {
        GetCursorPos(out var cursor);
        var dark = Color.FromRgb(25, 35, 32);
        var panel = new StackPanel { Margin = new(20) };
        var window = new Window { Left = 40, Top = 40, Width = 300, Height = 300, Content = panel, Background = Tokens.Brush(dark), ShowActivated = false, ShowInTaskbar = false, Topmost = true };
        try
        {
            var primary = Ui.Button("保存", () => { }, true); primary.Margin = new(0, 0, 0, 12); panel.Children.Add(primary);
            var light = Ui.IconButton("⋯", () => { }, "更多", true); light.Margin = new(0, 0, 0, 12); panel.Children.Add(light);
            var tile = Ui.Button("已选文件", () => { }); Ui.UseStyle(tile, "FileTileButton"); Ui.UpdateTile(tile, true); panel.Children.Add(tile);
            window.Show(); window.Activate(); await Task.Delay(250); window.UpdateLayout();
            foreach (var (button, label) in new[] { (primary, "主按钮"), (light, "桌面图标按钮"), (tile, "已选磁贴") })
            {
                var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    SetCursorPos((int)point.X - 30, (int)point.Y - 30); await Task.Delay(40);
                    SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(100); window.UpdateLayout();
                    System.Windows.Input.Mouse.Synchronize();
                    if (button.IsMouseOver) break;
                }
                GetCursorPos(out var actual);
                check(button.IsMouseOver, label + $"真实鼠标悬停（目标 {point.X:F0},{point.Y:F0}，实际 {actual.X},{actual.Y}，窗口 {window.Left},{window.Top}）");
                var border = button.Template.FindName("border", button) as Border;
                check(border != null, label + "具有共享按钮模板");
                var hover = (Border)button.Template.FindName("hover", button);
                var rendered = hover.Opacity > 0 ? hover.Background : border!.Background;
                check(rendered is SolidColorBrush, label + "实际模板背景存在");
                var fill = ((SolidColorBrush)rendered).Color;
                Color Composite(Color c) => Color.FromRgb((byte)((c.R * c.A + dark.R * (255 - c.A)) / 255), (byte)((c.G * c.A + dark.G * (255 - c.A)) / 255), (byte)((c.B * c.A + dark.B * (255 - c.A)) / 255));
                var ratio = Contrast(((SolidColorBrush)button.Foreground).Color, Composite(fill));
                check(ratio >= 4.5, $"{label}模板实际悬停着色对比度 {ratio:F2}:1");
                if (ReferenceEquals(button, tile)) check(fill == Tokens.Primary100, "选中填充不会被悬停覆盖");
            }
        }
        finally { window.Close(); SetCursorPos(cursor.X, cursor.Y); }
    }
}
