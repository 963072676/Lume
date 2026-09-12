namespace Lume.Core;

public sealed class Organizer(StateStore store, AppState state)
{
    public AppState State { get; private set; } = state;
    public IReadOnlyList<DesktopFile> Files { get; private set; } = [];
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public List<string> WatchRoots => State.Configuration.Roots.Concat(State.Configuration.Collections.Where(c => c.MappedPath != null).Select(c => c.MappedPath!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public List<string> MonitorRoots => WatchRoots.Concat(State.Configuration.LinkedFiles.Select(p => System.IO.Path.GetDirectoryName(p)!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public CardOptions Options(string id) => State.Desktop.Cards.GetValueOrDefault(id) ?? new();
    public void SetOptions(string id, CardOptions options) => Transaction(() => State.Desktop.Cards[id] = options with { IconSize = Math.Clamp(options.IconSize, 24, 64) });
    public void SetSnap(bool enabled) => Transaction(() => State.Desktop.SnapEnabled = enabled);
    public void SetSystemEntries(bool enabled) => Transaction(() => State.Desktop.ShowSystemEntries = enabled);
    public void SetAllIconSize(int size)
    {
        if (size is not (26 or 34 or 48)) throw new ArgumentOutOfRangeException(nameof(size));
        Transaction(() => { foreach (var c in State.Configuration.Collections) State.Desktop.Cards[c.Id] = Options(c.Id) with { IconSize = size }; });
    }
    public void SetAllCollapsed(bool collapsed) => Transaction(() =>
    {
        foreach (var collection in State.Configuration.Collections)
            State.Desktop.Cards[collection.Id] = Options(collection.Id) with { Collapsed = collapsed };
    });
    public void SetLayout(Dictionary<string, CardPlacement> positions) => Transaction(() => State.Desktop.Positions = positions);
    public void RestoreDesktop(DesktopPreferences preferences) => Transaction(() => State.Desktop = StateStore.Clone(preferences));
    public IReadOnlyList<DesktopFile> CollectionFiles(string id, string query = "")
    {
        var collection = State.Configuration.Collections.First(c => c.Id == id); var options = Options(id);
        IEnumerable<DesktopFile> selected = collection.Recent ? Files.Where(f => !f.IsDirectory).OrderByDescending(f => f.ModifiedUtc).Take(40)
            : collection.MappedPath != null ? Files.Where(f => string.Equals(System.IO.Path.GetDirectoryName(f.Path), collection.MappedPath, StringComparison.OrdinalIgnoreCase))
            : Files.Where(f => CollectionOf(f) == id && (State.Configuration.Overrides.ContainsKey(f.Path) || !State.Configuration.Collections.Any(c => c.MappedPath != null && string.Equals(System.IO.Path.GetDirectoryName(f.Path), c.MappedPath, StringComparison.OrdinalIgnoreCase))));
        selected = selected.Where(f => RuleEngine.Search(f, query));
        if (collection.Recent) return selected.ToList();
        if (options.Sort == "manual") return selected.OrderBy(f => { var i = options.Order?.FindIndex(p => p.Equals(f.Path, StringComparison.OrdinalIgnoreCase)) ?? -1; return i < 0 ? int.MaxValue : i; }).ThenBy(f => f.Name).ToList();
        Func<DesktopFile, object> key = options.Sort switch { "modified" => f => f.ModifiedUtc, "size" => f => f.Size, "type" => f => f.Extension, _ => f => f.Name };
        return (options.Descending ? selected.OrderByDescending(key) : selected.OrderBy(key)).ThenBy(f => f.Name).ToList();
    }
    public void AddMappedCollection(string path)
    {
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(path)) throw new IOException("映射目录不可访问。");
        if (State.Configuration.Collections.Any(c => string.Equals(c.MappedPath, path, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("该文件夹已建立映射。");
        var name = new DirectoryInfo(path).Name;
        Edit("新建文件夹映射", c => c.Collections.Add(new(Guid.NewGuid().ToString("N"), name, "#92C7B5", MappedPath: path)));
    }
    public void AddRecentCollection()
    {
        if (State.Configuration.Collections.Any(c => c.Recent)) return;
        Edit("显示最近文件", c => c.Collections.Add(new(Guid.NewGuid().ToString("N"), "最近文件", "#92C7B5", Recent: true)));
    }
    public void AssignMany(IEnumerable<string> paths, string id)
    {
        var selected = paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!State.Configuration.Collections.Any(c => c.Id == id && c.MappedPath == null && !c.Recent)) throw new InvalidOperationException("映射和最近文件分区由目录内容决定，请拖入普通分区。");
        var unknown = selected.Where(p => !Files.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
        var scan = DesktopScanner.Scan([], unknown);
        if (scan.Files.Count != unknown.Count) throw new InvalidOperationException("部分文件不可访问，未导入；请检查文件是否已移动或属于系统隐藏文件。");
        Edit("拖入分区", c => { foreach (var path in selected) { c.Overrides[path] = id; State.Assignments[path] = id; } c.LinkedFiles = c.LinkedFiles.Concat(unknown).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }, "只创建分区引用，原文件保留在原位置。");
        Files = Files.Concat(scan.Files).ToList();
    }
    public string CollectionOf(DesktopFile file) => State.Assignments.GetValueOrDefault(file.Path, "inbox");
    public void ReorderFile(string collectionId, string path, string? beforePath)
    {
        var collection = State.Configuration.Collections.Single(c => c.Id == collectionId);
        if (collection.Recent) throw new InvalidOperationException("最近文件按修改时间排序。");
        var files = CollectionFiles(collectionId).Select(f => f.Path).ToList();
        var from = files.FindIndex(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || (beforePath != null && !files.Contains(beforePath, StringComparer.OrdinalIgnoreCase))) throw new InvalidOperationException("排序文件已不在分区中。");
        if (string.Equals(path, beforePath, StringComparison.OrdinalIgnoreCase)) return;
        var moved = files[from]; files.RemoveAt(from);
        var to = beforePath == null ? files.Count : files.FindIndex(p => p.Equals(beforePath, StringComparison.OrdinalIgnoreCase)); files.Insert(to, moved);
        SetOptions(collectionId, Options(collectionId) with { Sort = "manual", Order = files });
    }
    public string CollectionName(string id) => State.Configuration.Collections.FirstOrDefault(c => c.Id == id)?.Name ?? "临时收件箱";
    public void SavePlacement(string id, CardPlacement placement) => Transaction(() => State.Desktop.Positions[id] = placement);
    public void SetDesktopMode(int mode) => Transaction(() => State.Desktop.Mode = Math.Clamp(mode, 0, 2));
    public void SetGlassOpacity(byte opacity) => Transaction(() => State.Desktop.GlassOpacity = (byte)Math.Clamp((int)opacity, 150, 235));
    public void SetContextMenuEnabled(bool enabled) => Transaction(() => State.Desktop.ContextMenuEnabled = enabled);

    private void Transaction(Action mutation)
    {
        var previous = StateStore.Clone(State);
        try { mutation(); store.Save(State); }
        catch { State = previous; StateStore.Normalize(State); throw; }
    }

    public void ApplyScan(ScanResult scan)
    {
        Files = scan.Files;
        Warnings = scan.Warnings;
        var changes = CalculateChanges();
        if (changes.Count == 0) return;
        Transaction(() =>
        {
            foreach (var change in changes) State.Assignments[change.Path] = change.After;
            State.History.Add(new() { Title = $"自动归类 {changes.Count} 项", Detail = string.Join("\n", changes.Select(c => $"{System.IO.Path.GetFileName(c.Path)} → {CollectionName(c.After)}")), Changes = changes });
        });
    }

    private List<AssignmentChange> CalculateChanges() => Files.Select(f => new AssignmentChange(f.Path,
        State.Assignments.GetValueOrDefault(f.Path), RuleEngine.Classify(f, State.Configuration, DateTime.UtcNow)))
        .Where(c => c.Before != c.After).ToList();

    private void Reclassify()
    {
        foreach (var f in Files) State.Assignments[f.Path] = RuleEngine.Classify(f, State.Configuration, DateTime.UtcNow);
    }

    private void Edit(string title, Action<Configuration> edit, string detail = "") => Transaction(() =>
    {
        var before = StateStore.Clone(State.Configuration);
        edit(State.Configuration);
        Reclassify();
        State.History.Add(new() { Title = title, Detail = detail, PreviousConfiguration = before });
    });

    public void Assign(string path, string collectionId)
    {
        var file = Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("文件已不在当前视图，请刷新。");
        if (!State.Configuration.Collections.Any(c => c.Id == collectionId && c.MappedPath == null && !c.Recent)) throw new InvalidOperationException("请选择普通分区；映射和最近文件分区自动更新。");
        if (CollectionOf(file) == collectionId && State.Configuration.Overrides.ContainsKey(file.Path)) return;
        Edit($"手动归入「{CollectionName(collectionId)}」", c =>
        {
            c.Overrides[file.Path] = collectionId;
            c.Samples.RemoveAll(s => s.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (!file.IsDirectory && file.Extension.Length > 0) c.Samples.Add(new(file.Path, file.Extension, collectionId));
        }, file.Name);
    }

    public void Release(string path) => Edit("恢复自动归类", c => c.Overrides.Remove(path), System.IO.Path.GetFileName(path));

    public AiSnapshot CaptureAiSnapshot(bool includePinned = false, bool inboxOnly = false)
    {
        var config = State.Configuration;
        var files = Files.Where(f => (includePinned || !config.Overrides.ContainsKey(f.Path))
            && (!inboxOnly || CollectionOf(f) == "inbox")
            && !config.Collections.Any(c => c.MappedPath != null && string.Equals(System.IO.Path.GetDirectoryName(f.Path), c.MappedPath, StringComparison.OrdinalIgnoreCase)));
        return new(files.Select((f, i) => new AiItem("f" + i, f, CollectionOf(f), config.Overrides.GetValueOrDefault(f.Path))).ToList(),
            config.Collections.Where(c => c.MappedPath == null && !c.Recent).ToList());
    }

    public void ApplyAiSuggestions(AiSnapshot snapshot, IReadOnlyList<AiSuggestion> selected, IReadOnlyList<Collection>? pendingCollections = null)
    {
        if (selected.Count == 0) throw new InvalidOperationException("请先勾选需要应用的建议。");
        if (selected.Select(s => s.ItemId).Distinct().Count() != selected.Count) throw new InvalidOperationException("建议包含重复文件。");
        var pending = (pendingCollections ?? []).Where(c => selected.Any(s => s.CollectionId == c.Id)).ToList();
        var all = State.Configuration.Collections.Concat(pending).ToList();
        if (all.Select(c => c.Id).Distinct().Count() != all.Count || all.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Count
            || pending.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 80 || c.MappedPath != null || c.Recent)) throw new InvalidOperationException("新分组名称或标识无效，可能已存在同名分组。");
        var live = DesktopScanner.Scan([], snapshot.Items.Where(i => selected.Any(s => s.ItemId == i.Id)).Select(i => i.File.Path)).Files;
        var changes = selected.Select(s =>
        {
            var original = snapshot.Items.SingleOrDefault(f => f.Id == s.ItemId) ?? throw new InvalidOperationException("建议文件不在分析范围。");
            var current = Files.FirstOrDefault(f => f.Path.Equals(original.File.Path, StringComparison.OrdinalIgnoreCase));
            if (current == null || current != original.File || !live.Contains(original.File) || CollectionOf(current) != original.CollectionId
                || State.Configuration.Overrides.GetValueOrDefault(current.Path) != original.PinnedCollectionId
                || (!File.Exists(current.Path) && !Directory.Exists(current.Path)))
                throw new InvalidOperationException("分析后文件或归属已变化，请重新分析后应用。");
            if (!snapshot.Collections.Any(c => c.Id == s.CollectionId && State.Configuration.Collections.Contains(c)) && !pending.Any(c => c.Id == s.CollectionId))
                throw new InvalidOperationException("目标分区已改变，请重新分析。");
            return (File: current, Suggestion: s);
        }).ToList();
        Edit($"应用 AI 归类 {changes.Count} 项", c =>
        {
            c.Collections.AddRange(pending);
            foreach (var change in changes) c.Overrides[change.File.Path] = change.Suggestion.CollectionId;
        }, string.Join("\n", changes.Select(x => $"{x.File.Name} → {CollectionName(x.Suggestion.CollectionId)}")));
    }

    public void SaveRule(Rule rule)
    {
        var error = RuleEngine.Validate(rule, State.Configuration.Collections);
        if (error != null) throw new InvalidOperationException(error);
        Edit($"保存规则「{rule.Name}」", c =>
        {
            var index = c.Rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0) c.Rules[index] = rule; else c.Rules.Insert(0, rule);
        });
    }
    public void DeleteRule(string id) => Edit("删除规则", c => c.Rules.RemoveAll(r => r.Id == id));
    public void MoveRule(string id, int delta) => Edit("调整规则优先级", c =>
    {
        var from = c.Rules.FindIndex(r => r.Id == id);
        var to = Math.Clamp(from + delta, 0, c.Rules.Count - 1);
        if (from < 0) throw new InvalidOperationException("规则不存在。");
        var rule = c.Rules[from]; c.Rules.RemoveAt(from); c.Rules.Insert(to, rule);
    });
    public void AddCollection(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 24) throw new InvalidOperationException("分区名称需要 1–24 个字符。");
        if (State.Configuration.Collections.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("已有同名分区。");
        Edit($"创建分区「{name}」", c => c.Collections.Add(new(Guid.NewGuid().ToString("N"), name, "#B5B7E8")));
    }
    public void SetVisibility(string id, bool work, bool presentation) => Edit("更新分区模式", c =>
    {
        var index = c.Collections.FindIndex(x => x.Id == id);
        c.Collections[index] = c.Collections[index] with { InWork = work, InPresentation = presentation };
    });
    public void RenameCollection(string id, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 24) throw new InvalidOperationException("分区名称需要 1–24 个字符。");
        if (State.Configuration.Collections.Any(c => c.Id != id && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("已有同名分区。");
        Edit($"分区重命名为「{name}」", c =>
        {
            var index = c.Collections.FindIndex(x => x.Id == id);
            if (index < 0) throw new InvalidOperationException("分区不存在。");
            c.Collections[index] = c.Collections[index] with { Name = name };
        });
    }
    public void DeleteCollection(string id)
    {
        if (id == "inbox") throw new InvalidOperationException("临时收件箱不能删除。");
        Edit($"删除分区「{CollectionName(id)}」", c =>
        {
            c.Collections.RemoveAll(x => x.Id == id);
            c.Rules.RemoveAll(r => r.CollectionId == id);
            foreach (var path in c.Overrides.Where(p => p.Value == id).Select(p => p.Key).ToList()) c.Overrides[path] = "inbox";
            c.Samples.RemoveAll(s => s.CollectionId == id);
        }, "相关规则一并移除；只删除虚拟分区，文件原样保留。可撤销恢复。");
    }
    public void SetRoots(IEnumerable<string> roots)
    {
        var paths = roots.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0 || paths.Any(p => !Directory.Exists(p))) throw new InvalidOperationException("请至少选择一个可访问的目录。");
        Edit("更新监控目录", c => c.Roots = paths);
    }

    public List<Suggestion> Suggestions() => State.Configuration.Samples.GroupBy(s => (s.Extension, s.CollectionId))
        .Where(g => g.Count() >= 3 && !State.Configuration.DismissedSuggestions.Contains(g.Key.Extension + "|" + g.Key.CollectionId)
            && !State.Configuration.Rules.Any(r => r.Enabled && r.CollectionId == g.Key.CollectionId && r.Conditions.Count == 1
                && r.Conditions[0].Field == "extension" && RuleEngine.SplitExtensions(r.Conditions[0].Value).Contains(g.Key.Extension.TrimStart('.'))))
        .Select(g => new Suggestion(g.Key.Extension, g.Key.CollectionId, g.Count())).ToList();
    public void Accept(Suggestion suggestion) => SaveRule(new(Guid.NewGuid().ToString("N"), $"{suggestion.Extension} → {CollectionName(suggestion.CollectionId)}",
        suggestion.CollectionId, [new("extension", "in", suggestion.Extension)]));
    public void Dismiss(Suggestion suggestion) => Edit("忽略归类建议", c => c.DismissedSuggestions.Add(suggestion.Extension + "|" + suggestion.CollectionId));

    public bool CanUndo => State.History.Any(h => !h.Undone);
    public void Undo()
    {
        var previouslyLinked = State.Configuration.LinkedFiles.ToList();
        var entry = State.History.LastOrDefault(h => !h.Undone);
        if (entry == null) return;
        Transaction(() =>
        {
            if (entry.PreviousConfiguration != null) State.Configuration = StateStore.Clone(entry.PreviousConfiguration);
            else foreach (var change in entry.Changes) State.Configuration.Overrides[change.Path] = change.Before ?? "inbox";
            StateStore.Normalize(State);
            Reclassify();
            entry.Undone = true;
        });
        var removed = previouslyLinked.Except(State.Configuration.LinkedFiles, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Files = Files.Where(f => !removed.Contains(f.Path) || WatchRoots.Contains(System.IO.Path.GetDirectoryName(f.Path)!, StringComparer.OrdinalIgnoreCase)).ToList();
    }
}
