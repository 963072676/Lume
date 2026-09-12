using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Lume.Core;

namespace Lume.Desktop;

internal sealed class AiAnalysisDialog : Window
{
    private readonly Organizer organizer;
    private readonly Action changed;
    private readonly AiSettingsStore settings;
    private readonly HttpClient http;
    private readonly AiAnalysisClient client;
    private readonly TextBox baseUrl = new() { MinWidth = 300, MaxLength = 2048, ToolTip = "例如 https://服务地址/v1；本机服务可使用 http://localhost:端口/v1" };
    private readonly PasswordBox key = new() { MinWidth = 300, MaxLength = 4096, Padding = new(8), ToolTip = "使用 Windows 当前用户加密保存。本机免密服务可留空。" };
    private readonly ComboBox model = new() { IsEditable = true, MinWidth = 220, MaxDropDownHeight = 250, ToolTip = "可获取后选择，也可直接输入模型名称" };
    private readonly TextBox preference = new() { MaxLength = 1000, TextWrapping = TextWrapping.Wrap, MinHeight = 50, AcceptsReturn = true };
    private readonly CheckBox paths = new() { Content = "包含完整路径", ToolTip = "默认仅发送名称和应用信息；完整路径有助于识别安装目录。" };
    private readonly CheckBox pinned = new() { Content = "包含手动固定的项目", ToolTip = "应用后会替换勾选项目的固定归属。" };
    private readonly CheckBox inbox = new() { Content = "仅分析临时收件箱" };
    private readonly TextBlock status = Ui.Text("配置接口后，点击分析生成建议。", Tokens.Secondary, Ui.Muted);
    private readonly StackPanel results = new();
    private readonly StackPanel configuration = new();
    private readonly Button analyze, apply, cancel;
    private readonly List<(CheckBox Check, AiSuggestion Suggestion, ComboBox Target)> selections = [];
    private readonly List<Collection> pendingCollections = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<Collection> targets = [];
    private AiSnapshot? snapshot;
    private CancellationTokenSource? cancellation;
    private bool closed;

