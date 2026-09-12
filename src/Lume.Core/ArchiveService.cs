using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lume.Core;

public sealed class ArchiveItem
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public long Length { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "Planned";
    public string Message { get; set; } = "等待归档";
}
public sealed class ArchiveBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string DestinationRoot { get; set; } = "";
    public List<ArchiveItem> Items { get; set; } = [];
}

/// <summary>物理操作使用独立的预写日志，不与可撤销的分类配置快照混合。</summary>
public sealed class ArchiveService(string journalDirectory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool IsInside(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void ValidatePhysicalPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("物理归档不处理链接或云占位路径，请使用已下载到本地的普通文件和目录。");
        }
    }

    private static string Hash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private static bool HasContent(string path, ArchiveItem item) => File.Exists(path) && new FileInfo(path).Length == item.Length && Hash(path) == item.Sha256;

    public async Task<ArchiveBatch> PreviewAsync(IEnumerable<DesktopFile> files, IEnumerable<string> allowedRoots, string destinationRoot, bool monthFolder)
    {
        var selected = files.ToList(); var roots = allowedRoots.Select(Path.GetFullPath).ToList();
        var root = Path.GetFullPath(destinationRoot);
        ValidatePhysicalPath(root);
        if (File.Exists(root)) throw new IOException("目标位置是文件，不能作为归档目录。");
        var batch = new ArchiveBatch { DestinationRoot = root };
        await Task.Run(() =>
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in selected)
            {
                var source = Path.GetFullPath(file.Path);
                var destination = Path.Combine(root, monthFolder ? DateTime.Now.ToString("yyyy-MM") : "", Path.GetFileName(source));
                var item = new ArchiveItem { Source = source, Destination = destination };
                batch.Items.Add(item);
                try
                {
                    if (file.IsDirectory || !roots.Contains(Path.GetDirectoryName(source)!, StringComparer.OrdinalIgnoreCase)) throw new IOException("仅归档当前监控目录第一层的普通文件。");
                    ValidatePhysicalPath(source); ValidatePhysicalPath(destination);
                    if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("源位置和目标位置相同。");
                    if (!targets.Add(destination) || File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标同名，跳过且不覆盖。");
                    var info = new FileInfo(source); item.Length = info.Length; item.ModifiedUtc = info.LastWriteTimeUtc; item.Sha256 = Hash(source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { item.Status = "Skipped"; item.Message = ex.Message; }
            }
        });
        return batch;
    }

    private string JournalPath(ArchiveBatch batch)
    {
        if (!Guid.TryParseExact(batch.Id, "N", out _)) throw new InvalidDataException("归档记录编号无效。");
        return Path.Combine(journalDirectory, batch.Id + ".json");
    }
    private void Save(ArchiveBatch batch)
    {
        Directory.CreateDirectory(journalDirectory);
        var path = JournalPath(batch); var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, batch, Json); stream.Flush(true); }
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
    }

    public IReadOnlyList<ArchiveBatch> History()
    {
        if (!Directory.Exists(journalDirectory)) return [];
        var records = new List<ArchiveBatch>();
        foreach (var path in Directory.EnumerateFiles(journalDirectory, "*.json"))
        {
            var batch = JsonSerializer.Deserialize<ArchiveBatch>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"归档日志损坏：{path}");
            ValidateJournal(batch); records.Add(batch);
        }
        return records.OrderByDescending(b => b.CreatedUtc).ToList();
    }
    private void ValidateJournal(ArchiveBatch batch)
    {
        _ = JournalPath(batch);
        if (!Path.IsPathFullyQualified(batch.DestinationRoot)) throw new InvalidDataException("归档目标不是绝对路径。");
        foreach (var item in batch.Items)
            if (!Path.IsPathFullyQualified(item.Source) || !IsInside(item.Destination, batch.DestinationRoot)
                || item.Source.Equals(item.Destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("归档记录路径无效。");
    }

    public async Task ExecuteAsync(ArchiveBatch batch, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            ValidateJournal(batch);
            if (File.Exists(JournalPath(batch))) throw new InvalidOperationException("这批归档已经执行，请重新生成预览。");
            Save(batch); // 确保日志可写后才操作任何文件。
            foreach (var item in batch.Items.Where(i => i.Status == "Planned"))
            {
                if (cancellation.IsCancellationRequested) { item.Status = "Skipped"; item.Message = "用户取消，未移动"; Save(batch); continue; }
                try
                {
                    ValidatePhysicalPath(item.Source); ValidatePhysicalPath(item.Destination);
                    if (new FileInfo(item.Source).LastWriteTimeUtc != item.ModifiedUtc || !HasContent(item.Source, item)) throw new IOException("预览后源文件发生变化，请重新预览。");
                    item.Status = "Copying"; item.Message = "正在复制并校验"; Save(batch);
                    await CopyVerifiedAsync(item.Source, item.Destination, item, batch.Id, cancellation);
                    item.Status = "DestinationReady"; item.Message = "副本已校验，准备移除源文件"; Save(batch);
                    // 保存可恢复日志后，再检查源文件仍与预览一致。
                    ValidatePhysicalPath(item.Source);
                    if (!HasContent(item.Source, item) || new FileInfo(item.Source).LastWriteTimeUtc != item.ModifiedUtc) throw new IOException("复制期间源文件变化，已保留两份文件。");
                    DeleteVerified(item.Source, item.Destination, item);
                    item.Status = "Archived"; item.Message = "已归档，可恢复原位置"; Save(batch);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    item.Status = File.Exists(item.Destination) ? "Review" : "Failed";
                    item.Message = ex is OperationCanceledException ? "已取消，源文件保留" : ex.Message;
                    Save(batch);
                }
            }
        }
        finally { gate.Release(); }
    }

    private static async Task CopyVerifiedAsync(string source, string destination, ArchiveItem item, string id, CancellationToken cancellation)
    {
        ValidatePhysicalPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标已存在，不覆盖。请重新预览。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ValidatePhysicalPath(destination);
        var temporary = destination + ".lume-" + id + ".partial";
        var ownsTemp = false;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            {
                var originalHash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation));
                if (input.Length != item.Length || originalHash != item.Sha256) throw new IOException("文件内容与归档记录不一致，已停止。");
                input.Position = 0;
                // Windows 原生复制保留备用数据流（包括 Zone.Identifier），不使用只复制正文的流拷贝。
                await Task.Run(() => File.Copy(source, temporary, false), cancellation);
                ownsTemp = true;
                cancellation.ThrowIfCancellationRequested();
                using var output = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                output.Flush(true);
            }
            if (!HasContent(temporary, item)) throw new IOException("副本校验失败，源文件保留。");
            File.SetLastWriteTimeUtc(temporary, item.ModifiedUtc);
            ValidatePhysicalPath(destination);
            File.Move(temporary, destination, false); ownsTemp = false;
        }
        finally { if (ownsTemp && File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task RestoreAsync(string batchId, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var batch = History().Single(b => b.Id == batchId);
            foreach (var item in batch.Items.Where(i => i.Status is "Archived" or "RestoreFailed"))
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    ValidatePhysicalPath(item.Source); ValidatePhysicalPath(item.Destination);
                    if (File.Exists(item.Source) || Directory.Exists(item.Source)) throw new IOException("原位置已有同名项，保留归档文件，不覆盖。");
                    if (!HasContent(item.Destination, item)) throw new IOException("归档文件已被修改或缺失，不执行恢复。");
                    item.Status = "Restoring"; item.Message = "正在恢复并校验"; Save(batch);
                    await CopyVerifiedAsync(item.Destination, item.Source, item, batch.Id, cancellation);
                    item.Status = "RestoreCopied"; item.Message = "原位置副本已校验"; Save(batch);
                    ValidatePhysicalPath(item.Destination);
                    if (!HasContent(item.Destination, item)) throw new IOException("恢复期间归档文件发生变化，保留两份文件。");
                    DeleteVerified(item.Destination, item.Source, item);
                    item.Status = "Restored"; item.Message = "已恢复原位置"; Save(batch);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                { item.Status = "RestoreFailed"; item.Message = ex.Message; Save(batch); }
            }
        }
        finally { gate.Release(); }
    }

    // 断电恢复仅判定事实，不擅自删除仍然存在的源文件或副本。
    public async Task RecoverAsync()
    {
        await gate.WaitAsync();
        try
        {
            foreach (var batch in History())
            {
                var changed = false;
                foreach (var item in batch.Items.Where(i => i.Status is "Copying" or "DestinationReady" or "Restoring" or "RestoreCopied"))
                {
                    changed = true;
                    try
                    {
                        ValidatePhysicalPath(item.Source); ValidatePhysicalPath(item.Destination);
                        if (!File.Exists(item.Source) && HasContent(item.Destination, item)) { item.Status = "Archived"; item.Message = "已从中断日志确认归档完成"; }
                        else if (!File.Exists(item.Destination) && HasContent(item.Source, item)) { item.Status = item.Status is "Restoring" or "RestoreCopied" ? "Restored" : "Failed"; item.Message = "源位置文件完整"; }
                        else { item.Status = "Review"; item.Message = "操作曾中断，文件均保留。请核对原位置和目标位置。"; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { item.Status = "Review"; item.Message = ex.Message; }
                }
                if (changed) Save(batch);
            }
        }
        finally { gate.Release(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref int disposition, uint size);
    private static void DeleteVerified(string source, string copy, ArchiveItem item)
    {
        // 两端都锁住，校验后通过源文件句柄删除，避免路径在校验与删除之间被换成另一个文件。
        using var destination = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (destination.Length != item.Length || Convert.ToHexString(SHA256.HashData(destination)) != item.Sha256) throw new IOException("目标副本发生变化，保留源文件。");
        using var handle = CreateFile(source, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("源文件正在使用或无删除权限，保留两份文件。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        using var input = new FileStream(handle, FileAccess.Read);
        ValidatePhysicalPath(source);
        if (input.Length != item.Length || Convert.ToHexString(SHA256.HashData(input)) != item.Sha256) throw new IOException("源文件已变化，保留两份文件。");
        var delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 4)) throw new IOException("删除源文件失败，保留两份文件。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }
}
