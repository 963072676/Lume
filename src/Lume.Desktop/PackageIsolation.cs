using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lume.Desktop;

internal static class PackageIsolation
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    internal static bool IsPackaged
    {
        get
        {
            uint size = 1024; var name = new StringBuilder((int)size); var code = GetCurrentPackageFullName(ref size, name);
            if (code == 0 || code == 122) return true;
            // 启动器的子进程可能没有包身份，仍继承 MSIX 重定向；以实际文件句柄为准。
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lume");
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".environment-" + Guid.NewGuid().ToString("N") + ".tmp");
            // 包虚拟视图会合并真实目录；仅打开已有目录不足以判断新写入的去向。
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            var actual = new StringBuilder(32768); var count = GetFinalPathNameByHandle(stream.SafeFileHandle, actual, (uint)actual.Capacity, 0);
            if (count == 0 || count >= actual.Capacity) throw new IOException("无法验证本地数据写入位置。");
            return actual.ToString().Contains(@"\Packages\", StringComparison.OrdinalIgnoreCase) && actual.ToString().Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase);
        }
    }
    internal static bool RelaunchIfNeeded(string[] args)
    {
        if (args.Any(a => a is "--guard" or "--performance-self-test" or "--menu-self-test" or "--icons-self-test" or "--features-self-test" or "--demo" or "--smoke" or "--desktop-smoke") || !IsPackaged) return false;
        var exe = Environment.ProcessPath ?? throw new IOException("无法定位 Lume。");
        if (!string.Equals(Path.GetFileName(exe), "Lume.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lume");
        var arguments = args.ToList();
        if (Directory.Exists(data)) { arguments.InsertRange(0, new[] { "--import-packaged-data", PhysicalDirectory(data) }); }
        // WMI 由系统服务创建普通桌面进程，避免继承启动器的 MSIX 文件/注册表虚拟化。
        // 只启动当前可执行文件，使用结构化 WMI 参数和 Windows argv 引号规则。
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new IOException("无法访问 Windows 进程启动服务。");
        dynamic locator = Activator.CreateInstance(type)!;
        dynamic services = locator.ConnectServer(".", "root\\cimv2");
        dynamic processClass = services.Get("Win32_Process");
        dynamic startupClass = services.Get("Win32_ProcessStartup");
        dynamic startup = startupClass.SpawnInstance_(); startup.ShowWindow = 0;
        try
        {
            object pid = 0;
            var result = (uint)processClass.Create(string.Join(" ", new[] { exe }.Concat(arguments).Select(Quote)), Path.GetDirectoryName(exe), startup, out pid);
            if (result != 0) throw new IOException($"无法退出启动器的应用包隔离环境，Windows 错误码 {result}。");
            if (args.Contains("--stop") || args.Contains("--action"))
            {
                try { using var process = Process.GetProcessById(Convert.ToInt32(pid)); process.WaitForExit(5000); } catch (ArgumentException) { }
            }
        }
        finally { foreach (object obj in new object[] { startup, startupClass, processClass, services, locator }) Marshal.FinalReleaseComObject(obj); }
        return true;
    }
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2); return result.Append('"').ToString();
    }
    private static string PhysicalDirectory(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("无法读取现有数据的实际位置。");
        var actual = new StringBuilder(32768); var count = GetFinalPathNameByHandle(handle, actual, (uint)actual.Capacity, 0);
        if (count == 0 || count >= actual.Capacity) throw new IOException("无法解析现有数据的实际位置。");
        return actual.ToString();
    }
    internal static void ImportData(string source, string target)
    {
        if (!Path.IsPathFullyQualified(source)) throw new IOException("旧数据目录需要绝对路径。");
        if (File.Exists(Path.Combine(target, "state.json")) || !File.Exists(Path.Combine(source, "state.json"))) return;
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("真实数据目录已有内容，未覆盖。请保留两处数据后核对。");
        var stage = target + ".import-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage);
        // 不迁移旧进程的图层租约、启动器路径或临时文件；原数据完整保留。
        foreach (var name in new[] { "state.json", "state.json.bak", "layout-before-restore.json" }) CopyFile(Path.Combine(source, name), Path.Combine(stage, name));
        var journal = Path.Combine(source, "archive-journal");
        if (Directory.Exists(journal)) CopyDirectory(journal, Path.Combine(stage, "archive-journal"));
        foreach (var path in Directory.EnumerateFiles(source, "state.json.desktop.*")) CopyFile(path, Path.Combine(stage, Path.GetFileName(path)));
        var history = Path.Combine(source, "state.json.history");
        if (Directory.Exists(history)) CopyDirectory(history, Path.Combine(stage, "state.json.history"));
        CopyFile(Path.Combine(source, "state.json.legacy.bak"), Path.Combine(stage, "state.json.legacy.bak"));
        _ = new Lume.Core.StateStore(Path.Combine(stage, "state.json")).Load([]);
        if (Directory.Exists(target)) Directory.Delete(target, false);
        Directory.Move(stage, target);
        File.WriteAllText(Path.Combine(target, "package-migration.txt"), $"从启动器隔离目录复制并校验，原数据保留：{source}\n{DateTimeOffset.Now:O}");
    }
    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("旧日志目录包含链接，停止迁移。");
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFiles(source)) CopyFile(path, Path.Combine(target, Path.GetFileName(path)));
        foreach (var path in Directory.EnumerateDirectories(source)) CopyDirectory(path, Path.Combine(target, Path.GetFileName(path)));
    }
    private static void CopyFile(string source, string target)
    {
        if (!File.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("旧数据文件包含链接，停止迁移。");
        File.Copy(source, target, false);
        using var a = File.OpenRead(source); using var b = File.OpenRead(target);
        if (!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b))) throw new IOException("数据迁移校验不一致，原数据未改动。");
    }
}
