using System.Windows;
using System.Windows.Media;

namespace Lume.Desktop;

/// <summary>所有窗口共用的视觉契约。用户可选的分区色只作为标识，不在这里充当文字色。</summary>
internal static class Tokens
{
    public static readonly Color Primary50 = Color.FromRgb(0xEE, 0xF4, 0xF0);
    public static readonly Color Primary100 = Color.FromRgb(0xDC, 0xE9, 0xE2);
    public static readonly Color Primary600 = Color.FromRgb(0x35, 0x6B, 0x57);
    public static readonly Color Primary700 = Color.FromRgb(0x28, 0x52, 0x40);
    public static readonly Color Primary900 = Color.FromRgb(0x24, 0x33, 0x2E);

    public static readonly Color Ink900 = Color.FromRgb(0x24, 0x33, 0x2E);
    public static readonly Color Ink700 = Color.FromRgb(0x3A, 0x4A, 0x42);
    public static readonly Color Ink500 = Color.FromRgb(0x5F, 0x6B, 0x65);
    public static readonly Color Ink300 = Color.FromRgb(0x8A, 0x94, 0x8F);

    public static readonly Color Surface0 = Colors.White;
    public static readonly Color Surface50 = Color.FromRgb(0xF5, 0xF7, 0xF6);
    public static readonly Color Line100 = Color.FromRgb(0xE9, 0xED, 0xE7);
    public static readonly Color Line200 = Color.FromRgb(0xD6, 0xDC, 0xD3);

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
