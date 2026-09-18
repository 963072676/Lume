using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Lume.Core;
using Forms = System.Windows.Forms;

namespace Lume.Desktop;

internal sealed class SystemDesktopWindow : Window
{
    internal const string PositionId = "__windows-system";
    internal sealed record Entry(string Id, string Name);
    internal static readonly Entry[] Entries = [new("20D04FE0-3AEA-1069-A2D8-08002B30309D", "此电脑"), new("645FF040-5081-101B-9F08-00AA002F954E", "回收站"),
        new("F02C1A0D-BE21-4350-88B0-7367FC96EF3C", "网络"), new("59031A47-3F72-44A7-89C5-5595FE6B30EE", "用户文件"), new("5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0", "控制面板")];
    private readonly Organizer organizer;
    private readonly IntPtr desktop;
    private readonly Action settings;
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
    public CardPlacement Placement => organizer.Options(PositionId).Collapsed ? placement with { Height = 58 } : placement;
    internal int EntryCount { get; }
    internal IntPtr Handle => handle;
    public bool GlassApplied { get; private set; }
    public bool NativeGlassApplied { get; private set; }

    internal static List<Entry> EnabledEntries()
    {
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
        return Entries.Where(e => settings?.GetValue("{" + e.Id + "}") is int hidden ? hidden == 0 : e.Name == "回收站").ToList();
    }
    public SystemDesktopWindow(Organizer organizer, IntPtr desktop, Action settings, IReadOnlyList<Entry>? entries = null,
        Func<string, CardPlacement, string, CardPlacement>? adjust = null, Action? finishAdjustment = null)
    {
        this.organizer = organizer; this.desktop = desktop; this.settings = settings;
        this.adjust = adjust ?? ((_, p, _) => p); this.finishAdjustment = finishAdjustment ?? (() => { });
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
            var image = new Image { Source = ReadIcon(entry), Width = 34, Height = 34, Margin = new(0, 0, 0, 5) };
            var label = Ui.Text(entry.Name, 11, Brushes.White); label.TextAlignment = TextAlignment.Center;
            var column = new StackPanel { Width = 66 }; column.Children.Add(image); column.Children.Add(label);
            var button = new Button { Content = column, Width = 74, Height = 70, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(3), Margin = new(0), ToolTip = entry.Name };
            System.Windows.Automation.AutomationProperties.SetName(button, entry.Name);
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
        Loaded += (_, _) => { UpdateView(); ApplyGlass(); };
        Closed += (_, _) => this.finishAdjustment();
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
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    internal static ImageSource ReadIcon(Entry entry)
    {
        var pidl = IntPtr.Zero; var info = new ShellInfo();
        try
        {
            if (SHParseDisplayName("::{" + entry.Id + "}", IntPtr.Zero, out pidl, 0, out _) >= 0)
            {
                SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x108);
                if (info.Icon != IntPtr.Zero) return IconArtwork.Normalize(Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()), false);
            }
            return new BitmapImage(new Uri("pack://application:,,,/Assets/lume.png"));
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }
}
