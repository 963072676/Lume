using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lume.Core;
namespace Lume.Desktop;

internal sealed class CommandPalette : Window
{
    private sealed record Entry(string Title, string Detail, Func<Task> Execute);
    private readonly TextBox input = new() { Height = 40, ToolTip = "搜索文件、分区或操作；输入 > 只找操作" };
    private readonly ListBox results = new() { BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly TextBlock empty = Ui.Text("没有匹配结果，试试其他关键词。", color: Ui.Muted);
    private List<Entry> entries = [];

    public CommandPalette(Window owner, Organizer organizer, Func<string, Task> command, Action<DesktopFile> open, Action<string> focus)
    {
        Owner = owner; Title = "快速操作"; Width = 600; Height = 440; MinWidth = 440; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var layout = new DockPanel { Margin = new(24) };
        var heading = Ui.Text("想找什么？", Tokens.DialogTitle, bold: true); heading.Margin = new(0, 0, 0, 16); DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        input.Margin = new(0, 0, 0, 12); DockPanel.SetDock(input, Dock.Top); layout.Children.Add(input);
        var hint = Ui.Text("↑ ↓ 选择 · Enter 打开 · Esc 关闭", Tokens.Label, Ui.Muted); hint.Margin = new(0, 12, 0, 0); DockPanel.SetDock(hint, Dock.Bottom); layout.Children.Add(hint);
        var body = new Grid(); body.Children.Add(results); body.Children.Add(empty); layout.Children.Add(body); Content = layout;
        void Refresh()
        {
            var text = input.Text.Trim(); var actionsOnly = text.StartsWith('>'); var query = actionsOnly ? text[1..].Trim() : text;
            entries = DesktopMenu.Actions.Where(a => a.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(a => new Entry(a.Label, a.Detail, () => command(a.Id))).ToList();
            if (!actionsOnly && query.Length > 0)
            {
                entries.AddRange(organizer.State.Configuration.Collections.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(c => new Entry(c.Name, "打开分区", () => { focus(c.Id); return Task.CompletedTask; })));
                entries.AddRange(organizer.Files.Where(f => RuleEngine.Search(f, query)).Take(40).Select(f => new Entry(FilePresentation.DisplayName(f), f.Path, () => { open(f); return Task.CompletedTask; })));
            }
            results.Items.Clear();
            foreach (var entry in entries)
            {
                var row = new StackPanel { Margin = new(8, 6, 8, 6) }; row.Children.Add(Ui.Text(entry.Title));
                var detail = Ui.Text(entry.Detail, Tokens.Label, Ui.Muted); detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(detail); results.Items.Add(row);
            }
            results.SelectedIndex = entries.Count > 0 ? 0 : -1; empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task Execute()
        {
            if (results.SelectedIndex < 0) return;
            var entry = entries[results.SelectedIndex]; Close();
            try { await entry.Execute(); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        input.TextChanged += (_, _) => Refresh();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { e.Handled = true; await Execute(); }
            else if (e.Key is Key.Up or Key.Down && results.Items.Count > 0)
            {
                results.SelectedIndex = Math.Clamp(results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, results.Items.Count - 1);
                results.ScrollIntoView(results.SelectedItem); e.Handled = true;
            }
        };
        results.MouseDoubleClick += async (_, _) => await Execute(); Loaded += (_, _) => { Refresh(); input.Focus(); };
    }
}
