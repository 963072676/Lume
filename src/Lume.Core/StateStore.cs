using System.Security.Cryptography;
using System.Text.Json;

namespace Lume.Core;

public sealed record HistoryChunk(string File, int Count, int Undoable);

public sealed class StateStore(string path)
{
    public const int ActiveHistoryLimit = 200;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    private string BasePath => Path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? Path[..^4] : Path;
    private string HistoryDirectory => BasePath + ".history";
    private sealed record DesktopSettings(string Revision, DesktopPreferences Desktop);
    private sealed record PreservedReferences(List<string> History, List<string> Settings);
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    // History entries are immutable once appended. Undo replaces an entry rather than editing it.
    internal static AppState RollbackCopy(AppState state) => new()
    {
        Version = state.Version, Configuration = Clone(state.Configuration), Assignments = new(state.Assignments, StringComparer.OrdinalIgnoreCase),
        Desktop = Clone(state.Desktop), History = [.. state.History], HistoryArchives = [.. state.HistoryArchives],
        HistoryFile = state.HistoryFile, StorageRevision = state.StorageRevision
    };

    public AppState Load(IEnumerable<string> defaults)
    {
        if (!File.Exists(Path)) return AppState.Create(defaults);
        try
        {
            return LoadCore(Path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NullReferenceException or ArgumentException)
        { throw new InvalidDataException($"配置损坏或版本不兼容，已保留原文件。请检查 {Path} 和 .bak 备份。", ex); }
    }

    private AppState LoadCore(string source, bool settingsBackup = false)
    {
            var state = ReadEnvelope(source);
            Validate(state);
            if (state.HistoryFile != null) state.History = ReadHistory(state.HistoryFile);
            if (state.StorageRevision != null)
            {
                var settings = DesktopPath(state.StorageRevision) + (settingsBackup ? ".bak" : "");
                if (settingsBackup && !File.Exists(settings)) throw new InvalidDataException("外观备份不存在。");
                if (File.Exists(settings))
                {
                    var value = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(settings), Options) ?? throw new InvalidDataException("外观设置为空。");
                    if (value.Revision != state.StorageRevision) throw new InvalidDataException("外观设置与配置版本不一致。");
                    ValidateDesktop(value.Desktop); state.Desktop = value.Desktop;
                }
            }
            ValidateHistory(state.History); Normalize(state); return state;
    }

