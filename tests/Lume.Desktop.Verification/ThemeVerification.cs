using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using Lume.Core;

namespace Lume.Desktop;

public sealed partial class MainWindow
{
    private void VerifyThemes(string folder, Action<bool, string> check)
    {
        settingsSection = "外观"; Navigate("设置"); UpdateLayout();
        var settingsContent = settingsBody.Content;
        var initialTheme = organizer.State.Desktop.Theme;
        var opacity = organizer.State.Desktop.GlassOpacity;
        var accent = Ui.Accent;
        var primary = Ui.Button("主题验证", () => { }, true);
        var tile = Ui.Button("选择验证", () => { }); Ui.UpdateTile(tile, true);
        var surface = new Window { Content = Ui.Row(primary, tile), Left = -16000, Width = 400, Height = 150, ShowActivated = false };
        var refreshCount = 0; void Changed() => refreshCount++;
        DesktopCardWindow? desktopCard = null;
        SystemDesktopWindow? systemCard = null;
        IEnumerable<Border> Borders(DependencyObject node)
        {
            if (node is Border border) yield return border;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var child in Borders(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
        DataChanged += Changed;
        try
        {
            surface.Show(); surface.UpdateLayout();
            // 挂载到本次屏幕外测试窗口，避免占用 Explorer 或隐藏用户桌面。
            var host = new WindowInteropHelper(surface).Handle;
            desktopCard = new DesktopCardWindow(organizer, organizer.State.Configuration.Collections[0], new(20, 20), host,
                (file, _) => Ui.Text(file.Name), () => { }, () => { }, _ => { });
            systemCard = new SystemDesktopWindow(organizer, host, () => { }, []);
            desktopCard.Show(); systemCard.Show();
            foreach (var palette in ThemePalette.All)
            {
                themeChoices[palette.Id].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                surface.UpdateLayout(); UpdateLayout();
                check(ReferenceEquals(settingsContent, settingsBody.Content), palette.Name + "切换保留设置控件树");
                check(ReferenceEquals(accent, Ui.Accent) && ((SolidColorBrush)accent).Color == ThemePalette.ColorOf(palette.Accent), palette.Name + "共享画刷实时更新");
                check(((SolidColorBrush)tile.Background).Color == ThemePalette.ColorOf(palette.Selected), palette.Name + "已有选中控件实时更新");
                var hover = (Border)primary.Template.FindName("hover", primary);
                var border = (Border)primary.Template.FindName("border", primary);
                check(((SolidColorBrush)border.Background).Color == Tokens.Primary600 && ((SolidColorBrush)hover.Background).Color == Tokens.Primary700, palette.Name + "模板普通和悬停背景实时更新");
                check(VisualVerification.Contrast(Colors.White, Tokens.Primary600) >= 4.5 && VisualVerification.Contrast(Colors.White, Tokens.Primary700) >= 4.5, palette.Name + "按钮白字对比度通过");
                check(VisualVerification.Contrast(Tokens.Primary700, Tokens.Primary100) >= 4.5 && VisualVerification.Contrast(Tokens.Ink500, Tokens.Surface50) >= 4.5, palette.Name + "选中和辅助文字对比度通过");
                check(new StateStore(store.Path).Load([]).Desktop.Theme == palette.Id, palette.Name + "已保存且可重新加载");
                check(organizer.State.Desktop.GlassOpacity == opacity, palette.Name + "保留透明度");
                check(themeChoices.Values.Count(Ui.GetSelected) == 1 && Ui.GetSelected(themeChoices[palette.Id]), palette.Name + "色板选择状态唯一");
                desktopCard.ApplyGlass(); systemCard.ApplyGlass();
                var tint = Tokens.Alpha(Tokens.Glass, opacity).Color;
                foreach (var card in new Window[] { desktopCard, systemCard })
                {
                    card.UpdateLayout();
                    check(Borders(card).Any(b => b.Background is SolidColorBrush brush && brush.Color == tint), palette.Name + "实际分区底色更新：" + card.Title);
                }
                Capture(Path.Combine(folder, "theme-" + palette.Id + ".png"));
            }
            check(refreshCount == ThemePalette.All.Count, "每次主题切换同步刷新桌面分区");
            Width = MinWidth; Height = MinHeight; Capture(Path.Combine(folder, "theme-compact.png"));
        }
        finally
        {
            DataChanged -= Changed; desktopCard?.Close(); systemCard?.Close(); surface.Close();
            ApplyTheme(initialTheme); Width = 1000; Height = 720;
        }
    }
}
