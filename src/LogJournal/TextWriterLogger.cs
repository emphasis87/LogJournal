using Microsoft.Extensions.Logging;

namespace LogJournal;

/// <summary>A synchronous MEL logger that writes formatted entries to a <see cref="TextWriter"/>.</summary>
/// <remarks>The caller owns the writer and is responsible for disposing it.</remarks>
public sealed class TextWriterLogger : ILogger
{
    private readonly object _gate = new();
    private readonly TextWriter _writer;
    private readonly Func<TextWriterLogEntry, string> _formatter;
    private readonly string? _categoryName;
    private readonly bool _flushAfterWrite;
    private readonly IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    /// <summary>Creates a logger using the default formatter or a caller-provided formatter.</summary>
    /// <param name="writer">The destination writer. It is not disposed by the logger.</param>
    /// <param name="formatter">The output formatter, or <see langword="null"/> for <see cref="FormatDefault"/>.</param>
    /// <param name="categoryName">An optional category included in each entry.</param>
    /// <param name="flushAfterWrite">Whether to flush the writer after every entry.</param>
    public TextWriterLogger(
        TextWriter writer,
        Func<TextWriterLogEntry, string>? formatter = null,
        string? categoryName = null,
        bool flushAfterWrite = true)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _formatter = formatter ?? FormatDefault;
        _categoryName = categoryName;
        _flushAfterWrite = flushAfterWrite;
    }

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _scopeProvider.Push(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var scopes = new List<TextWriterLogScope>();
        _scopeProvider.ForEachScope(static (scope, captured) =>
            captured.Add(new TextWriterLogScope(
                scope?.ToString() ?? string.Empty,
                SnapshotProperties(scope))), scopes);

        var entry = new TextWriterLogEntry(
            DateTimeOffset.UtcNow,
            _categoryName,
            logLevel,
            eventId,
            formatter(state, exception),
            exception,
            SnapshotProperties(state),
            scopes);

        lock (_gate)
        {
            _writer.WriteLine(_formatter(entry));
            if (_flushAfterWrite)
            {
                _writer.Flush();
            }
        }
    }

    /// <summary>Formats an entry as a readable single-line header followed by the exception, if present.</summary>
    public static string FormatDefault(TextWriterLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var text = new System.Text.StringBuilder();
        text.Append(entry.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        text.Append(" [").Append(entry.Level).Append(']');
        if (!string.IsNullOrEmpty(entry.CategoryName))
        {
            text.Append(' ').Append(entry.CategoryName);
        }
        if (entry.EventId.Id != 0 || entry.EventId.Name is not null)
        {
            text.Append('[').Append(entry.EventId.Id);
            if (entry.EventId.Name is not null)
            {
                text.Append(':').Append(entry.EventId.Name);
            }
            text.Append(']');
        }
        foreach (TextWriterLogScope scope in entry.Scopes)
        {
            text.Append(" => ").Append(scope.Message);
        }
        text.Append(' ').Append(entry.Message);
        if (entry.Exception is not null)
        {
            text.AppendLine().Append(entry.Exception);
        }
        return text.ToString();
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> SnapshotProperties(object? state) =>
        state is IEnumerable<KeyValuePair<string, object?>> properties
            ? properties.ToArray()
            : [];
}

/// <summary>A stable snapshot passed to a <see cref="TextWriterLogger"/> formatter.</summary>
/// <param name="TimestampUtc">The UTC time at which the logger received the entry.</param>
/// <param name="CategoryName">The optional logger category.</param>
/// <param name="Level">The log level.</param>
/// <param name="EventId">The event identifier.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The associated exception, if any.</param>
/// <param name="Properties">A shallow snapshot of structured state properties.</param>
/// <param name="Scopes">Snapshots of active scopes, ordered outermost to innermost.</param>
public sealed record TextWriterLogEntry(
    DateTimeOffset TimestampUtc,
    string? CategoryName,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Properties,
    IReadOnlyList<TextWriterLogScope> Scopes);

/// <summary>A stable scope snapshot passed to a <see cref="TextWriterLogger"/> formatter.</summary>
/// <param name="Message">The rendered scope value.</param>
/// <param name="Properties">A shallow snapshot of structured scope properties.</param>
public sealed record TextWriterLogScope(
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Properties);
