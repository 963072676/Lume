namespace Lume.Core;

public sealed record AlignmentGuide(bool Vertical, int Position, int Start, int End);
public sealed record SnapResult(CardPlacement Placement, List<AlignmentGuide> Guides);
public static class LayoutEngine
{
    public static List<CardPlacement> Arrange(int count, CardPlacement area, IEnumerable<CardPlacement> obstacles)
    {
        var fixedCards = obstacles.ToList();
        foreach (var height in new[] { 340, 260, 200, 160 })
        {
            var placed = new List<CardPlacement>();
            for (var y = area.Y + 16; y + height <= area.Y + area.Height - 16 && placed.Count < count; y += 16)
                for (var x = area.X + 16; x + 330 <= area.X + area.Width - 16 && placed.Count < count; x += 16)
                {
                    var p = new CardPlacement(x, y, 330, height);
                    if (fixedCards.Concat(placed).Any(o => p.X < o.X + o.Width + 16 && p.X + p.Width + 16 > o.X && p.Y < o.Y + o.Height + 16 && p.Y + p.Height + 16 > o.Y)) continue;
                    placed.Add(p);
                }
            if (placed.Count == count) return placed;
        }
        throw new InvalidOperationException("当前屏幕空闲区域不足，请解锁部分分区或将分区移到其他显示器后再排列。");
    }
    public static CardPlacement Constrain(CardPlacement p, CardPlacement area, int minWidth = 240, int minHeight = 160)
    {
        var w = Math.Clamp(p.Width, Math.Min(minWidth, area.Width), area.Width);
        var h = Math.Clamp(p.Height, Math.Min(minHeight, area.Height), area.Height);
        return new(Math.Clamp(p.X, area.X, area.X + area.Width - w), Math.Clamp(p.Y, area.Y, area.Y + area.Height - h), w, h);
    }
    public static SnapResult Snap(CardPlacement p, IEnumerable<CardPlacement> peers, CardPlacement area, int threshold = 8, string edges = "move")
    {
        var list = peers.Append(area).ToList(); var guides = new List<AlignmentGuide>();
        (int delta, int line, CardPlacement peer)? x = null, y = null;
        foreach (var peer in list)
        {
            var xs = edges == "move" ? new[] { p.X, p.X + p.Width / 2, p.X + p.Width } : edges.Contains('W') ? new[] { p.X } : edges.Contains('E') ? new[] { p.X + p.Width } : [];
            var ys = edges == "move" ? new[] { p.Y, p.Y + p.Height / 2, p.Y + p.Height } : edges.Contains('N') ? new[] { p.Y } : edges.Contains('S') ? new[] { p.Y + p.Height } : [];
            foreach (var a in xs) foreach (var b in new[] { peer.X, peer.X + peer.Width / 2, peer.X + peer.Width })
                if (Math.Abs(b - a) <= threshold && (x == null || Math.Abs(b - a) < Math.Abs(x.Value.delta))) x = (b - a, b, peer);
            foreach (var a in ys) foreach (var b in new[] { peer.Y, peer.Y + peer.Height / 2, peer.Y + peer.Height })
                if (Math.Abs(b - a) <= threshold && (y == null || Math.Abs(b - a) < Math.Abs(y.Value.delta))) y = (b - a, b, peer);
        }
        if (x is { } sx)
        {
            p = edges == "move" ? p with { X = p.X + sx.delta } : edges.Contains('W') ? p with { X = p.X + sx.delta, Width = p.Width - sx.delta } : p with { Width = p.Width + sx.delta };
            guides.Add(new(true, sx.line, Math.Min(p.Y, sx.peer.Y), Math.Max(p.Y + p.Height, sx.peer.Y + sx.peer.Height)));
        }
        if (y is { } sy)
        {
            p = edges == "move" ? p with { Y = p.Y + sy.delta } : edges.Contains('N') ? p with { Y = p.Y + sy.delta, Height = p.Height - sy.delta } : p with { Height = p.Height + sy.delta };
            guides.Add(new(false, sy.line, Math.Min(p.X, sy.peer.X), Math.Max(p.X + p.Width, sy.peer.X + sy.peer.Width)));
        }
        return new(p, guides);
    }
    public static CardPlacement Resize(CardPlacement p, string edges, int dx, int dy)
    {
        if (edges.Contains('W')) { dx = Math.Min(dx, p.Width - 240); p = p with { X = p.X + dx, Width = p.Width - dx }; }
        if (edges.Contains('E')) p = p with { Width = Math.Max(240, p.Width + dx) };
        if (edges.Contains('N')) { dy = Math.Min(dy, p.Height - 160); p = p with { Y = p.Y + dy, Height = p.Height - dy }; }
        if (edges.Contains('S')) p = p with { Height = Math.Max(160, p.Height + dy) };
        return p;
    }
}
