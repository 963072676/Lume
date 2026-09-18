using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("comctl32.dll")] private static extern void InitCommonControls();
    private static void PumpUntil(Func<bool> done, int timeout = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeout) { Application.DoEvents(); Thread.Sleep(10); }
        if (!done()) throw new Exception("等待超时");
    }
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var checks = new List<string>(); var measurements = new List<object>();
        InitCommonControls();
        var window = CreateWindowExW(0x08000080, "SysListView32", "Lume recovery test", 0x80000000, -16000, 0, 50, 50, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Exception("测试窗口创建失败");
        try
        {
            foreach (var scenario in new[] { "normal", "killed", "was-hidden", "wrong-window-pid", "wrong-window-class", "truncated", "wrong-parent-time", "already-restored" })
            {
                ShowWindow(window, 0);
                var path = Path.Combine(output, scenario + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, "isolated test lease");
                using (var writer = new BinaryWriter(File.Create(path + ".native")))
                {
                    writer.Write(0x31474d4cu); writer.Write(1);
                    if (scenario != "truncated")
                    {
                        writer.Write(window.ToInt64()); writer.Write(scenario == "wrong-window-pid" ? 1u : (uint)Environment.ProcessId); writer.Write(scenario == "was-hidden" ? 0u : 1u);
                        var name = new byte[64]; System.Text.Encoding.ASCII.GetBytes(scenario == "wrong-window-class" ? "TXMiniSkin" : "SysListView32").CopyTo(name, 0); writer.Write(name);
                    }
                }
                using var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe")) { Arguments = "-n 3 127.0.0.1", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true })!;
                var start = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add(parent.Id.ToString()); start.ArgumentList.Add((parent.StartTime.ToUniversalTime().Ticks + (scenario == "wrong-parent-time" ? 1 : 0)).ToString()); start.ArgumentList.Add(path);
                using var guard = Process.Start(start)!;
                try
                {
                    if (scenario == "wrong-parent-time")
                    {
                        PumpUntil(() => guard.HasExited); if (guard.ExitCode != 4 || IsWindowVisible(window) || File.Exists(path + ".ready")) throw new Exception(scenario);
                    }
                    else
                    {
                        PumpUntil(() => File.Exists(path + ".ready") || guard.HasExited);
                        if (guard.HasExited) throw new Exception("保护进程提前退出 " + guard.ExitCode);
                        guard.Refresh(); measurements.Add(new { scenario, privateMiB = guard.PrivateMemorySize64 / 1048576d, workingMiB = guard.WorkingSet64 / 1048576d, handles = guard.HandleCount, cpuSeconds = guard.TotalProcessorTime.TotalSeconds });
                        if (scenario == "already-restored") { File.Delete(path); File.Delete(path + ".native"); }
                        if (scenario != "normal") parent.Kill();
                        PumpUntil(() => guard.HasExited);
                        var shouldShow = scenario is "normal" or "killed";
                        PumpUntil(() => IsWindowVisible(window) == shouldShow);
                        if (guard.ExitCode != (scenario == "truncated" ? 6 : 0)) throw new Exception(scenario + " exit=" + guard.ExitCode);
                        if (File.Exists(path + ".ready")) throw new Exception("未清理就绪标记");
                        if (scenario == "truncated" && !File.Exists(path)) throw new Exception("损坏租约未保留");
                    }
                    checks.Add(scenario); Console.WriteLine("PASS " + scenario);
                }
                finally { if (!parent.HasExited) parent.Kill(); if (!guard.HasExited) { guard.Kill(); guard.WaitForExit(); } }
            }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, measurements }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { DestroyWindow(window); }
    }
}
