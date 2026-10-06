using System.IO;
using System.Windows.Media.Imaging;

namespace Lume.Desktop;

internal static class ImagePreview
{
    internal const int MaximumSide = 1600;
    internal static BitmapSource Load(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
        var frame = decoder.Frames[0]; var width = frame.PixelWidth; var height = frame.PixelHeight;
        token.ThrowIfCancellationRequested(); stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width > MaximumSide || height > MaximumSide)
        {
            // Set only one dimension so the decoder preserves the original aspect ratio.
            if (width >= height) bitmap.DecodePixelWidth = MaximumSide; else bitmap.DecodePixelHeight = MaximumSide;
        }
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        token.ThrowIfCancellationRequested(); return bitmap;
    }
}
