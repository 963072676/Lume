namespace Lume.Core;

public sealed record ScanResult(List<DesktopFile> Files, List<string> Warnings);

public static class DesktopScanner
{
    public static ScanResult Scan(IEnumerable<string> roots, IEnumerable<string>? linkedFiles = null)
    {
        var result = new Dictionary<string, DesktopFile>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) { warnings.Add($"目录不可访问：{root}"); continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        var attributes = info.Attributes;
                        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        // OneDrive 云占位文件也使用 ReparsePoint，不能将所有重解析点都丢弃。
                        if ((attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget != null) continue;
                        var directory = (attributes & FileAttributes.Directory) != 0;
                        result[info.FullName] = new(info.FullName, info.Name, directory ? "" : info.Extension.ToLowerInvariant(),
                            directory ? 0 : info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, directory, InferSource(info.FullName));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"暂时无法读取：{path}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { warnings.Add($"无法扫描：{root}（{ex.Message}）"); }
        }
        foreach (var path in linkedFiles ?? [])
        {
            try
            {
                var info = new FileInfo(path);
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.LinkTarget != null) continue;
                var directory = (info.Attributes & FileAttributes.Directory) != 0;
                result[info.FullName] = new(info.FullName, info.Name, directory ? "" : info.Extension.ToLowerInvariant(), directory ? 0 : info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, directory, InferSource(info.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"暂时无法读取：{path}"); }
        }
        var files = result.Values.Select(f => f with { Target = f.IsDirectory ? null : ShortcutReader.Read(f.Path) }).ToList();
        return new(MergeDesktopShortcuts(files, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), warnings);
    }

    public static List<DesktopFile> MergeDesktopShortcuts(List<DesktopFile> files, string personal, string common)
    {
        bool Same(DesktopFile a, DesktopFile b) => a.Target != null && b.Target != null
            && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && a.Target.Path.Equals(b.Target.Path, StringComparison.OrdinalIgnoreCase)
            && a.Target.Arguments == b.Target.Arguments && a.Target.WorkingDirectory.Equals(b.Target.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        var user = files.Where(f => string.Equals(Path.GetDirectoryName(f.Path), personal, StringComparison.OrdinalIgnoreCase)).ToList();
        return files.Where(f => !string.Equals(Path.GetDirectoryName(f.Path), common, StringComparison.OrdinalIgnoreCase) || !user.Any(u => Same(u, f))).ToList();
    }

    // 来源仅为文件名和路径推测；不伪装成已识别创建进程。
    public static string InferSource(string path)
    {
        var name = Path.GetFileName(path);
        if (path.Contains("WeChat", StringComparison.OrdinalIgnoreCase) || name.StartsWith("微信")) return "微信（推测）";
        if (path.Contains("Tencent Files", StringComparison.OrdinalIgnoreCase) || name.StartsWith("QQ")) return "QQ（推测）";
        if (name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Snip", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("屏幕截图") || name.StartsWith("截屏")) return "截图工具（推测）";
        if (Path.GetDirectoryName(path)?.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("Downloads", StringComparison.OrdinalIgnoreCase) || p == "下载") == true)
            return "下载目录（推测）";
        return "未知来源";
    }
}
