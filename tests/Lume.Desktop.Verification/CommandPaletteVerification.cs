using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lume.Core;

namespace Lume.Desktop;

internal sealed partial class CommandPalette
{
    private Func<IReadOnlyList<DesktopFile>, string, CancellationToken, FileSearchResult>? findFilesForVerification;
    internal static int RunVerification()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "palette-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var windows = new List<CommandPalette>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        void Result(bool passed, string? error = null)
        {
            var result = JsonSerializer.Serialize(new { passed, checks, error, fixture, desktopTakeover = false, inputSimulation = false, focusRequested = false }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(fixture, "result.json"), result); File.WriteAllText(Path.Combine(root, "latest-result.json"), result);
        }
        app.Startup += async (_, _) =>
        {
            Window? owner = null;
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
                async Task Until(Func<bool> ok)
                {
                    var clock = Stopwatch.StartNew(); while (!ok() && clock.Elapsed.TotalSeconds < 6) await Task.Delay(20);
                    if (!ok()) throw new TimeoutException("命令面板查询未在期限内收敛");
                }
                DesktopFile Item(string name) => new(Path.Combine(fixture, name), name, ".txt", 1, DateTime.UnixEpoch, DateTime.UnixEpoch, false, "synthetic");
                var files = Enumerable.Range(0, 80).Select(i => Item($"项目文件-{i:D3}.txt")).ToArray();
                var state = AppState.Create([]); state.Configuration.Collections.AddRange(Enumerable.Range(0, 50).Select(i => new Collection("project-" + i, "项目分区 " + i, "#92C7B5")));
                var organizer = new Organizer(new StateStore(Path.Combine(fixture, "state.json")), state); organizer.ApplyScan(new(files.ToList(), []), false);
                var commands = new List<string>(); var opened = new List<string>(); var focused = new List<string>();
                owner = new Window { Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false }; owner.Show();
                CommandPalette NewPalette()
                {
                    var palette = new CommandPalette(owner, organizer, id => { commands.Add(id); return Task.CompletedTask; }, file => opened.Add(file.Path), focused.Add)
                        { Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                    windows.Add(palette); palette.Show(); return palette;
                }
                BlockedFiles BlockCurrent(CommandPalette palette)
                {
                    organizer.ApplyScan(new(files.ToList(), []), false); var snapshot = organizer.Files; var blocked = new BlockedFiles(files);
                    palette.findFilesForVerification = (current, query, cancellation) => FileSearch.Find(ReferenceEquals(current, snapshot) ? blocked : current, query, cancellation: cancellation);
                    return blocked;
                }
                var window = NewPalette(); await Until(window.DisplayIsCurrent);
                Check(window.entries.Count == DesktopMenu.Actions.Length && !window.ShowActivated && !window.ShowInTaskbar, "空查询只显示操作，隔离窗口不激活且不显示任务栏");
                window.input.Text = "项目";
                Check(window.entries.Count == 0 && !window.results.IsEnabled && window.searchDelay.IsEnabled, "输入后立即禁止执行旧结果，等待合并查询");
                window.input.Text = "无匹配"; window.input.Text = "项目";
                await Until(() => window.DisplayIsCurrent() && !window.searchDelay.IsEnabled);
                Check(window.entries.Count == 60 && window.entries.Count(e => e.Detail == "打开分区") == 20 && window.entries.Count(e => e.Detail.StartsWith(fixture)) == 40, "连续输入采用最后一轮查询，最多20分区与40文件");
                Check(window.hint.Text.Contains("结果较多"), "截断结果提供缩小查询提示");
                window.MoveSelection(1000); Check(window.results.SelectedIndex == 59, "向下选择限制在末项");
                window.MoveSelection(-1000); Check(window.results.SelectedIndex == 0, "向上选择限制在首项");
                var fileItem = (ListBoxItem)window.results.Items[20];
                Check(AutomationProperties.GetName(window.input).Length > 0 && AutomationProperties.GetName(window.results).Length > 0
                    && AutomationProperties.GetName(fileItem).Contains(files[0].Path) && AutomationProperties.GetHelpText(fileItem) == files[0].Path && fileItem.ToolTip as string == files[0].Path,
                    "搜索输入及结果提供辅助名称，文件路径可通过提示和辅助信息读取");
                window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var image = File.Create(Path.Combine(root, "latest-preview.png"))) encoder.Save(image);
                window.input.Text = "无匹配"; await window.RefreshAsync();
                Check(window.entries.Count == 0 && window.results.SelectedIndex == -1 && window.empty.Visibility == Visibility.Visible, "无匹配显示空态且没有可执行选择");
                using (var blocked = BlockCurrent(window))
                {
                    window.input.Text = "项目 txt"; var oldQuery = window.RefreshAsync();
                    await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var beats = 0; var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    heartbeat.Tick += (_, _) => beats++; heartbeat.Start(); await Task.Delay(100); heartbeat.Stop();
                    Check(beats >= 2 && blocked.ThreadId != Environment.CurrentManagedThreadId, "后台扫描等待期间 Dispatcher 保持响应");
                    var newer = Item("new.txt"); organizer.ApplyScan(new([newer], []), false); window.input.Text = "new txt"; await window.RefreshAsync();
                    Check(window.entries.Count == 1 && window.entries[0].Detail == newer.Path, "新查询可完成而无需等待过期扫描");
                    blocked.Release.Set(); await oldQuery;
                    Check(window.entries.Count == 1 && window.entries[0].Detail == newer.Path && window.DisplayIsCurrent(), "被取消的旧扫描不能覆盖新结果");
                }
                using (var blocked = BlockCurrent(window))
                {
                    window.input.Text = "项目 txt"; var oldQuery = window.RefreshAsync();
                    await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var newer = Item("项目新目录.txt"); organizer.ApplyScan(new([newer], []), false); blocked.Release.Set(); await oldQuery;
                    await Until(window.DisplayIsCurrent);
                    Check(window.entries.Count == 1 && window.entries[0].Detail == newer.Path, "相同查询期间扫描快照变化时自动重查，拒绝旧文件结果");
                }
                using (var blocked = BlockCurrent(window))
                {
                    window.input.Text = "项目"; var oldQuery = window.RefreshAsync();
                    await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    state.Configuration.Collections.RemoveAll(c => c.Id.StartsWith("project-")); blocked.Release.Set(); await oldQuery;
                    await Until(window.DisplayIsCurrent);
                    Check(window.entries.All(e => e.Detail != "打开分区"), "查询期间删除分区时不保留已经失效的分区入口");
                }
                using (var blocked = BlockCurrent(window))
                {
                    window.input.Text = ">刷新"; await window.RefreshAsync();
                    Check(window.entries.Count == 1 && !blocked.Entered.Task.IsCompleted, "仅操作查询跳过文件扫描");
                    await window.ExecuteAsync();
                    Check(window.closed && commands.SequenceEqual(new[] { "refresh" }) && opened.Count == 0 && focused.Count == 0, "执行操作只调用匹配命令一次并关闭面板");
                }
                var currentFile = Item("new.txt"); organizer.ApplyScan(new([Item("old.txt"), currentFile], []), false); window = NewPalette();
                window.input.Text = "old txt"; window.input.Text = "new txt"; await window.ExecuteAsync();
                Check(window.closed && opened.SequenceEqual(new[] { currentFile.Path }), "合并等待期间执行会等待当前查询，不打开上一轮文件");
                window = NewPalette();
                using (var blocked = BlockCurrent(window))
                {
                    window.input.Text = "项目 txt";
                    var execution = window.ExecuteAsync(); await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await window.ExecuteAsync(); blocked.Release.Set(); await execution;
                    Check(window.closed && opened.Count == 2 && opened[1] == files[0].Path, "查询期间重复执行不会重复打开文件");
                }
                window = NewPalette();
                using (var blocked = BlockCurrent(window))
                {
                    window.input.Text = "项目 txt";
                    var pending = window.RefreshAsync(); await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    window.Close(); blocked.Release.Set(); await pending;
                    Check(window.closed && !window.searchDelay.IsEnabled && window.results.Items.Count == 0 && window.searchCancellation == null && opened.Count == 2, "关闭面板会取消待查询并释放结果，不执行任何动作");
                }
                Result(true);
            }
            catch (Exception ex) { exit = 1; Result(false, ex.ToString()); }
            finally { foreach (var window in windows) if (!window.closed) window.Close(); owner?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }

    private sealed class BlockedFiles(IReadOnlyList<DesktopFile> files) : IReadOnlyList<DesktopFile>, IDisposable
    {
        private int blocked;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public int ThreadId { get; private set; }
        public int Count => files.Count;
        public DesktopFile this[int index]
        {
            get
            {
                if (index == 0 && Interlocked.Exchange(ref blocked, 1) == 0)
                {
                    ThreadId = Environment.CurrentManagedThreadId; Entered.TrySetResult();
                    if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("隔离扫描未释放");
                }
                return files[index];
            }
        }
        public IEnumerator<DesktopFile> GetEnumerator() => files.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
