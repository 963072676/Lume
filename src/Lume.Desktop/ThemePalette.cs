using System.Windows.Media;
using Lume.Core;

namespace Lume.Desktop;

internal sealed record ThemePalette(string Id, string Name, string Description,
    string Soft, string Selected, string Accent, string Hover, string Deep, string Glass)
{
    public static IReadOnlyList<ThemePalette> All { get; } = Array.AsReadOnly(new[]
    {
        new ThemePalette("sky", "晴空蓝", "干净、轻盈", "#EEF6FF", "#DCEBFC", "#2866A6", "#205388", "#243B53", "#30465C"),
        new ThemePalette("lavender", "浅雾紫", "柔和、安静", "#F5F1FC", "#EAE0F7", "#72529B", "#5B417D", "#3F3452", "#484057"),
        new ThemePalette("apricot", "暖杏茶", "温暖、明亮", "#FFF5EB", "#F8E5D3", "#966032", "#794A25", "#513D2E", "#574739"),
        new ThemePalette("sage", "经典青绿", "熟悉、自然", "#EEF4F0", "#DCE9E2", "#356B57", "#285240", "#24332E", "#192320")
    });

    public static ThemePalette Find(string? id) => All.First(p => p.Id == ThemeIds.Normalize(id));
    public static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
}
