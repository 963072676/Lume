using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lume.Core;

internal static class ReliabilityTests
{
    private static void Check(bool value) { if (!value) throw new Exception("可靠性回归断言失败"); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("应拒绝此操作：" + typeof(T).Name);
    }
    private sealed class Pump : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Enqueue((callback, state));
        public void Complete(Task task)
        {
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted)
            {
                if (queue.TryDequeue(out var work)) work.Callback(work.State);
                else { if (DateTime.UtcNow > limit) throw new TimeoutException("异步测试未完成"); Thread.Sleep(1); }
            }
            task.GetAwaiter().GetResult();
        }
    }
    public static void Register(Action<string, Action> test, string root)
    {
        test("AI 后台核对后整批应用且可撤销", () =>
        {
            var folder = Path.Combine(root, "async-ai"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "a.xyz"); File.WriteAllText(path, "fixture");
            var organizer = new Organizer(new StateStore(Path.Combine(folder, "state.json")), AppState.Create([]));
            organizer.ApplyScan(DesktopScanner.Scan([], [path])); var snapshot = organizer.CaptureAiSnapshot();
            var pump = new Pump(); var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                pump.Complete(organizer.ApplyAiSuggestionsAsync(snapshot, [new(snapshot.Items[0].Id, "apps", "test", .9)]));
                Check(organizer.CollectionOf(organizer.Files[0]) == "apps");
                organizer.Undo(); Check(organizer.CollectionOf(organizer.Files[0]) == "inbox");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        test("AI 后台核对取消或快捷目标超时整批不提交", () =>
        {
            var folder = Path.Combine(root, "failed-ai"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "a.url"); File.WriteAllText(path, "fixture");
            var organizer = new Organizer(new StateStore(Path.Combine(folder, "state.json")), AppState.Create([]));
            organizer.ApplyScan(DesktopScanner.Scan([], [path], _ => null)); var snapshot = organizer.CaptureAiSnapshot();
            var originalCollection = organizer.CollectionOf(organizer.Files[0]);
            var selected = new[] { new AiSuggestion(snapshot.Items[0].Id, "apps", "test", .9) }; var history = organizer.State.History.Count;
            var pump = new Pump(); var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                Reject<TimeoutException>(() => pump.Complete(organizer.ApplyAiSuggestionsAsync(snapshot, selected, readShortcut: _ => throw new TimeoutException())));
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                Reject<OperationCanceledException>(() => pump.Complete(organizer.ApplyAiSuggestionsAsync(snapshot, selected, cancellation: cancel.Token)));
                Check(organizer.State.History.Count == history && organizer.CollectionOf(organizer.Files[0]) == originalCollection);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        test("AI 后台核对拒绝期间发生的手动归类", () =>
        {
            var folder = Path.Combine(root, "stale-ai"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "a.txt"); File.WriteAllText(path, "fixture");
            var organizer = new Organizer(new StateStore(Path.Combine(folder, "state.json")), AppState.Create([]));
            organizer.ApplyScan(DesktopScanner.Scan([], [path])); var snapshot = organizer.CaptureAiSnapshot();
            var pump = new Pump(); var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                var work = organizer.ApplyAiSuggestionsAsync(snapshot, [new(snapshot.Items[0].Id, "apps", "test", .9)]);
                organizer.Assign(path, "work"); var history = organizer.State.History.Count;
                Reject<InvalidOperationException>(() => pump.Complete(work));
                Check(organizer.State.History.Count == history && organizer.CollectionOf(organizer.Files[0]) == "work");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        var now = DateTime.UtcNow;
        DesktopFile Item(string name) => new(Path.Combine(root, name), name, Path.GetExtension(name), 1024, now, now, false, "未知来源");
        Rule RuleOf(string value, string op = "in") => new("reliability-rule", "回归规则", "work", [new(op == "in" ? "extension" : "name", op, value)]);
        (string Directory, StateStore Store, Organizer Organizer) Create(string name)
        {
            var directory = Path.Combine(root, "reliability-" + name);
            var store = new StateStore(Path.Combine(directory, "state.json")); var organizer = new Organizer(store, AppState.Create([root]));
            organizer.AddCollection("备份测试"); return (directory, store, organizer);
        }
        void MakeZip(string path, params (string Name, string Text)[] files)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open(), new UTF8Encoding(false)); writer.Write(file.Text);
            }
            var manifest = new { Schema = 1, CreatedUtc = now, ProductVersion = "test", Files = files.Select(f => new
                { Name = f.Name, Bytes = Encoding.UTF8.GetByteCount(f.Text), Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Text))) }).ToList() };
            using var output = zip.CreateEntry("lume-backup.json").Open(); JsonSerializer.Serialize(output, manifest);
        }

        test("完整备份覆盖分段历史外观和 AI 设置并排除运行日志", () =>
        {
            var (directory, store, organizer) = Create("full-backup");
            for (var i = 0; i < 205; i++) organizer.State.History.Add(new() { Title = "backup-history-" + i });
            store.Save(organizer.State); organizer.SetTheme("sage");
            LayoutBackup.Write(Path.Combine(directory, "layout-before-restore.json"), organizer.State.Desktop);
            File.WriteAllText(Path.Combine(directory, "ai-settings.json"), "opaque-protected-settings");
            File.WriteAllText(Path.Combine(directory, "desktop-lease.json"), "runtime-only");
            Directory.CreateDirectory(Path.Combine(directory, "diagnostics")); File.WriteAllText(Path.Combine(directory, "diagnostics", "sample.json"), "runtime-only");
            Directory.CreateDirectory(Path.Combine(directory, "archive-journal")); File.WriteAllText(Path.Combine(directory, "archive-journal", Guid.NewGuid().ToString("N") + ".json.corrupt-keep"), "preserved-original");
            var backup = Path.Combine(root, "complete.lume-backup.zip"); var info = DataBackup.Export(directory, backup, "test");
            Check(info.Collections == 6 && info.HistoryEntries == 206 && info.ProductVersion == "test");
            using var zip = ZipFile.OpenRead(backup);
            Check(zip.Entries.Any(e => e.FullName.StartsWith("state.json.history/")) && zip.GetEntry("ai-settings.json") != null);
            Check(zip.GetEntry("desktop-lease.json") == null && zip.Entries.All(e => !e.FullName.StartsWith("diagnostics/")));
            var restored = Path.Combine(root, "complete-restored"); Check(DataBackup.RestoreOffline(restored, backup) == "");
            var loadedStore = new StateStore(Path.Combine(restored, "state.json")); var loaded = loadedStore.Load([]);
            Check(loaded.Desktop.Theme == "sage" && loaded.History.Count + loaded.HistoryArchives.Sum(c => c.Count) == 206);
            var history = Path.Combine(root, "complete-history.json"); loadedStore.ExportHistory(loaded, history);
            Check(JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(history))!.Count == 206);
            Check(File.ReadAllText(Path.Combine(restored, "ai-settings.json")) == "opaque-protected-settings");
            Check(File.ReadAllBytes(Path.Combine(restored, "layout-before-restore.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "layout-before-restore.json"))));
        });
        test("完整恢复先校验再替换并保留当前整份数据", () =>
        {
            var (directory, store, organizer) = Create("restore"); var backup = Path.Combine(root, "restore.lume-backup.zip");
            DataBackup.Export(directory, backup, "test"); organizer.AddCollection("后来新增");
            File.WriteAllText(Path.Combine(directory, "local-extra.txt"), "keep-current-extra");
            var physical = Path.Combine(root, "restore-physical.txt"); File.WriteAllText(physical, "untouched");
            var preserved = DataBackup.RestoreOffline(directory, backup);
            Check(store.Load([]).Configuration.Collections.Count == 6);
            Check(new StateStore(Path.Combine(preserved, "state.json")).Load([]).Configuration.Collections.Count == 7);
            Check(File.ReadAllText(Path.Combine(preserved, "local-extra.txt")) == "keep-current-extra" && File.ReadAllText(physical) == "untouched");
        });
        test("篡改备份恢复失败不改变现有数据", () =>
        {
            var (directory, store, _) = Create("tampered"); var before = File.ReadAllBytes(store.Path);
            var backup = Path.Combine(root, "tampered.zip"); DataBackup.Export(directory, backup, "test");
            using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("state.json")!; byte[] data;
                using (var input = entry.Open()) { using var copy = new MemoryStream(); input.CopyTo(copy); data = copy.ToArray(); }
                data[0] ^= 1; entry.Delete(); using var output = zip.CreateEntry("state.json").Open(); output.Write(data);
            }
            Reject<InvalidDataException>(() => DataBackup.RestoreOffline(directory, backup));
            Check(File.ReadAllBytes(store.Path).SequenceEqual(before));
        });
        test("备份路径穿越和大小写重复项均拒绝", () =>
        {
            var path = Path.Combine(root, "traversal.zip"); MakeZip(path, ("../outside.json", "{}"));
            Reject<InvalidDataException>(() => DataBackup.Inspect(path)); Check(!File.Exists(Path.Combine(root, "outside.json")));
            var duplicate = Path.Combine(root, "duplicate.zip"); MakeZip(duplicate, ("state.json", "{}"), ("STATE.JSON", "{}"));
            Reject<InvalidDataException>(() => DataBackup.Inspect(duplicate));
        });
        test("缺少历史依赖和数据目录内备份均拒绝", () =>
        {
            var (directory, _, _) = Create("missing-history");
            Reject<IOException>(() => DataBackup.Export(directory, Path.Combine(directory, "backup.zip"), "test"));
            var state = AppState.Create([]); state.HistoryFile = new string('a', 64) + ".json";
            var bad = Path.Combine(root, "missing-history.zip"); MakeZip(bad, ("state.json", JsonSerializer.Serialize(state)));
            Reject<InvalidDataException>(() => DataBackup.Inspect(bad));
        });

        test("归档损坏主日志使用有效备份并保留原件且不移动文件", () =>
        {
            var folder = Path.Combine(root, "journal-backup"); var source = Path.Combine(folder, "source"); var target = Path.Combine(folder, "target");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "keep.txt"), "same"); File.WriteAllText(Path.Combine(target, "keep.txt"), "same");
            var journal = Path.Combine(folder, "journal"); var service = new ArchiveService(journal);
            var batch = service.PreviewAsync(DesktopScanner.Scan([source]).Files, [source], target, false).GetAwaiter().GetResult();
            batch.Items[0].Status = "DestinationReady"; batch.Items[0].Length = 4; batch.Items[0].Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("same")));
            Directory.CreateDirectory(journal); var path = Path.Combine(journal, batch.Id.ToUpperInvariant() + ".json");
            File.WriteAllText(path + ".bak", JsonSerializer.Serialize(batch)); File.WriteAllText(path, "{broken-original");
            service.RecoverAsync().GetAwaiter().GetResult(); Check(service.Warnings.Any(w => w.Kind == "backup"));
            Check(service.History().Single().Items[0].Status == "Review");
            Check(Directory.GetFiles(journal, "*.corrupt-*").Any(p => File.ReadAllText(p) == "{broken-original"));
            Check(File.ReadAllText(Path.Combine(source, "keep.txt")) == "same" && File.ReadAllText(Path.Combine(target, "keep.txt")) == "same");
        });
        test("双损坏归档日志隔离而其他批次仍可读取与恢复", () =>
        {
            var journal = Path.Combine(root, "journal-isolation"); Directory.CreateDirectory(journal); var service = new ArchiveService(journal);
            var good = new ArchiveBatch { DestinationRoot = Path.Combine(root, "journal-good-target") };
            File.WriteAllText(Path.Combine(journal, good.Id + ".json"), JsonSerializer.Serialize(good));
            var path = Path.Combine(journal, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "also-broken");
            service.RecoverAsync().GetAwaiter().GetResult(); Check(service.Warnings.Count == 1 && service.History().Single().Id == good.Id);
            Check(File.ReadAllText(path) == "broken" && File.ReadAllText(path + ".bak") == "also-broken");
        });
        test("归档日志身份不一致和非法状态被隔离", () =>
        {
            var journal = Path.Combine(root, "journal-invalid"); Directory.CreateDirectory(journal); var service = new ArchiveService(journal);
            var batch = new ArchiveBatch { DestinationRoot = Path.Combine(root, "invalid-target") };
            var path = Path.Combine(journal, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(batch));
            batch.Items.Add(new() { Source = Path.Combine(root, "a.txt"), Destination = Path.Combine(batch.DestinationRoot, "a.txt"), Status = "Unknown", Sha256 = new string('A', 64) });
            File.WriteAllText(Path.Combine(journal, batch.Id + ".json"), JsonSerializer.Serialize(batch));
            service.RecoverAsync().GetAwaiter().GetResult(); Check(service.History().Count == 0 && service.Warnings.Count == 2);
        });

        test("预处理缓存检测原位条件更改且不影响关键词语义", () =>
        {
            var rule = RuleOf(".PNG，jpg;WEBP"); var png = Item("a.PNG"); Check(RuleEngine.Matches(png, rule, now));
            rule.Conditions[0] = new("extension", "in", "pdf"); Check(!RuleEngine.Matches(png, rule, now));
            rule.Conditions.Clear(); Check(!RuleEngine.Matches(png, rule, now));
            rule.Conditions.Add(new("name", "contains", "文，圖;项目")); Check(RuleEngine.Matches(Item("项目.txt"), rule, now));
        });
        test("正则后向引用与环视兼容且灾难回溯模式可处理", () =>
        {
            Check(RuleEngine.Matches(Item("aaaa.txt"), RuleOf(@"^(a+)\1\.", "regex"), now));
            Check(RuleEngine.Matches(Item("report.txt"), RuleOf(@"(?=report)report", "regex"), now));
            Check(!RuleEngine.Matches(Item(new string('a', 120) + "!"), RuleOf("^(a+)+$", "regex"), now));
        });
        test("批量归类与逐项归类一致且快照隔离手动归属与条件", () =>
        {
            var config = AppState.Create([]).Configuration; var files = new[] { Item("a.md"), Item("b.PNG"), Item("c.zzz") };
            config.Overrides[files[0].Path] = "inbox"; var snapshot = RuleEngine.ClassificationSnapshot(config);
            var expected = files.Select(f => RuleEngine.Classify(f, config, now)).ToArray();
            config.Overrides[files[0].Path] = "work"; config.Rules[0].Conditions.Clear();
            Check(RuleEngine.ClassifyFiles(files, snapshot, now).SequenceEqual(expected));
        });
        test("归类与预览支持取消和整批超时并不部分写入", () =>
        {
            var config = AppState.Create([]).Configuration; var files = new[] { Item("a.md") };
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Reject<OperationCanceledException>(() => RuleEngine.ClassifyFiles(files, config, now, cancel.Token));
            Reject<TimeoutException>(() => RuleEngine.ClassifyFiles(files, config, now, budget: TimeSpan.Zero));
            Reject<TimeoutException>(() => RuleEngine.Preview(files, config, RuleOf("md"), now, budget: TimeSpan.Zero));
            var (_, _, organizer) = Create("async-cancel"); var history = organizer.State.History.Count;
            Reject<OperationCanceledException>(() => organizer.ApplyScanAsync(new([.. files], []), cancellation: cancel.Token).GetAwaiter().GetResult());
            Check(organizer.State.History.Count == history && organizer.State.Assignments.Count == 0);
        });
        test("异步规则保存保持手动优先级并可撤销且取消不保存", () =>
        {
            var (_, store, organizer) = Create("async-save"); var files = new[] { Item("async.zzz"), Item("fixed.zzz") };
            organizer.ApplyScan(new([.. files], [])); organizer.Assign(files[1].Path, "screenshots");
            organizer.SaveRuleAsync(RuleOf("zzz")).GetAwaiter().GetResult();
            Check(organizer.CollectionOf(files[0]) == "work" && organizer.CollectionOf(files[1]) == "screenshots");
            Check(store.Load([]).Configuration.Rules[0].Id == "reliability-rule"); organizer.Undo(); Check(organizer.CollectionOf(files[0]) == "inbox");
            var before = File.ReadAllBytes(store.Path); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Reject<OperationCanceledException>(() => organizer.SaveRuleAsync(RuleOf("zzz"), cancel.Token).GetAwaiter().GetResult());
            Check(File.ReadAllBytes(store.Path).SequenceEqual(before));
        });
        test("异步扫描丢弃过期结果而不覆盖刚保存的规则", () =>
        {
            var (_, _, organizer) = Create("async-stale"); organizer.State.Configuration.Rules = [RuleOf("(?=.)^(a+)+$", "regex")];
            var files = Enumerable.Range(0, 5).Select(i => Item(new string('a', 120) + "!" + i)).ToArray();
            var previous = SynchronizationContext.Current; var pump = new Pump(); SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                var pending = organizer.ApplyScanAsync(new([.. files], []));
                organizer.SaveRule(RuleOf("a", "starts")); pump.Complete(pending);
                Check(pending.Result == ScanApplyResult.Stale && files.All(f => organizer.CollectionOf(f) == "work"));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        test("异步规则保存写入失败回滚规则历史和归属", () =>
        {
            var (_, store, organizer) = Create("async-save-failure"); var file = Item("failure.zzz"); organizer.ApplyScan(new([file], []));
            var before = File.ReadAllBytes(store.Path); var history = organizer.State.History.Count;
            using (var locked = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject<IOException>(() => organizer.SaveRuleAsync(RuleOf("zzz")).GetAwaiter().GetResult());
            Check(organizer.CollectionOf(file) == "inbox" && organizer.State.History.Count == history && !organizer.State.Configuration.Rules.Any(r => r.Id == "reliability-rule"));
            Check(File.ReadAllBytes(store.Path).SequenceEqual(before));
        });
        test("异步规则保存遇到并发变更拒绝提交整个过期快照", () =>
        {
            var (_, _, organizer) = Create("async-rule-stale"); var files = Enumerable.Range(0, 5).Select(i => Item(new string('a', 120) + "!" + i)).ToList();
            organizer.ApplyScan(new(files, []), false);
            var previous = SynchronizationContext.Current; var pump = new Pump(); SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                var pending = organizer.SaveRuleAsync(RuleOf("(?=.)^(a+)+$", "regex"));
                organizer.SaveRule(RuleOf("a", "starts")); Reject<InvalidOperationException>(() => pump.Complete(pending));
                Check(organizer.State.Configuration.Rules[0].Conditions[0].Operator == "starts" && files.All(f => organizer.CollectionOf(f) == "work"));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
    }
}
