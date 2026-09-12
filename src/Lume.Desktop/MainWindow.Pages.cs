using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Lume.Core;
using Ellipse = System.Windows.Shapes.Ellipse;
namespace Lume.Desktop;
public sealed partial class MainWindow
{
    private UIElement BuildRules()
    {
        var panel = new StackPanel();
        var suggestion = organizer.Suggestions().FirstOrDefault();
        if (suggestion != null)
        {
            var row = new StackPanel(); row.Children.Add(Ui.Text($"{suggestion.Extension} 常被放进「{organizer.CollectionName(suggestion.CollectionId)}」", Tokens.ItemTitle, bold: true));
            var actions = Ui.Row(Ui.Button("设为自动规则", () => Run(() => organizer.Accept(suggestion))), Ui.Button("忽略建议", () => Run(() => organizer.Dismiss(suggestion)))); actions.Margin = new(0, 10, 0, 0); row.Children.Add(actions); panel.Children.Add(Ui.Card(row));
        }
        var query = search.Text.Trim();
        var rules = organizer.State.Configuration.Rules.Where(rule => query.Length == 0 || rule.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || organizer.CollectionName(rule.CollectionId).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var rule in rules)
        {
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.Children.Add(new Border { Background = Ui.Brush(organizer.State.Configuration.Collections.FirstOrDefault(c => c.Id == rule.CollectionId)?.Color ?? "#92C7B5"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 12, 0) });
            var text = new StackPanel(); text.Children.Add(Ui.Text(rule.Name + (rule.Enabled ? "" : " · 已停用"), Tokens.ItemTitle, rule.Enabled ? Ui.Ink : Ui.Muted, true));
            var detail = Ui.Text($"{rule.Conditions.Count} 个条件 → {organizer.CollectionName(rule.CollectionId)}", Tokens.Secondary, Ui.Muted); detail.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(detail); Grid.SetColumn(text, 1); body.Children.Add(text);
            var actions = Ui.Row(Ui.IconButton("↑", () => Run(() => organizer.MoveRule(rule.Id, -1)), "上移规则"), Ui.IconButton("↓", () => Run(() => organizer.MoveRule(rule.Id, 1)), "下移规则"));
            var toggle = Ui.Button(rule.Enabled ? "已启用" : "已停用", () => Run(() => organizer.SaveRule(rule with { Enabled = !rule.Enabled })));
            toggle.Width = 64; toggle.Height = 28; toggle.MinHeight = 28; toggle.Padding = new Thickness(8, 4, 8, 4); toggle.HorizontalContentAlignment = HorizontalAlignment.Center;
            toggle.ToolTip = rule.Enabled ? "点击停用规则" : "点击启用规则";
            toggle.Background = rule.Enabled ? Tokens.Brush(Tokens.Primary100) : Tokens.Brush(Tokens.Surface50); toggle.Foreground = rule.Enabled ? Ui.Accent : Ui.Muted; actions.Children.Add(toggle);
            actions.Children.Add(Ui.Button("编辑", () => EditRule(rule)));
            var remove = Ui.Button("删除", () => Run(() => organizer.DeleteRule(rule.Id))); remove.Foreground = Ui.Danger; actions.Children.Add(remove);
            Grid.SetColumn(actions, 2); body.Children.Add(actions);
            panel.Children.Add(Ui.Card(body, new Thickness(16)));
        }
        if (rules.Count == 0) panel.Children.Add(Ui.Text(query.Length == 0 ? "还没有规则。点击页头的「＋ 创建规则」，让相似的文件自动归类。" : "没有匹配的规则。", Tokens.ItemTitle, Ui.Muted));
        return panel;
    }
    private UIElement BuildHistory()
    {
        var panel = new StackPanel();
        var archiveButton = Ui.Button("物理归档…", () => OpenArchive()); archiveButton.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(archiveButton);
        panel.Children.Add(Ui.Text("物理归档可以恢复到原位置；虚拟归类只改变 Lume 的归属。", Tokens.Secondary, Ui.Muted));
        Border TimelineCard(Brush lineColor, string type, string title, string detail, UIElement? action = null)
        {
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rail = new Grid { Margin = new Thickness(0, 0, 12, 0) }; rail.Children.Add(new Border { Width = 2, Background = lineColor, HorizontalAlignment = HorizontalAlignment.Center }); rail.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = lineColor, Stroke = Tokens.Brush(Tokens.Surface0), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetColumn(rail, 0); body.Children.Add(rail);
            var content = new StackPanel(); var tag = Ui.Text(type, Tokens.Label, lineColor, true); content.Children.Add(tag); content.Children.Add(Ui.Text(title, Tokens.ItemTitle, bold: true));
            if (!string.IsNullOrWhiteSpace(detail)) { var details = Ui.Text(detail, Tokens.Secondary, Ui.Muted); details.Margin = new Thickness(0, 4, 0, 0); details.MaxHeight = 64; details.TextTrimming = TextTrimming.CharacterEllipsis; details.ToolTip = detail; content.Children.Add(details); }
            if (action != null) { if (action is FrameworkElement element) element.Margin = new Thickness(0, 8, 0, 0); content.Children.Add(action); }
            Grid.SetColumn(content, 1); body.Children.Add(content); return Ui.Card(body, new Thickness(12, 12, 16, 12));
        }
        var hasPhysicalHistory = false;
        if (Archives != null)
        {
            try
            {
                foreach (var batch in Archives.History().Take(30))
                {
                    hasPhysicalHistory = true;
                    var restore = Ui.Button("恢复到原位置（同名不覆盖）", async () =>
                    {
                        try { await Task.Run(() => Archives.RestoreAsync(batch.Id)); await RefreshAsync(); }
                        catch (Exception ex) { ShowError(ex); }
                    }); restore.IsEnabled = batch.Items.Any(i => i.Status is "Archived" or "RestoreFailed");
                    panel.Children.Add(TimelineCard(Tokens.Brush(Tokens.Warning600), "物理归档", $"{batch.CreatedUtc.ToLocalTime():MM-dd HH:mm} · {batch.Items.Count} 项", string.Join("\n", batch.Items.Select(i => $"{Path.GetFileName(i.Source)}：{i.Message}")), restore));
                }
            }
            catch (Exception ex) { hasPhysicalHistory = true; panel.Children.Add(Ui.Text("归档日志读取失败：" + ex.Message, Tokens.Secondary, Ui.Muted)); }
        }
        var last = organizer.State.History.LastOrDefault(h => !h.Undone);
        foreach (var entry in organizer.State.History.AsEnumerable().Reverse().Take(150))
        {
            UIElement? undo = ReferenceEquals(entry, last) ? Ui.Button("撤销这一步", () => Run(() => organizer.Undo()), true) : null;
            panel.Children.Add(TimelineCard(Tokens.Brush(Tokens.Primary600), "虚拟归类", $"{entry.Title}{(entry.Undone ? " · 已撤销" : "")}  ·  {entry.TimeUtc.ToLocalTime():MM-dd HH:mm:ss}", entry.Detail, undo));
        }
        if (organizer.State.History.Count == 0 && !hasPhysicalHistory) panel.Children.Add(Ui.Text("还没有整理记录。首次扫描或修改规则后，记录会出现在这里。", Tokens.ItemTitle, Ui.Muted));
        else if (organizer.State.History.Count > 150) panel.Children.Add(Ui.Text("界面显示最近 150 条，完整记录保存在本地配置中。", Tokens.Label, Ui.Muted));
        return panel;
    }
}
