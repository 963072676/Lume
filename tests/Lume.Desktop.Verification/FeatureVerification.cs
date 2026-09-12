using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal static class FeatureVerification
{
    internal static int Run(bool interactive = true)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "features-verification"); Directory.CreateDirectory(folder);
        var fixture = Path.Combine(folder, "fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        app.Startup += async (_, _) =>
        {
            FilePreviewWindow? window = null;
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
                File.WriteAllText(Path.Combine(fixture, "项目计划.txt"), "项目计划\n\n预览、自由调整、对齐线\n原文件保持不变。");
                using (var bitmap = new System.Drawing.Bitmap(800, 500))
                {
                    using var graphics = System.Drawing.Graphics.FromImage(bitmap); graphics.Clear(System.Drawing.Color.SeaGreen);
                    using var font = new System.Drawing.Font("Microsoft YaHei UI", 40); graphics.DrawString("Lume 图片预览", font, System.Drawing.Brushes.White, 80, 190);
                    bitmap.Save(Path.Combine(fixture, "图片.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                void Zip(string name, string part, string xml) { using var zip = ZipFile.Open(Path.Combine(fixture, name), ZipArchiveMode.Create); using var writer = new StreamWriter(zip.CreateEntry(part).Open()); writer.Write(xml); }
                Zip("文档.docx", "word/document.xml", "<document><p><t>项目计划 Word 内容</t></p></document>");
                Zip("表格.xlsx", "xl/worksheets/sheet1.xml", "<worksheet><row><c t='inlineStr'><is><t>销售额</t></is></c><c><v>123</v></c></row></worksheet>");
                Zip("演示.pptx", "ppt/slides/slide1.xml", "<sld><p><t>项目演示</t></p></sld>");
                Zip("压缩包.zip", "folder/readme.txt", "只读列表");
                WritePdf(Path.Combine(fixture, "两页.pdf"));
                File.WriteAllText(Path.Combine(fixture, "损坏.png"), "not an image");
                using (var wav = new BinaryWriter(File.Create(Path.Combine(fixture, "静音.wav")))) { wav.Write(Encoding.ASCII.GetBytes("RIFF")); wav.Write(16036); wav.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16); wav.Write((short)1); wav.Write((short)1); wav.Write(8000); wav.Write(16000); wav.Write((short)2); wav.Write((short)16); wav.Write(Encoding.ASCII.GetBytes("data")); wav.Write(16000); wav.Write(new byte[16000]); }
                var files = DesktopScanner.Scan([fixture]).Files;
                var thumbnail = await ShellIcons.GetAsync(files.Single(f => f.Name == "图片.png"));
                var brokenThumbnail = await ShellIcons.GetAsync(files.Single(f => f.Name == "损坏.png"));
                Check(thumbnail is BitmapSource picture && (brokenThumbnail is not BitmapSource brokenPicture || !ShellIcons.SamePixels(picture, brokenPicture)), "图片文件图标显示内容缩略图且与损坏图片备用图标区分");
                foreach (var name in new[] { "项目计划.txt", "图片.png", "文档.docx", "表格.xlsx", "演示.pptx", "压缩包.zip", "两页.pdf", "静音.wav", "损坏.png" })
                {
                    var file = files.Single(f => f.Name == name); var original = File.ReadAllBytes(file.Path);
                    window = new(file, false) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, ShowActivated = false };
                    window.Show(); await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    await (window.Loading ?? throw new Exception("预览未开始加载")).WaitAsync(TimeSpan.FromSeconds(20));
                    window.UpdateLayout();
                    Check(window.PreviewContent != null, name + " 生成内容");
                    if (name == "损坏.png") Check(window.PreviewContent is TextBox text && text.Text.StartsWith("无法预览"), "损坏图片显示可恢复错误");
                    else Check(!window.StatusText.Contains("失败") && !window.StatusText.Contains("默认应用"), name + " 读取成功");
                    if (name == "两页.pdf")
                    {
                        Check(Find<TextBlock>(window).Any(t => t.Text == "1 / 2"), "PDF 真实渲染两页文档");
                        Find<Button>(window).Single(b => b.Content as string == "下一页").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (var i = 0; i < 100 && !Find<TextBlock>(window).Any(t => t.Text == "2 / 2"); i++) await Task.Delay(50);
                        Check(Find<TextBlock>(window).Any(t => t.Text == "2 / 2"), "PDF 下一页按钮实际翻页");
                    }
                    if (name == "图片.png") { var slider = Find<Slider>(window).Single(); slider.Value = 1.5; Check(Find<Image>(window).Any(i => i.LayoutTransform is ScaleTransform t && t.ScaleX == 1.5), "图片缩放控件生效"); }
                    window.UpdateLayout();
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(Path.Combine(folder, name + ".png"))) encoder.Save(stream);
                    window.Close(); window = null; Check(original.SequenceEqual(File.ReadAllBytes(file.Path)), name + " 预览没有改写文件");
                }
                var pending = new FilePreviewWindow(files.First(), false) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, ShowActivated = false }; pending.Show(); pending.Close(); if (pending.Loading != null) await pending.Loading;
                checks.Add("加载中关闭预览不会重新打开窗口");
                var ruleOrganizer = new Organizer(new StateStore(Path.Combine(fixture, "rules-state.json")), AppState.Create([]));
                ruleOrganizer.ApplyScan(new([new DesktopFile(Path.Combine(fixture, "音乐.lnk"), "音乐.lnk", ".lnk", 10, DateTime.UtcNow, DateTime.UtcNow, false, "未知来源",
                    new ShortcutTarget(@"D:\Games\Player.exe", "Player.exe", ".exe", "file"))], []));
                var ruleOwner = new Window { Left = -16000, ShowActivated = false }; ruleOwner.Show();
                var ruleDialog = new RuleDialog(ruleOwner, ruleOrganizer, new Rule("preview", "影音规则", "work", [new Lume.Core.Condition("name", "contains", "音乐,视频,剧,音")]))
                    { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, ShowActivated = false };
                ruleDialog.Show(); ruleDialog.UpdateLayout();
                Find<Button>(ruleDialog).Single(b => b.Content as string == "预览匹配").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Find<TextBlock>(ruleDialog).Any(t => t.Text.Contains("条件命中 1 项 · 本规则生效 1 项")), "规则编辑器真实按钮预览多关键词命中");
                var fieldBox = Find<ComboBox>(ruleDialog).Single(c => c.SelectedValue as string == "name");
                fieldBox.SelectedValue = "targetPath";
                Find<TextBox>(ruleDialog).Single(t => t.Text == "音乐,视频,剧,音").Text = "Game";
                Find<Button>(ruleDialog).Single(b => b.Content as string == "预览匹配").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Find<TextBlock>(ruleDialog).Any(t => t.Text.Contains("条件命中 1 项") && t.Text.Contains(@"D:\Games\Player.exe")), "快捷目标字段切换与目标证据展示");
                ruleDialog.UpdateLayout();
                var ruleImage = new RenderTargetBitmap((int)ruleDialog.ActualWidth, (int)ruleDialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); ruleImage.Render(ruleDialog);
                var ruleEncoder = new PngBitmapEncoder(); ruleEncoder.Frames.Add(BitmapFrame.Create(ruleImage));
                using (var stream = File.Create(Path.Combine(folder, "规则预览.png"))) ruleEncoder.Save(stream);
                ruleDialog.Close(); ruleOwner.Close();
                await AiVerification.RunAsync(folder, checks);
                await VisualVerification.RunAsync(checks, interactive);
                await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { passed = true, interactive, checks }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { exit = 1; File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); }
            finally { window?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T result) yield return result; foreach (var item in Find<T>(child)) yield return item; }
    }
    private static void WritePdf(string path)
    {
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", "", "" };
        for (int i = 5; i < 7; i++) { var s = $"BT /F1 30 Tf 60 220 Td (Lume PDF page {i - 4}) Tj ET"; objects[i] = $"<< /Length {s.Length} >>\nstream\n{s}\nendstream"; }
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length; text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n"); foreach (var o in offsets) text.Append($"{o:D10} 00000 n \n"); text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }
}
