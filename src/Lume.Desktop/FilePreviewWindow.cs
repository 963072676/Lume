using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Lume.Desktop;

internal sealed class FilePreviewWindow : Window
{
    private readonly ContentControl content = new();
    private readonly TextBlock status = Ui.Text("正在读取…", Tokens.Secondary, Ui.Muted);
    private readonly CancellationTokenSource cancellation = new();
    private PdfDocument? pdf;
    private uint page;
    private MediaElement? media;
    private bool rendering;
    internal Task? Loading { get; private set; }
    internal string StatusText => status.Text;
    internal object? PreviewContent => content.Content;
    internal FilePreviewWindow(DesktopFile file, bool embedded)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Lume · 文件预览"; Width = 760; Height = 570; MinWidth = 400; MinHeight = 260;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Tokens.Brush(Tokens.Surface50);
        var layout = new DockPanel { Margin = new Thickness(16) };
        var header = new DockPanel();
        var actions = Ui.Row(Ui.Button("用默认应用打开", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Path) { UseShellExecute = true }); } catch (Exception ex) { status.Text = ex.Message; } }), Ui.Button("关闭", Close));
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        var titleBlock = new StackPanel();
        var title = Ui.Text(FilePresentation.DisplayName(file), Tokens.ItemTitle, bold: true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = file.Path; titleBlock.Children.Add(title);
        var metadata = Ui.Text($"{file.Path}  ·  {file.Size:N0} 字节  ·  修改于 {file.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}", Tokens.Secondary, Ui.Muted); metadata.TextTrimming = TextTrimming.CharacterEllipsis; metadata.ToolTip = file.Path; metadata.Margin = new Thickness(0, 4, 12, 0); titleBlock.Children.Add(metadata); header.Children.Add(titleBlock);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        status.Margin = new Thickness(0, 8, 0, 8); DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status);
        layout.Children.Add(new Border { Background = Tokens.Brush(Tokens.Surface0), BorderBrush = Tokens.Brush(Tokens.Line100), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Tokens.Radius12), Padding = new Thickness(Tokens.Space12), Child = content }); Content = layout;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => { cancellation.Cancel(); media?.Stop(); media?.Close(); pdf = null; };
        if (embedded)
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            DesktopNative.Point pointer = default; DesktopNative.Rect origin = default;
            title.Cursor = Cursors.SizeAll;
            title.MouseLeftButtonDown += (_, e) => { DesktopNative.GetCursorPos(out pointer); DesktopNative.GetWindowRect(new WindowInteropHelper(this).Handle, out origin); title.CaptureMouse(); e.Handled = true; };
            title.MouseMove += (_, _) =>
            {
                if (!title.IsMouseCaptured) return;
                DesktopNative.GetCursorPos(out var current); var h = new WindowInteropHelper(this).Handle; var parent = DesktopNative.GetParent(h);
                var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(current.X, current.Y)).WorkingArea;
                var pos = LayoutEngine.Constrain(new(origin.Left + current.X - pointer.X, origin.Top + current.Y - pointer.Y, origin.Right - origin.Left, origin.Bottom - origin.Top), new(area.X, area.Y, area.Width, area.Height));
                var point = new DesktopNative.Point { X = pos.X, Y = pos.Y }; DesktopNative.ScreenToClient(parent, ref point); DesktopNative.SetWindowPos(h, IntPtr.Zero, point.X, point.Y, pos.Width, pos.Height, 0x10);
            };
            title.MouseLeftButtonUp += (_, _) => title.ReleaseMouseCapture();
            SourceInitialized += (_, _) =>
            {
                var desktop = DesktopNative.FindDesktop().View; if (desktop == IntPtr.Zero) return;
                var h = new WindowInteropHelper(this).Handle; DesktopNative.Attach(h, desktop);
            };
            Loaded += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle; var desktop = DesktopNative.GetParent(h); if (desktop == IntPtr.Zero) return;
                var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                var p = new DesktopNative.Point { X = area.X + (area.Width - 760) / 2, Y = area.Y + (area.Height - 570) / 2 };
                DesktopNative.ScreenToClient(desktop, ref p); DesktopNative.SetWindowPos(h, IntPtr.Zero, p.X, p.Y, 760, 570, 0x40);
            };
        }
        Loaded += (_, _) => Loading = LoadAsync(file);
    }
    private async Task LoadAsync(DesktopFile file)
    {
        try
        {
            if (file.IsDirectory) { var entries = await Task.Run(() => Directory.EnumerateFileSystemEntries(file.Path).Take(200).Select(Path.GetFileName).ToArray()); cancellation.Token.ThrowIfCancellationRequested(); content.Content = Text(string.Join("\n", entries)); status.Text = "文件夹 · 最多显示 200 项"; return; }
            await Task.Run(() => FilePreview.Validate(file.Path)); cancellation.Token.ThrowIfCancellationRequested();
            var kind = FilePreview.Kind(file.Path);
            if (kind == "image")
            {
                var image = await Task.Run(() => { using var stream = File.OpenRead(file.Path); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1600; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap; });
                cancellation.Token.ThrowIfCancellationRequested();
                var display = new Image { Source = image, Stretch = Stretch.Uniform, Width = Math.Min(680, 370.0 * image.PixelWidth / image.PixelHeight) };
                var panel = new DockPanel(); var controls = Ui.Row(Ui.Button("适应", () => display.LayoutTransform = null), Ui.Button("100%", () => display.LayoutTransform = new ScaleTransform(1, 1))); DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
                var zoom = new Slider { Minimum = .25, Maximum = 3, Value = 1, Width = 180, ToolTip = "缩放" }; zoom.ValueChanged += (_, _) => display.LayoutTransform = new ScaleTransform(zoom.Value, zoom.Value); DockPanel.SetDock(zoom, Dock.Bottom); panel.Children.Add(zoom);
                panel.Children.Add(new ScrollViewer { Content = display, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }); content.Content = panel;
            }
            else if (kind == "pdf")
            {
                pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(file.Path)); cancellation.Token.ThrowIfCancellationRequested();
                await RenderPage(); return;
            }
            else if (kind == "media")
            {
                var panel = new DockPanel(); media = new MediaElement { Source = new Uri(file.Path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
                media.MediaFailed += (_, e) => status.Text = "此媒体无法解码：" + e.ErrorException.Message;
                var controls = Ui.Row(Ui.Button("播放", () => media.Play()), Ui.Button("暂停", () => media.Pause()), Ui.Button("停止", () => media.Stop()));
                DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls); panel.Children.Add(media); content.Content = panel;
            }
            else if (kind is "text" or "office" or "zip") { var text = await Task.Run(() => FilePreview.ReadText(file.Path)); cancellation.Token.ThrowIfCancellationRequested(); content.Content = Text(text); }
            else content.Content = Text($"{file.Name}\n\n{file.Path}\n\n大小：{file.Size:N0} 字节\n修改时间：{file.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n\n此格式暂无内容预览，可用默认应用打开。");
            status.Text = $"只读预览 · {file.Size:N0} 字节 · Esc 关闭";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { if (!cancellation.IsCancellationRequested) { content.Content = Text("无法预览：" + ex.Message); status.Text = "可用默认应用打开"; } }
        catch (OperationCanceledException) { }
    }
    private async Task RenderPage()
    {
        if (rendering || pdf == null || cancellation.IsCancellationRequested) return; rendering = true;
        try
        {
            using var pdfPage = pdf.GetPage(page); using var stream = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1200 }); cancellation.Token.ThrowIfCancellationRequested();
            if (stream.Size > 32 * 1024 * 1024) throw new IOException("PDF 页面过大。");
            using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size); var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            cancellation.Token.ThrowIfCancellationRequested();
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(bytes); bitmap.EndInit(); bitmap.Freeze();
            var panel = new DockPanel();
            var before = Ui.Button("上一页", async () => { if (page > 0 && !rendering) { page--; await RenderPage(); } });
            var after = Ui.Button("下一页", async () => { if (pdf != null && page + 1 < pdf.PageCount && !rendering) { page++; await RenderPage(); } });
            var controls = Ui.Row(before, Ui.Text($"{page + 1} / {pdf!.PageCount}", 12), after); DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
            panel.Children.Add(new ScrollViewer { Content = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = 700 } }); content.Content = panel; status.Text = "PDF 只读预览 · Esc 关闭";
        }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) status.Text = "PDF 预览失败：" + ex.Message; }
        finally { rendering = false; }
    }
    private static TextBox Text(string value) => new() { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 12, 0, 0) };
}
