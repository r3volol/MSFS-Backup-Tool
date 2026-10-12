using MsfsBackup.Core;

namespace MsfsBackup.Cli;

static class Program
{
    const string Help = """
        MSFS Backup Tool (command line)

        Usage:
          msfsbackup_cli list
          msfsbackup_cli backup  --dest <folder> [--folders <list>]
          msfsbackup_cli move    --dest <folder> [--folders <list>] [--yes]
          msfsbackup_cli info    --from <backup folder>
          msfsbackup_cli restore --from <backup folder> [--folders <list>] [--target <key>=<path>]...
                                 [--on-conflict overwrite|skip|newer] [--move-back] [--elevate]

        Commands:
          list     Show the sim folders found on this PC.
          backup   Copy the folders into a new backup. The sim folders are left as they are.
          move     Copy the folders, check every file, then delete the originals from the sim folders.
                   Nothing is deleted if any file fails the check.
          info     Show what a backup contains and where each folder would be restored.
          restore  Copy (or with --move-back, move) a backup into the sim folders and recreate links.

        Options:
          --dest         Folder to create the backup in ("MSFS Backup <date>" is created inside it).
          --from         A backup folder (the one containing manifest.json).
          --folders      Comma-separated folders to include. Default: all. Each item can be a full id
                         (MSFS2024-Steam/Community), a sim/folder key (MSFS2024/Community) or a folder
                         kind (Community, Community2024, WasmMsfs2020, WasmMsfs2024, WorkFolders).
          --target       Restore a folder somewhere other than the detected/original location. Repeatable.
          --on-conflict  What to do with files/links that already exist. Without it you'll be asked.
          --move-back    Move files out of the backup instead of copying (fast on the same drive).
          --elevate      Accept the admin prompt for symbolic links without asking.
          --yes          Don't ask for confirmation before a move.

        Exit codes: 0 ok, 1 unexpected error, 2 error, 3 cancelled, 4 finished with problems.
        Logs: %LOCALAPPDATA%\MSFS Backup Tool\Logs
        """;

