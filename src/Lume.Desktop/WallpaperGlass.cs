using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Lume.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Lume.Desktop;

internal static class WallpaperGlass
{
    internal const int MaximumFrames = 8;
    internal const long MaximumPixelBytes = 32 * 1024 * 1024;
    private static readonly WallpaperFrameCache Cache = new(MaximumFrames, MaximumPixelBytes);
    private static string decodedSourceKey = "";
    internal readonly record struct Source(string Path, string Key);
#if VERIFICATION
    internal static Func<Source>? ReadSourceOverride { get; set; }
#endif

    internal static Source ReadSource()
    {
#if VERIFICATION
        if (ReadSourceOverride != null) return ReadSourceOverride();
#endif
        using var key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop");
        return FromPath(key?.GetValue("WallPaper") as string);
    }
    internal static Source FromPath(string? path)
    {
        var source = new Source("", "");
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) source = new(path, path + "|" + File.GetLastWriteTimeUtc(path).Ticks); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (source.Key.Length == 0) Clear(); else Cache.SetSource(source.Key);
        return source;
    }
    internal static void Clear() { Cache.Clear(); decodedSourceKey = ""; }
    public static string SourceKey() => ReadSource().Key;
    public static Brush? At(CardPlacement placement) => At(placement, ReadSource());
    internal static Brush? At(CardPlacement placement, Source source)
    {
        if (string.IsNullOrEmpty(source.Path)) { Clear(); return null; }
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(placement.X + 10, placement.Y + 10)) ?? Forms.Screen.PrimaryScreen!;
        var rect = screen.Bounds;
        return At(placement, source, new Rect(rect.X, rect.Y, rect.Width, rect.Height));
    }
    internal static Brush At(CardPlacement placement, Source source, Rect screen)
    {
        var width = Math.Max(1, (int)screen.Width / 4); var height = Math.Max(1, (int)screen.Height / 4);
        var bitmap = Cache.GetOrCreate(source.Key, ((int)screen.Width, (int)screen.Height), () => Render(source, width, height));
        return new ImageBrush(bitmap) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect((placement.X - screen.Left) / screen.Width, (placement.Y - screen.Top) / screen.Height, placement.Width / screen.Width, placement.Height / screen.Height), Stretch = Stretch.Fill };
    }
    private static BitmapSource Render(Source wallpaper, int width, int height)
    {
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
        if (decodedSourceKey != wallpaper.Key) source.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        source.DecodePixelWidth = width; source.UriSource = new Uri(wallpaper.Path); source.EndInit(); source.Freeze(); decodedSourceKey = wallpaper.Key;
        var image = new Image { Source = source, Width = width, Height = height, Stretch = Stretch.UniformToFill,
            Effect = new BlurEffect { Radius = 9, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
        image.Measure(new Size(width, height)); image.Arrange(new Rect(0, 0, width, height));
        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); render.Render(image); render.Freeze(); return render;
    }
}

internal sealed class WallpaperFrameCache
{
    private readonly int maximumFrames;
    private readonly long maximumPixelBytes;
    private readonly LinkedList<((int Width, int Height) Key, BitmapSource Bitmap, long Bytes)> order = new();
    private readonly Dictionary<(int Width, int Height), LinkedListNode<((int Width, int Height) Key, BitmapSource Bitmap, long Bytes)>> frames = [];
    private string sourceKey = "";
    internal int Count => frames.Count;
    internal long PixelBytes { get; private set; }
    internal WallpaperFrameCache(int maximumFrames, long maximumPixelBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrames); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPixelBytes);
        this.maximumFrames = maximumFrames; this.maximumPixelBytes = maximumPixelBytes;
    }
    internal void SetSource(string key) { if (sourceKey != key) { Clear(); sourceKey = key; } }
    internal void Clear() { frames.Clear(); order.Clear(); PixelBytes = 0; sourceKey = ""; }
    internal BitmapSource GetOrCreate(string source, (int Width, int Height) key, Func<BitmapSource> render)
    {
        SetSource(source);
        if (frames.TryGetValue(key, out var cached)) { order.Remove(cached); order.AddLast(cached); return cached.Value.Bitmap; }
        var bitmap = render(); if (!bitmap.IsFrozen) bitmap.Freeze();
        var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8);
        if (bytes > maximumPixelBytes) return bitmap;
        while (frames.Count >= maximumFrames || PixelBytes + bytes > maximumPixelBytes)
        {
            var oldest = order.First!; PixelBytes -= oldest.Value.Bytes; frames.Remove(oldest.Value.Key); order.RemoveFirst();
        }
        frames[key] = order.AddLast((key, bitmap, bytes)); PixelBytes += bytes; return bitmap;
    }
}
