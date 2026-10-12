namespace MsfsBackup.Core;

public sealed record FileItem(string RelativePath, long Length, DateTime LastWriteUtc);

public sealed class ScanResult
{
    public List<string> TopLevelEntries { get; } = new();
    public List<FileItem> Files { get; } = new();
    /// <summary>Real (non-link) folders, parents before children.</summary>
    public List<string> Directories { get; } = new();
    public List<LinkRecord> Links { get; } = new();
    /// <summary>Links owned by MSFS Addons Linker. Left out of the backup and never touched.</summary>
    public List<LinkRecord> IgnoredLinks { get; } = new();
    public long TotalBytes { get; set; }
    /// <summary>File count and bytes per top-level entry.</summary>
    public Dictionary<string, (long Files, long Bytes)> PerEntry { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Walks a folder tree without ever following junctions or symbolic links.</summary>
public static class Scanner
{
    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,          // include hidden and system files
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    public static ScanResult Scan(string root, LocationFilter filter, CancellationToken ct, Action<string>? onItem = null)
    {
        var result = new ScanResult();
        if (!Directory.Exists(root))
            return result;

        foreach (var entry in TopLevel(root, filter))
        {
            ct.ThrowIfCancellationRequested();
            onItem?.Invoke(entry.Name);
            if (LinkInspector.TryRead(entry, entry.Name) is { } link && AddonsLinker.Owns(link))
            {
                result.IgnoredLinks.Add(link);
                continue;
            }
            result.TopLevelEntries.Add(entry.Name);
            result.PerEntry[entry.Name] = (0, 0);
            Walk(entry, entry.Name, entry.Name, result, ct);
        }
        return result;
    }

    /// <summary>Quick count of what a location holds, for display.</summary>
    /// <remarks>Addons Linker links are counted separately and not included in Items or Links.</remarks>
    public static (int Items, int Links, int AddonsLinker) CountTopLevel(string root, LocationFilter filter)
    {
        if (!Directory.Exists(root))
            return (0, 0, 0);
        int items = 0, links = 0, linker = 0;
        foreach (var e in TopLevel(root, filter))
        {
            var link = LinkInspector.TryRead(e, e.Name);
            if (link != null && AddonsLinker.Owns(link))
            {
                linker++;
                continue;
            }
            items++;
            if (link != null)
                links++;
        }
        return (items, links, linker);
    }

    static IEnumerable<FileSystemInfo> TopLevel(string root, LocationFilter filter)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(root).EnumerateFileSystemInfos("*", Options).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UserFacingException($"Access denied reading \"{root}\".", ex);
        }
        return filter == LocationFilter.WorkFoldersOnly ? entries.Where(IsWorkFolderPackage) : entries;
    }

    /// <summary>MSFS 2020 keeps WASM data in &lt;package&gt;\work. Only those package folders are included.</summary>
    static bool IsWorkFolderPackage(FileSystemInfo e) =>
        e is DirectoryInfo d
        && !d.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && !d.Name.Equals("Community", StringComparison.OrdinalIgnoreCase)
        && !d.Name.Equals("Official", StringComparison.OrdinalIgnoreCase)
        && Directory.Exists(Path.Combine(d.FullName, "work"));

    static void Walk(FileSystemInfo entry, string rel, string top, ScanResult r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var link = LinkInspector.TryRead(entry, rel);
        if (link != null)
        {
            (AddonsLinker.Owns(link) ? r.IgnoredLinks : r.Links).Add(link);
            return;
        }

        if (entry is DirectoryInfo dir)
        {
            r.Directories.Add(rel);
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos("*", Options).ToList();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UserFacingException($"Access denied reading \"{dir.FullName}\".", ex);
            }
            foreach (var child in children)
                Walk(child, Path.Combine(rel, child.Name), top, r, ct);
        }
        else if (entry is FileInfo file)
        {
            r.Files.Add(new FileItem(rel, file.Length, file.LastWriteTimeUtc));
            r.TotalBytes += file.Length;
            var (f, b) = r.PerEntry[top];
            r.PerEntry[top] = (f + 1, b + file.Length);
        }
    }
}
