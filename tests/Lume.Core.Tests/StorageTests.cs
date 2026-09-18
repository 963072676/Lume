using System.Text.Json;
using Lume.Core;

internal static class StorageTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        void Check(bool value) { if (!value) throw new Exception("存储回归断言失败"); }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; } throw new Exception("应拒绝此操作"); }
        (StateStore, Organizer) Create(string name)
        {
            var store = new StateStore(Path.Combine(root, name, "state.json")); return (store, new(store, AppState.Create([])));
        }
        test("旧格式迁移保留完整原件且外观保存不重写配置历史", () =>
        {
            var (store, _) = Create("split-legacy"); Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
            var legacy = AppState.Create([]); legacy.History.Add(new() { Title = "legacy" });
            var raw = JsonSerializer.Serialize(legacy); File.WriteAllText(store.Path, raw);
            var organizer = new Organizer(store, store.Load([])); organizer.SetTheme("lavender");
            Check(File.ReadAllText(store.Path + ".legacy.bak") == raw && store.Load([]).Version == 2);
            var main = File.ReadAllBytes(store.Path); var history = Directory.GetFiles(store.Path + ".history").ToDictionary(p => p, File.ReadAllBytes);
            organizer.SetTheme("sage"); organizer.SetSnap(false); organizer.SavePlacement("work", new(10, 20));
            Check(File.ReadAllBytes(store.Path).SequenceEqual(main));
            Check(history.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            var loaded = store.Load([]); Check(loaded.Desktop.Theme == "sage" && !loaded.Desktop.SnapEnabled && loaded.Desktop.Positions["work"].X == 10 && loaded.History.Count == 1);
        });
        test("历史分段限定常驻数量并跨分段完整撤销和导出", () =>
        {
            var (store, organizer) = Create("split-history");
            for (var i = 0; i < 205; i++)
            {
                var previous = StateStore.Clone(organizer.State.Configuration);
                previous.Collections[1] = previous.Collections[1] with { Name = "step-" + i };
                organizer.State.History.Add(new() { Title = "history-" + i, PreviousConfiguration = previous });
            }
            store.Save(organizer.State); organizer = new(store, store.Load([]));
            Check(organizer.State.History.Count == 200 && organizer.State.HistoryArchives.Sum(a => a.Count) == 5);
            var export = Path.Combine(root, "export-all-history.json"); store.ExportHistory(organizer.State, export);
            Check(JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(export))!.Count == 205);
            for (var i = 204; i >= 0; i--) { Check(organizer.CanUndo); organizer.Undo(); Check(organizer.State.Configuration.Collections[1].Name == "step-" + i); }
            Check(!organizer.CanUndo && !new Organizer(store, store.Load([])).CanUndo);
            store.ExportHistory(organizer.State, export);
            Check(JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(export))!.All(e => e.Undone));
        });
        test("设置和撤销写入失败都回滚内存且保留磁盘版本", () =>
        {
            var (store, organizer) = Create("split-failure"); organizer.AddCollection("before"); organizer.SetTheme("lavender");
            var settings = store.Path + ".desktop." + organizer.State.StorageRevision + ".json";
            using (var locked = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => organizer.SetTheme("sage"));
            Check(organizer.State.Desktop.Theme == "lavender" && store.Load([]).Desktop.Theme == "lavender");
            using (var locked = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(organizer.Undo);
            Check(organizer.CanUndo && organizer.State.Configuration.Collections.Count == 6 && !organizer.State.History.Last().Undone);
            organizer.Undo(); Check(organizer.State.Configuration.Collections.Count == 5);
        });
        test("损坏主配置可校验恢复备份且保留损坏原件", () =>
        {
            var (store, organizer) = Create("split-recovery"); organizer.AddCollection("one"); organizer.AddCollection("two");
            File.WriteAllText(store.Path, "{broken");
            Check(store.InspectBackup().Collections == 6);
            var restored = store.RestoreBackup(); Check(restored.Configuration.Collections.Count == 6 && restored.History.Count == 1);
            Check(Directory.GetFiles(Path.GetDirectoryName(store.Path)!, "state.json.preserved-*.json").Any(p => File.ReadAllText(p) == "{broken"));
        });
        test("损坏外观设置优先恢复同配置版本的外观备份", () =>
        {
            var (store, organizer) = Create("settings-recovery"); organizer.AddCollection("keep"); organizer.SetTheme("lavender"); organizer.SetTheme("sage");
            var settings = store.Path + ".desktop." + organizer.State.StorageRevision + ".json"; File.WriteAllText(settings, "broken");
            Reject(() => store.Load([]));
            var restored = store.RestoreBackup(); Check(restored.Desktop.Theme == "lavender" && restored.Configuration.Collections.Count == 6);
            Check(File.ReadAllText(settings) == "broken");
        });
        test("恢复后保留原有历史依赖但继续回收未来无引用历史文件", () =>
        {
            var (store, organizer) = Create("recovery-retention"); organizer.AddCollection("one"); organizer.AddCollection("two");
            var preserved = Directory.GetFiles(store.Path + ".history");
            File.WriteAllText(store.Path, "{broken");
            organizer = new(store, store.RestoreBackup());
            for (var i = 0; i < 12; i++) organizer.RenameCollection("work", "after-" + i);
            Check(preserved.All(File.Exists));
            Check(Directory.GetFiles(store.Path + ".history").Length <= preserved.Length + 2);
        });
        test("损坏历史和越界历史路径不会被当作有效备份", () =>
        {
            var (store, organizer) = Create("invalid-history"); organizer.AddCollection("one"); organizer.AddCollection("two");
            File.WriteAllText(Path.Combine(store.Path + ".history", organizer.State.HistoryFile!), "broken"); Reject(() => store.Load([]));
            Check(store.InspectBackup().Collections == 6);
            File.WriteAllText(store.Path + ".bak", "{\"Version\":1,\"HistoryFile\":\"../../secret.json\"}");
            Reject(() => store.InspectBackup());
        });
    }
}
