# LogJournal

A replayable log journal for Microsoft.Extensions.Logging.

## Introduction

`LogJournal` directly implements the standard Microsoft.Extensions.Logging
(MEL) `ILogger` interface and captures messages during application initialization.
Once final logging is configured, `ReplayTo` replays the current batch in order
to the supplied destination `ILogger` and removes successfully delivered entries.
It neither retains the destination logger nor forwards subsequent writes to it.

A standalone `new LogJournal()` always owns its storage. The optional
`LogJournalFactory : ILoggerFactory` creates loggers that share storage within
that factory. Both variants use the same internal buffering and snapshot
implementation in `LogJournalStore`.

The journal preserves `LogLevel`, `EventId`, exceptions, message templates,
structured arguments, and active scopes. For a standalone journal, the category
is determined by the destination logger. The buffer is in memory only and is
intended for a short initialization phase. Timestamps emitted by the destination
provider reflect replay time because MEL `ILogger` has no original timestamp parameter.

## Running the example

Building this repository requires the .NET 10 SDK selected by `global.json`.
Running the sample and tests also requires the .NET 8 runtime. The library targets
`net8.0` and depends only on `Microsoft.Extensions.Logging.Abstractions`; it does
not require the ASP.NET Core shared framework.

```powershell
dotnet run --project samples/LogJournal.Sample/LogJournal.Sample.csproj
```

Switching from initialization logging to final logging:

```csharp
var logger = new LogJournal();
logger.LogInformation("Buffered during startup");

using var finalFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole());

ILogger finalLogger = finalFactory.CreateLogger("Startup");
logger.ReplayTo(finalLogger);
finalLogger.LogInformation("Written directly to the final logger");
```

The caller manages the lifetime of the destination logger and its `ILoggerFactory`.

## Repository layout

```text
src/LogJournal/                  Library and NuGet package project
samples/LogJournal.Sample/       Standalone and factory initialization example
tests/LogJournal.Tests/          Unit and regression tests
tests/LogJournal.PackageConsumer/ Consumer of the generated NuGet package
.github/workflows/               CI and release publishing
```

The solution includes the library, sample, and unit tests. The packaged consumer
is intentionally outside the solution because its restore requires the package
to have been built first. The public types currently use the `LogJournalExample`
namespace, preserved from the original example.

## NuGet packaging

The initial package version is `1.0.0-alpha1`. To build, test, and create local packages:

```powershell
dotnet build LogJournal.slnx --configuration Release
dotnet test LogJournal.slnx --configuration Release --no-build
dotnet pack src/LogJournal/LogJournal.csproj --configuration Release --no-build --output artifacts
dotnet run --project tests/LogJournal.PackageConsumer/LogJournal.PackageConsumer.csproj --configuration Release -- artifacts/LogJournal.1.0.0-alpha1.nupkg
```

Packing produces `LogJournal.<version>.nupkg` and `LogJournal.<version>.snupkg`.
The main package includes the library, XML API documentation, this README, and
the MIT license. The symbols package contains portable debugging symbols. The
.NET SDK supplies GitHub Source Link metadata when building committed source.

The consumer restores from the local `artifacts` feed into its own `obj/packages`
cache and uses a `PackageReference`, not a project reference. It verifies the package
contents and dependencies, then exercises standalone and factory replay. When
repacking the same version locally, clear the consumer's `obj` directory before
rerunning it so NuGet does not reuse an older cached package.

After publishing, consumers can reference the package with:

```xml
<PackageReference Include="LogJournal" Version="1.0.0-alpha1" />
```

Package availability and ownership of the `LogJournal` NuGet ID must be confirmed
before the first public release. Preparing a package does not publish or reserve it.

## GitHub Actions

`ci.yml` runs on pushes and pull requests. On Linux it restores, builds, tests,
packs, runs the packaged consumer and sample, and uploads the NuGet and symbols
packages as workflow artifacts. It requires no publishing credentials.

`publish.yml` runs when a GitHub release is published. The release tag supplies
the package version, for example `v1.0.0-alpha1`. The workflow validates the tag,
builds and tests the solution, packs that version, runs the packaged consumer
and sample, and pushes to nuget.org using the `NUGET_API_KEY` repository secret.
NuGet push also discovers the corresponding `.snupkg` file. Duplicate package
versions are skipped; a new version is required to publish changed content.

No release or package publication is performed by local build and test commands.

## Multiple categories

```csharp
using var journals = new LogJournalFactory();
ILogger configuration = journals.CreateLogger("Configuration");
ILogger database = journals.CreateLogger("Database");

using (configuration.BeginScope("Phase: {Phase}", "Initialization"))
{
    configuration.LogInformation("Loading configuration");
    database.LogInformation("Connecting to database");
    configuration.LogInformation("Configuration completed");
}

using var finalFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(options => options.IncludeScopes = true));

journals.ReplayTo(finalFactory);
finalFactory.CreateLogger("Application").LogInformation("Application is running");
```

`CreateLogger` returns an `ILogger` for writing entries, not an independently
replayable `LogJournal`. The factory controls replay and draining of shared storage.
The standard MEL `journals.CreateLogger<T>()` extension is also supported.

Repeated `CreateLogger(string)` calls with the same category return the same
instance within a factory, including concurrent calls. Categories are compared
using `StringComparer.Ordinal`. Each factory has its own cache, cleared on
`Dispose()`. Replay does not change this cache. Destination loggers are not part of it.

Each entry in shared storage contains its category. `ReplayTo(ILoggerFactory)`
replays messages in the order they entered storage, rather than grouping them
by category. A shared lock determines the order of concurrent writes. Destination
loggers are created by category and retained only for the current replay call.

