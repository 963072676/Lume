namespace Lume.Core;

/// <summary>A single-consumer directory snapshot. File events invalidate only their directory;
/// a full scan also refreshes shortcut targets that may change outside watched directories.</summary>
public sealed class DesktopScanSession
{
    private readonly Dictionary<string, ScanResult> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DesktopFile File, ShortcutTarget? Target)> shortcuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, ShortcutTarget?> readShortcut;
    private List<string> linkedPaths = [];
    private ScanResult linked = new([], []);
    private ScanResult? snapshot;
    public int LastScannedDirectories { get; private set; }
    public int LastShortcutReads { get; private set; }

    public DesktopScanSession(Func<string, ShortcutTarget?>? readShortcut = null) => this.readShortcut = readShortcut ?? ShortcutReader.Read;

    public ScanResult Scan(IEnumerable<string> roots, IEnumerable<string> linkedFiles, IEnumerable<string> dirtyRoots, bool full)
    {
        LastScannedDirectories = 0; LastShortcutReads = 0;
        var requested = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dirty = dirtyRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = linkedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = snapshot == null;
        if (full) shortcuts.Clear();
        foreach (var removed in directories.Keys.Except(requested, StringComparer.OrdinalIgnoreCase).ToList()) { directories.Remove(removed); changed = true; }
        foreach (var root in requested)
        {
            if (!full && !dirty.Contains(root) && directories.ContainsKey(root)) continue;
            directories[root] = DesktopScanner.Scan([root], readShortcut: ReadShortcut);
            LastScannedDirectories++; changed = true;
        }
        if (full || !paths.SequenceEqual(linkedPaths, StringComparer.OrdinalIgnoreCase) || paths.Any(p => dirty.Contains(Path.GetDirectoryName(p)!)))
        {
            linked = DesktopScanner.Scan([], paths, ReadShortcut); linkedPaths = paths; changed = true;
        }
        if (!changed) return snapshot!;
        var files = new Dictionary<string, DesktopFile>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var scan in directories.Values.Append(linked))
        {
            foreach (var file in scan.Files) files[file.Path] = file;
            warnings.AddRange(scan.Warnings);
        }
        foreach (var old in shortcuts.Keys.Where(p => !files.ContainsKey(p)).ToList()) shortcuts.Remove(old);
        var merged = DesktopScanner.MergeDesktopShortcuts(files.Values.ToList(),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        return snapshot = new(merged.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), warnings.Distinct().ToList());
    }

    private ShortcutTarget? ReadShortcut(DesktopFile file)
    {
        if (file.Extension is not (".lnk" or ".url")) return null;
        if (shortcuts.TryGetValue(file.Path, out var cached) && cached.File == file) return cached.Target;
        LastShortcutReads++;
        var target = readShortcut(file.Path);
        // Bound memory for very large shortcut folders. Scans remain correct on a cache miss.
        if (shortcuts.Count >= 2048) shortcuts.Clear();
        shortcuts[file.Path] = (file, target);
        return target;
    }
}
