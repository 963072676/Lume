using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    private sealed record Entry(DesktopFile File, string? IconPath, Task<ImageSource> Task, DateTime Created);
    private sealed record Request(DesktopFile File, TaskCompletionSource<ImageSource> Completion);
    internal sealed record SystemIconResult(ImageSource Icon, long? Count);
    private sealed record SystemRequest(SystemDesktopWindow.Entry Entry, TaskCompletionSource<SystemIconResult> Completion);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<Request> Queue = new(256);
    private static readonly BlockingCollection<SystemRequest> SystemQueue = new(32);
    private static readonly ConcurrentDictionary<string, BitmapSource> TypeIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ImageSource AppFallback = DrawFallback(true, false);
    private static readonly ImageSource FileFallback = DrawFallback(false, false);
    private static readonly ImageSource FolderFallback = DrawFallback(false, true);
    private static int associationGeneration;
    internal static event Action<string?>? Invalidated;
    static ShellIcons()
    {
        // Shell 图标读取在有限数量的后台 STA 中执行，不阻塞抽屉交互。
        for (var i = 0; i < 2; i++)
        {
            var worker = new Thread(Work) { IsBackground = true, Name = "Lume 图标加载 " + i };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        var systemWorker = new Thread(SystemWork) { IsBackground = true, Name = "Lume 系统图标加载" };
        systemWorker.SetApartmentState(ApartmentState.STA); systemWorker.Start();
    }
    internal static bool HasIndividualIcon(DesktopFile file) => !file.IsDirectory &&
        (FilePresentation.IsShortcut(file) || MediaThumbnails.Supports(file) || file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".ico", StringComparison.OrdinalIgnoreCase));
    internal static string CacheKey(DesktopFile file) => HasIndividualIcon(file)
        ? $"{file.Path}\0{file.ModifiedUtc.Ticks}\0{file.Size}"
        : file.IsDirectory ? "<directory>" : file.Extension;
    internal static ImageSource Fallback(DesktopFile file) => file.IsDirectory ? FolderFallback : HasIndividualIcon(file) ? AppFallback : FileFallback;
    public static Task<ImageSource> GetAsync(DesktopFile file)
    {
        lock (Gate)
        {
            var key = CacheKey(file); var now = DateTime.UtcNow;
            if (Cache.TryGetValue(key, out var cached) && (now - cached.Created < TimeSpan.FromMinutes(2) || !cached.Task.IsCompleted)) return cached.Task;
            if (Cache.Count >= 512)
                foreach (var old in Cache.Where(p => p.Value.Task.IsCompleted).OrderBy(p => p.Value.Created).Take(128).Select(p => p.Key).ToArray()) Cache.Remove(old);
            var completion = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Queue.TryAdd(new(file, completion))) return Task.FromResult(Fallback(file));
            Cache[key] = new(file, IconResourcePath(file), completion.Task, now); return completion.Task;
        }
    }
    internal static Task<SystemIconResult> GetSystemAsync(SystemDesktopWindow.Entry entry)
    {
        var completion = new TaskCompletionSource<SystemIconResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        return SystemQueue.TryAdd(new(entry, completion)) ? completion.Task : Task.FromResult(new SystemIconResult(SystemDesktopWindow.FallbackIcon, null));
    }
    private static bool Affected(DesktopFile file, string? iconPath, string path)
    {
        if (path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)
            || path.Equals(file.Target?.Path, StringComparison.OrdinalIgnoreCase)
            || path.Equals(iconPath, StringComparison.OrdinalIgnoreCase)) return true;
        // Some Shell providers omit IconLocation. A changed standalone .ico can still back a shortcut.
        if (FilePresentation.IsShortcut(file) && path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return true;
        return path.Equals(System.IO.Path.GetDirectoryName(file.Path), StringComparison.OrdinalIgnoreCase)
            || (iconPath != null && path.Equals(System.IO.Path.GetDirectoryName(iconPath), StringComparison.OrdinalIgnoreCase))
            || (file.Target is { Path: var target } && System.IO.Path.IsPathFullyQualified(target)
                && path.Equals(System.IO.Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase));
    }
    private static string? IconResourcePath(DesktopFile file)
    {
        var location = file.Target?.IconLocation;
        if (string.IsNullOrWhiteSpace(location)) return null;
        var separator = location.LastIndexOf(',');
        if (separator >= 0 && int.TryParse(location[(separator + 1)..].Trim(), out _)) location = location[..separator];
        var path = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
        if (path.Length == 0 || path[0] == '@') return null;
        try { return System.IO.Path.GetFullPath(path, System.IO.Path.GetDirectoryName(file.Path)!); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return null; }
    }
    internal static void OnShellChange(ShellIconChange change)
    {
        if ((change.EventId & (ShellIconChanges.AssociationChanged | ShellIconChanges.ImageChanged)) != 0)
        {
            Interlocked.Increment(ref associationGeneration);
            lock (Gate) Cache.Clear();
            TypeIcons.Clear();
            Invalidated?.Invoke(null);
        }
        else if (!string.IsNullOrEmpty(change.Path) || !string.IsNullOrEmpty(change.NewPath))
        {
            lock (Gate)
                foreach (var key in Cache.Where(pair =>
                    (!string.IsNullOrEmpty(change.Path) && Affected(pair.Value.File, pair.Value.IconPath, change.Path)) ||
                    (!string.IsNullOrEmpty(change.NewPath) && Affected(pair.Value.File, pair.Value.IconPath, change.NewPath))).Select(pair => pair.Key).ToArray()) Cache.Remove(key);
            if (!string.IsNullOrEmpty(change.Path)) Invalidated?.Invoke(change.Path);
            if (!string.IsNullOrEmpty(change.NewPath)) Invalidated?.Invoke(change.NewPath);
        }
    }
    public static Image CreateImage(DesktopFile file)
    {
        var image = new Image { Source = Fallback(file), Width = 34, Height = 34, Margin = new(0, 2, 0, 4), Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var iconPath = IconResourcePath(file);
        var version = 0; var subscribed = false;
        image.Loaded += (_, _) =>
        {
            if (!subscribed) { Invalidated += OnInvalidated; subscribed = true; }
            Refresh();
        };
        image.Unloaded += (_, _) => { if (subscribed) { Invalidated -= OnInvalidated; subscribed = false; } version++; };
        void OnInvalidated(string? path) { if (path == null || Affected(file, iconPath, path)) Refresh(); }
        void Refresh() { var requested = ++version; _ = LoadAsync(requested); }
        async Task LoadAsync(int requested)
        {
            var source = await GetAsync(file);
            if (!image.Dispatcher.HasShutdownStarted)
                await image.Dispatcher.InvokeAsync(() => { if (requested == version) image.Source = source; });
        }
        return image;
    }
    private static void SystemWork()
    {
        var initialized = OleInitialize(IntPtr.Zero) >= 0;
        try
        {
            foreach (var request in SystemQueue.GetConsumingEnumerable())
            {
                SystemIconResult icon;
                try { icon = initialized ? SystemDesktopWindow.ReadSystemIcon(request.Entry) : new(SystemDesktopWindow.FallbackIcon, null); }
                catch (Exception) { icon = new(SystemDesktopWindow.FallbackIcon, null); }
                request.Completion.TrySetResult(icon);
            }
        }
        finally { if (initialized) OleUninitialize(); }
    }
    private static void Work()
    {
        var initialized = OleInitialize(IntPtr.Zero) >= 0;
        try
        {
            foreach (var request in Queue.GetConsumingEnumerable())
            {
                ImageSource icon;
                try { icon = IconArtwork.Normalize(initialized ? Resolve(request.File) : Fallback(request.File), MediaThumbnails.Supports(request.File)); }
                catch (Exception) { icon = Fallback(request.File); }
                request.Completion.TrySetResult(icon);
            }
        }
        finally { if (initialized) OleUninitialize(); }
    }
    private static ImageSource Resolve(DesktopFile file)
    {
        if (MediaThumbnails.Supports(file))
        {
            try { if (MediaThumbnails.Read(file) is { } thumbnail) return thumbnail; } catch (Exception) { }
        }
        var typeKey = file.IsDirectory ? "<directory>" : file.Extension;
        var generation = Volatile.Read(ref associationGeneration);
        if (!TypeIcons.TryGetValue(typeKey, out var generic))
        {
            generic = Read(file.IsDirectory ? "folder" : "file" + file.Extension, file.IsDirectory ? 0x10u : 0x80u, true);
            if (generic != null)
            {
                if (TypeIcons.Count >= 256) TypeIcons.Clear();
                if (generation == Volatile.Read(ref associationGeneration)) TypeIcons.TryAdd(typeKey, generic);
            }
        }
        if (!HasIndividualIcon(file)) return generic ?? Fallback(file);
        // 用真实快捷方式路径读取指定图标/目标图标；不执行目标，也不生成正文缩略图。
        var actual = Read(file.Path, 0, false);
        return actual != null && (generic == null || !SamePixels(actual, generic)) ? actual : Fallback(file);
    }
    private static BitmapSource? Read(string path, uint attributes, bool typeOnly)
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
    internal static bool SamePixels(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return false;
        var a = new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0);
        var b = new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0);
        var stride = first.PixelWidth * 4; var left = new byte[stride * first.PixelHeight]; var right = new byte[left.Length];
        a.CopyPixels(left, stride, 0); b.CopyPixels(right, stride, 0); return left.AsSpan().SequenceEqual(right);
    }
    private static ImageSource DrawFallback(bool app, bool folder)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(folder ? Color.FromRgb(225, 178, 71) : app ? Color.FromRgb(70, 139, 120) : Color.FromRgb(109, 142, 181)), null, new Rect(2, 2, 28, 28), 5, 5);
            var pen = new Pen(Brushes.White, 2);
            if (app) { dc.DrawRectangle(null, pen, new Rect(8, 8, 16, 16)); dc.DrawLine(pen, new Point(8, 13), new Point(24, 13)); }
            else { dc.DrawLine(pen, new Point(8, 12), new Point(folder ? 17 : 24, 12)); dc.DrawLine(pen, new Point(8, 18), new Point(24, 18)); }
        }
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
}
