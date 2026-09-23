using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal static class SystemIconVerification
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint attributes, out uint flags);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr first, IntPtr second);

    internal static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        var recycle = SystemDesktopWindow.Entries.Single(e => e.Id == SystemDesktopWindow.RecycleBinId);
        var computer = SystemDesktopWindow.Entries.Single(e => e.Name == "此电脑");
        var empty = SystemDesktopWindow.ReadIcon(recycle, false) as BitmapSource;
        var full = SystemDesktopWindow.ReadIcon(recycle, true) as BitmapSource;
        var computerIcon = SystemDesktopWindow.ReadIcon(computer);
        check(empty is { PixelWidth: 128 } && full is { PixelWidth: 128 } && !ShellIcons.SamePixels(empty, full),
            "空与满回收站读取不同的系统图标");
        check(!ShellIconChanges.HasPidlPayload(ShellIconChanges.ImageChanged)
            && !ShellIconChanges.HasPidlPayload(ShellIconChanges.AssociationChanged)
            && ShellIconChanges.HasPidlPayload(0x00002000), "图像列表与关联通知不当作文件路径解析");

        using var watcher = new ShellIconChanges();
        check(watcher.Registered, "隐藏管理窗口仍注册 Shell 消息接收器");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(ShellIconChange change) { if ((change.EventId & 0x00002000) != 0) received.TrySetResult(); }
        ShellIconChanges.Changed += Changed;
        var host = new Window { Left = -16000, Top = 0, Width = 300, Height = 200, ShowActivated = false };
        SystemDesktopWindow? card = null;
        var pidl = IntPtr.Zero;
        try
        {
            host.Show();
            var organizer = new Organizer(new StateStore(Path.Combine(fixture, "system-state.json")), AppState.Create([]));
            var count = 0L; var computerRefreshes = 0;
            card = new SystemDesktopWindow(organizer, new WindowInteropHelper(host).Handle, () => { }, [recycle, computer],
                readIconAsync: entry => Task.FromResult(entry.Id == recycle.Id
                    ? new ShellIcons.SystemIconResult(count == 0 ? empty! : full!, count)
                    : new ShellIcons.SystemIconResult(RefreshComputer(), null)));
            System.Windows.Media.ImageSource RefreshComputer() { computerRefreshes++; return computerIcon; }
            card.Show();
            await Until(() => card.IconRefreshCount == 2);
            check(ReferenceEquals(card.IconImage(recycle.Id)?.Source, empty), "系统入口初始使用空回收站图标");

            if (SHParseDisplayName("::{" + recycle.Id + "}", IntPtr.Zero, out pidl, 0, out _) < 0 || pidl == IntPtr.Zero)
                throw new InvalidOperationException("回收站 Shell 标识不可用");
            count = 1;
            SHChangeNotify(0x00002000, 0x1000, pidl, IntPtr.Zero); // UPDATEITEM, IDLIST | FLUSH
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => card.IconRefreshCount >= 4 && ReferenceEquals(card.IconImage(recycle.Id)?.Source, full));
            check(card.IconImage(recycle.Id)?.Source == full, "Shell 通知后立即切换满回收站图标");
            check(computerRefreshes >= 2, "无文件路径的系统入口通知刷新其他系统图标");
            count = 0;
            card.RefreshDynamicIcons();
            // The periodic fallback is throttled; another Shell event models a restore or empty operation.
            SHChangeNotify(0x00002000, 0x1000, pidl, IntPtr.Zero);
            await Until(() => card.IconRefreshCount >= 6 && ReferenceEquals(card.IconImage(recycle.Id)?.Source, empty));
            check(card.IconImage(recycle.Id)?.Source == empty, "回收站清空后恢复空图标与可访问名称");
        }
        finally
        {
            ShellIconChanges.Changed -= Changed;
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            card?.Close(); host.Close();
        }
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(100);
        if (!done()) throw new TimeoutException("系统图标未响应 Shell 通知");
    }
}
