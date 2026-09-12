using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Lume.Desktop;

/// <summary>Align short Chinese menu names without injecting spaces into command names.</summary>
public sealed class SpacedMenuText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SpacedMenuText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(SpacedMenuText), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private FormattedText Format(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface("Microsoft YaHei UI"), 13, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    internal bool Distributed => Text.Length is >= 2 and <= 4 && Text.All(c => c is >= '\u4e00' and <= '\u9fff');
    internal double LabelWidth => Distributed ? 60 : Format(Text).WidthIncludingTrailingWhitespace;
    protected override Size MeasureOverride(Size availableSize) => new(LabelWidth, 22);
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!Distributed) { var text = Format(Text); drawingContext.DrawText(text, new Point(0, (ActualHeight - text.Height) / 2)); return; }
        var glyphs = Text.Select(c => Format(c.ToString())).ToArray();
        var gap = (60 - glyphs.Sum(g => g.WidthIncludingTrailingWhitespace)) / (glyphs.Length - 1);
        var x = 0.0;
        foreach (var glyph in glyphs) { drawingContext.DrawText(glyph, new Point(x, (ActualHeight - glyph.Height) / 2)); x += glyph.WidthIncludingTrailingWhitespace + gap; }
    }
}
