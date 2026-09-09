using LogJournal;
using Microsoft.Extensions.Logging;
using StandaloneLogJournal = LogJournal.LogJournal;

var startupLogger = new StandaloneLogJournal();

startupLogger.LogInformation("Application initialization started");

using (startupLogger.BeginScope("Component: {Component}", "Configuration"))
{
    startupLogger.LogDebug("Loading configuration from {FileName}", "appsettings.json");
    startupLogger.LogInformation("Configuration loaded with {SettingCount} settings", 12);
}

startupLogger.LogWarning(
    new EventId(1001, "OptionalDependencyMissing"),
    "Optional dependency {Dependency} is not available; initialization continues",
    "MetricsExporter");

using var journalFactory = new LogJournalFactory();
ILogger configurationLogger = journalFactory.CreateLogger("Configuration");
ILogger databaseLogger = journalFactory.CreateLogger("Database");

using (configurationLogger.BeginScope("Initialization phase: {Phase}", "Services"))
{
    configurationLogger.LogInformation("Loading service configuration");
    databaseLogger.LogInformation("Initializing database client");
    configurationLogger.LogInformation("Service configuration completed");
}

// In a real application, add all final providers and settings here.
using var finalLoggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    });
});

startupLogger.LogInformation("Final logging is ready; replaying buffered messages");
ILogger finalStartupLogger = finalLoggerFactory.CreateLogger("Startup");
startupLogger.ReplayTo(finalStartupLogger);
journalFactory.ReplayTo(finalLoggerFactory);

// Existing journal loggers now forward directly through the final logging pipeline.
startupLogger.LogInformation("Application initialization completed");

ILogger applicationLogger = finalLoggerFactory.CreateLogger("Application");
applicationLogger.LogInformation("Application is running");
