using System.Collections;
using Lume.Core;

internal static class FileSearchTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("文件搜索断言失败"); }
        void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("没有抛出 " + typeof(T).Name); }
        DesktopFile File(string name, string source = "未知来源", bool folder = false) => new(@"C:\synthetic\" + name, name, Path.GetExtension(name), 1, DateTime.UnixEpoch, DateTime.UnixEpoch, folder, source);
        test("批量搜索支持中文来源大小写和所有空白分隔的多关键词", () =>
        {
            var files = new[] { File("项目报告.TXT", "微信（推测）"), File("项目照片.png", "微信（推测）"), File("项目报告.txt") };
            var result = FileSearch.Find(files, "\t项目　TXT\n微信\r");
            Check(result.Files.Count == 1 && result.Files[0] == files[0] && !result.HasMore);
            Check(RuleEngine.Search(files[0], "项目\tTXT") && !RuleEngine.Search(files[1], "项目\tTXT"));
        });
        test("文件夹关键词与多条件仍遵循既有搜索语义", () =>
        {
            var files = new[] { File("项目", folder: true), File("项目报告.txt"), File("其他", folder: true) };
            Check(FileSearch.Find(files, "项目 文件夹").Files.SequenceEqual(files.Take(1)));
        });
        test("搜索恰好达到上限不误报截断，超过时保留原扫描顺序", () =>
        {
            var files = new[] { File("a.txt"), File("b.txt"), File("c.txt") };
            var truncated = FileSearch.Find(files, "txt", 2);
            Check(truncated.HasMore && truncated.Files.SequenceEqual(files.Take(2)));
            Check(!FileSearch.Find(files.Take(2).ToArray(), "txt", 2).HasMore);
            Check(FileSearch.Find(files, "无匹配").Files.Count == 0);
        });
        test("宽泛搜索在确认一项额外结果后停止，不遍历十万匹配项", () =>
        {
            var files = new ObservedFiles(100000, File("项目.txt"));
            var result = FileSearch.Find(files, "项目 txt");
            Check(result.Files.Count == 40 && result.HasMore && files.Reads <= 41);
        });
        test("查询取消在遍历期间生效且预先取消不访问任何文件", () =>
        {
            using var cancellation = new CancellationTokenSource();
            var files = new ObservedFiles(100000, File("项目.txt"), count => { if (count == 120) cancellation.Cancel(); });
            Throws<OperationCanceledException>(() => FileSearch.Find(files, "无匹配", cancellation: cancellation.Token));
            Check(files.Reads >= 120 && files.Reads <= 184);
            var unread = new ObservedFiles(100000, File("项目.txt"));
            Throws<OperationCanceledException>(() => FileSearch.Find(unread, "项目", cancellation: cancellation.Token));
            Check(unread.Reads == 0);
        });
        test("结果只保留有界命中引用且调用方不能修改返回列表", () =>
        {
            var files = new[] { File("项目.txt") }; var result = FileSearch.Find(files, "项目");
            Throws<NotSupportedException>(() => ((IList<DesktopFile>)result.Files).Clear());
            Check(result.Files.Count == 1 && ReferenceEquals(result.Files[0], files[0]));
        });
        test("搜索拒绝无效上限并对空白查询保持按扫描顺序匹配", () =>
        {
            var files = new[] { File("a.txt"), File("b.txt") };
            Throws<ArgumentOutOfRangeException>(() => FileSearch.Find(files, "a", 0));
            Throws<ArgumentOutOfRangeException>(() => FileSearch.Find(files, "a", 1001));
            Check(FileSearch.Find(files, " \t\n").Files.SequenceEqual(files));
        });
    }
    private sealed class ObservedFiles(int count, DesktopFile file, Action<int>? onRead = null) : IReadOnlyList<DesktopFile>
    {
        public int Reads { get; private set; }
        public int Count => count;
        public DesktopFile this[int index] { get { Reads++; onRead?.Invoke(Reads); return file; } }
        public IEnumerator<DesktopFile> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
