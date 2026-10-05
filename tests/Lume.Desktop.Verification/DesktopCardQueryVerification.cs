using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lume.Core;

namespace Lume.Desktop;

internal static class DesktopCardQueryVerification
{
    internal static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "card-query-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var cards = new List<DesktopCardWindow>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        app.Startup += (_, _) =>
        {
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
                T Field<T>(DesktopCardWindow card, string name) => (T)typeof(DesktopCardWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card)!;
                void Query(DesktopCardWindow card, string value) { Field<TextBox>(card, "search").Text = value; Field<DispatcherTimer>(card, "searchDelay").Stop(); }
                int Count(DesktopCardWindow card) => int.Parse(((TextBlock)Field<Border>(card, "countBadge").Child).Text);
                void Page(DesktopCardWindow card, int value) => typeof(DesktopCardWindow).GetField("filePage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(card, value);
                var state = AppState.Create([]); state.Configuration.Rules.Clear();
                state.Configuration.Collections = [new("inbox", "收件箱", "#92C7B5"), new("work", "工作", "#92C7B5"), new("other", "其他", "#92C7B5"), new("recent", "最近", "#92C7B5", Recent: true)];
                var organizer = new Organizer(new StateStore(Path.Combine(fixture, "state.json")), state); var now = DateTime.UtcNow;
                var files = Enumerable.Range(0, 26001).Select(i => new DesktopFile(Path.Combine(fixture, $"refresh-{i:D5}.txt"), $"refresh-{i:D5}.txt", ".txt", i, now, now.AddMinutes(-i), false, "验证")).ToList();
                organizer.ApplyScan(new(files, []), false); var ids = new[] { "inbox", "work", "other" };
                for (var i = 0; i < files.Count; i++) state.Assignments[files[i].Path] = ids[i % ids.Length];
                var initial = organizer.QueryCollections(state.Configuration.Collections.Select(c => c.Id));
                var tilesBuilt = 0; var rendered = new Dictionary<string, DesktopFile>(StringComparer.OrdinalIgnoreCase);
                UIElement Tile(DesktopFile file, TileSelection selection)
                {
                    tilesBuilt++; rendered[file.Path] = file; var button = Ui.Button(file.Name, () => { }); selection.Add(file, button); return button;
                }
                foreach (var collection in state.Configuration.Collections)
                {
                    var card = new DesktopCardWindow(organizer, collection, new(-16000, 0), IntPtr.Zero, Tile, () => { }, () => { }, _ => { }, initialFiles: initial[collection.Id]);
                    cards.Add(card); card.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                }
                var inbox = cards.Single(c => c.CollectionId == "inbox"); var work = cards.Single(c => c.CollectionId == "work");
                var other = cards.Single(c => c.CollectionId == "other"); var recent = cards.Single(c => c.CollectionId == "recent");
                Check(cards.All(c => c.Handle == IntPtr.Zero && !c.IsVisible && !c.ShowActivated && !c.ShowInTaskbar), "实际卡片只创建控件，不显示窗口、不附着桌面、不请求焦点");
                Check(Count(inbox) == 8667 && Count(work) == 8667 && Count(other) == 8667 && Count(recent) == 40, "首次卡片读取共享初始化结果并显示各自完整计数");
                Check(cards.All(c => c.Selection.Model.Visible.Count <= 80) && tilesBuilt == 280, "首次创建普通分区各80项、最近分区40项图块");
                Query(work, "refresh-00001"); Query(other, "missing"); Query(recent, "refresh-00150"); Query(inbox, "\u3000\t");
                DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(work.Selection.Model.Visible.Single() == files[1].Path && Count(work) == 1 && Count(other) == 0 && Count(recent) == 0 && Count(inbox) == 8667, "实际批量刷新保留各卡片不同搜索和空白关键词");
                Check(Field<WrapPanel>(other, "files").Children.Count == 1 && other.Selection.Model.Visible.Count == 0, "空搜索显示提示且没有文件图块与选择");
                var built = tilesBuilt; var selected = inbox.Selection.Model.Visible[0]; inbox.Selection.Model.Select(selected); inbox.Selection.Refresh();
                DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(tilesBuilt == built && inbox.Selection.Model.Selected.Contains(selected), "无变化刷新不重建卡片图块并保持当前选择");
                Query(inbox, "missing"); DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(Count(inbox) == 0 && inbox.Selection.Model.Visible.Count == 0 && inbox.Selection.Model.Selected.Count == 0, "独立搜索移除不可见选择且不影响其他卡片");
                organizer.SetOptions("work", new(Collapsed: true)); built = tilesBuilt; DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(Count(work) == 1 && work.Selection.Model.Visible.Count == 0 && Field<WrapPanel>(work, "files").Children.Count == 0 && tilesBuilt == built, "折叠仍有搜索计数而不创建隐藏文件控件");
                organizer.SetOptions("work", new()); DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(work.Selection.Model.Visible.Single() == files[1].Path && Count(work) == 1, "展开恢复本卡片搜索结果");
                Query(work, ""); organizer.SetOptions("work", new(Sort: "size", Descending: true)); DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(work.Selection.Model.Visible[0] == files[25999].Path && work.Selection.Model.Visible.Count == 80, "批量刷新采用该卡片的独立反向排序");
                Page(work, int.MaxValue); DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(work.Selection.Model.Visible.Count == 27 && Count(work) == 8667, "分页越界收敛到最后一页且保留完整计数");
                Page(work, 0); Query(work, "refresh-00001"); DesktopSurface.RefreshCardFiles(organizer, cards); work.Selection.Model.Select(files[1].Path);
                var changed = files.Select((f, i) => i == 1 ? f with { Size = 99, ModifiedUtc = now.AddHours(1) } : f).ToList();
                organizer.ApplyScan(new(changed, []), false); DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(rendered[files[1].Path].Size == 99 && work.Selection.Model.Selected.Contains(files[1].Path), "新扫描元数据进入实际图块且保留仍可见的选择");
                organizer.ApplyScan(new(changed.Where(f => f.Path != files[1].Path).ToList(), []), false); DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(Count(work) == 0 && work.Selection.Model.Visible.Count == 0 && work.Selection.Model.Selected.Count == 0, "删除后的新扫描清除搜索结果和选择");
                Query(recent, ""); DesktopSurface.RefreshCardFiles(organizer, cards);
                Check(Count(recent) == 40 && recent.Selection.Model.Visible.Count == 40 && !recent.Selection.Model.Visible.Contains(files[1].Path), "最近分区批量刷新仍先取40项并排除已删除文件");
                Query(inbox, "missing"); inbox.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Check(Count(inbox) == 0 && inbox.Selection.Model.Visible.Count == 0, "初始化结果消费后不被再次加载沿用");
                var supplied = new DesktopCardWindow(organizer, state.Configuration.Collections[0], new(-16000, 0), IntPtr.Zero, Tile, () => { }, () => { }, _ => { }, initialFiles: []);
                try
                {
                    supplied.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Check(Count(supplied) == 0 && supplied.Selection.Model.Visible.Count == 0, "明确提供空初始化结果时直接采用，不重新查询覆盖");
                    supplied.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Check(Count(supplied) == 8667 && supplied.Selection.Model.Visible.Count == 80, "空初始化结果也只消费一次，随后加载读取当前查询");
                }
                finally { supplied.Close(); }
                Result(true);
            }
            catch (Exception ex) { exit = 1; Result(false, ex.ToString()); }
            finally { foreach (var card in cards) card.Close(); app.Shutdown(); }
        };
        void Result(bool passed, string? error = null)
        {
            var result = JsonSerializer.Serialize(new { passed, checks, error, version = RuntimeIdentity.Version, commit = RuntimeIdentity.Commit, desktopTakeover = false, inputSimulation = false, focusRequested = false, windowsShown = false }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(fixture, "result.json"), result); File.WriteAllText(Path.Combine(root, "latest-result.json"), result);
        }
        app.Run(); return exit;
    }
}