    private static AppState ReadEnvelope(string path) => JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("配置为空。");
    public static void Normalize(AppState state)
    {
        state.Desktop.Theme = ThemeIds.Normalize(state.Desktop.Theme);
        state.Configuration.Overrides = new(state.Configuration.Overrides, StringComparer.OrdinalIgnoreCase);
        state.Assignments = new(state.Assignments, StringComparer.OrdinalIgnoreCase);
    }
    private static void ValidateDesktop(DesktopPreferences value) => LayoutBackup.Validate(new(1, value.Positions, value.Cards, value.GlassOpacity, value.SnapEnabled, value.ShowSystemEntries));
    private static void ValidateHistory(List<HistoryEntry> history)
    {
        if (history == null || history.Any(e => e == null || e.Changes == null || string.IsNullOrEmpty(e.Id))) throw new InvalidDataException("历史记录无效。");
    }
    private static void Validate(AppState state)
    {
        if (state.Version is not (1 or 2)) throw new InvalidDataException("不支持的配置版本。");
        var c = state.Configuration;
        if (c.Collections.Count == 0 || c.Collections.Count(x => x.Id == "inbox") != 1 || c.Collections.Select(x => x.Id).Distinct().Count() != c.Collections.Count)
            throw new InvalidDataException("分区配置无效。");
        if (c.Rules.Any(r => RuleEngine.Validate(r, c.Collections) != null)) throw new InvalidDataException("规则配置无效。");
        if (c.Roots.Any(r => !System.IO.Path.IsPathFullyQualified(r))) throw new InvalidDataException("目录必须为绝对路径。");
        if (c.LinkedFiles.Any(r => !System.IO.Path.IsPathFullyQualified(r))) throw new InvalidDataException("文件引用必须为绝对路径。");
        if (c.Collections.Any(x => x.MappedPath != null && (!System.IO.Path.IsPathFullyQualified(x.MappedPath) || x.Recent))) throw new InvalidDataException("映射分区配置无效。");
        ValidateDesktop(state.Desktop); ValidateHistory(state.History);
        if (state.StorageRevision != null && !Guid.TryParseExact(state.StorageRevision, "N", out _)) throw new InvalidDataException("存储版本标识无效。");
        if (state.HistoryFile != null) ValidateHistoryName(state.HistoryFile);
        foreach (var archive in state.HistoryArchives)
        {
            ValidateHistoryName(archive.File);
            if (archive.Count < 1 || archive.Undoable < 0 || archive.Undoable > archive.Count) throw new InvalidDataException("历史分段索引无效。");
        }
        _ = c.Overrides.Count; _ = c.Samples.Count; _ = c.DismissedSuggestions.Count; _ = state.Assignments.Count;
    }
    private string DesktopPath(string revision) => BasePath + ".desktop." + revision + ".json";
    private static void ValidateHistoryName(string name)
    {
        if (name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal) || !name[..64].All(Uri.IsHexDigit)) throw new InvalidDataException("历史文件标识无效。");
    }
    public List<HistoryEntry> ReadHistory(string name)
    {
        ValidateHistoryName(name);
        var bytes = File.ReadAllBytes(System.IO.Path.Combine(HistoryDirectory, name));
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(name[..64], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("历史文件校验失败。");
        var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(bytes, Options) ?? throw new InvalidDataException("历史文件为空。");
        ValidateHistory(entries); return entries;
    }
    internal HistoryChunk WriteHistory(List<HistoryEntry> entries)
    {
        ValidateHistory(entries); var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, Options);
        var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".json";
        Directory.CreateDirectory(HistoryDirectory); var target = System.IO.Path.Combine(HistoryDirectory, name);
        if (!File.Exists(target)) AtomicWrite(target, stream => stream.Write(bytes), false);
        else if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("现有历史文件校验失败。");
        return new(name, entries.Count, entries.Count(e => !e.Undone));
    }

    public void SaveDesktop(AppState state)
    {
        ValidateDesktop(state.Desktop);
        if (state.StorageRevision == null || !File.Exists(Path)) { Save(state); return; }
        var value = new DesktopSettings(state.StorageRevision, state.Desktop);
        AtomicWrite(DesktopPath(state.StorageRevision), stream => JsonSerializer.Serialize(stream, value, Options));
    }
    public void Save(AppState state)
    {
        Validate(state);
        var archives = state.HistoryArchives.ToList();
        var excess = Math.Max(0, state.History.Count - ActiveHistoryLimit);
        var older = state.History.Take(excess).ToList();
        if (older.Count > 0 && archives.Count > 0 && archives[^1].Count < ActiveHistoryLimit)
        {
            var tail = ReadHistory(archives[^1].File); var take = Math.Min(ActiveHistoryLimit - tail.Count, older.Count);
            tail.AddRange(older.Take(take)); older.RemoveRange(0, take); archives[^1] = WriteHistory(tail);
        }
        foreach (var chunk in older.Chunk(ActiveHistoryLimit)) archives.Add(WriteHistory(chunk.ToList()));
        var recent = state.History.Skip(excess).ToList(); var active = WriteHistory(recent);
        var revision = Guid.NewGuid().ToString("N");
        var envelope = new AppState
        {
            Version = 2, Configuration = state.Configuration, Assignments = state.Assignments, Desktop = state.Desktop,
            History = [], HistoryFile = active.File, HistoryArchives = archives, StorageRevision = revision
        };
        // Retain a standalone original before the first split-format migration.
        if (File.Exists(Path) && state.StorageRevision == null && !File.Exists(BasePath + ".legacy.bak")) File.Copy(Path, BasePath + ".legacy.bak", false);
        AtomicWrite(Path, stream => JsonSerializer.Serialize(stream, envelope, Options));
        state.Version = 2; state.History = recent; state.HistoryArchives = archives; state.HistoryFile = active.File; state.StorageRevision = revision;
        PruneUnreferencedFiles();
    }

    private static void AtomicWrite(string path, Action<FileStream> write, bool backup = true)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { write(stream); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, backup ? path + ".bak" : null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void PruneUnreferencedFiles()
    {
        // A damaged backup prevents collection. Explicit recovery records the files that existed
        // at that moment so a preserved damaged envelope does not pin every future history rewrite.
        try
        {
            var folder = System.IO.Path.GetDirectoryName(BasePath)!; var name = System.IO.Path.GetFileName(BasePath);
            var protectedPaths = new[] { BasePath, BasePath + ".bak" }
                .Concat(Directory.EnumerateFiles(folder, name + ".preserved-*.json")).Where(File.Exists);
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var revisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in protectedPaths)
            {
                if (File.Exists(path + ".references"))
                {
                    var saved = JsonSerializer.Deserialize<PreservedReferences>(File.ReadAllText(path + ".references")) ?? throw new InvalidDataException("恢复保留清单无效。");
                    foreach (var history in saved.History) { ValidateHistoryName(history); referenced.Add(history); }
                    foreach (var settings in saved.Settings)
                    {
                        if (System.IO.Path.GetFileName(settings) != settings || !settings.StartsWith(name + ".desktop.", StringComparison.Ordinal)) throw new InvalidDataException("恢复设置清单无效。");
                        var full = System.IO.Path.Combine(folder, settings);
                        revisions.Add(full.EndsWith(".bak", StringComparison.Ordinal) ? full[..^4] : full);
                    }
                    continue;
                }
                var state = ReadEnvelope(path);
                if (state.HistoryFile != null) referenced.Add(state.HistoryFile);
                foreach (var archive in state.HistoryArchives) referenced.Add(archive.File);
                if (state.StorageRevision != null) revisions.Add(DesktopPath(state.StorageRevision));
            }
            foreach (var file in Directory.EnumerateFiles(HistoryDirectory, "*.json"))
            {
                var filename = System.IO.Path.GetFileName(file); ValidateHistoryName(filename);
                if (!referenced.Contains(filename)) File.Delete(file);
            }
            foreach (var file in Directory.EnumerateFiles(folder, name + ".desktop.*.json*"))
                if (!revisions.Contains(file.EndsWith(".bak", StringComparison.Ordinal) ? file[..^4] : file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException) { }
    }

    public void ExportHistory(AppState state, string destination)
    {
        var full = System.IO.Path.GetFullPath(destination);
        if (full.StartsWith(BasePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择配置文件之外的导出位置。");
        AtomicWrite(full, stream =>
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }); writer.WriteStartArray();
            foreach (var archive in state.HistoryArchives)
                foreach (var entry in ReadHistory(archive.File)) JsonSerializer.Serialize(writer, entry, Options);
            foreach (var entry in state.History) JsonSerializer.Serialize(writer, entry, Options);
            writer.WriteEndArray(); writer.Flush();
        });
    }

    private (AppState State, string Source) ReadBackup()
    {
        foreach (var (source, settingsBackup) in new[] { (Path, true), (Path + ".bak", false) })
        {
            try
            {
                var state = LoadCore(source, settingsBackup);
                if (settingsBackup && state.StorageRevision == null) continue;
                foreach (var archive in state.HistoryArchives)
                {
                    var entries = ReadHistory(archive.File);
                    if (entries.Count != archive.Count || entries.Count(e => !e.Undone) != archive.Undoable) throw new InvalidDataException("历史备份索引不一致。");
                }
                return (state, settingsBackup ? DesktopPath(state.StorageRevision!) + ".bak" : source);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException) { }
        }
        throw new InvalidDataException("没有通过完整校验的备份。原配置和历史文件均已保留，请从数据目录手动恢复。");
    }
    public BackupInfo InspectBackup()
    {
        var (state, source) = ReadBackup();
        return new(File.GetLastWriteTimeUtc(source), state.Configuration.Collections.Count, state.History.Count + state.HistoryArchives.Sum(a => a.Count));
    }
    public AppState RestoreBackup()
    {
        var (state, _) = ReadBackup();
        var folder = System.IO.Path.GetDirectoryName(BasePath)!;
        var references = new PreservedReferences(
            Directory.Exists(HistoryDirectory) ? Directory.EnumerateFiles(HistoryDirectory, "*.json").Select(p => System.IO.Path.GetFileName(p)).ToList() : [],
            Directory.EnumerateFiles(folder, System.IO.Path.GetFileName(BasePath) + ".desktop.*.json*").Select(p => System.IO.Path.GetFileName(p)).ToList());
        foreach (var original in new[] { Path, Path + ".bak" }.Where(File.Exists))
        {
            var preserved = BasePath + ".preserved-" + Guid.NewGuid().ToString("N") + ".json";
            File.Copy(original, preserved, false);
            AtomicWrite(preserved + ".references", stream => JsonSerializer.Serialize(stream, references), false);
        }
        Save(state); return Load([]);
    }
}

public sealed record BackupInfo(DateTime SavedUtc, int Collections, int HistoryEntries);
