using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal static class WallpaperGlassVerification
{
    internal static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wallpaper-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var measurements = new Dictionary<string, long>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        BitmapSource Bitmap(int size, Color color)
        {
            var pixels = new byte[size * size * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = color.A; }
            return BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        }
        void Write(string path, Color color)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(64, color)));
            using var stream = File.Create(path); encoder.Save(stream);
        }
        void Result(bool passed, string? error = null)
        {
            var json = JsonSerializer.Serialize(new { passed, checks, measurements, error, version = RuntimeIdentity.Version, commit = RuntimeIdentity.Commit, realWallpaperRegistryAccess = false, realDisplayChanges = false, windowsShown = false, desktopTakeover = false, inputSimulation = false }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(fixture, "result.json"), json); File.WriteAllText(Path.Combine(root, "latest-result.json"), json);
        }
        try
        {
            var countLimited = new WallpaperFrameCache(2, 4096); var built = 0;
            BitmapSource Frame() { built++; return Bitmap(8, Colors.Red); }
            var first = countLimited.GetOrCreate("a", (32, 32), Frame);
            Check(first.IsFrozen && ReferenceEquals(first, countLimited.GetOrCreate("a", (32, 32), Frame)) && built == 1, "同来源同分辨率复用冻结位图且不重新渲染");
            var second = countLimited.GetOrCreate("a", (64, 64), Frame);
            _ = countLimited.GetOrCreate("a", (32, 32), Frame); _ = countLimited.GetOrCreate("a", (96, 96), Frame);
            var refreshed = countLimited.GetOrCreate("a", (64, 64), Frame);
            Check(countLimited.Count == 2 && built == 4 && !ReferenceEquals(second, refreshed), "条目达到上限时淘汰最久未访问的分辨率");
            countLimited.SetSource("b"); Check(countLimited.Count == 0 && countLimited.PixelBytes == 0, "壁纸来源改变立即释放旧缓存引用");
            var bytesLimited = new WallpaperFrameCache(8, 256);
            var old = bytesLimited.GetOrCreate("a", (32, 32), Frame); _ = bytesLimited.GetOrCreate("a", (64, 64), Frame);
            Check(bytesLimited.Count == 1 && bytesLimited.PixelBytes == 256 && !ReferenceEquals(old, bytesLimited.GetOrCreate("a", (32, 32), Frame)), "像素容量达到上限时独立于条目上限淘汰");
            var huge = bytesLimited.GetOrCreate("a", (80, 80), () => Bitmap(16, Colors.Blue));
            var repeatHuge = bytesLimited.GetOrCreate("a", (80, 80), () => Bitmap(16, Colors.Blue));
            Check(huge.PixelWidth == 16 && huge.IsFrozen && !ReferenceEquals(huge, repeatHuge) && bytesLimited.PixelBytes == 256 && bytesLimited.Count == 1, "超出像素上限的单张位图可显示但不加入长期缓存");
            var rejected = false; try { _ = bytesLimited.GetOrCreate("a", (99, 99), () => throw new IOException("synthetic")); } catch (IOException) { rejected = true; }
            Check(rejected && bytesLimited.Count == 1 && bytesLimited.PixelBytes == 256, "渲染失败不加入空缓存或破坏已有计数");
            var invalid = 0; try { _ = new WallpaperFrameCache(0, 256); } catch (ArgumentOutOfRangeException) { invalid++; }
            try { _ = new WallpaperFrameCache(2, 0); } catch (ArgumentOutOfRangeException) { invalid++; }
            Check(invalid == 2, "缓存拒绝无效条目与像素容量");
            bytesLimited.Clear(); Check(bytesLimited.Count == 0 && bytesLimited.PixelBytes == 0, "清空缓存同时释放位图引用和计数");

            var path = Path.Combine(fixture, "wallpaper.png"); Write(path, Colors.Red);
            var source = WallpaperGlass.FromPath(path); var placement = new CardPlacement(-1500, 30, 320, 220); var screen = new Rect(-1920, 0, 1920, 1080);
            var brush = (ImageBrush)WallpaperGlass.At(placement, source, screen); var bitmap = (BitmapSource)brush.ImageSource;
            Check(bitmap.IsFrozen && bitmap.PixelWidth == 480 && bitmap.PixelHeight == 270, "实际壁纸保持四分之一分辨率和冻结输出");
            Check(brush.Viewbox == new Rect(420d / 1920, 30d / 1080, 320d / 1920, 220d / 1080) && brush.ViewboxUnits == BrushMappingMode.RelativeToBoundingBox && brush.Stretch == Stretch.Fill, "负坐标显示器的卡片裁切比例保持正确");
            Check(ReferenceEquals(bitmap, ((ImageBrush)WallpaperGlass.At(placement with { X = -1300 }, source, screen)).ImageSource), "同分辨率不同卡片共享模糊图而分别裁切");
            var center = new byte[4]; bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), center, 4, 0);
            Check(center[2] > 200 && center[0] < 40 && center[1] < 40, "实际离屏渲染的图像颜色来自隔离壁纸");
            var retainedBrush = brush; var changedTime = File.GetLastWriteTimeUtc(path).AddSeconds(2); Write(path, Colors.Blue); File.SetLastWriteTimeUtc(path, changedTime);
            var next = WallpaperGlass.FromPath(path); var changed = (BitmapSource)((ImageBrush)WallpaperGlass.At(placement, next, screen)).ImageSource;
            changed.CopyPixels(new Int32Rect(changed.PixelWidth / 2, changed.PixelHeight / 2, 1, 1), center, 4, 0);
            measurements["reloadedBlue"] = center[0]; measurements["reloadedRed"] = center[2];
            Check(source.Key != next.Key && !ReferenceEquals(bitmap, changed) && center[0] > 200 && center[2] < 40, "同路径壁纸时间变化后重新加载实际像素");
            Check(ReferenceEquals(retainedBrush.ImageSource, bitmap) && bitmap.IsFrozen, "失效与淘汰不破坏已显示卡片持有的图像");
            _ = WallpaperGlass.FromPath(Path.Combine(fixture, "gone.png"));
            Check(WallpaperGlass.At(placement, WallpaperGlass.FromPath(null)) == null, "壁纸不存在或为空时使用原有无背景回退");
            source = WallpaperGlass.FromPath(path);
            var rebuilt = (BitmapSource)((ImageBrush)WallpaperGlass.At(placement, source, screen)).ImageSource;
            Check(!ReferenceEquals(changed, rebuilt), "缺失壁纸后再次出现不保留缺失前缓存");
            var bounded = new WallpaperFrameCache(WallpaperGlass.MaximumFrames, WallpaperGlass.MaximumPixelBytes);
            for (var i = 0; i < 64; i++) _ = bounded.GetOrCreate("a", (1920 + i * 16, 1080 + i * 8), () => Bitmap(8, Colors.Green));
            measurements["retainedFramesAfter64Sizes"] = bounded.Count;
            Check(bounded.Count == WallpaperGlass.MaximumFrames && bounded.PixelBytes <= WallpaperGlass.MaximumPixelBytes, "连续64种模拟分辨率后的缓存数量和像素容量仍有界");
            WallpaperGlass.Clear();
            Check(!ReferenceEquals(rebuilt, ((ImageBrush)WallpaperGlass.At(placement, source, screen)).ImageSource), "显式清空后恢复显示按需重建壁纸图像");
            VerifyCardBatch(source, Check, measurements);
            Result(true); return 0;
        }
        catch (Exception ex) { Result(false, ex.ToString()); return 1; }
        finally { WallpaperGlass.Clear(); }
    }

    private static void VerifyCardBatch(WallpaperGlass.Source source, Action<bool, string> check, Dictionary<string, long> measurements)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        var host = new Window { Left = -16000, Top = 0, Width = 400, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        var cards = new List<DesktopCardWindow>(); SystemDesktopWindow? system = null; var reads = 0;
        WallpaperGlass.ReadSourceOverride = () => { reads++; return source; };
        try
        {
            var handle = new WindowInteropHelper(host).EnsureHandle();
            var state = AppState.Create([]); state.Configuration.Rules.Clear(); state.Configuration.Collections = [new("inbox", "收件箱", "#92C7B5"), new("work", "工作", "#92C7B5")];
            var organizer = new Organizer(new StateStore(Path.Combine(Path.GetTempPath(), "Lume-wallpaper-card-" + Guid.NewGuid().ToString("N"), "state.json")), state);
            UIElement Tile(DesktopFile file, TileSelection _) => new TextBlock { Text = file.Name };
            foreach (var collection in state.Configuration.Collections)
            {
                var card = new DesktopCardWindow(organizer, collection, new(30, 30, 320, 220), handle, Tile, () => { }, () => { }, _ => { }, initialFiles: [], initialWallpaper: source);
                cards.Add(card); _ = new WindowInteropHelper(card).EnsureHandle(); card.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            }
            system = new SystemDesktopWindow(organizer, handle, () => { }, [], initialWallpaper: source);
            _ = new WindowInteropHelper(system).EnsureHandle(); system.UpdateView(source);
            check(!host.IsVisible && !DesktopNative.IsWindowVisible(handle) && cards.All(c => !c.IsVisible && !DesktopNative.IsWindowVisible(c.Handle) && DesktopNative.GetParent(c.Handle) == handle) && !system.IsVisible && !DesktopNative.IsWindowVisible(system.Handle) && DesktopNative.GetParent(system.Handle) == handle, "实际卡片附着本次隐藏宿主，不显示窗口、不附着Explorer、不请求焦点");
            check(reads == 0 && cards.All(c => c.GlassApplied) && system.GlassApplied, "普通与系统卡片初始化采用提供的来源且不重复读取");
            DesktopSurface.RefreshCardFiles(organizer, cards); measurements["cardBatchSourceReads"] = reads;
            check(reads == 1 && cards.All(c => c.GlassApplied), "实际普通卡片批量刷新只读取一次壁纸来源");
            var shared = WallpaperGlass.ReadSource(); var capturedReads = reads;
            DesktopSurface.RefreshCardFiles(organizer, cards, shared); system.RefreshPlacement(shared);
            check(reads == capturedReads && cards.All(c => c.GlassApplied) && system.GlassApplied, "普通与系统卡片刷新共用同批来源而不重新读取");
            var tokens = Tokens.Theme;
            foreach (var card in cards) card.UpdateFiles(wallpaper: shared);
            check(ReferenceEquals(tokens, Tokens.Theme) && cards.All(c => c.GlassApplied), "共用来源不改变当前主题且刷新仍保留背景");
            cards[0].RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            check(reads == capturedReads + 1, "初始化来源消费后不在后续加载沿用");
        }
        finally
        {
            WallpaperGlass.ReadSourceOverride = null;
            foreach (var card in cards) card.Close(); system?.Close(); host.Close(); app.Shutdown();
        }
    }
}
