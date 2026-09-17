using Microsoft.Extensions.Logging;

namespace LogJournal;

    /// <summary>Buffers initialization logs, then forwards them after explicit replay.</summary>
public sealed class LogJournal : ILogger
{
    private readonly LogJournalStore _store = new();

    /// <summary>Captures a fixed, shallow snapshot of the scope state.</summary>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _store.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _store.IsEnabled(string.Empty, logLevel);

    /// <summary>Gets whether the buffered backlog contains an Error or Critical entry.</summary>
    public bool HasErrors => _store.HasErrors;

    /// <summary>Gets whether the journal currently contains any buffered entries.</summary>
    public bool HasBacklog => _store.HasBacklog;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) =>
        _store.Log(string.Empty, logLevel, eventId, state, exception, formatter);

    /// <summary>Replays buffered entries and forwards subsequent writes to the destination.</summary>
    /// <remarks>Later calls replace the retained destination logger.</remarks>
    /// <param name="destination">The configured logger that receives the buffered entries.</param>
    public void ReplayTo(ILogger destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(destination, this))
        {
            throw new ArgumentException("The log journal cannot replay to itself.", nameof(destination));
        }

        _store.ReplayTo(_ => destination);
    }
}
