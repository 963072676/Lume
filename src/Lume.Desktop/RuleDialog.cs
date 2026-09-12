using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Net.Http;
using Lume.Core;
using Condition = Lume.Core.Condition;

namespace Lume.Desktop;

internal sealed class RuleDialog : Window
{
    private sealed record FieldOption(string Id, string Label) { public override string ToString() => Label; }
    private static readonly FieldOption[] Fields = [new("extension", "扩展名"), new("name", "文件名"), new("createdDays", "创建距今天数"),
        new("modifiedDays", "修改距今天数"), new("sizeMb", "文件大小 MB"), new("source", "来源（推测）"), new("kind", "文件 / 文件夹"), new("path", "文件自身完整路径"),
        new("targetName", "快捷目标：文件名"), new("targetPath", "快捷目标：完整路径"), new("targetExtension", "快捷目标：扩展名"),
        new("targetKind", "快捷目标：类型"), new("targetSizeMb", "快捷目标：大小 MB"), new("targetDescription", "快捷目标：文件描述"),
        new("targetProduct", "快捷目标：产品名称"), new("targetCompany", "快捷目标：公司名称")];
    private readonly StackPanel rows = new();
    private readonly TextBox name = new();
    private readonly ComboBox target = new();
    private readonly CheckBox enabled = new() { Content = "启用此规则", IsChecked = true };
    private readonly TextBlock preview = Ui.Text("", Tokens.Secondary, Ui.Muted);
    private readonly List<(ComboBox Field, ComboBox Operator, Func<string> Value, UIElement Row)> conditions = [];
    private readonly Organizer organizer;
    private readonly string id;
    private readonly string? aiDirectory;
    private readonly TextBox aiPrompt = new() { MaxLength = 1000, ToolTip = "描述想归类的文件，例如：常见多媒体音视频文件" };
    private CancellationTokenSource? aiCancellation;
    private readonly StackPanel editor;
    private readonly StackPanel footerPanel;
    private readonly HttpMessageHandler? aiHandler;
    public Rule? Result { get; private set; }

