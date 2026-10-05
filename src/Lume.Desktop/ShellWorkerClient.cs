using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Lume.Desktop;

/// <summary>One owned worker per lane, bounded callers and an end-to-end deadline including queue/startup/IPC.</summary>
internal sealed class ShellWorkerClient : IDisposable
{
    internal const int MaximumPending = 256;
    internal static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan idleLimit, retryDelay;
    private readonly Timer idleTimer;
    private NamedPipeServerStream? pipe;
    private Process? process;
    private long lastUsed = Stopwatch.GetTimestamp(), retryAfter;
    private int pending, disposed, starts, failures, workerPid;
    internal int Starts => Volatile.Read(ref starts);
    internal int Failures => Volatile.Read(ref failures);
    internal int Pending => Volatile.Read(ref pending);
    internal int? WorkerPid => Volatile.Read(ref workerPid) is var pid && pid != 0 ? pid : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    internal ShellWorkerClient(TimeSpan? idleLimit = null, TimeSpan? retryDelay = null)
    {
        this.idleLimit = idleLimit ?? TimeSpan.FromSeconds(10);
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
        idleTimer = new Timer(_ => { _ = RetireIdleAsync(); }, null, Timeout.Infinite, Timeout.Infinite);
    }
    internal Task<ShellReply?> SendAsync(ShellRequest request, CancellationToken token = default, TimeSpan? limit = null)
    {
        if (Volatile.Read(ref disposed) != 0) return Task.FromResult<ShellReply?>(null);
        if (Interlocked.Increment(ref pending) > MaximumPending) { Interlocked.Decrement(ref pending); return Task.FromResult<ShellReply?>(null); }
        var started = Stopwatch.GetTimestamp();
        // Process creation and serialization must also stay off the caller's UI thread.
        return Task.Run(async () =>
        {
            try { return await SendCoreAsync(request, token, limit ?? RequestLimit, started).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref pending); }
        });
    }
    private async Task<ShellReply?> SendCoreAsync(ShellRequest request, CancellationToken caller, TimeSpan limit, long started)
    {
        var remaining = limit - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime.Token);
        deadline.CancelAfter(remaining);
        var acquired = false;
        try
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false); acquired = true;
            deadline.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() < retryAfter) return null;
            await EnsureWorkerAsync(deadline.Token).ConfigureAwait(false);
            await ShellProtocol.WriteAsync(pipe!, request, deadline.Token).ConfigureAwait(false);
            var reply = await ShellProtocol.ReadAsync<ShellReply>(pipe!, deadline.Token).ConfigureAwait(false);
            lastUsed = Stopwatch.GetTimestamp(); ScheduleIdle(idleLimit); return reply;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException
            or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // Timing out while waiting for the gate must not kill somebody else's in-flight request.
            if (acquired)
            {
                ResetWorker(); Interlocked.Increment(ref failures);
                if (!caller.IsCancellationRequested && !lifetime.IsCancellationRequested)
                    retryAfter = Stopwatch.GetTimestamp() + (long)(retryDelay.TotalSeconds * Stopwatch.Frequency);
            }
            if (caller.IsCancellationRequested) throw new OperationCanceledException(caller);
            return null;
        }
        finally { if (acquired) gate.Release(); }
    }
    private async Task EnsureWorkerAsync(CancellationToken token)
    {
        if (process is { HasExited: false } && pipe is { IsConnected: true }) return;
        if (!ResetWorker()) throw new IOException("上次工作进程尚未退出。");
        token.ThrowIfCancellationRequested();
        var name = "Lume-Shell-" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var owner = Process.GetCurrentProcess();
        var start = CreateStartInfo(["--shell-worker", name, owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString()]);
        process = Process.Start(start) ?? throw new IOException("Shell 工作进程启动失败。"); Volatile.Write(ref workerPid, process.Id); Interlocked.Increment(ref starts);
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != process.Id) throw new IOException("Shell 工作进程身份无效。");
        var hello = await ShellProtocol.ReadAsync<ShellReply>(pipe, token).ConfigureAwait(false);
        if (!hello.Ok || hello.Pid != process.Id) throw new InvalidDataException("Shell 工作进程握手无效。");
    }
    internal static ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法定位 Shell 工作进程。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Lume.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
    private bool ResetWorker()
    {
        ScheduleIdle(Timeout.InfiniteTimeSpan);
        pipe?.Dispose(); pipe = null;
        if (process != null)
        {
            // Only this exact Process object was started by this client. Never kill Explorer or another Lume instance.
            try { if (!process.HasExited) { process.Kill(); if (!process.WaitForExit(1000)) { ScheduleIdle(TimeSpan.FromSeconds(1)); return false; } } }
            catch (System.ComponentModel.Win32Exception) { ScheduleIdle(TimeSpan.FromSeconds(1)); return false; }
            catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
        Volatile.Write(ref workerPid, 0); return true;
    }
    private async Task RetireIdleAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (Volatile.Read(ref pending) != 0 || !await gate.WaitAsync(0).ConfigureAwait(false)) { ScheduleIdle(TimeSpan.FromSeconds(1)); return; }
        try
        {
            var remaining = idleLimit - Stopwatch.GetElapsedTime(lastUsed);
            if (remaining <= TimeSpan.Zero && Volatile.Read(ref pending) == 0) ResetWorker();
            else ScheduleIdle(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));
        }
        finally { gate.Release(); }
    }
    private void ScheduleIdle(TimeSpan delay)
    {
        try { idleTimer.Change(delay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); idleTimer.Dispose();
        // Disposal can be requested from the dispatcher while a native call is stuck.
        _ = Task.Run(async () => { await gate.WaitAsync().ConfigureAwait(false); try { ResetWorker(); } finally { gate.Release(); } });
    }
}
