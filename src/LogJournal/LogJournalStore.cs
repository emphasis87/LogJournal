using Microsoft.Extensions.Logging;

namespace LogJournalExample;

internal sealed class LogJournalStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();
    private readonly Dictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly AsyncLocal<ScopeNode?> _currentScope = new();
    private bool _disposed;
    private bool _replaying;

    public ILogger CreateLogger(string categoryName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_loggers.TryGetValue(categoryName, out ILogger? logger))
            {
                logger = new JournalLogger(this, categoryName);
                _loggers.Add(categoryName, logger);
            }

            return logger;
        }
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var scope = new ScopeNode(new StateSnapshot(state.ToString() ?? string.Empty, state),
                _currentScope.Value);
            _currentScope.Value = scope;
            return new Scope(this, scope);
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        lock (_gate)
        {
            return !_disposed && logLevel != LogLevel.None;
        }
    }

    public void Log<TState>(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _entries.Enqueue(new Entry(
                categoryName,
                logLevel,
                eventId,
                exception,
                new StateSnapshot(formatter(state, exception), state),
                _currentScope.Value));
        }
    }

    public void ReplayTo(Func<string, ILogger> resolveLogger)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_replaying)
            {
                throw new InvalidOperationException("Recursive replay is not supported.");
            }

            _replaying = true;
            var activeScopes = new List<(ScopeNode Scope, IDisposable? Handle)>();
            var pendingScopes = new Stack<ScopeNode>();
            try
            {
                // Messages added by the destination during replay belong to the next batch.
                int count = _entries.Count;
                for (int index = 0; index < count; index++)
                {
                    Entry entry = _entries.Peek();
                    ILogger logger = resolveLogger(entry.CategoryName);
                    ScopeNode? common = activeScopes.Count == 0 ? null : activeScopes[^1].Scope;
                    ScopeNode? next = entry.Scope;
                    // Walk only changed branches; identical chains require no traversal.
                    while (!ReferenceEquals(common, next))
                    {
                        if (common is not null && (next is null || common.Depth > next.Depth))
                        {
                            common = common.Parent;
                        }
                        else
                        {
                            pendingScopes.Push(next!);
                            next = next!.Parent;
                        }
                    }

                    CloseScopes(common?.Depth ?? 0);
                    while (pendingScopes.TryPop(out ScopeNode? scope))
                    {
                        activeScopes.Add((scope, logger.BeginScope(scope.State)));
                    }

                    logger.Log(entry.Level, entry.EventId, entry.State, entry.Exception,
                        static (state, _) => state.ToString());
                    _entries.Dequeue();
                }
            }
            finally
            {
                try
                {
                    CloseScopes(0);
                }
                finally
                {
                    _replaying = false;
                }
            }

            void CloseScopes(int keepCount)
            {
                List<Exception>? errors = null;
                for (int index = activeScopes.Count - 1; index >= keepCount; index--)
                {
                    IDisposable? handle = activeScopes[index].Handle;
                    activeScopes.RemoveAt(index);
                    try
                    {
                        handle?.Dispose();
                    }
                    catch (Exception exception)
                    {
                        (errors ??= []).Add(exception);
                    }
                }

                if (errors is not null)
                {
                    throw new AggregateException("Failed to close replay scopes.", errors);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_replaying)
            {
                throw new InvalidOperationException("Cannot dispose the journal during replay.");
            }

            _disposed = true;
            _entries.Clear();
            _loggers.Clear();
            _currentScope.Value = null;
        }
    }

    private sealed record Entry(
        string CategoryName,
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        StateSnapshot State,
        ScopeNode? Scope);

    private sealed class ScopeNode(StateSnapshot state, ScopeNode? parent)
    {
        public StateSnapshot State { get; } = state;
        public ScopeNode? Parent { get; } = parent;
        public int Depth { get; } = (parent?.Depth ?? 0) + 1;
    }

    private sealed class StateSnapshot : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly string _message;
        private readonly KeyValuePair<string, object?>[] _properties;

        public StateSnapshot(string message, object? state)
        {
            _message = message;
            // Copy the producer-owned container before returning; values are not deep-cloned.
            _properties = state is IEnumerable<KeyValuePair<string, object?>> properties
                ? properties.ToArray()
                : [];
        }

        public int Count => _properties.Length;

        public KeyValuePair<string, object?> this[int index] => _properties[index];

        public override string ToString() => _message;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, object?>>)_properties).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class JournalLogger(LogJournalStore store, string categoryName) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => store.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => store.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            store.Log(categoryName, logLevel, eventId, state, exception, formatter);
    }

    private sealed class Scope(LogJournalStore owner, ScopeNode node) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._currentScope.Value, node))
            {
                owner._currentScope.Value = node.Parent;
            }
        }
    }
}
