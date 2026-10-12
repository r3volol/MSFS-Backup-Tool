using System.Buffers;
using System.Diagnostics;

namespace MsfsBackup.Core;

public static class PathUtil
{
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length > 3 ? Path.TrimEndingDirectorySeparator(full) : full;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsSameOrUnder(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if anything (file, folder or link, even a broken one) exists at the path. Never follows links.</summary>
    public static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>First segment of a relative path, i.e. the add-on folder it belongs to.</summary>
    public static string TopOf(string relativePath)
    {
        int i = relativePath.IndexOf('\\');
        return i < 0 ? relativePath : relativePath[..i];
    }

    public static string UniqueFolder(string path)
    {
        if (!Exists(path))
            return path;
        for (int n = 2; ; n++)
        {
            string p = $"{path} ({n})";
            if (!Exists(p))
                return p;
        }
    }
}

public static class FileOps
{
    const long ChunkedCopyThreshold = 16L * 1024 * 1024;
    const int BufferSize = 4 * 1024 * 1024;
    static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2); // FAT/exFAT store times at 2 s resolution

    /// <summary>Copies one file, keeping its timestamps and attributes. Reports bytes as they're written.</summary>
    public static void CopyFile(string src, string dst, Action<long> onBytes, CancellationToken ct)
    {
        var info = new FileInfo(src);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst))
            ClearReadOnly(dst);

        if (info.Length < ChunkedCopyThreshold)
        {
            File.Copy(src, dst, overwrite: true);
            onBytes(info.Length);
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                output.SetLength(info.Length);
                int n;
                while ((n = input.Read(buffer, 0, BufferSize)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    onBytes(n);
                }
            }
            File.SetCreationTimeUtc(dst, info.CreationTimeUtc);
            File.SetLastWriteTimeUtc(dst, info.LastWriteTimeUtc);
            File.SetAttributes(dst, info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
        }
        catch
        {
            try { File.Delete(dst); } catch { }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Verification: same size and last-write time (within 2 s).</summary>
    public static bool Matches(string path, FileItem expected)
    {
        var fi = new FileInfo(path);
        return fi.Exists
            && fi.Length == expected.Length
            && (fi.LastWriteTimeUtc - expected.LastWriteUtc).Duration() <= TimeTolerance;
    }

    public static bool IsNewer(DateTime a, DateTime b) => a - b > TimeTolerance;

    public static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;
        ClearReadOnly(path);
        File.Delete(path);
    }

    /// <summary>Removes a junction or symbolic link itself. Never touches what it points to.</summary>
    public static void DeleteLink(string path)
    {
        var attr = File.GetAttributes(path);
        if (!attr.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"Refusing to delete \"{path}\": it isn't a link.");
        if (attr.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        if (attr.HasFlag(FileAttributes.Directory))
            Directory.Delete(path, recursive: false);
        else
            File.Delete(path);
    }

    static void ClearReadOnly(string path)
    {
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
    }
}

public static class DiskSpace
{
    const long Margin = 1L * 1024 * 1024 * 1024;

    public static void Ensure(string folder, long bytesNeeded)
    {
        if (bytesNeeded <= 0)
            return;
        DriveInfo drive;
        try { drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!); }
        catch (ArgumentException) { return; } // network path: can't check
        long free = drive.AvailableFreeSpace;
        if (free < bytesNeeded + Margin)
            throw new UserFacingException(
                $"Not enough free space on {drive.Name}: need {Fmt.Bytes(bytesNeeded + Margin)} (incl. 1 GB margin), only {Fmt.Bytes(free)} free.");
    }
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{b} B" : $"{v:0.0} {units[u]}";
    }
}

public static class SimProcessGuard
{
    static readonly string[] ProcessNames = { "FlightSimulator", "FlightSimulator2024" };

    /// <summary>Name of the running sim executable, or null.</summary>
    public static string? RunningSim()
    {
        foreach (var name in ProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0)
                    return name + ".exe";
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return null;
    }

    /// <summary>Test hook only.</summary>
    internal static bool Disabled { get; set; }

    public static void EnsureNotRunning()
    {
        if (!Disabled && RunningSim() is string exe)
            throw new UserFacingException($"Microsoft Flight Simulator is running ({exe}). Close the sim first.");
    }
}

/// <summary>Turns many small progress updates into at most ~10 reports a second.</summary>
internal sealed class ProgressTracker
{
    readonly IProgress<ProgressInfo>? _sink;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastReport = -1000;
    string _phase = "";
    string? _item;
    long _bytesDone, _bytesTotal, _filesDone, _filesTotal;

    public ProgressTracker(IProgress<ProgressInfo>? sink) => _sink = sink;

    public void Start(string phase, long filesTotal, long bytesTotal)
    {
        _phase = phase;
        _filesTotal = filesTotal;
        _bytesTotal = bytesTotal;
        _filesDone = _bytesDone = 0;
        _item = null;
        Report(force: true);
    }

    public void Item(string item) { _item = item; Report(); }
    public void AddBytes(long n) { _bytesDone += n; Report(); }
    public void FileDone(string item) { _filesDone++; _item = item; Report(); }
    public void AddDone(long files, long bytes, string item) { _filesDone += files; _bytesDone += bytes; _item = item; Report(); }
    public void Finish() => Report(force: true);

    void Report(bool force = false)
    {
        long now = _clock.ElapsedMilliseconds;
        if (!force && now - _lastReport < 100)
            return;
        _lastReport = now;
        _sink?.Report(new ProgressInfo(_phase, _item, _bytesDone, _bytesTotal, _filesDone, _filesTotal));
    }
}
