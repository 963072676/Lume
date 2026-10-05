using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lume.Core;

namespace Lume.Desktop;

internal sealed partial class CommandPalette : Window
{
    private sealed record Entry(string Title, string Detail, Func<Task> Execute);
    private const int CollectionLimit = 20;
    private const string KeyHint = "↑ ↓ 选择 · Enter 打开 · Esc 关闭";
    private readonly TextBox input = new() { Height = 40, MaxLength = 512, ToolTip = "搜索文件、分区或操作；输入 > 只找操作" };
    private readonly ListBox results = new() { BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly TextBlock empty = Ui.Text("没有匹配结果，试试其他关键词。", color: Ui.Muted);
    private readonly TextBlock hint = Ui.Text(KeyHint, Tokens.Label, Ui.Muted);
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly Organizer organizer;
    private readonly Window owner;
    private readonly Func<string, Task> command;
    private readonly Action<DesktopFile> open;
    private readonly Action<string> focus;
    private List<Entry> entries = [];
    private CancellationTokenSource? searchCancellation;
    private Task? pendingRefresh;
    private long revision, pendingRevision = -1, displayedRevision = -1;
    private IReadOnlyList<DesktopFile>? displayedFiles;
    private Collection[] displayedCollections = [];
    private bool closed, executing;

    public CommandPalette(Window owner, Organizer organizer, Func<string, Task> command, Action<DesktopFile> open, Action<string> focus)
    {
        this.owner = owner; this.organizer = organizer; this.command = command; this.open = open; this.focus = focus;
        Owner = owner; Title = "快速操作"; Width = 600; Height = 440; MinWidth = 440; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        AutomationProperties.SetName(input, "搜索文件、分区或操作");
        AutomationProperties.SetName(results, "快速操作搜索结果");
        AutomationProperties.SetLiveSetting(empty, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(hint, AutomationLiveSetting.Polite);
        VirtualizingPanel.SetIsVirtualizing(results, true);
        VirtualizingPanel.SetVirtualizationMode(results, VirtualizationMode.Recycling);
        var layout = new DockPanel { Margin = new(24) };
        var heading = Ui.Text("想找什么？", Tokens.DialogTitle, bold: true); heading.Margin = new(0, 0, 0, 16); DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        input.Margin = new(0, 0, 0, 12); DockPanel.SetDock(input, Dock.Top); layout.Children.Add(input);
        hint.Margin = new(0, 12, 0, 0); DockPanel.SetDock(hint, Dock.Bottom); layout.Children.Add(hint);
        var body = new Grid(); body.Children.Add(results); body.Children.Add(empty); layout.Children.Add(body); Content = layout;
        input.TextChanged += (_, _) => QueueRefresh();
        searchDelay.Tick += async (_, _) => { searchDelay.Stop(); await RefreshAsync(); };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { e.Handled = true; await ExecuteAsync(); }
            else if (e.Key is Key.Up or Key.Down) { e.Handled = true; MoveSelection(e.Key == Key.Down ? 1 : -1); }
        };
        results.MouseDoubleClick += async (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(results, source) is ListBoxItem)
                await ExecuteAsync();
        };
        Loaded += async (_, _) => { await RefreshAsync(); if (!closed && ShowActivated) input.Focus(); };
        Closed += (_, _) =>
        {
            closed = true; revision++; searchDelay.Stop(); searchCancellation?.Cancel();
            entries.Clear(); results.Items.Clear(); displayedFiles = null; displayedCollections = [];
        };
    }

    private void QueueRefresh()
    {
        if (closed) return;
        revision++; searchCancellation?.Cancel(); searchDelay.Stop();
        // A pending query must never leave an executable selection from the previous text.
        entries.Clear(); results.Items.Clear(); results.IsEnabled = false;
        empty.Text = "正在搜索…"; empty.Visibility = Visibility.Visible; hint.Text = KeyHint;
        searchDelay.Start();
    }

    private bool DisplayIsCurrent() => displayedRevision == revision && ReferenceEquals(displayedFiles, organizer.Files)
        && displayedCollections.SequenceEqual(organizer.State.Configuration.Collections);

