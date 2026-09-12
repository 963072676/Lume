using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lume.Desktop;

internal static class IconArtwork
{
    // Normalize visible artwork instead of just resizing differently padded source bitmaps.
    internal static BitmapSource Normalize(ImageSource source, bool thumbnail)
    {
        const int size = 128;
        ImageSource artwork = source;
        if (!thumbnail && source is BitmapSource bitmap)
        {
            var rgba = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = rgba.PixelWidth * 4; var pixels = new byte[stride * rgba.PixelHeight]; rgba.CopyPixels(pixels, stride, 0);
            var left = rgba.PixelWidth; var top = rgba.PixelHeight; var right = -1; var bottom = -1;
            for (var y = 0; y < rgba.PixelHeight; y++) for (var x = 0; x < rgba.PixelWidth; x++)
                if (pixels[y * stride + x * 4 + 3] > 24) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            if (right >= left && bottom >= top) { var crop = new CroppedBitmap(rgba, new Int32Rect(left, top, right - left + 1, bottom - top + 1)); crop.Freeze(); artwork = crop; }
        }
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            if (thumbnail) drawing.DrawRoundedRectangle(Ui.Brush("#F1F4EF"), new Pen(Ui.Brush("#CBD6CC"), 2), new Rect(5, 5, 118, 118), 12, 12);
            var available = thumbnail ? 110.0 : 116.0;
            var ratio = available / Math.Max(Math.Max(artwork.Width, artwork.Height), 1);
            var width = artwork.Width * ratio; var height = artwork.Height * ratio;
            drawing.DrawImage(artwork, new Rect((size - width) / 2, (size - height) / 2, width, height));
        }
        var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
}
