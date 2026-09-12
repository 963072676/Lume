using System.Runtime.InteropServices;
using Lume.Core;

internal static class RuleMatchingTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        var now = DateTime.UtcNow;
        DesktopFile Item(string name) => new(Path.Combine(root, name), name, Path.GetExtension(name), 10, now, now, false, "未知来源");
        Rule Rule(string field, string op, string value) => new("custom", "自定义", "work", [new(field, op, value)]);
        void Check(bool ok) { if (!ok) throw new Exception("规则匹配断言失败"); }
        bool Match(DesktopFile f, string field, string op, string value) => RuleEngine.Matches(f, Rule(field, op, value), now);
        test("截图影音关键词按任一匹配且不区分大小写", () =>
        {
            foreach (var name in new[] { "音乐.lnk", "腾讯视频.lnk", "追剧.url", "抖音.lnk" }) Check(Match(Item(name), "name", "contains", "音乐,视频,剧,音"));
            Check(!Match(Item("工作.lnk"), "name", "contains", "音乐,视频,剧,音"));
            Check(Match(Item("GAME.lnk"), "name", "contains", "音乐，game；视频"));
            Check(!Match(Item("Game.lnk"), "name", "contains", "Game Studio"));
        });
        test("空关键词不全命中且正则逗号保持原意", () =>
        {
            Check(!Match(Item("abc"), "name", "contains", ",，； ;"));
            Check(RuleEngine.Validate(Rule("name", "contains", ",，；"), AppState.Create([]).Configuration.Collections) != null);
            Check(Match(Item("aaa"), "name", "regex", "^a{2,4}$"));
            Check(!Match(Item("a"), "name", "regex", "^a{2,4}$"));
            Check(Match(Item("a,b.txt"), "name", "literal", "a,b"));
            Check(!Match(Item("a.txt"), "name", "literal", "a,b"));
            Check(Match(Item("视频播放器"), "name", "starts", "音乐,视频"));
        });
        test("快捷目标与快捷方式自身字段独立且缺失大小不误命中", () =>
        {
            var f = Item("启动.lnk") with { Target = new(@"D:\Games\Studio\Player.exe", "Player.exe", ".exe", "file", 3 * 1048576, "播放器", "Studio", "Example") };
            Check(Match(f, "targetPath", "contains", "game")); Check(!Match(f, "path", "contains", "game"));
            Check(Match(f, "targetName", "contains", "player")); Check(Match(f, "targetExtension", "in", "EXE,dll"));
            Check(Match(f, "targetSizeMb", "gt", "2")); Check(Match(f, "targetDescription", "contains", "播放"));
            Check(Match(f, "targetProduct", "contains", "studio")); Check(Match(f, "targetCompany", "contains", "example"));
            Check(!Match(Item("缺失.lnk"), "targetSizeMb", "lt", "1"));
            Check(!Match(Item("普通.txt"), "targetPath", "regex", ".*"));
            foreach (var c in new[] { new Condition("targetPath", "contains", "Game"), new("targetKind", "is", "url"), new("targetExtension", "in", ".exe"), new("targetSizeMb", "gt", "1") })
                Check(RuleEngine.Validate(new("x", "x", "work", [c]), AppState.Create([]).Configuration.Collections) == null);
        });
        test("草稿预览与保存归属一致并区分优先规则和手动固定", () =>
        {
            var store = new StateStore(Path.Combine(root, "rule-preview", "state.json"));
            var organizer = new Organizer(store, AppState.Create([]));
            var files = new[] { Item("音乐.lnk"), Item("视频.lnk"), Item("剧.lnk") };
            organizer.ApplyScan(new(files.ToList(), []));
            organizer.Assign(files[0].Path, "inbox");
            var draft = Rule("name", "contains", "音乐,视频,剧");
            var preview = RuleEngine.Preview(files, organizer.State.Configuration, draft, now);
            Check(preview.Count == 3 && preview.Count(p => p.Applied) == 2);
            organizer.SaveRule(draft);
            Check(preview.All(p => organizer.CollectionOf(p.File) == p.CollectionId));
            organizer.MoveRule(draft.Id, 4);
            var blocked = RuleEngine.Preview(files, organizer.State.Configuration, draft, now);
            Check(blocked.All(p => !p.Applied));
            Check(blocked.Count(p => p.Reason.StartsWith("优先规则")) == 2);
        });
        if (!OperatingSystem.IsWindows()) return;
        test("真实lnk与url扫描读取目标且不改写快捷方式", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var fixtures = Path.Combine(root, "shortcut-rules"); Directory.CreateDirectory(fixtures);
            // WScript validates TargetPath using the system code page on some hosts.
            // Keep the target portable; the shortcut names still exercise Chinese paths.
            var game = Path.Combine(root, "Games", "music.txt"); Directory.CreateDirectory(Path.GetDirectoryName(game)!); File.WriteAllText(game, "内容");
            object? shell = null;
            void Create(string name, string target)
            {
                if (!OperatingSystem.IsWindows()) return;
                // Write the InternetShortcut fixture directly: WScript's URL setter
                // depends on installed protocol handlers on some Windows editions.
                if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(Path.Combine(fixtures, name), "[InternetShortcut]\r\nURL=" + target + "\r\n");
                    return;
                }
                object? link = null;
                var stage = "CreateShortcut";
                try
                {
                    link = ((dynamic)shell!).CreateShortcut(Path.Combine(fixtures, name));
                    stage = "TargetPath";
                    ((dynamic)link).TargetPath = target;
                    stage = "Save";
                    ((dynamic)link).Save();
                }
                catch (Exception ex) { throw new InvalidOperationException($"Shortcut fixture {name}: {stage} failed", ex); }
                finally { if (link != null) Marshal.FinalReleaseComObject(link); }
            }
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                Create("启动.lnk", game); Create("目录.lnk", Path.GetDirectoryName(game)!);
                Create("丢失.lnk", Path.Combine(root, "Games", "missing.exe")); Create("游戏.url", "steam://rungameid/1234");
                Create("程序.lnk", Environment.ProcessPath!);
            }
            finally { if (shell != null) Marshal.FinalReleaseComObject(shell); }
            var before = Directory.GetFiles(fixtures).ToDictionary(p => p, File.ReadAllBytes);
            var files = DesktopScanner.Scan([fixtures]).Files;
            var linkFile = files.Single(f => f.Name == "启动.lnk");
            Check(linkFile.Target?.Path == game && Match(linkFile, "targetPath", "contains", "Game"));
            Check(Match(linkFile, "targetExtension", "in", "txt") && linkFile.Target?.Size == new FileInfo(game).Length);
            Check(files.Single(f => f.Name == "目录.lnk").Target?.Kind == "folder");
            Check(files.Single(f => f.Name == "丢失.lnk").Target is { Kind: "unknown", Size: null });
            Check(files.Single(f => f.Name == "游戏.url").Target is { Kind: "url", Path: "steam://rungameid/1234" });
            Check(files.Single(f => f.Name == "程序.lnk").Target is { Kind: "file", Product.Length: > 0 });
            Check(before.All(p => p.Value.SequenceEqual(File.ReadAllBytes(p.Key))));
        });
    }
}
