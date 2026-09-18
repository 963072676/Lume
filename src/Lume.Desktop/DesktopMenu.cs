using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lume.Desktop;

internal sealed record DesktopMenuItem(string Id, string Label, bool Separator = false);

internal static class DesktopMenu
{
    // 菜单命令表变化时使用新的 COM 类，避免 Explorer 缓存旧类工厂。
    internal const string ClassId = "{A972E74D-D168-4A54-963A-6514BD37CB74}";
    private const string PreviousClassPath = @"CLSID\{A972E74D-D168-4A54-963A-6514BD37CB73}";
    internal const string HandlerPath = @"Directory\Background\shellex\ContextMenuHandlers\Lume";
    internal const string ClassPath = @"CLSID\" + ClassId;
    private const string OwnerId = "lume-desktop-menu-76169c84";
    /// <summary>全部可执行命令。菜单、托盘、命令面板与旧注册表项共用这一份白名单，只增不删。</summary>
    internal static readonly (string Id, string Label, string Detail)[] Actions =
    [
        ("settings", "打开设置", "常规、分区、目录与本地数据"),
        ("new-collection", "新建分区", "给文件一个新位置"),
        ("refresh", "刷新桌面", "重新扫描关注目录"),
        ("ai-organize", "AI 整理", "先分析，再预览和应用"),
        ("rules", "自动规则", "创建、编辑和排序归类规则"),
        ("history", "整理历史", "查看归类与归档记录"),
        ("undo", "撤销上一步", "恢复最近一次虚拟归类"),
        ("archive", "物理归档", "预览后移动旧文件，可恢复"),
        ("map-folder", "映射文件夹", "把常用目录显示为分区"),
        ("recent", "最近文件", "显示最近修改的 40 项"),
        ("arrange", "排列分区", "自动排列并避让锁定分区"),
        ("backup-layout", "备份布局", "保存位置和分区偏好"),
        ("restore-layout", "恢复布局", "从布局备份恢复"),
        ("collapse-all", "折叠全部", "收起桌面分区"),
        ("expand-all", "展开全部", "展开桌面分区"),
        ("mode-all", "全部视图", "显示全部桌面分区"),
        ("mode-work", "工作视图", "仅显示工作分区"),
        ("mode-presentation", "演示视图", "仅显示演示分区"),
        ("show", "启用分区", "重新显示桌面分区"),
        ("pause", "暂停分区", "恢复 Windows 原桌面"),
        ("exit", "退出 Lume", "停止驻留并恢复桌面")
    ];
    internal static readonly string[] Commands = Actions.Select(a => a.Id).ToArray();
    /// <summary>桌面右键一级菜单，只保留每天都会用到的动作；其余收进命令面板（Ctrl+K）与设置中心。</summary>
    internal static readonly DesktopMenuItem[] Items =
    [
        new("settings", "打开设置中心"), new("new-collection", "新建分区…"), new("refresh", "刷新桌面分区"),
        new("archive", "物理归档（先预览）…", true),
        new("show", "启用桌面分区", true), new("pause", "暂停分区并恢复原桌面"), new("exit", "退出 Lume 并恢复桌面")
    ];
    public static bool IsCommand(string command) => Commands.Contains(command);
    internal static string CommandLine(string executable, string command)
    {
        if (!IsCommand(command) || !Path.IsPathFullyQualified(executable) || executable.Contains('"')) throw new ArgumentException("无效的桌面菜单命令。");
        return $"\"{executable}\" --action {command}";
    }
    public static void Register()
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定 Lume 可执行文件位置。");
        // 开发时通过 dotnet 启动的 DLL 不覆盖已发布程序的系统入口。
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return;
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        WriteNative(classes, ShellPath());
        RemovePreviousClass(classes);
        using var old = classes.OpenSubKey(@"DesktopBackground\shell", true);
        if (old != null) RemoveLegacy(old);
        NotifyShell();
    }
    internal static string ShellPath()
    {
        var name = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shell-extension.txt")).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\ALume\.Shell\.[0-9a-f]{16}\.dll\z")) throw new IOException("原生菜单扩展清单无效。");
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path)) throw new IOException("缺少原生菜单扩展，请保留发布目录中的 DLL 和清单。");
        return path;
    }
    internal static void WriteNative(RegistryKey classes, string dll, string prefix = "")
    {
        if (!Path.IsPathFullyQualified(dll) || dll.Contains('"')) throw new ArgumentException("无效的原生菜单扩展路径。");
        foreach (var path in new[] { prefix + ClassPath, prefix + HandlerPath })
        {
            using var existing = classes.OpenSubKey(path);
            if (existing != null && existing.GetValue("LumeOwner") as string != OwnerId) throw new IOException("存在其他程序的同名菜单注册，未覆盖。");
        }
        using var cls = classes.CreateSubKey(prefix + ClassPath);
        cls.SetValue("LumeOwner", OwnerId); cls.SetValue("", "Lume 桌面原生菜单");
        using var server = cls.CreateSubKey("InprocServer32"); server.SetValue("", dll); server.SetValue("ThreadingModel", "Apartment");
        using var handler = classes.CreateSubKey(prefix + HandlerPath);
        handler.SetValue("LumeOwner", OwnerId); handler.SetValue("", ClassId);
    }
    internal static void RemoveNative(RegistryKey classes, string prefix = "")
    {
        foreach (var path in new[] { prefix + HandlerPath, prefix + ClassPath })
        {
            using var existing = classes.OpenSubKey(path);
            if (existing != null && existing.GetValue("LumeOwner") as string != OwnerId) throw new IOException("菜单所有者不匹配，未删除。");
        }
        Remove(classes, prefix + HandlerPath); Remove(classes, prefix + ClassPath);
    }
    private static void RemovePreviousClass(RegistryKey classes)
    {
        using var previous = classes.OpenSubKey(PreviousClassPath);
        if (previous?.GetValue("LumeOwner") as string == OwnerId) { previous.Close(); classes.DeleteSubKeyTree(PreviousClassPath, false); }
    }
    public static void Unregister()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
        if (classes != null) RemoveNative(classes);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\Classes\DesktopBackground\shell", true);
        if (parent != null) RemoveLegacy(parent);
        NotifyShell();
    }
    private static void RemoveLegacy(RegistryKey parent)
    {
        using (var key = parent.OpenSubKey("Lume"))
            if (key?.GetValue("LumeOwner") as string != OwnerId) return;
        Remove(parent, "Lume");
    }
    internal static void Remove(RegistryKey parent, string name)
    {
        using (var key = parent.OpenSubKey(name))
        {
            if (key == null) return;
            if (key.GetValue("LumeOwner") as string != OwnerId) throw new IOException("菜单所有者不匹配，未删除。");
        }
        parent.DeleteSubKeyTree(name, false);
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    internal static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}

/// <summary>固定命令列表，无任意路径或脚本参数。菜单进程仅通知当前用户会话中的驻留进程。</summary>
internal sealed class DesktopCommandSignals : IDisposable
{
    private readonly Dictionary<string, EventWaitHandle> events;
    public DesktopCommandSignals(bool demo) => events = DesktopMenu.Commands.ToDictionary(id => id,
        id => new EventWaitHandle(false, EventResetMode.AutoReset, Name(id, demo)));
    private static string Name(string command, bool demo) => $"Local\\Lume.Menu.{(demo ? "Demo." : "")}{command}";
    public static bool Send(string command, bool demo)
    {
        if (!DesktopMenu.IsCommand(command)) return false;
        if (!EventWaitHandle.TryOpenExisting(Name(command, demo), out var signal)) return false;
        using (signal) return signal.Set();
    }
    public string? Take() => events.FirstOrDefault(pair => pair.Value.WaitOne(0)).Key;
    internal IReadOnlyList<(string Command, WaitHandle Signal)> Signals => events.Select(p => (p.Key, (WaitHandle)p.Value)).ToList();
    public void Dispose() { foreach (var signal in events.Values) signal.Dispose(); }
}
