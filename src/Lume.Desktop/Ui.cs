using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lume.Desktop;

public static class Ui
{
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached("HoverBrush", typeof(Brush), typeof(Ui), new PropertyMetadata(Brushes.Transparent));
    public static Brush GetHoverBrush(DependencyObject target) => (Brush)target.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject target, Brush value) => target.SetValue(HoverBrushProperty, value);
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.RegisterAttached("Selected", typeof(bool), typeof(Ui), new PropertyMetadata(false));
    public static bool GetSelected(DependencyObject target) => (bool)target.GetValue(SelectedProperty);
    public static void SetSelected(DependencyObject target, bool value) => target.SetValue(SelectedProperty, value);
    public static readonly DependencyProperty DarkTileProperty = DependencyProperty.RegisterAttached("DarkTile", typeof(bool), typeof(Ui), new PropertyMetadata(false));
    public static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    public static SolidColorBrush Brush(Color color) => Tokens.Brush(color);
    public static readonly Brush Ink = Tokens.Brush(Tokens.Ink900);
    public static readonly Brush Muted = Tokens.Brush(Tokens.Ink500);
    public static readonly Brush Accent = Tokens.Brush(Tokens.Primary600);
    public static readonly Brush SecondaryInk = Tokens.Brush(Tokens.Ink700);
    public static readonly Brush Danger = Tokens.Brush(Tokens.Danger600);
    public const string EmptyDropText = "拖到这里归类";

    public static void InstallStyles(Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Lume;component/Styles.xaml", UriKind.Relative) });
        var windowStyle = new Style(typeof(Window), (Style)app.Resources[typeof(Window)]);
        windowStyle.Setters.Add(new Setter(Window.IconProperty,
            new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/lume.png"))));
        app.Resources[typeof(Window)] = windowStyle;
    }

    public static TextBlock Text(string text, double size = Tokens.Body, Brush? color = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Ink, FontWeight = bold ? Tokens.MediumWeight : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, MinWidth = 24, MinHeight = 32 };
        if (primary)
        {
            // 共用模板，以语义样式指定深色悬停层，保持白字可读。
            UseStyle(button, "PrimaryButton");
            button.Background = Accent; button.Foreground = Brushes.White; button.BorderBrush = Accent;
        }
        button.Click += (_, _) => action();
        return button;
    }
    /// <summary>按资源键套用语义样式；未找到时保持默认，不改变调用方行为。</summary>
    public static void UseStyle(Button button, string key)
    {
        if (Application.Current?.TryFindResource(key) is Style style) button.Style = style;
    }
    /// <summary>恢复为默认按钮模板（用于选中态切换回未选中）。</summary>
    public static void UseDefaultStyle(Button button)
    {
        if (Application.Current?.TryFindResource(typeof(Button)) is Style style) button.Style = style;
    }
    public static Button IconButton(string glyph, Action action, string name, bool light = false)
    {
        var button = Button(glyph, action);
        button.Width = 24; button.Height = 24; button.MinWidth = 24; button.MinHeight = 24;
        button.Padding = new Thickness(0); button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center; button.Margin = new Thickness(0, 0, 4, 0);
        button.Background = light ? Tokens.WhiteAlpha(0x18) : Brushes.Transparent;
        button.Foreground = light ? Brushes.White : Ink;
        button.BorderBrush = light ? Tokens.WhiteAlpha(0x48) : Tokens.Line200Brush;
        button.BorderThickness = new Thickness(0);
        button.ToolTip = name;
        if (light) UseStyle(button, "LightIconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
    public static void SetDarkTile(Button button, bool value = true) => button.SetValue(DarkTileProperty, value);
    public static bool IsDarkTile(Button button) => (bool)button.GetValue(DarkTileProperty);
    public static void UpdateTile(Button button, bool selected)
    {
        SetSelected(button, selected);
        var dark = IsDarkTile(button);
        if (dark)
        {
            button.Background = selected ? Tokens.WhiteAlpha(0x3D) : Brushes.Transparent;
            button.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
            button.Foreground = Brushes.White;
        }
        else
        {
            // 选中填充必须与模板悬停色 #EEF4F0 拉开色阶，否则多选后无法确认选中项。
            button.Background = selected ? Tokens.Brush(Tokens.Primary100) : Brushes.Transparent;
            button.BorderBrush = selected ? Accent : Brushes.Transparent;
            button.Foreground = selected ? Tokens.Brush(Tokens.Primary700) : Ink;
        }
        button.BorderThickness = new Thickness(selected ? 2 : 1);
    }
    public static Border Badge(string text, Brush? foreground = null, Brush? background = null) => new()
    {
        Child = Text(text, Tokens.Label, foreground ?? Ink, true),
        Background = background ?? Tokens.Brush(Tokens.Primary100), BorderBrush = Brushes.Transparent,
        CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
    };
    public static Border DropZone(string text = EmptyDropText, Brush? foreground = null, Brush? border = null, Brush? background = null)
    {
        foreground ??= Tokens.WhiteAlpha(0xD9); border ??= Tokens.WhiteAlpha(0x59); background ??= Tokens.WhiteAlpha(0x08);
        var content = Text(text, Tokens.Label, foreground); content.TextAlignment = TextAlignment.Center;
        var zone = new Border { Child = content, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Tokens.Radius8), Padding = new Thickness(Tokens.Space8), MinHeight = 54 };
        var grid = new Grid(); grid.Children.Add(new Rectangle { Stroke = border, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 }, RadiusX = Tokens.Radius8, RadiusY = Tokens.Radius8 }); grid.Children.Add(zone);
        return new Border { Child = grid, Background = background, CornerRadius = new CornerRadius(Tokens.Radius8), Margin = new Thickness(0, Tokens.Space8, 0, 0) };
    }
    public static Grid FormGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, Tokens.Space8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }
    public static void AddFormRow(Grid grid, string label, string description, UIElement control, int row)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { MinHeight = 58 });
        var text = new StackPanel { Margin = new(0, 8, 20, 8), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(label, Tokens.Body, SecondaryInk));
        if (!string.IsNullOrEmpty(description)) { var detail = Text(description, Tokens.Label, Muted); detail.Margin = new(0, 3, 0, 0); text.Children.Add(detail); }
        Grid.SetRow(text, row); grid.Children.Add(text);
        Grid.SetRow(control, row); Grid.SetColumn(control, 1); if (control is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(control);
    }
    public static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = Tokens.Brush(Tokens.Surface0), BorderBrush = Tokens.Brush(Tokens.Line100), BorderThickness = new(1), CornerRadius = new(Tokens.Radius12),
        Padding = padding ?? new Thickness(Tokens.Space16), Child = child, Margin = new(0, 0, 0, Tokens.Space12)
    };
    public static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    public static string? Prompt(Window owner, string title, string label, string initial = "")
    {
        var window = new Window { Owner = owner, Title = title, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(26) };
        panel.Children.Add(Text(label));
        var input = new TextBox { Margin = new(0, 12, 0, 20), MaxLength = 24, Text = initial };
        panel.Children.Add(input);
        var ok = Button("保存", () => { if (!string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; }, true);
        ok.IsDefault = true;
        panel.Children.Add(Row(ok, Button("取消", () => window.DialogResult = false)));
        window.Content = panel;
        window.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? input.Text : null;
    }
}
