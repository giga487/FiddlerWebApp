using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace FiddlerWebApp.Services.Diagnostics;

public sealed record DiagnosticLogEntry(long Sequence, DateTimeOffset Timestamp, LogEventLevel Level,
    string Category, string Message, string MessageTemplate, string? Exception, string Properties);

public sealed record DiagnosticLogFilter(LogEventLevel MinimumLevel = LogEventLevel.Verbose,
    string Category = "", string Message = "", bool ExceptionsOnly = false,
    DateTimeOffset? From = null, DateTimeOffset? Until = null, long AfterSequence = 0);

/// <summary>A bounded snapshot sink; logging threads never call UI code.</summary>
public sealed class DiagnosticLogStore(int capacity = 2000) : ILogEventSink
{
    private readonly object gate = new();
    private readonly Queue<DiagnosticLogEntry> entries = new();
    public int Capacity { get; } = Math.Clamp(capacity, 1, 10000);
    private long sequence;
    public long LatestSequence { get { lock (gate) return sequence; } }

    private static string Clip(string value) => value.Length <= 16384 ? value : value[..16384] + " [truncated]";

    public void Emit(LogEvent logEvent)
    {
        string category = logEvent.Properties.TryGetValue("SourceContext", out var source)
            ? (source as ScalarValue)?.Value?.ToString() ?? source.ToString() : "Application";
        string properties = string.Join(Environment.NewLine, logEvent.Properties.Take(32)
            .Select(pair => $"{Clip(pair.Key)}: {Clip(pair.Value.ToString())}"));
        lock (gate)
        {
            entries.Enqueue(new(++sequence, logEvent.Timestamp, logEvent.Level, Clip(category),
                Clip(logEvent.RenderMessage(CultureInfo.InvariantCulture)), Clip(logEvent.MessageTemplate.Text),
                logEvent.Exception is null ? null : Clip(logEvent.Exception.ToString()), Clip(properties)));
            while (entries.Count > Capacity) entries.Dequeue();
        }
    }

    public IReadOnlyList<DiagnosticLogEntry> Query(DiagnosticLogFilter filter, int limit = 200)
    {
        lock (gate)
        {
            return entries.Reverse().Where(entry => entry.Sequence > filter.AfterSequence && entry.Level >= filter.MinimumLevel
                && entry.Category.Contains(filter.Category, StringComparison.OrdinalIgnoreCase)
                && (entry.Message.Contains(filter.Message, StringComparison.OrdinalIgnoreCase)
                    || entry.Exception?.Contains(filter.Message, StringComparison.OrdinalIgnoreCase) == true)
                && (!filter.ExceptionsOnly || entry.Exception is not null)
                && (filter.From is null || entry.Timestamp >= filter.From)
                && (filter.Until is null || entry.Timestamp <= filter.Until))
                .Take(Math.Clamp(limit, 1, 1000)).ToArray();
        }
    }
}
