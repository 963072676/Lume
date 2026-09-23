using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Lume.Core;
using Forms = System.Windows.Forms;

namespace Lume.Desktop;

internal sealed class SystemDesktopWindow : Window
{
    internal const string PositionId = "__windows-system";
    internal const string RecycleBinId = "645FF040-5081-101B-9F08-00AA002F954E";
    internal sealed record Entry(string Id, string Name);
    internal static readonly Entry[] Entries = [new("20D04FE0-3AEA-1069-A2D8-08002B30309D", "此电脑"), new("645FF040-5081-101B-9F08-00AA002F954E", "回收站"),
        new("F02C1A0D-BE21-4350-88B0-7367FC96EF3C", "网络"), new("59031A47-3F72-44A7-89C5-5595FE6B30EE", "用户文件"), new("5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0", "控制面板")];
    private readonly Organizer organizer;
    private readonly IntPtr desktop;
    private readonly Action settings;
    private readonly Func<Entry, Task<ShellIcons.SystemIconResult>> readIconAsync;
    private readonly Func<string, CardPlacement, string, CardPlacement> adjust;
    private readonly Action finishAdjustment;
    private CardPlacement placement;
    private IntPtr handle;
    private bool dragging;
    private string glassSignature = "";
    private readonly Border backdrop = new() { CornerRadius = new(12), Opacity = .32 };
    private readonly Border tint = new() { CornerRadius = new(12) };
    private readonly Border marker = new() { Width = 8, Height = 8, CornerRadius = new(4), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0), Background = Ui.Accent };
    private readonly TextBlock title = Ui.Text("Windows 系统入口", 13, Brushes.White, true);
    private readonly Border countBadge;
    private readonly Button collapseButton;
    private readonly Button lockButton;
    private readonly ScrollViewer scroll;
    private readonly Dictionary<string, (Image Image, Button Button)> entryImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> iconVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer iconDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool pendingAllIcons;
    private DateTime lastRecycleRefreshUtc;
    private DateTime lastAllRefreshUtc;
    public CardPlacement Placement => organizer.Options(PositionId).Collapsed ? placement with { Height = 58 } : placement;
    internal int EntryCount { get; }
    internal IntPtr Handle => handle;
    internal int IconRefreshCount { get; private set; }
    internal Image? IconImage(string id) => entryImages.GetValueOrDefault(id).Image;
    internal static ImageSource FallbackIcon { get; } = CreateFallbackIcon();
    private static ImageSource CreateFallbackIcon()
    {
        var icon = new BitmapImage(new Uri("pack://application:,,,/Assets/lume.png"));
        icon.Freeze(); return icon;
    }
    public bool GlassApplied { get; private set; }
    public bool NativeGlassApplied { get; private set; }

    internal static List<Entry> EnabledEntries()
    {
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
        return Entries.Where(e => settings?.GetValue("{" + e.Id + "}") is int hidden ? hidden == 0 : e.Name == "回收站").ToList();
    }
    public SystemDesktopWindow(Organizer organizer, IntPtr desktop, Action settings, IReadOnlyList<Entry>? entries = null,
        Func<string, CardPlacement, string, CardPlacement>? adjust = null, Action? finishAdjustment = null,
        Func<Entry, Task<ShellIcons.SystemIconResult>>? readIconAsync = null)
    {
        this.organizer = organizer; this.desktop = desktop; this.settings = settings;
        this.readIconAsync = readIconAsync ?? ShellIcons.GetSystemAsync;
        this.adjust = adjust ?? ((_, p, _) => p); this.finishAdjustment = finishAdjustment ?? (() => { });
        iconDebounce.Tick += (_, _) => { iconDebounce.Stop(); RefreshIcons(pendingAllIcons); pendingAllIcons = false; };
        entries ??= EnabledEntries(); EntryCount = entries.Count;
        countBadge = Ui.Badge(EntryCount.ToString(), Brushes.White, Tokens.WhiteAlpha(0x2E));
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        placement = organizer.State.Desktop.Positions.GetValueOrDefault(PositionId) ?? new(area.Left + 24, area.Bottom - 175, Math.Max(260, EntryCount * 76 + 32), 160);
        placement = LayoutEngine.Constrain(placement, new(area.X, area.Y, area.Width, area.Height));
        Style = (Style)Application.Current.FindResource(typeof(Window)); Title = "Lume.WindowsSystem";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Left = -16000; Width = placement.Width; Height = Placement.Height;
        title.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 1, ShadowDepth = 0, Opacity = .8 };
        var body = new Grid { Margin = new(16, 12, 16, 12) };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new());
        var header = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new(0, 0, 0, 8), MinHeight = 32 };
        var menuButton = Ui.IconButton("⋯", () => { }, "打开分区更多菜单", true); menuButton.Margin = new(0);
        collapseButton = Ui.IconButton("▲", ToggleCollapsed, "折叠此分区", true); collapseButton.Margin = new(0, 0, 4, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(collapseButton, "ToggleCollectionCollapsed");
        lockButton = Ui.IconButton("", () =>
        {
            organizer.SetOptions(PositionId, organizer.Options(PositionId) with { Locked = !organizer.Options(PositionId).Locked });
            UpdateView();
        }, "锁定分区", true);
        lockButton.FontFamily = new FontFamily("Segoe MDL2 Assets"); lockButton.Margin = new(0, 0, 4, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(lockButton, "ToggleCollectionLocked");
        var actions = Ui.Row(lockButton, collapseButton, menuButton); DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        countBadge.Margin = new(8, 0, 8, 0); DockPanel.SetDock(countBadge, Dock.Right); header.Children.Add(countBadge);
        DockPanel.SetDock(marker, Dock.Left); header.Children.Add(marker); header.Children.Add(title);
        var menu = new ContextMenu();
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("折叠 / 展开", ToggleCollapsed);
        Add("移动到下一块显示器", () =>
        {
            if (organizer.Options(PositionId).Locked) return;
            var monitors = Forms.Screen.AllScreens; var index = Array.FindIndex(monitors, s => s.Bounds.Contains(placement.X, placement.Y));
            var targetArea = monitors[(index + 1) % monitors.Length].WorkingArea; placement = placement with { X = targetArea.Left + 30, Y = targetArea.Top + 30 }; Position(); SavePosition();
        });
        menu.Items.Add(new Separator());
        Add("打开设置中心", settings);
        menuButton.Click += (_, _) => { menu.PlacementTarget = menuButton; menu.IsOpen = true; };
        body.Children.Add(header);
        DesktopNative.Point start = default; CardPlacement? origin = null;
        header.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (menuButton.IsMouseOver || collapseButton.IsMouseOver || lockButton.IsMouseOver) return;
            if (e.ClickCount == 2) { ToggleCollapsed(); e.Handled = true; return; }
            if (organizer.Options(PositionId).Locked) return;
            dragging = true; DesktopNative.GetCursorPos(out start); origin = placement; header.CaptureMouse(); e.Handled = true;
        };
        header.MouseMove += (_, _) =>
        {
            if (!dragging || origin == null || !header.IsMouseCaptured) return;
            DesktopNative.GetCursorPos(out var p);
            placement = this.adjust(PositionId, origin with { X = origin.X + p.X - start.X, Y = origin.Y + p.Y - start.Y }, "move");
            Position();
        };
        header.MouseLeftButtonUp += (_, _) =>
        {
            if (!header.IsMouseCaptured) return;
            origin = null; header.ReleaseMouseCapture(); dragging = false; this.finishAdjustment(); SavePosition();
        };
        header.LostMouseCapture += (_, _) =>
        {
            if (origin != null) { placement = origin; origin = null; Position(); }
            dragging = false; this.finishAdjustment();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && header.IsMouseCaptured) { header.ReleaseMouseCapture(); e.Handled = true; }
        };
        var icons = new WrapPanel();
        foreach (var entry in entries)
        {
            var image = new Image { Source = FallbackIcon, Width = 34, Height = 34, Margin = new(0, 0, 0, 5) };
            var label = Ui.Text(entry.Name, 11, Brushes.White); label.TextAlignment = TextAlignment.Center;
            var column = new StackPanel { Width = 66 }; column.Children.Add(image); column.Children.Add(label);
            var button = new Button { Content = column, Width = 74, Height = 70, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(3), Margin = new(0), ToolTip = entry.Name };
            System.Windows.Automation.AutomationProperties.SetName(button, entry.Name);
            entryImages.Add(entry.Id, (image, button));
            button.MouseDoubleClick += (_, _) => Open(entry);
            button.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Open(entry); e.Handled = true; } };
            var context = new ContextMenu(); var open = new MenuItem { Header = "打开" }; open.Click += (_, _) => Open(entry); context.Items.Add(open); button.ContextMenu = context;
            button.ContextMenuOpening += (_, e) =>
            {
                DesktopNative.GetCursorPos(out var pt);
                if (ShellContextMenu.Show(handle, new[] { "::{" + entry.Id + "}" }, pt))
                    e.Handled = true;
            };
            icons.Children.Add(button);
        }
        scroll = new ScrollViewer { Content = icons, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        var glassLayers = new Grid(); glassLayers.Children.Add(backdrop); glassLayers.Children.Add(tint); glassLayers.Children.Add(body);
        Content = new Border { CornerRadius = new(12), BorderBrush = Tokens.WhiteAlpha(0x38), BorderThickness = new(1), Child = glassLayers };
        foreach (var edge in new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW" })
        {
            var grip = new Thumb
            {
                Width = edge is "N" or "S" ? double.NaN : edge.Length == 2 ? 18 : 9,
                Height = edge is "E" or "W" ? double.NaN : edge.Length == 2 ? 18 : 9,
                HorizontalAlignment = edge.Contains('W') ? HorizontalAlignment.Left : edge.Contains('E') ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                VerticalAlignment = edge.Contains('N') ? VerticalAlignment.Top : edge.Contains('S') ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
                Cursor = edge is "N" or "S" ? Cursors.SizeNS : edge is "E" or "W" ? Cursors.SizeWE : edge is "NE" or "SW" ? Cursors.SizeNESW : Cursors.SizeNWSE
            };
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Ui.Brush("#01000000")); grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            CardPlacement? begin = null; DesktopNative.Point pointer = default;
            grip.DragStarted += (_, _) =>
            {
                if (organizer.Options(PositionId).Locked || organizer.Options(PositionId).Collapsed) { grip.CancelDrag(); return; }
                begin = placement; DesktopNative.GetCursorPos(out pointer); dragging = true;
            };
            grip.DragDelta += (_, _) =>
            {
                if (begin == null) return;
                DesktopNative.GetCursorPos(out var current);
                placement = this.adjust(PositionId, LayoutEngine.Resize(begin, edge, current.X - pointer.X, current.Y - pointer.Y), edge);
                Position();
            };
            grip.DragCompleted += (_, e) =>
            {
                if (begin == null) return;
                if (e.Canceled) placement = begin;
                begin = null; dragging = false; Position(); this.finishAdjustment(); SavePosition();
            };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && grip.IsDragging) { grip.CancelDrag(); e.Handled = true; } };
            glassLayers.Children.Add(grip);
        }
        SourceInitialized += (_, _) => { handle = new WindowInteropHelper(this).Handle; DesktopNative.Attach(handle, desktop); };
        Loaded += (_, _) => { ShellIconChanges.Changed += OnShellChange; UpdateView(); ApplyGlass(); RefreshIcons(true); };
        Closed += (_, _) => { ShellIconChanges.Changed -= OnShellChange; iconDebounce.Stop(); iconVersions.Clear(); this.finishAdjustment(); };
    }
    private static void Open(Entry entry)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{" + entry.Id + "}") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开系统入口"); }
    }
    private void ToggleCollapsed()
    {
        organizer.SetOptions(PositionId, organizer.Options(PositionId) with { Collapsed = !organizer.Options(PositionId).Collapsed });
        UpdateView();
    }
    internal void UpdateView()
    {
        var options = organizer.Options(PositionId);
        scroll.Visibility = options.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        collapseButton.Content = options.Collapsed ? "▼" : "▲";
        lockButton.Content = options.Locked ? "" : "";
        lockButton.ToolTip = options.Locked ? "解锁分区" : "锁定分区";
        Position();
    }
    private void SavePosition()
    {
        try { organizer.SavePlacement(PositionId, placement); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "布局保存失败"); }
    }
    internal void RefreshPlacement()
    {
        if (dragging) return;
        if (organizer.State.Desktop.Positions.TryGetValue(PositionId, out var saved)) placement = saved;
        UpdateView();
    }
    private void OnShellChange(ShellIconChange change)
    {
        // Namespace PIDLs such as the Recycle Bin have no filesystem path.
        // Any Shell event can alter its state; coalesce bursts before querying.
        pendingAllIcons |= (change.EventId & (ShellIconChanges.AssociationChanged | ShellIconChanges.ImageChanged)) != 0
            || (change.Path == null && change.NewPath == null);
        if (!iconDebounce.IsEnabled) iconDebounce.Start();
    }
    internal void RefreshDynamicIcons()
    {
        // Recover missed Shell notifications without polling every health tick.
        if (DateTime.UtcNow - lastAllRefreshUtc >= TimeSpan.FromMinutes(5)) RefreshIcons(true);
        else if (DateTime.UtcNow - lastRecycleRefreshUtc >= TimeSpan.FromSeconds(30)) RefreshIcons(false);
    }
    private void RefreshIcons(bool all)
    {
        if (!IsLoaded) return;
        lastRecycleRefreshUtc = DateTime.UtcNow;
        if (all) lastAllRefreshUtc = lastRecycleRefreshUtc;
        foreach (var entry in all ? Entries.Where(e => entryImages.ContainsKey(e.Id)) : Entries.Where(e => e.Id == RecycleBinId && entryImages.ContainsKey(e.Id)))
        {
            var version = iconVersions.GetValueOrDefault(entry.Id) + 1;
            iconVersions[entry.Id] = version;
            _ = RefreshIconAsync(entry, version);
        }
    }
    private async Task RefreshIconAsync(Entry entry, int generation)
    {
        ShellIcons.SystemIconResult result;
        try { result = await readIconAsync(entry); }
        catch (Exception) { return; }
        if (!IsLoaded || iconVersions.GetValueOrDefault(entry.Id) != generation || !entryImages.TryGetValue(entry.Id, out var target)) return;
        if (!ReferenceEquals(result.Icon, FallbackIcon) || ReferenceEquals(target.Image.Source, FallbackIcon)) target.Image.Source = result.Icon;
        IconRefreshCount++;
        if (entry.Id == RecycleBinId && result.Count is { } count)
        {
            var description = count == 0 ? "回收站 · 空" : $"回收站 · {count} 项";
            target.Button.ToolTip = description;
            System.Windows.Automation.AutomationProperties.SetName(target.Button, description);
        }
    }
    private void Position()
    {
        if (handle == IntPtr.Zero) return;
        var area = Forms.Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains(placement.X + 20, placement.Y + 20))?.WorkingArea ?? Forms.Screen.PrimaryScreen!.WorkingArea;
        placement = LayoutEngine.Constrain(placement, new(area.X, area.Y, area.Width, area.Height));
        var p = new DesktopNative.Point { X = placement.X, Y = placement.Y }; DesktopNative.ScreenToClient(desktop, ref p);
        DesktopNative.SetWindowPos(handle, IntPtr.Zero, p.X, p.Y, placement.Width, Placement.Height, 0x10 | 0x20 | 0x40);
        ApplyGlass();
    }
    public void ApplyGlass()
    {
        if (handle == IntPtr.Zero) return;
        var key = placement + ":" + organizer.State.Desktop.GlassOpacity + ":" + Tokens.Theme.Id + ":" + WallpaperGlass.SourceKey();
        if (key == glassSignature) return;
        glassSignature = key;
        var configured = (byte)Math.Clamp(organizer.State.Desktop.GlassOpacity, (byte)15, (byte)240);
        NativeGlassApplied = DesktopNative.Acrylic(handle, configured);
        try { backdrop.Background = WallpaperGlass.At(placement); } catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { backdrop.Background = null; }
        GlassApplied = NativeGlassApplied || backdrop.Background != null;
        tint.Background = Tokens.Alpha(Tokens.Glass, configured);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellInfo
    { public IntPtr Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Display; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint attributes, out uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref ShellInfo info, uint size, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RecycleInfo
    { public uint Size; public long Bytes; public long Count; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StockIconInfo
    { public uint Size; public IntPtr Icon; public int SystemIndex; public int IconIndex; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref RecycleInfo info);
    [DllImport("shell32.dll")] private static extern int SHGetStockIconInfo(int id, uint flags, ref StockIconInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    internal static long? RecycleBinItemCount()
    {
        var info = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? info.Count : null;
    }
    internal static ShellIcons.SystemIconResult ReadSystemIcon(Entry entry)
    {
        var count = entry.Id == RecycleBinId ? RecycleBinItemCount() : null;
        return new(ReadIcon(entry, count.HasValue ? count.Value > 0 : null), count);
    }
    private static BitmapSource NormalizeIcon(IntPtr icon) => IconArtwork.Normalize(Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()), false);
    private static ImageSource? ReadRecycleBinIcon(bool full)
    {
        // Desktop icon settings can supply custom empty/full resources.
        var keyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{" + RecycleBinId + @"}\DefaultIcon";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var location = key?.GetValue(full ? "Full" : "Empty") as string;
        if (!string.IsNullOrWhiteSpace(location))
        {
            var separator = location.LastIndexOf(',');
            var parsed = 0;
            var hasIndex = separator >= 0 && int.TryParse(location[(separator + 1)..].Trim(), out parsed);
            var path = Environment.ExpandEnvironmentVariables((hasIndex ? location[..separator] : location).Trim().Trim('"'));
            var index = hasIndex ? parsed : 0;
            if (System.IO.File.Exists(path))
            {
                IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
                try
                {
                    if (ExtractIconExW(path, index, out large, out small, 1) > 0 && large != IntPtr.Zero) return NormalizeIcon(large);
                }
                finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
            }
        }
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        try
        {
            if (SHGetStockIconInfo(full ? 32 : 31, 0x00000100, ref info) == 0 && info.Icon != IntPtr.Zero) return NormalizeIcon(info.Icon);
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
        return null;
    }
    internal static ImageSource ReadIcon(Entry entry, bool? recycleBinFull = null)
    {
        var pidl = IntPtr.Zero; var info = new ShellInfo();
        try
        {
            if (entry.Id == RecycleBinId && recycleBinFull is { } full && ReadRecycleBinIcon(full) is { } recycleIcon)
                return recycleIcon;
            if (SHParseDisplayName("::{" + entry.Id + "}", IntPtr.Zero, out pidl, 0, out _) >= 0)
            {
                SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x108);
                if (info.Icon != IntPtr.Zero) return NormalizeIcon(info.Icon);
            }
            return FallbackIcon;
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }
}
