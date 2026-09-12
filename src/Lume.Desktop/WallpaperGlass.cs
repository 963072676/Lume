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
    private static readonly Dictionary<string, BitmapSource> Cache = [];
    private static string fingerprint = "";
    public static string SourceKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop");
        var path = key?.GetValue("WallPaper") as string;
        return path != null && File.Exists(path) ? path + File.GetLastWriteTimeUtc(path).Ticks : "";
    }
    public static Brush? At(CardPlacement placement)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop");
        var path = key?.GetValue("WallPaper") as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(placement.X + 10, placement.Y + 10)) ?? Forms.Screen.PrimaryScreen!;
        var rect = screen.Bounds;
        var current = path + File.GetLastWriteTimeUtc(path).Ticks;
        if (fingerprint != current) { fingerprint = current; Cache.Clear(); }
        var cacheKey = $"{rect.Width}x{rect.Height}";
        if (!Cache.TryGetValue(cacheKey, out var bitmap))
        {
            var width = Math.Max(1, rect.Width / 4); var height = Math.Max(1, rect.Height / 4);
            var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.DecodePixelWidth = width; source.UriSource = new Uri(path); source.EndInit(); source.Freeze();
            var image = new Image { Source = source, Width = width, Height = height, Stretch = Stretch.UniformToFill,
                Effect = new BlurEffect { Radius = 9, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            image.Measure(new Size(width, height)); image.Arrange(new Rect(0, 0, width, height));
            var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); render.Render(image); render.Freeze(); bitmap = render; Cache[cacheKey] = bitmap;
        }
        return new ImageBrush(bitmap) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect((placement.X - rect.Left) / (double)rect.Width, (placement.Y - rect.Top) / (double)rect.Height, placement.Width / (double)rect.Width, placement.Height / (double)rect.Height), Stretch = Stretch.Fill };
    }

    public static double AverageBrightnessAt(CardPlacement placement)
    {
        try
        {
            var brush = At(placement) as ImageBrush;
            if (brush?.ImageSource is not BitmapSource source) return 0;
            var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(placement.X + 10, placement.Y + 10)) ?? Forms.Screen.PrimaryScreen!;
            var scaleX = source.PixelWidth / (double)screen.Bounds.Width; var scaleY = source.PixelHeight / (double)screen.Bounds.Height;
            var x = Math.Clamp((int)((placement.X - screen.Bounds.Left) * scaleX), 0, source.PixelWidth - 1);
            var y = Math.Clamp((int)((placement.Y - screen.Bounds.Top) * scaleY), 0, source.PixelHeight - 1);
            var width = Math.Clamp((int)(placement.Width * scaleX), 1, source.PixelWidth - x);
            var height = Math.Clamp((int)(placement.Height * scaleY), 1, source.PixelHeight - y);
            var sample = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
            var stepX = Math.Max(1, sample.PixelWidth / 24); var stepY = Math.Max(1, sample.PixelHeight / 24);
            var pixels = new byte[sample.PixelWidth * sample.PixelHeight * 4]; sample.CopyPixels(pixels, sample.PixelWidth * 4, 0);
            double total = 0; var count = 0;
            for (var row = 0; row < sample.PixelHeight; row += stepY)
                for (var column = 0; column < sample.PixelWidth; column += stepX)
                {
                    var offset = (row * sample.PixelWidth + column) * 4;
                    var r = pixels[offset + 2] / 255d; var g = pixels[offset + 1] / 255d; var b = pixels[offset] / 255d;
                    total += .2126 * r + .7152 * g + .0722 * b; count++;
                }
            return count == 0 ? 0 : total / count;
        }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }
}