    static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == LinkCreator.HelperVerb)
            return LinkCreator.RunHelper(args[1], args[2]);
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            Console.WriteLine(Help);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var log = Logger.CreateDefault();
        log.LineWritten += line =>
        {
            if (line.Contains("[WARN]") || line.Contains("[ERROR]") || line.Contains("==="))
                ConsoleProgress.WriteLine(line);
        };

        try
        {
            var opts = Options.Parse(args.Skip(1));
            return args[0].ToLowerInvariant() switch
            {
                "list" => List(),
                "backup" => Backup(opts, log, cts.Token, move: false),
                "move" => Backup(opts, log, cts.Token, move: true),
                "info" => Info(opts),
                "restore" => Restore(opts, log, cts.Token),
                _ => throw new UserFacingException($"Unknown command \"{args[0]}\". Run \"msfsbackup_cli help\"."),
            };
        }
        catch (UserFacingException ex)
        {
            ConsoleProgress.EndLine();
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            ConsoleProgress.EndLine();
            Console.Error.WriteLine("Cancelled.");
            return 3;
        }
        catch (Exception ex)
        {
            ConsoleProgress.EndLine();
            log.Error(ex.ToString());
            Console.Error.WriteLine($"Unexpected error: {ex.Message}\nLog: {log.MainLogPath}");
            return 1;
        }
    }

    static int List()
    {
        var installs = SimLocator.Detect();
        if (installs.Count == 0)
        {
            Console.WriteLine("No MSFS 2020 or 2024 installs found (no UserCfg.opt in the usual places).");
            return 0;
        }
        foreach (var inst in installs)
        {
            Console.WriteLine($"{inst.DisplayName}   [{inst.ConfigFile}]");
            foreach (var l in inst.Locations)
            {
                var (items, links) = Scanner.CountTopLevel(l.Path, l.Filter);
                string state = Directory.Exists(l.Path) ? $"{items} items, {links} links" : "not found";
                Console.WriteLine($"  {l.Id,-36} {state,-22} {l.Path}");
            }
        }
        return 0;
    }

    static int Backup(Options o, Logger log, CancellationToken ct, bool move)
    {
        string dest = o.Require("dest");
        var all = SimLocator.Detect().SelectMany(i => i.Locations).ToList();
        var selected = o.Folders == null
            ? all.Where(l => Directory.Exists(l.Path)).ToList()
            : all.Where(l => o.Folders.Any(f => Matches(f, l.Id, l.MatchKey, l.Kind))).ToList();
        if (selected.Count == 0)
            throw new UserFacingException("No matching folders found. Run \"msfsbackup_cli list\" to see what was detected.");

        Console.WriteLine((move ? "Moving" : "Backing up") + ":");
        foreach (var l in selected)
            Console.WriteLine($"  {l.DisplayName}  ({l.Path})");

        if (move && !o.Yes && !Confirm("Move these folders out of the sim? The originals are deleted once every copied file passes the check. (Use \"backup\" to keep them.)"))
            return 3;

        var result = new BackupEngine(log, new ConsoleProgress(), ct).Run(selected, dest, move);
        ConsoleProgress.EndLine();
        Console.WriteLine($"Done. {result.FilesCopied:N0} files copied ({Fmt.Bytes(result.Bytes)})" +
            (result.EntriesMovedDirectly > 0 ? $", {result.EntriesMovedDirectly} folders moved directly" : "") +
            $", {result.LinksRecorded} links recorded.");
        Console.WriteLine($"Backup: {result.BackupFolder}");
        return log.Errors + log.Warnings > 0 ? 4 : 0;
    }

    static int Info(Options o)
    {
        string from = o.Require("from");
        var m = BackupManifest.Load(from);
        Console.WriteLine($"{m.Operation} made {m.CreatedUtc.ToLocalTime():g} on {m.MachineName} (tool {m.ToolVersion})" +
            (m.Complete ? "" : "  ** INCOMPLETE **"));
        var installs = SimLocator.Detect();
        foreach (var l in m.Locations)
        {
            Console.WriteLine($"  {l.Id,-36} {l.FileCount,8:N0} files {Fmt.Bytes(l.TotalBytes),10} {l.Links.Count,4} links");
            Console.WriteLine($"      from: {l.OriginalPath}");
            Console.WriteLine($"      to:   {SimLocator.DefaultRestoreTarget(l, installs)}");
        }
        return 0;
    }

    static int Restore(Options o, Logger log, CancellationToken ct)
    {
        string from = o.Require("from");
        var manifest = BackupManifest.Load(from);
        var installs = SimLocator.Detect();

        var targets = new List<RestoreTarget>();
        foreach (var l in manifest.Locations)
        {
            if (o.Folders != null && !o.Folders.Any(f => Matches(f, l.Id, l.MatchKey, l.Kind)))
                continue;
            string target = o.Targets.FirstOrDefault(t => Matches(t.Key, l.Id, l.MatchKey, l.Kind)).Value
                ?? SimLocator.DefaultRestoreTarget(l, installs);
            targets.Add(new RestoreTarget(l, target));
            Console.WriteLine($"  {l.DisplayName}  ->  {target}");
        }
        if (targets.Count == 0)
            throw new UserFacingException("None of the requested folders are in this backup. Run \"msfsbackup_cli info\".");

        var result = new RestoreEngine(log, new ConsoleProgress(), ct)
            .Run(from, manifest, targets, o.OnConflict, o.MoveBack, AskConflict);
        ConsoleProgress.EndLine();

        if (result.NeedElevation.Count > 0)
        {
            if (o.Elevate || Confirm($"{result.NeedElevation.Count} symbolic link(s) need administrator rights to create. Show the admin prompt?"))
            {
                var outcomes = LinkCreator.CreateElevated(result.NeedElevation.Select(p => p.ToRequest()).ToList());
                foreach (var r in outcomes)
                {
                    if (r.Success) result.LinksCreated++;
                    else { result.Errors++; log.Error($"Couldn't create link \"{r.LinkPath}\": {r.Error}"); }
                }
                result.NeedElevation.Clear();
            }
        }

        Console.WriteLine(result.Summary());
        if (result.NeedElevation.Count > 0)
        {
            Console.WriteLine("Links not created (admin rights declined):");
            foreach (var p in result.NeedElevation)
                Console.WriteLine($"  {p.LinkPath}  ->  {p.ResolvedTarget}");
        }
        if (result.DeadLinks.Count > 0)
        {
            Console.WriteLine("Links skipped because their target folder is missing (re-point them in the app, or reinstall those add-ons):");
            foreach (var p in result.DeadLinks)
                Console.WriteLine($"  {p.LinkPath}  ->  {p.ResolvedTarget}");
        }
        bool problems = result.Errors > 0 || result.DeadLinks.Count > 0 || result.NeedElevation.Count > 0;
        return problems ? 4 : 0;
    }

    static ConflictMode? AskConflict(ConflictSummary s)
    {
        ConsoleProgress.EndLine();
        Console.WriteLine($"{s.Files:N0} file(s) and {s.Links} link(s) already exist at the destination.");
        if (Console.IsInputRedirected)
            throw new UserFacingException("Conflicts found. Re-run with --on-conflict overwrite|skip|newer.");
        while (true)
        {
            Console.Write("[O]verwrite with backup, [S]kip existing, keep [N]ewer, [C]ancel? ");
            switch (Console.ReadLine()?.Trim().ToLowerInvariant())
            {
                case "o": return ConflictMode.Overwrite;
                case "s": return ConflictMode.Skip;
                case "n": return ConflictMode.KeepNewer;
                case "c" or null: return null;
            }
        }
    }

    static bool Confirm(string question)
    {
        if (Console.IsInputRedirected)
            throw new UserFacingException($"{question} (No console to ask; pass --yes / --elevate.)");
        Console.Write($"{question} [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    static bool Matches(string token, string id, string matchKey, FolderKind kind) =>
        token.Equals(id, StringComparison.OrdinalIgnoreCase)
        || token.Equals(matchKey, StringComparison.OrdinalIgnoreCase)
        || token.Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase);
}

