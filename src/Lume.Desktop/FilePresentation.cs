using System.IO;
using Lume.Core;

namespace Lume.Desktop;

internal static class FilePresentation
{
    internal static bool IsShortcut(DesktopFile file) => !file.IsDirectory &&
        new[] { ".lnk", ".url", ".appref-ms" }.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);
    internal static string DisplayName(DesktopFile file)
    {
        var name = IsShortcut(file) ? Path.GetFileNameWithoutExtension(file.Name) : file.Name;
        return string.IsNullOrWhiteSpace(name) ? file.Name : name;
    }
}
