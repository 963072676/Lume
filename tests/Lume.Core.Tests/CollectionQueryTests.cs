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
    }
}
