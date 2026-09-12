using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lume.Core;

namespace Lume.Desktop;

internal static class AiVerification
{
    private sealed class FakeProvider : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; await Task.Delay(30, token);
            if (request.Headers.Authorization?.Parameter != "fixture-key") throw new Exception("未发送测试鉴权");
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture-model\"}]}") };
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("规则生成器"))
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "{\"conditions\":[{\"field\":\"extension\",\"operator\":\"in\",\"value\":\"mp3,mp4,mkv,flac\"}]}" }, finish_reason = "stop" } } })) };
            using var payload = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var suggestions = payload.RootElement.GetProperty("files").EnumerateArray().Select(f => new
            { itemId = f.GetProperty("id").GetString(), collectionId = "apps", reason = "本地协议测试：应用启动器归入应用分区", confidence = .95 }).ToArray();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new { suggestions }) }, finish_reason = "stop" } } })) };
        }
    }
    private static IEnumerable<T> Find<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var item in Find<T>(VisualTreeHelper.GetChild(node, i))) yield return item;
    }
    public static async Task RunAsync(string folder, List<string> checks)
    {
        void Check(bool condition, string text) { if (!condition) throw new Exception(text); checks.Add(text); }
        var directory = Path.Combine(folder, "ai-fixture", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var desktop = Path.Combine(directory, "desktop"); Directory.CreateDirectory(desktop);
        File.WriteAllText(Path.Combine(desktop, "音乐播放器.xyz"), "test bytes");
        var organizer = new Organizer(new StateStore(Path.Combine(directory, "state.json")), AppState.Create([])); organizer.ApplyScan(DesktopScanner.Scan([desktop]));
        var settings = new AiSettingsStore(directory); settings.Save(new("https://fixture.invalid/v1", "fixture-key", ""), false, "播放器归应用");
        Check(!File.ReadAllText(Path.Combine(directory, "ai-settings.json")).Contains("fixture-key"), "AI 密钥没有明文落盘");
        Check(settings.ReadKey(settings.Load()) == "fixture-key", "AI 密钥通过 Windows 当前用户加密往返");
        var owner = new Window { Left = -16000, ShowActivated = false }; owner.Show();
        var handler = new FakeProvider(); var changed = 0;
        var dialog = new AiAnalysisDialog(owner, organizer, directory, () => changed++, handler)
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, ShowActivated = false };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            Button Button(string text) => Find<Button>(dialog).Single(b => b.Content as string == text);
            void Click(string text) => Button(text).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            async Task Wait(Func<bool> done) { for (var i = 0; i < 100 && !done(); i++) await Task.Delay(30); Check(done(), "AI 异步操作完成"); }
            Click("获取模型 / 测试连接"); await Wait(() => Button("分析桌面分组").IsEnabled);
            Check(Find<ComboBox>(dialog).Single().Text == "fixture-model", "AI 模型列表控件完成加载与选择");
            Click("分析桌面分组"); await Wait(() => Button("应用勾选建议").IsEnabled);
            Check(organizer.CollectionOf(organizer.Files.Single()) == "inbox", "AI 预览阶段没有自动修改分组");
            Check(handler.Requests == 2, "AI 模型列表和分析按钮均发送协议请求");
            var scroll = Find<ScrollViewer>(dialog).First(s => s.Content is StackPanel); scroll.ScrollToBottom(); dialog.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(folder, "AI建议预览.png"))) encoder.Save(stream);
            var destination = Find<ComboBox>(dialog).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "AiSuggestionTarget");
            destination.SelectedValue = "work";
            Click("应用勾选建议");
            Check(changed == 1 && organizer.CollectionOf(organizer.Files.Single()) == "work", "AI 下拉修改目标后实际按用户选择应用");
            Check(!Button("应用勾选建议").IsEnabled, "AI 应用后禁止重复提交同一批结果");
            organizer.Undo(); Check(organizer.CollectionOf(organizer.Files.Single()) == "inbox", "AI 实际界面应用后可撤销");
            Check(File.ReadAllText(organizer.Files.Single().Path) == "test bytes", "AI 归类未改变文件内容和路径");
            var quickHandler = new FakeProvider();
            var quick = new AiAnalysisDialog(owner, organizer, directory, () => { }, quickHandler, startAnalysis: true)
                { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, ShowActivated = false };
            try
            {
                quick.Show();
                await Wait(() => Find<Button>(quick).Any(b => b.Content as string == "应用勾选建议" && b.IsEnabled));
                Check(quickHandler.Requests == 1, "AI 一键入口加载已保存配置后自动开始分析");
                Check(organizer.CollectionOf(organizer.Files.Single()) == "inbox", "AI 一键入口仍先预览而不直接修改分组");
            }
            finally { quick.Close(); }
            var ruleHandler = new FakeProvider();
            var ruleDialog = new RuleDialog(owner, organizer, new Rule("ai-rule", "多媒体规则", "apps", [new Lume.Core.Condition("name", "contains", "多媒体")]), directory, ruleHandler)
                { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, ShowActivated = false };
            try
            {
                ruleDialog.Show(); ruleDialog.UpdateLayout();
                Find<Button>(ruleDialog).Single(b => b.Content as string == "AI 分析并填入条件").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Wait(() => Find<TextBox>(ruleDialog).Any(t => t.Text == "mp3,mp4,mkv,flac"));
                Check(ruleHandler.Requests == 1 && !organizer.State.Configuration.Rules.Any(r => r.Id == "ai-rule"), "规则编辑器AI生成扩展名但未提前保存");
                Check(Find<ComboBox>(ruleDialog).Any(c => c.SelectedValue as string == "extension"), "AI规则字段实际切换为扩展名");
                Check(ruleDialog.Icon != null, "规则编辑器显示 Lume Logo");
            }
            finally { ruleDialog.Close(); }
        }
        finally { dialog.Close(); owner.Close(); }
    }
}
