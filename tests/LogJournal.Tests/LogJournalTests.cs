using Microsoft.Extensions.Logging;
using Xunit;

namespace LogJournal.Tests;

public sealed class LogJournalTests
{
    [ThreadStatic]
    private static List<KeyValuePair<string, object?>>? _reusableState;

    [Fact]
    public void HasErrors_TracksOnlyBufferedErrorAndCriticalEntries()
    {
        var journal = new LogJournal();
        Assert.False(journal.HasErrors);
        Assert.False(journal.HasBacklog);

        journal.LogWarning("Warning");
        Assert.False(journal.HasErrors);
        Assert.True(journal.HasBacklog);
        journal.LogError("Error");
        journal.LogCritical("Critical");
        Assert.True(journal.HasErrors);

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);
        Assert.False(journal.HasErrors);
        Assert.False(journal.HasBacklog);

        journal.LogError("Forwarded error");
        Assert.False(journal.HasErrors);
        Assert.False(journal.HasBacklog);
        Assert.Equal("Forwarded error", destination.Entries[^1].Message);
    }

    [Fact]
    public void FactoryHasErrors_AggregatesCategoriesAndRetainsFailedEntryForRetry()
    {
        using var journals = new LogJournalFactory();
        ILogger first = journals.CreateLogger("First");
        ILogger second = journals.CreateLogger("Second");
        first.LogInformation("Information");
        second.LogError("Failure");
        Assert.True(journals.HasErrors);
        Assert.True(journals.HasBacklog);

        var failing = new RecordingFactory(onWrite: entry =>
        {
            if (entry.Log.Level == LogLevel.Error)
            {
                throw new InvalidOperationException("Destination failed");
            }
        });
        Assert.Throws<InvalidOperationException>(() => journals.ReplayTo(failing));
        Assert.True(journals.HasErrors);
        Assert.True(journals.HasBacklog);

        journals.ReplayTo(new RecordingFactory());
        Assert.False(journals.HasErrors);
        Assert.False(journals.HasBacklog);
        second.LogCritical("Forwarded critical");
        Assert.False(journals.HasErrors);
        Assert.False(journals.HasBacklog);
    }

    [Fact]
    public void BeginScope_CapturesTextAndPropertiesOnlyOnce()
    {
        var journal = new LogJournal();
        var state = new CountingScopeState();
        using (journal.BeginScope(state))
        {
            Assert.Equal(1, state.FormatCalls);
            Assert.Equal(1, state.EnumerationCalls);
            state.Value = "Changed";
            for (int index = 0; index < 20; index++)
            {
                journal.LogInformation("Entry {Index}", index);
            }
        }

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);

        Assert.Equal(1, state.FormatCalls);
        Assert.Equal(1, state.EnumerationCalls);
        Assert.Equal(20, destination.Entries.Count);
        Assert.All(destination.Entries, entry =>
        {
            Assert.Equal(["Original"], entry.Scopes);
            Assert.Equal("Original", entry.ScopeProperties[0]["Value"]);
        });
        Assert.Single(destination.Trace, item => item == "Begin Original");
        Assert.Single(destination.Trace, item => item == "End Original");
    }

    [Fact]
    public void BeginScope_FixesUnstructuredMutableTextBeforeFirstWrite()
    {
        var journal = new LogJournal();
        var state = new System.Text.StringBuilder("Original");
        using (journal.BeginScope(state))
        {
            state.Clear().Append("Changed");
            journal.LogInformation("First");
            journal.LogInformation("Second");
        }

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);

        Assert.Equal(["Begin Original", "Log First", "Log Second", "End Original"], destination.Trace);
    }

    [Fact]
    public void ReplayScopes_KeepCommonAncestorsOpenAcrossEntries()
    {
        var journal = new LogJournal();
        using (journal.BeginScope("Outer"))
        {
            journal.LogInformation("First");
            using (journal.BeginScope("Inner"))
            {
                journal.LogInformation("Second");
                journal.LogInformation("Third");
            }
            journal.LogInformation("Fourth");
        }
        journal.LogInformation("Outside");

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);

        Assert.Equal(["Begin Outer", "Log First", "Begin Inner", "Log Second", "Log Third",
            "End Inner", "Log Fourth", "End Outer", "Log Outside"], destination.Trace);
    }

    [Fact]
    public void ReplayScopes_DoNotMergeDifferentScopesWithIdenticalValues()
    {
        var journal = new LogJournal();
        using (journal.BeginScope("Same"))
        {
            journal.LogInformation("First");
        }
        using (journal.BeginScope("Same"))
        {
            journal.LogInformation("Second");
        }

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);

        Assert.Equal(["Begin Same", "Log First", "End Same", "Begin Same", "Log Second", "End Same"],
            destination.Trace);
    }

    [Fact]
    public void ReplayScopes_KeepScopeOpenAcrossFactoryCategories()
    {
        using var journals = new LogJournalFactory();
        ILogger first = journals.CreateLogger("First");
        ILogger second = journals.CreateLogger("Second");
        using (first.BeginScope("Shared"))
        {
            first.LogInformation("One");
            second.LogInformation("Two");
            first.LogInformation("Three");
        }

        var destination = new RecordingFactory();
        journals.ReplayTo(destination);

        Assert.Equal(["Begin Shared", "Log One", "Log Two", "Log Three", "End Shared"], destination.Trace);
        Assert.All(destination.Entries, entry => Assert.Equal(["Shared"], entry.Log.Scopes));
    }

    [Fact]
    public async Task ReplayScopes_ReopenInterleavedAsyncContextsWithoutLeakingProperties()
    {
        var journal = new LogJournal();
        var firstWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (journal.BeginScope("Root"))
        {
            await Task.WhenAll(WriteFirst(), WriteSecond());
        }

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);

        Assert.Equal(["Begin Root", "Begin A", "Log A1", "End A", "Begin B", "Log B1",
            "End B", "Begin A", "Log A2", "End A", "End Root"], destination.Trace);
        Assert.Equal(["Root", "A"], destination.Entries[0].Scopes);
        Assert.Equal(["Root", "B"], destination.Entries[1].Scopes);
        Assert.Equal(["Root", "A"], destination.Entries[2].Scopes);

        async Task WriteFirst()
        {
            using (journal.BeginScope("A"))
            {
                journal.LogInformation("A1");
                firstWritten.SetResult();
                await secondWritten.Task;
                journal.LogInformation("A2");
            }
        }

        async Task WriteSecond()
        {
            await firstWritten.Task;
            using (journal.BeginScope("B"))
            {
                journal.LogInformation("B1");
            }
            secondWritten.SetResult();
        }
    }

    [Fact]
    public void ReplayScopes_ContinueAcrossTheSwitchUntilTheJournalScopeEnds()
    {
        var journal = new LogJournal();
        var destination = new RecordingLogger();
        using (destination.BeginScope("Ambient"))
        using (journal.BeginScope("Startup"))
        {
            journal.LogInformation("First batch");
            journal.ReplayTo(destination);
            destination.LogInformation("After replay");
            journal.LogInformation("Forwarded");
        }

        Assert.Equal(["Begin Ambient", "Begin Startup", "Log First batch", "Log After replay",
            "Log Forwarded", "End Startup", "End Ambient"],
            destination.Trace);
        Assert.All(destination.Entries, entry => Assert.Equal(["Ambient", "Startup"], entry.Scopes));
    }

    [Fact]
    public void ReplayScopes_CloseOnFailureAndRestoreScopesForRetry()
    {
        var journal = new LogJournal();
        using (journal.BeginScope("Outer"))
        using (journal.BeginScope("Inner"))
        {
            journal.LogInformation("First");
            journal.LogInformation("Failure");
            journal.LogInformation("Last");
        }
        var destination = new RecordingLogger(onWrite: entry =>
        {
            if (entry.Message == "Failure")
            {
                throw new InvalidOperationException("Destination failed");
            }
        });

        Assert.Throws<InvalidOperationException>(() => journal.ReplayTo(destination));
        Assert.Equal(["Begin Outer", "Begin Inner", "Log First", "Log Failure", "End Inner", "End Outer"],
            destination.Trace);
        destination.LogInformation("Outside");
        Assert.Empty(destination.Entries[^1].Scopes);

        var retry = new RecordingLogger();
        journal.ReplayTo(retry);
        Assert.Equal(["Begin Outer", "Begin Inner", "Log Failure", "Log Last", "End Inner", "End Outer"],
            retry.Trace);
    }

    [Fact]
    public void FactoryCreateLogger_CachesByExactCategoryWithinEachStore()
    {
        using var factory = new LogJournalFactory();
        using var otherFactory = new LogJournalFactory();
        ILogger logger = factory.CreateLogger("Configuration");

        Assert.Same(logger, factory.CreateLogger("Configuration"));
        Assert.NotSame(logger, factory.CreateLogger("configuration"));
        Assert.NotSame(logger, factory.CreateLogger("Database"));
        Assert.NotSame(logger, otherFactory.CreateLogger("Configuration"));

        logger.LogInformation("Startup");
        factory.ReplayTo(new RecordingFactory());
        Assert.Same(logger, factory.CreateLogger("Configuration"));
    }

    [Fact]
    public async Task FactoryCreateLogger_ConcurrentCallsReturnSameInstance()
    {
        using var factory = new LogJournalFactory();
        ILogger[] loggers = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => factory.CreateLogger("Shared"))));

        Assert.All(loggers, logger => Assert.Same(loggers[0], logger));
    }

    [Fact]
    public void FactoryReplay_PreservesBacklogOrderThenForwardsByCategory()
    {
        using var journals = new LogJournalFactory();
        ILogger configuration = journals.CreateLogger("Configuration");
        ILogger database = journals.CreateLogger("Database");
        configuration.LogInformation("Loading {FileName}", "settings.json");
        database.LogWarning("Connecting");
        journals.CreateLogger("Configuration").LogInformation("Loaded");

        var destination = new RecordingFactory();
        Assert.Empty(destination.Entries);
        journals.ReplayTo(destination);
        Assert.Equal(["Configuration", "Database", "Configuration"],
            destination.Entries.Select(entry => entry.Category));
        Assert.Equal(["Loading settings.json", "Connecting", "Loaded"],
            destination.Entries.Select(entry => entry.Log.Message));
        Assert.Equal("settings.json", destination.Entries[0].Log.Properties["FileName"]);
        Assert.Equal(["Configuration", "Database"], destination.CreatedCategories);

        Assert.Throws<InvalidOperationException>(() => journals.ReplayTo(destination));
        database.LogInformation("Next batch");
        Assert.Equal(4, destination.Entries.Count);
        var next = destination.Entries[^1];
        Assert.Equal("Database", next.Category);
        Assert.Equal("Next batch", next.Log.Message);
    }

    [Fact]
    public void FactoriesAndStandaloneJournals_HaveIndependentStorageAndScopes()
    {
        using var firstFactory = new LogJournalFactory();
        using var secondFactory = new LogJournalFactory();
        ILogger first = firstFactory.CreateLogger("SameCategory");
        ILogger second = secondFactory.CreateLogger("SameCategory");
        var standalone = new LogJournal();
        var otherStandalone = new LogJournal();

        using (first.BeginScope("First factory only"))
        {
            first.LogInformation("First");
            second.LogInformation("Second");
            standalone.LogInformation("Standalone");
            otherStandalone.LogInformation("Other standalone");
        }

        var firstDestination = new RecordingFactory();
        firstFactory.ReplayTo(firstDestination);
        Assert.Equal("First", Assert.Single(firstDestination.Entries).Log.Message);

        var secondDestination = new RecordingFactory();
        secondFactory.ReplayTo(secondDestination);
        RecordedLog secondLog = Assert.Single(secondDestination.Entries).Log;
        Assert.Equal("Second", secondLog.Message);
        Assert.Empty(secondLog.Scopes);

        var standaloneDestination = new RecordingLogger();
        standalone.ReplayTo(standaloneDestination);
        Assert.Equal("Standalone", Assert.Single(standaloneDestination.Entries).Message);
        Assert.Empty(standaloneDestination.Entries[0].Scopes);

        var otherDestination = new RecordingLogger();
        otherStandalone.ReplayTo(otherDestination);
        Assert.Equal("Other standalone", Assert.Single(otherDestination.Entries).Message);
        Assert.Empty(otherDestination.Entries[0].Scopes);
    }

    [Fact]
    public async Task FactoryScopes_AreSharedAcrossCategoriesAndFlowAcrossAwait()
    {
        using var journals = new LogJournalFactory();
        ILogger first = journals.CreateLogger("First");
        ILogger second = journals.CreateLogger("Second");
        var properties = new Dictionary<string, object?> { ["Stage"] = "Configuration" };

        using (first.BeginScope(properties))
        {
            properties["Stage"] = "Database";
            first.LogInformation("Before await");
            await Task.Yield();
            properties.Clear();
            using (second.BeginScope("Nested"))
            {
                second.LogInformation("After await");
            }
        }

        properties.Clear();
        second.LogInformation("Outside");
        var destination = new RecordingFactory();
        journals.ReplayTo(destination);

        Assert.Equal("Configuration", destination.Entries[0].Log.ScopeProperties[0]["Stage"]);
        Assert.Equal("Configuration", destination.Entries[1].Log.ScopeProperties[0]["Stage"]);
        Assert.Equal("Nested", destination.Entries[1].Log.Scopes[1]);
        Assert.Empty(destination.Entries[2].Log.Scopes);
    }

    [Fact]
    public async Task FactoryConcurrentWriters_PreserveEachWritersOrderAndIsolateScopes()
    {
        using var journals = new LogJournalFactory();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            ILogger logger = journals.CreateLogger($"Worker{worker}");
            using (logger.BeginScope("Worker {Worker}", worker))
            {
                for (int index = 0; index < 50; index++)
                {
                    logger.LogInformation("Entry {Index}", index);
                }
            }
        })));

        var destination = new RecordingFactory();
        journals.ReplayTo(destination);
        Assert.Equal(200, destination.Entries.Count);
        for (int worker = 0; worker < 4; worker++)
        {
            RecordedLog[] entries = destination.Entries
                .Where(entry => entry.Category == $"Worker{worker}")
                .Select(entry => entry.Log).ToArray();
            Assert.Equal(Enumerable.Range(0, 50), entries.Select(entry => (int)entry.Properties["Index"]!));
            Assert.All(entries, entry => Assert.Equal(worker, entry.ScopeProperties[0]["Worker"]));
        }
    }

    [Fact]
    public void FactoryReplay_PreservesGeneratedThreadLocalStateAcrossCategories()
    {
        using var journals = new LogJournalFactory();
        ILogger first = journals.CreateLogger("First");
        ILogger second = journals.CreateLogger("Second");
        var exception = new InvalidOperationException("Failure");
        GeneratedMessages.Loaded(first, "first.json", 12);
        GeneratedMessages.Loaded(second, "second.json", 34);
        GeneratedMessages.Failed(first, "missing.json", exception);

        var destination = new RecordingFactory();
        journals.ReplayTo(destination);

        Assert.Equal(["First", "Second", "First"], destination.Entries.Select(entry => entry.Category));
        Assert.Equal(["Loaded first.json with 12 settings", "Loaded second.json with 34 settings",
            "Could not load missing.json"], destination.Entries.Select(entry => entry.Log.Message));
        Assert.Equal(12, destination.Entries[0].Log.Properties["SettingCount"]);
        Assert.Equal(34, destination.Entries[1].Log.Properties["SettingCount"]);
        Assert.Equal("first.json", destination.Entries[0].Log.Properties["FileName"]);
        Assert.Equal("second.json", destination.Entries[1].Log.Properties["FileName"]);
        Assert.Equal("Loaded {FileName} with {SettingCount} settings",
            destination.Entries[0].Log.Properties["{OriginalFormat}"]);
        Assert.Equal(101, destination.Entries[2].Log.EventId.Id);
        Assert.Same(exception, destination.Entries[2].Log.Exception);
    }

    [Fact]
    public void FactoryDispose_ClosesItsLoggersButDoesNotDisposeDestination()
    {
        var journals = new LogJournalFactory();
        ILogger logger = journals.CreateLogger("Startup");
        logger.LogInformation("Ready");
        var destination = new RecordingFactory();
        journals.ReplayTo(destination);
        logger.LogInformation("Not replayed");

        journals.Dispose();
        journals.Dispose();

        Assert.False(destination.Disposed);
        Assert.Equal(["Ready", "Not replayed"], destination.Entries.Select(entry => entry.Log.Message));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.Throws<ObjectDisposedException>(() => journals.CreateLogger("Another"));
        Assert.Throws<ObjectDisposedException>(() => journals.CreateLogger("Startup"));
        Assert.Throws<ObjectDisposedException>(() => journals.ReplayTo(destination));
        Assert.Throws<ObjectDisposedException>(() => logger.LogInformation("Too late"));
        Assert.Throws<ObjectDisposedException>(() => logger.BeginScope("Too late"));
    }

    [Fact]
    public void FactoryReplay_RetainsDestinationResolverUntilDispose()
    {
        using var journals = new LogJournalFactory();
        journals.CreateLogger("Startup").LogInformation("Startup");
        var destination = new RecordingFactory();
        journals.ReplayTo(destination);
        journals.CreateLogger("Startup").LogInformation("Forwarded");

        Assert.Equal(["Startup", "Forwarded"], destination.Entries.Select(entry => entry.Log.Message));
        Assert.Throws<InvalidOperationException>(() => journals.ReplayTo(new RecordingFactory()));
    }

    [Fact]
    public void FactoryRejectsSelfReplayAndProviderRegistration()
    {
        using var journals = new LogJournalFactory();
        Assert.Throws<ArgumentException>(() => journals.ReplayTo(journals));
        Assert.Throws<ArgumentNullException>(() => journals.ReplayTo(null!));
        Assert.Throws<ArgumentNullException>(() => journals.CreateLogger(null!));
        Assert.Throws<NotSupportedException>(() => journals.AddProvider(null!));
    }

    [Fact]
    public void ReplayTo_PreservesFormattedLogValuesFromMelExtensions()
    {
        var journal = new LogJournal();
        var probe = new StateProbeLogger(journal);
        var destination = new RecordingLogger();
        var exception = new InvalidOperationException("Configuration failed");
        var eventId = new EventId(42, "ConfigurationError");

        probe.LogInformation("Loaded {FileName} with {SettingCount:D3} settings", "first.json", 12);
        Assert.Equal("Microsoft.Extensions.Logging.FormattedLogValues", probe.LastState?.GetType().FullName);
        probe.LogError(eventId, exception, "Could not load {FileName}", "missing.json");
        Assert.Empty(destination.Entries);

        journal.ReplayTo(destination);
        probe.LogInformation("Loaded {FileName} with {SettingCount:D3} settings", "live.json", 34);
        Assert.Equal(3, destination.Entries.Count);

        Assert.Equal(
            ["Loaded first.json with 012 settings", "Could not load missing.json",
             "Loaded live.json with 034 settings"],
            destination.Entries.Select(entry => entry.Message));
        RecordedLog first = destination.Entries[0];
        Assert.Equal(LogLevel.Information, first.Level);
        Assert.Equal("first.json", first.Properties["FileName"]);
        Assert.Equal(12, first.Properties["SettingCount"]);
        Assert.Equal("Loaded {FileName} with {SettingCount:D3} settings", first.Properties["{OriginalFormat}"]);
        RecordedLog error = destination.Entries[1];
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(eventId.Id, error.EventId.Id);
        Assert.Equal(eventId.Name, error.EventId.Name);
        Assert.Same(exception, error.Exception);
        Assert.Equal("missing.json", error.Properties["FileName"]);
        Assert.Equal("Could not load {FileName}", error.Properties["{OriginalFormat}"]);
        Assert.Equal("live.json", destination.Entries[2].Properties["FileName"]);
        Assert.Equal(34, destination.Entries[2].Properties["SettingCount"]);
    }

    [Fact]
    public void ReplayTo_PreservesGeneratedLoggerMessageCallsWithReusedThreadLocalState()
    {
        var journal = new LogJournal();
        var probe = new StateProbeLogger(journal);
        var destination = new RecordingLogger();
        var exception = new InvalidOperationException("Configuration failed");

        GeneratedMessages.Loaded(probe, "first.json", 12);
        object? firstState = probe.LastState;
        Assert.Equal("LoggerMessageState", firstState?.GetType().Name);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(firstState));

        GeneratedMessages.Loaded(probe, "second.json", 34);
        Assert.Same(firstState, probe.LastState);
        GeneratedMessages.Failed(probe, "missing.json", exception);
        Assert.Same(firstState, probe.LastState);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(firstState));
        Assert.Empty(destination.Entries);

        journal.ReplayTo(destination);
        GeneratedMessages.Loaded(probe, "live.json", 56);
        Assert.Same(firstState, probe.LastState);
        Assert.Equal(4, destination.Entries.Count);

        Assert.Equal(
            ["Loaded first.json with 12 settings", "Loaded second.json with 34 settings",
             "Could not load missing.json", "Loaded live.json with 56 settings"],
            destination.Entries.Select(entry => entry.Message));

        for (int index = 0; index < 2; index++)
        {
            RecordedLog entry = destination.Entries[index];
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Equal(100, entry.EventId.Id);
            Assert.Equal("Loaded", entry.EventId.Name);
            Assert.Equal(index == 0 ? "first.json" : "second.json", entry.Properties["FileName"]);
            Assert.Equal(index == 0 ? 12 : 34, entry.Properties["SettingCount"]);
            Assert.Equal("Loaded {FileName} with {SettingCount} settings", entry.Properties["{OriginalFormat}"]);
        }

        RecordedLog error = destination.Entries[2];
        Assert.Same(exception, error.Exception);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(101, error.EventId.Id);
        Assert.Equal("missing.json", error.Properties["FileName"]);
        Assert.Equal("Could not load {FileName}", error.Properties["{OriginalFormat}"]);
        Assert.Equal("live.json", destination.Entries[3].Properties["FileName"]);
        Assert.Equal(56, destination.Entries[3].Properties["SettingCount"]);
    }

    [Fact]
    public void Log_SnapshotsThreadStaticStateBeforeItIsReusedAndCleared()
    {
        var journal = new LogJournal();
        var destination = new RecordingLogger();
        List<KeyValuePair<string, object?>> state = _reusableState ??= [];
        int formatterCalls = 0;

        // Simulate a producer that owns the state only for the duration of Log().
        foreach (string fileName in new[] { "first.json", "second.json" })
        {
            state.Clear();
            state.Add(new("FileName", fileName));
            state.Add(new("{OriginalFormat}", "Loaded {FileName}"));
            journal.Log(LogLevel.Information, new EventId(200), state, null, (values, _) =>
            {
                formatterCalls++;
                return $"Loaded {values[0].Value}";
            });
        }

        state.Clear();
        Assert.Equal(2, formatterCalls);

        journal.ReplayTo(destination);

        Assert.Equal(2, formatterCalls);
        Assert.Equal(["Loaded first.json", "Loaded second.json"],
            destination.Entries.Select(entry => entry.Message));
        Assert.Equal("first.json", destination.Entries[0].Properties["FileName"]);
        Assert.Equal("second.json", destination.Entries[1].Properties["FileName"]);
        Assert.All(destination.Entries, entry =>
            Assert.Equal("Loaded {FileName}", entry.Properties["{OriginalFormat}"]));
    }

    [Fact]
    public void BeginScope_FixesStructuredPropertiesForItsLifetime()
    {
        var journal = new LogJournal();
        var state = new Dictionary<string, object?> { ["Component"] = "Configuration" };

        using (journal.BeginScope("Startup"))
        using (journal.BeginScope(state))
        {
            state["Component"] = "Changed before first write";
            journal.LogInformation("First");
            state["Component"] = "Database";
            journal.LogInformation("Second");
        }

        state.Clear();
        var destination = new RecordingLogger();
        journal.ReplayTo(destination);
        journal.LogInformation("Outside scope");
        Assert.Equal(3, destination.Entries.Count);

        Assert.Equal("Startup", destination.Entries[0].Scopes[0]);
        Assert.Equal("Configuration", destination.Entries[0].ScopeProperties[1]["Component"]);
        Assert.Equal("Configuration", destination.Entries[1].ScopeProperties[1]["Component"]);
        Assert.Empty(destination.Entries[2].Scopes);
        Assert.Equal(2, destination.Trace.Count(item => item.StartsWith("Begin ")));
        Assert.Single(destination.Trace, item => item == "Begin Startup");
    }

    [Fact]
    public void Log_FormatsUnstructuredMutableStateBeforeReplay()
    {
        var journal = new LogJournal();
        var state = new System.Text.StringBuilder("Original");
        journal.Log(LogLevel.Information, default, state, null, static (value, _) => value.ToString());
        state.Clear().Append("Changed");

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);

        Assert.Equal("Original", Assert.Single(destination.Entries).Message);
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void IsEnabled_BeforeReplay_AcceptsEveryMessageLevel(LogLevel level)
    {
        var journal = new LogJournal();

        Assert.True(journal.IsEnabled(level));
        Assert.False(journal.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void IsEnabled_AfterReplay_UsesDestinationFilter()
    {
        var journal = new LogJournal();
        var destination = new RecordingLogger(LogLevel.Warning);

        Assert.True(journal.IsEnabled(LogLevel.Debug));

        journal.ReplayTo(destination);

        Assert.False(journal.IsEnabled(LogLevel.Debug));
        Assert.True(journal.IsEnabled(LogLevel.Warning));
        Assert.False(journal.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void ReplayTo_DrainsBacklogThenForwardsAndCanOnlyBeCalledOnce()
    {
        var journal = new LogJournal();
        journal.LogInformation("Initialization started");
        journal.LogWarning("Configuration fallback used");

        var destination = new RecordingLogger();
        Assert.Empty(destination.Entries);

        journal.ReplayTo(destination);

        Assert.Equal(
            ["Initialization started", "Configuration fallback used"],
            destination.Entries.Select(entry => entry.Message));

        journal.LogInformation("Initialization completed");

        Assert.Equal(
            ["Initialization started", "Configuration fallback used", "Initialization completed"],
            destination.Entries.Select(entry => entry.Message));

        Assert.Throws<InvalidOperationException>(() => journal.ReplayTo(new RecordingLogger()));
    }

    [Fact]
    public void ReplayTo_RetainsDestinationForImmediateForwarding()
    {
        var journal = new LogJournal();
        journal.LogInformation("Startup");
        var destination = new RecordingLogger();
        journal.ReplayTo(destination);
        journal.LogInformation("Forwarded");

        Assert.Equal(["Startup", "Forwarded"], destination.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void ReplayTo_PreservesLogDataAndScopes()
    {
        var journal = new LogJournal();
        var exception = new InvalidOperationException("Failure");

        using (journal.BeginScope("Initialization"))
        {
            journal.LogError(
                new EventId(42, "ConfigurationError"),
                exception,
                "Could not load {FileName}",
                "appsettings.json");
        }

        var destination = new RecordingLogger();
        journal.ReplayTo(destination);

        RecordedLog entry = Assert.Single(destination.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(new EventId(42, "ConfigurationError"), entry.EventId);
        Assert.Same(exception, entry.Exception);
        Assert.Equal("Could not load appsettings.json", entry.Message);
        Assert.Equal(["Initialization"], entry.Scopes);
    }

    private sealed class CountingScopeState : IEnumerable<KeyValuePair<string, object?>>
    {
        public string Value { get; set; } = "Original";
        public int FormatCalls { get; private set; }
        public int EnumerationCalls { get; private set; }

        public override string ToString()
        {
            FormatCalls++;
            return Value;
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            EnumerationCalls++;
            yield return new KeyValuePair<string, object?>("Value", Value);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Deliberately retain the producer's state only to verify its identity and clearing.
    private sealed class StateProbeLogger(ILogger inner) : ILogger
    {
        public object? LastState { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LastState = state;
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class RecordingFactory(Action<(string Category, RecordedLog Log)>? onWrite = null)
        : ILoggerFactory
    {
        private readonly IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

        public List<string> Trace { get; } = [];
        public List<(string Category, RecordedLog Log)> Entries { get; } = [];
        public List<string> CreatedCategories { get; } = [];
        public List<ILogger> CreatedLoggers { get; } = [];
        public bool Disposed { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            CreatedCategories.Add(categoryName);
            var logger = new RecordingLogger(onWrite: entry =>
            {
                var categorized = (categoryName, entry);
                Entries.Add(categorized);
                onWrite?.Invoke(categorized);
            },
                scopeProvider: _scopeProvider, trace: Trace);
            CreatedLoggers.Add(logger);
            return logger;
        }

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLogger(
        LogLevel minimumLevel = LogLevel.Trace,
        Action<RecordedLog>? onWrite = null,
        IExternalScopeProvider? scopeProvider = null,
        List<string>? trace = null) : ILogger
    {
        private readonly IExternalScopeProvider _scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();

        public List<RecordedLog> Entries { get; } = [];
        public List<string> Trace { get; } = trace ?? [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            IDisposable scope = _scopeProvider.Push(state);
            Trace.Add($"Begin {state}");
            return new Scope(() =>
            {
                scope.Dispose();
                Trace.Add($"End {state}");
            });
        }

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<object?>();
            _scopeProvider.ForEachScope(static (scope, captured) => captured.Add(scope), scopes);
            var entry = new RecordedLog(
                logLevel,
                eventId,
                exception,
                formatter(state, exception),
                scopes.Select(scope => scope?.ToString() ?? string.Empty).ToArray(),
                ReadProperties(state),
                scopes.Select(scope => ReadProperties(scope)).ToArray());
            Entries.Add(entry);
            Trace.Add($"Log {entry.Message}");
            onWrite?.Invoke(entry);
        }

        private static IReadOnlyDictionary<string, object?> ReadProperties(object? state) =>
            state is IEnumerable<KeyValuePair<string, object?>> properties
                ? properties.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
    }

    private sealed record RecordedLog(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        string Message,
        IReadOnlyList<string> Scopes,
        IReadOnlyDictionary<string, object?> Properties,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> ScopeProperties);

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

internal static partial class GeneratedMessages
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Debug,
        Message = "Loaded {FileName} with {SettingCount} settings")]
    public static partial void Loaded(ILogger logger, string fileName, int settingCount);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error,
        Message = "Could not load {FileName}")]
    public static partial void Failed(ILogger logger, string fileName, Exception exception);
}
