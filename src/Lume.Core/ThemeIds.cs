namespace Lume.Core;

public static class ThemeIds
{
    public const string Default = "sky";
    public static bool IsKnown(string? value) => value is "sky" or "lavender" or "apricot" or "sage";
    public static string Normalize(string? value) => IsKnown(value) ? value! : Default;
}
