using Lume.Core;

internal static class ShellScanTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("Shell 扫描回归断言失败。"); }
        string Folder(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        test("快捷目标超时保留文件且失败不缓存，后续扫描可以恢复", () =>
        {
            var folder = Folder("target-timeout"); var path = Path.Combine(folder, "a.lnk"); File.WriteAllText(path, "fixture");
            var reads = 0;
            var scanner = new DesktopScanSession(_ => { if (++reads == 1) throw new TimeoutException(); return new("target", "app", ".exe", "file"); });
            var failed = scanner.Scan([folder], [], [], true);
            Check(failed.Files.Single().Target == null && failed.Warnings.Count == 1 && failed.DeferredTargets!.Single() == path);
            Check(scanner.LastShortcutFailures == 1);
            var recovered = scanner.Scan([folder], [], [folder], false);
            Check(reads == 2 && recovered.Files.Single().Target?.Name == "app" && recovered.Warnings.Count == 0 && recovered.DeferredTargets!.Count == 0);
        });
        test("完整扫描读取失败仅复用未改变的快捷方式目标", () =>
        {
            var folder = Folder("target-fallback"); var path = Path.Combine(folder, "a.lnk"); File.WriteAllText(path, "fixture");
            var fail = false; var target = new ShortcutTarget("target", "app", ".exe", "file");
            var scanner = new DesktopScanSession(_ => fail ? throw new TimeoutException() : target);
            Check(scanner.Scan([folder], [], [], true).Files.Single().Target == target);
            fail = true; Check(scanner.Scan([folder], [], [], true).Files.Single().Target == target);
            File.AppendAllText(path, "changed");
            Check(scanner.Scan([folder], [], [folder], false).Files.Single().Target == null);
            fail = false; Check(scanner.Scan([folder], [], [folder], false).Files.Single().Target == target);
        });
        test("目标信息整批预算耗尽后保留所有文件且合并提示", () =>
        {
            var folder = Folder("target-budget");
            for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(folder, i + ".lnk"), "fixture");
            var reads = 0; var scanner = new DesktopScanSession(_ => { reads++; return null; });
            var result = scanner.Scan([folder], [], [], true, targetBudget: TimeSpan.Zero);
            Check(reads == 0 && result.Files.Count == 100 && result.Warnings.Count == 1 && result.DeferredTargets!.Count == 100 && scanner.LastShortcutFailures == 100);
            var bounded = new DesktopScanSession((_, remaining) => { Check(remaining > TimeSpan.Zero && remaining <= TimeSpan.FromSeconds(1)); return null; });
            Check(bounded.Scan([folder], [], [], true, targetBudget: TimeSpan.FromSeconds(1)).Warnings.Count == 0);
        });
        test("扫描取消不会提交部分目录快照", () =>
        {
            var a = Folder("cancel-target-a"); var b = Folder("cancel-target-b"); var path = Path.Combine(a, "a.lnk"); File.WriteAllText(path, "fixture");
            using var cancel = new CancellationTokenSource(); var cancelOnRead = false;
            var scanner = new DesktopScanSession(_ => { if (cancelOnRead) cancel.Cancel(); return null; });
            var before = scanner.Scan([a, b], [], [], true);
            File.WriteAllText(Path.Combine(b, "new.txt"), "new"); cancelOnRead = true;
            var canceled = false;
            try { scanner.Scan([a, b], [], [], true, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && ReferenceEquals(before, scanner.Scan([a, b], [], [], false)));
            cancelOnRead = false; Check(scanner.Scan([a, b], [], [], true).Files.Count == 2);
        });
        test("目标读取失败不覆盖已有归类，恢复后正常归类且不持久化临时覆盖", () =>
        {
            var folder = Folder("deferred-classification"); var path = Path.Combine(folder, "a.lnk");
            var state = AppState.Create([]); state.Configuration.Rules.Clear(); state.Configuration.Rules.Add(new("target-rule", "应用目标", "apps", [new("targetName", "contains", "app")]));
            var organizer = new Organizer(new StateStore(Path.Combine(folder, "state.json")), state);
            var file = new DesktopFile(path, "a.lnk", ".lnk", 1, DateTime.UtcNow, DateTime.UtcNow, false, "test", new("target", "app", ".exe", "file"));
            organizer.ApplyScan(new([file], [])); Check(organizer.CollectionOf(file) == "apps");
            organizer.ApplyScan(new([file with { Target = null }], ["deferred"], [path]));
            Check(organizer.CollectionOf(file) == "apps" && state.Configuration.Overrides.Count == 0);
            organizer.Assign(path, "work"); Check(organizer.CollectionOf(file) == "work");
            organizer.Undo(); Check(organizer.CollectionOf(file) == "apps" && state.Configuration.Overrides.Count == 0);
            organizer.Assign(path, "work"); organizer.Release(path);
            Check(organizer.CollectionOf(file) == "apps" && state.Configuration.Overrides.Count == 0);
            organizer.ApplyScan(new([file], [])); Check(organizer.CollectionOf(file) == "apps");
            organizer.ApplyScan(new([file with { Target = null }], [])); Check(organizer.CollectionOf(file) == "inbox");
        });
        test("导入快捷方式先保存手动引用而不执行目标读取", () =>
        {
            var folder = Folder("import-shortcut"); var path = Path.Combine(folder, "a.lnk"); File.WriteAllText(path, "fixture");
            var organizer = new Organizer(new StateStore(Path.Combine(folder, "state.json")), AppState.Create([]));
            organizer.AssignMany([path], "apps");
            Check(organizer.Files.Single().Target == null && organizer.CollectionOf(organizer.Files.Single()) == "apps" && organizer.State.Configuration.LinkedFiles.Contains(path));
        });
    }
}
