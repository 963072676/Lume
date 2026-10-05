using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Lume.Core;

namespace Lume.Desktop;

internal sealed class ReferenceCleanupWindow : Window
{
    private readonly Organizer organizer;
    private readonly StateStore store;
    private readonly Action changed;
    private readonly ShellWorkerClient worker = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox days = new() { ItemsSource = new[] { 7, 30, 90 }, SelectedItem = 30, Width = 86 };
    private readonly TextBlock summary = Ui.Text("尚未检测。首次确认缺失时开始计时，以后可再次检测。", Tokens.Secondary, Ui.Muted);
    private readonly ListView candidates = new();
    private readonly CheckBox confirm = new() { Content = "我已检查列表，仅清理这些记录", Margin = new(0, 12, 0, 8) };
    private readonly Button review, clean, undo;
    private readonly TextBlock result = Ui.Text("", Tokens.Label, Ui.Muted);
    private ReferenceReviewPlan? plan;
    private string? cleanupId;
    private bool busy, closed;

    internal ReferenceCleanupWindow(Window owner, Organizer organizer, StateStore store, Action changed)
    {
        Owner = owner; this.organizer = organizer; this.store = store; this.changed = changed;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "清理过期引用"; Width = 840; Height = 620; MinWidth = 680; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new(24) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(Ui.Text("清理已删除文件留下的记录", Tokens.DialogTitle, bold: true));
        header.Children.Add(Ui.Text("检测自动归类记录和学习样本；保留手动固定、手动排序、外部引用及无法确认的路径。只检测本地固定磁盘中的关注目录，不读取文件内容。", Tokens.Secondary, Ui.Muted));
        review = Ui.Button("检测并预览", () => _ = ReviewAsync());
        var controls = Ui.Row(Ui.Text("首次确认缺失后等待"), days, Ui.Text("天"), review); controls.Margin = new(0, 14, 0, 12); header.Children.Add(controls);
        summary.Margin = new(0, 0, 0, 12); header.Children.Add(summary);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        footer.Children.Add(confirm);
        clean = Ui.Button("备份并清理", () => _ = CleanAsync(), true);
        undo = Ui.Button("撤销本次清理", Undo);
        footer.Children.Add(Ui.Row(clean, undo, Ui.Button("关闭", Close)));
        result.Margin = new(0, 12, 0, 6); footer.Children.Add(result);
        footer.Children.Add(Ui.Text("清理前自动生成完整备份，可在整理历史撤销。清理后数据格式升级，回到旧版时需恢复清理前备份。备份与撤销记录仍保留原数据，磁盘总占用不一定立即下降。", Tokens.Label, Ui.Muted));
        var view = new GridView();
        view.Columns.Add(new() { Header = "文件", Width = 155, DisplayMemberBinding = new Binding(nameof(StaleReference.Name)) });
        view.Columns.Add(new() { Header = "待清理路径", Width = 345, DisplayMemberBinding = new Binding(nameof(StaleReference.Path)) });
        view.Columns.Add(new() { Header = "首次缺失", Width = 105, DisplayMemberBinding = new Binding(nameof(StaleReference.FirstMissingUtc)) { StringFormat = "yyyy-MM-dd" } });
        view.Columns.Add(new() { Header = "学习样本", Width = 70, DisplayMemberBinding = new Binding(nameof(StaleReference.Samples)) });
        candidates.View = view; VirtualizingPanel.SetIsVirtualizing(candidates, true); VirtualizingPanel.SetVirtualizationMode(candidates, VirtualizationMode.Recycling);
        var itemStyle = new Style(typeof(ListViewItem)); itemStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(StaleReference.Path))));
        itemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(StaleReference.Name))));
        itemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.HelpTextProperty, new Binding(nameof(StaleReference.Path)))); candidates.ItemContainerStyle = itemStyle;
        ScrollViewer.SetCanContentScroll(candidates, true); panel.Children.Add(candidates); Content = panel;
        System.Windows.Automation.AutomationProperties.SetName(days, "引用清理等待天数");
        System.Windows.Automation.AutomationProperties.SetName(candidates, "可清理引用预览列表");
        confirm.Checked += (_, _) => Sync(); confirm.Unchecked += (_, _) => Sync();
        days.SelectionChanged += (_, _) => { plan = null; candidates.ItemsSource = null; confirm.IsChecked = false; summary.Text = "等待天数已变化，请重新检测。"; Sync(); };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); worker.Dispose(); };
        Sync();
    }

    private void Sync()
    {
        review.IsEnabled = !busy; days.IsEnabled = !busy; confirm.IsEnabled = !busy && plan?.Candidates.Count > 0;
        clean.IsEnabled = !busy && plan?.Candidates.Count > 0 && confirm.IsChecked == true;
        undo.IsEnabled = !busy && cleanupId != null && organizer.State.History.LastOrDefault(h => !h.Undone)?.Id == cleanupId;
    }
    private async Task ReviewAsync()
    {
        if (busy) return;
        busy = true; plan = null; confirm.IsChecked = false; candidates.ItemsSource = null; Sync(); result.Text = "正在检测引用…";
        try
        {
            var scope = organizer.CaptureReferenceReview();
            var observations = await WindowsReferenceProbe.ReadAsync(worker, scope.Paths, scope.Roots, lifetime.Token);
            if (closed) return;
            plan = organizer.ReviewReferences(scope, observations, (int)days.SelectedItem);
            candidates.ItemsSource = plan.Candidates;
            summary.Text = $"可清理 {plan.Candidates.Count} 项 · 等待期内 {plan.WaitingCount} 项 · 无法确认 {plan.UnavailableCount} 项 · 受保护 {plan.ProtectedCount} 项";
            result.Text = "检测完成。缺失时间已保存；没有清理任何记录。";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) { if (!closed) result.Text = ex.Message; }
        finally { busy = false; if (!closed) Sync(); }
    }
    private async Task CleanAsync()
    {
        if (busy || plan == null || !clean.IsEnabled) return;
        var selected = plan; busy = true; Sync(); result.Text = "正在生成完整备份并重新检测…";
        try
        {
            var backup = Path.Combine(RuntimeIdentity.BackupDirectory(Path.GetDirectoryName(store.Path)!),
                "before-reference-cleanup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".lume-backup.zip");
            await organizer.CleanReferencesAsync(selected, backup, RuntimeIdentity.Version,
                (paths, token) => WindowsReferenceProbe.ReadAsync(worker, paths, selected.Roots, token), lifetime.Token);
            if (closed) return;
            cleanupId = organizer.State.History.LastOrDefault(h => !h.Undone)?.Id; plan = null; confirm.IsChecked = false; candidates.ItemsSource = null;
            summary.Text = $"已清理 {selected.Candidates.Count} 项过期引用，原文件未修改。";
            result.Text = "完整备份：" + backup; result.ToolTip = backup; changed();
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) { if (!closed) { result.Text = ex.Message; plan = null; confirm.IsChecked = false; } }
        finally { busy = false; if (!closed) Sync(); }
    }
    private void Undo()
    {
        if (busy || cleanupId == null || organizer.State.History.LastOrDefault(h => !h.Undone)?.Id != cleanupId) { result.Text = "已有其他操作，请在整理历史按顺序撤销。"; Sync(); return; }
        try { organizer.Undo(); cleanupId = null; result.Text = "本次清理已撤销，引用和学习样本已恢复。"; changed(); }
        catch (Exception ex) { result.Text = ex.Message; }
        Sync();
    }
}
