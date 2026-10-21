using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;
using Windows.Data.Pdf;
using Windows.Storage;

namespace Lume.Desktop;

internal static class PdfPreviewVerification
{
    private sealed record Page(int Width, int Height, string Color, uint OutputWidth, uint OutputHeight, int Rotation = 0, string Crop = "");
    internal static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "pdf-preview-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var measurements = new List<object>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        app.Startup += async (_, _) =>
        {
            FilePreviewWindow? window = null;
            try
            {
                var invalid = 0;
                foreach (var (width, height) in new[] { (0d, 100d), (100d, -1d), (double.NaN, 100d), (100d, double.PositiveInfinity) })
                    try { _ = PdfPreview.RenderSize(width, height); } catch (InvalidDataException) { invalid++; }
                Check(invalid == 4, "渲染前拒绝零负数及非有限页面尺寸");
                var extreme = new[] { (1e300, 1e-300), (1e-300, 1e300), (double.Epsilon, double.Epsilon) }.Select(s => PdfPreview.RenderSize(s.Item1, s.Item2)).ToArray();
                Check(extreme.All(s => s.Width is >= 1 and <= PdfPreview.MaximumWidth && s.Height is >= 1 and <= PdfPreview.MaximumHeight), "极端宽高比不产生零尺寸或突破渲染限额");
                var pages = new[] {
                    new Page(600, 400, "1 0 0", 1200, 800), new Page(400, 4000, "0 1 0", 240, 2400),
                    new Page(4000, 600, "0 0 1", 1200, 180), new Page(600, 400, "1 0 0", 1200, 1800, 90),
                    new Page(600, 400, "0 1 0", 1200, 1600, Crop: "/CropBox [0 0 300 400]"),
                    new Page(400, 4000, "0 0 1", 1200, 120, 90) };
                var path = Path.Combine(fixture, "pages.pdf"); WritePdf(path, pages);
                var hash = SHA256.HashData(File.ReadAllBytes(path)); var document = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
                Check(document.PageCount == pages.Length, "真实 PDF 加载全部独立测试页面");
                for (uint i = 0; i < pages.Length; i++)
                {
                    using var page = document.GetPage(i); var image = await PdfPreview.RenderAsync(page); var expected = pages[i];
                    Check(image.PixelWidth == expected.OutputWidth && image.PixelHeight == expected.OutputHeight, $"页面{i + 1} 保持比例并应用实际旋转裁切后的宽高");
                    Check(image.IsFrozen && await Task.Run(() => MatchesColor(image, expected.Color)), $"页面{i + 1} 实际像素颜色正确且可跨线程读取");
                    var bytes = (long)image.PixelWidth * image.PixelHeight * ((image.Format.BitsPerPixel + 7) / 8);
                    Check(bytes <= (long)PdfPreview.MaximumWidth * PdfPreview.MaximumHeight * 4, $"页面{i + 1} 返回位图像素容量不突破宽高限额");
                    measurements.Add(new { page = i + 1, width = image.PixelWidth, height = image.PixelHeight, pixelBytes = bytes });
                }
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel(); using var page = document.GetPage(0); var rejected = false;
                    try { _ = await PdfPreview.RenderAsync(page, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
                    Check(rejected, "预先取消拒绝开始 PDF 页面渲染");
                }
                var info = new FileInfo(path); var file = new DesktopFile(path, info.Name, ".pdf", info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, false, "private fixture");
                window = new(file, false) { ShowActivated = false, ShowInTaskbar = false };
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await (window.Loading ?? throw new InvalidOperationException("PDF 加载未开始")).WaitAsync(TimeSpan.FromSeconds(20));
                Check(window.PreviewContent is DependencyObject first && Find<TextBlock>(first).Any(t => t.Text == "1 / 6") && Find<Image>(first).Single().Source is BitmapSource initial && initial.PixelWidth == 1200 && initial.PixelHeight == 800, "实际预览首次加载保留普通页面尺寸和页码");
                Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "实际 PDF 预览不显示窗口不创建句柄或附着桌面");
                var before = (DependencyObject)window.PreviewContent!;
                Find<Button>(before).Single(b => b.Content as string == "上一页").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ReferenceEquals(before, window.PreviewContent), "首页向前翻页不会越界或重建内容");
                for (var i = 1; i < pages.Length; i++)
                {
                    var content = (DependencyObject)window.PreviewContent!;
                    Find<Button>(content).Single(b => b.Content as string == "下一页").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => window.PreviewContent is DependencyObject current && Find<TextBlock>(current).Any(t => t.Text == $"{i + 1} / 6"));
                    var image = Find<Image>((DependencyObject)window.PreviewContent!).Single().Source as BitmapSource;
                    Check(image != null && image.PixelWidth == pages[i].OutputWidth && image.PixelHeight == pages[i].OutputHeight && MatchesColor(image, pages[i].Color), $"实际下一页按钮显示页面{i + 1} 的受限图像和颜色");
                }
                var last = (DependencyObject)window.PreviewContent!;
                Find<Button>(last).Single(b => b.Content as string == "下一页").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ReferenceEquals(last, window.PreviewContent), "尾页向后翻页不会越界或重建内容");
                Find<Button>(last).Single(b => b.Content as string == "上一页").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => window.PreviewContent is DependencyObject current && Find<TextBlock>(current).Any(t => t.Text == "5 / 6"));
                Check(Find<Image>((DependencyObject)window.PreviewContent!).Single().Source is BitmapSource previous && previous.PixelHeight == 1600 && MatchesColor(previous, pages[4].Color), "实际上一页按钮恢复裁切页面及正确像素");
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "渲染及双向翻页不改写 PDF 原件");
                window.Close(); window = new(file, false) { ShowActivated = false, ShowInTaskbar = false };
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); var pending = window.Loading!; window.Close();
                await pending.WaitAsync(TimeSpan.FromSeconds(20));
                Check(window.PreviewContent == null && !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "加载中关闭隐藏预览不提交迟到结果或创建窗口");
                var corruptPath = Path.Combine(fixture, "corrupt.pdf"); File.WriteAllText(corruptPath, "not a PDF");
                window = new(file with { Path = corruptPath }, false) { ShowActivated = false, ShowInTaskbar = false };
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await window.Loading!.WaitAsync(TimeSpan.FromSeconds(20));
                Check(window.PreviewContent is TextBox error && error.Text.StartsWith("无法预览") && !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "损坏 PDF 在隐藏控件内显示可恢复错误");
                var result = new { passed = true, checks, measurements, version = RuntimeIdentity.Version, commit = RuntimeIdentity.Commit, windowsShown = false, nativeHandlesCreated = false, desktopTakeover = false, inputSimulation = false };
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(Path.Combine(fixture, "result.json"), json); File.WriteAllText(Path.Combine(root, "latest-result.json"), json);
            }
            catch (Exception ex)
            {
                exit = 1; var json = JsonSerializer.Serialize(new { passed = false, checks, measurements, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(fixture, "error.txt"), ex.ToString()); File.WriteAllText(Path.Combine(root, "latest-result.json"), json);
            }
            finally { window?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }
    private static bool MatchesColor(BitmapSource image, string color)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return color switch { "1 0 0" => pixel[2] > 250 && pixel[0] < 5 && pixel[1] < 5, "0 1 0" => pixel[1] > 250 && pixel[0] < 5 && pixel[2] < 5, _ => pixel[0] > 250 && pixel[1] < 5 && pixel[2] < 5 };
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) foreach (var result in Find<T>(child)) yield return result;
    }
    private static async Task Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew(); while (!done()) { if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("PDF 翻页未在期限内完成。"); await Task.Delay(10); }
    }
    private static void WritePdf(string path, IReadOnlyList<Page> pages)
    {
        var children = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{3 + 2 * i} 0 R"));
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{children}] /Count {pages.Count} >>" };
        foreach (var page in pages)
        {
            var number = objects.Count + 1;
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Rotate {page.Rotation} {page.Crop} /Contents {number + 1} 0 R >>"));
            var content = FormattableString.Invariant($"{page.Color} rg 0 0 {page.Width} {page.Height} re f\n");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(text.Length); text.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length; text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }
}
