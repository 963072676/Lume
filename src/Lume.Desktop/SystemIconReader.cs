using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Lume.Desktop;

// Independent of Window/Application resource initialization so it can run in the headless worker.
internal static class SystemIconReader
{
    private const string RecycleBinId = "645FF040-5081-101B-9F08-00AA002F954E";
    private static readonly HashSet<string> KnownIds = new(StringComparer.OrdinalIgnoreCase)
        { "20D04FE0-3AEA-1069-A2D8-08002B30309D", RecycleBinId, "F02C1A0D-BE21-4350-88B0-7367FC96EF3C", "59031A47-3F72-44A7-89C5-5595FE6B30EE", "5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0" };
    internal static bool IsKnownId(string? id) => id != null && KnownIds.Contains(id);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellInfo
    { public IntPtr Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Display; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint attributes, out uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref ShellInfo info, uint size, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RecycleInfo
    { public uint Size; public long Bytes; public long Count; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StockIconInfo
    { public uint Size; public IntPtr Icon; public int SystemIndex; public int IconIndex; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref RecycleInfo info);
    [DllImport("shell32.dll")] private static extern int SHGetStockIconInfo(int id, uint flags, ref StockIconInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    internal static long? RecycleBinItemCount()
    {
        var info = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? info.Count : null;
    }
    internal static (BitmapSource? Icon, long? Count) ReadSystem(string id)
    {
        var count = id == RecycleBinId ? RecycleBinItemCount() : null;
        return (ReadIcon(id, count.HasValue ? count.Value > 0 : null), count);
    }
    private static BitmapSource NormalizeIcon(IntPtr icon) => IconArtwork.Normalize(Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()), false);
    private static BitmapSource? ReadRecycleBinIcon(bool full)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{" + RecycleBinId + @"}\DefaultIcon");
        var location = key?.GetValue(full ? "Full" : "Empty") as string;
        if (!string.IsNullOrWhiteSpace(location))
        {
            var separator = location.LastIndexOf(','); var parsed = 0;
            var hasIndex = separator >= 0 && int.TryParse(location[(separator + 1)..].Trim(), out parsed);
            var path = Environment.ExpandEnvironmentVariables((hasIndex ? location[..separator] : location).Trim().Trim('"'));
            if (System.IO.File.Exists(path))
            {
                IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
                try { if (ExtractIconExW(path, hasIndex ? parsed : 0, out large, out small, 1) > 0 && large != IntPtr.Zero) return NormalizeIcon(large); }
                finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
            }
        }
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        try { if (SHGetStockIconInfo(full ? 32 : 31, 0x00000100, ref info) == 0 && info.Icon != IntPtr.Zero) return NormalizeIcon(info.Icon); }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
        return null;
    }
    internal static BitmapSource? ReadIcon(string id, bool? recycleBinFull = null)
    {
        var pidl = IntPtr.Zero; var info = new ShellInfo();
        try
        {
            if (id == RecycleBinId && recycleBinFull is { } full && ReadRecycleBinIcon(full) is { } recycleIcon) return recycleIcon;
            if (SHParseDisplayName("::{" + id + "}", IntPtr.Zero, out pidl, 0, out _) >= 0)
            {
                SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x108);
                if (info.Icon != IntPtr.Zero) return NormalizeIcon(info.Icon);
            }
            return null;
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }
}