    public RuleDialog(Window owner, Organizer organizer, Rule? rule = null, string? aiDirectory = null, HttpMessageHandler? aiHandler = null)
    {
        Owner = owner; this.organizer = organizer; id = rule?.Id ?? Guid.NewGuid().ToString("N");
        this.aiDirectory = aiDirectory; this.aiHandler = aiHandler;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = rule == null ? "创建智能规则" : "编辑智能规则";
        Width = 820; Height = 720; MinWidth = 800; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new(28) };
        var footer = new StackPanel();
        footerPanel = footer;
        DockPanel.SetDock(footer, Dock.Bottom);
        preview.Margin = new(0, 14, 0, 14); footer.Children.Add(new ScrollViewer { Content = preview, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        footer.Children.Add(Ui.Row(Ui.Button("预览匹配", UpdatePreview), Ui.Button("保存规则", Save, true), Ui.Button("取消", () => DialogResult = false)));
        panel.Children.Add(footer);
        var body = new StackPanel();
        editor = body;
        body.Children.Add(Ui.Text("让文件自己找到位置", Tokens.DialogTitle, bold: true));
        var subtitle = Ui.Text("全部条件满足时归入目标分区。列表靠上的规则先匹配。", Tokens.Secondary, Ui.Muted); subtitle.Margin = new(0, 8, 0, 20); body.Children.Add(subtitle);
        body.Children.Add(Ui.Text("规则名称")); name.Text = rule?.Name ?? ""; name.Margin = new(0, 6, 0, 18); body.Children.Add(name);
        body.Children.Add(Ui.Text("AI 生成条件（描述用途，可留空使用规则名称）", 12));
        aiPrompt.Margin = new(0, 6, 0, 8); body.Children.Add(aiPrompt);
        body.Children.Add(Ui.Row(Ui.Button("AI 分析并填入条件", () => _ = GenerateAsync(), true),
            Ui.Button("填入常见多媒体后缀", () => ReplaceConditions([new("extension", "in", "mp3,wav,flac,aac,m4a,ogg,opus,wma,aiff,ape,mp4,mkv,avi,mov,wmv,webm,flv,m4v,mpeg,mpg,ts,m2ts,3gp")]))));
        body.Children.Add(Ui.Text("AI 使用已保存的接口与模型，仅发送规则名称、条件和用途；生成后仍需预览并保存。多媒体后缀快捷填充为本地模板，无需联网。", 11, Ui.Muted));
        body.Children.Add(Ui.Text("当文件满足以下所有条件", 14, bold: true));
        rows.Margin = new(0, 12, 0, 12); body.Children.Add(rows);
        body.Children.Add(Ui.Button("＋ 添加条件", () => { if (conditions.Count < 12) AddCondition(new("name", "contains", "")); }));
        var destination = Ui.Row(Ui.Text("那么，放入分区  "));
        target.ItemsSource = organizer.State.Configuration.Collections.Where(c => c.MappedPath == null && !c.Recent).ToList(); target.DisplayMemberPath = "Name"; target.SelectedValuePath = "Id";
        target.SelectedValue = rule?.CollectionId ?? "work"; target.Width = 210; destination.Children.Add(target); destination.Margin = new(0, 24, 0, 6); body.Children.Add(destination);
        enabled.IsChecked = rule?.Enabled ?? true; body.Children.Add(enabled);
        body.Children.Add(Ui.Text("包含 / 开头是：逗号或分号分隔多个关键词，命中任意一个即可，如 音乐,视频,剧,音。条件行之间仍须全部满足。\n快捷目标字段读取 .lnk / .url 指向的位置，例如目标路径包含 Game；网址支持 steam:// 等协议。目标不可访问时大小和应用信息不参与匹配。\n要匹配逗号本身，选择「包含原文」或正则。来源仅依据文件名和路径推测。", 11, Ui.Muted));
        panel.Children.Add(new ScrollViewer { Content = body }); Content = panel;
        foreach (var condition in rule?.Conditions ?? [new("extension", "in", "png,jpg")]) AddCondition(condition);
        Closed += (_, _) => aiCancellation?.Cancel();
    }

    private void ReplaceConditions(List<Condition> values)
    {
        rows.Children.Clear(); conditions.Clear(); foreach (var value in values) AddCondition(value); UpdatePreview();
    }
    private async Task GenerateAsync()
    {
        if (aiCancellation != null) return;
        if (aiDirectory == null) { preview.Text = "请从设置中心打开规则编辑器。"; return; }
        aiCancellation = new(); editor.IsEnabled = false; footerPanel.IsEnabled = false;
        preview.Text = "AI 正在生成规则条件…关闭窗口可取消。";
        try
        {
            var store = new AiSettingsStore(aiDirectory); var config = store.Load();
            var request = string.IsNullOrWhiteSpace(aiPrompt.Text) ? name.Text.Trim() : aiPrompt.Text.Trim();
            var draft = Draft(); if (string.IsNullOrWhiteSpace(draft.Name)) draft = draft with { Name = request };
            using var http = new HttpClient(aiHandler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var generated = await new AiAnalysisClient(http).GenerateRuleAsync(new(config.BaseUrl, store.ReadKey(config), config.Model), draft,
                organizer.State.Configuration.Collections, request, aiCancellation.Token);
            if (aiCancellation.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(name.Text)) name.Text = generated.Name;
            ReplaceConditions(generated.Conditions); preview.Text = "AI 条件已填入，尚未保存。\n" + preview.Text;
        }
        catch (OperationCanceledException) { preview.Text = "请求已取消或超时，原条件未改变。"; }
        catch (Exception ex) { preview.Text = ex is InvalidOperationException or InvalidDataException ? ex.Message : "AI 请求失败，请检查设置中心中的接口与密钥。原条件未改变。"; }
        finally { aiCancellation.Dispose(); aiCancellation = null; editor.IsEnabled = true; footerPanel.IsEnabled = true; }
    }

    private void AddCondition(Condition condition)
    {
        var field = new ComboBox { ItemsSource = Fields, Width = 185, Margin = new(0, 0, 8, 0), SelectedValuePath = "Id", SelectedValue = condition.Field };
        var op = new ComboBox { Width = 115, Margin = new(0, 0, 8, 0), SelectedValuePath = "Id" };
        var value = new TextBox { Text = condition.Value, Width = 270, MaxLength = 512 };
        var choices = new ComboBox { Width = 270, SelectedValuePath = "Id" };
        var valueHost = new ContentControl { Width = 270, Margin = new(0, 0, 8, 0), Content = value };
        var row = Ui.Row(field, op, valueHost); row.Margin = new(0, 0, 0, 10);
        void SetOperators()
        {
            op.ItemsSource = (field.SelectedValue as string) switch
            {
                "extension" or "targetExtension" => new FieldOption[] { new("in", "是其中之一") },
                string textField when RuleEngine.IsTextField(textField) => [new("contains", "包含"), new("starts", "开头是"), new("literal", "包含原文"), new("regex", "正则匹配")],
                "source" or "kind" or "targetKind" => [new("is", "是")],
                _ => [new("lt", "小于"), new("gt", "大于")]
            };
            op.SelectedIndex = 0;
            if (field.SelectedValue as string is "source" or "kind" or "targetKind")
            {
                choices.ItemsSource = field.SelectedValue as string == "targetKind"
                    ? new FieldOption[] { new("file", "文件"), new("folder", "文件夹"), new("url", "网址 / 协议"), new("unknown", "不可访问 / 未知") }
                    : field.SelectedValue as string == "kind"
                    ? new FieldOption[] { new("file", "文件"), new("folder", "文件夹") }
                    : new[] { "微信（推测）", "QQ（推测）", "截图工具（推测）", "下载目录（推测）", "未知来源" }.Select(s => new FieldOption(s, s)).ToArray();
                choices.SelectedIndex = 0; valueHost.Content = choices;
            }
            else valueHost.Content = value;
        }
        field.SelectionChanged += (_, _) => SetOperators();
        SetOperators(); op.SelectedValue = condition.Operator;
        if (ReferenceEquals(valueHost.Content, choices)) choices.SelectedValue = condition.Value;
        var remove = Ui.IconButton("×", () => { rows.Children.Remove(row); conditions.RemoveAll(c => ReferenceEquals(c.Row, row)); }, "删除此条件"); remove.Foreground = Ui.Danger; remove.Margin = new Thickness(0, 0, 0, 0); row.Children.Add(remove);
        conditions.Add((field, op, () => ReferenceEquals(valueHost.Content, choices) ? choices.SelectedValue as string ?? "" : value.Text.Trim(), row)); rows.Children.Add(row);
    }

    private Rule Draft() => new(id, name.Text.Trim(), target.SelectedValue as string ?? "inbox",
        conditions.Select(c => new Condition(c.Field.SelectedValue as string ?? "", c.Operator.SelectedValue as string ?? "", c.Value())).ToList(), enabled.IsChecked == true);

    private void UpdatePreview()
    {
        var rule = Draft(); var error = RuleEngine.Validate(rule, organizer.State.Configuration.Collections);
        if (error != null) { preview.Text = error; return; }
        var matched = RuleEngine.Preview(organizer.Files, organizer.State.Configuration, rule, DateTime.UtcNow);
        preview.Text = rule.Enabled
            ? $"条件命中 {matched.Count} 项 · 本规则生效 {matched.Count(x => x.Applied)} 项 · 手动固定或优先规则影响 {matched.Count(x => !x.Applied)} 项\n"
                + string.Join("\n", matched.Take(8).Select(x => $"{x.File.Name} → {organizer.CollectionName(x.CollectionId)}（{x.Reason}）"
                    + (x.File.Target is { } t ? $"\n  目标：{t.Path}" : "")))
            : "规则已停用，保存后不参与自动归类。";
        if (rule.Conditions.Any(c => c.Field.StartsWith("target", StringComparison.Ordinal)))
            preview.Text += $"\n扫描范围内已读取 {organizer.Files.Count(f => f.Target != null)} 个快捷方式目标。未读取到的目标不会误匹配。";
    }
    private void Save()
    {
        var rule = Draft(); var error = RuleEngine.Validate(rule, organizer.State.Configuration.Collections);
        if (error != null) { preview.Text = error; return; }
        Result = rule; DialogResult = true;
    }
}
