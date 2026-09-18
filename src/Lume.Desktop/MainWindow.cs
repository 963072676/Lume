using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Lume.Core;
using Microsoft.Win32;

namespace Lume.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly Organizer organizer;
    private readonly StateStore store;
    private readonly RuntimeDiagnostics? diagnostics;
    private readonly bool demo;
    private readonly bool smoke;
    private readonly ContentControl content = new();
    private readonly TextBlock status = Ui.Text("正在读取文件…", Tokens.Secondary, Ui.Muted);
    private readonly TextBlock heading = Ui.Text("我的桌面", Tokens.PageTitle, bold: true);
    private readonly TextBlock summary = Ui.Text("", Tokens.Secondary, Ui.Muted);
    private readonly TextBox search = new() { Width = 220, Height = 34, ToolTip = "搜索文件名或来源；空格分隔多个关键词", Margin = new(0, 0, 8, 0) };
    private readonly DockPanel toolbar = new();
    private readonly StackPanel searchTools = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel viewSwitcher = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel titleActions = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock shortcutHint = Ui.Text("Ctrl+K 命令面板  ·  Ctrl+Z 撤销上一步", Tokens.Secondary, Ui.Muted);
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer periodic = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DesktopScanSession scanner = new();
    private readonly HashSet<string> dirtyRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> pendingRoots = new(StringComparer.OrdinalIgnoreCase);
    private int pendingRefresh;
    private DateTime lastFullScanUtc = DateTime.MinValue;
    private bool fullScanRequested = true;
    private readonly List<string> watcherWarnings = [];
    private readonly Dictionary<string, Button> navigation = [];
    private readonly TileSelection boardSelection = new();
    private string? activeCollection;
    private int filePage;
    private const int FilesPerPage = 80;
    private string settingsSection = "常规";
    private ContentControl settingsBody = new();
    private readonly Dictionary<string, Button> settingsTabs = [];
    private string page = "桌面";
    private string rootsSignature = "";
    private bool refreshing;
    private bool firstScan = true;
    private bool refreshAgain;
    private bool closed;
    private int selectedMode;
    private CommandPalette? commandPalette;
    public int VerificationExitCode { get; private set; }
    public event Action? DataChanged;
    public ArchiveService? Archives { get; set; }
    public bool Resident { get; set; }
    public bool Exiting { get; set; }
    public Func<string, Task>? GlobalCommand { get; set; }
    internal Func<bool>? IsDesktopPaused { get; set; }
    private bool monitoringStarted;

    public MainWindow(Organizer organizer, StateStore store, bool demo, bool smoke, RuntimeDiagnostics? diagnostics = null)
    {
        this.organizer = organizer; this.store = store; this.demo = demo; this.smoke = smoke; this.diagnostics = diagnostics;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Lume · 桌面整理" + (demo ? " — 隔离演示" : "");
        Width = 1000; Height = 720; MinWidth = 840; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (smoke) { WindowStartupLocation = WindowStartupLocation.Manual; Left = -16000; Top = 0; ShowActivated = false; }
        selectedMode = Math.Clamp(organizer.State.Desktop.Mode, 0, 2);
        var layout = new DockPanel();
        var navigationBar = BuildNavigation(); DockPanel.SetDock(navigationBar, Dock.Top); layout.Children.Add(navigationBar);
        var main = new DockPanel { Margin = new(28, 24, 28, 18) }; layout.Children.Add(main);
        var top = new StackPanel { Margin = new(0, 0, 0, 20) }; DockPanel.SetDock(top, Dock.Top);
        var titleRow = new DockPanel();
        titleActions.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(titleActions, Dock.Right); titleRow.Children.Add(titleActions); titleRow.Children.Add(heading); top.Children.Add(titleRow);
        summary.Margin = new(0, 6, 0, 18); top.Children.Add(summary);
        BuildToolbar(); top.Children.Add(toolbar);
        search.TextChanged += (_, _) => { filePage = 0; if (page is "桌面" or "收件箱" or "智能规则") Render(); };
        main.Children.Add(top);
        var bottom = new DockPanel { Margin = new(0, 14, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom);
        shortcutHint.Text = "Ctrl+K 快速操作";
        DockPanel.SetDock(shortcutHint, Dock.Right); bottom.Children.Add(shortcutHint); bottom.Children.Add(status);
        status.TextTrimming = TextTrimming.CharacterEllipsis; status.TextWrapping = TextWrapping.NoWrap;
        status.ToolTip = "关闭窗口后继续在托盘运行";
        main.Children.Add(bottom); main.Children.Add(content); Content = layout;

        debounce.Tick += async (_, _) => { debounce.Stop(); await RefreshAsync(false); };
        periodic.Tick += async (_, _) => { diagnostics?.Heartbeat(); await RefreshAsync(false); };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K) { OpenCommandPalette(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && searchTools.Visibility == Visibility.Visible) { search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && Keyboard.FocusedElement is not TextBox) { Run(() => organizer.Undo()); e.Handled = true; }
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key is Key.D1 or Key.D2 or Key.D3) { SetMode(e.Key - Key.D1); Navigate("桌面"); e.Handled = true; }
            else if (page is "桌面" or "收件箱") HandleFileKeys(boardSelection, e);
        };

        Loaded += async (_, _) =>
        {
            await StartMonitoringAsync();
#if VERIFICATION
            if (smoke) await SmokeAsync();
#endif
        };
        Closing += (_, e) => { if (Resident && !Exiting) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { closed = true; preview?.Close(); periodic.Stop(); debounce.Stop(); foreach (var watcher in watchers) watcher.Dispose(); };
    }
    public async Task StartMonitoringAsync() { if (monitoringStarted) return; monitoringStarted = true; await RefreshAsync(); periodic.Start(); }
    public void ShowSettings() { Render(); Show(); WindowState = WindowState.Normal; Activate(); }
    public void RefreshView() { if (IsVisible || smoke) Render(); DataChanged?.Invoke(); _ = RefreshAsync(); }
    internal void RequestEnvironmentalRefresh() { fullScanRequested = true; ScheduleRefresh(); }
    public void ShowPage(string target) { Navigate(target); ShowSettings(); }
    public void CreateCollectionFromMenu() { ShowSettings(); AddCollection(); }
    public void UndoFromMenu() => Run(() => organizer.Undo());
    private void OpenAi() => OpenAiFromMenu(false);
    public void SetAllCollapsedFromMenu(bool collapsed) => Run(() => organizer.SetAllCollapsed(collapsed));
    public void OpenAiFromMenu(bool startAnalysis = true)
    {
        ShowSettings();
        new AiAnalysisDialog(this, organizer, Path.GetDirectoryName(store.Path)!, () => Run(() => { }), startAnalysis: startAnalysis).ShowDialog();
    }
    public void MapFolderFromMenu()
    {
        ShowSettings(); var dialog = new OpenFolderDialog { Title = "选择要映射到桌面的文件夹" };
        if (dialog.ShowDialog(this) == true) { Run(() => organizer.AddMappedCollection(dialog.FolderName)); _ = RefreshAsync(); }
    }
    public void RecentFromMenu() => Run(organizer.AddRecentCollection);
    public void BackupLayoutFromMenu()
    {
        var dialog = new SaveFileDialog { Filter = "Lume 布局|*.lume-layout.json", FileName = "桌面布局.lume-layout.json" };
        if (dialog.ShowDialog() == true) Run(() => LayoutBackup.Write(dialog.FileName, organizer.State.Desktop));
    }
    public void RestoreLayoutFromMenu()
    {
        var dialog = new OpenFileDialog { Filter = "Lume 布局|*.json" };
        if (dialog.ShowDialog() == true) Run(() =>
        {
            var snapshot = LayoutBackup.Read(dialog.FileName);
            LayoutBackup.Write(Path.Combine(Path.GetDirectoryName(store.Path)!, "layout-before-restore.json"), organizer.State.Desktop);
            organizer.RestoreDesktop(LayoutBackup.Apply(snapshot, organizer.State.Desktop, organizer.State.Configuration.Collections.Select(c => c.Id)));
        });
    }
    public void ArrangeFromMenu() => Run(() =>
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var selected = organizer.State.Configuration.Collections.Where(c => !organizer.Options(c.Id).Locked).ToList();
        var positions = StateStore.Clone(organizer.State.Desktop.Positions);
        var arranged = LayoutEngine.Arrange(selected.Count, new(area.X, area.Y, area.Width, area.Height), positions.Where(p => organizer.Options(p.Key).Locked && organizer.State.Configuration.Collections.Any(c => c.Id == p.Key)).Select(p => p.Value));
        for (var i = 0; i < selected.Count; i++) positions[selected[i].Id] = arranged[i];
        organizer.SetLayout(positions);
    });
    public void SetViewFromMenu(int value) => SetMode(value);
    public void OpenArchive(string? collectionId = null)
    {
        if (Archives == null) return;
        ShowSettings();
        new ArchiveDialog(this, organizer, Archives, collectionId, Path.GetDirectoryName(store.Path)).ShowDialog();
        _ = RefreshAsync();
    }

    private void BuildToolbar()
    {
        toolbar.Children.Clear();
        viewSwitcher.Children.Clear();
        foreach (var (label, value) in new[] { ("全部", 0), ("工作", 1), ("演示", 2) })
        {
            var button = Ui.Button(label, () => SetMode(value));
            button.Width = 58; button.Height = 32; button.MinHeight = 32; button.Padding = new Thickness(4, 4, 4, 4);
            button.HorizontalContentAlignment = HorizontalAlignment.Center; button.Margin = new Thickness(value == 0 ? 0 : 1, 0, 0, 0);
            viewSwitcher.Children.Add(button);
        }
        DockPanel.SetDock(viewSwitcher, Dock.Right); toolbar.Children.Add(viewSwitcher);
        searchTools.Children.Clear();
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索当前视图文件");
        var caption = Ui.Text("搜索  ", Tokens.Secondary, Ui.Muted); searchTools.Children.Add(caption);
        searchTools.Children.Add(search);
        var refresh = Ui.Button("刷新", () => _ = RefreshAsync()); refresh.Height = 34; refresh.MinHeight = 34; refresh.Padding = new Thickness(12, 6, 12, 6); searchTools.Children.Add(refresh);
        toolbar.Children.Add(searchTools);
        UpdateModeVisuals();
    }

    private void SetMode(int value)
    {
        selectedMode = Math.Clamp(value, 0, 2);
        if (organizer.State.Desktop.Mode != selectedMode) organizer.SetDesktopMode(selectedMode);
        filePage = 0; boardSelection.Clear(); UpdateModeVisuals(); Render(); DataChanged?.Invoke();
    }

    private void UpdateModeVisuals()
    {
        foreach (var (button, index) in viewSwitcher.Children.OfType<Button>().Select((button, index) => (button, index)))
        {
            var active = index == selectedMode;
            if (active) Ui.UseStyle(button, "PrimaryButton"); else Ui.UseDefaultStyle(button);
            button.Background = active ? Ui.Accent : Tokens.Brush(Tokens.Surface0);
            button.Foreground = active ? Brushes.White : Ui.SecondaryInk;
            button.BorderBrush = active ? Ui.Accent : Tokens.Brush(Tokens.Line200);
            button.ToolTip = index switch { 0 => "显示全部分区", 1 => "只显示工作视图分区", _ => "只显示演示视图分区" };
        }
    }

    private static Border BuildSegmented((string Label, int Value)[] options, int selectedValue, Action<int> changed)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<Button>();
        void Sync(int value)
        {
            foreach (var (button, option) in buttons.Zip(options, (button, option) => (button, option)))
            {
                var active = option.Value == value;
                if (active) Ui.UseStyle(button, "PrimaryButton"); else Ui.UseDefaultStyle(button);
                button.Background = active ? Ui.Accent : Tokens.Brush(Tokens.Surface0);
                button.Foreground = active ? Brushes.White : Ui.SecondaryInk;
                button.BorderBrush = active ? Ui.Accent : Tokens.Brush(Tokens.Line200);
            }
        }
        foreach (var (label, value) in options)
        {
            var button = Ui.Button(label, () => { Sync(value); changed(value); }); button.Width = 40; button.Height = 30; button.MinHeight = 30; button.Padding = new Thickness(4); button.HorizontalContentAlignment = HorizontalAlignment.Center; button.Margin = new Thickness(buttons.Count == 0 ? 0 : 1, 0, 0, 0); buttons.Add(button); host.Children.Add(button);
        }
        Sync(selectedValue);
        return new Border { Child = host, BorderBrush = Tokens.Brush(Tokens.Line200), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Tokens.Radius8), Padding = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right };
    }

    private void BuildPageActions()
    {
        titleActions.Children.Clear();
        Button More()
        {
            Button more = null!;
            more = Ui.Button("⋯ 更多", () => OpenMoreMenu(more));
            more.Padding = new Thickness(12, 7, 12, 7);
            return more;
        }

        switch (page)
        {
            case "桌面":
                titleActions.Children.Add(More());
                titleActions.Children.Add(Ui.Button("AI 整理", OpenAi, true));
                break;
            case "收件箱":
                titleActions.Children.Add(More()); titleActions.Children.Add(Ui.Button("AI 整理", OpenAi, true));
                break;
            case "智能规则":
                titleActions.Children.Add(Ui.Button("AI 分析与归类", OpenAi));
                titleActions.Children.Add(Ui.Button("＋ 创建规则", () => EditRule(), true));
                break;
            case "整理历史":
                var undo = Ui.Button("↶ 撤销上一步", () => Run(() => organizer.Undo()), true); undo.IsEnabled = organizer.CanUndo;
                titleActions.Children.Add(More()); titleActions.Children.Add(undo);
                break;
        }
    }

    private void OpenMoreMenu(Button owner)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "打开命令面板    Ctrl+K", OpenCommandPalette);
        AddMenu(menu, "物理归档…", () => OpenArchive());
        AddMenu(menu, "撤销上一步    Ctrl+Z", () => Run(() => organizer.Undo()));
        menu.Items.Add(new Separator());
        AddMenu(menu, "刷新当前视图", () => _ = RefreshAsync());
        AddMenu(menu, "打开帮助", () => MessageBox.Show(this, "Ctrl+K 命令面板 · Ctrl+F 搜索当前位置 · Ctrl+Z 撤销上一步\n桌面卡片可拖动标题排列，双击标题折叠。", "Lume 帮助", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.PlacementTarget = owner; menu.IsOpen = true;
    }

    private void OpenCommandPalette()
    {
        if (commandPalette?.IsVisible == true) { commandPalette.Activate(); return; }
        commandPalette = new CommandPalette(this, organizer, RunMenuCommand, OpenFile, FocusCollection);
        commandPalette.ShowDialog();
    }

    internal async Task RunMenuCommand(string command)
    {
        if (GlobalCommand != null) { await GlobalCommand(command); return; }
        switch (command)
        {
            case "settings": ShowPage("设置"); break;
            case "new-collection": CreateCollectionFromMenu(); break;
            case "refresh": await RefreshAsync(); break;
            case "map-folder": MapFolderFromMenu(); break;
            case "recent": RecentFromMenu(); break;
            case "arrange": ArrangeFromMenu(); break;
            case "backup-layout": BackupLayoutFromMenu(); break;
            case "restore-layout": RestoreLayoutFromMenu(); break;
            case "ai-organize": OpenAiFromMenu(); break;
            case "rules": ShowPage("智能规则"); break;
            case "history": ShowPage("整理历史"); break;
            case "undo": UndoFromMenu(); break;
            case "archive": OpenArchive(); break;
            case "collapse-all": SetAllCollapsedFromMenu(true); break;
            case "expand-all": SetAllCollapsedFromMenu(false); break;
            case "mode-all": SetViewFromMenu(0); break;
            case "mode-work": SetViewFromMenu(1); break;
            case "mode-presentation": SetViewFromMenu(2); break;
            default: status.Text = "此操作需要桌面驻留模式"; break;
        }
    }

    internal void FocusCollection(string id)
    {
        activeCollection = id; SetMode(0);
        search.Clear(); Navigate("桌面");
    }

    private Border BuildNavigation()
    {
        var panel = new DockPanel { Margin = new(28, 16, 24, 16) };
        var brand = Ui.Row(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/lume.png")), Width = 28, Height = 28, Margin = new(0, 0, 8, 0) }, Ui.Text("lume", 22, bold: true));
        brand.Margin = new(0, 0, 30, 0); DockPanel.SetDock(brand, Dock.Left); panel.Children.Add(brand);
        var quick = Ui.Button("⌕  快速操作", OpenCommandPalette); quick.ToolTip = "搜索文件与全部操作 · Ctrl+K"; quick.Margin = new(0); DockPanel.SetDock(quick, Dock.Right); panel.Children.Add(quick);
        var links = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (key, caption) in new[] { ("桌面", "桌面"), ("收件箱", "待整理"), ("智能规则", "规则"), ("整理历史", "历史"), ("设置", "设置") })
        {
            var button = Ui.Button(caption, () => Navigate(key));
            button.Padding = new(16, 8, 16, 8); button.Margin = new(0, 0, 4, 0);
            button.BorderThickness = new(0); System.Windows.Automation.AutomationProperties.SetName(button, key);
            links.Children.Add(button); navigation[key] = button;
        }
        panel.Children.Add(links);
        return new Border { Background = Brushes.White, BorderBrush = Tokens.Brush(Tokens.Line100), BorderThickness = new(0, 0, 0, 1), Child = panel };
    }

    private void UpdateNavVisuals()
    {
        foreach (var (label, button) in navigation)
        {
            var active = label == page;
            button.Background = active ? Tokens.Primary100Brush : Brushes.Transparent;
            button.Foreground = active ? Ui.Accent : Ui.Muted;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void Navigate(string target)
    {
        page = target; filePage = 0; boardSelection.Clear(); search.Clear(); Render();
    }

    private void Run(Action action)
    {
        try { action(); ConfigureWatchers(); Render(); DataChanged?.Invoke(); status.Text = "已保存 · 可在整理历史中查看和撤销"; }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex)
    {
        diagnostics?.Record(DiagnosticKind.OperationFailed, error: ex);
        status.Text = "操作未完成：" + ex.Message;
        if (!smoke) MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private async Task RefreshAsync(bool force = true)
    {
        if (closed) return;
        fullScanRequested |= force;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                ConfigureWatchers();
                var roots = organizer.WatchRoots;
                var linked = organizer.State.Configuration.LinkedFiles.ToList();
                var full = fullScanRequested || watcherWarnings.Count > 0 || DateTime.UtcNow - lastFullScanUtc >= TimeSpan.FromMinutes(5);
                fullScanRequested = false;
                var dirty = dirtyRoots.ToArray(); dirtyRoots.Clear();
                ScanResult scan;
                var scanClock = Stopwatch.StartNew();
                try { scan = await Task.Run(() => scanner.Scan(roots, linked, dirty, full)); }
                catch { fullScanRequested = true; throw; }
                if (full) lastFullScanUtc = DateTime.UtcNow;
                diagnostics?.Record(DiagnosticKind.Scan, scanClock.Elapsed.TotalMilliseconds, scan.Files.Count, scan.Warnings.Count, scanner.LastScannedDirectories);
                if (closed) return;
                if (!roots.SequenceEqual(organizer.WatchRoots, StringComparer.OrdinalIgnoreCase) || !linked.SequenceEqual(organizer.State.Configuration.LinkedFiles, StringComparer.OrdinalIgnoreCase)) { refreshAgain = true; continue; }
                var changed = !organizer.Files.SequenceEqual(scan.Files);
                var ageRules = organizer.State.Configuration.Rules.Any(r => r.Enabled && r.Conditions.Any(c => c.Field is "createdDays" or "modifiedDays"));
                var classified = organizer.ApplyScan(scan, changed || firstScan || ageRules); ConfigureWatchers();
                if (changed || firstScan || classified)
                {
                    var renderClock = Stopwatch.StartNew();
                    firstScan = false; if (IsVisible || smoke) Render();
                    DataChanged?.Invoke();
                    diagnostics?.Record(DiagnosticKind.Refresh, renderClock.Elapsed.TotalMilliseconds, scan.Files.Count);
                }
                var problems = organizer.Warnings.Concat(watcherWarnings).ToList();
                status.Text = problems.Count > 0 ? $"{problems.Count} 条提示 · {problems[0]}" : $"● 正在关注 {roots.Count} 个目录 · 上次同步 {DateTime.Now:HH:mm:ss}";
                status.ToolTip = string.Join("\n", problems);
            } while (refreshAgain);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { refreshing = false; }
    }
    private void ConfigureWatchers()
    {
        var roots = organizer.MonitorRoots;
        var signature = string.Join("|", roots.Select(r => r + ":" + Directory.Exists(r)));
        if (signature == rootsSignature) return;
        fullScanRequested = true;
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); watcherWarnings.Clear();
        rootsSignature = signature;
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size };
                watcher.Created += OnFileChanged; watcher.Changed += OnFileChanged; watcher.Deleted += OnFileChanged; watcher.Renamed += OnFileChanged;
                watcher.Error += (_, error) => Dispatcher.BeginInvoke(() => { diagnostics?.Record(DiagnosticKind.WatcherError, error: error.GetException()); rootsSignature = ""; ScheduleRefresh(); });
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { diagnostics?.Record(DiagnosticKind.WatcherError, error: ex); watcherWarnings.Add($"实时监控不可用，使用每 30 秒扫描：{root}"); rootsSignature = ""; }
        }
    }
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (sender is FileSystemWatcher watcher)
        {
            pendingRoots[watcher.Path] = 0;
            if (Interlocked.Exchange(ref pendingRefresh, 1) != 0) return;
            Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref pendingRefresh, 0);
                foreach (var root in pendingRoots.Keys) { pendingRoots.TryRemove(root, out _); dirtyRoots.Add(root); }
                if (!closed) ScheduleRefresh();
            });
        }
    }
    private void ScheduleRefresh() { if (!closed) { debounce.Stop(); debounce.Start(); } }

    private void Render()
    {
        if (Tokens.Theme.Id != ThemeIds.Normalize(organizer.State.Desktop.Theme)) Tokens.ApplyTheme(organizer.State.Desktop.Theme);
        BuildPageActions();
        var searchable = page is "桌面" or "收件箱" or "智能规则";
        toolbar.Visibility = searchable ? Visibility.Visible : Visibility.Collapsed;
        searchTools.Visibility = searchable ? Visibility.Visible : Visibility.Collapsed;
        viewSwitcher.Visibility = page == "桌面" ? Visibility.Visible : Visibility.Collapsed;
        UpdateModeVisuals(); UpdateNavVisuals();
        heading.Text = page switch { "桌面" => "桌面，井然有序。", "收件箱" => "待整理", "智能规则" => "自动归类", "整理历史" => "整理记录", _ => "偏好设置" };
        summary.Text = page switch
        {
            "桌面" => $"{organizer.Files.Count} 个项目 · {organizer.State.Configuration.Collections.Count} 个分区" + (demo ? " · 隔离演示" : ""),
            "收件箱" => "给新文件找个位置。拖入分区，或右键归类。",
            "智能规则" => "按顺序匹配，手动归属优先。",
            "整理历史" => "归类可撤销，归档可恢复。",
            _ => "简单设一次，其余交给 Lume。"
        };
        content.Content = page switch { "智能规则" => new ScrollViewer { Content = BuildRules() }, "整理历史" => new ScrollViewer { Content = BuildHistory() }, "设置" => BuildSettings(), _ => BuildBoard() };
    }


    private static void AddMenu(ContextMenu menu, string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private FilePreviewWindow? preview;
    private void PreviewFile(DesktopFile file) { preview?.Close(); preview = new FilePreviewWindow(file, Resident && !IsVisible); preview.Show(); preview.Activate(); }
    private void OpenFile(DesktopFile file)
    {
        if (demo) { status.Text = "演示文件只用于预览；可在真实目录模式下打开文件。"; return; }
        if (!File.Exists(file.Path) && !Directory.Exists(file.Path)) { status.Text = "文件已离开原位置，正在刷新。"; _ = RefreshAsync(); return; }
        ShellOpen(file.Path);
    }
    private void ShellOpen(string path, string? arguments = null)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = arguments ?? "" }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void AddCollection()
    {
        var name = Ui.Prompt(this, "新建分区", "给这个分区起个名字");
        if (name != null) Run(() => organizer.AddCollection(name));
    }
    private void EditRule(Rule? rule = null)
    {
        var dialog = new RuleDialog(this, organizer, rule, Path.GetDirectoryName(store.Path)!);
        if (dialog.ShowDialog() == true && dialog.Result != null) Run(() => organizer.SaveRule(dialog.Result));
    }

}
