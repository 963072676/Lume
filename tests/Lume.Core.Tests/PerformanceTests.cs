using Lume.Core;

internal static class PerformanceTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        void Check(bool value) { if (!value) throw new Exception("性能回归断言失败"); }
        test("无文件变化时诊断仍更新且不新增归类历史", () =>
        {
            var organizer = new Organizer(new StateStore(Path.Combine(root, "warning-only", "state.json")), AppState.Create([]));
            Check(!organizer.ApplyScan(new([], ["目录不可访问"]), false));
            Check(organizer.Warnings.Count == 1 && organizer.State.History.Count == 0);
            Check(!organizer.ApplyScan(new([], []), false));
            Check(organizer.Warnings.Count == 0 && organizer.State.History.Count == 0);
        });
        test("目录增量扫描只更新事件目录且完整校验恢复丢失事件", () =>
        {
            var a = Path.Combine(root, "incremental-a"); var b = Path.Combine(root, "incremental-b");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "a.txt"), "a"); File.WriteAllText(Path.Combine(b, "b.txt"), "b");
            var scanner = new DesktopScanSession();
            var first = scanner.Scan([a, b], [], [], true);
            Check(first.Files.Count == 2 && scanner.LastScannedDirectories == 2);
            Check(ReferenceEquals(first, scanner.Scan([a, b], [], [], false)) && scanner.LastScannedDirectories == 0);
            File.WriteAllText(Path.Combine(a, "new.txt"), "new"); File.WriteAllText(Path.Combine(b, "missed.txt"), "missed");
            Check(scanner.Scan([a, b], [], [a], false).Files.Count == 3 && scanner.LastScannedDirectories == 1);
            Check(scanner.Scan([a, b], [], [], true).Files.Count == 4);
            File.Delete(Path.Combine(a, "new.txt"));
            Check(scanner.Scan([a, b], [], [a], false).Files.Count == 3);
            Check(scanner.Scan([a], [], [], false).Files.Count == 1);
        });
        test("快捷方式缓存复用且元数据变化和完整扫描重新读取目标", () =>
        {
            var folder = Path.Combine(root, "shortcut-cache"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "a.lnk"); File.WriteAllText(path, "a"); var reads = 0;
            var scanner = new DesktopScanSession(_ => new("target", (++reads).ToString(), ".exe", "file"));
            scanner.Scan([folder], [], [], true); Check(reads == 1);
            scanner.Scan([folder], [], [folder], false); Check(reads == 1);
            File.AppendAllText(path, "changed");
            scanner.Scan([folder], [], [folder], false); Check(reads == 2);
            var scan = scanner.Scan([folder], [], [], true); Check(reads == 3 && scan.Files.Single().Target?.Name == "3");
        });
        test("增量文件引用不导入相邻文件且失联目录可重试", () =>
        {
            var folder = Path.Combine(root, "linked-cache"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "a.txt"); File.WriteAllText(path, "a"); File.WriteAllText(Path.Combine(folder, "other.txt"), "other");
            var missing = Path.Combine(root, "incremental-missing"); var scanner = new DesktopScanSession();
            var first = scanner.Scan([missing], [path], [], true); Check(first.Warnings.Count == 1 && first.Files.Count == 1);
            Directory.CreateDirectory(missing); File.WriteAllText(Path.Combine(missing, "returned.txt"), "returned");
            var scan = scanner.Scan([missing], [path], [missing], false); Check(scan.Warnings.Count == 0 && scan.Files.Count == 2);
            File.Delete(path); Check(scanner.Scan([missing], [path], [folder], false).Files.Count == 1);
        });
        test("手动顺序字典保留首个重复项大小写与未知项目名称顺序", () =>
        {
            var state = AppState.Create([]); var folder = Path.Combine(root, "manual-order");
            var files = new[] { "a.txt", "b.txt", "c.txt", "d.txt" }.Select(n => new DesktopFile(Path.Combine(folder, n), n, ".txt", 1, DateTime.UtcNow, DateTime.UtcNow, false, "unknown")).ToList();
            var organizer = new Organizer(new StateStore(Path.Combine(folder, "state.json")), state); organizer.ApplyScan(new(files, []));
            organizer.SetOptions("work", new(Sort: "manual", Order: [files[2].Path.ToUpperInvariant(), files[1].Path, files[2].Path]));
            Check(organizer.CollectionFiles("work").Select(f => f.Name).SequenceEqual(new[] { "c.txt", "b.txt", "a.txt", "d.txt" }));
        });
    }
}
