using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lume.Core;

/// <summary>Read shortcut properties only. Never resolve, launch or save the shortcut.</summary>
public static class ShortcutReader
{
    public static ShortcutTarget? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)) return null;
        return ReadWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static ShortcutTarget? ReadWindows(string path)
    {
        object? shell = null, link = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024 || (info.Attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) return null;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            if (shell == null) return null;
            link = ((dynamic)shell).CreateShortcut(path);
            string target = ((dynamic)link).TargetPath;
            if (string.IsNullOrWhiteSpace(target)) return null;
            string iconLocation;
            try { iconLocation = ((dynamic)link).IconLocation ?? ""; }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { iconLocation = ""; }
            target = Environment.ExpandEnvironmentVariables(target);
            if (!Path.IsPathFullyQualified(target) && Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile)
                return new(target, uri.Host, "", "url", IconLocation: iconLocation);
            if (Uri.TryCreate(target, UriKind.Absolute, out var fileUri) && fileUri.IsFile) target = fileUri.LocalPath;
            var name = Path.GetFileName(target.TrimEnd('\\', '/'));
            var result = new ShortcutTarget(target, name, Path.GetExtension(name).ToLowerInvariant(), "unknown",
                Arguments: info.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? (string)((dynamic)link).Arguments : "",
                WorkingDirectory: info.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? (string)((dynamic)link).WorkingDirectory : "",
                IconLocation: iconLocation);
            // A network or cloud target must not cause network access / hydration during a scan.
            if (!Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal)) return result;
            var targetInfo = new FileInfo(target);
            if (!targetInfo.Exists && !Directory.Exists(target)) return result;
            var attributes = targetInfo.Attributes;
            if ((attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) return result;
            if ((attributes & FileAttributes.Directory) != 0) return result with { Kind = "folder", Extension = "" };
            result = result with { Kind = "file", Size = targetInfo.Length };
            if (result.Extension is ".exe" or ".dll")
            {
                var version = FileVersionInfo.GetVersionInfo(target);
                result = result with { Description = version.FileDescription ?? "", Product = version.ProductName ?? "", Company = version.CompanyName ?? "" };
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException
            or System.Security.SecurityException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
        finally
        {
            if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
