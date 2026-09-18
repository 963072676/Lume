using System.Text.Json;

namespace Lume.Core;

public sealed record LayoutSnapshot(int Version, Dictionary<string, CardPlacement> Positions, Dictionary<string, CardOptions> Cards, byte GlassOpacity, bool SnapEnabled, bool ShowSystemEntries = true);
public static class LayoutBackup
{
    public static LayoutSnapshot Capture(DesktopPreferences value) => new(1, StateStore.Clone(value.Positions), StateStore.Clone(value.Cards), value.GlassOpacity, value.SnapEnabled, value.ShowSystemEntries);
    public static LayoutSnapshot Read(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("布局文件过大。");
        var value = JsonSerializer.Deserialize<LayoutSnapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException("布局文件为空。");
        Validate(value); return value;
    }
    public static void Validate(LayoutSnapshot value)
    {
        if (value.Version != 1 || value.Positions == null || value.Cards == null || value.Positions.Count > 500 || value.Cards.Count > 500) throw new InvalidDataException("不支持的布局格式。");
        if (value.Positions.Any(p => p.Value == null || p.Value.Width is < 240 or > 20000 || p.Value.Height is < 160 or > 20000 || Math.Abs((long)p.Value.X) > 200000 || Math.Abs((long)p.Value.Y) > 200000)) throw new InvalidDataException("布局尺寸或坐标无效。");
        if (value.Cards.Any(p => p.Value == null || p.Value.IconSize is < 24 or > 64 || p.Value.Sort is not ("name" or "modified" or "size" or "type" or "manual") || p.Value.Order?.Count > 20000)) throw new InvalidDataException("分区外观配置无效。");
    }
    public static void Write(string path, DesktopPreferences value) => File.WriteAllText(path, JsonSerializer.Serialize(Capture(value), new JsonSerializerOptions { WriteIndented = true }));
    public static DesktopPreferences Apply(LayoutSnapshot snapshot, DesktopPreferences current, IEnumerable<string> ids)
    {
        Validate(snapshot); var result = StateStore.Clone(current); var known = ids.ToHashSet();
        known.Add("__windows-system");
        foreach (var pair in snapshot.Positions.Where(p => known.Contains(p.Key))) result.Positions[pair.Key] = pair.Value;
        foreach (var pair in snapshot.Cards.Where(p => known.Contains(p.Key))) result.Cards[pair.Key] = pair.Value;
        result.GlassOpacity = (byte)Math.Clamp((int)snapshot.GlassOpacity, 15, 240); result.SnapEnabled = snapshot.SnapEnabled; result.ShowSystemEntries = snapshot.ShowSystemEntries; return result;
    }
}
