namespace SoftcomSmartProvisioner.Services;

public sealed record AppLogEntry(DateTime Timestamp, string Level, string Source, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss");
}

public sealed class AppLogService
{
    private readonly object _sync = new();
    private readonly List<AppLogEntry> _entries = new();
    private readonly string _logDirectory;
    private readonly string _logFile;

    public AppLogService(string appDataDirectory)
    {
        _logDirectory = Path.Combine(appDataDirectory, "logs");
        Directory.CreateDirectory(_logDirectory);
        _logFile = Path.Combine(_logDirectory, $"smart-provisioner-{DateTime.Now:yyyyMMdd}.log");
    }

    public string LogDirectory => _logDirectory;

    public AppLogEntry Write(string source, string message, string level = "INFO")
    {
        var entry = new AppLogEntry(DateTime.Now, level, source, message);
        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > 600) _entries.RemoveRange(0, _entries.Count - 600);
            File.AppendAllText(_logFile, $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{level}] [{source}] {message}{Environment.NewLine}");
        }
        return entry;
    }

    public IReadOnlyList<AppLogEntry> GetRecent()
    {
        lock (_sync) return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_sync) _entries.Clear();
    }
}
