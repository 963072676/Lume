using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Lume.Core;
using Forms = System.Windows.Forms;

namespace Lume.Desktop;

internal sealed class DesktopSurface : IDisposable
{
    private readonly Organizer organizer;
    private readonly Func<DesktopFile, TileSelection, UIElement> tile;
    private readonly Action refresh;
    private readonly Action settings;
    private readonly Action<string> archive;
    private readonly string leasePath;
    private readonly DispatcherTimer health = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<DesktopCardWindow> cards = [];
    private SystemDesktopWindow? systemEntries;
    private string systemSignature = "";
    private readonly AlignmentOverlay guides = new();
    private IntPtr view;
    private IntPtr icons;
    private Process? guard;
    private bool paused;
    private bool disposed;
    private bool rebuilding;
    private string displaySignature = "";
    public event Action<string>? Error;
    public IReadOnlyList<DesktopCardWindow> Cards => cards;
    internal SystemDesktopWindow? SystemEntries => systemEntries;
    internal int VisibleGuideCount => guides.VisibleCount;
    public bool Paused => paused;
    internal void RequestRebuild() { displaySignature = ""; }
    public bool Attached => !paused && cards.Count > 0 && cards.All(c => DesktopNative.GetParent(c.Handle) == view && DesktopNative.IsWindowVisible(c.Handle));
    public DesktopSurface(Organizer organizer, string dataDirectory, Func<DesktopFile, TileSelection, UIElement> tile, Action refresh, Action settings, Action<string> archive)
    {
        this.organizer = organizer; this.tile = tile; this.refresh = refresh; this.settings = settings; this.archive = archive;
        leasePath = Path.Combine(dataDirectory, "desktop-lease-" + Guid.NewGuid().ToString("N") + ".json");
        health.Tick += async (_, _) =>
        {
            if (paused || disposed || rebuilding) return;
            if (guard?.HasExited == true) { paused = true; CloseCards(); DesktopRecovery.Restore(leasePath); Error?.Invoke("恢复保护进程已退出，已暂停分区并恢复原桌面。"); return; }
            var displays = string.Join("|", Forms.Screen.AllScreens.Select(s => s.Bounds.ToString()));
            if (!DesktopNative.IsWindow(view) || cards.Any(c => !DesktopNative.IsWindow(c.Handle)) || displays != displaySignature) await RebuildAsync();
            else if (File.Exists(leasePath)) DesktopRecovery.HideManagedLayers(leasePath);
            RefreshSystemEntries();
        };
    }
    public async Task StartAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        DesktopRecovery.Restore(Path.Combine(Path.GetDirectoryName(leasePath)!, "desktop-lease.json"));
        foreach (var previous in Directory.EnumerateFiles(Path.GetDirectoryName(leasePath)!, "desktop-lease-*.json")) DesktopRecovery.Restore(previous);
        var process = Process.GetCurrentProcess();
        guard = DesktopRecovery.LaunchGuard(process, leasePath);
        for (var i = 0; i < 60 && !File.Exists(leasePath + ".ready") && !guard.HasExited; i++) await Task.Delay(50);
        if (!File.Exists(leasePath + ".ready")) throw new IOException("桌面恢复保护进程未就绪，未接管原图标。");
        await RebuildAsync(); health.Start();
    }
    private async Task RebuildAsync()
    {
        if (rebuilding) return; rebuilding = true;
        try
        {
            CloseCards(); DesktopRecovery.Restore(leasePath);
            (view, icons) = DesktopNative.FindDesktop();
            if (view == IntPtr.Zero || icons == IntPtr.Zero) throw new IOException("暂时找不到 Windows 桌面图标层，等待资源管理器恢复。");
            DesktopRecovery.SaveLease(leasePath, icons);
            displaySignature = string.Join("|", Forms.Screen.AllScreens.Select(s => s.Bounds.ToString()));
            CreateCards();
            await Task.Delay(120);
            if (cards.Any(c => DesktopNative.GetParent(c.Handle) != view)) throw new IOException("桌面分区挂载校验失败。");
            DesktopRecovery.HideManagedLayers(leasePath);
        }
        catch (Exception ex) { CloseCards(); DesktopRecovery.Restore(leasePath); view = IntPtr.Zero; Error?.Invoke(ex.Message); }
        finally { rebuilding = false; }
    }
    private void CreateCards()
    {
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        var width = 320; var height = 220; var columns = Math.Max(1, (area.Width - 48) / (width + 18));
        var selected = organizer.State.Configuration.Collections.Where(c => organizer.State.Desktop.Mode == 0 || organizer.State.Desktop.Mode == 1 && c.InWork || organizer.State.Desktop.Mode == 2 && c.InPresentation).ToList();
        var dpiContext = DesktopNative.SetThreadDpiAwarenessContext(DesktopNative.GetWindowDpiAwarenessContext(view));
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                var c = selected[i]; var position = organizer.State.Desktop.Positions.GetValueOrDefault(c.Id) ?? new(area.Left + 24 + (i % columns) * (width + 18), area.Top + 28 + (i / columns) * (height + 18), width, height);
                var card = new DesktopCardWindow(organizer, c, position, view, tile, refresh, settings, archive, Adjust, () => guides.Dispose()); cards.Add(card); card.Show();
            }
        }
        finally { DesktopNative.SetThreadDpiAwarenessContext(dpiContext); }
        RefreshSystemEntries();
    }
    private void RefreshSystemEntries()
    {
        if (paused || disposed || view == IntPtr.Zero) return;
        var entries = SystemDesktopWindow.EnabledEntries();
        var signature = organizer.State.Desktop.ShowSystemEntries + string.Join("|", entries.Select(e => e.Id));
        if (systemSignature == signature) { systemEntries?.RefreshPlacement(); return; }
        systemEntries?.Close(); systemEntries = null; systemSignature = signature;
        if (!organizer.State.Desktop.ShowSystemEntries || entries.Count == 0) return;
       var context = DesktopNative.SetThreadDpiAwarenessContext(DesktopNative.GetWindowDpiAwarenessContext(view));
        try { systemEntries = new SystemDesktopWindow(organizer, view, settings, entries, Adjust, () => guides.Dispose()); systemEntries.Show(); }
       finally { DesktopNative.SetThreadDpiAwarenessContext(context); }
    }
    public void Refresh()
    {
        if (paused || disposed || rebuilding || view == IntPtr.Zero) return;
        var ids = organizer.State.Configuration.Collections.Where(c => organizer.State.Desktop.Mode == 0 || organizer.State.Desktop.Mode == 1 && c.InWork || organizer.State.Desktop.Mode == 2 && c.InPresentation).Select(c => c.Id);
        if (!ids.SequenceEqual(cards.Select(c => c.CollectionId))) { CloseCards(); CreateCards(); }
       foreach (var card in cards) { card.UpdateFiles(); card.ApplyGlass(); }
       RefreshSystemEntries();
        systemEntries?.ApplyGlass();
   }
    private CardPlacement Adjust(string id, CardPlacement requested, string edges)
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(requested.X + requested.Width / 2, requested.Y + Math.Min(40, requested.Height / 2))).WorkingArea;
        var area = new CardPlacement(screen.X, screen.Y, screen.Width, screen.Height);
        requested = LayoutEngine.Constrain(requested, area);
       if (!organizer.State.Desktop.SnapEnabled || System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) { guides.Dispose(); return requested; }
        var peers = cards.Where(c => c.CollectionId != id).Select(c => c.Placement);
        if (systemEntries != null && id != SystemDesktopWindow.PositionId) peers = peers.Append(systemEntries.Placement);
        var result = LayoutEngine.Snap(requested, peers, area, 8, edges);
       var constrained = LayoutEngine.Constrain(result.Placement, area);
        guides.Show(view, constrained == result.Placement ? result.Guides : []); return constrained;
    }
    public async Task ToggleAsync()
    {
        paused = !paused;
        if (paused) { CloseCards(); DesktopRecovery.Restore(leasePath); }
        else await RebuildAsync();
    }
    private void CloseCards() { guides.Dispose(); systemEntries?.Close(); systemEntries = null; systemSignature = ""; foreach (var card in cards.ToList()) { try { card.Close(); } catch (InvalidOperationException) { } } cards.Clear(); }
    public void Dispose() { if (disposed) return; disposed = true; health.Stop(); CloseCards(); DesktopRecovery.Restore(leasePath); guard?.Dispose(); }
}