Scopes are shared between loggers from the same factory in the current
asynchronous context and flow across `await`. Different factories and standalone
journals have independent storage and scopes. Ordering across separate stores
is not guaranteed.

Calling `Dispose()` on the journal factory discards pending entries. Its existing
loggers then return `false` from `IsEnabled`; writing, opening a scope, creating
a logger, and replaying throw `ObjectDisposedException`. The destination factory
is not disposed. `AddProvider` is unsupported and throws `NotSupportedException`:
configure providers only on the final factory.

## Snapshots and replay

The journal does not automatically detect when configuration is complete.
After configuration, the application explicitly calls
`ReplayTo(finalFactory.CreateLogger("Startup"))`. This synchronously delivers
the current batch to the destination logger. Subsequent journal writes are
buffered again and require another explicit `ReplayTo` call, possibly with a
different logger. Replaying an empty journal does nothing. `IsEnabled` remains
`true` for all levels except `LogLevel.None`, regardless of the previous destination.
After initialization, use the final logger directly for normal application operation.
The destination provider may process delivered messages asynchronously.
Complete all initialization tasks before replaying; later writes would remain
buffered until another explicit replay. The same applies to the journal factory.

Replay stops if the destination logger throws. Successfully delivered entries
have been removed; the current and subsequent entries remain available for retry.
If the destination processes an entry and then throws, retrying may duplicate
that entry. Delivery is not transactional and has no exactly-once guarantee.

During `Log<TState>()`, the original formatter is invoked and structured properties
exposed as `IEnumerable<KeyValuePair<string, object?>>` are copied, including
`{OriginalFormat}`. Neither the original `TState` nor its formatter is retained.
The producer can therefore clear or reuse its argument container after `Log()`
returns. Replay passes a custom state implementing `IReadOnlyList` of properties
and a formatter returning the captured text, rather than the original `TState` type.

Scope text and structured properties are captured once, at `BeginScope`, and
remain fixed for that scope's lifetime. Replacing or removing entries in the
original property container does not affect the captured snapshot, even before
the first log message. Open a new scope when the context changes, or put changing
values in individual messages.

Scopes form an immutable parent-linked chain. Each log entry retains only the
current node reference: scope capture is O(1) with no per-message scope-copy
allocation. Nodes share their ancestors without retaining the original scope
container or its disposal handle. Message state is still snapshotted on every write.

The replay loop compares scope nodes by identity and keeps common ancestors open
across consecutive messages, including messages from different categories.
It closes scopes that have been left and opens newly entered scopes. Separate
scopes with identical text are not merged. Interleaved asynchronous contexts may
require reopening a scope. All scopes created during replay are closed at the
end of each batch, including on failure; the destination's existing ambient
scopes are left intact. Scopes without messages are not replayed, and their
original elapsed duration is not reproduced.

The destination provider must support scopes. Factory replay expects standard
MEL behavior with scope context shared across categories.

Message and scope property copies are shallow: mutable objects stored as values
are not deep-cloned. Captured message and scope text remains stable, but those
objects may change. An immutable scope chain does not make such objects immutable.
Exceptions are also retained by reference. Prefer immutable arguments and scalar
values. Provider-internal context and `Activity` are not automatically captured.

## Tests

```powershell
dotnet test LogJournal.slnx
```

`ReplayTo_PreservesFormattedLogValuesFromMelExtensions` uses the standard MEL
`LogInformation` and `LogError` methods. It verifies the incoming `FormattedLogValues`
type, formatted text, preservation of numeric arguments as numbers, `{OriginalFormat}`,
`EventId`, and exceptions during replay, including writes to a subsequent batch.

The test project uses the `Microsoft.Gen.Logging` generator from
`Microsoft.Extensions.Telemetry.Abstractions` version `8.0.0`. The standard MEL
generator is disabled in this project to prevent both generators from generating
the same `[LoggerMessage]` methods. The production project does not require this package.

`ReplayTo_PreservesGeneratedLoggerMessageCallsWithReusedThreadLocalState` invokes
actual generated methods before and after the first replay. It verifies text,
structured arguments, templates, exceptions, and the identity of the reused
`LoggerMessageState`, including its clearing after the generated method returns.
The generator uses `LoggerMessageHelper.ThreadLocalState`, `TagArray`, and
`state.Clear()`, rather than a callback created through `LoggerMessage.Define`.
The static formatter passed to `ILogger.Log` remains part of the MEL contract.

After building, generated code can be inspected at
`tests/LogJournal.Tests/obj/Debug/net8.0/generated/Microsoft.Gen.Logging/Microsoft.Gen.Logging.LoggingGenerator/Logging.g.cs`.

A separate regression test simulates a producer with a `[ThreadStatic]` container
and counts formatter invocations. Scope tests verify that text and properties
are captured only once at `BeginScope`, and that later mutations do not affect
the snapshot. Additional tests verify formatting of unstructured message state
at write time.
Replay tests verify separate batches, changing destinations, and the absence of
duplicates when replaying a drained journal. A `WeakReference` test verifies that
the destination logger can be collected after `ReplayTo` returns while the journal
remains alive.

Factory tests verify shared ordering and categories, isolation between standalone
journals and different factories, scopes across `await`, and concurrent writes
from multiple tasks. They also cover generated `LoggerMessage` writes through
the factory, separate batches, disposal behavior, and garbage collection of the
destination factory and its loggers.
Scope lifetime tests check the exact sequence of opening, writing, and closing
for nested scopes, identical values with different identities, interleaved
asynchronous contexts, multiple categories, batch boundaries, and replay failures.
