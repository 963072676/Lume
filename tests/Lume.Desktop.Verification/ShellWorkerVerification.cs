using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lume.Core;

namespace Lume.Desktop;

internal static class ShellWorkerVerification
{
    internal static int OwnerProbe(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 2;
        using var client = new ShellWorkerClient();
        var reply = client.SendAsync(new("echo"), limit: TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (reply is not { Ok: true, Pid: > 0 }) return 1;
        var ready = args[1] + ".ready";
        _ = client.SendAsync(new("hang", Path: ready), limit: TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        while (!File.Exists(ready)) { if (clock.Elapsed > TimeSpan.FromSeconds(2)) return 1; Thread.Sleep(10); }
        File.WriteAllText(args[1], JsonSerializer.Serialize(new { ownerPid = Environment.ProcessId, workerPid = reply.Pid }));
        Thread.Sleep(Timeout.Infinite); return 0;
    }
    internal static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "shell-worker-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var measurements = new Dictionary<string, double>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            var beats = 0; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) => beats++;
            timer.Start();
            try
            {
                using var client = new ShellWorkerClient(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(150));
                var hello = await client.SendAsync(new("echo"), limit: TimeSpan.FromSeconds(5));
                Check(hello is { Ok: true, Pid: > 0 } && hello.Pid != Environment.ProcessId, "请求由独立进程执行");
                var firstPid = hello!.Pid;
                using (var worker = Process.GetProcessById(firstPid)) measurements["workerStartupPrivateMiB"] = worker.PrivateMemorySize64 / (1024d * 1024);
                var parallel = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.SendAsync(new("echo"))));
                Check(parallel.All(r => r?.Pid == firstPid) && client.Starts == 1, "并发请求串行复用一个工作进程");

                var directory = Path.Combine(fixture, "files"); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "sample.txt"); File.WriteAllText(path, "fixture");
                var url = Path.Combine(directory, "example.url"); File.WriteAllText(url, "[InternetShortcut]\r\nURL=https://example.invalid/lume\r\n");
                var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(url));
                var target = await client.SendAsync(new("shortcut", Path: url));
                Check(target?.Target is { Kind: "url", Path: "https://example.invalid/lume" }, "真实网页快捷方式经 IPC 读取目标且不访问网页");
                Check(before.AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(url))), "读取目标不修改原快捷方式");
                var file = DesktopScanner.Scan([], [path], _ => null).Files.Single();
                var icon = ShellProtocol.Decode(await client.SendAsync(new("icon", file)));
                Check(icon is { PixelWidth: 128, PixelHeight: 128, IsFrozen: true }, "真实 Shell 图标像素传回主进程且可跨线程使用");
                var recycle = await client.SendAsync(new("system", SystemId: SystemDesktopWindow.RecycleBinId));
                File.WriteAllText(Path.Combine(fixture, "recycle-result.json"), JsonSerializer.Serialize(new { recycle?.Ok, recycle?.Width, recycle?.Height, recycle?.Count }));
                Check(ShellProtocol.Decode(recycle) is { PixelWidth: 128 } && (recycle?.Count == null || recycle.Count >= 0), "系统图标经进程读取，计数不可读取时保留未知状态");
                using (var worker = Process.GetProcessById(firstPid)) measurements["workerPrivateMiB"] = worker.PrivateMemorySize64 / (1024d * 1024);

                var store = new StateStore(Path.Combine(fixture, "state.json")); var organizer = new Organizer(store, AppState.Create([directory]));
                window = new MainWindow(organizer, store, true, false); await window.StartMonitoringAsync();
                Check(organizer.Files.Count == 2 && !window.IsVisible, "管理界面在未显示窗口时完成隔离目录扫描");

                var baseline = beats; var clock = Stopwatch.StartNew();
                var hung = await client.SendAsync(new("hang"), limit: TimeSpan.FromMilliseconds(300));
                measurements["hungRequestMs"] = clock.Elapsed.TotalMilliseconds;
                Check(hung == null && clock.Elapsed < TimeSpan.FromSeconds(2), "无限挂起请求在期限内返回失败");
                Check(beats - baseline >= 3, "工作进程挂起时管理界面 Dispatcher 仍响应");
                Check(!Alive(firstPid), "超时后确认卡死进程已经退出");
                var starts = client.Starts;
                Check(await client.SendAsync(new("echo")) == null && client.Starts == starts, "失败冷却期间不连续创建工作进程");
                await Task.Delay(200);
                var recovered = await client.SendAsync(new("echo"));
                Check(recovered is { Pid: > 0 } && recovered.Pid != firstPid, "冷却后请求启动新进程并成功恢复");

                var hang = client.SendAsync(new("hang"), limit: TimeSpan.FromMilliseconds(700));
                await Task.Delay(50);
                clock.Restart();
                var queued = Enumerable.Range(0, 400).Select(_ => client.SendAsync(new("echo"), limit: TimeSpan.FromMilliseconds(100))).ToArray();
                Check(client.Pending <= ShellWorkerClient.MaximumPending, "请求风暴保留的等待任务有容量上限");
                var queuedResults = await Task.WhenAll(queued);
                measurements["queuedRequestsMs"] = clock.Elapsed.TotalMilliseconds;
                Check(queuedResults.All(r => r == null) && clock.Elapsed < TimeSpan.FromSeconds(2), "排队请求使用包含等待时间的期限而不累计阻塞");
                Check(client.WorkerPid == recovered!.Pid && Alive(recovered.Pid), "排队超时不会终止其他正在执行的请求");
                Check(await hang == null && !Alive(recovered.Pid), "执行请求到期后才结束对应卡死进程");
                await Task.Delay(200);

                var current = await client.SendAsync(new("echo")); var cancelPid = current!.Pid;
                using (var cancellation = new CancellationTokenSource(150))
                {
                    var canceled = false;
                    try { await client.SendAsync(new("hang"), cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
                    Check(canceled && !Alive(cancelPid), "取消正在执行的请求会结束其工作进程");
                }
                current = await client.SendAsync(new("echo")); var crashPid = current!.Pid;
                Check(await client.SendAsync(new("crash")) == null && !Alive(crashPid), "工作进程异常退出返回失败并释放连接");
                await Task.Delay(200); current = await client.SendAsync(new("echo"));
                Check(current is { Ok: true }, "异常退出后可以重新建立连接");
                var idlePid = current!.Pid;
                var idleClock = Stopwatch.StartNew();
                await Until(() => !Alive(idlePid) && client.WorkerPid == null, TimeSpan.FromSeconds(4));
                measurements["idleExitMs"] = idleClock.Elapsed.TotalMilliseconds;
                Check(!Alive(idlePid) && client.WorkerPid == null, "空闲工作进程自动退出并释放占用");
                current = await client.SendAsync(new("echo"));
                Check(current is { Ok: true } && current.Pid != idlePid, "空闲退出后按需重新创建工作进程");
                var disposePid = current!.Pid;
                hang = client.SendAsync(new("hang")); await Task.Delay(50); clock.Restart(); client.Dispose();
                Check(clock.Elapsed < TimeSpan.FromMilliseconds(100), "关闭客户端不等待卡住的调用，不阻塞界面");
                await hang; await Until(() => !Alive(disposePid), TimeSpan.FromSeconds(3));
                Check(!Alive(disposePid) && await client.SendAsync(new("echo")) == null, "关闭后清理进程并拒绝新请求");

                var invalid = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(invalid, ShellProtocol.MaximumFrameBytes + 1);
                var rejected = false;
                try { await ShellProtocol.ReadAsync<ShellReply>(new MemoryStream(invalid), default); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "IPC 在分配载荷之前拒绝超长消息");
                rejected = false;
                try { ShellProtocol.Decode(new(Pixels: [1], Width: 128, Height: 128)); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "IPC 拒绝错误像素尺寸和载荷长度");
                rejected = false;
                try { await ShellProtocol.ReadAsync<ShellReply>(new MemoryStream([8, 0, 0, 0, 1]), default); } catch (EndOfStreamException) { rejected = true; }
                Check(rejected, "IPC 截断消息立即失败，不等待无期限数据");

                var ownerResult = Path.Combine(fixture, "owner.json");
                using var owner = Process.Start(ShellWorkerClient.CreateStartInfo(["--shell-owner-self-test", ownerResult]))!;
                try
                {
                    await Until(() => File.Exists(ownerResult), TimeSpan.FromSeconds(8));
                    using var record = JsonDocument.Parse(File.ReadAllText(ownerResult));
                    var childPid = record.RootElement.GetProperty("workerPid").GetInt32();
                    Check(record.RootElement.GetProperty("ownerPid").GetInt32() == owner.Id && Alive(childPid), "退出保护验证只使用本次创建的测试父子进程");
                    owner.Kill(); await owner.WaitForExitAsync();
                    await Until(() => !Alive(childPid), TimeSpan.FromSeconds(3));
                    Check(!Alive(childPid), "所属进程被强制结束后挂起中的工作进程自动退出");
                }
                finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }

                var result = new { passed = true, count = checks.Count, checks, measurements, desktopTakeover = false, inputSimulation = false };
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(fixture, "result.json"), json); File.WriteAllText(Path.Combine(root, "latest-result.json"), json);
            }
            catch (Exception ex)
            {
                exit = 1; var json = JsonSerializer.Serialize(new { passed = false, checks, measurements, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(fixture, "error.txt"), ex.ToString()); File.WriteAllText(Path.Combine(root, "latest-result.json"), json);
            }
            finally { timer.Stop(); if (window != null) { window.Exiting = true; window.Close(); } app.Shutdown(); }
        };
        app.Run(); return exit;
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; }
    }
    private static async Task Until(Func<bool> done, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.Elapsed > limit) throw new TimeoutException("工作进程验证未在期限内完成。"); await Task.Delay(25); }
    }
}
