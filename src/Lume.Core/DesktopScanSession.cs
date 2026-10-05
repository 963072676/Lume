namespace Lume.Core;

/// <summary>A single-consumer directory snapshot. File events invalidate only their directory;
/// a full scan also refreshes shortcut targets that may change outside watched directories.</summary>
public sealed class DesktopScanSession
{
    private readonly Dictionary<string, ScanResult> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DesktopFile File, ShortcutTarget? Target)> shortcuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, TimeSpan, ShortcutTarget?> readShortcut;
    private CancellationToken cancellation;
    private System.Diagnostics.Stopwatch shortcutClock = new();
    private TimeSpan shortcutBudget;
    private bool refreshShortcuts;
    private int directoryShortcutFailures;
    private readonly List<string> deferredTargets = [];
    private List<string> linkedPaths = [];
    private ScanResult linked = new([], []);
    private ScanResult? snapshot;
    public int LastScannedDirectories { get; private set; }
    public int LastShortcutReads { get; private set; }
    public int LastShortcutFailures { get; private set; }

    public DesktopScanSession(Func<string, ShortcutTarget?>? readShortcut = null) => this.readShortcut = (path, _) => (readShortcut ?? ShortcutReader.Read)(path);
    public DesktopScanSession(Func<string, TimeSpan, ShortcutTarget?> readShortcut) => this.readShortcut = readShortcut;

    public ScanResult Scan(IEnumerable<string> roots, IEnumerable<string> linkedFiles, IEnumerable<string> dirtyRoots, bool full,
        CancellationToken cancellationToken = default, TimeSpan? targetBudget = null)
    {
        cancellation = cancellationToken; cancellation.ThrowIfCancellationRequested();
        shortcutBudget = targetBudget ?? TimeSpan.MaxValue; shortcutClock = System.Diagnostics.Stopwatch.StartNew(); refreshShortcuts = full;
        LastScannedDirectories = 0; LastShortcutReads = 0; LastShortcutFailures = 0;
        var requested = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dirty = dirtyRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = linkedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = snapshot == null;
        var nextDirectories = new Dictionary<string, ScanResult>(directories, StringComparer.OrdinalIgnoreCase);
        foreach (var removed in directories.Keys.Except(requested, StringComparer.OrdinalIgnoreCase).ToList()) { nextDirectories.Remove(removed); changed = true; }
        foreach (var root in requested)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!full && !dirty.Contains(root) && directories.ContainsKey(root)) continue;
            nextDirectories[root] = ScanDirectory([root], []);
            LastScannedDirectories++; changed = true;
        }
        var nextLinked = linked;
        if (full || !paths.SequenceEqual(linkedPaths, StringComparer.OrdinalIgnoreCase) || paths.Any(p => dirty.Contains(Path.GetDirectoryName(p)!)))
        {
            nextLinked = ScanDirectory([], paths); changed = true;
        }
        if (!changed) return snapshot!;
        var files = new Dictionary<string, DesktopFile>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scan in nextDirectories.Values.Append(nextLinked))
        {
            foreach (var file in scan.Files) files[file.Path] = file;
            warnings.AddRange(scan.Warnings);
            deferred.UnionWith(scan.DeferredTargets ?? []);
        }
        foreach (var old in shortcuts.Keys.Where(p => !files.ContainsKey(p)).ToList()) shortcuts.Remove(old);
        var merged = DesktopScanner.MergeDesktopShortcuts(files.Values.ToList(),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        cancellation.ThrowIfCancellationRequested();
        directories.Clear(); foreach (var pair in nextDirectories) directories.Add(pair.Key, pair.Value);
        linked = nextLinked; linkedPaths = paths;
        return snapshot = new(merged.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), warnings.Distinct().ToList(), deferred.ToList());
    }

    private ScanResult ScanDirectory(IEnumerable<string> roots, IEnumerable<string> paths)
    {
        directoryShortcutFailures = 0; deferredTargets.Clear();
        var scan = DesktopScanner.Scan(roots, paths, ReadShortcut, cancellation);
        if (directoryShortcutFailures > 0) scan.Warnings.Add("部分快捷目标读取未完成，已保留文件和仍有效的旧目标信息，后续完整扫描会重试。");
        return scan with { DeferredTargets = [.. deferredTargets] };
    }

    private ShortcutTarget? ReadShortcut(DesktopFile file)
    {
        if (file.Extension is not (".lnk" or ".url")) return null;
        cancellation.ThrowIfCancellationRequested();
        var reusable = shortcuts.TryGetValue(file.Path, out var cached) && cached.File == file;
        if (!refreshShortcuts && reusable) return cached.Target;
        LastShortcutReads++;
        ShortcutTarget? target;
        try
        {
            var remaining = shortcutBudget - shortcutClock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            target = readShortcut(file.Path, remaining);
        }
        catch (TimeoutException)
        {
            LastShortcutFailures++; directoryShortcutFailures++;
            deferredTargets.Add(file.Path);
            // A failed request is not cached. Preserve prior metadata only when the shortcut itself is unchanged.
            return reusable ? cached.Target : null;
        }
        // Bound memory for very large shortcut folders. Scans remain correct on a cache miss.
        if (shortcuts.Count >= 2048) shortcuts.Clear();
        shortcuts[file.Path] = (file, target);
        return target;
    }
}
