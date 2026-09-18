using System.Windows.Threading;

namespace Lume.Desktop;

/// <summary>One sleeping background thread waits on named command events. No periodic UI wakeup.</summary>
internal sealed class DesktopSignalPump : IDisposable
{
    private readonly ManualResetEvent stop = new(false);
    private readonly ManualResetEvent ready = new(false);
    private readonly TaskCompletionSource shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private bool disposed;
    public DesktopSignalPump(Dispatcher dispatcher, IReadOnlyList<WaitHandle> signals, Func<int, Task> dispatch)
    {
        var handles = new WaitHandle[] { stop }.Concat(signals).ToArray();
        worker = new Thread(() =>
        {
            if (WaitHandle.WaitAny([stop, ready]) == 0) return;
            while (true)
            {
                var selected = WaitHandle.WaitAny(handles);
                if (selected == 0) return;
                if (dispatcher.HasShutdownStarted) return;
                var operation = dispatcher.InvokeAsync(() => dispatch(selected - 1)).Task.Unwrap();
                // Quit can run inside dispatch. Dispose wakes this wait without blocking the UI thread.
                if (Task.WaitAny(operation, shutdown.Task) == 1) return;
                try { operation.GetAwaiter().GetResult(); } catch (TaskCanceledException) { return; }
            }
        }) { IsBackground = true, Name = "Lume 命令等待" };
        worker.Start();
    }
    public void Start() { if (!disposed) ready.Set(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        stop.Set(); shutdown.TrySetResult(); worker.Join(); stop.Dispose(); ready.Dispose();
    }
}
