namespace MsfsBackup.Core;

public sealed class BackupResult
{
    public string BackupFolder { get; init; } = "";
    public int FilesCopied { get; set; }
    public int EntriesMovedDirectly { get; set; }
    public long Bytes { get; set; }
    public int LinksRecorded { get; set; }
}

/// <summary>Backup (copy out) and Move (copy out, verify, then remove the originals).</summary>
public sealed class BackupEngine
{
    readonly Logger _log;
    readonly ProgressTracker _progress;
    readonly CancellationToken _ct;

    public BackupEngine(Logger log, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        _log = log;
        _progress = new ProgressTracker(progress);
        _ct = ct;
    }

    sealed class Job
    {
        public required SimLocation Location { get; init; }
        public required ScanResult Scan { get; init; }
        public required ManifestLocation Manifest { get; init; }
        public required string DataRoot { get; init; }
        public bool SameVolume { get; init; }
        /// <summary>Top-level entries moved by a simple rename (Move on the same drive).</summary>
        public HashSet<string> Renamed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public BackupResult Run(IReadOnlyList<SimLocation> locations, string destinationRoot, bool move)
    {
        SimProcessGuard.EnsureNotRunning();
        if (locations.Count == 0)
            throw new UserFacingException("No folders selected.");
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new UserFacingException("Choose where to save the backup.");

        destinationRoot = PathUtil.Normalize(destinationRoot);
        foreach (var l in locations)
        {
            if (!Directory.Exists(l.Path))
                throw new UserFacingException($"Folder not found: {l.Path}");
            if (PathUtil.IsSameOrUnder(destinationRoot, l.Path))
                throw new UserFacingException($"The backup can't be saved inside a folder being backed up ({l.Path}).");
        }

        string verb = move ? "Move" : "Backup";
        _log.ResetCounts();
        _log.Info($"=== {verb} started: {locations.Count} folder(s) to {destinationRoot} ===");

        // 1. Scan
        _progress.Start("Scanning folders", 0, 0);
        var scans = new List<(SimLocation Loc, ScanResult Scan)>();
        foreach (var l in locations)
        {
            var s = Scanner.Scan(l.Path, l.Filter, _ct, _progress.Item);
            _log.Info($"{l.DisplayName}: {s.TopLevelEntries.Count} items, {s.Files.Count:N0} files, {Fmt.Bytes(s.TotalBytes)}, {s.Links.Count} links");
            scans.Add((l, s));
        }

        // 2. Backup folder + manifest (marked incomplete until verified)
        Directory.CreateDirectory(destinationRoot);
        string backupFolder = PathUtil.UniqueFolder(Path.Combine(destinationRoot, $"MSFS Backup {DateTime.Now:yyyy-MM-dd HHmm}"));
        Directory.CreateDirectory(backupFolder);
        using var folderLog = _log.AddFile(Path.Combine(backupFolder, "backup.log"));
        _log.Info($"Backup folder: {backupFolder}");

        var manifest = new BackupManifest
        {
            Operation = verb,
            CreatedUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            ToolVersion = typeof(BackupEngine).Assembly.GetName().Version?.ToString(3) ?? "",
        };
        var jobs = new List<Job>();
        foreach (var (l, s) in scans)
        {
            var ml = new ManifestLocation
            {
                Id = l.Id,
                MatchKey = l.MatchKey,
                Sim = l.Sim,
                Store = l.Store,
                Kind = l.Kind,
                Filter = l.Filter,
                OriginalPath = l.Path,
                DataFolder = Path.Combine($"{l.Sim}-{Names.StoreShort(l.Store)}", l.Kind.ToString()),
                FileCount = s.Files.Count,
                TotalBytes = s.TotalBytes,
                Entries = s.TopLevelEntries,
                Links = s.Links,
            };
            manifest.Locations.Add(ml);
            jobs.Add(new Job
            {
                Location = l,
                Scan = s,
                Manifest = ml,
                DataRoot = Path.Combine(backupFolder, ml.DataFolder),
                SameVolume = move && PathUtil.SameVolume(l.Path, backupFolder),
            });
        }
        manifest.Save(backupFolder);

        long bytesToCopy = jobs.Where(j => !j.SameVolume).Sum(j => j.Scan.TotalBytes);
        DiskSpace.Ensure(backupFolder, bytesToCopy);

        // 3. Transfer
        var result = new BackupResult { BackupFolder = backupFolder, LinksRecorded = jobs.Sum(j => j.Scan.Links.Count) };
        var copied = new List<(string Src, string Dst, FileItem Item)>();
        _progress.Start(move ? "Moving files" : "Copying files", jobs.Sum(j => j.Scan.Files.Count), jobs.Sum(j => j.Scan.TotalBytes));

        foreach (var job in jobs)
        {
            Directory.CreateDirectory(job.DataRoot);
            if (job.SameVolume)
                RenameEntries(job, result);

            foreach (var dir in job.Scan.Directories.Where(d => !job.Renamed.Contains(PathUtil.TopOf(d))))
                Directory.CreateDirectory(Path.Combine(job.DataRoot, dir));

            foreach (var f in job.Scan.Files.Where(f => !job.Renamed.Contains(PathUtil.TopOf(f.RelativePath))))
            {
                _ct.ThrowIfCancellationRequested();
                string src = Path.Combine(job.Location.Path, f.RelativePath);
                string dst = Path.Combine(job.DataRoot, f.RelativePath);
                try
                {
                    FileOps.CopyFile(src, dst, _progress.AddBytes, _ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new UserFacingException($"Couldn't copy \"{src}\": {ex.Message}", ex);
                }
                copied.Add((src, dst, f));
                result.Bytes += f.Length;
                _progress.FileDone(f.RelativePath);
            }
        }
        result.FilesCopied = copied.Count;

        // 4. Verify
        _progress.Start("Verifying copies", copied.Count, 0);
        int bad = 0;
        foreach (var c in copied)
        {
            _ct.ThrowIfCancellationRequested();
            if (!FileOps.Matches(c.Dst, c.Item))
            {
                bad++;
                _log.Error($"Verification failed: {c.Dst}");
            }
            _progress.FileDone(c.Item.RelativePath);
        }
        if (bad > 0)
            throw new UserFacingException($"{bad} file(s) failed verification, so the backup is incomplete" +
                (move ? " and nothing was deleted from the sim folders." : ".") + " See the log for details.");

        manifest.Complete = true;
        manifest.Save(backupFolder);
        _log.Info($"Copied and verified {copied.Count:N0} files ({Fmt.Bytes(result.Bytes)}); recorded {result.LinksRecorded} links.");

        // 5. Move: remove the originals (only what was scanned and verified)
        if (move)
            RemoveOriginals(jobs, copied);

        _progress.Finish();
        _log.Info($"=== {verb} finished: {backupFolder} ===");
        return result;
    }

    void RenameEntries(Job job, BackupResult result)
    {
        var topLevelLinks = new HashSet<string>(
            job.Scan.Links.Where(k => !k.RelativePath.Contains('\\')).Select(k => k.RelativePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in job.Scan.TopLevelEntries)
        {
            _ct.ThrowIfCancellationRequested();
            if (topLevelLinks.Contains(entry))
                continue; // links are recorded in the manifest and removed afterwards
            string src = Path.Combine(job.Location.Path, entry);
            string dst = Path.Combine(job.DataRoot, entry);
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
                _log.Warn($"Couldn't move \"{entry}\" directly ({ex.Message}). Copying it instead.");
            }
        }
        if (job.Renamed.Count > 0)
            _log.Info($"{job.Location.DisplayName}: moved {job.Renamed.Count} item(s) directly (same drive).");
    }

    void RemoveOriginals(List<Job> jobs, List<(string Src, string Dst, FileItem Item)> copied)
    {
        _progress.Start("Removing originals", copied.Count, 0);
        foreach (var c in copied)
        {
            _ct.ThrowIfCancellationRequested();
            try { FileOps.DeleteFile(c.Src); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Couldn't delete \"{c.Src}\": {ex.Message}");
            }
            _progress.FileDone(c.Item.RelativePath);
        }

        foreach (var job in jobs)
        {
            foreach (var link in job.Scan.Links)
            {
                string path = Path.Combine(job.Location.Path, link.RelativePath);
                if (!PathUtil.Exists(path))
                    continue; // travelled with a renamed folder
                try { FileOps.DeleteLink(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"Couldn't remove link \"{path}\": {ex.Message}");
                }
            }

            // Deepest folders first; a folder that still has unexpected content is left alone.
            foreach (var dir in job.Scan.Directories
                         .Where(d => !job.Renamed.Contains(PathUtil.TopOf(d)))
                         .OrderByDescending(d => d.Count(ch => ch == '\\')))
            {
                string path = Path.Combine(job.Location.Path, dir);
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"Left \"{path}\" in place: {ex.Message}");
                }
            }
            _log.Info($"{job.Location.DisplayName}: originals removed.");
        }
    }
}
