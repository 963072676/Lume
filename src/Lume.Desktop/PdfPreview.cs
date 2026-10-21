using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Lume.Desktop;

internal static class PdfPreview
{
    internal const uint MaximumWidth = 1200, MaximumHeight = 2400;
    internal static (uint Width, uint Height) RenderSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new InvalidDataException("PDF 页面尺寸无效。");
        var aspect = width / height;
        return aspect >= (double)MaximumWidth / MaximumHeight
            ? (MaximumWidth, (uint)Math.Clamp(Math.Round(MaximumWidth / aspect), 1, MaximumHeight))
            : ((uint)Math.Clamp(Math.Round(MaximumHeight * aspect), 1, MaximumWidth), MaximumHeight);
    }
    internal static async Task<BitmapSource> RenderAsync(PdfPage page, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var size = page.Size; var (width, height) = RenderSize(size.Width, size.Height);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height });
        token.ThrowIfCancellationRequested();
        if (stream.Size > 32 * 1024 * 1024) throw new IOException("PDF 页面过大。");
        using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
        token.ThrowIfCancellationRequested();
        var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
        using var encoded = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = encoded; bitmap.EndInit(); bitmap.Freeze();
        token.ThrowIfCancellationRequested(); return bitmap;
    }
}
