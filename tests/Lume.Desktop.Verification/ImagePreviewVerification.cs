using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lume.Desktop;

internal static class ImagePreviewVerification
{
    internal static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "image-preview-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var measurements = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        void Write(string path, int width, int height, Color color)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path); encoder.Save(stream);
        }
        Color Center(BitmapSource bitmap)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var pixel = new byte[4];
            converted.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }
        void Result(bool passed, string? error = null)
        {
            var json = JsonSerializer.Serialize(new { passed, checks, measurements, error, version = RuntimeIdentity.Version, commit = RuntimeIdentity.Commit,
                windowsShown = false, nativeHandlesCreated = false, desktopTakeover = false, inputSimulation = false }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(fixture, "result.json"), json); File.WriteAllText(Path.Combine(root, "latest-result.json"), json);
        }
        try
        {
            foreach (var sample in new[] { (Name: "portrait", Width: 2000, Height: 6000, Color: Colors.Red), (Name: "landscape", Width: 6000, Height: 2000, Color: Colors.Blue), (Name: "square", Width: 1800, Height: 1800, Color: Colors.Green), (Name: "small", Width: 64, Height: 32, Color: Colors.Crimson) })
            {
                var path = Path.Combine(fixture, sample.Name + ".png"); Write(path, sample.Width, sample.Height, sample.Color);
                var hash = SHA256.HashData(File.ReadAllBytes(path)); var bitmap = ImagePreview.Load(path);
                Check(bitmap.IsFrozen && bitmap.PixelWidth <= ImagePreview.MaximumSide && bitmap.PixelHeight <= ImagePreview.MaximumSide, sample.Name + " 解码长边有界且可跨线程使用");
                var expectedSide = Math.Min(ImagePreview.MaximumSide, Math.Max(sample.Width, sample.Height));
                Check(Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) == expectedSide && Math.Abs((long)bitmap.PixelWidth * sample.Height - (long)bitmap.PixelHeight * sample.Width) <= Math.Max(sample.Width, sample.Height), sample.Name + " 保持比例且不放大小图");
                var color = Center(bitmap);
                Check(Math.Abs(color.R - sample.Color.R) <= 2 && Math.Abs(color.G - sample.Color.G) <= 2 && Math.Abs(color.B - sample.Color.B) <= 2 && color.A == 255, sample.Name + " 冻结后的实际像素保持原颜色");
                using (var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(exclusive.Length > 0, sample.Name + " 解码完成后释放文件句柄");
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), sample.Name + " 预览不改写原图");
                measurements.Add(new { sample.Name, sourceWidth = sample.Width, sourceHeight = sample.Height, width = bitmap.PixelWidth, height = bitmap.PixelHeight,
                    pixelBytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8) });
            }
            var changedPath = Path.Combine(fixture, "changed.png"); Write(changedPath, 64, 64, Colors.Red); var before = ImagePreview.Load(changedPath);
            Write(changedPath, 64, 64, Colors.Blue); var after = ImagePreview.Load(changedPath);
            Check(Center(before).R > 250 && Center(after).B > 250 && !ReferenceEquals(before, after), "同路径替换图像后读取新像素且不破坏原有冻结图");
            var rejected = false; var missing = Path.Combine(fixture, "missing.png");
            try { _ = ImagePreview.Load(missing); } catch (FileNotFoundException) { rejected = true; }
            Check(rejected, "缺失图像保持可恢复的文件错误");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); rejected = false;
            try { _ = ImagePreview.Load(missing, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "预先取消不打开缺失图像或执行解码");
            var corrupt = Path.Combine(fixture, "corrupt.png"); File.WriteAllText(corrupt, "not an image"); rejected = false;
            try { _ = ImagePreview.Load(corrupt); } catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException or IOException) { rejected = true; }
            Check(rejected, "损坏图像返回错误而不提供无效位图");
            using (var exclusive = File.Open(corrupt, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(exclusive.Length > 0, "解码失败也释放源文件句柄");
            Result(true); return 0;
        }
        catch (Exception ex) { Result(false, ex.ToString()); return 1; }
    }
}
