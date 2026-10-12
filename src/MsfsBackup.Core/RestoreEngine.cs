namespace MsfsBackup.Core;

public sealed record RestoreTarget(ManifestLocation Location, string TargetPath);

public sealed record ConflictSummary(int Files, int Links);

/// <summary>A link that couldn't be created yet: its target is missing, or it needs admin rights.</summary>
public sealed class PendingLink
{
    public required LinkRecord Record { get; init; }
    public required string LinkPath { get; init; }
    public required string ResolvedTarget { get; init; }

    public LinkRequest ToRequest(string? newTarget = null) => new()
    {
        LinkPath = LinkPath,
        Target = newTarget ?? (Record.Type == LinkType.Junction ? ResolvedTarget : Record.Target),
        Type = Record.Type,
        IsDirectory = Record.IsDirectory,
    };
}

public sealed class RestoreResult
{
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int EntriesMovedDirectly { get; set; }
    public int LinksCreated { get; set; }
    public int LinksAlreadyPresent { get; set; }
    public int LinksSkipped { get; set; }
    public int Errors { get; set; }
    public List<PendingLink> DeadLinks { get; } = new();
    public List<PendingLink> NeedElevation { get; } = new();

    public string Summary() =>
        $"Files: {FilesCopied:N0} restored" +
        (EntriesMovedDirectly > 0 ? $", {EntriesMovedDirectly} folder(s) moved back directly" : "") +
        (FilesSkipped > 0 ? $", {FilesSkipped:N0} skipped (already there)" : "") + ".\n" +
        $"Links: {LinksCreated} created, {LinksAlreadyPresent} already in place" +
        (LinksSkipped > 0 ? $", {LinksSkipped} skipped" : "") +
        (DeadLinks.Count > 0 ? $", {DeadLinks.Count} with a missing target" : "") +
        (NeedElevation.Count > 0 ? $", {NeedElevation.Count} waiting for admin rights" : "") + "." +
        (Errors > 0 ? $"\n{Errors} error(s). See the log for details." : "");
}

public sealed class RestoreEngine
{
    readonly Logger _log;
    readonly ProgressTracker _progress;
    readonly CancellationToken _ct;

    public RestoreEngine(Logger log, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        _log = log;
        _progress = new ProgressTracker(progress);
        _ct = ct;
    }

