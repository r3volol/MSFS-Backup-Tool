using System.Text;

namespace MsfsBackup.Core;

/// <summary>Writes timestamped lines to one or more log files and raises an event per line.</summary>
public sealed class Logger : IDisposable
{
    readonly object _lock = new();
    readonly List<StreamWriter> _files = new();

    public event Action<string>? LineWritten;

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MSFS Backup Tool", "Logs");

    public string? MainLogPath { get; private set; }
    public int Warnings { get; private set; }
    public int Errors { get; private set; }

    public static Logger CreateDefault()
    {
        var log = new Logger();
        try
        {
            Directory.CreateDirectory(LogDirectory);
            log.MainLogPath = Path.Combine(LogDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}.log");
            log.AddFile(log.MainLogPath);
        }
        catch (IOException) { /* logging to file is best effort */ }
        return log;
    }

    /// <summary>Also write to <paramref name="path"/> until the returned token is disposed.</summary>
    public IDisposable AddFile(string path)
    {
        var w = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        lock (_lock) _files.Add(w);
        return new Detach(this, w);
    }

    public void ResetCounts() { Warnings = 0; Errors = 0; }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) { Warnings++; Write("WARN", message); }
    public void Error(string message) { Errors++; Write("ERROR", message); }

    void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        lock (_lock)
        {
            foreach (var w in _files)
            {
                try { w.WriteLine(line); } catch (IOException) { }
            }
        }
        LineWritten?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var w in _files) w.Dispose();
            _files.Clear();
        }
    }

    sealed class Detach : IDisposable
    {
        readonly Logger _owner;
        readonly StreamWriter _writer;
        public Detach(Logger owner, StreamWriter writer) { _owner = owner; _writer = writer; }

        public void Dispose()
        {
            lock (_owner._lock) _owner._files.Remove(_writer);
            _writer.Dispose();
        }
    }
}
