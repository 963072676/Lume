using System.Text.Json;
using Lume.Core;

internal static class ArchiveTests
{
    public static void Register(Action<string, Action> test, string testRoot)
    {
        static void Assert(bool condition, string message = "归档断言失败") { if (!condition) throw new Exception(message); }
        (ArchiveService Service, string Source, string Target, string Journal) Setup(string name)
        {
            var folder = Path.Combine(testRoot, "archive-" + name); var source = Path.Combine(folder, "source"); Directory.CreateDirectory(source);
            var journal = Path.Combine(folder, "journal"); return (new(journal), source, Path.Combine(folder, "target"), journal);
        }
        static void Write(string folder, string name = "报告.txt", string text = "原始正文") => File.WriteAllText(Path.Combine(folder, name), text);
        static ArchiveBatch Plan(ArchiveService service, string source, string target) => service.PreviewAsync(DesktopScanner.Scan([source]).Files, [source], target, false).GetAwaiter().GetResult();
        test("物理归档移动成功并跨重启恢复原路", () =>
        {
            var (service, source, target, journal) = Setup("roundtrip"); Write(source);
            var batch = Plan(service, source, target); service.ExecuteAsync(batch).GetAwaiter().GetResult();
            Assert(batch.Items[0].Status == "Archived", batch.Items[0].Message); Assert(!File.Exists(Path.Combine(source, "报告.txt"))); Assert(File.ReadAllText(Path.Combine(target, "报告.txt")) == "原始正文");
            new ArchiveService(journal).RestoreAsync(batch.Id).GetAwaiter().GetResult();
            Assert(File.ReadAllText(Path.Combine(source, "报告.txt")) == "原始正文"); Assert(!File.Exists(Path.Combine(target, "报告.txt")));
        });
        test("物理归档保留下载安全标记备用数据流", () =>
        {
            var (service, source, target, _) = Setup("ads"); Write(source);
            File.WriteAllText(Path.Combine(source, "报告.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            var batch = Plan(service, source, target); service.ExecuteAsync(batch).GetAwaiter().GetResult();
            Assert(batch.Items[0].Status == "Archived", batch.Items[0].Message); Assert(File.ReadAllText(Path.Combine(target, "报告.txt") + ":Zone.Identifier").Contains("ZoneId=3"));
        });
        test("归档预览不修改源文件", () => { var (s, source, target, _) = Setup("preview"); Write(source); Plan(s, source, target); Assert(File.Exists(Path.Combine(source, "报告.txt"))); Assert(!Directory.Exists(target)); });
        test("归档同名跳过且不覆盖", () => { var (s, source, target, _) = Setup("collision"); Write(source); Directory.CreateDirectory(target); Write(target, text: "原有目标"); var batch = Plan(s, source, target); s.ExecuteAsync(batch).GetAwaiter().GetResult(); Assert(batch.Items[0].Status == "Skipped"); Assert(File.ReadAllText(Path.Combine(target, "报告.txt")) == "原有目标"); Assert(File.Exists(Path.Combine(source, "报告.txt"))); });
        test("预览后目标出现同名项不覆盖", () => { var (s, source, target, _) = Setup("late-target"); Write(source); var batch = Plan(s, source, target); Directory.CreateDirectory(target); Write(target, text: "稍后出现"); s.ExecuteAsync(batch).GetAwaiter().GetResult(); Assert(File.ReadAllText(Path.Combine(target, "报告.txt")) == "稍后出现"); Assert(File.Exists(Path.Combine(source, "报告.txt"))); });
        test("预览后源内容变化拒绝移动", () => { var (s, source, target, _) = Setup("changed"); Write(source); var batch = Plan(s, source, target); Write(source, text: "变更正文"); s.ExecuteAsync(batch).GetAwaiter().GetResult(); Assert(batch.Items[0].Status == "Failed"); Assert(File.Exists(Path.Combine(source, "报告.txt"))); });
        test("恢复原位置同名时保留两边", () => { var (s, source, target, _) = Setup("restore-collision"); Write(source); var batch = Plan(s, source, target); s.ExecuteAsync(batch).GetAwaiter().GetResult(); Write(source, text: "新文件"); s.RestoreAsync(batch.Id).GetAwaiter().GetResult(); Assert(File.ReadAllText(Path.Combine(source, "报告.txt")) == "新文件"); Assert(File.Exists(Path.Combine(target, "报告.txt"))); });
        test("归档文件被修改后不盲目恢复", () => { var (s, source, target, _) = Setup("changed-target"); Write(source); var batch = Plan(s, source, target); s.ExecuteAsync(batch).GetAwaiter().GetResult(); Write(target, text: "外部修改"); s.RestoreAsync(batch.Id).GetAwaiter().GetResult(); Assert(!File.Exists(Path.Combine(source, "报告.txt"))); Assert(File.ReadAllText(Path.Combine(target, "报告.txt")) == "外部修改"); });
        test("中断日志确认源缺失目标完整的归档", () => { var (s, source, target, journal) = Setup("recover-archived"); Write(source); var batch = Plan(s, source, target); s.ExecuteAsync(batch).GetAwaiter().GetResult(); batch.Items[0].Status = "DestinationReady"; File.WriteAllText(Path.Combine(journal, batch.Id + ".json"), JsonSerializer.Serialize(batch)); s.RecoverAsync().GetAwaiter().GetResult(); Assert(s.History()[0].Items[0].Status == "Archived"); });
        test("中断后两份存在时不自动删除任意一份", () => { var (s, source, target, journal) = Setup("recover-both"); Write(source); var batch = Plan(s, source, target); Directory.CreateDirectory(target); Write(target); Directory.CreateDirectory(journal); batch.Items[0].Status = "DestinationReady"; File.WriteAllText(Path.Combine(journal, batch.Id + ".json"), JsonSerializer.Serialize(batch)); s.RecoverAsync().GetAwaiter().GetResult(); Assert(s.History()[0].Items[0].Status == "Review"); Assert(File.Exists(Path.Combine(source, "报告.txt")) && File.Exists(Path.Combine(target, "报告.txt"))); });
        test("归档不接收监控范围外文件", () => { var (s, source, target, _) = Setup("scope"); var other = Path.Combine(testRoot, "outside"); Directory.CreateDirectory(other); Write(other); var batch = s.PreviewAsync(DesktopScanner.Scan([other]).Files, [source], target, false).GetAwaiter().GetResult(); Assert(batch.Items[0].Status == "Skipped"); });
        test("归档跳过目录", () => { var (s, source, target, _) = Setup("folder"); Directory.CreateDirectory(Path.Combine(source, "folder")); var batch = Plan(s, source, target); Assert(batch.Items[0].Status == "Skipped"); });
        test("重复执行同一归档批次被拒绝", () => { var (s, source, target, _) = Setup("twice"); Write(source); var batch = Plan(s, source, target); s.ExecuteAsync(batch).GetAwaiter().GetResult(); try { s.ExecuteAsync(batch).GetAwaiter().GetResult(); throw new Exception("应拒绝重复执行"); } catch (InvalidOperationException) { } });
        test("无法写预操作日志时绝不移动文件", () => { var (_, source, target, journal) = Setup("journal-fail"); Write(source); File.WriteAllText(journal, "blocked"); var s = new ArchiveService(journal); var batch = Plan(s, source, target); try { s.ExecuteAsync(batch).GetAwaiter().GetResult(); } catch (IOException) { } Assert(File.Exists(Path.Combine(source, "报告.txt"))); Assert(!Directory.Exists(target)); });
        test("归档路径边界不能混淆相同前缀目录", () => Assert(!ArchiveService.IsInside(Path.Combine(testRoot, "target-evil", "a.txt"), Path.Combine(testRoot, "target"))));
    }
}
