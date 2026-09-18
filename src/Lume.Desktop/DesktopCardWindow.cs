using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Lume.Core;
using Forms = System.Windows.Forms;

namespace Lume.Desktop;

internal sealed class DesktopCardWindow : Window
{
    private readonly Organizer organizer;
    private readonly Func<DesktopFile, TileSelection, UIElement> tileFactory;
    private readonly Action refresh;
    private readonly WrapPanel files = new();
    internal TileSelection Selection { get; } = new();
    private readonly Border marker = new() { Width = 8, Height = 8, CornerRadius = new(4), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
    private readonly TextBlock title = Ui.Text("", 13, Brushes.White, true);    private readonly Border countBadge = Ui.Badge("0", Brushes.White, Tokens.WhiteAlpha(0x2E));
    private readonly TextBox search = new() { Height = 32, Background = Tokens.WhiteAlpha(0x12), Foreground = Brushes.White, BorderBrush = Tokens.WhiteAlpha(0x48), BorderThickness = new(1), Padding = new(8, 5, 8, 5), ToolTip = "在此分区搜索文件" };
    private readonly Grid searchHost = new() { Visibility = Visibility.Collapsed };
    private CardPlacement placement;
    private readonly IntPtr desktop;
    private string signature = "";
    private string glassSignature = "";
    private int filePage;
    private const int PageSize = 80;
    private readonly StackPanel pager = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border backdrop = new() { CornerRadius = new(12), Opacity = .32 };
    private readonly Border tint = new() { CornerRadius = new(12) };
    private readonly Func<string, CardPlacement, string, CardPlacement> adjust;
    private readonly Action finishAdjustment;
    private bool adjusting;
    private bool searchOpen;
    private readonly List<UIElement> collapsible = [];
    private readonly Button collapseButton;
    private readonly Button lockButton;
    public CardPlacement Placement => organizer.Options(CollectionId).Collapsed ? placement with { Height = 58 } : placement;
    public string CollectionId { get; }
    public IntPtr Handle { get; private set; }
    public bool GlassApplied { get; private set; }
    public bool NativeGlassApplied { get; private set; }

    public DesktopCardWindow(Organizer organizer, Collection collection, CardPlacement placement, IntPtr desktop,
        Func<DesktopFile, TileSelection, UIElement> tileFactory, Action refresh, Action settings, Action<string> archive,
        Func<string, CardPlacement, string, CardPlacement>? adjust = null, Action? finishAdjustment = null)
    {
        this.organizer = organizer; this.tileFactory = tileFactory; this.refresh = refresh; this.placement = placement; this.desktop = desktop; CollectionId = collection.Id;
        this.adjust = adjust ?? ((_, p, _) => p); this.finishAdjustment = finishAdjustment ?? (() => { });
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Lume.Desktop." + collection.Id; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
        Width = placement.Width; Height = placement.Height; Left = -16000;
        marker.Background = Ui.Brush(collection.Color);
        // 分区标题是卡片上最重要的文字，与文件名一样加描边兜底，避免浅色壁纸下不可辨。
        title.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 1, ShadowDepth = 0, Opacity = .8 };
        var body = new Grid { Margin = new(16, 12, 16, 12) };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new(0, 0, 0, 8), MinHeight = 32 };
        var menuButton = Ui.IconButton("⋯", () => { }, "打开分区更多菜单", true); menuButton.Margin = new(0);
        collapseButton = Ui.IconButton("▴", ToggleCollapsed, "折叠此分区", true); collapseButton.Margin = new(0, 0, 4, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(collapseButton, "ToggleCollectionCollapsed");
        lockButton = Ui.IconButton("", () => { organizer.SetOptions(CollectionId, organizer.Options(CollectionId) with { Locked = !organizer.Options(CollectionId).Locked }); UpdateFiles(true); }, "锁定分区", true);
        lockButton.FontFamily = new FontFamily("Segoe MDL2 Assets"); lockButton.Margin = new(0, 0, 4, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(lockButton, "ToggleCollectionLocked");
        var actions = Ui.Row(lockButton, collapseButton, menuButton); DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        countBadge.Margin = new(8, 0, 8, 0); DockPanel.SetDock(countBadge, Dock.Right); header.Children.Add(countBadge);
        DockPanel.SetDock(marker, Dock.Left); header.Children.Add(marker); header.Children.Add(title);
        var menu = new ContextMenu();
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        void Inline(string label, UIElement control)
        {
            var caption = Ui.Text(label, Tokens.Secondary, Ui.SecondaryInk, true); caption.Width = 72;
            var item = new MenuItem { Header = Ui.Row(caption, control), Padding = new Thickness(10, 5, 10, 5), HeaderTemplate = null };
            menu.Items.Add(item);
        }
        Add("打开搜索    Ctrl+F", ToggleSearch);
        menu.Items.Add(new Separator());
        var sortBox = new ComboBox { Width = 124, Height = 30, ItemsSource = new[] { ("手动顺序", "manual"), ("名称", "name"), ("类型", "type"), ("修改时间", "modified"), ("大小", "size") }, DisplayMemberPath = "Item1", SelectedValuePath = "Item2", SelectedValue = organizer.Options(CollectionId).Sort };
        sortBox.SelectionChanged += (_, _) => { if (sortBox.SelectedValue is string value) { organizer.SetOptions(CollectionId, organizer.Options(CollectionId) with { Sort = value }); UpdateFiles(true); } };
        Inline("排序", sortBox);
        var iconBox = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, size) in new[] { ("小", 26), ("中", 34), ("大", 48) })
        {
            var item = Ui.Button(label, () => { organizer.SetOptions(CollectionId, organizer.Options(CollectionId) with { IconSize = size }); UpdateFiles(true); }); item.Width = 34; item.Height = 28; item.MinHeight = 28; item.Padding = new Thickness(2); item.HorizontalContentAlignment = HorizontalAlignment.Center; item.Margin = new Thickness(1, 0, 0, 0); iconBox.Children.Add(item);
        }
        Inline("图标大小", iconBox);
        menu.Items.Add(new Separator());
        Add("折叠 / 展开", ToggleCollapsed);
        Add("移到下一块显示器", () =>
        {
            if (organizer.Options(CollectionId).Locked) return;
            var monitors = Forms.Screen.AllScreens; var index = Array.FindIndex(monitors, s => s.Bounds.Contains(placement.X, placement.Y));
            var area = monitors[(index + 1) % monitors.Length].WorkingArea; placement = placement with { X = area.Left + 30, Y = area.Top + 30 }; Position(); SavePosition();
        });
        menu.Items.Add(new Separator());
        Add("重命名分区…", () => { var name = Ui.Prompt(this, "重命名分区", "分区名称", collection.Name); if (name != null) { organizer.RenameCollection(CollectionId, name); refresh(); } });
        Add("归档此分区的旧文件…", () => archive(CollectionId));
        if (collection.MappedPath != null) Add("打开映射目录", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(collection.MappedPath) { UseShellExecute = true }));
        Add("打开设置与规则", settings);
        if (CollectionId != "inbox") { var dissolve = new MenuItem { Header = "解散分区（文件保留）", Foreground = Ui.Danger }; dissolve.Click += (_, _) => { organizer.DeleteCollection(CollectionId); refresh(); }; menu.Items.Add(dissolve); }
        menuButton.Click += (_, _) => { menu.PlacementTarget = menuButton; menu.IsOpen = true; };
        body.Children.Add(header);
        DesktopNative.Point dragStart = default; CardPlacement? origin = null;
        header.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (menuButton.IsMouseOver || collapseButton.IsMouseOver || lockButton.IsMouseOver) return;
            if (e.ClickCount == 2) { ToggleCollapsed(); e.Handled = true; return; }
            if (organizer.Options(CollectionId).Locked) return;
            adjusting = true;
            DesktopNative.GetCursorPos(out dragStart); origin = this.placement; header.CaptureMouse(); e.Handled = true;
        };
        header.MouseMove += (_, e) => { if (origin == null || !header.IsMouseCaptured) return; DesktopNative.GetCursorPos(out var current); this.placement = this.adjust(CollectionId, origin with { X = origin.X + current.X - dragStart.X, Y = origin.Y + current.Y - dragStart.Y }, "move"); Position(); };
        header.MouseLeftButtonUp += (_, _) => { if (!header.IsMouseCaptured) return; origin = null; header.ReleaseMouseCapture(); adjusting = false; this.finishAdjustment(); SavePosition(); };
        header.LostMouseCapture += (_, _) => { if (origin != null) { this.placement = origin; origin = null; Position(); } adjusting = false; this.finishAdjustment(); };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { ToggleSearch(); e.Handled = true; }
            else if (e.Key == Key.Escape && searchOpen) { ToggleSearch(); e.Handled = true; }
            else if (e.Key == Key.Escape && header.IsMouseCaptured) { header.ReleaseMouseCapture(); e.Handled = true; }
        };
        Grid.SetRow(searchHost, 1); searchHost.Margin = new Thickness(0, 0, 0, 8); searchHost.Children.Add(search); body.Children.Add(searchHost);
        search.TextChanged += (_, _) => { filePage = 0; UpdateFiles(true); };
        var scroll = new ScrollViewer { Content = files, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 2); body.Children.Add(scroll);
        collapsible.Add(searchHost); collapsible.Add(scroll);
        Grid.SetRow(pager, 3); body.Children.Add(pager); collapsible.Add(pager);
        var glassLayers = new Grid(); glassLayers.Children.Add(backdrop); glassLayers.Children.Add(tint); glassLayers.Children.Add(body);
        Content = new Border { CornerRadius = new(12), BorderBrush = Tokens.WhiteAlpha(0x38), BorderThickness = new(1), Child = glassLayers };
        foreach (var edge in new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW" })
        {
            var grip = new Thumb { Width = edge is "N" or "S" ? double.NaN : edge.Length == 2 ? 18 : 9, Height = edge is "E" or "W" ? double.NaN : edge.Length == 2 ? 18 : 9,
                HorizontalAlignment = edge.Contains('W') ? HorizontalAlignment.Left : edge.Contains('E') ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                VerticalAlignment = edge.Contains('N') ? VerticalAlignment.Top : edge.Contains('S') ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
                Cursor = edge is "N" or "S" ? Cursors.SizeNS : edge is "E" or "W" ? Cursors.SizeWE : edge is "NE" or "SW" ? Cursors.SizeNESW : Cursors.SizeNWSE };
            // 分层窗口的全透明像素会被 Windows 鼠标命中跳过，边角保留最低非零透明度。
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Ui.Brush("#01000000")); grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            CardPlacement? begin = null; DesktopNative.Point pointer = default;
            grip.DragStarted += (_, _) => { if (organizer.Options(CollectionId).Locked || organizer.Options(CollectionId).Collapsed) { grip.CancelDrag(); return; } begin = this.placement; DesktopNative.GetCursorPos(out pointer); adjusting = true; };
            grip.DragDelta += (_, _) => { if (begin == null) return; DesktopNative.GetCursorPos(out var current); this.placement = this.adjust(CollectionId, LayoutEngine.Resize(begin, edge, current.X - pointer.X, current.Y - pointer.Y), edge); Position(); };
            grip.DragCompleted += (_, e) => { if (begin == null) return; if (e.Canceled) this.placement = begin; begin = null; adjusting = false; Position(); this.finishAdjustment(); SavePosition(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && grip.IsDragging) { grip.CancelDrag(); e.Handled = true; } };
            glassLayers.Children.Add(grip);
        }
        PreviewKeyDown += (_, e) => (tileFactory.Target as MainWindow)?.HandleFileKeys(Selection, e);
        AllowDrop = true; DragOver += (_, e) => { e.Effects = collection.MappedPath == null && !collection.Recent && (e.Data.GetDataPresent("Lume.VirtualFile") || e.Data.GetDataPresent("Lume.VirtualFiles") || e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { try { var paths = e.Data.GetData("Lume.VirtualFiles") as string[] ?? (e.Data.GetData("Lume.VirtualFile") is string path ? new[] { path } : e.Data.GetData(DataFormats.FileDrop) as string[] ?? []); if (paths.Length == 1 && organizer.CollectionFiles(CollectionId).Any(f => f.Path.Equals(paths[0], StringComparison.OrdinalIgnoreCase))) organizer.ReorderFile(CollectionId, paths[0], null);
            else organizer.AssignMany(paths, CollectionId); refresh(); } catch (Exception ex) { MessageBox.Show(ex.Message, "无法归类"); } e.Handled = true; };
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            DesktopNative.Attach(Handle, desktop);
            HwndSource.FromHwnd(Handle).AddHook(Hook);
        };
        Loaded += (_, _) => { Position(); ApplyGlass(); UpdateFiles(true); }; Closed += (_, _) => this.finishAdjustment();
    }
    private void ToggleCollapsed() { organizer.SetOptions(CollectionId, organizer.Options(CollectionId) with { Collapsed = !organizer.Options(CollectionId).Collapsed }); UpdateFiles(true); }
    private void ToggleSearch()
    {
        searchOpen = !searchOpen;
        searchHost.Visibility = searchOpen ? Visibility.Visible : Visibility.Collapsed;
        if (searchOpen) { search.Focus(); search.SelectAll(); }
        else { search.Clear(); }
        UpdateFiles(true);
    }
    private void ChangeMode(int mode) { organizer.SetDesktopMode(mode); refresh(); }
    private IntPtr Hook(IntPtr h, int message, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (message == 0x0112 && ((long)wp & 0xFFF0) == 0xF020) { handled = true; return IntPtr.Zero; }
        return IntPtr.Zero;
    }
    public void Position()
    {
        if (Handle == IntPtr.Zero) return;
        var area = Forms.Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains(placement.X + 20, placement.Y + 20))?.WorkingArea ?? Forms.Screen.PrimaryScreen!.WorkingArea;
        placement = LayoutEngine.Constrain(placement, new(area.X, area.Y, area.Width, area.Height));
        var point = new DesktopNative.Point { X = placement.X, Y = placement.Y }; DesktopNative.ScreenToClient(desktop, ref point);
        DesktopNative.SetWindowPos(Handle, IntPtr.Zero, point.X, point.Y, placement.Width, Placement.Height, 0x10 | 0x20 | 0x40);
        ApplyGlass();
    }
    private void SavePosition() { try { organizer.SavePlacement(CollectionId, placement); } catch (Exception ex) { MessageBox.Show(ex.Message, "布局保存失败"); } }
    public void ApplyGlass()
    {
        if (Handle == IntPtr.Zero) return;
        var key = placement + ":" + organizer.State.Desktop.GlassOpacity + ":" + Tokens.Theme.Id + ":" + WallpaperGlass.SourceKey();
        if (key == glassSignature) return;
        glassSignature = key;
        var configured = (byte)Math.Clamp(organizer.State.Desktop.GlassOpacity, (byte)15, (byte)240);
        NativeGlassApplied = DesktopNative.Acrylic(Handle, configured);
        try { backdrop.Background = WallpaperGlass.At(placement); } catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { backdrop.Background = null; }
        GlassApplied = NativeGlassApplied || backdrop.Background != null;
        tint.Background = Tokens.Alpha(Tokens.Glass, configured);
    }
    public void UpdateFiles(bool force = false)
    {
        var collection = organizer.State.Configuration.Collections.First(c => c.Id == CollectionId);
        var options = organizer.Options(CollectionId);
        if (!adjusting && organizer.State.Desktop.Positions.TryGetValue(CollectionId, out var stored)) placement = stored;
        foreach (var element in collapsible) element.Visibility = options.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        searchHost.Visibility = options.Collapsed || !searchOpen ? Visibility.Collapsed : Visibility.Visible;
        collapseButton.Content = options.Collapsed ? "▾" : "▴";
        collapseButton.ToolTip = options.Collapsed ? "展开此分区" : "折叠此分区";
        System.Windows.Automation.AutomationProperties.SetName(collapseButton, options.Collapsed ? "展开此分区" : "折叠此分区");
        marker.Background = Ui.Brush(collection.Color);
        lockButton.Content = options.Locked ? "\uE72E" : "\uE785";
        lockButton.ToolTip = options.Locked ? "解除锁定位置与大小" : "锁定位置与大小";
        System.Windows.Automation.AutomationProperties.SetName(lockButton, options.Locked ? "解除锁定分区" : "锁定分区");
        Position();
        title.Text = collection.Name; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = collection.MappedPath ?? collection.Name;
        var all = organizer.CollectionFiles(CollectionId, search.Text);
        var pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize); filePage = Math.Clamp(filePage, 0, pages - 1);
        var selected = all.Skip(filePage * PageSize).Take(PageSize).ToList();
        if (countBadge.Child is TextBlock count) count.Text = all.Count.ToString();
        var current = string.Join("|", selected.Select(f => f.Path + f.ModifiedUtc.Ticks + f.Size)) + collection.Name + options + filePage + all.Count;
        if (!force && current == signature) return; signature = current;
        pager.Children.Clear();
        if (pages > 1)
        {
            var previous = Ui.IconButton("‹", () => { filePage--; UpdateFiles(true); }, "上一页", true); previous.IsEnabled = filePage > 0;
            var next = Ui.IconButton("›", () => { filePage++; UpdateFiles(true); }, "下一页", true); next.IsEnabled = filePage + 1 < pages;
            pager.Children.Add(previous); pager.Children.Add(Ui.Text($"{filePage + 1} / {pages}  ", Tokens.Label, Brushes.White)); pager.Children.Add(next);
        }
        files.Children.Clear();
        if (options.Collapsed) { Selection.SetFiles([]); return; }
        Selection.SetFiles(selected);
        var tileOwners = new Dictionary<Button, DesktopFile>();
        // 拖放提示结束后必须恢复由 UpdateTile 算出的边框与选中态，不能简单置 0。
        void RestoreTiles() => Selection.Refresh();
        foreach (var file in selected)
        {
            var tile = tileFactory(file, Selection);
            if (tile is Button button)
            {
                button.Style = (Style)Application.Current.FindResource("DarkTileButton"); Ui.SetDarkTile(button); Ui.UpdateTile(button, Selection.Model.Selected.Contains(file.Path)); tileOwners[button] = file;
                var localDragging = false;
                string? DropBefore(Point point)
                {
                    foreach (var child in files.Children.OfType<Button>())
                    {
                        var origin = child.TranslatePoint(new Point(), this);
                        if (new Rect(origin, new Size(child.ActualWidth, child.ActualHeight)).Contains(point))
                            return selected.FirstOrDefault(f => child.ToolTip is string tip && tip.Contains(f.Path))?.Path;
                    }
                    return null;
                }
                void ClearDrag() { localDragging = false; RestoreTiles(); }
                button.Tag = new Func<Point, bool>(point =>
                {
                    if (collection.Recent) return false;
                    var here = button.TranslatePoint(point, this);
                    if (!new Rect(0, 0, ActualWidth, ActualHeight).Contains(here)) { ClearDrag(); button.ReleaseMouseCapture(); return false; }
                    localDragging = true; button.CaptureMouse();
                    var beforePath = DropBefore(here);
                    foreach (var child in files.Children.OfType<Button>()) { child.BorderThickness = child.ToolTip is string tip && beforePath != null && tip.Contains(beforePath) ? new(2, 0, 0, 0) : new(0); child.BorderBrush = Brushes.White; }
                    return true;
                });
                button.PreviewMouseLeftButtonUp += (_, e) =>
                {
                    if (!localDragging) return;
                    var beforePath = DropBefore(e.GetPosition(this)); ClearDrag(); button.ReleaseMouseCapture(); e.Handled = true;
                    try { organizer.ReorderFile(CollectionId, file.Path, beforePath); refresh(); } catch (Exception ex) { MessageBox.Show(ex.Message, "无法排序"); }
                };
                button.LostMouseCapture += (_, _) => ClearDrag();
                button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && localDragging) { ClearDrag(); button.ReleaseMouseCapture(); e.Handled = true; } };
                button.AllowDrop = !collection.Recent;
                button.DragOver += (_, e) =>
                {
                    if (MainWindow.DragData(e).Length == 1 && e.Data.GetData("Lume.VirtualFile") is string path && organizer.CollectionFiles(CollectionId).Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    { e.Effects = DragDropEffects.Move; button.BorderThickness = new(2, 0, 0, 0); button.BorderBrush = Brushes.White; e.Handled = true; }
                };
                button.DragLeave += (_, _) => RestoreTiles();
                button.Drop += (_, e) =>
                {
                    RestoreTiles();
                    if (MainWindow.DragData(e).Length == 1 && e.Data.GetData("Lume.VirtualFile") is string path && organizer.CollectionFiles(CollectionId).Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    { try { organizer.ReorderFile(CollectionId, path, file.Path); refresh(); } catch (Exception ex) { MessageBox.Show(ex.Message, "无法排序"); } e.Handled = true; }
                };
                button.Foreground = Brushes.White;
                if (button.Content is StackPanel p) foreach (var child in p.Children.OfType<TextBlock>()) { child.Foreground = Brushes.White; child.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 1, ShadowDepth = 0, Opacity = .8 }; }
                if (button.Content is StackPanel panel) { panel.Width = Math.Max(58, options.IconSize + 24); foreach (var image in panel.Children.OfType<Image>()) image.Width = image.Height = options.IconSize; button.Width = panel.Width + 8; button.Height = options.IconSize + 48; }
            }
            files.Children.Add(tile);
        }
        if (selected.Count == 0) files.Children.Add(Ui.DropZone(Ui.EmptyDropText, Tokens.WhiteAlpha(0xD9), Tokens.WhiteAlpha(0x59), Tokens.WhiteAlpha(0x08)));
    }
}
