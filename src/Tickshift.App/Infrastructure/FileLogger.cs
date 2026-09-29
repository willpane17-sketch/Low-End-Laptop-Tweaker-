using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Tickshift.Security;

namespace Tickshift.App.Infrastructure;

/// <summary>
/// Structured, bounded, privacy-preserving file logging. One file per day, each capped in size, old files
/// deleted after the retention period. Every line passes through <see cref="LogRedactor"/>, so user names,
/// profile paths, URLs and e-mail addresses are never written.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private string? _currentPath;
    private bool _disabled;

    public FileLoggerProvider(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);
        try
        {
            Directory.CreateDirectory(directory);
            PruneOldFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _disabled = true;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (_disabled) return;
        var sb = new StringBuilder(256);
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture))
          .Append(" [").Append(LevelTag(level)).Append("] ")
          .Append(category).Append(": ")
          .Append(message);
        if (exception is not null)
            sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message).Append(" @ ").Append(FirstFrame(exception));

        var line = LogRedactor.Redact(sb.ToString());
        lock (_gate)
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the app.
            }
        }
    }

    private void EnsureWriter()
    {
        var path = Path.Combine(_directory, $"tickshift-{DateTime.Now:yyyyMMdd}.log");
        if (_writer is not null && _currentPath == path && _writer.BaseStream.Length < MaxFileBytes) return;

        _writer?.Dispose();
        if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
        {
            var rolled = Path.Combine(_directory, $"tickshift-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(path, rolled, overwrite: true);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
        _currentPath = path;
    }

    private void PruneOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "tickshift-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
    }

    private static string FirstFrame(Exception ex)
    {
        var trace = ex.StackTrace;
        if (string.IsNullOrEmpty(trace)) return "no stack";
        var line = trace.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