sealed class Options
{
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public List<string>? Folders { get; private set; }
    public List<KeyValuePair<string, string>> Targets { get; } = new();
    public ConflictMode OnConflict { get; private set; } = ConflictMode.Ask;
    public bool MoveBack { get; private set; }
    public bool Elevate { get; private set; }
    public bool Yes { get; private set; }

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        using var e = args.GetEnumerator();
        string Next(string name) => e.MoveNext() ? e.Current : throw new UserFacingException($"--{name} needs a value.");

        while (e.MoveNext())
        {
            string a = e.Current;
            switch (a.ToLowerInvariant())
            {
                case "--dest": o._values["dest"] = Next("dest"); break;
                case "--from": o._values["from"] = Next("from"); break;
                case "--folders":
                    o.Folders = Next("folders").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "--target":
                    string t = Next("target");
                    int eq = t.IndexOf('=');
                    if (eq <= 0) throw new UserFacingException("--target must look like MSFS2024/Community=D:\\Path");
                    o.Targets.Add(new(t[..eq].Trim(), t[(eq + 1)..].Trim()));
                    break;
                case "--on-conflict":
                    o.OnConflict = Next("on-conflict").ToLowerInvariant() switch
                    {
                        "overwrite" => ConflictMode.Overwrite,
                        "skip" => ConflictMode.Skip,
                        "newer" => ConflictMode.KeepNewer,
                        var v => throw new UserFacingException($"Unknown --on-conflict value \"{v}\"."),
                    };
                    break;
                case "--move-back": o.MoveBack = true; break;
                case "--elevate": o.Elevate = true; break;
                case "--yes" or "-y": o.Yes = true; break;
                default: throw new UserFacingException($"Unknown option \"{a}\".");
            }
        }
        return o;
    }

    public string Require(string name) =>
        _values.TryGetValue(name, out var v) ? v : throw new UserFacingException($"--{name} is required.");
}

/// <summary>Single updating status line.</summary>
sealed class ConsoleProgress : IProgress<ProgressInfo>
{
    static readonly object Lock = new();
    static bool _midLine;
    string? _phase;

    public void Report(ProgressInfo p)
    {
        lock (Lock)
        {
            if (Console.IsOutputRedirected)
            {
                if (p.Phase != _phase) Console.WriteLine(p.Phase + "...");
                _phase = p.Phase;
                return;
            }
            if (p.Phase != _phase && _midLine)
                Console.WriteLine();
            _phase = p.Phase;

            string text = p.Phase;
            if (p.BytesTotal > 0)
                text += $"  {100.0 * p.BytesDone / p.BytesTotal,5:0.0}%  {p.FilesDone:N0}/{p.FilesTotal:N0} files  {Fmt.Bytes(p.BytesDone)} / {Fmt.Bytes(p.BytesTotal)}";
            else if (p.FilesTotal > 0)
                text += $"  {p.FilesDone:N0}/{p.FilesTotal:N0}";
            else if (p.Item != null)
                text += $"  {p.Item}";

            int width = Math.Max(20, SafeWidth() - 1);
            if (text.Length > width) text = text[..width];
            Console.Write("\r" + text.PadRight(width));
            _midLine = true;
        }
    }

    public static void WriteLine(string line)
    {
        lock (Lock)
        {
            if (_midLine) { Console.WriteLine(); _midLine = false; }
            Console.WriteLine(line);
        }
    }

    public static void EndLine()
    {
        lock (Lock)
        {
            if (_midLine) { Console.WriteLine(); _midLine = false; }
        }
    }

    static int SafeWidth()
    {
        try { return Console.WindowWidth; } catch (IOException) { return 100; }
    }
}
