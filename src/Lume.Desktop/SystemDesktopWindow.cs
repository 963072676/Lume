using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Lume.Core;

namespace Lume.Desktop;

internal sealed class SystemDesktopWindow : Window
{
    private const string PositionId = "__windows-system";
    internal sealed record Entry(string Id, string Name);
    internal static readonly Entry[] Entries = [new("20D04FE0-3AEA-1069-A2D8-08002B30309D", "此电脑"), new("645FF040-5081-101B-9F08-00AA002F954E", "回收站"),
        new("F02C1A0D-BE21-4350-88B0-7367FC96EF3C", "网络"), new("59031A47-3F72-44A7-89C5-5595FE6B30EE", "用户文件"), new("5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0", "控制面板")];
    private readonly Organizer organizer;
    private readonly IntPtr desktop;
    private CardPlacement placement;
    private IntPtr handle;
    private bool dragging;
    internal int EntryCount { get; }
    internal IntPtr Handle => handle;

    internal static List<Entry> EnabledEntries()
    {
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
        return Entries.Where(e => settings?.GetValue("{" + e.Id + "}") is int hidden ? hidden == 0 : e.Name == "回收站").ToList();
    }
    public SystemDesktopWindow(Organizer organizer, IntPtr desktop, Action settings, IReadOnlyList<Entry>? entries = null)
    {
        this.organizer = organizer; this.desktop = desktop; entries ??= EnabledEntries(); EntryCount = entries.Count;
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        placement = organizer.State.Desktop.Positions.GetValueOrDefault(PositionId) ?? new(area.Left + 24, area.Bottom - 145, Math.Max(260, EntryCount * 76 + 28), 160);
        placement = LayoutEngine.Constrain(placement, new(area.X, area.Y, area.Width, area.Height));
        Style = (Style)Application.Current.FindResource(typeof(Window)); Title = "Lume.WindowsSystem";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Left = -16000; Width = placement.Width; Height = 160;
        var body = new StackPanel { Margin = new(14, 8, 14, 10) };
        var header = new DockPanel { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.SizeAll, Margin = new(0, 0, 0, 5) };
        var menu = Ui.Button("···", settings); menu.Padding = new(6, 0, 6, 0); menu.Margin = new(0); menu.Background = Brushes.Transparent; menu.Foreground = Brushes.White; menu.BorderThickness = new(0);
        DockPanel.SetDock(menu, Dock.Right); header.Children.Add(menu); header.Children.Add(Ui.Text("Windows 系统入口", 12, Brushes.White, true)); body.Children.Add(header);
        DesktopNative.Point start = default; CardPlacement? origin = null;
        header.PreviewMouseLeftButtonDown += (_, e) => { if (menu.IsMouseOver) return; dragging = true; DesktopNative.GetCursorPos(out start); origin = placement; header.CaptureMouse(); e.Handled = true; };
        header.MouseMove += (_, _) => { if (!dragging || origin == null) return; DesktopNative.GetCursorPos(out var p); placement = origin with { X = origin.X + p.X - start.X, Y = origin.Y + p.Y - start.Y }; Position(); };
        header.MouseLeftButtonUp += (_, _) => { if (!dragging) return; dragging = false; origin = null; header.ReleaseMouseCapture(); organizer.SavePlacement(PositionId, placement); };
        header.LostMouseCapture += (_, _) => { if (origin != null) { placement = origin; Position(); } dragging = false; origin = null; };
        var icons = new WrapPanel();
        foreach (var entry in entries)
        {
            var image = new Image { Source = ReadIcon(entry), Width = 34, Height = 34, Margin = new(0, 0, 0, 5) };
            var label = Ui.Text(entry.Name, 11, Brushes.White); label.TextAlignment = TextAlignment.Center;
            var column = new StackPanel { Width = 66 }; column.Children.Add(image); column.Children.Add(label);
            var button = new Button { Content = column, Width = 74, Height = 70, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(3), Margin = new(0), ToolTip = entry.Name };
            System.Windows.Automation.AutomationProperties.SetName(button, entry.Name);
            button.MouseDoubleClick += (_, _) => Open(entry);
            button.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Open(entry); e.Handled = true; } };
            var context = new ContextMenu(); var open = new MenuItem { Header = "打开" }; open.Click += (_, _) => Open(entry); context.Items.Add(open); button.ContextMenu = context;
            icons.Children.Add(button);
        }
        body.Children.Add(icons);
        Content = new Border { Background = Ui.Brush("#D923382D"), BorderBrush = Ui.Brush("#669CB5A4"), BorderThickness = new(1), CornerRadius = new(16), Child = body };
        SourceInitialized += (_, _) => { handle = new WindowInteropHelper(this).Handle; DesktopNative.Attach(handle, desktop); };
        Loaded += (_, _) => Position();
    }
    private static void Open(Entry entry)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{" + entry.Id + "}") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开系统入口"); }
    }
    internal void RefreshPlacement()
    {
        if (dragging) return;
        if (organizer.State.Desktop.Positions.TryGetValue(PositionId, out var saved)) placement = saved;
        Position();
    }
    private void Position()
    {
        if (handle == IntPtr.Zero) return;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(placement.X, placement.Y)).WorkingArea;
        placement = LayoutEngine.Constrain(placement, new(area.X, area.Y, area.Width, area.Height));
        var p = new DesktopNative.Point { X = placement.X, Y = placement.Y }; DesktopNative.ScreenToClient(desktop, ref p);
        DesktopNative.SetWindowPos(handle, IntPtr.Zero, p.X, p.Y, placement.Width, 160, 0x10 | 0x40);
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
