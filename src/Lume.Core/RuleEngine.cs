using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Lume.Core;

public static class RuleEngine
{
    public static string? Validate(Rule rule, IEnumerable<Collection> collections)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return "请输入规则名称。";
        if (!collections.Any(c => c.Id == rule.CollectionId && c.MappedPath == null && !c.Recent)) return "请选择普通分区作为规则目标。";
        if (rule.Conditions.Count is < 1 or > 12) return "每条规则需要 1–12 个条件。";
        foreach (var c in rule.Conditions)
        {
            if (string.IsNullOrWhiteSpace(c.Value) || c.Value.Length > 512) return "条件值不能为空，且最多 512 个字符。";
            if (c.Field is "extension" or "targetExtension" && c.Operator == "in")
            {
                if (SplitExtensions(c.Value).Count == 0) return "请输入扩展名，如 png,jpg。";
            }
            else if (IsTextField(c.Field) && c.Operator is "contains" or "starts" or "regex" or "literal")
            {
                if (c.Operator == "regex")
                    try { _ = new Regex(c.Value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
                    catch (ArgumentException) { return "正则表达式格式不正确。"; }
                if (c.Operator is "contains" or "starts" && SplitKeywords(c.Value).Length == 0) return "请输入至少一个关键词。";
            }
            else if (c.Field is "createdDays" or "modifiedDays" or "sizeMb" or "targetSizeMb" && c.Operator is "lt" or "gt")
            {
                if (!double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0)
                    return "天数或大小必须是非负数字。";
            }
            else if (c.Field == "source" && c.Operator == "is") { }
            else if (c.Field == "kind" && c.Operator == "is" && c.Value is "file" or "folder") { }
            else if (c.Field == "targetKind" && c.Operator == "is" && c.Value is "file" or "folder" or "url" or "unknown") { }
            else return "不支持这个条件组合。";
        }
        return null;
    }

    public static bool IsTextField(string field) => field is "name" or "path" or "targetName" or "targetPath" or "targetDescription" or "targetProduct" or "targetCompany";

    public static string[] SplitKeywords(string value) => value.Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static HashSet<string> SplitExtensions(string value) => value.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim().TrimStart('.').ToLowerInvariant()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly ConditionalWeakTable<Rule, PreparedRule> Prepared = new();
    private sealed class PreparedRule(Rule rule)
    {
        private PreparedCondition[] conditions = rule.Conditions.Select(c => new PreparedCondition(c)).ToArray();
        public bool Matches(DesktopFile file, DateTime now, Action? check = null)
        {
            // Rule lists can be edited in place by callers; never reuse stale conditions.
            var changed = conditions.Length != rule.Conditions.Count;
            for (var i = 0; !changed && i < conditions.Length; i++) changed = conditions[i].Source != rule.Conditions[i];
            if (changed)
                conditions = rule.Conditions.Select(c => new PreparedCondition(c)).ToArray();
            if (conditions.Length == 0) return false;
            foreach (var condition in conditions)
            {
                check?.Invoke();
                var matched = condition.Match(file, now);
                check?.Invoke();
                if (!matched) return false;
            }
            return true;
        }
    }
    private sealed class PreparedCondition
    {
        public Condition Source { get; }
        private readonly HashSet<string>? extensions;
        private readonly string[] keywords = [];
        private readonly Regex? regex;
        private readonly double threshold = double.NaN;
        public PreparedCondition(Condition source)
        {
            Source = source;
            if (source.Field is "extension" or "targetExtension") extensions = SplitExtensions(source.Value);
            else if (IsTextField(source.Field))
            {
                if (source.Operator is "contains" or "starts") keywords = SplitKeywords(source.Value);
                if (source.Operator == "regex")
                {
                    try
                    {
                        try { regex = new(source.Value, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)); }
                        catch (NotSupportedException) { regex = new(source.Value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
                    }
                    catch (ArgumentException) { }
                }
            }
            else if (double.TryParse(source.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) threshold = number;
        }
        public bool Match(DesktopFile f, DateTime now)
        {
            var c = Source;
            if (c.Field == "extension") return !f.IsDirectory && extensions!.GetAlternateLookup<ReadOnlySpan<char>>().Contains(f.Extension.AsSpan().TrimStart('.'));
            if (c.Field == "targetExtension") return f.Target is { Extension.Length: > 0 } t && t.Kind != "folder" && extensions!.GetAlternateLookup<ReadOnlySpan<char>>().Contains(t.Extension.AsSpan().TrimStart('.'));
            if (c.Field == "targetKind") return f.Target != null && c.Value == f.Target.Kind;
            if (c.Field == "source") return f.Source.Equals(c.Value, StringComparison.OrdinalIgnoreCase);
            if (c.Field == "kind") return c.Value == (f.IsDirectory ? "folder" : "file");
            if (IsTextField(c.Field))
            {
                var text = c.Field switch
                {
                    "name" => f.Name, "path" => f.Path,
                    "targetName" => f.Target?.Name, "targetPath" => f.Target?.Path,
                    "targetDescription" => f.Target?.Description, "targetProduct" => f.Target?.Product,
                    "targetCompany" => f.Target?.Company, _ => null
                };
                if (string.IsNullOrEmpty(text)) return false;
                try
                {
                    return c.Operator switch
                    {
                        "contains" => KeywordsMatch(text, false),
                        "starts" => KeywordsMatch(text, true),
                        "literal" => text.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
                        "regex" => regex?.IsMatch(text) == true,
                        _ => false
                    };
                }
                catch (RegexMatchTimeoutException) { return false; }
                catch (ArgumentException) { return false; }
            }
            if (double.IsNaN(threshold)) return false;
            var number = c.Field switch
            {
                "createdDays" => Math.Max(0, (now - f.CreatedUtc).TotalDays),
                "modifiedDays" => Math.Max(0, (now - f.ModifiedUtc).TotalDays),
                "sizeMb" => f.Size / 1048576.0,
                "targetSizeMb" => f.Target?.Size / 1048576.0 ?? double.NaN,
                _ => double.NaN
            };
            return c.Operator == "lt" ? number < threshold : c.Operator == "gt" && number > threshold;
        }
        private bool KeywordsMatch(string text, bool starts)
        {
            foreach (var keyword in keywords)
                if (starts ? text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) : text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public static bool Matches(DesktopFile file, Rule rule, DateTime nowUtc) => rule.Enabled && Prepared.GetValue(rule, static r => new(r)).Matches(file, nowUtc);

    private static bool HasCollection(Configuration config, string id)
    {
        foreach (var collection in config.Collections) if (collection.Id == id) return true;
        return false;
    }

    public static string Classify(DesktopFile file, Configuration config, DateTime now)
    {
        if (config.Overrides.TryGetValue(file.Path, out var pinned) && HasCollection(config, pinned)) return pinned;
        foreach (var rule in config.Rules) if (HasCollection(config, rule.CollectionId) && Matches(file, rule, now)) return rule.CollectionId;
        return "inbox";
    }

    public static Configuration ClassificationSnapshot(Configuration config) => new()
    {
        Collections = [.. config.Collections],
        Rules = config.Rules.Select(r => r with { Conditions = [.. r.Conditions] }).ToList(),
        Overrides = new(config.Overrides, StringComparer.OrdinalIgnoreCase)
    };

    private sealed class WorkLimit(CancellationToken cancellation, TimeSpan? budget)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TimeSpan maximum = budget ?? TimeSpan.FromSeconds(2);
        public void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (clock.Elapsed > maximum) throw new TimeoutException("规则计算超过时间限制，未应用归类。请简化复杂正则或减少规则后重试。");
        }
    }

    public static string[] ClassifyFiles(IReadOnlyList<DesktopFile> files, Configuration snapshot, DateTime now,
        CancellationToken cancellation = default, TimeSpan? budget = null)
    {
        var limit = new WorkLimit(cancellation, budget); Action check = limit.Check; check();
        var targets = snapshot.Collections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var rules = snapshot.Rules.Where(r => r.Enabled && targets.Contains(r.CollectionId))
            .Select(r => { check(); return (r.CollectionId, Prepared: new PreparedRule(r)); }).ToArray();
        var results = new string[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            check();
            var file = files[i];
            if (snapshot.Overrides.TryGetValue(file.Path, out var pinned) && targets.Contains(pinned)) { results[i] = pinned; continue; }
            results[i] = "inbox";
            foreach (var rule in rules) if (rule.Prepared.Matches(file, now, check)) { results[i] = rule.CollectionId; break; }
        }
        check();
        return results;
    }

    public sealed record PreviewItem(DesktopFile File, string CollectionId, string Reason, bool Applied);

    public static List<PreviewItem> Preview(IEnumerable<DesktopFile> files, Configuration config, Rule draft, DateTime now,
        CancellationToken cancellation = default, TimeSpan? budget = null)
    {
        var limit = new WorkLimit(cancellation, budget); Action check = limit.Check; check();
        if (!draft.Enabled) return [];
        var rules = config.Rules.ToList();
        var index = rules.FindIndex(r => r.Id == draft.Id);
        if (index < 0) rules.Insert(0, draft); else rules[index] = draft;
        var targets = config.Collections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var prepared = rules.Where(r => r.Enabled && targets.Contains(r.CollectionId))
            .Select(r => { check(); return (Rule: r, Prepared: new PreparedRule(r)); }).ToArray();
        var previewRule = new PreparedRule(draft);
        var results = new List<PreviewItem>();
        foreach (var file in files)
        {
            check();
            if (!previewRule.Matches(file, now, check)) continue;
            if (config.Overrides.TryGetValue(file.Path, out var pinned) && targets.Contains(pinned))
                results.Add(new(file, pinned, "手动固定", false));
            else
            {
                var winner = prepared.First(r => r.Prepared.Matches(file, now, check)).Rule;
                results.Add(new(file, winner.CollectionId, winner.Id == draft.Id ? "本规则生效" : $"优先规则：{winner.Name}", winner.Id == draft.Id));
            }
        }
        check(); return results;
    }

    public static string[] SearchTerms(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    public static bool Search(DesktopFile file, string query) => query.Length == 0 || Search(file, SearchTerms(query));
    public static bool Search(DesktopFile file, IReadOnlyList<string> terms)
    {
        for (var i = 0; i < terms.Count; i++)
            if (!file.Name.Contains(terms[i], StringComparison.OrdinalIgnoreCase) && !file.Source.Contains(terms[i], StringComparison.OrdinalIgnoreCase)
                && !(terms[i] == "文件夹" && file.IsDirectory)) return false;
        return true;
    }
}
