using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

/// <summary>Native/provider calls only used by the isolated STA worker.</summary>
internal static class ShellIconReader
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private static readonly Dictionary<string, BitmapSource> TypeIcons = new(StringComparer.OrdinalIgnoreCase);
    private static int generation = -1;
    internal static ImageSource Read(DesktopFile file, int requestedGeneration)
    {
        if (generation != requestedGeneration) { TypeIcons.Clear(); generation = requestedGeneration; }
        return IconArtwork.Normalize(Resolve(file), MediaThumbnails.Supports(file));
    }
    private static ImageSource Resolve(DesktopFile file)
    {
        if (MediaThumbnails.Supports(file))
            try { if (MediaThumbnails.Read(file) is { } thumbnail) return thumbnail; } catch (Exception) { }
        var typeKey = file.IsDirectory ? "<directory>" : file.Extension;
        if (!TypeIcons.TryGetValue(typeKey, out var generic))
        {
            generic = ReadNative(file.IsDirectory ? "folder" : "file" + file.Extension, file.IsDirectory ? 0x10u : 0x80u, true);
            if (generic != null) { if (TypeIcons.Count >= 256) TypeIcons.Clear(); TypeIcons.TryAdd(typeKey, generic); }
        }
        if (!ShellIcons.HasIndividualIcon(file)) return generic ?? ShellIcons.Fallback(file);
        var actual = ReadNative(file.Path, 0, false);
        return actual != null && (generic == null || !ShellIcons.SamePixels(actual, generic)) ? actual : ShellIcons.Fallback(file);
    }
    private static BitmapSource? ReadNative(string path, uint attributes, bool typeOnly)
    {
        var info = new FileInfo();
        try
        {
            _ = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100u | (typeOnly ? 0x10u : 0));
            if (info.Icon == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
    }
}
