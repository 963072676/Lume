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
            foreach (var collections in new[] { 5, 30, 60 })
            {
                var state = AppState.Create([]); var now = DateTime.UtcNow; const int count = 10000;
                state.Configuration.Collections = Enumerable.Range(0, collections).Select(i => new Collection(i == 0 ? "inbox" : "group-" + i, "group " + i, "#92C7B5")).ToList();
                state.Configuration.Rules.Clear();
                var files = Enumerable.Range(0, count).Select(i => new DesktopFile(Path.Combine(root, $"query-{i:D5}.txt"), $"query-{i:D5}.txt", ".txt", i, now, now, false, "unknown")).ToList();
                var organizer = new Organizer(new StateStore(Path.Combine(root, $"query-{collections}.json")), state); organizer.ApplyScan(new(files, []), false);
                for (var i = 0; i < files.Count; i++) state.Assignments[files[i].Path] = state.Configuration.Collections[i % collections].Id;
                var ids = state.Configuration.Collections.Select(c => c.Id).ToArray();
                var measurement = Measure(() =>
                {
                    var result = organizer.QueryCollections(ids, "query txt");
                    if (result.Sum(p => p.Value.Count) != count) throw new InvalidOperationException("跨分区查询丢失文件");
                });
                var ok = measurement.Milliseconds < 100 && measurement.Bytes < 1024 * 1024; passed &= ok;
                rows.Add(new { scenario = "collection-query", count, collections, medianMs = measurement.Milliseconds, allocatedBytes = measurement.Bytes, maxMs = 100, maxAllocatedBytes = 1024 * 1024, passed = ok });
                var cold = Measure(() =>
                {
                    organizer.ApplyScan(new(files, []), false);
                    if (organizer.QueryCollections(ids, "query txt").Sum(p => p.Value.Count) != count) throw new InvalidOperationException("初次查询丢失文件");
                });
                var coldOk = cold.Milliseconds < 500 && cold.Bytes < 8 * 1024 * 1024; passed &= coldOk;
                rows.Add(new { scenario = "collection-query-cold", count, collections, medianMs = cold.Milliseconds, allocatedBytes = cold.Bytes, maxMs = 500, maxAllocatedBytes = 8 * 1024 * 1024, passed = coldOk });
            }
            {
                var now = DateTime.UtcNow; var config = AppState.Create([]).Configuration;
                config.Rules = Enumerable.Range(0, 30).Select(i => new Rule("bench-" + i, "synthetic", "work", [new("extension", "in", $"fake{i},never{i}")])).ToList();
                var files = Enumerable.Range(0, 5000).Select(i => new DesktopFile(Path.Combine(root, $"file-{i}.data"), $"file-{i}.data", ".data", 1, now, now, false, "unknown")).ToList();
                foreach (var batch in new[] { false, true })
                {
                    var measurement = Measure(() =>
                    {
                        if (batch) { if (RuleEngine.ClassifyFiles(files, config, now).Any(r => r != "inbox")) throw new InvalidOperationException("批量归类结果不正确"); }
                        else foreach (var file in files) if (RuleEngine.Classify(file, config, now) != "inbox") throw new InvalidOperationException("预处理归类结果不正确");
                    });
                    var ok = measurement.Milliseconds < 1000 && measurement.Bytes < 4 * 1024 * 1024; passed &= ok;
                    rows.Add(new { scenario = batch ? "batch-rules" : "cached-rules", count = files.Count, rules = 30, medianMs = measurement.Milliseconds, allocatedBytes = measurement.Bytes, maxMs = 1000, maxAllocatedBytes = 4 * 1024 * 1024, passed = ok });
                }
                config.Rules = [new("regex", "synthetic", "work", [new("name", "regex", "^(a+)+$")])];
                files = files.Take(100).Select(f => f with { Name = new string('a', 120) + "!" }).ToList();
                var regex = Measure(() => { foreach (var file in files) if (RuleEngine.Classify(file, config, now) != "inbox") throw new InvalidOperationException("正则结果不正确"); });
                var regexOk = regex.Milliseconds < 1000 && regex.Bytes < 4 * 1024 * 1024; passed &= regexOk;
                rows.Add(new { scenario = "nonbacktracking-regex", count = files.Count, medianMs = regex.Milliseconds, allocatedBytes = regex.Bytes, maxMs = 1000, maxAllocatedBytes = 4 * 1024 * 1024, passed = regexOk });
            }
            var result = new { passed, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = Environment.OSVersion.Version.ToString(), processors = Environment.ProcessorCount, samples = 7, rows };
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(passed ? $"PASS 性能回归（{rows.Count} 场景，7 次采样）" : "FAIL 性能回归，见结果文件");
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
