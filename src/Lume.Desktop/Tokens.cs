using System.Windows;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;

namespace Lume.Desktop;

/// <summary>所有窗口共用的视觉契约。用户可选的分区色只作为标识，不在这里充当文字色。</summary>
internal static class Tokens
{
    public static ThemePalette Theme { get; private set; } = ThemePalette.Find(null);
    // 共享画刷保持引用稳定，已打开的窗口和控件无需重建即可换色。
    public static readonly SolidColorBrush Primary50Brush = ThemeBrush(Theme.Soft);
    public static readonly SolidColorBrush Primary100Brush = ThemeBrush(Theme.Selected);
    public static readonly SolidColorBrush Primary600Brush = ThemeBrush(Theme.Accent);
    public static readonly SolidColorBrush Primary700Brush = ThemeBrush(Theme.Hover);
    public static readonly SolidColorBrush Primary900Brush = ThemeBrush(Theme.Deep);
    public static Color Primary50 => Primary50Brush.Color;
    public static Color Primary100 => Primary100Brush.Color;
    public static Color Primary600 => Primary600Brush.Color;
    public static Color Primary700 => Primary700Brush.Color;
    public static Color Primary900 => Primary900Brush.Color;
    public static Color Glass => ThemePalette.ColorOf(Theme.Glass);

    public static void ApplyTheme(string? id)
    {
        Theme = ThemePalette.Find(id);
        SetThemeBrush(Primary50Brush, Theme.Soft);
        SetThemeBrush(Primary100Brush, Theme.Selected);
        SetThemeBrush(Primary600Brush, Theme.Accent);
        SetThemeBrush(Primary700Brush, Theme.Hover);
        SetThemeBrush(Primary900Brush, Theme.Deep);
    }

    // Binding 防止 WPF 在模板或样式中自动冻结共享画刷。
    private sealed class LiveColor(string value) : INotifyPropertyChanged
    {
        public Color Color { get; private set; } = ThemePalette.ColorOf(value);
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(string value)
        {
            Color = ThemePalette.ColorOf(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
        }
    }
    private static SolidColorBrush ThemeBrush(string value)
    {
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(LiveColor.Color)) { Source = new LiveColor(value), Mode = BindingMode.OneWay });
        return brush;
    }
    private static void SetThemeBrush(SolidColorBrush brush, string value) =>
        ((LiveColor)BindingOperations.GetBinding(brush, SolidColorBrush.ColorProperty)!.Source).Set(value);

    public static readonly Color Ink900 = Color.FromRgb(0x27, 0x33, 0x43);
    public static readonly Color Ink700 = Color.FromRgb(0x43, 0x50, 0x62);
    public static readonly Color Ink500 = Color.FromRgb(0x63, 0x70, 0x80);
    public static readonly Color Ink300 = Color.FromRgb(0x8B, 0x96, 0xA5);

    public static readonly Color Surface0 = Colors.White;
    public static readonly Color Surface50 = Color.FromRgb(0xF6, 0xF8, 0xFB);
    public static readonly Color Line100 = Color.FromRgb(0xE7, 0xEC, 0xF2);
    public static readonly Color Line200 = Color.FromRgb(0xD5, 0xDE, 0xE9);

    public static readonly Color Danger600 = Color.FromRgb(0xB3, 0x40, 0x2F);
    public static readonly Color Warning600 = Color.FromRgb(0xA9, 0x6A, 0x12);
    public static readonly Color Success600 = Color.FromRgb(0x2E, 0x7D, 0x5B);
    public static readonly Color Info600 = Color.FromRgb(0x2F, 0x5F, 0x8F);

    public const double PageTitle = 26;
    public const double DialogTitle = 20;
    public const double SectionTitle = 16;
    public const double ItemTitle = 14;
    public const double Body = 13;
    public const double Secondary = 12;
    public const double Label = 11;

    public const double Space4 = 4;
    public const double Space8 = 8;
    public const double Space12 = 12;
    public const double Space16 = 16;
    public const double Space24 = 24;
    public const double Space32 = 32;
    public const double Space48 = 48;

    public const double Radius4 = 4;
    public const double Radius8 = 8;
    public const double Radius12 = 12;
    public const double Radius16 = 16;

    public static readonly FontWeight MediumWeight = FontWeight.FromOpenTypeWeight(500);
    public static readonly SolidColorBrush Line200Brush = Brush(Line200);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Alpha(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush WhiteAlpha(byte alpha) => Alpha(Colors.White, alpha);
    public static SolidColorBrush BlackAlpha(byte alpha) => Alpha(Colors.Black, alpha);
}
