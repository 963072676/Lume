using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lume.Core;
using Microsoft.Win32.SafeHandles;

namespace Lume.Desktop;

internal sealed record ReferencePathProbe(string Name, ReferencePresence Presence);
internal sealed record ReferenceProbeResult(string? ScopeIdentity, ReferencePathProbe[] Entries);

internal static class WindowsReferenceProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    private static string? Identity(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed) return null;
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // A local path may hide a junction, cloud placeholder or disconnected volume higher in the tree.
        while (true)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return null;
            var parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }
        using var handle = CreateFile(root, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // READ_ATTRIBUTES, BACKUP_SEMANTICS, OPEN_REPARSE_POINT
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.IndexHigh == 0 && info.IndexLow == 0)
            || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return null;
        return $"{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}:{info.Created.dwHighDateTime:X8}{info.Created.dwLowDateTime:X8}";
    }

    internal static ReferenceProbeResult Read(string root, string[] names)
    {
        if (names.Length is < 1 or > 32 || names.Any(n => n.Length is < 1 or > 255 || n is "." or ".." || Path.GetFileName(n) != n || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) return new(null, []);
        ReferenceProbeResult Unknown() => new(null, names.Select(n => new ReferencePathProbe(n, ReferencePresence.Unavailable)).ToArray());
        try
        {
            var identity = Identity(root); if (identity == null) return Unknown();
            var results = names.Select(name =>
            {
                try { _ = File.GetAttributes(Path.Combine(root, name)); return new ReferencePathProbe(name, ReferencePresence.Present); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new ReferencePathProbe(name, ReferencePresence.Missing); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new ReferencePathProbe(name, ReferencePresence.Unavailable); }
            }).ToArray();
            return Identity(root) == identity ? new(identity, results) : Unknown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Unknown(); }
    }

    internal static async Task<IReadOnlyList<ReferenceObservation>> ReadAsync(ShellWorkerClient worker, IReadOnlyList<string> paths,
        IReadOnlyList<string> roots, CancellationToken cancellation)
    {
        var allowed = roots.ToHashSet(StringComparer.OrdinalIgnoreCase); var result = new List<ReferenceObservation>();
        var started = Stopwatch.GetTimestamp(); var budget = TimeSpan.FromSeconds(15);
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
            foreach (var chunk in group.Chunk(32))
            {
                cancellation.ThrowIfCancellationRequested();
                var remaining = budget - Stopwatch.GetElapsedTime(started);
                ShellReply? reply = null;
                if (allowed.Contains(group.Key) && remaining > TimeSpan.Zero)
                    reply = await worker.SendAsync(new("references", Path: group.Key, Names: chunk.Select(Path.GetFileName).ToArray()!), cancellation,
                        remaining < ShellWorkerClient.RequestLimit ? remaining : ShellWorkerClient.RequestLimit);
                var probes = reply is { Ok: true, References: not null } ? reply.References : null;
                // Ignore malformed, duplicate and truncated replies. Every unconfirmed path is protected.
                var entries = probes?.Entries;
                var valid = entries != null && entries.Length == chunk.Length && entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length
                    && entries.All(e => chunk.Any(p => string.Equals(Path.GetFileName(p), e.Name, StringComparison.OrdinalIgnoreCase)));
                foreach (var path in chunk)
                {
                    var item = valid ? entries!.Single(e => string.Equals(Path.GetFileName(path), e.Name, StringComparison.OrdinalIgnoreCase)) : null;
                    result.Add(new(path, item?.Presence ?? ReferencePresence.Unavailable, probes?.ScopeIdentity));
                }
            }
        return result;
    }
}
