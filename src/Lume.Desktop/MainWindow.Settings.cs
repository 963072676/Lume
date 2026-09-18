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
    private UIElement BuildSettings()
    {
        settingsBody = new();
        var layout = new DockPanel(); var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 16) };
        settingsTabs.Clear();
        foreach (var name in new[] { "常规", "外观", "分区", "目录", "AI 与数据" })
        {
            var tab = Ui.Button(name, () => { settingsSection = name; RenderSettingsSection(); });
            settingsTabs[name] = tab; tabs.Children.Add(tab);
        }
        DockPanel.SetDock(tabs, Dock.Top); layout.Children.Add(tabs);
        layout.Children.Add(new ScrollViewer { Content = settingsBody }); RenderSettingsSection(); return layout;
    }
    private void RenderSettingsSection()
    {
        foreach (var (name, button) in settingsTabs)
        {
            button.Background = name == settingsSection ? Tokens.Primary100Brush : Brushes.Transparent;
            button.Foreground = name == settingsSection ? Ui.Accent : Ui.Muted;
            button.BorderThickness = new(0);
        }
        var panel = new StackPanel();
        switch (settingsSection)
        {
            case "外观": AddThemeSettings(panel); AddGlassSettings(panel); break;
            case "分区": AddCollectionsSettings(panel); AddGlassSettings(panel); break;
            case "目录": AddRootsSettings(panel); break;
            case "AI 与数据": AddAiSettings(panel); AddDataSettings(panel); break;
            default: AddGeneralSettings(panel); AddIntegrationSettings(panel); break;
        }
        settingsBody.Content = panel;
    }
    private void ApplyTheme(string id)
    {
        try
        {
            organizer.SetTheme(id);
            Tokens.ApplyTheme(id);
            // 仅同步色板状态，保留设置滚动位置和滑块控件。
            SyncThemeChoices();
            DataChanged?.Invoke();
            status.Text = "已应用「" + Tokens.Theme.Name + "」· 自动保存";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private readonly Dictionary<string, Button> themeChoices = [];
    private void SyncThemeChoices()
    {
        foreach (var (id, button) in themeChoices)
        {
            var selected = id == Tokens.Theme.Id;
            Ui.SetSelected(button, selected);
            button.BorderBrush = selected ? Ui.Accent : Tokens.Line200Brush;
            button.BorderThickness = new(selected ? 2 : 1);
            button.Background = selected ? Tokens.Primary50Brush : Brushes.White;
            button.ToolTip = selected ? "当前主题" : "点击应用「" + ThemePalette.Find(id).Name + "」";
            if (button.Tag is TextBlock label) label.Text = selected ? "✓ 当前使用" : "选择配色";
        }
    }
    private void AddThemeSettings(StackPanel panel)
    {
        var body = new StackPanel();
        body.Children.Add(Ui.Text("主题配色", Tokens.SectionTitle, bold: true));
        var hint = Ui.Text("选一种喜欢的颜色，让桌面轻盈一点。切换立即生效，下次启动仍会保留。", Tokens.Secondary, Ui.Muted);
        hint.Margin = new(0, 6, 0, 18); body.Children.Add(hint);
        var choices = new WrapPanel(); themeChoices.Clear();
        foreach (var palette in ThemePalette.All)
        {
            var preview = new StackPanel();
            var swatches = new Grid { Height = 48, Margin = new(0, 0, 0, 12) };
            foreach (var color in new[] { palette.Soft, palette.Selected, palette.Accent })
            {
                var index = swatches.ColumnDefinitions.Count;
                swatches.ColumnDefinitions.Add(new());
                var swatch = new Border { Background = Ui.Brush(color), CornerRadius = new(5), Margin = new(0, 0, index == 2 ? 0 : 4, 0) };
                Grid.SetColumn(swatch, index); swatches.Children.Add(swatch);
            }
            preview.Children.Add(swatches);
            preview.Children.Add(Ui.Text(palette.Name, Tokens.ItemTitle, bold: true));
            var detail = Ui.Text(palette.Description, Tokens.Label, Ui.Muted); detail.Margin = new(0, 4, 0, 10); preview.Children.Add(detail);
            var state = Ui.Text("", Tokens.Label, Ui.Accent); preview.Children.Add(state);
            var button = Ui.Button("", () => ApplyTheme(palette.Id));
            button.Content = preview; button.Tag = state;
            button.Width = 160; button.Padding = new(14); button.Margin = new(0, 0, 12, 12);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Ui.SetHoverBrush(button, Brushes.Transparent);
            System.Windows.Automation.AutomationProperties.SetName(button, "主题：" + palette.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Theme-" + palette.Id);
            themeChoices[palette.Id] = button; choices.Children.Add(button);
        }
        SyncThemeChoices(); body.Children.Add(choices);
        body.Children.Add(Ui.Text("同步调整按钮、选中状态和桌面玻璃底色；各分区的标识色保持独立。", Tokens.Label, Ui.Muted));
        panel.Children.Add(Ui.Card(body));
    }
    private void AddAiSettings(StackPanel panel)
    {
        var ai = new StackPanel(); ai.Children.Add(Ui.Text("AI 分析与归类", Tokens.SectionTitle, bold: true));
        ai.Children.Add(Ui.Text("配置 Base URL、API Key 和模型，根据文件名、快捷目标和现有分区生成建议。", Tokens.Secondary, Ui.Muted));
        var aiButton = Ui.Button("配置 AI / 分析桌面", OpenAi); aiButton.HorizontalAlignment = HorizontalAlignment.Left; aiButton.Margin = new Thickness(0, 12, 0, 0); ai.Children.Add(aiButton); panel.Children.Add(Ui.Card(ai));

    }
    private void AddGeneralSettings(StackPanel panel)
    {
        var daily = new StackPanel(); daily.Children.Add(Ui.Text("日常习惯", Tokens.SectionTitle, bold: true));
        var dailyGrid = Ui.FormGrid();
        var systemContent = new StackPanel(); systemContent.Children.Add(Ui.Text("在桌面保留 Windows 系统入口", Tokens.Secondary));
        var system = new CheckBox { Content = systemContent, IsChecked = organizer.State.Desktop.ShowSystemEntries };
        system.Click += (_, _) => Run(() => organizer.SetSystemEntries(system.IsChecked == true)); Ui.AddFormRow(dailyGrid, "系统入口", "遵循 Windows 桌面图标设置。", system, 0);
        var startup = new CheckBox { Content = Ui.Text("登录 Windows 后自动显示", Tokens.Secondary), IsChecked = !demo && StartupRegistration.Enabled, IsEnabled = !demo };
        startup.ToolTip = demo ? "演示模式不修改开机启动" : "登录当前 Windows 用户后显示桌面分区";
        startup.Click += (_, _) => { try { StartupRegistration.Set(startup.IsChecked == true); } catch (Exception ex) { startup.IsChecked = StartupRegistration.Enabled; ShowError(ex); } }; Ui.AddFormRow(dailyGrid, "开机启动", demo ? "隔离演示中不可用。" : "登录后自动整理桌面。", startup, 1);
        daily.Children.Add(dailyGrid); panel.Children.Add(Ui.Card(daily));

    }
    private void AddIntegrationSettings(StackPanel panel)
    {
        var integration = new StackPanel(); integration.Children.Add(Ui.Text("桌面右键菜单", Tokens.SectionTitle, bold: true));
        var enabled = new CheckBox { Content = "在桌面右键菜单添加 Lume 入口", IsChecked = organizer.State.Desktop.ContextMenuEnabled, IsEnabled = !demo };
        enabled.Click += (_, _) =>
        {
            var before = organizer.State.Desktop.ContextMenuEnabled;
            try
            {
                if (enabled.IsChecked == true) DesktopMenu.Register(); else DesktopMenu.Unregister();
                try { organizer.SetContextMenuEnabled(enabled.IsChecked == true); }
                catch { if (before) DesktopMenu.Register(); else DesktopMenu.Unregister(); throw; }
            }
            catch (Exception ex) { enabled.IsChecked = before; ShowError(ex); }
        };
        integration.Children.Add(enabled);
        integration.Children.Add(Ui.Text(demo ? "演示模式不修改系统右键菜单。" : "在桌面空白处右键 → Lume 桌面整理。Windows 11 默认菜单需点「显示更多选项」。只为当前用户注册，关闭此选项即可移除。", Tokens.Label, Ui.Muted));
        panel.Children.Add(Ui.Card(integration));
    }
    private void AddRootsSettings(StackPanel panel)
    {
        var folders = Ui.FormGrid();
        var mapping = Ui.Button("新建映射", MapFolderFromMenu); mapping.Width = 88; Ui.AddFormRow(folders, "文件夹映射", "把常用目录映射为分区。", mapping, 0);
        var recent = Ui.Button("显示", RecentFromMenu); recent.Width = 88; Ui.AddFormRow(folders, "最近文件", "依据已关注目录的修改时间，最多显示 40 项。", recent, 1);
        panel.Children.Add(Ui.Card(folders));
        var roots = new StackPanel(); roots.Children.Add(Ui.Text("关注的目录", Tokens.SectionTitle, bold: true));
        var hint = Ui.Text("只扫描目录第一层，跳过隐藏项、系统项和符号链接；云占位文件仅读取元数据。", Tokens.Label, Ui.Muted); hint.Margin = new Thickness(0, 8, 0, 16); roots.Children.Add(hint);
        foreach (var root in organizer.State.Configuration.Roots)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = Ui.Button("移除", () => { Run(() => organizer.SetRoots(organizer.State.Configuration.Roots.Where(p => p != root))); _ = RefreshAsync(); });
            remove.Width = 72; remove.IsEnabled = organizer.State.Configuration.Roots.Count > 1; remove.Foreground = Ui.Danger; DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove); line.Children.Add(Ui.Text(root, Tokens.Secondary)); roots.Children.Add(line);
        }
        var add = Ui.Button("＋ 添加目录", () =>
        {
            var dialog = new OpenFolderDialog { Title = "选择需要关注的目录" };
            if (dialog.ShowDialog(this) == true) { Run(() => organizer.SetRoots(organizer.State.Configuration.Roots.Append(dialog.FolderName))); _ = RefreshAsync(); }
        }); add.HorizontalAlignment = HorizontalAlignment.Left; roots.Children.Add(add); panel.Children.Add(Ui.Card(roots));
    }
    private void AddCollectionsSettings(StackPanel panel)
    {
        var layoutGrid = Ui.FormGrid();
        var layoutActions = Ui.Row(Ui.Button("排列", ArrangeFromMenu), Ui.Button("备份", BackupLayoutFromMenu), Ui.Button("恢复", RestoreLayoutFromMenu));
        Ui.AddFormRow(layoutGrid, "布局", "排列、备份或恢复桌面分区。", layoutActions, 0);
        var collapseActions = Ui.Row(Ui.Button("折叠", () => SetAllCollapsedFromMenu(true)), Ui.Button("展开", () => SetAllCollapsedFromMenu(false)));
        Ui.AddFormRow(layoutGrid, "分区折叠", "一键收起或展开全部分区。", collapseActions, 1);
        panel.Children.Add(Ui.Card(layoutGrid));
        var modes = new StackPanel(); modes.Children.Add(Ui.Text("分区可见性", Tokens.SectionTitle, bold: true));
        var warning = Ui.Text("切换视图会同步桌面分区。演示视图隐藏未勾选分区，但不会隐藏其他应用窗口。", Tokens.Label, Ui.Muted); warning.Margin = new Thickness(0, 8, 0, 16); modes.Children.Add(warning);
        var modeGrid = Ui.FormGrid();
        var modeRow = 0;
        foreach (var collection in organizer.State.Configuration.Collections)
        {
            var work = new CheckBox { Content = "工作视图", IsChecked = collection.InWork };
            var presentation = new CheckBox { Content = "演示视图", IsChecked = collection.InPresentation };
            void Save(object sender, RoutedEventArgs e) => Run(() => organizer.SetVisibility(collection.Id, work.IsChecked == true, presentation.IsChecked == true));
            work.Click += Save; presentation.Click += Save;
            var rename = Ui.Button("重命名", () =>
            {
                var value = Ui.Prompt(this, "重命名分区", "分区名称", collection.Name);
                if (value != null) Run(() => organizer.RenameCollection(collection.Id, value));
            });
            var remove = Ui.Button("删除", () => Run(() => organizer.DeleteCollection(collection.Id))); remove.Foreground = Ui.Danger;
            remove.IsEnabled = collection.Id != "inbox"; remove.ToolTip = "同时移除指向此分区的规则。文件不会删除，可以撤销恢复。";
            var actions = Ui.Row(work, presentation, rename, remove); Ui.AddFormRow(modeGrid, collection.Name, "工作视图 / 演示视图", actions, modeRow++);
        }
        modes.Children.Add(modeGrid);
        panel.Children.Add(Ui.Card(modes));
    }
    private void AddGlassSettings(StackPanel panel)
    {
        var glass = new StackPanel(); glass.Children.Add(Ui.Text("分区外观", Tokens.SectionTitle, bold: true));
        var appearanceGrid = Ui.FormGrid();
        var configuredSizes = organizer.State.Configuration.Collections.Select(c => organizer.Options(c.Id).IconSize).ToList();
        var sizes = configuredSizes.Distinct().ToList();
        var iconSize = BuildSegmented([("小", 26), ("中", 34), ("大", 48)], sizes.Count == 1 ? sizes[0] : -1, size =>
        {
            organizer.SetAllIconSize(size);
            DataChanged?.Invoke();
            status.Text = "已应用全局图标大小";
        });
        Ui.AddFormRow(appearanceGrid, "图标大小", sizes.Count > 1 ? "当前各分区大小不同，选择后统一。" : "全部分区统一调整。", iconSize, 0);
        var snap = new CheckBox { Content = Ui.Text("显示对齐线并吸附", Tokens.Secondary), IsChecked = organizer.State.Desktop.SnapEnabled };
        snap.Click += (_, _) =>
        {
            organizer.SetSnap(snap.IsChecked == true);
            DataChanged?.Invoke();
            status.Text = "已更新对齐吸附设置";
        };
        Ui.AddFormRow(appearanceGrid, "对齐吸附", "拖动时自动对齐；按住 Alt 临时关闭。", snap, 1);
        glass.Children.Add(appearanceGrid);
        var opacity = new Slider { Minimum = 15, Maximum = 240, Value = Math.Clamp(organizer.State.Desktop.GlassOpacity, (byte)15, (byte)240), Margin = new Thickness(0, 16, 0, 8), TickFrequency = 5, IsSnapToTickEnabled = true };
        void ApplyOpacity(bool commit)
        {
            var val = (byte)Math.Clamp((int)opacity.Value, 15, 240);
            if (commit)
            {
                organizer.SetGlassOpacity(val);
                status.Text = "已保存底色深浅";
            }
            else
            {
                organizer.State.Desktop.GlassOpacity = val;
            }
            DataChanged?.Invoke();
        }
        opacity.ValueChanged += (_, _) =>
        {
            if (opacity.IsMouseCaptureWithin || opacity.IsFocused)
                ApplyOpacity(false);
        };
        opacity.PreviewMouseLeftButtonUp += (_, _) => ApplyOpacity(true);
        opacity.KeyUp += (_, _) => ApplyOpacity(true);
        glass.Children.Add(opacity); glass.Children.Add(Ui.Text("调整分区底色深浅。向左更通透清爽，向右更深沉对比。", Tokens.Label, Ui.Muted)); panel.Children.Add(Ui.Card(glass));
    }
    private void AddDataSettings(StackPanel panel)
    {
        var data = new StackPanel(); data.Children.Add(Ui.Text("本地数据与恢复", Tokens.SectionTitle, bold: true));
        var dataGrid = Ui.FormGrid(); Ui.AddFormRow(dataGrid, "配置文件", store.Path, Ui.Button("打开目录", () => ShellOpen(Path.GetDirectoryName(store.Path)!)), 0);
        Ui.AddFormRow(dataGrid, "备份策略", "配置、外观和历史分开保存；恢复时保留原配置。", Ui.Button("校验并恢复备份", () =>
        {
            try
            {
                var backup = store.InspectBackup();
                if (MessageBox.Show(this, $"恢复 {backup.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm} 的备份？包含 {backup.Collections} 个分区、{backup.HistoryEntries} 条历史。原配置会另行保留。", "恢复配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                Run(organizer.RestoreBackup); RefreshView();
            }
            catch (Exception ex) { ShowError(ex); }
        }), 1);
        Ui.AddFormRow(dataGrid, "快捷键", "Ctrl+K 命令面板 · Ctrl+F 搜索 · Ctrl+Z 撤销 · Ctrl+Alt+1 / 2 / 3 切换视图。", Ui.Text("已启用", Tokens.Secondary, Ui.Brush(Tokens.Success600), true), 2);
        Ui.AddFormRow(dataGrid, "运行诊断", "日志最多约 1 MiB，只记录耗时、数量和错误码，不包含文件名、路径或密钥。", Ui.Button("导出脱敏诊断…", () =>
        {
            if (diagnostics == null) { status.Text = "当前验收模式未启用运行诊断"; return; }
            var dialog = new SaveFileDialog { Filter = "ZIP 诊断包|*.zip", FileName = "Lume-诊断.zip" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                diagnostics.Export(dialog.FileName, new(organizer.Files.Count, organizer.WatchRoots.Count, organizer.State.Configuration.Collections.Count,
                    organizer.State.History.Count + organizer.State.HistoryArchives.Sum(a => a.Count), IsDesktopPaused?.Invoke() ?? false));
                status.Text = "已导出脱敏诊断";
            }
            catch (Exception ex) { ShowError(ex); }
        }), 3);
        data.Children.Add(dataGrid);
        panel.Children.Add(Ui.Card(data));
    }
}
