using Lume.Core;

internal static class InteractionTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        void Check(bool ok) { if (!ok) throw new Exception("交互断言失败"); }
        Organizer Create()
        {
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            foreach (var name in new[] { "a.xyz", "b.xyz", "c.xyz" }) File.WriteAllText(Path.Combine(dir, name), name);
            var o = new Organizer(new StateStore(Path.Combine(dir, "state.json")), AppState.Create([])); o.ApplyScan(DesktopScanner.Scan([dir])); return o;
        }
        test("用户与公共桌面同目标快捷方式合并且不同参数保留", () =>
        {
            var personal = Path.Combine(root, "personal"); var common = Path.Combine(root, "common");
            var f = new DesktopFile(Path.Combine(personal, "G HUB.lnk"), "G HUB.lnk", ".lnk", 1, DateTime.UtcNow, DateTime.UtcNow, false, "", new("C:\\app.exe", "app.exe", ".exe", "file"));
            var other = f with { Path = Path.Combine(common, f.Name) };
            Check(DesktopScanner.MergeDesktopShortcuts([f, other], personal, common).Single() == f);
            Check(DesktopScanner.MergeDesktopShortcuts([f, other with { Target = other.Target! with { Arguments = "--profile other" } }], personal, common).Count == 2);
            Check(DesktopScanner.MergeDesktopShortcuts([f, other with { Target = null }], personal, common).Count == 2);
        });
        test("图标拖动顺序持久化并可切回名称排序", () =>
        {
            var o = Create(); var f = o.Files.ToArray();
            o.ReorderFile("inbox", f[2].Path, f[0].Path); Check(o.CollectionFiles("inbox")[0] == f[2]);
            o.ReorderFile("inbox", f[2].Path, null); Check(o.CollectionFiles("inbox")[2] == f[2]);
            o.ReorderFile("inbox", f[1].Path, f[0].Path);
            var state = new StateStore(Path.Combine(Path.GetDirectoryName(f[0].Path)!, "state.json")).Load([]);
            Check(state.Desktop.Cards["inbox"].Order![0] == f[1].Path);
            o.SetOptions("inbox", o.Options("inbox") with { Sort = "name" }); Check(o.CollectionFiles("inbox")[0] == f[0]);
            Check(f.All(x => File.Exists(x.Path)));
        });
        test("AI临时分组与分配同一事务应用及撤销未使用分组不创建", () =>
        {
            var o = Create(); var snapshot = o.CaptureAiSnapshot(); var group = new Collection("temporary", "多媒体", "#92C7B5");
            o.ApplyAiSuggestions(snapshot, [new(snapshot.Items[0].Id, group.Id, "手动选择", .9)], [group, new("unused", "未使用", "#92C7B5")]);
            Check(o.State.Configuration.Collections.Any(c => c.Id == group.Id) && !o.State.Configuration.Collections.Any(c => c.Id == "unused"));
            Check(o.CollectionOf(snapshot.Items[0].File) == group.Id); o.Undo();
            Check(!o.State.Configuration.Collections.Any(c => c.Id == group.Id) && o.CollectionOf(snapshot.Items[0].File) == "inbox");
        });
    }
}
