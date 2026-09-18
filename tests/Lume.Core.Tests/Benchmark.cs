using System.Diagnostics;
using System.Text.Json;
using Lume.Core;

internal static class Benchmark
{
    public static int Run(string output)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lume-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rows = new List<object>();
            var passed = true;
            foreach (var count in new[] { 5000, 10000 })
            {
                var state = AppState.Create([]);
                var organizer = new Organizer(new StateStore(Path.Combine(root, $"sort-{count}.json")), state);
                var now = DateTime.UtcNow;
                var files = Enumerable.Range(0, count).Select(i => new DesktopFile(Path.Combine(root, $"item-{i:D5}.txt"), $"item-{i:D5}.txt", ".txt", 1, now, now, false, "unknown")).ToList();
                organizer.ApplyScan(new(files, []));
                foreach (var file in files) state.Assignments[file.Path] = "work";
                organizer.SetOptions("work", new(Sort: "manual", Order: files.Select(f => f.Path).Reverse().ToList()));
                var measurement = Measure(() =>
                {
                    var sorted = organizer.CollectionFiles("work");
                    if (sorted.Count != count || sorted[0].Path != files[^1].Path) throw new InvalidOperationException("排序结果不正确");
                });
                var ok = measurement.Milliseconds < 1000 && measurement.Bytes < 32 * 1024 * 1024;
                passed &= ok;
                rows.Add(new { scenario = "manual-sort", count, medianMs = measurement.Milliseconds, allocatedBytes = measurement.Bytes, maxMs = 1000, maxAllocatedBytes = 32 * 1024 * 1024, passed = ok });
            }
            foreach (var count in new[] { 0, 1000 })
            {
                var state = AppState.Create([]);
                for (var i = 0; i < count; i++) state.History.Add(new() { Title = "synthetic", Detail = new string('x', 256), PreviousConfiguration = AppState.Create([]).Configuration });
                var store = new StateStore(Path.Combine(root, $"save-{count}.json"));
                store.Save(state);
                var organizer = new Organizer(store, state);
                var enabled = false;
                var measurement = Measure(() => { enabled = !enabled; organizer.SetSnap(enabled); });
                var ok = measurement.Milliseconds < 1000 && measurement.Bytes < 1024 * 1024;
                passed &= ok;
                rows.Add(new { scenario = "settings-save", historyCount = count, medianMs = measurement.Milliseconds, allocatedBytes = measurement.Bytes, maxMs = 1000, maxAllocatedBytes = 1024 * 1024, passed = ok });
            }
            var result = new { passed, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = Environment.OSVersion.Version.ToString(), processors = Environment.ProcessorCount, samples = 7, rows };
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(passed ? "PASS 性能回归（4 场景，7 次采样）" : "FAIL 性能回归，见结果文件");
            return passed ? 0 : 1;
        }
        finally
        {
            // Only remove this run's fresh synthetic fixture, never caller-supplied paths.
            Directory.Delete(root, true);
        }
    }

    private static (double Milliseconds, long Bytes) Measure(Action action)
    {
        action(); action();
        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var i = 0; i < 7; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew(); action(); watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before));
        }
        return (samples.OrderBy(s => s.Milliseconds).ElementAt(3).Milliseconds, samples.OrderBy(s => s.Bytes).ElementAt(3).Bytes);
    }
}
