using Lume.Core;

if (args.Length == 3 && args[0] == "--rule-audit")
{
    var state = new StateStore(args[1]).Load([]);
    var config = state.Configuration;
    var scan = DesktopScanner.Scan(config.Roots.Concat(config.Collections.Where(c => c.MappedPath != null).Select(c => c.MappedPath!)), config.LinkedFiles);
    var evidence = new
    {
        files = scan.Files.Count, targets = scan.Files.Count(f => f.Target != null),
        rules = config.Rules.Select(r => new { r.Name, r.Conditions, matched = RuleEngine.Preview(scan.Files, config, r, DateTime.UtcNow).Select(p => new { p.File.Name, target = p.File.Target?.Path, p.Applied, p.Reason }) }),
        gameTargets = scan.Files.Where(f => RuleEngine.Matches(f, new("audit", "audit", "work", [new("targetPath", "contains", "Game")]), DateTime.UtcNow)).Select(f => new { f.Name, f.Target }),
        scan.Warnings
    };
    System.IO.File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(evidence, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var tests = new List<(string Name, Action Body)>();
var now = DateTime.UtcNow;
DesktopFile FileItem(string name, int days = 1, long size = 100) => new(Path.Combine(Path.GetTempPath(), name), name, Path.GetExtension(name), size, now.AddDays(-days), now.AddDays(-days), false, DesktopScanner.InferSource(name));
void Test(string name, Action body) => tests.Add((name, body));
void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"期望 {expected}，实际 {actual}"); }
void True(bool value) { if (!value) throw new Exception("断言失败"); }
void Throws<T>(Action body) where T : Exception { try { body(); } catch (T) { return; } throw new Exception("没有抛出 " + typeof(T).Name); }
Rule RuleOf(params Condition[] c) => new("test", "测试", "work", [.. c]);
var root = Path.Combine(Path.GetTempPath(), "Lume-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Organizer Create(string name) { var store = new StateStore(Path.Combine(root, name, "state.json")); return new(store, AppState.Create([root])); }

Test("默认图片规则忽略扩展名大小写", () => Equal(RuleEngine.Classify(FileItem("截图.PNG"), AppState.Create([]).Configuration, now), "screenshots"));
Test("未知类型进入收件箱", () => Equal(RuleEngine.Classify(FileItem("未知.xyz"), AppState.Create([]).Configuration, now), "inbox"));
Test("多条件必须全部匹配", () => { var rule = RuleOf(new Condition("extension", "in", "png,jpg"), new Condition("name", "contains", "截图")); True(RuleEngine.Matches(FileItem("截图.png"), rule, now)); True(!RuleEngine.Matches(FileItem("照片.png"), rule, now)); });
Test("首条规则优先", () => { var c = AppState.Create([]).Configuration; c.Rules.Insert(0, RuleOf(new Condition("extension", "in", "png"))); Equal(RuleEngine.Classify(FileItem("a.png"), c, now), "work"); });
Test("停用规则不参与匹配", () => True(!RuleEngine.Matches(FileItem("a.png"), RuleOf(new Condition("extension", "in", "png")) with { Enabled = false }, now)));
Test("手动归属优先", () => { var f = FileItem("a.png"); var c = AppState.Create([]).Configuration; c.Overrides[f.Path.ToUpperInvariant()] = "inbox"; Equal(RuleEngine.Classify(f, c, now), "inbox"); });
Test("失效分区固定不会吞掉文件", () => { var f = FileItem("a.png"); var c = AppState.Create([]).Configuration; c.Overrides[f.Path] = "deleted"; Equal(RuleEngine.Classify(f, c, now), "screenshots"); });
Test("文件年龄和大小条件", () => True(RuleEngine.Matches(FileItem("a.md", 8, 2 * 1048576), RuleOf(new Condition("createdDays", "gt", "7"), new Condition("sizeMb", "gt", "1")), now)));
Test("年龄边界按严格小于处理", () => True(!RuleEngine.Matches(FileItem("a.md", 7), RuleOf(new Condition("createdDays", "lt", "7")), now)));
Test("非法数值和正则拒绝保存", () => { var c = AppState.Create([]).Configuration.Collections; True(RuleEngine.Validate(RuleOf(new Condition("sizeMb", "gt", "NaN")), c) != null); True(RuleEngine.Validate(RuleOf(new Condition("name", "regex", "[")), c) != null); });
Test("非法条件组合被拒绝", () => True(RuleEngine.Validate(RuleOf(new Condition("extension", "regex", "png")), AppState.Create([]).Configuration.Collections) != null));
Test("正则执行时间受限", () => { var f = FileItem(new string('a', 120) + "!"); True(!RuleEngine.Matches(f, RuleOf(new Condition("name", "regex", "^(a+)+$")), now)); });
Test("目录规则不按扩展名误分类", () => { var f = FileItem("folder.pdf") with { IsDirectory = true }; Equal(RuleEngine.Classify(f, AppState.Create([]).Configuration, now), "apps"); });
Test("来源标注推测和未知", () => { Equal(DesktopScanner.InferSource("微信图片.jpg"), "微信（推测）"); Equal(DesktopScanner.InferSource("报告.pdf"), "未知来源"); });
Test("搜索支持中文和多关键词", () => { True(RuleEngine.Search(FileItem("微信项目.png"), "微信 png")); True(!RuleEngine.Search(FileItem("微信项目.png"), "微信 pdf")); });
Test("扫描不递归且跳过隐藏项", () => { var path = Path.Combine(root, "scanner"); Directory.CreateDirectory(Path.Combine(path, "sub")); File.WriteAllText(Path.Combine(path, "a.txt"), "a"); File.WriteAllText(Path.Combine(path, "sub", "deep.txt"), "b"); File.WriteAllText(Path.Combine(path, "hidden.txt"), "c"); File.SetAttributes(Path.Combine(path, "hidden.txt"), FileAttributes.Hidden); var result = DesktopScanner.Scan([path, path]); Equal(result.Files.Count, 2); Equal(result.Warnings.Count, 0); });
Test("不可访问目录保留诊断", () => Equal(DesktopScanner.Scan([Path.Combine(root, "missing")]).Warnings.Count, 1));
Test("自动归类历史和重复扫描幂等", () => { var o = Create("auto"); var f = FileItem("auto.md"); o.ApplyScan(new([f], [])); Equal(o.State.History.Count, 1); o.ApplyScan(new([f], [])); Equal(o.State.History.Count, 1); });
Test("撤销自动分类不会立即被规则重复应用", () => { var o = Create("auto-undo"); var f = FileItem("undo.md"); o.ApplyScan(new([f], [])); o.Undo(); Equal(o.CollectionOf(f), "inbox"); o.ApplyScan(new([f], [])); Equal(o.CollectionOf(f), "inbox"); o.Release(f.Path); Equal(o.CollectionOf(f), "work"); });
Test("手动分类和撤销恢复归属", () => { var o = Create("manual"); var f = FileItem("manual.md"); o.ApplyScan(new([f], [])); o.Assign(f.Path, "screenshots"); Equal(o.CollectionOf(f), "screenshots"); o.Undo(); Equal(o.CollectionOf(f), "work"); });
Test("保存规则立即重算且可撤销", () => { var o = Create("rule"); var f = FileItem("rule.xyz"); o.ApplyScan(new([f], [])); o.SaveRule(RuleOf(new Condition("extension", "in", "xyz"))); Equal(o.CollectionOf(f), "work"); o.Undo(); Equal(o.CollectionOf(f), "inbox"); });
Test("学习需要三个不同文件且必须确认", () => { var o = Create("learn"); var files = Enumerable.Range(1, 3).Select(i => FileItem($"sample{i}.psd")).ToList(); o.ApplyScan(new(files, [])); o.Assign(files[0].Path, "work"); o.Assign(files[0].Path, "work"); Equal(o.Suggestions().Count, 0); o.Assign(files[1].Path, "work"); Equal(o.Suggestions().Count, 0); o.Assign(files[2].Path, "work"); Equal(o.Suggestions().Count, 1); var next = FileItem("new.psd"); o.ApplyScan(new([.. files, next], [])); Equal(o.CollectionOf(next), "inbox"); o.Accept(o.Suggestions()[0]); Equal(o.CollectionOf(next), "work"); Equal(o.Suggestions().Count, 0); });
Test("重启恢复配置并保持路径不区分大小写", () => { var o = Create("restart"); var f = FileItem("restart.md"); o.ApplyScan(new([f], [])); o.Assign(f.Path, "inbox"); var s = new StateStore(Path.Combine(root, "restart", "state.json")).Load([]); True(s.Configuration.Overrides.ContainsKey(f.Path.ToUpperInvariant())); Equal(s.Assignments[f.Path.ToUpperInvariant()], "inbox"); True(s.History.Count > 0); });
Test("原子保存创建上一版本备份", () => { var o = Create("backup"); o.AddCollection("测试分区"); o.AddCollection("测试分区2"); True(File.Exists(Path.Combine(root, "backup", "state.json.bak"))); Equal(new StateStore(Path.Combine(root, "backup", "state.json.bak")).Load([]).Configuration.Collections.Count, 6); });
Test("配置损坏不静默覆盖", () => { var path = Path.Combine(root, "broken.json"); File.WriteAllText(path, "{broken"); Throws<InvalidDataException>(() => new StateStore(path).Load([])); Equal(File.ReadAllText(path), "{broken"); });
Test("存储失败回滚内存变更", () => { var invalidParent = Path.Combine(root, "file-not-directory"); File.WriteAllText(invalidParent, "blocked"); var o = new Organizer(new StateStore(Path.Combine(invalidParent, "state.json")), AppState.Create([root])); Throws<IOException>(() => o.AddCollection("不能保存")); Equal(o.State.Configuration.Collections.Count, 5); Equal(o.State.History.Count, 0); });
Test("文件移出目录不会留在可见列表", () => { var o = Create("delete"); o.ApplyScan(new([FileItem("gone.md")], [])); o.ApplyScan(new([], [])); Equal(o.Files.Count, 0); });
Test("物理文件内容与路径保持不变", () => { var path = Path.Combine(root, "physical"); Directory.CreateDirectory(path); var file = Path.Combine(path, "真实.txt"); File.WriteAllText(file, "原始内容"); var o = Create("physical-state"); o.ApplyScan(DesktopScanner.Scan([path])); o.Assign(file, "screenshots"); o.SaveRule(RuleOf(new Condition("extension", "in", "txt"))); o.Undo(); o.Undo(); Equal(File.ReadAllText(file), "原始内容"); Equal(Directory.GetFiles(path).Length, 1); });
Test("规则优先级调整并可撤销", () => { var o = Create("priority"); o.SaveRule(RuleOf(new Condition("extension", "in", "png"))); o.MoveRule("test", 1); Equal(o.State.Configuration.Rules[1].Id, "test"); o.Undo(); Equal(o.State.Configuration.Rules[0].Id, "test"); });
Test("拒绝空目录配置和重复分区名称", () => { var o = Create("invalid"); Throws<InvalidOperationException>(() => o.SetRoots([])); Throws<InvalidOperationException>(() => o.AddCollection("工作资料")); });

Test("删除分区保留文件并可完整撤销", () => { var o = Create("delete-collection"); var f = FileItem("keep.png"); o.ApplyScan(new([f], [])); o.Assign(f.Path, "screenshots"); o.DeleteCollection("screenshots"); Equal(o.CollectionOf(f), "inbox"); True(!o.State.Configuration.Rules.Any(r => r.CollectionId == "screenshots")); o.Undo(); Equal(o.CollectionOf(f), "screenshots"); True(o.State.Configuration.Rules.Any(r => r.CollectionId == "screenshots")); });
Test("收件箱不可删除且分区可重命名", () => { var o = Create("rename"); Throws<InvalidOperationException>(() => o.DeleteCollection("inbox")); o.RenameCollection("work", "项目资料"); Equal(o.CollectionName("work"), "项目资料"); o.Undo(); Equal(o.CollectionName("work"), "工作资料"); });

ArchiveTests.Register(Test, root);
RuleMatchingTests.Register(Test, root);
AiAnalysisTests.Register(Test, root);
InteractionTests.Register(Test, root);
Test("系统入口位置和显示开关随布局备份恢复且状态持久化", () =>
{
    var o = Create("system-entries"); o.SetSystemEntries(false); o.SavePlacement("__windows-system", new(25, 50, 260, 160));
    var snapshot = LayoutBackup.Capture(o.State.Desktop); o.SetSystemEntries(true);
    var restored = LayoutBackup.Apply(snapshot, o.State.Desktop, o.State.Configuration.Collections.Select(c => c.Id));
    True(!restored.ShowSystemEntries); Equal(restored.Positions["__windows-system"], new CardPlacement(25, 50, 260, 160));
    True(new StateStore(Path.Combine(root, "system-entries", "state.json")).Load([]).Desktop.ShowSystemEntries);
});
Test("全局图标大小保留分区锁定折叠排序并持久化", () =>
{
    var o = Create("global-icons"); o.SetOptions("work", new(true, true, "size", true)); o.SetAllIconSize(26);
    True(o.State.Configuration.Collections.All(c => o.Options(c.Id).IconSize == 26));
    Equal(o.Options("work"), new CardOptions(true, true, "size", true, 26));
    True(new StateStore(Path.Combine(root, "global-icons", "state.json")).Load([]).Desktop.Cards.Values.All(c => c.IconSize == 26));
    Throws<ArgumentOutOfRangeException>(() => o.SetAllIconSize(100));
});
Test("AI多媒体规则生成后命中扩展名并拒绝非法条件", () =>
{
    var draft = RuleOf(new Condition("name", "contains", "多媒体"));
    var rule = RuleEngine.Validate(draft, AppState.Create([]).Configuration.Collections);
    var parsed = AiAnalysisClient.ParseRule("{\"conditions\":[{\"field\":\"extension\",\"operator\":\"in\",\"value\":\"mp3,mp4,mkv,flac,wav\"}]}", draft, AppState.Create([]).Configuration.Collections);
    True(RuleEngine.Matches(FileItem("电影.MP4"), parsed, now)); True(RuleEngine.Matches(FileItem("音乐.flac"), parsed, now));
    True(!RuleEngine.Matches(FileItem("多媒体.exe"), parsed, now)); Equal(parsed.Id, draft.Id); Equal(parsed.CollectionId, draft.CollectionId);
    Throws<InvalidDataException>(() => AiAnalysisClient.ParseRule("{\"conditions\":[{\"field\":\"execute\",\"operator\":\"run\",\"value\":\"cmd\"}]}", draft, AppState.Create([]).Configuration.Collections));
});
Test("全部折叠展开持久化且保留位置锁定与排序", () =>
{
    var o = Create("collapse-all"); var id = "work";
    o.SetOptions(id, new(Locked: true, Sort: "size", Descending: true, IconSize: 48));
    o.SavePlacement(id, new(50, 60, 350, 400));
    o.SetAllCollapsed(true); True(o.State.Configuration.Collections.All(c => o.Options(c.Id).Collapsed));
    Equal(o.Options(id), new CardOptions(true, true, "size", true, 48));
    var restored = new StateStore(Path.Combine(root, "collapse-all", "state.json")).Load([]);
    True(restored.Desktop.Cards.Values.All(c => c.Collapsed));
    o.SetAllCollapsed(false); True(o.State.Configuration.Collections.All(c => !o.Options(c.Id).Collapsed));
    Equal(o.State.Desktop.Positions[id], new CardPlacement(50, 60, 350, 400));
    Equal(o.Options(id), new CardOptions(true, false, "size", true, 48));
});
DesktopFeatureTests.Register(Test, root);
SelectionTests.Register(Test);
var failed = 0;
try
{
    foreach (var (name, body) in tests) { try { body(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
    Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
}
finally
{
    // 仅清理本次创建且已校验前缀的临时目录。
    var resolved = Path.GetFullPath(root);
    if (resolved.StartsWith(Path.Combine(Path.GetTempPath(), "Lume-tests-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
}
return failed == 0 ? 0 : 1;
