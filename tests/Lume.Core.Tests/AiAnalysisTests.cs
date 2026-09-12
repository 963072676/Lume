using System.Net;
using System.Text.Json;
using Lume.Core;

internal static class AiAnalysisTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    public static void Register(Action<string, Action> test, string root)
    {
        void Check(bool ok) { if (!ok) throw new Exception("AI 测试断言失败"); }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return; } throw new Exception("应拒绝无效建议"); }
        Organizer Create()
        {
            var dir = Path.Combine(root, "ai-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "player.xyz"), "unchanged"); File.WriteAllText(Path.Combine(dir, "other.xyz"), "other");
            var o = new Organizer(new StateStore(Path.Combine(dir, "state.json")), AppState.Create([])); o.ApplyScan(DesktopScanner.Scan([dir])); return o;
        }
        string Suggest(string id, string collection = "apps") => JsonSerializer.Serialize(new { suggestions = new[] { new { itemId = id, collectionId = collection, reason = "播放器", confidence = 0.95 } } });
        test("AI 地址规范化支持根地址及自定义前缀且拒绝密钥藏入URL", () =>
        {
            Check(AiAnalysisClient.Endpoint("https://example.com", "models").AbsoluteUri == "https://example.com/v1/models");
            Check(AiAnalysisClient.Endpoint("http://localhost:1234/api/v1/chat/completions", "models").AbsoluteUri == "http://localhost:1234/api/v1/models");
            Reject(() => AiAnalysisClient.Endpoint("http://example.com/v1", "models"));
            Reject(() => AiAnalysisClient.Endpoint("https://key@example.com/v1", "models"));
            Reject(() => AiAnalysisClient.Endpoint("https://example.com/v1?key=secret", "models"));
        });
        test("AI 默认不发送完整路径或文件正文", () =>
        {
            var o = Create(); var snapshot = o.CaptureAiSnapshot(); var json = AiAnalysisClient.BuildPayload(snapshot, false, "影音归应用");
            Check(!json.Contains("unchanged") && !json.Contains("ai-"));
            Check(AiAnalysisClient.BuildPayload(snapshot, true, "").Contains("ai-"));
            o.Assign(o.Files[0].Path, "work"); Check(o.CaptureAiSnapshot().Items.Count == 1); Check(o.CaptureAiSnapshot(true).Items.Count == 2);
        });
        test("AI 响应拒绝未知分区文件重复条目以及越界置信度", () =>
        {
            var snapshot = Create().CaptureAiSnapshot();
            Reject(() => AiAnalysisClient.ParseSuggestions(Suggest("outside"), snapshot));
            Reject(() => AiAnalysisClient.ParseSuggestions(Suggest("f0", "delete-files"), snapshot));
            Reject(() => AiAnalysisClient.ParseSuggestions(Suggest("f0").Replace("0.95", "2"), snapshot));
            Reject(() => AiAnalysisClient.ParseSuggestions("not json", snapshot));
            var one = JsonDocument.Parse(Suggest("f0")).RootElement.GetProperty("suggestions")[0].GetRawText();
            Reject(() => AiAnalysisClient.ParseSuggestions("{\"suggestions\":[" + one + "," + one + "]}", snapshot));
            Check(AiAnalysisClient.ParseSuggestions("```json\n" + Suggest("f0") + "\n```", snapshot).Count == 1);
        });
        test("AI 获取模型和批次请求鉴权与内容协议", () =>
        {
            var snapshot = Create().CaptureAiSnapshot(); var calls = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                Check(request.Headers.Authorization?.Parameter == "test-key"); calls++;
                if (request.Method == HttpMethod.Get) return Json("{\"data\":[{\"id\":\"model-a\"}]}");
                var body = await request.Content!.ReadAsStringAsync(token); using var doc = JsonDocument.Parse(body);
                Check(doc.RootElement.GetProperty("model").GetString() == "model-a");
                Check(!doc.RootElement.GetProperty("stream").GetBoolean());
                return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Suggest("f0") }, finish_reason = "stop" } } }));
            }));
            var client = new AiAnalysisClient(http); var connection = new AiConnection("https://example.com/v1", "test-key", "model-a");
            Check(client.ModelsAsync(connection, default).GetAwaiter().GetResult().Single() == "model-a");
            Check(client.AnalyzeAsync(connection, snapshot, false, "", null, default).GetAwaiter().GetResult().Count == 1); Check(calls == 2);
        });
        test("AI 错误响应不回显密钥或服务原文", () =>
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-test-key") })));
            try { new AiAnalysisClient(http).ModelsAsync(new("https://example.com/v1", "secret-test-key", "x"), default).GetAwaiter().GetResult(); throw new Exception("应失败"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("401") && !ex.Message.Contains("secret")); }
        });
        test("AI 本机真实HTTP请求完成模型获取及分析往返", () =>
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var socket = await listener.AcceptTcpClientAsync(limit.Token); await using var stream = socket.GetStream();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(limit.Token); var length = 0; var auth = false; string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(limit.Token)))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1].Trim());
                        if (line == "Authorization: Bearer socket-fixture-key") auth = true;
                    }
                    Check(auth); var body = new char[length]; var read = 0;
                    while (read < length) { var count = await reader.ReadAsync(body.AsMemory(read), limit.Token); if (count == 0) throw new Exception("请求被截断"); read += count; }
                    string json;
                    if (i == 0) { Check(requestLine == "GET /v1/models HTTP/1.1"); json = "{\"data\":[{\"id\":\"socket-model\"}]}"; }
                    else
                    {
                        Check(requestLine == "POST /v1/chat/completions HTTP/1.1"); using var payload = JsonDocument.Parse(new string(body));
                        Check(payload.RootElement.GetProperty("model").GetString() == "socket-model");
                        json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Suggest("f0") }, finish_reason = "stop" } } });
                    }
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    var header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, limit.Token); await stream.WriteAsync(bytes, limit.Token);
                }
            });
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            var client = new AiAnalysisClient(http); var connection = new AiConnection($"http://127.0.0.1:{port}/v1", "socket-fixture-key", "socket-model");
            Check(client.ModelsAsync(connection, limit.Token).GetAwaiter().GetResult().Single() == "socket-model");
            Check(client.AnalyzeAsync(connection, Create().CaptureAiSnapshot(), false, "", null, limit.Token).GetAwaiter().GetResult().Count == 1);
            server.GetAwaiter().GetResult();
        });
        test("AI 取消请求终止等待", () =>
        {
            using var http = new HttpClient(new Handler(async (_, token) => { await Task.Delay(5000, token); return Json("{}"); }));
            using var cancellation = new CancellationTokenSource(50);
            try { new AiAnalysisClient(http).ModelsAsync(new("http://localhost/v1", "", "x"), cancellation.Token).GetAwaiter().GetResult(); throw new Exception("应取消"); }
            catch (OperationCanceledException) { }
        });
        test("AI 应用为单次可撤销事务且不移动文件", () =>
        {
            var o = Create(); var snapshot = o.CaptureAiSnapshot(); var before = o.State.History.Count;
            o.ApplyAiSuggestions(snapshot, snapshot.Items.Select(i => new AiSuggestion(i.Id, "apps", "应用", .9)).ToList());
            Check(o.State.History.Count == before + 1 && o.Files.All(f => o.CollectionOf(f) == "apps"));
            o.Undo(); Check(o.Files.All(f => o.CollectionOf(f) == "inbox"));
            Check(File.ReadAllText(o.Files.Single(f => f.Name == "player.xyz").Path) == "unchanged");
        });
        test("AI 拒绝分析后手动变更或文件修改并保证整批不部分应用", () =>
        {
            var o = Create(); var snapshot = o.CaptureAiSnapshot(); o.Assign(o.Files[0].Path, "work");
            Reject(() => o.ApplyAiSuggestions(snapshot, snapshot.Items.Select(i => new AiSuggestion(i.Id, "apps", "应用", .9)).ToList()));
            Check(o.CollectionOf(o.Files[1]) == "inbox");
            snapshot = o.CaptureAiSnapshot(true); File.AppendAllText(o.Files[1].Path, "changed");
            Reject(() => o.ApplyAiSuggestions(snapshot, snapshot.Items.Select(i => new AiSuggestion(i.Id, "apps", "应用", .9)).ToList()));
            Check(o.CollectionOf(o.Files[0]) == "work");
        });
    }
}
