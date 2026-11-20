using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YTDownloadTool.Core.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB per daily file
    private const int RetentionDays = 7;

    private readonly string _logsDirectory;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _writeLock = new();
    private bool _disposed;

    public RollingFileLoggerProvider(string logsDirectory, LogLevel minLevel = LogLevel.Information)
    {
        _logsDirectory = logsDirectory;
        _minLevel = minLevel;
        Directory.CreateDirectory(_logsDirectory);
        PruneOldLogs();
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(this, name, _minLevel));
    }

    internal void WriteEntry(LogLevel level, string category, string message, Exception? exception)
    {
        if (_disposed)
            return;

        var redactedMessage = SensitiveDataRedactor.Redact(message);
        var sb = new StringBuilder(256);
        sb.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'"));
        sb.Append(" [").Append(ShortLevel(level)).Append("] ");
        sb.Append('[').Append(category).Append("] ");
        sb.AppendLine(redactedMessage);

        if (exception is not null)
        {
            sb.AppendLine(SensitiveDataRedactor.Redact(exception.ToString()));
        }

        var line = sb.ToString();

        lock (_writeLock)
        {
            if (_disposed)
                return;

            try
            {
                Directory.CreateDirectory(_logsDirectory);
                var filePath = GetCurrentLogFilePath();
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the application.
            }
        }
    }

    private string GetCurrentLogFilePath()
    {
        var dateStamp = DateTime.UtcNow.ToString("yyyyMMdd");
        var basePath = Path.Combine(_logsDirectory, $"ytdownloadtool-{dateStamp}.log");
        if (!File.Exists(basePath) || new FileInfo(basePath).Length < MaxFileSizeBytes)
            return basePath;

        for (var i = 1; i <= 50; i++)
        {
            var rolled = Path.Combine(_logsDirectory, $"ytdownloadtool-{dateStamp}.{i}.log");
            if (!File.Exists(rolled) || new FileInfo(rolled).Length < MaxFileSizeBytes)
                return rolled;
        }

        return basePath;
    }

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_logsDirectory, "ytdownloadtool-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "INF"
    };

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
                return;

            provider.WriteEntry(logLevel, category, message, exception);
        }
    }
}
