using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal static class MediaThumbnails
{
    private static readonly HashSet<string> Extensions = new("png,jpg,jpeg,bmp,gif,webp,tif,tiff,heic,avif,mp4,mkv,avi,mov,wmv,webm,m4v,mpg,mpeg,mp3,m4a,flac,wma,aac,ogg".Split(','), StringComparer.OrdinalIgnoreCase);
    public static bool Supports(DesktopFile file) => !file.IsDirectory && Extensions.Contains(file.Extension.TrimStart('.'));
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageFactory { [PreserveSig] int GetImage(Size size, uint flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageFactory item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    // Called on ShellIcons' bounded STA workers, never the UI thread.
    public static BitmapSource? Read(DesktopFile file)
    {
        var info = new FileInfo(file.Path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0 || file.Path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var iid = typeof(IImageFactory).GUID; IImageFactory? factory = null; var bitmap = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(file.Path, IntPtr.Zero, ref iid, out factory) < 0) return null;
            if (factory.GetImage(new Size { Width = 128, Height = 128 }, 0x8, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); source.Freeze(); return source;
        }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (factory != null) Marshal.FinalReleaseComObject(factory); }
    }
}
