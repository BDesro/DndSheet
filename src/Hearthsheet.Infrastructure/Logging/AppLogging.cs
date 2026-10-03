using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.Infrastructure.Logging;

/// <summary>
/// Minimal ILoggerProvider writing "[time] [LEVEL] [Category] message" lines to daily files (app-yyyyMMdd.log),
/// pruned after 14 days. One sink doesn't justify a logging framework dependency; the standard ILogger
/// abstraction keeps swapping one in (Serilog etc.) a composition-root change.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainDays = 14;

    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLoggerProvider(string directory, LogLevel minimum)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        foreach (var old in new DirectoryInfo(directory).GetFiles("app-*.log")
                     .Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-RetainDays)))
        {
            try { old.Delete(); } catch (IOException) { /* In use by another instance; try next start. */ }
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer is null || today != _day)
            {
                _writer?.Dispose();
                _day = today;
                var path = Path.Combine(_directory, $"app-{today:yyyyMMdd}.log");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            _writer.WriteLine(line);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] [")
                .Append(LevelName(logLevel)).Append("] [").Append(category).Append("] ")
                .Append(formatter(state, exception));
            if (exception is not null) message.AppendLine().Append(exception);
            try { provider.Write(SecretRedactor.Redact(message.ToString())); }
            catch (IOException) { /* A failing log must never take the application down. */ }
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            _ => "CRITICAL",
        };
    }
}

/// <summary>
/// Last line of defense: scrubs anything shaped like a GitHub token, a JWT or an auth header from log lines.
/// The primary defense is that tokens are never passed to the logger in the first place.
/// </summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*|(?i:authorization\s*[:=]).*|(?i:bearer\s+)\S+|(?i:(password|token)""?\s*[:=]\s*)\S+)")]
    private static partial Regex SecretPattern();

    public static string Redact(string text) => SecretPattern().Replace(text, "[REDACTED]");
}
