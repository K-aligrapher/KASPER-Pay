using System.Collections.Concurrent;
using System.Text.Json;
using Kasper.Application;
using Microsoft.Extensions.Logging;

namespace Kasper.Infrastructure.Observability;

/// <summary>
/// Structured JSON-lines log in %LOCALAPPDATA%\KASPER\logs\kasper-yyyyMMdd.log (NFR-030), kept 14 days.
/// Only message templates and their values are written: callers never log prompts, content or secrets.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider()
    {
        Directory.CreateDirectory(KasperPaths.Logs);
        foreach (var old in Directory.GetFiles(KasperPaths.Logs, "kasper-*.log").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)))
            try { File.Delete(old); } catch (IOException) { }
        _writer = new Thread(Write) { IsBackground = true, Name = "KASPER log writer" };
        _writer.Start();
    }

    private void Write()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(Path.Combine(KasperPaths.Logs, $"kasper-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); }
            catch (IOException) { }
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _queue);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(string category, BlockingCollection<string> queue) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) =>
            level >= (category.StartsWith("Microsoft") || category.StartsWith("System") ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var entry = new Dictionary<string, object?>
            {
                ["t"] = DateTime.UtcNow.ToString("O"),
                ["level"] = level.ToString(),
                ["category"] = category,
                ["msg"] = formatter(state, exception),
            };
            if (state is IEnumerable<KeyValuePair<string, object?>> props)
                foreach (var (k, v) in props.Where(p => p.Key != "{OriginalFormat}")) entry[k] = v?.ToString();
            if (exception is not null) entry["error"] = exception.GetType().Name + ": " + exception.Message;
            queue.TryAdd(JsonSerializer.Serialize(entry));
        }
    }
}
