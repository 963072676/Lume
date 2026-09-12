using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Lume.Core;

public static class FilePreview
{
    public const long MaximumBytes = 64 * 1024 * 1024;
    public static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" => "image",
        ".pdf" => "pdf",
        ".mp4" or ".wmv" or ".avi" or ".mp3" or ".wav" or ".wma" => "media",
        ".docx" or ".xlsx" or ".pptx" => "office",
        ".zip" => "zip",
        ".txt" or ".md" or ".json" or ".xml" or ".csv" or ".log" or ".cs" or ".js" or ".ts" or ".py" or ".html" or ".css" or ".ini" or ".yaml" or ".yml" or ".bat" or ".cmd" or ".ps1" => "text",
        _ => "info"
    };
    public static void Validate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new IOException("文件已不存在。");
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0) throw new IOException("请先在资源管理器中将此文件下载到本地，再预览。");
        if (info.Length > MaximumBytes && Kind(path) != "media") throw new IOException("此文件超过 64 MB，请使用默认应用打开。");
    }
    public static string ReadText(string path)
    {
        Validate(path); var kind = Kind(path);
        if (kind is "office" or "zip") return ReadArchive(path, kind == "office");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[Math.Min(128 * 1024, (int)stream.Length)]; var n = stream.ReadAtLeast(bytes, bytes.Length, false);
        string text;
        if (n >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) text = Encoding.Unicode.GetString(bytes, 2, n - 2);
        else if (n >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) text = Encoding.BigEndianUnicode.GetString(bytes, 2, n - 2);
        else try { var decoder = new UTF8Encoding(false, true).GetDecoder(); var chars = new char[n]; var count = decoder.GetChars(bytes, 0, n, chars, 0, stream.Length <= n); text = new string(chars, 0, count).TrimStart('\ufeff'); }
            catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding("GB18030").GetString(bytes, 0, n); }
        return text + (stream.Length > n ? "\n\n……仅显示前 128 KB" : "");
    }
    private static string ReadArchive(string path, bool office)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 10000) throw new IOException("压缩包条目过多，请使用默认应用打开。");
        if (!office) return string.Join("\n", zip.Entries.Take(300).Select(e => $"{e.FullName}  ({e.Length:N0} 字节)")) + (zip.Entries.Count > 300 ? "\n……只显示前 300 项" : "");
        var output = new StringBuilder("文档文字预览（不执行宏，不保留原版式）\n\n");
        var entries = zip.Entries.Where(e => e.FullName == "word/document.xml" || e.FullName == "xl/sharedStrings.xml" || e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) || (e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))).Take(30);
        var shared = new List<string>();
        foreach (var entry in entries.OrderBy(e => e.FullName == "xl/sharedStrings.xml" ? "" : e.FullName, StringComparer.Ordinal))
        {
            if (entry.Length > 2 * 1024 * 1024) { output.AppendLine("此部分过大，已跳过。"); continue; }
            using var source = entry.Open(); using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
            var doc = XDocument.Load(reader);
            if (entry.FullName == "xl/sharedStrings.xml") { shared.AddRange(doc.Descendants().Where(x => x.Name.LocalName == "si").Select(x => string.Concat(x.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))); continue; }
            output.AppendLine("— " + entry.FullName + " —");
            if (entry.FullName.StartsWith("xl/", StringComparison.Ordinal))
                foreach (var row in doc.Descendants().Where(x => x.Name.LocalName == "row").Take(300))
                    output.AppendLine(string.Join("\t", row.Elements().Where(x => x.Name.LocalName == "c").Select(c => { var v = c.Descendants().FirstOrDefault(x => x.Name.LocalName is "v" or "t")?.Value ?? ""; return (string?)c.Attribute("t") == "s" && int.TryParse(v, out var index) && index >= 0 && index < shared.Count ? shared[index] : v; })));
            else foreach (var paragraph in doc.Descendants().Where(x => x.Name.LocalName == "p")) output.AppendLine(string.Concat(paragraph.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value)));
            if (output.Length > 128 * 1024) { output.Length = 128 * 1024; output.Append("\n……内容已截断"); break; }
        }
        return output.ToString();
    }
}
