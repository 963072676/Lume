using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lume.Core;

public sealed record DataBackupInfo(DateTime CreatedUtc, string ProductVersion, int Files, int Collections, int HistoryEntries);

public static class DataBackup
{
    private const string ManifestName = "lume-backup.json";
    private const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumFiles = 10000;
    private sealed record BackupFile(string Name, long Bytes, string Sha256);
    private sealed record Manifest(int Schema, DateTime CreatedUtc, string ProductVersion, List<BackupFile> Files);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static bool Inside(string path, string folder) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Allowed(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(s => s is "" or "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return false;
        if (!name.Contains('/')) return name is "ai-settings.json" or "layout-before-restore.json" or "state.json" || (name.StartsWith("state.json.", StringComparison.Ordinal) && !name.EndsWith(".tmp", StringComparison.Ordinal));
        var parts = name.Split('/');
        return parts.Length == 2 && (parts[0] == "state.json.history" && parts[1].Length == 69 && parts[1].EndsWith(".json", StringComparison.Ordinal) && parts[1][..64].All(Uri.IsHexDigit)
            || parts[0] == "archive-journal" && (parts[1].EndsWith(".json", StringComparison.Ordinal) || parts[1].EndsWith(".json.bak", StringComparison.Ordinal) || parts[1].Contains(".json.corrupt-", StringComparison.Ordinal)));
    }
    private static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份和恢复不处理链接目录或云占位数据。");
    }
    private static string[] FilesIn(string directory)
    {
        NoLink(directory);
        var files = Directory.EnumerateFiles(directory).Where(p => Allowed(Path.GetFileName(p))).ToList();
        foreach (var name in new[] { "state.json.history", "archive-journal" })
        {
            var folder = Path.Combine(directory, name); if (!Directory.Exists(folder)) continue;
            NoLink(folder);
            files.AddRange(Directory.EnumerateFiles(folder).Where(p => Allowed(name + "/" + Path.GetFileName(p))));
        }
        if (files.Count > MaximumFiles) throw new IOException("数据文件超过备份数量限制。");
        foreach (var file in files) NoLink(file);
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private static DataBackupInfo ValidateData(string directory, Manifest manifest)
    {
        var path = Path.Combine(directory, "state.json");
        if (!File.Exists(path)) throw new InvalidDataException("备份中缺少主配置。");
        try
        {
            var store = new StateStore(path); var state = store.Load([]);
            foreach (var chunk in state.HistoryArchives)
            {
                var history = store.ReadHistory(chunk.File);
                if (history.Count != chunk.Count || history.Count(h => !h.Undone) != chunk.Undoable) throw new InvalidDataException("备份历史索引不一致。");
            }
            return new(manifest.CreatedUtc, manifest.ProductVersion, manifest.Files.Count, state.Configuration.Collections.Count,
                state.History.Count + state.HistoryArchives.Sum(c => c.Count));
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        { throw new InvalidDataException("备份配置或历史依赖不完整，当前数据保持原状。", ex); }
    }

    public static DataBackupInfo Export(string dataDirectory, string destination, string productVersion)
    {
        var directory = Path.GetFullPath(dataDirectory); var output = Path.GetFullPath(destination);
        if (Inside(output, directory)) throw new IOException("请将备份保存到数据目录之外。");
        var files = FilesIn(directory); var entries = new List<BackupFile>(); long total = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var name = Path.GetRelativePath(directory, file).Replace('\\', '/');
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var bytes = new byte[64 * 1024]; long length = 0; int read;
                    while ((read = source.Read(bytes)) > 0)
                    {
                        total += read; length += read;
                        if (total > MaximumBytes) throw new IOException("备份数据超过 512 MiB，请先导出和整理较早历史。");
                        hash.AppendData(bytes, 0, read); target.Write(bytes, 0, read);
                    }
                    entries.Add(new(name, length, Convert.ToHexString(hash.GetHashAndReset())));
                }
                using var manifestStream = archive.CreateEntry(ManifestName).Open();
                JsonSerializer.Serialize(manifestStream, new Manifest(1, DateTime.UtcNow, productVersion, entries), Json);
            }
            // Reject a moving snapshot rather than publish mismatched configuration/history.
            if (!files.SequenceEqual(FilesIn(directory), StringComparer.OrdinalIgnoreCase)
                || entries.Any(e => Hash(Path.Combine(directory, e.Name.Replace('/', Path.DirectorySeparatorChar))) != e.Sha256))
                throw new IOException("备份期间数据发生变化，请稍后重试。");
            var info = Inspect(temp);
            File.Move(temp, output, true); return info;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static (Manifest Manifest, DataBackupInfo Info) Extract(string source, string stage)
    {
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count is < 2 or > MaximumFiles + 1) throw new InvalidDataException("备份文件数量无效。");
        var manifestEntry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("文件不是 Lume 完整备份。");
        if (manifestEntry.Length > 2 * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
        Manifest manifest;
        try
        {
            using var input = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<Manifest>(input) ?? throw new InvalidDataException("备份清单为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException("备份清单损坏。", ex); }
        if (manifest.Schema != 1 || manifest.Files == null || manifest.Files.Count + 1 != archive.Entries.Count
            || manifest.Files.Any(f => f == null || !Allowed(f.Name) || f.Bytes < 0 || f.Bytes > MaximumBytes || f.Sha256 == null || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit))
            || manifest.Files.Sum(f => f.Bytes) > MaximumBytes || manifest.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count
            || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new InvalidDataException("备份清单、路径或容量无效。");
        Directory.CreateDirectory(stage);
        foreach (var file in manifest.Files)
        {
            var entry = archive.GetEntry(file.Name) ?? throw new InvalidDataException("备份文件缺失。");
            if (entry.Length != file.Bytes) throw new InvalidDataException("备份文件长度不一致。");
            var path = Path.GetFullPath(Path.Combine(stage, file.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (!Inside(path, stage)) throw new InvalidDataException("备份路径超出恢复目录。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var input = entry.Open())
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[64 * 1024]; long copied = 0; int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    copied += read; if (copied > file.Bytes) throw new InvalidDataException("备份内容超过声明长度。");
                    output.Write(buffer, 0, read);
                }
                if (copied != file.Bytes) throw new InvalidDataException("备份内容被截断。");
                output.Flush(true);
            }
            if (!Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份内容校验失败。");
        }
        return (manifest, ValidateData(stage, manifest));
    }
    public static DataBackupInfo Inspect(string source)
    {
        var stage = Path.Combine(Path.GetTempPath(), "Lume-backup-" + Guid.NewGuid().ToString("N"));
        try { return Extract(source, stage).Info; }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    // Caller must stop Lume and its writers before replacing the data directory.
    public static string RestoreOffline(string dataDirectory, string source)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var parent = Path.GetDirectoryName(directory) ?? throw new IOException("恢复目录无效。");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".Lume-restore-" + Guid.NewGuid().ToString("N"));
        var preserved = Path.Combine(parent, "Lume-before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var moved = false;
        try
        {
            _ = Extract(source, stage);
            if (Directory.Exists(directory)) { NoLink(directory); Directory.Move(directory, preserved); moved = true; }
            try { Directory.Move(stage, directory); }
            catch { if (moved) Directory.Move(preserved, directory); throw; }
            return moved ? preserved : "";
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
