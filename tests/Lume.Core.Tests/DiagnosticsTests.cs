using System.IO.Compression;
using Lume.Core;

internal static class DiagnosticsTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        void Check(bool value) { if (!value) throw new Exception("诊断回归断言失败"); }
        test("诊断容量受限且错误消息路径密钥不能进入日志和导出", () =>
        {
            var folder = Path.Combine(root, "diagnostic-log"); Directory.CreateDirectory(folder);
            // Exercise rotation without depending on scheduler throughput.
            File.WriteAllText(Path.Combine(folder, "runtime.jsonl"), new string(' ', RuntimeDiagnostics.MaximumFileBytes));
            using var diagnostics = new RuntimeDiagnostics(folder);
            diagnostics.Record(DiagnosticKind.WatcherError, error: new IOException("secret-token C:\\private\\report.docx https://private.invalid"));
            diagnostics.Heartbeat(); diagnostics.Dispose();
            var files = Directory.GetFiles(folder); Check(files.Length == 2 && files.All(p => new FileInfo(p).Length <= RuntimeDiagnostics.MaximumFileBytes));
            var log = File.ReadAllText(Path.Combine(folder, "runtime.jsonl")); Check(!log.Contains("secret-token") && !log.Contains("private") && log.Contains("PrivateBytes"));
            // Unknown external fields in a valid line must be discarded on export.
            File.AppendAllText(Path.Combine(folder, "runtime.jsonl"), "{\"Kind\":0,\"Error\":0,\"message\":\"secret-token\"}\npartial");
            var export = Path.Combine(root, "diagnostics.zip"); diagnostics.Export(export, new(1, 2, 3, 4, false));
            using var zip = ZipFile.OpenRead(export); Check(zip.Entries.Count == 2);
            foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); var text = reader.ReadToEnd(); Check(!text.Contains("secret-token") && !text.Contains("report.docx")); }
        });
        test("诊断目录无法写入不影响调用方且溢出计数可见", () =>
        {
            var file = Path.Combine(root, "diagnostics-blocked"); File.WriteAllText(file, "block");
            using var diagnostics = new RuntimeDiagnostics(file);
            diagnostics.Record(DiagnosticKind.Startup); diagnostics.Dispose(); Check(diagnostics.Dropped > 0);
        });
    }
}
