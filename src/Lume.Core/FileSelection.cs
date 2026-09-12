namespace Lume.Core;

/// <summary>选择只存在于一个文件视图中，按显示顺序计算范围，筛选后自动去掉不可见项。</summary>
public sealed class FileSelection
{
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private List<string> visible = [];
    private string? anchor;
    public IReadOnlyList<string> Visible => visible;
    public IReadOnlySet<string> Selected => selected;

    public void SetVisible(IEnumerable<string> paths)
    {
        visible = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        selected.IntersectWith(visible);
        if (anchor != null && !visible.Contains(anchor, StringComparer.OrdinalIgnoreCase)) anchor = null;
    }
    public void Clear() { selected.Clear(); anchor = null; }
    public void SelectAll() { selected.Clear(); selected.UnionWith(visible); anchor = visible.FirstOrDefault(); }
    public void Select(string path, bool control = false, bool shift = false)
    {
        var to = visible.FindIndex(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (to < 0) return;
        var from = anchor == null ? -1 : visible.FindIndex(p => p.Equals(anchor, StringComparison.OrdinalIgnoreCase));
        if (shift && from >= 0)
        {
            if (!control) selected.Clear();
            selected.UnionWith(visible.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1));
        }
        else
        {
            if (!control) selected.Clear();
            if (!selected.Add(path) && control) selected.Remove(path);
            anchor = path;
        }
    }
    public string[] PathsFor(string path) => selected.Contains(path) ? visible.Where(selected.Contains).ToArray() : [path];
}