    private Task RefreshAsync()
    {
        searchDelay.Stop();
        if (closed || DisplayIsCurrent()) return Task.CompletedTask;
        if (pendingRevision == revision && pendingRefresh != null) return pendingRefresh;
        searchCancellation?.Cancel();
        entries.Clear(); results.Items.Clear(); results.IsEnabled = false;
        empty.Text = "正在搜索…"; empty.Visibility = Visibility.Visible;
        var cancellation = new CancellationTokenSource(); searchCancellation = cancellation;
        pendingRevision = revision;
        return pendingRefresh = SearchAsync(revision, input.Text.Trim(), organizer.Files,
            organizer.State.Configuration.Collections.ToArray(), cancellation);
    }

    private async Task SearchAsync(long requestedRevision, string text, IReadOnlyList<DesktopFile> files,
        Collection[] collections, CancellationTokenSource cancellation)
    {
        try
        {
            var actionsOnly = text.StartsWith('>'); var query = actionsOnly ? text[1..].Trim() : text;
            var found = DesktopMenu.Actions.Where(a => a.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(a => new Entry(a.Label, a.Detail, () => command(a.Id))).ToList();
            var more = false;
            if (!actionsOnly && query.Length > 0)
            {
                var groups = collections.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(CollectionLimit + 1).ToArray();
                more = groups.Length > CollectionLimit;
                found.AddRange(groups.Take(CollectionLimit).Select(c => new Entry(c.Name, "打开分区", () => { focus(c.Id); return Task.CompletedTask; })));
                var matches = await Task.Run(() =>
                {
#if VERIFICATION
                    if (findFilesForVerification != null) return findFilesForVerification(files, query, cancellation.Token);
#endif
                    return FileSearch.Find(files, query, cancellation: cancellation.Token);
                }, cancellation.Token);
                more |= matches.HasMore;
                found.AddRange(matches.Files.Select(f => new Entry(FilePresentation.DisplayName(f), f.Path, () => { open(f); return Task.CompletedTask; })));
            }
            if (closed || cancellation.IsCancellationRequested || requestedRevision != revision) return;
            if (!ReferenceEquals(files, organizer.Files) || !collections.SequenceEqual(organizer.State.Configuration.Collections)) { QueueRefresh(); return; }
            entries = found; results.Items.Clear();
            foreach (var entry in entries)
            {
                var row = new StackPanel { Margin = new(8, 6, 8, 6) }; row.Children.Add(Ui.Text(entry.Title));
                var detail = Ui.Text(entry.Detail, Tokens.Label, Ui.Muted); detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(detail);
                var item = new ListBoxItem { Content = row, ToolTip = entry.Detail };
                AutomationProperties.SetName(item, entry.Title + "，" + entry.Detail);
                AutomationProperties.SetHelpText(item, entry.Detail);
                results.Items.Add(item);
            }
            results.IsEnabled = true; results.SelectedIndex = entries.Count > 0 ? 0 : -1;
            empty.Text = "没有匹配结果，试试其他关键词。"; empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = more ? "结果较多，请增加关键词（最多显示 20 个分区和 40 个文件）。\n" + KeyHint : KeyHint;
            displayedRevision = requestedRevision; displayedFiles = files; displayedCollections = collections;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!closed && requestedRevision == revision && !cancellation.IsCancellationRequested)
            {
                entries.Clear(); results.Items.Clear(); empty.Text = "搜索未完成：" + ex.Message;
                empty.Visibility = Visibility.Visible; results.IsEnabled = false;
            }
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, cancellation)) searchCancellation = null;
            if (pendingRevision == requestedRevision) { pendingRevision = -1; pendingRefresh = null; }
            cancellation.Dispose();
        }
    }

    private void MoveSelection(int direction)
    {
        if (!DisplayIsCurrent() || results.Items.Count == 0) return;
        results.SelectedIndex = Math.Clamp(results.SelectedIndex + direction, 0, results.Items.Count - 1);
        results.ScrollIntoView(results.SelectedItem);
    }

    private async Task ExecuteAsync()
    {
        if (closed || executing) return;
        executing = true; var requestedRevision = revision;
        try
        {
            await RefreshAsync();
            if (closed || requestedRevision != revision || !DisplayIsCurrent() || results.SelectedIndex < 0 || results.SelectedIndex >= entries.Count) return;
            var entry = entries[results.SelectedIndex]; Close();
            try { await entry.Execute(); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        finally { executing = false; }
    }
}
