using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lume.Core;
using Drawing = System.Drawing;

namespace Lume.Desktop;

internal static class IconVerification
{
    public static int Run()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "icons-verification"); Directory.CreateDirectory(folder);
        var fixture = Path.Combine(folder, "fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                var first = Path.Combine(fixture, "one"); var second = Path.Combine(fixture, "two"); Directory.CreateDirectory(first); Directory.CreateDirectory(second);
                var redIcon = Path.Combine(fixture, "red.ico"); var blueIcon = Path.Combine(fixture, "blue.ico");
                SaveIcon(redIcon, Drawing.Color.Crimson); SaveIcon(blueIcon, Drawing.Color.RoyalBlue);
                var red = Path.Combine(first, "同名 应用.lnk"); var blue = Path.Combine(second, "同名 应用.lnk");
                Shortcut(red, redIcon); Shortcut(blue, blueIcon);
                var files = DesktopScanner.Scan([first, second]).Files;
                var a = files.Single(f => f.Path == red); var b = files.Single(f => f.Path == blue);
                Check(a.Target?.IconLocation.Contains(redIcon, StringComparison.OrdinalIgnoreCase) == true,
                    "快捷方式读取自定义图标资源位置");
                var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(red)));
                var originalBlue = File.ReadAllBytes(blue);
                Check(FilePresentation.DisplayName(a) == "同名 应用", "快捷方式显示名称隐藏 .lnk");
                Check(FilePresentation.DisplayName(a with { Name = "应用.LNK", Extension = ".LNK" }) == "应用", "大写快捷方式后缀");
                Check(FilePresentation.DisplayName(a with { Name = "网页.url", Extension = ".url" }) == "网页", "网页快捷方式显示名称");
                Check(FilePresentation.DisplayName(a with { Name = "资料.docx", Extension = ".docx" }) == "资料.docx", "普通文件保留扩展名");
                Check(FilePresentation.DisplayName(a with { Name = "文件夹.lnk", IsDirectory = true }) == "文件夹.lnk", "文件夹名称不误截断");
                Check(FilePresentation.DisplayName(a with { Name = ".lnk" }) == ".lnk", "名称不会截成空字符串");
                var task = ShellIcons.GetAsync(a);
                Check(ReferenceEquals(task, ShellIcons.GetAsync(a)), "相同快捷方式并发读取合并");
                var icons = await Task.WhenAll(task, ShellIcons.GetAsync(b)).WaitAsync(TimeSpan.FromSeconds(15));
                Check(icons.All(i => i is BitmapSource), "两个快捷方式读出实际图标");
                Check(!ShellIcons.SamePixels((BitmapSource)icons[0], (BitmapSource)icons[1]), "同名同后缀快捷方式图标不会串用");
                Check(icons.All(i => i.IsFrozen), "图标可安全跨线程使用");
                var iconResourceCache = ShellIcons.GetAsync(a);
                ShellIcons.OnShellChange(new ShellIconChange(0x00002000, redIcon));
                Check(!ReferenceEquals(iconResourceCache, ShellIcons.GetAsync(a)), "图标资源变化使相关快捷方式缓存失效");
                Check(originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(red))), "读取图标没有改变快捷方式内容");
                Shortcut(red, blueIcon); File.SetLastWriteTimeUtc(red, DateTime.UtcNow.AddSeconds(2));
                var changed = DesktopScanner.Scan([first]).Files.Single();
                var changedIcon = await ShellIcons.GetAsync(changed).WaitAsync(TimeSpan.FromSeconds(10));
                Check(!ReferenceEquals(task, ShellIcons.GetAsync(changed)), "快捷方式更新后重新加载图标");
                Check(changedIcon is BitmapSource updated && ShellIcons.SamePixels(updated, (BitmapSource)icons[1]), "快捷方式更新后显示新的图标资源");
                var missing = a with { Path = Path.Combine(fixture, "不存在.lnk") };
                Check(await ShellIcons.GetAsync(missing).WaitAsync(TimeSpan.FromSeconds(10)) != null, "失效路径仍有备用图标");
                await ShellIcons.GetAsync(b); Check(originalBlue.AsSpan().SequenceEqual(File.ReadAllBytes(blue)), "重复读取不改变快捷方式内容");
                await SystemIconVerification.RunAsync(fixture, Check);

                var store = new StateStore(Path.Combine(fixture, "state.json")); var organizer = new Organizer(store, AppState.Create([first, second]));
                window = new MainWindow(organizer, store, true, false);
                var tile = (Button)window.BuildFileTile(b); var tileBody = (StackPanel)tile.Content;
                var image = (Image)tileBody.Children[0]; image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Dispatcher.Yield(DispatcherPriority.Background);
                Check(ReferenceEquals(image.Source, icons[1]), "真实文件卡片异步更新为应用图标");
                var cached = ShellIcons.GetAsync(a);
                ShellIcons.OnShellChange(new ShellIconChange(0x00000001, a.Path, blue));
                Check(!ReferenceEquals(cached, ShellIcons.GetAsync(a)), "快捷方式更名或目标更新使旧图标缓存失效");
                Check(((TextBlock)tileBody.Children[1]).Text == "同名 应用" && tile.ToolTip.ToString()!.Contains(blue), "卡片使用简洁名称，提示保留真实路径");
                Check(tile.ContextMenu != null && window.FileMenu(b, new TileSelection()).Items.OfType<MenuItem>().Any(i => i.Header as string == "移入回收站"), "文件卡片按需创建菜单并提供回收站删除入口");
                var actual = DesktopScanner.Scan([Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)]).Files.Where(FilePresentation.IsShortcut).ToList();
                var actualIcons = await Task.WhenAll(actual.Select(ShellIcons.GetAsync)).WaitAsync(TimeSpan.FromSeconds(25));
                var canvas = new StackPanel { Width = 880, Margin = new(20) };
                canvas.Children.Add(Ui.Text("应用快捷方式 · 图标与名称", 23, bold: true));
                canvas.Children.Add(Ui.Text($"本机桌面 {actual.Count} 项 · 展示名称隐藏快捷方式后缀", 12, Ui.Muted));
                var tiles = new WrapPanel { Width = 880, Margin = new(0, 18, 0, 0) }; canvas.Children.Add(tiles);
                foreach (var file in actual)
                {
                    var item = (Button)window.BuildFileTile(file); tiles.Children.Add(item);
                    ((Image)((StackPanel)item.Content).Children[0]).RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                }
                await Dispatcher.Yield(DispatcherPriority.Background);
                var root = new Border { Child = canvas, Background = Ui.Brush("#EEF2EB") };
                root.Measure(new Size(920, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(folder, "desktop-shortcuts.png"))) encoder.Save(stream);
                Check(actualIcons.All(i => i != null), "本机快捷方式全部有图标或备用图标");
                File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks, realShortcuts = actual.Count, realShellIcons = actualIcons.Count(i => i is BitmapSource), fallbackIcons = actualIcons.Count(i => i is not BitmapSource) }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { exit = 1; File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); }
            finally { window?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }
    private static void Shortcut(string path, string icon)
    {
        // WScript.Save uses the system code page on some Windows images. Keep
        // fixture creation portable, then exercise the real Unicode path below.
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(temporary);
            try { shortcut.TargetPath = Environment.ProcessPath!; shortcut.IconLocation = icon + ",0"; shortcut.Save(); }
            finally { Marshal.FinalReleaseComObject(shortcut); }
            File.Move(temporary, path, true);
        }
        finally { Marshal.FinalReleaseComObject(shell); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private static void SaveIcon(string path, Drawing.Color color)
    {
        using var bitmap = new Drawing.Bitmap(32, 32); using (var graphics = Drawing.Graphics.FromImage(bitmap)) graphics.Clear(color);
        var handle = bitmap.GetHicon();
        try { using var icon = Drawing.Icon.FromHandle(handle); using var output = File.Create(path); icon.Save(output); }
        finally { DestroyIcon(handle); }
    }
}
