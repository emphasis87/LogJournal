using Microsoft.Extensions.Logging;

namespace LogJournalExample;

/// <summary>Creates category loggers backed by one shared initialization journal.</summary>
public sealed class LogJournalFactory : ILoggerFactory
{
    private readonly LogJournalStore _store = new();

    /// <summary>Returns the cached logger for the exact category name.</summary>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return _store.CreateLogger(categoryName);
    }

    /// <summary>Drains the current batch in order, preserving each entry's category.</summary>
    /// <remarks>Neither the destination factory nor its loggers are retained after replay.</remarks>
    /// <param name="destination">The configured factory supplying destination loggers.</param>
    public void ReplayTo(ILoggerFactory destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(destination, this))
        {
            throw new ArgumentException("The log journal factory cannot replay to itself.", nameof(destination));
        }

        // Cache only for this replay; neither the factory nor its loggers are retained.
        var loggers = new Dictionary<string, ILogger>(StringComparer.Ordinal);
        _store.ReplayTo(categoryName =>
        {
            if (!loggers.TryGetValue(categoryName, out ILogger? logger))
            {
                logger = destination.CreateLogger(categoryName);
                loggers.Add(categoryName, logger);
            }

            return logger;
        });
    }

    /// <summary>Provider registration is unsupported; configure providers on the destination factory.</summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("Configure providers on the destination factory, not the log journal factory.");

    /// <summary>Discards pending entries and closes this factory without disposing replay destinations.</summary>
    public void Dispose() => _store.Dispose();
}
