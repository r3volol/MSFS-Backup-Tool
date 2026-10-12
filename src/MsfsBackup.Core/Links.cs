using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace MsfsBackup.Core;

public static class LinkInspector
{
    /// <summary>Returns the link definition if <paramref name="fsi"/> is a junction or symbolic link, else null.</summary>
    public static LinkRecord? TryRead(FileSystemInfo fsi, string relativePath)
    {
        if (!fsi.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;

        LinkType type;
        switch (NativeMethods.GetReparseTag(fsi.FullName))
        {
            case NativeMethods.IO_REPARSE_TAG_MOUNT_POINT: type = LinkType.Junction; break;
            case NativeMethods.IO_REPARSE_TAG_SYMLINK: type = LinkType.SymbolicLink; break;
            default: return null; // OneDrive placeholders, dedup files etc. are treated as ordinary files
        }

        string? raw = fsi.LinkTarget;
        if (string.IsNullOrEmpty(raw))
            return null;
        raw = NormalizeTarget(raw);

        return new LinkRecord
        {
            RelativePath = relativePath,
            Type = type,
            IsDirectory = fsi.Attributes.HasFlag(FileAttributes.Directory),
            Target = raw,
            ResolvedTarget = Resolve(fsi.FullName, raw),
        };
    }

    public static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                && NativeMethods.GetReparseTag(path) is NativeMethods.IO_REPARSE_TAG_MOUNT_POINT or NativeMethods.IO_REPARSE_TAG_SYMLINK;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>True if <paramref name="linkPath"/> is a link whose target resolves to <paramref name="resolvedTarget"/>.</summary>
    public static bool PointsTo(string linkPath, string resolvedTarget)
    {
        if (!IsLink(linkPath))
            return false;
        FileSystemInfo fsi = File.GetAttributes(linkPath).HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(linkPath) : new FileInfo(linkPath);
        var rec = TryRead(fsi, "");
        return rec != null && PathUtil.SamePath(rec.ResolvedTarget, resolvedTarget);
    }

    /// <summary>Absolute target of a link located at <paramref name="linkPath"/>.</summary>
    public static string Resolve(string linkPath, string target) =>
        Path.IsPathFullyQualified(target)
            ? target
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));

    static string NormalizeTarget(string t)
    {
        foreach (var prefix in new[] { @"\??\", @"\\?\" })
        {
            if (!t.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            string rest = t[prefix.Length..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + rest[4..];
            if (rest.Length >= 2 && rest[1] == ':')
                return rest;
        }
        return t;
    }
}

public static class LinkCreator
{
    /// <summary>Hidden command-line verb used to create links in an elevated copy of the tool.</summary>
    public const string HelperVerb = "__create-links";

    const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    const int ERROR_CANCELLED = 1223;

    public static LinkResult TryCreate(LinkRequest r)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r.LinkPath)!);
            if (r.Type == LinkType.Junction)
                NativeMethods.CreateJunction(r.LinkPath, r.Target);
            else if (r.IsDirectory)
                Directory.CreateSymbolicLink(r.LinkPath, r.Target);
            else
                File.CreateSymbolicLink(r.LinkPath, r.Target);
            return new LinkResult { LinkPath = r.LinkPath, Success = true };
        }
        catch (Exception ex) when (IsPrivilegeError(ex))
        {
            return new LinkResult { LinkPath = r.LinkPath, NeedsElevation = true, Error = ex.Message };
        }
        catch (Exception ex)
        {
            return new LinkResult { LinkPath = r.LinkPath, Error = ex.Message };
        }
    }

    static bool IsPrivilegeError(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex.HResult & 0xFFFF) == ERROR_PRIVILEGE_NOT_HELD
        || (ex is Win32Exception w && w.NativeErrorCode == ERROR_PRIVILEGE_NOT_HELD);

    /// <summary>Runs this program again as administrator (one UAC prompt) to create the given links.</summary>
    public static List<LinkResult> CreateElevated(IReadOnlyList<LinkRequest> requests)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MSFS Backup Tool");
        Directory.CreateDirectory(dir);
        string id = Guid.NewGuid().ToString("N");
        string inPath = Path.Combine(dir, $"links-{id}.json");
        string outPath = Path.Combine(dir, $"links-{id}.result.json");
        File.WriteAllText(inPath, JsonSerializer.Serialize(requests.ToList(), JsonContext.Default.ListLinkRequest));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{HelperVerb} \"{inPath}\" \"{outPath}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using (var p = Process.Start(psi)!)
                p.WaitForExit();

            if (!File.Exists(outPath))
                return Failed(requests, "The administrator helper didn't produce a result.");
            return JsonSerializer.Deserialize(File.ReadAllText(outPath), JsonContext.Default.ListLinkResult)
                ?? Failed(requests, "The administrator helper's result was unreadable.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return Failed(requests, "Administrator permission was declined.");
        }
        finally
        {
            try { File.Delete(inPath); } catch { }
            try { File.Delete(outPath); } catch { }
        }
    }

    /// <summary>Entry point for the elevated helper process.</summary>
    public static int RunHelper(string inPath, string outPath)
    {
        var requests = JsonSerializer.Deserialize(File.ReadAllText(inPath), JsonContext.Default.ListLinkRequest) ?? new();
        var results = requests.Select(TryCreate).ToList();
        File.WriteAllText(outPath, JsonSerializer.Serialize(results, JsonContext.Default.ListLinkResult));
        return 0;
    }

    static List<LinkResult> Failed(IReadOnlyList<LinkRequest> requests, string error) =>
        requests.Select(r => new LinkResult { LinkPath = r.LinkPath, Error = error }).ToList();
}
