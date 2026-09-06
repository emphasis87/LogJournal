using Microsoft.Extensions.Logging;

namespace LogJournalExample;

/// <summary>Buffers initialization logs in private storage for explicit replay.</summary>
public sealed class LogJournal : ILogger
{
    private readonly LogJournalStore _store = new();

    /// <summary>Captures a fixed, shallow snapshot of the scope state.</summary>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _store.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _store.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) =>
        _store.Log(string.Empty, logLevel, eventId, state, exception, formatter);

    /// <summary>Replays the current batch and removes successfully delivered entries.</summary>
    /// <remarks>The destination is not retained. Subsequent writes remain buffered.</remarks>
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