    sealed class Job
    {
        public required ManifestLocation Location { get; init; }
        public required string DataRoot { get; init; }
        public required string TargetRoot { get; init; }
        public required ScanResult Scan { get; init; }
        public bool SameVolume { get; init; }
        public HashSet<string> Renamed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="askConflict">Called once if anything already exists at the destination. Return null to cancel.</param>
    public RestoreResult Run(string backupFolder, BackupManifest manifest, IReadOnlyList<RestoreTarget> targets,
        ConflictMode mode, bool moveBack, Func<ConflictSummary, ConflictMode?> askConflict)
    {
        SimProcessGuard.EnsureNotRunning();
        if (targets.Count == 0)
            throw new UserFacingException("No folders selected to restore.");

        _log.ResetCounts();
        _log.Info($"=== Restore started from {backupFolder} ===");
        if (!manifest.Complete)
            _log.Warn("This backup never finished; some files may be missing from it.");

        // 1. Scan the backup
        _progress.Start("Scanning backup", 0, 0);
        var jobs = new List<Job>();
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t.TargetPath))
                throw new UserFacingException($"No destination set for {t.Location.DisplayName}.");
            string dataRoot = Path.Combine(backupFolder, t.Location.DataFolder);
            if (!Directory.Exists(dataRoot) && t.Location.FileCount > 0)
                _log.Warn($"{t.Location.DisplayName}: data folder missing from the backup ({dataRoot}).");
            var scan = Scanner.Scan(dataRoot, LocationFilter.All, _ct, _progress.Item);
            string targetRoot = PathUtil.Normalize(t.TargetPath);
            jobs.Add(new Job
            {
                Location = t.Location,
                DataRoot = dataRoot,
                TargetRoot = targetRoot,
                Scan = scan,
                SameVolume = moveBack && PathUtil.SameVolume(dataRoot, targetRoot),
            });
            _log.Info($"{t.Location.DisplayName}: {scan.Files.Count:N0} files, {Fmt.Bytes(scan.TotalBytes)}, {t.Location.Links.Count} links -> {targetRoot}");
        }

        // 2. Conflicts: ask once, apply to everything
        int fileConflicts = jobs.Sum(j => j.Scan.Files.Count(f => PathUtil.Exists(Path.Combine(j.TargetRoot, f.RelativePath))));
        int linkConflicts = jobs.Sum(j => j.Location.Links.Count(l =>
        {
            string lp = Path.Combine(j.TargetRoot, l.RelativePath);
            return PathUtil.Exists(lp) && !LinkInspector.PointsTo(lp, LinkInspector.Resolve(lp, l.Target));
        }));
        if (fileConflicts + linkConflicts > 0)
        {
            if (mode == ConflictMode.Ask)
                mode = askConflict(new ConflictSummary(fileConflicts, linkConflicts)) ?? throw new OperationCanceledException();
            _log.Info($"{fileConflicts:N0} file(s) and {linkConflicts} link(s) already exist; applying: {mode}.");
        }

        // 3. Space
        foreach (var group in jobs.Where(j => !j.SameVolume).GroupBy(j => Path.GetPathRoot(j.TargetRoot), StringComparer.OrdinalIgnoreCase))
            DiskSpace.Ensure(group.First().TargetRoot, group.Sum(j => j.Scan.TotalBytes));

        // 4. Files
        var result = new RestoreResult();
        var copied = new List<(string Dst, FileItem Item)>();
        _progress.Start(moveBack ? "Moving files back" : "Restoring files", jobs.Sum(j => j.Scan.Files.Count), jobs.Sum(j => j.Scan.TotalBytes));
        foreach (var job in jobs)
        {
            Directory.CreateDirectory(job.TargetRoot);
            if (job.SameVolume)
                RenameEntries(job, result);

            foreach (var dir in job.Scan.Directories.Where(d => !job.Renamed.Contains(PathUtil.TopOf(d))))
            {
                try { Directory.CreateDirectory(Path.Combine(job.TargetRoot, dir)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Errors++;
                    _log.Error($"Couldn't create folder \"{Path.Combine(job.TargetRoot, dir)}\": {ex.Message}");
                }
            }

            foreach (var f in job.Scan.Files.Where(f => !job.Renamed.Contains(PathUtil.TopOf(f.RelativePath))))
            {
                _ct.ThrowIfCancellationRequested();
                string src = Path.Combine(job.DataRoot, f.RelativePath);
                string dst = Path.Combine(job.TargetRoot, f.RelativePath);
                if (PathUtil.Exists(dst) && !ShouldOverwrite(dst, f, mode))
                {
                    result.FilesSkipped++;
                    _progress.AddDone(1, f.Length, f.RelativePath);
                    continue;
                }
                try
                {
                    FileOps.CopyFile(src, dst, _progress.AddBytes, _ct);
                    copied.Add((dst, f));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Errors++;
                    _log.Error($"Couldn't restore \"{dst}\": {ex.Message}");
                }
                _progress.FileDone(f.RelativePath);
            }
        }
        result.FilesCopied = copied.Count;

        // 5. Verify
        _progress.Start("Verifying", copied.Count, 0);
        foreach (var (dst, item) in copied)
        {
            _ct.ThrowIfCancellationRequested();
            if (!FileOps.Matches(dst, item))
            {
                result.Errors++;
                _log.Error($"Verification failed: {dst}");
            }
            _progress.FileDone(item.RelativePath);
        }

        // 6. Links
        _progress.Start("Recreating links", jobs.Sum(j => j.Location.Links.Count), 0);
        foreach (var job in jobs)
            foreach (var link in job.Location.Links)
            {
                RestoreLink(job, link, mode, result);
                _progress.FileDone(link.RelativePath);
            }

        _progress.Finish();
        _log.Info(result.Summary().Replace('\n', ' '));
        _log.Info("=== Restore finished ===");
        return result;
    }

    static bool ShouldOverwrite(string dst, FileItem backup, ConflictMode mode) => mode switch
    {
        ConflictMode.Overwrite => true,
        ConflictMode.KeepNewer => File.Exists(dst) && FileOps.IsNewer(backup.LastWriteUtc, File.GetLastWriteTimeUtc(dst)),
        _ => false,
    };

    void RenameEntries(Job job, RestoreResult result)
    {
        foreach (var entry in job.Scan.TopLevelEntries)
        {
            _ct.ThrowIfCancellationRequested();
            string src = Path.Combine(job.DataRoot, entry);
            string dst = Path.Combine(job.TargetRoot, entry);
            if (PathUtil.Exists(dst) || LinkInspector.IsLink(src))
                continue; // merge file by file instead
            try
            {
                if (Directory.Exists(src))
                    Directory.Move(src, dst);
                else
                    File.Move(src, dst);
                job.Renamed.Add(entry);
                result.EntriesMovedDirectly++;
                var (files, bytes) = job.Scan.PerEntry[entry];
                _progress.AddDone(files, bytes, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Couldn't move \"{entry}\" back directly ({ex.Message}). Copying it instead.");
            }
        }
    }

    void RestoreLink(Job job, LinkRecord link, ConflictMode mode, RestoreResult result)
    {
        string linkPath = Path.Combine(job.TargetRoot, link.RelativePath);
        var pending = new PendingLink
        {
            Record = link,
            LinkPath = linkPath,
            ResolvedTarget = LinkInspector.Resolve(linkPath, link.Target),
        };

        if (PathUtil.Exists(linkPath))
        {
            if (LinkInspector.PointsTo(linkPath, pending.ResolvedTarget))
            {
                result.LinksAlreadyPresent++;
                return;
            }
            if (!LinkInspector.IsLink(linkPath))
            {
                result.LinksSkipped++;
                _log.Warn($"Link \"{link.RelativePath}\" not recreated: a real folder or file is already there.");
                return;
            }
            if (mode != ConflictMode.Overwrite)
            {
                result.LinksSkipped++;
                _log.Info($"Link \"{link.RelativePath}\" skipped: a different link is already there.");
                return;
            }
            FileOps.DeleteLink(linkPath);
        }

        if (!PathUtil.Exists(pending.ResolvedTarget))
        {
            result.DeadLinks.Add(pending);
            _log.Warn($"Link \"{link.RelativePath}\": target is missing ({pending.ResolvedTarget}).");
            return;
        }

        var r = LinkCreator.TryCreate(pending.ToRequest());
        if (r.Success)
            result.LinksCreated++;
        else if (r.NeedsElevation)
            result.NeedElevation.Add(pending);
        else
        {
            result.Errors++;
            _log.Error($"Couldn't create link \"{linkPath}\": {r.Error}");
        }
    }
}
