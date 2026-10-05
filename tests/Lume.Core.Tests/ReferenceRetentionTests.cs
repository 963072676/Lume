using Lume.Core;

static class ReferenceRetentionTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        (Organizer Organizer, StateStore Store, string Files, string Path, string Backup) Create(string name)
        {
            var folder = Path.Combine(root, "retention-" + name); var files = Path.Combine(folder, "files"); Directory.CreateDirectory(files);
            var store = new StateStore(Path.Combine(folder, "data", "state.json"));
            var state = AppState.Create([files]); var path = Path.Combine(files, "missing.txt");
            state.Assignments[path] = "work"; state.Configuration.Samples.Add(new(path, ".txt", "work")); store.Save(state);
            return (new(store, state), store, files, path, Path.Combine(folder, "before.lume-backup.zip"));
        }
        ReferenceReviewPlan Review(Organizer organizer, DateTime utc, ReferenceObservation[] values, int days = 30) => organizer.ReviewReferences(organizer.CaptureReferenceReview(), values, days, utc);
        ReferenceObservation Missing(string path, string identity = "volume:root:created") => new(path, ReferencePresence.Missing, identity);
        ReferenceReviewPlan Mature(Organizer o, string path)
        { Review(o, now, [Missing(path)]); return Review(o, now.AddDays(30), [Missing(path)]); }
        void Clean(Organizer o, ReferenceReviewPlan plan, string backup,
            Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<ReferenceObservation>>>? recheck = null, CancellationToken token = default) =>
            o.CleanReferencesAsync(plan, backup, "test", recheck ?? ((paths, _) => Task.FromResult<IReadOnlyList<ReferenceObservation>>(paths.Select(p => Missing(p)).ToArray())), token).GetAwaiter().GetResult();
        void Check(bool value) { if (!value) throw new Exception("引用清理断言失败"); }
        void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("期望 " + typeof(T).Name); }

        test("引用清理首次缺失开始计时并在完整30天后提供候选", () =>
        {
            var f = Create("grace"); var first = Review(f.Organizer, now, [Missing(f.Path)]);
            Check(first.Candidates.Count == 0 && first.WaitingCount == 1 && f.Organizer.State.Assignments.ContainsKey(f.Path));
            var restarted = new Organizer(f.Store, f.Store.Load([]));
            Check(Review(restarted, now.AddDays(30).AddTicks(-1), [Missing(f.Path)]).Candidates.Count == 0);
            var mature = Review(restarted, now.AddDays(30), [Missing(f.Path)]);
            Check(mature.Candidates.Single().FirstMissingUtc == now && mature.Candidates.Single().Samples == 1);
        });
        test("引用清理保护手动固定外部引用排序当前文件及未关注目录", () =>
        {
            var f = Create("protected"); var o = f.Organizer;
            string Add(string name) { var p = Path.Combine(f.Files, name); o.State.Assignments[p] = "work"; return p; }
            var pinned = Add("pinned.txt"); o.State.Configuration.Overrides[pinned] = "work";
            var linked = Add("linked.txt"); o.State.Configuration.LinkedFiles.Add(linked);
            var ordered = Add("ordered.txt"); o.SetOptions("work", new(Order: [ordered]));
            var visible = Add("visible.txt"); o.ApplyScan(new([new(visible, "visible.txt", ".txt", 1, now, now, false, "test")], []), false);
            o.State.Assignments[Path.Combine(root, "unmonitored.txt")] = "work";
            var scope = o.CaptureReferenceReview(); Check(scope.Paths.SequenceEqual([f.Path]) && scope.ProtectedCount == 5);
            Check(Mature(o, f.Path).Candidates.Count == 1 && o.State.Configuration.Overrides.ContainsKey(pinned));
        });
        test("无法访问文件返回根目录被替换及时钟回退均重启引用等待期", () =>
        {
            var f = Create("unknown"); var o = f.Organizer; Mature(o, f.Path);
            Check(Review(o, now.AddDays(31), [new(f.Path, ReferencePresence.Unavailable)]).UnavailableCount == 1 && o.State.MissingReferences.Count == 0);
            Check(Review(o, now.AddDays(60), [Missing(f.Path)]).Candidates.Count == 0);
            Check(Review(o, now.AddDays(100), [Missing(f.Path, "other-root")]).Candidates.Count == 0);
            Check(Review(o, now.AddDays(99), [Missing(f.Path, "other-root")]).Candidates.Count == 0);
            Check(Review(o, now.AddDays(140), [new(f.Path, ReferencePresence.Present)]).Candidates.Count == 0 && o.State.MissingReferences.Count == 0);
        });
        test("引用检测缺少身份或缺少结果不会沿用旧缺失时间", () =>
        {
            var f = Create("incomplete"); Mature(f.Organizer, f.Path);
            Check(Review(f.Organizer, now.AddDays(40), [new(f.Path, ReferencePresence.Missing)]).UnavailableCount == 1 && f.Organizer.State.MissingReferences.Count == 0);
            Mature(f.Organizer, f.Path); Check(Review(f.Organizer, now.AddDays(40), []).UnavailableCount == 1 && f.Organizer.State.MissingReferences.Count == 0);
        });
        test("引用检测拒绝重复额外路径非法时间和过时预览", () =>
        {
            var f = Create("invalid"); var o = f.Organizer; var scope = o.CaptureReferenceReview();
            Throws<InvalidDataException>(() => o.ReviewReferences(scope, [Missing(f.Path), Missing(f.Path.ToUpperInvariant())]));
            Throws<InvalidDataException>(() => o.ReviewReferences(scope, [Missing(Path.Combine(f.Files, "extra.txt"))]));
            Throws<ArgumentException>(() => o.ReviewReferences(scope, [], checkedUtc: DateTime.SpecifyKind(now, DateTimeKind.Local)));
            Throws<ArgumentOutOfRangeException>(() => o.ReviewReferences(scope, [], 0));
            o.AddCollection("changed"); Throws<InvalidOperationException>(() => o.ReviewReferences(scope, [Missing(f.Path)]));
        });
        test("引用清理先验证完整备份再复检并可重启后完整撤销", () =>
        {
            var f = Create("undo"); var plan = Mature(f.Organizer, f.Path);
            var physical = Path.Combine(f.Files, "keep.bin"); File.WriteAllText(physical, "原始内容");
            Clean(f.Organizer, plan, f.Backup, (paths, _) =>
            {
                Check(File.Exists(f.Backup) && DataBackup.Inspect(f.Backup).Collections == 5);
                return Task.FromResult<IReadOnlyList<ReferenceObservation>>(paths.Select(p => Missing(p)).ToArray());
            });
            Check(!f.Organizer.State.Assignments.ContainsKey(f.Path) && f.Organizer.State.Configuration.Samples.Count == 0 && f.Store.Load([]).Version == 3);
            var loaded = new Organizer(f.Store, f.Store.Load([])); loaded.Undo();
            Check(loaded.State.Assignments[f.Path.ToUpperInvariant()] == "work" && loaded.State.Configuration.Samples.Single().Path == f.Path && loaded.State.MissingReferences.Count == 0);
            Check(File.ReadAllText(physical) == "原始内容" && loaded.State.History.Single().Undone);
        });
        test("引用清理在归档历史中仍完整撤销失踪文件归属", () =>
        {
            var f = Create("archive"); Clean(f.Organizer, Mature(f.Organizer, f.Path), f.Backup);
            for (var i = 0; i < StateStore.ActiveHistoryLimit; i++) f.Organizer.State.History.Add(new() { Title = "already undone", Undone = true });
            f.Store.Save(f.Organizer.State); var loaded = new Organizer(f.Store, f.Store.Load([]));
            Check(loaded.State.HistoryArchives.Sum(a => a.Undoable) == 1); loaded.Undo();
            Check(loaded.State.Assignments[f.Path] == "work" && loaded.State.Configuration.Samples.Count == 1 && !loaded.CanUndo);
            Check(new Organizer(f.Store, f.Store.Load([])).State.Assignments[f.Path] == "work");
        });
        test("引用清理复检发现文件返回时整批保持原状", () =>
        {
            var f = Create("return"); var plan = Mature(f.Organizer, f.Path);
            Throws<InvalidOperationException>(() => Clean(f.Organizer, plan, f.Backup, (paths, _) => Task.FromResult<IReadOnlyList<ReferenceObservation>>([new(f.Path, ReferencePresence.Present)])));
            Check(File.Exists(f.Backup) && f.Organizer.State.Assignments.ContainsKey(f.Path) && f.Organizer.State.Configuration.Samples.Count == 1 && f.Organizer.State.History.Count == 0);
        });
        test("引用清理备份失败时不执行复检或删除任何记录", () =>
        {
            var f = Create("backup-fail"); var called = false;
            Throws<IOException>(() => Clean(f.Organizer, Mature(f.Organizer, f.Path), Path.Combine(Path.GetDirectoryName(f.Store.Path)!, "inside.zip"), (_, _) => { called = true; return Task.FromResult<IReadOnlyList<ReferenceObservation>>([]); }));
            Check(!called && f.Organizer.State.Assignments.ContainsKey(f.Path) && f.Organizer.State.Configuration.Samples.Count == 1);
        });
        test("引用清理复检期间配置变化拒绝过时计划且保留新配置", () =>
        {
            var f = Create("stale"); var plan = Mature(f.Organizer, f.Path);
            Throws<InvalidOperationException>(() => Clean(f.Organizer, plan, f.Backup, (paths, _) =>
            { f.Organizer.AddCollection("during-check"); return Task.FromResult<IReadOnlyList<ReferenceObservation>>(paths.Select(p => Missing(p)).ToArray()); }));
            Check(f.Organizer.State.Assignments.ContainsKey(f.Path) && f.Organizer.State.Configuration.Collections.Any(c => c.Name == "during-check"));
        });
        test("引用清理预览后新增手动排序也受保护", () =>
        {
            var f = Create("order-race"); var plan = Mature(f.Organizer, f.Path); f.Organizer.SetOptions("work", new(Order: [f.Path]));
            Throws<InvalidOperationException>(() => Clean(f.Organizer, plan, f.Backup)); Check(f.Organizer.State.Assignments.ContainsKey(f.Path));
        });
        test("引用清理保存失败回滚版本归属样本和历史", () =>
        {
            var f = Create("save-fail"); var plan = Mature(f.Organizer, f.Path); FileStream? held = null;
            try
            {
                Throws<IOException>(() => Clean(f.Organizer, plan, f.Backup, (paths, _) =>
                { held = new FileStream(f.Store.Path, FileMode.Open, FileAccess.Read, FileShare.None); return Task.FromResult<IReadOnlyList<ReferenceObservation>>(paths.Select(p => Missing(p)).ToArray()); }));
                Check(f.Organizer.State.Version == 2 && f.Organizer.State.Assignments[f.Path] == "work" && f.Organizer.State.Configuration.Samples.Count == 1 && f.Organizer.State.History.Count == 0);
            }
            finally { held?.Dispose(); }
            Check(f.Store.Load([]).Assignments.ContainsKey(f.Path));
        });
        test("引用清理取消后保留备份且不提交任何清理", () =>
        {
            var f = Create("cancel"); using var cancel = new CancellationTokenSource(); var plan = Mature(f.Organizer, f.Path);
            Throws<OperationCanceledException>(() => Clean(f.Organizer, plan, f.Backup, (paths, _) =>
            { cancel.Cancel(); return Task.FromResult<IReadOnlyList<ReferenceObservation>>(paths.Select(p => Missing(p)).ToArray()); }, cancel.Token));
            Check(File.Exists(f.Backup) && f.Organizer.State.Assignments.ContainsKey(f.Path) && f.Organizer.State.History.Count == 0);
        });
        test("引用清理只含学习样本时也支持持久化和完整撤销", () =>
        {
            var f = Create("sample-only"); f.Organizer.State.Assignments.Remove(f.Path); var plan = Mature(f.Organizer, f.Path);
            Check(!plan.Candidates.Single().Assignment && plan.Candidates.Single().Samples == 1); Clean(f.Organizer, plan, f.Backup);
            var restarted = new Organizer(f.Store, f.Store.Load([])); restarted.Undo();
            Check(restarted.State.Assignments.Count == 0 && restarted.State.Configuration.Samples.Count == 1);
        });
        test("引用清理拒绝其他组织器产生的计划", () =>
        {
            var f = Create("owner"); var other = Create("other-owner");
            Throws<InvalidOperationException>(() => Clean(other.Organizer, Mature(f.Organizer, f.Path), other.Backup));
            Check(!File.Exists(other.Backup) && other.Organizer.State.Assignments.Count == 1);
        });
        test("引用检测期间仅文件快照变化也拒绝旧预览", () =>
        {
            var f = Create("scan-stale"); var scope = f.Organizer.CaptureReferenceReview();
            f.Organizer.ApplyScan(new([], []), false);
            Throws<InvalidOperationException>(() => f.Organizer.ReviewReferences(scope, [Missing(f.Path)]));
        });
        test("引用清理前完整备份可还原旧格式及所有被移除记录", () =>
        {
            var f = Create("rollback-format"); Clean(f.Organizer, Mature(f.Organizer, f.Path), f.Backup);
            Check(f.Store.Load([]).Version == 3);
            var destination = Path.Combine(Path.GetDirectoryName(f.Backup)!, "restored"); DataBackup.RestoreOffline(destination, f.Backup);
            var restored = new StateStore(Path.Combine(destination, "state.json")).Load([]);
            Check(restored.Version == 2 && restored.Assignments[f.Path] == "work" && restored.Configuration.Samples.Count == 1 && restored.History.Count == 0);
        });
    }
}
