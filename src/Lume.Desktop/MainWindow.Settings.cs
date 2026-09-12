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
        foreach (var name in new[] { "常规", "分区", "目录", "AI 与数据" })
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
            button.Background = name == settingsSection ? Tokens.Brush(Tokens.Primary100) : Brushes.Transparent;
            button.Foreground = name == settingsSection ? Ui.Accent : Ui.Muted;
            button.BorderThickness = new(0);
        }
        var panel = new StackPanel();
        switch (settingsSection)
        {
            case "分区": AddCollectionsSettings(panel); AddGlassSettings(panel); break;
            case "目录": AddRootsSettings(panel); break;
            case "AI 与数据": AddAiSettings(panel); AddDataSettings(panel); break;
            default: AddGeneralSettings(panel); AddIntegrationSettings(panel); break;
        }
        settingsBody.Content = panel;
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
        var iconSize = BuildSegmented([("小", 26), ("中", 34), ("大", 48)], sizes.Count == 1 ? sizes[0] : -1, size => Run(() => organizer.SetAllIconSize(size)));
        Ui.AddFormRow(appearanceGrid, "图标大小", sizes.Count > 1 ? "当前各分区大小不同，选择后统一。" : "全部分区统一调整。", iconSize, 0);
        var snap = new CheckBox { Content = Ui.Text("显示对齐线并吸附", Tokens.Secondary), IsChecked = organizer.State.Desktop.SnapEnabled };
        snap.Click += (_, _) => Run(() => organizer.SetSnap(snap.IsChecked == true)); Ui.AddFormRow(appearanceGrid, "对齐吸附", "拖动时自动对齐；按住 Alt 临时关闭。", snap, 1);
        glass.Children.Add(appearanceGrid);
        var opacity = new Slider { Minimum = 150, Maximum = 235, Value = Math.Clamp(organizer.State.Desktop.GlassOpacity, (byte)150, (byte)235), Margin = new Thickness(0, 16, 0, 8), TickFrequency = 5, IsSnapToTickEnabled = true };
        opacity.PreviewMouseLeftButtonUp += (_, _) => Run(() => organizer.SetGlassOpacity((byte)opacity.Value));
        opacity.KeyUp += (_, _) => Run(() => organizer.SetGlassOpacity((byte)opacity.Value));
        glass.Children.Add(opacity); glass.Children.Add(Ui.Text("调整分区底色深浅。静态壁纸柔化，文字保持清晰。", Tokens.Label, Ui.Muted)); panel.Children.Add(Ui.Card(glass));
    }
    private void AddDataSettings(StackPanel panel)
    {
        var data = new StackPanel(); data.Children.Add(Ui.Text("本地数据与恢复", Tokens.SectionTitle, bold: true));
        var dataGrid = Ui.FormGrid(); Ui.AddFormRow(dataGrid, "配置文件", store.Path, Ui.Button("打开目录", () => ShellOpen(Path.GetDirectoryName(store.Path)!)), 0);
        Ui.AddFormRow(dataGrid, "备份策略", "每次保存保留一份 .bak；配置异常时停止加载，不覆盖原文件。", Ui.Text("自动", Tokens.Secondary, Ui.Brush(Tokens.Success600), true), 1);
        Ui.AddFormRow(dataGrid, "快捷键", "Ctrl+K 命令面板 · Ctrl+F 搜索 · Ctrl+Z 撤销 · Ctrl+Alt+1 / 2 / 3 切换视图。", Ui.Text("已启用", Tokens.Secondary, Ui.Brush(Tokens.Success600), true), 2);
        data.Children.Add(dataGrid);
        panel.Children.Add(Ui.Card(data));
    }
}
