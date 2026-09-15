using Microsoft.Extensions.Logging;

namespace LogJournal;

/// <summary>Creates category loggers that write through one shared <see cref="TextWriter"/>.</summary>
/// <remarks>The caller owns the writer and is responsible for disposing it.</remarks>
public sealed class TextWriterLoggerFactory : ILoggerFactory
{
    private readonly object _gate = new();
    private readonly TextWriterLoggerContext _context;
    private readonly Dictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a factory using the default formatter or a caller-provided formatter.</summary>
    /// <param name="writer">The shared destination writer. It is not disposed by the factory.</param>
    /// <param name="formatter">The output formatter, or <see langword="null"/> for <see cref="TextWriterLogger.FormatDefault"/>.</param>
    /// <param name="flushAfterWrite">Whether to flush the writer after every entry.</param>
    public TextWriterLoggerFactory(
        TextWriter writer,
        Func<TextWriterLogEntry, string>? formatter = null,
        bool flushAfterWrite = true)
    {
        _context = new TextWriterLoggerContext(writer, formatter ?? TextWriterLogger.FormatDefault, flushAfterWrite);
    }

    /// <summary>Returns the cached logger for the exact category name.</summary>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_loggers.TryGetValue(categoryName, out ILogger? logger))
            {
                logger = new TextWriterLogger(_context, categoryName);
                _loggers.Add(categoryName, logger);
            }
            return logger;
        }
    }

    /// <summary>Provider registration is unsupported because the writer is the destination.</summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("TextWriterLoggerFactory does not support logging providers.");

    /// <summary>Stops all created loggers without disposing the caller-owned writer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _loggers.Clear();
            _context.Dispose();
        }
    }
}
