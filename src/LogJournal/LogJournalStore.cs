using Microsoft.Extensions.Logging;

namespace LogJournal;

internal sealed class LogJournalStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();
    private readonly Dictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly AsyncLocal<ScopeNode?> _currentScope = new();
    private readonly List<(ScopeNode Scope, IDisposable? Handle)> _activeScopes = [];
    private readonly Stack<ScopeNode> _pendingScopes = new();
    private Func<string, ILogger>? _resolveLogger;
    private int _errorCount;
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

    public bool IsEnabled(string categoryName, LogLevel logLevel)
    {
        lock (_gate)
        {
            return !_disposed && (_resolveLogger?.Invoke(categoryName).IsEnabled(logLevel)
                ?? logLevel != LogLevel.None);
        }
    }

    public bool HasErrors
    {
        get
        {
            lock (_gate)
            {
                return _errorCount != 0;
            }
        }
    }

    public bool HasBacklog
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count != 0;
            }
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
            var entry = new Entry(
                categoryName,
                logLevel,
                eventId,
                exception,
                new StateSnapshot(formatter(state, exception), state),
                _currentScope.Value);
            if (_resolveLogger is null)
            {
                _entries.Enqueue(entry);
                if (IsErrorOrHigher(logLevel))
                {
                    _errorCount++;
                }
                return;
            }

            ILogger logger = _resolveLogger(categoryName);
            TransitionScopes(logger, entry.Scope);
            entry.LogTo(logger);
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
            try
            {
                if (_resolveLogger is not null)
                {
                    CloseScopes(0);
                    _resolveLogger = resolveLogger;
                    return;
                }

                while (_entries.TryPeek(out Entry? entry))
                {
                    ILogger logger = resolveLogger(entry.CategoryName);
                    TransitionScopes(logger, entry.Scope);
                    entry.LogTo(logger);
                    _entries.Dequeue();
                    if (IsErrorOrHigher(entry.Level))
                    {
                        _errorCount--;
                    }
                }

                CloseScopesOutside(_currentScope.Value);
                _resolveLogger = resolveLogger;
            }
            finally
            {
                if (_resolveLogger is null)
                {
                    CloseScopes(0);
                }
                _replaying = false;
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

            try
            {
                CloseScopes(0);
            }
            finally
            {
                _disposed = true;
                _resolveLogger = null;
                _entries.Clear();
                _errorCount = 0;
                _loggers.Clear();
                _currentScope.Value = null;
            }
        }
    }

    private static bool IsErrorOrHigher(LogLevel logLevel) =>
        logLevel >= LogLevel.Error && logLevel != LogLevel.None;

    private void TransitionScopes(ILogger logger, ScopeNode? next)
    {
        try
        {
            ScopeNode? common = _activeScopes.Count == 0 ? null : _activeScopes[^1].Scope;
            while (!ReferenceEquals(common, next))
            {
                if (common is not null && (next is null || common.Depth > next.Depth))
                {
                    common = common.Parent;
                }
                else
                {
                    _pendingScopes.Push(next!);
                    next = next!.Parent;
                }
            }

            CloseScopes(common?.Depth ?? 0);
            while (_pendingScopes.TryPop(out ScopeNode? scope))
            {
                _activeScopes.Add((scope, logger.BeginScope(scope.State)));
            }
        }
        finally
        {
            _pendingScopes.Clear();
        }
    }

    private void CloseScopesOutside(ScopeNode? scope)
    {
        ScopeNode? common = _activeScopes.Count == 0 ? null : _activeScopes[^1].Scope;
        while (common is not null && !IsAncestorOrSelf(common, scope))
        {
            common = common.Parent;
        }
        CloseScopes(common?.Depth ?? 0);
    }

    private static bool IsAncestorOrSelf(ScopeNode candidate, ScopeNode? scope)
    {
        while (scope is not null && scope.Depth > candidate.Depth)
        {
            scope = scope.Parent;
        }
        return ReferenceEquals(candidate, scope);
    }

    private void CloseScopes(int keepCount)
    {
        List<Exception>? errors = null;
        for (int index = _activeScopes.Count - 1; index >= keepCount; index--)
        {
            IDisposable? handle = _activeScopes[index].Handle;
            _activeScopes.RemoveAt(index);
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

    private sealed record Entry(
        string CategoryName,
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        StateSnapshot State,
        ScopeNode? Scope)
    {
        public void LogTo(ILogger logger) =>
            logger.Log(Level, EventId, State, Exception, static (state, _) => state.ToString());
    }

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

        public bool IsEnabled(LogLevel logLevel) => store.IsEnabled(categoryName, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            store.Log(categoryName, logLevel, eventId, state, exception, formatter);
    }

    private sealed class Scope(LogJournalStore owner, ScopeNode node) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (ReferenceEquals(owner._currentScope.Value, node))
                {
                    owner._currentScope.Value = node.Parent;
                    if (owner._activeScopes.Count >= node.Depth
                        && ReferenceEquals(owner._activeScopes[node.Depth - 1].Scope, node))
                    {
                        owner.CloseScopes(node.Depth - 1);
                    }
                }
            }
        }
    }
}
