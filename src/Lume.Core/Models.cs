namespace Lume.Core;

public sealed record DesktopFile(string Path, string Name, string Extension, long Size,
    DateTime CreatedUtc, DateTime ModifiedUtc, bool IsDirectory, string Source, ShortcutTarget? Target = null);

public sealed record ShortcutTarget(string Path, string Name, string Extension, string Kind,
    long? Size = null, string Description = "", string Product = "", string Company = "", string Arguments = "", string WorkingDirectory = "");

public sealed record Collection(string Id, string Name, string Color, bool InWork = true, bool InPresentation = false, string? MappedPath = null, bool Recent = false);
public sealed record CardOptions(bool Locked = false, bool Collapsed = false, string Sort = "name", bool Descending = false, int IconSize = 34, List<string>? Order = null);
public sealed record Condition(string Field, string Operator, string Value);
public sealed record Rule(string Id, string Name, string CollectionId, List<Condition> Conditions, bool Enabled = true);
public sealed record LearningSample(string Path, string Extension, string CollectionId);
public sealed record Suggestion(string Extension, string CollectionId, int Count);
public sealed record AssignmentChange(string Path, string? Before, string After);
public sealed record CardPlacement(int X, int Y, int Width = 330, int Height = 340);
public sealed class DesktopPreferences
{
    public bool ShowSystemEntries { get; set; } = true;
    public Dictionary<string, CardOptions> Cards { get; set; } = [];
    public bool SnapEnabled { get; set; } = true;
    public Dictionary<string, CardPlacement> Positions { get; set; } = [];
    public int Mode { get; set; }
    public byte GlassOpacity { get; set; } = 190;
    public bool ContextMenuEnabled { get; set; } = true;
}

public sealed class Configuration
{
    public List<string> LinkedFiles { get; set; } = [];
    public List<string> Roots { get; set; } = [];
    public List<Collection> Collections { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LearningSample> Samples { get; set; } = [];
    public List<string> DismissedSuggestions { get; set; } = [];
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public Configuration? PreviousConfiguration { get; set; }
    public List<AssignmentChange> Changes { get; set; } = [];
    public bool Undone { get; set; }
}

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public Configuration Configuration { get; set; } = new();
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<HistoryEntry> History { get; set; } = [];
    public DesktopPreferences Desktop { get; set; } = new();

    public static AppState Create(IEnumerable<string> roots) => new()
    {
        Configuration = new()
        {
            Roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Collections = [new("inbox", "临时收件箱", "#E8B86D"), new("work", "工作资料", "#9BAAF5"),
                new("screenshots", "截图与图片", "#92C7B5", false), new("archives", "压缩包", "#D3ADD8", false),
                new("apps", "应用与文件夹", "#8DC1DD", true, true)],
            Rules = [new("images", "图片自动归类", "screenshots", [new("extension", "in", "png,jpg,jpeg,gif,webp,bmp,heic")]),
                new("documents", "文档自动归类", "work", [new("extension", "in", "pdf,doc,docx,xls,xlsx,ppt,pptx,md,txt,csv")]),
                new("archives", "压缩包自动归类", "archives", [new("extension", "in", "zip,rar,7z,tar,gz")]),
                new("shortcuts", "快捷方式自动归类", "apps", [new("extension", "in", "lnk,url")]),
                new("folders", "文件夹自动归类", "apps", [new("kind", "is", "folder")])]
        }
    };
}
