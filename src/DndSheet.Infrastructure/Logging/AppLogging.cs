using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DndSheet.Infrastructure.Logging;

/// <summary>A formatted log line destination (console window, rolling file).</summary>
public interface ILogSink
{
    void Write(LogLevel level, string line);
}

/// <summary>
/// Minimal ILoggerProvider fanning out to sinks with a fixed "[time] [LEVEL] [Category] message" format.
/// Two sinks don't justify a logging framework dependency; the standard ILogger abstraction keeps
/// swapping in one (Serilog etc.) a composition-root change.
/// </summary>
public sealed class AppLoggerProvider(IReadOnlyList<ILogSink> sinks) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SinkLogger(ShortCategory(categoryName), sinks);

    private static string ShortCategory(string category) => category[(category.LastIndexOf('.') + 1)..];

    public void Dispose()
    {
        foreach (var sink in sinks) (sink as IDisposable)?.Dispose();
    }

    private sealed class SinkLogger(string category, IReadOnlyList<ILogSink> sinks) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] [")
                .Append(LevelName(logLevel)).Append("] [").Append(category).Append("] ")
                .Append(formatter(state, exception));
            if (exception is not null) message.AppendLine().Append(exception);
            var line = SecretRedactor.Redact(message.ToString());
            foreach (var sink in sinks)
            {
                try { sink.Write(logLevel, line); }
                catch (IOException) { /* A failing log sink must never take the application down. */ }
            }
        }
    }

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        _ => "CRITICAL",
    };
}

/// <summary>
/// Last line of defense: scrubs anything shaped like a GitHub token or auth header from log lines.
/// The primary defense is that tokens are never passed to the logger in the first place.
/// </summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|(?i:authorization\s*[:=]).*|(?i:bearer\s+)\S+|(?i:(password|token)\s*[:=]\s*)\S+)")]
    private static partial Regex SecretPattern();

    public static string Redact(string text) => SecretPattern().Replace(text, "[REDACTED]");
}

public sealed class ConsoleLogSink : ILogSink
{
    private readonly Lock _lock = new();

    public void Write(LogLevel level, string line)
    {
        lock (_lock)
        {
            Console.ForegroundColor = level switch
            {
                LogLevel.Trace or LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Information => ConsoleColor.Gray,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => ConsoleColor.Magenta,
            };
            Console.WriteLine(line);
            Console.ResetColor();
        }
    }
}

/// <summary>Daily log files (app-yyyyMMdd.log), pruned after <c>retainDays</c>.</summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLogSink(string directory, LogLevel minimum, int retainDays)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        foreach (var old in new DirectoryInfo(directory).GetFiles("app-*.log")
                     .Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-retainDays)))
        {
            try { old.Delete(); } catch (IOException) { /* In use by another instance; try next start. */ }
        }
    }

    public void Write(LogLevel level, string line)
    {
        if (level < _minimum) return;
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

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }
}
