using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Lume.Core;

public enum DiagnosticKind { Startup, Scan, Refresh, WatcherError, SurfaceError, OperationFailed, Unhandled, Heartbeat, Resume, DisplayChanged, GuardExit }
public enum DiagnosticError { None, Io, AccessDenied, InvalidData, WatcherOverflow, Windows, Other }
public sealed record DiagnosticEntry(DateTime Utc, DiagnosticKind Kind, double DurationMs, int Items, int Issues, int Work,
    DiagnosticError Error, int ErrorCode, long PrivateBytes = 0, long WorkingSetBytes = 0, int Handles = 0);
public sealed record DiagnosticSummary(int Files, int Roots, int Collections, int History, bool DesktopPaused);

/// <summary>Typed events only: no paths, exception messages, request bodies or credentials.</summary>
public sealed class RuntimeDiagnostics : IDisposable
{
    public const int MaximumFileBytes = 512 * 1024;
    private readonly string directory;
    private readonly object filesGate = new();
    private readonly Channel<DiagnosticEntry> queue = Channel.CreateBounded<DiagnosticEntry>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private int dropped;
    public int Dropped => Volatile.Read(ref dropped);
    public RuntimeDiagnostics(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        writer = Task.Run(async () =>
        {
            await foreach (var entry in queue.Reader.ReadAllAsync())
            {
                try
                {
                    var line = JsonSerializer.Serialize(entry) + "\n";
                    lock (filesGate)
                    {
                        Directory.CreateDirectory(this.directory);
                        var file = Path.Combine(this.directory, "runtime.jsonl");
                        if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) > MaximumFileBytes)
                            File.Move(file, file + ".1", true);
                        File.AppendAllText(file, line, new UTF8Encoding(false));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref dropped); }
            }
        });
    }
    public void Record(DiagnosticKind kind, double durationMs = 0, int items = 0, int issues = 0, int work = 0, Exception? error = null)
    {
        var category = error switch
        {
            null => DiagnosticError.None, InternalBufferOverflowException => DiagnosticError.WatcherOverflow,
            InvalidDataException => DiagnosticError.InvalidData, UnauthorizedAccessException => DiagnosticError.AccessDenied,
            System.ComponentModel.Win32Exception => DiagnosticError.Windows, IOException => DiagnosticError.Io, _ => DiagnosticError.Other
        };
        Enqueue(new(DateTime.UtcNow, kind, double.IsFinite(durationMs) ? Math.Round(Math.Max(0, durationMs), 2) : 0,
            Math.Max(0, items), Math.Max(0, issues), Math.Max(0, work), category,
            error is System.ComponentModel.Win32Exception windows ? windows.NativeErrorCode : error?.HResult ?? 0));
    }
    public void Heartbeat()
    {
        using var process = Process.GetCurrentProcess();
        Enqueue(new(DateTime.UtcNow, DiagnosticKind.Heartbeat, 0, 0, Dropped, 0, DiagnosticError.None, 0,
            process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount));
    }
    private void Enqueue(DiagnosticEntry entry) { if (!queue.Writer.TryWrite(entry)) Interlocked.Increment(ref dropped); }

    public void Export(string destination, DiagnosticSummary summary)
    {
        var target = Path.GetFullPath(destination);
        if (target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("请在诊断目录之外保存导出文件。");
        List<DiagnosticEntry> entries = [];
        lock (filesGate)
        {
            foreach (var file in new[] { "runtime.jsonl.1", "runtime.jsonl" }.Select(n => Path.Combine(directory, n)).Where(File.Exists))
            {
                if (new FileInfo(file).Length > MaximumFileBytes) continue;
                foreach (var line in File.ReadLines(file))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<DiagnosticEntry>(line);
                        if (entry != null && Enum.IsDefined(entry.Kind) && Enum.IsDefined(entry.Error)) entries.Add(entry);
                    }
                    catch (JsonException) { }
                }
            }
        }
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                using (var output = new StreamWriter(zip.CreateEntry("runtime.jsonl").Open(), new UTF8Encoding(false)))
                    foreach (var entry in entries) output.WriteLine(JsonSerializer.Serialize(entry));
                using var info = new StreamWriter(zip.CreateEntry("summary.json").Open(), new UTF8Encoding(false));
                info.Write(JsonSerializer.Serialize(new { schema = 1, createdUtc = DateTime.UtcNow, runtime = Environment.Version.ToString(),
                    os = Environment.OSVersion.Version.ToString(), processors = Environment.ProcessorCount, dropped = Dropped, summary }));
            }
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() { queue.Writer.TryComplete(); writer.Wait(TimeSpan.FromSeconds(2)); }
}