    public AiAnalysisDialog(Window owner, Organizer organizer, string directory, Action changed, HttpMessageHandler? handler = null, bool startAnalysis = false)
    {
        Owner = owner; this.organizer = organizer; this.changed = changed; settings = new(directory);
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }; client = new(http);
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "AI 分析与归类"; Width = 820; Height = 720; MinWidth = 760; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new(26) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        status.Margin = new(0, 10, 0, 10); footer.Children.Add(status);
        analyze = Ui.Button("分析桌面分组", () => _ = AnalyzeAsync());
        apply = Ui.Button("应用勾选建议", Apply, true); apply.IsEnabled = false;
        cancel = Ui.Button("取消请求", () => cancellation?.Cancel()); cancel.IsEnabled = false;
        footer.Children.Add(Ui.Row(analyze, apply, cancel, Ui.Button("关闭", Close))); layout.Children.Add(footer);
        var body = new StackPanel(); body.Children.Add(Ui.Text("让 AI 帮你判断文件用途", Tokens.DialogTitle, bold: true));
        body.Children.Add(Ui.Text("兼容 OpenAI Chat Completions 接口；按现有普通分区生成归类建议。", Tokens.Secondary, Ui.Muted));
        var form = new Grid { Margin = new Thickness(0, 16, 0, 12) }; form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) }); form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Field(string label, UIElement input)
        {
            var row = form.RowDefinitions.Count; form.RowDefinitions.Add(new RowDefinition { MinHeight = 48 });
            var title = Ui.Text(label, Tokens.Secondary, Ui.SecondaryInk, true); title.VerticalAlignment = VerticalAlignment.Top; title.Margin = new Thickness(0, 8, 16, 0); Grid.SetRow(title, row); Grid.SetColumn(title, 0); form.Children.Add(title);
            if (input is FrameworkElement element) element.Margin = new Thickness(0, 4, 0, 4); Grid.SetRow(input, row); Grid.SetColumn(input, 1); form.Children.Add(input);
        }
        Field("服务地址", baseUrl);
        Field("API Key", key);
        Field("模型", Ui.Row(model, Ui.Button("获取模型 / 测试连接", () => _ = LoadModelsAsync())));
        preference.ToolTip = "可选，例如游戏启动器归游戏，下载工具归工具";
        Field("归类偏好", preference);
        var scope = new StackPanel(); scope.Children.Add(paths); scope.Children.Add(pinned); scope.Children.Add(inbox); Field("分析范围", scope);
        configuration.Children.Add(form); var save = Ui.Button("保存 AI 配置", Save); save.HorizontalAlignment = HorizontalAlignment.Left; save.Margin = new Thickness(0, 8, 0, 0); configuration.Children.Add(save); body.Children.Add(configuration);
        body.Children.Add(Ui.Text("点击分析会将文件信息、现有分组和偏好发送到你配置的服务，不上传文件正文或桌面截图。应用只改变分组，原文件位置不变；整理历史中可撤销。", Tokens.Label, Ui.Muted));
        var title = Ui.Text("归类建议", Tokens.SectionTitle, bold: true); title.Margin = new Thickness(0, 18, 0, 8); body.Children.Add(title); body.Children.Add(results);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = layout;
        try
        {
            var saved = settings.Load(); baseUrl.Text = saved.BaseUrl; model.Text = saved.Model; paths.IsChecked = saved.IncludePaths; preference.Text = saved.Preference;
            try { key.Password = settings.ReadKey(saved); }
            catch (Exception) { status.Text = "已保存的密钥无法解密，请重新输入。"; }
        }
        catch (Exception) { status.Text = "AI 配置无法读取，原文件已保留；请重新填写后保存。"; }
        Closed += (_, _) => { closed = true; cancellation?.Cancel(); http.Dispose(); };
        if (startAnalysis) Loaded += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(model.Text)) { status.Text = "首次使用：请填写接口和模型，再点击分析桌面分组。"; baseUrl.Focus(); }
            else await AnalyzeAsync();
        };
    }
    private AiConnection Connection() => new(baseUrl.Text.Trim(), key.Password.Trim(), model.Text.Trim());
    private void Save()
    {
        try { settings.Save(Connection(), paths.IsChecked == true, preference.Text.Trim()); status.Text = "AI 配置已保存，密钥已加密。"; }
        catch (Exception ex) { ShowFailure(ex); }
    }
    private void Busy(bool busy)
    {
        configuration.IsEnabled = !busy; analyze.IsEnabled = !busy; cancel.IsEnabled = busy;
        apply.IsEnabled = !busy && snapshot != null && selections.Count > 0;
    }
    private async Task LoadModelsAsync()
    {
        if (cancellation != null) return;
        cancellation = new(); Busy(true); status.Text = "正在获取模型列表…";
        try
        {
            var available = await client.ModelsAsync(Connection(), cancellation.Token); if (closed) return;
            var previous = model.Text; model.ItemsSource = available; model.Text = string.IsNullOrWhiteSpace(previous) ? available.FirstOrDefault() ?? "" : previous;
            status.Text = $"连接成功，获取 {available.Count} 个模型。请从中选择支持对话的模型。";
        }
        catch (Exception ex) { if (!closed) ShowFailure(ex); }
        finally { cancellation.Dispose(); cancellation = null; if (!closed) Busy(false); }
    }
    private async Task AnalyzeAsync()
    {
        if (cancellation != null) return;
        cancellation = new(); Busy(true); selections.Clear(); pendingCollections.Clear(); targets.Clear(); results.Children.Clear(); results.IsEnabled = true; snapshot = null;
        try
        {
            var connection = Connection(); settings.Save(connection, paths.IsChecked == true, preference.Text.Trim());
            snapshot = organizer.CaptureAiSnapshot(pinned.IsChecked == true, inbox.IsChecked == true);
            var suggestions = await client.AnalyzeAsync(connection, snapshot, paths.IsChecked == true, preference.Text.Trim(),
                new Progress<string>(s => { if (!closed) status.Text = s; }), cancellation.Token);
            if (closed) return;
            foreach (var c in snapshot.Collections) targets.Add(c);
            foreach (var suggestion in suggestions)
            {
                var item = snapshot.Items.Single(f => f.Id == suggestion.ItemId); var changedGroup = item.CollectionId != suggestion.CollectionId;
                var check = new CheckBox { Content = new TextBlock { Text = $"{FilePresentation.DisplayName(item.File)}  ·  {organizer.CollectionName(item.CollectionId)} →", TextWrapping = TextWrapping.Wrap },
                    IsChecked = changedGroup && suggestion.Confidence >= 0.75 && item.PinnedCollectionId == null };
                var row = new StackPanel(); var titleRow = new DockPanel(); var icon = ShellIcons.CreateImage(item.File);
                icon.Width = 28; icon.Height = 28; icon.Margin = new(0, 0, 10, 0); DockPanel.SetDock(icon, Dock.Left);
                titleRow.Children.Add(icon); titleRow.Children.Add(check); row.Children.Add(titleRow);
                var destination = new ComboBox { ItemsSource = targets, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = suggestion.CollectionId, Width = 210 };
                System.Windows.Automation.AutomationProperties.SetAutomationId(destination, "AiSuggestionTarget");
                destination.SelectionChanged += (_, _) => { if (item.PinnedCollectionId == null && destination.SelectedValue as string != item.CollectionId) check.IsChecked = true; };
                var destinationRow = Ui.Row(destination, Ui.Button("＋ 新分组", () =>
                {
                    var name = Ui.Prompt(this, "临时新建分组", "分组名称（应用建议时才创建）");
                    if (string.IsNullOrWhiteSpace(name)) return; name = name.Trim();
                    if (name.Length > 80) { status.Text = "分组名称最多 80 个字符。"; return; }
                    var existing = targets.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) { destination.SelectedValue = existing.Id; return; }
                    var created = new Collection(Guid.NewGuid().ToString("N"), name, "#92C7B5"); pendingCollections.Add(created); targets.Add(created); destination.SelectedValue = created.Id;
                }));
                titleRow.Children.Remove(check); DockPanel.SetDock(destinationRow, Dock.Right); titleRow.Children.Add(destinationRow); titleRow.Children.Add(check);
                row.Children.Add(Ui.Text($"置信度 {suggestion.Confidence:P0} · {suggestion.Reason}", Tokens.Secondary, Ui.Muted));
                if (item.File.Target is { } target) row.Children.Add(Ui.Text($"快捷目标：{target.Name}" + (paths.IsChecked == true ? $" · {target.Path}" : ""), Tokens.Label, Ui.Muted));
                if (item.PinnedCollectionId != null) row.Children.Add(Ui.Text("手动固定项目：只有主动勾选才会替换归属。", Tokens.Label, Ui.Muted));
                results.Children.Add(Ui.Card(row, new(12))); selections.Add((check, suggestion, destination));
            }
            status.Text = $"分析 {snapshot.Items.Count} 项，返回 {suggestions.Count} 条建议，{suggestions.Count(s => snapshot.Items.Single(i => i.Id == s.ItemId).CollectionId != s.CollectionId)} 项建议更换分组；未返回 {snapshot.Items.Count - suggestions.Count} 项保持原状。请审阅后应用。";
        }
        catch (Exception ex) { snapshot = null; if (!closed) ShowFailure(ex); }
        finally { cancellation.Dispose(); cancellation = null; if (!closed) Busy(false); }
    }
    private void Apply()
    {
        if (snapshot == null) return;
        try
        {
            var selected = selections.Where(s => s.Check.IsChecked == true).Select(s => s.Suggestion with { CollectionId = s.Target.SelectedValue as string ?? s.Suggestion.CollectionId }).ToList();
            organizer.ApplyAiSuggestions(snapshot, selected, pendingCollections); changed();
            status.Text = $"已应用 {selected.Count} 项，原文件未移动。可在整理历史中撤销。";
            snapshot = null; selections.Clear(); apply.IsEnabled = false; results.IsEnabled = false;
        }
        catch (Exception ex) { ShowFailure(ex); }
    }
    private void ShowFailure(Exception ex) => status.Text = ex switch
    {
        OperationCanceledException => cancellation?.IsCancellationRequested == true ? "请求已取消，没有应用归类。" : "请求超时，请重试或更换模型。",
        HttpRequestException => "连接失败，请检查服务地址、网络和服务是否运行。",
        InvalidOperationException or InvalidDataException => ex.Message,
        _ => "操作失败，请检查配置和本地文件权限；没有应用新的归类。"
    };
}
