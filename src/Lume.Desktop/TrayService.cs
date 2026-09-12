using System.Drawing;
using Forms = System.Windows.Forms;

namespace Lume.Desktop;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon artwork;
    public TrayService(Action settings, Action toggle, Action archive, Action exit)
    {
        using var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lume.ico")).Stream;
        artwork = new Icon(resource, 32, 32);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置中心", null, (_, _) => settings());
        menu.Items.Add("显示 / 暂停桌面分区", null, (_, _) => toggle());
        menu.Items.Add("物理归档…", null, (_, _) => archive());
        menu.Items.Add("此电脑", null, (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true }));
        menu.Items.Add("回收站", null, (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出并恢复桌面", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Text = "Lume · 桌面分区", Icon = artwork, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => settings();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public void Notify(string message) { icon.BalloonTipTitle = "Lume"; icon.BalloonTipText = message; icon.ShowBalloonTip(3500); }
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); artwork.Dispose(); }
}
