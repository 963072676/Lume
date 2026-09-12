using System.Text.Json;

namespace Lume.Core;

public sealed class StateStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    public AppState Load(IEnumerable<string> defaults)
    {
        if (!File.Exists(Path)) return AppState.Create(defaults);
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(Path), Options) ?? throw new InvalidDataException("配置为空。");
            Validate(state);
            Normalize(state);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NullReferenceException or ArgumentException)
        { throw new InvalidDataException($"配置损坏或版本不兼容，已保留原文件。请检查 {Path} 和 .bak 备份。", ex); }
    }

    public static void Normalize(AppState state)
    {
        state.Configuration.Overrides = new(state.Configuration.Overrides, StringComparer.OrdinalIgnoreCase);
        state.Assignments = new(state.Assignments, StringComparer.OrdinalIgnoreCase);
    }

    private static void Validate(AppState state)
    {
        if (state.Version != 1) throw new InvalidDataException("不支持的配置版本。");
        var c = state.Configuration;
        if (c.Collections.Count == 0 || c.Collections.Count(x => x.Id == "inbox") != 1 || c.Collections.Select(x => x.Id).Distinct().Count() != c.Collections.Count)
            throw new InvalidDataException("分区配置无效。");
        if (c.Rules.Any(r => RuleEngine.Validate(r, c.Collections) != null)) throw new InvalidDataException("规则配置无效。");
        if (c.Roots.Any(r => !System.IO.Path.IsPathFullyQualified(r))) throw new InvalidDataException("目录必须为绝对路径。");
        if (c.LinkedFiles.Any(r => !System.IO.Path.IsPathFullyQualified(r))) throw new InvalidDataException("文件引用必须为绝对路径。");
        if (c.Collections.Any(x => x.MappedPath != null && (!System.IO.Path.IsPathFullyQualified(x.MappedPath) || x.Recent))) throw new InvalidDataException("映射分区配置无效。");
        LayoutBackup.Validate(LayoutBackup.Capture(state.Desktop));
        _ = c.Overrides.Count; _ = c.Samples.Count; _ = c.DismissedSuggestions.Count; _ = state.Assignments.Count; _ = state.History.Count;
    }

    public void Save(AppState state)
    {
        Validate(state);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, Options);
                stream.Flush(true);
            }
            if (File.Exists(Path)) File.Replace(temp, Path, Path + ".bak");
            else File.Move(temp, Path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
