using Lume.Core;

static class CollectionQueryTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        var now = DateTime.UtcNow;
        DesktopFile Item(string folder, string name, int age = 0, long size = 1, bool directory = false) => new(Path.Combine(folder, name), name, Path.GetExtension(name), size, now.AddDays(-age), now.AddDays(-age), directory, "未知来源");
        Organizer Create(string name) => new(new StateStore(Path.Combine(root, "query-test-" + name, "state.json")), AppState.Create([]));
        void Check(bool value) { if (!value) throw new Exception("集合查询断言失败"); }
        void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("期望 " + typeof(T).Name); }

        test("批量查询保持映射与手动固定的重叠可见性", () =>
        {
            var o = Create("mapped"); var folder = Path.Combine(root, "mapped-query");
            o.State.Configuration.Collections.AddRange([new("map-a", "A", "#92C7B5", MappedPath: folder), new("map-b", "B", "#92C7B5", MappedPath: folder.ToUpperInvariant())]);
            var mapped = Item(folder, "a.txt"); var pinned = Item(folder, "b.txt"); var outside = Item(root, "c.txt");
            o.ApplyScan(new([mapped, pinned, outside], [])); o.Assign(pinned.Path, "work");
            var result = o.QueryCollections(["map-a", "work", "map-b"]);
            Check(result["map-a"].Count == 2 && result["map-b"].Count == 2 && result["work"].Select(f => f.Name).SequenceEqual(["b.txt", "c.txt"]));
            o.Release(pinned.Path); Check(o.CollectionFiles("work").Single().Path == outside.Path);
        });
        test("最近文件先取40项再筛选且忽略文件夹与排序设置", () =>
        {
            var o = Create("recent"); o.AddRecentCollection(); var id = o.State.Configuration.Collections.Single(c => c.Recent).Id;
            var files = Enumerable.Range(0, 50).Select(i => Item(root, $"recent-{i:D2}.txt", i)).ToList(); files.Add(Item(root, "目录", -1, directory: true));
            o.ApplyScan(new(files, [])); o.SetOptions(id, new(Sort: "size", Descending: true));
            Check(o.CollectionFiles(id).Count == 40 && o.CollectionFiles(id).First().Name == "recent-00.txt");
            Check(o.CollectionFiles(id, "recent-49").Count == 0 && o.CollectionFiles("work", "recent-49").Count == 1);
        });
        test("批量查询复用后文件新建删除和元数据更新仍反映当前扫描", () =>
        {
            var o = Create("scan"); var first = Item(root, "first-query.txt", size: 1); o.ApplyScan(new([first], []));
            var snapshot = o.CollectionFiles("work"); Check(snapshot.Single().Size == 1);
            var changed = first with { Size = 88 }; var next = Item(root, "new-query.txt"); o.ApplyScan(new([changed, next], []));
            Check(o.CollectionFiles("work").Count == 2 && o.CollectionFiles("work").First().Size == 88 && snapshot.Single().Size == 1);
            o.ApplyScan(new([next], [])); Check(o.CollectionFiles("work").Single().Path == next.Path);
        });
        test("批量查询在手动归类撤销和删除分区后失效", () =>
        {
            var o = Create("config"); var file = Item(root, "assign-query.txt"); o.ApplyScan(new([file], []));
            Check(o.CollectionFiles("work").Count == 1); o.Assign(file.Path, "inbox");
            Check(o.QueryCollections(["work", "inbox"])["work"].Count == 0 && o.CollectionFiles("inbox").Count == 1);
            o.Undo(); Check(o.CollectionFiles("work").Count == 1 && o.CollectionFiles("inbox").Count == 0);
            o.DeleteCollection("work"); Throws<InvalidOperationException>(() => o.CollectionFiles("work")); Check(o.CollectionFiles("inbox").Count == 1);
        });
        test("查询保持名称类型大小修改时间的稳定次序与反向设置", () =>
        {
            var o = Create("sort"); var a = Item(root, "a.txt", 2, 20); var b = Item(root, "b.txt", 2, 20); var c = Item(root, "c.txt", 1, 10);
            o.ApplyScan(new([c, b, a], []));
            foreach (var sort in new[] { "name", "type", "size", "modified" })
            {
                o.SetOptions("work", new(Sort: sort));
                var expected = sort == "size" ? new[] { "c.txt", "a.txt", "b.txt" } : new[] { "a.txt", "b.txt", "c.txt" };
                Check(o.CollectionFiles("work").Select(f => f.Name).SequenceEqual(expected));
            }
            o.SetOptions("work", new(Sort: "modified", Descending: true)); Check(o.CollectionFiles("work").Select(f => f.Name).SequenceEqual(["c.txt", "a.txt", "b.txt"]));
            o.SetOptions("work", new(Sort: "manual", Order: [b.Path.ToUpperInvariant(), b.Path, a.Path]));
            Check(o.CollectionFiles("work").Select(f => f.Name).SequenceEqual(["b.txt", "a.txt", "c.txt"]));
            o.SetOptions("work", new(Sort: "manual", Order: [c.Path])); Check(o.CollectionFiles("work").First().Path == c.Path);
        });
        test("查询结果只读并支持多个关键词与重复分区请求", () =>
        {
            var o = Create("readonly"); var file = Item(root, "中文项目.TXT"); o.ApplyScan(new([file], []));
            var result = o.QueryCollections(["work", "work"], "中文 TXT"); Check(result.Count == 1 && result["work"].Count == 1);
            Throws<NotSupportedException>(() => ((IList<DesktopFile>)result["work"]).Clear());
            Check(o.CollectionFiles("work", "中文 pdf").Count == 0 && o.CollectionFiles("work").Count == 1);
        });
        test("同根映射共享只读排序与本批筛选而仍保留所有分区", () =>
        {
            var o = Create("shared-map"); var folder = Path.Combine(root, "shared-map");
            o.State.Configuration.Collections.AddRange(Enumerable.Range(0, 60).Select(i => new Collection("alias-" + i, "映射 " + i, "#92C7B5", MappedPath: i % 2 == 0 ? folder : folder.ToUpperInvariant())));
            var files = new[] { Item(folder, "a.txt"), Item(folder, "b.png") }; o.ApplyScan(new([.. files], []), false);
            var ids = o.State.Configuration.Collections.Where(c => c.MappedPath != null).Select(c => c.Id).ToArray();
            var result = o.QueryCollections(ids); Check(result.Count == 60 && result.Values.All(v => v.Count == 2 && ReferenceEquals(v, result[ids[0]])));
            var filtered = o.QueryCollections(ids, "a txt"); Check(filtered.Values.All(v => v.Count == 1 && ReferenceEquals(v, filtered[ids[0]])));
            Throws<NotSupportedException>(() => ((IList<DesktopFile>)filtered[ids[0]]).Clear()); Check(o.CollectionFiles(ids[0]).Count == 2);
        });
        test("共享映射独立排序且折叠锁定和图标偏好不重建排序", () =>
        {
            var o = Create("shared-options"); var folder = Path.Combine(root, "shared-options");
            o.State.Configuration.Collections.AddRange([new("a", "A", "#92C7B5", MappedPath: folder), new("b", "B", "#92C7B5", MappedPath: folder)]);
            o.ApplyScan(new([Item(folder, "b.txt", size: 1), Item(folder, "a.txt", size: 2)], []), false);
            var original = o.CollectionFiles("a"); o.SetOptions("a", new(Collapsed: true, Locked: true, IconSize: 48));
            Check(ReferenceEquals(original, o.CollectionFiles("a")) && ReferenceEquals(original, o.CollectionFiles("b")));
            o.SetOptions("b", new(Sort: "size")); var sorted = o.QueryCollections(["a", "b"]);
            Check(sorted["a"].Select(f => f.Name).SequenceEqual(["a.txt", "b.txt"]) && sorted["b"].Select(f => f.Name).SequenceEqual(["b.txt", "a.txt"]));
            Check(!ReferenceEquals(sorted["a"], sorted["b"])); o.SetOptions("b", new()); Check(ReferenceEquals(original, o.CollectionFiles("b")));
        });
        test("共享目录的不同手动顺序保持独立且更改后替换过期视图", () =>
        {
            var o = Create("shared-manual"); var folder = Path.Combine(root, "shared-manual");
            o.State.Configuration.Collections.AddRange([new("a", "A", "#92C7B5", MappedPath: folder), new("b", "B", "#92C7B5", MappedPath: folder)]);
            var a = Item(folder, "a.txt"); var b = Item(folder, "b.txt"); o.ApplyScan(new([a, b], []), false);
            o.SetOptions("a", new(Sort: "manual", Order: [b.Path, a.Path])); o.SetOptions("b", new(Sort: "manual", Order: [a.Path, b.Path]));
            var before = o.QueryCollections(["a", "b"]); Check(before["a"].First() == b && before["b"].First() == a);
            o.SetOptions("a", new(Sort: "manual", Order: [a.Path])); var after = o.QueryCollections(["a", "b"]);
            Check(after["a"].First() == a && !ReferenceEquals(before["a"], after["a"]) && ReferenceEquals(before["b"], after["b"]));
        });
        test("重复最近分区共享40项视图且仍忽略独立排序设置", () =>
        {
            var o = Create("shared-recent"); o.State.Configuration.Collections.AddRange([new("r1", "R1", "#92C7B5", Recent: true), new("r2", "R2", "#92C7B5", Recent: true)]);
            o.ApplyScan(new(Enumerable.Range(0, 50).Select(i => Item(root, $"shared-recent-{i:D2}.txt", i)).ToList(), []), false);
            o.SetOptions("r2", new(Sort: "name", Descending: true)); var result = o.QueryCollections(["r1", "r2"]);
            Check(result["r1"].Count == 40 && ReferenceEquals(result["r1"], result["r2"]) && result["r2"].First().Name == "shared-recent-00.txt");
            var filtered = o.QueryCollections(["r1", "r2"], "recent-49"); Check(filtered["r1"].Count == 0 && ReferenceEquals(filtered["r1"], filtered["r2"]));
        });
        test("多种重叠排序超过引用上限时只临时计算并继续复用已有视图", () =>
        {
            var o = Create("shared-budget"); var folder = Path.Combine(root, "shared-budget");
            o.State.Configuration.Collections.AddRange([new("a", "A", "#92C7B5", MappedPath: folder), new("b", "B", "#92C7B5", MappedPath: folder), new("c", "C", "#92C7B5", MappedPath: folder)]);
            o.ApplyScan(new(Enumerable.Range(0, 10000).Select(i => Item(folder, $"budget-{i:D5}.txt", i % 7, 10000 - i)).ToList(), []), false);
            var name = o.CollectionFiles("a"); o.SetOptions("b", new(Sort: "size")); var size = o.CollectionFiles("b"); o.SetOptions("c", new(Sort: "modified"));
            var uncached = o.CollectionFiles("c"); Check(uncached.Count == 10000 && !ReferenceEquals(uncached, o.CollectionFiles("c")));
            Check(ReferenceEquals(name, o.CollectionFiles("a")) && ReferenceEquals(size, o.CollectionFiles("b")));
            o.SetOptions("a", new(Sort: "size")); var admitted = o.CollectionFiles("c"); Check(ReferenceEquals(admitted, o.CollectionFiles("c")));
        });
        test("超大目录不长期缓存但本批同根结果共享且新扫描仍更新", () =>
        {
            var o = Create("shared-large"); var folder = Path.Combine(root, "shared-large");
            o.State.Configuration.Collections.AddRange([new("a", "A", "#92C7B5", MappedPath: folder), new("b", "B", "#92C7B5", MappedPath: folder)]);
            var files = Enumerable.Range(0, 26000).Select(i => Item(folder, $"large-{i:D5}.txt")).ToList(); o.ApplyScan(new(files, []), false);
            var before = o.QueryCollections(["a", "b"]); Check(before["a"].Count == 26000 && ReferenceEquals(before["a"], before["b"]));
            Check(!ReferenceEquals(before["a"], o.CollectionFiles("a"))); o.ApplyScan(new([files[0] with { Size = 99 }], []), false);
            var after = o.QueryCollections(["a", "b"]); Check(after["a"].Single().Size == 99 && ReferenceEquals(after["a"], after["b"]) && before["a"].Count == 26000);
        });
    }
}
