using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Lume.Core;

namespace Lume.Desktop;

internal sealed class AlignmentOverlay : IDisposable
{
    private readonly List<Window> lines = [];
    internal int VisibleCount => lines.Count(w => w.IsVisible && DesktopNative.GetParent(new WindowInteropHelper(w).Handle) != IntPtr.Zero);
    public void Show(IntPtr desktop, IReadOnlyList<AlignmentGuide> guides)
    {
        while (lines.Count > guides.Count) { lines[^1].Close(); lines.RemoveAt(lines.Count - 1); }
        for (var i = 0; i < guides.Count; i++)
        {
            if (i == lines.Count)
            {
                var line = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromRgb(102, 226, 184)), ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false, Width = 2, Height = 2, Left = -16000 };
                line.SourceInitialized += (_, _) =>
                {
                    var h = new WindowInteropHelper(line).Handle; DesktopNative.Attach(h, desktop);
                    DesktopNative.SetStyle(h, -20, new IntPtr(DesktopNative.GetStyle(h, -20).ToInt64() | 0x08000020));
                };
                lines.Add(line); line.Show();
            }
            var g = guides[i]; var p = new DesktopNative.Point { X = g.Vertical ? g.Position : g.Start, Y = g.Vertical ? g.Start : g.Position };
            DesktopNative.ScreenToClient(desktop, ref p);
            DesktopNative.SetWindowPos(new WindowInteropHelper(lines[i]).Handle, IntPtr.Zero, p.X, p.Y, g.Vertical ? 2 : Math.Max(2, g.End - g.Start), g.Vertical ? Math.Max(2, g.End - g.Start) : 2, 0x10 | 0x40);
        }
    }
    public void Dispose() { foreach (var line in lines) line.Close(); lines.Clear(); }
}
