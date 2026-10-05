using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal sealed record ShellRequest(string Kind, DesktopFile? File = null, string? Path = null, string? SystemId = null, int Generation = 0, string[]? Names = null);
internal sealed record ShellReply(bool Ok = true, ShortcutTarget? Target = null, byte[]? Pixels = null, int Width = 0, int Height = 0, long? Count = null, int Pid = 0,
    ReferenceProbeResult? References = null);

/// <summary>Length-framed local IPC. Images are bounded raw pixels, never filenames or encoded image documents.</summary>
internal static class ShellProtocol
{
    internal const int MaximumFrameBytes = 256 * 1024;
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Shell 消息超过容量限制。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Shell 消息长度无效。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Shell 消息为空。");
    }
    internal static ShellReply Image(ImageSource source, long? count = null)
    {
        var bitmap = source is BitmapSource b ? b : IconArtwork.Normalize(source, false);
        if (bitmap.PixelWidth is < 1 or > 128 || bitmap.PixelHeight is < 1 or > 128) throw new InvalidDataException("Shell 图像尺寸无效。");
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0).CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new(Pixels: pixels, Width: bitmap.PixelWidth, Height: bitmap.PixelHeight, Count: count);
    }
    internal static BitmapSource? Decode(ShellReply? reply)
    {
        if (reply is not { Ok: true, Pixels: not null }) return null;
        if (reply.Width is < 1 or > 128 || reply.Height is < 1 or > 128 || reply.Pixels.Length != reply.Width * reply.Height * 4)
            throw new InvalidDataException("Shell 图像像素长度无效。");
        var image = BitmapSource.Create(reply.Width, reply.Height, 96, 96, PixelFormats.Pbgra32, null, reply.Pixels, reply.Width * 4);
        image.Freeze(); return image;
    }
}

internal static class ShellWorkerHost
{
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    // This entry point precedes package migration, state, mutexes, WPF Application and desktop takeover.
    internal static int Run(string[] args)
    {
        if (args.Length != 4 || !args[1].StartsWith("Lume-Shell-", StringComparison.Ordinal)
            || args[1].Length != 43 || !Guid.TryParseExact(args[1][11..], "N", out _)
            || !int.TryParse(args[2], out var parentPid) || !long.TryParse(args[3], out var startTicks)) return 2;
        var initialized = false;
        try
        {
            SetErrorMode(0x8003); // Suppress native provider/drive error dialogs in this headless process.
            // Small offscreen bitmaps do not need a hardware render device in every short-lived worker.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            using var parent = Process.GetProcessById(parentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks || parent.HasExited
                || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 2;
            // A blocked COM call cannot observe pipe disconnect. A separate watcher still exits with its owner.
            var watcher = new Thread(() => { try { parent.WaitForExit(); } catch (InvalidOperationException) { } Environment.Exit(0); })
                { IsBackground = true, Name = "Lume Shell owner" };
            watcher.Start();
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5000);
            initialized = OleInitialize(IntPtr.Zero) >= 0;
            if (!initialized) return 3;
            ShellProtocol.WriteAsync(pipe, new ShellReply(Pid: Environment.ProcessId), CancellationToken.None).GetAwaiter().GetResult();
            while (true)
            {
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var request = ShellProtocol.ReadAsync<ShellRequest>(pipe, idle.Token).GetAwaiter().GetResult();
                var reply = Handle(request);
                ShellProtocol.WriteAsync(pipe, reply, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 3; }
        finally { if (initialized) OleUninitialize(); }
    }
    private static ShellReply Handle(ShellRequest request)
    {
#if VERIFICATION
        if (request.Kind == "hang")
        {
            if (request.Path != null) File.WriteAllText(request.Path, Environment.ProcessId.ToString());
            Thread.Sleep(Timeout.Infinite); return new(false);
        }
        if (request.Kind == "crash") Environment.Exit(17);
        if (request.Kind == "echo") return new(Pid: Environment.ProcessId);
#endif
        try
        {
            return request.Kind switch
            {
                "shortcut" when request.Path != null => new(Target: ShortcutReader.Read(request.Path)),
                "references" when request.Path != null && request.Names != null => new(References: WindowsReferenceProbe.Read(request.Path, request.Names)),
                "icon" when request.File != null => ShellProtocol.Image(ShellIconReader.Read(request.File, request.Generation)),
                "system" when SystemIconReader.IsKnownId(request.SystemId) => ReadSystem(request.SystemId!),
                _ => new(false)
            };
        }
        catch (Exception) { return new(false); }
    }
    private static ShellReply ReadSystem(string id)
    {
        var result = SystemIconReader.ReadSystem(id);
        return result.Icon != null ? ShellProtocol.Image(result.Icon, result.Count) : new(false);
    }
}
