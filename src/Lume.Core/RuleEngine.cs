using System.Globalization;
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

    public static bool Matches(DesktopFile file, Rule rule, DateTime nowUtc) => rule.Enabled && rule.Conditions.Count > 0 && rule.Conditions.All(c => Match(file, c, nowUtc));

    private static bool Match(DesktopFile f, Condition c, DateTime now)
    {
        if (c.Field == "extension") return !f.IsDirectory && SplitExtensions(c.Value).Contains(f.Extension.TrimStart('.'));
        if (c.Field == "targetExtension") return f.Target is { Extension.Length: > 0 } t && t.Kind != "folder" && SplitExtensions(c.Value).Contains(t.Extension.TrimStart('.'));
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
                    "contains" => SplitKeywords(c.Value).Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase)),
                    "starts" => SplitKeywords(c.Value).Any(v => text.StartsWith(v, StringComparison.OrdinalIgnoreCase)),
                    "literal" => text.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
                    "regex" => Regex.IsMatch(text, c.Value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)),
                    _ => false
                };
            }
            catch (RegexMatchTimeoutException) { return false; }
            catch (ArgumentException) { return false; }
        }
        if (!double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)) return false;
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

    public static string Classify(DesktopFile file, Configuration config, DateTime now)
    {
        if (config.Overrides.TryGetValue(file.Path, out var pinned) && config.Collections.Any(c => c.Id == pinned)) return pinned;
        return config.Rules.FirstOrDefault(r => config.Collections.Any(c => c.Id == r.CollectionId) && Matches(file, r, now))?.CollectionId ?? "inbox";
    }

    public sealed record PreviewItem(DesktopFile File, string CollectionId, string Reason, bool Applied);

    public static List<PreviewItem> Preview(IEnumerable<DesktopFile> files, Configuration config, Rule draft, DateTime now)
    {
        var rules = config.Rules.ToList();
        var index = rules.FindIndex(r => r.Id == draft.Id);
        if (index < 0) rules.Insert(0, draft); else rules[index] = draft;
        return files.Where(f => Matches(f, draft, now)).Select(f =>
        {
            if (config.Overrides.TryGetValue(f.Path, out var pinned) && config.Collections.Any(c => c.Id == pinned))
                return new PreviewItem(f, pinned, "手动固定", false);
            var winner = rules.First(r => config.Collections.Any(c => c.Id == r.CollectionId) && Matches(f, r, now));
            return new PreviewItem(f, winner.CollectionId, winner.Id == draft.Id ? "本规则生效" : $"优先规则：{winner.Name}", winner.Id == draft.Id);
        }).ToList();
    }

    public static bool Search(DesktopFile file, string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .All(term => file.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || file.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (term == "文件夹" && file.IsDirectory));
}
