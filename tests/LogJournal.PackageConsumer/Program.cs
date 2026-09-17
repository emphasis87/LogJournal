using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using LogJournal;
using Microsoft.Extensions.Logging;
using StandaloneLogJournal = LogJournal.LogJournal;

if (args.Length != 1)
{
    throw new ArgumentException("Pass the path to the generated .nupkg file.");
}

using (ZipArchive package = ZipFile.OpenRead(args[0]))
{
    foreach (string path in new[] { "lib/net8.0/LogJournal.dll", "lib/net8.0/LogJournal.xml", "README.md", "LICENSE" })
    {
        if (package.GetEntry(path) is null)
        {
            throw new InvalidOperationException($"The package is missing {path}.");
        }
    }

    using Stream manifest = package.GetEntry("LogJournal.nuspec")!.Open();
    XDocument document = XDocument.Load(manifest);
    XNamespace ns = document.Root!.Name.Namespace;
    string[] dependencies = document.Descendants(ns + "dependency")
        .Select(element => (string)element.Attribute("id")!).ToArray();
    if (!dependencies.SequenceEqual(["Microsoft.Extensions.Logging.Abstractions"]))
    {
        throw new InvalidOperationException("Unexpected package dependencies.");
    }

    using Stream packagedAssembly = package.GetEntry("lib/net8.0/LogJournal.dll")!.Open();
    using Stream restoredAssembly = File.OpenRead(typeof(StandaloneLogJournal).Assembly.Location);
    if (!SHA256.HashData(packagedAssembly).SequenceEqual(SHA256.HashData(restoredAssembly)))
    {
        throw new InvalidOperationException("The restored assembly differs from the package. Clear the consumer's obj directory and retry.");
    }
}

using (ZipArchive symbols = ZipFile.OpenRead(Path.ChangeExtension(args[0], ".snupkg")))
{
    if (symbols.GetEntry("lib/net8.0/LogJournal.pdb") is null)
    {
        throw new InvalidOperationException("The symbols package is missing the library PDB.");
    }
}

using var destination = new RecordingFactory();
using var fallbackOutput = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
var fallbackLogger = new TextWriterLogger(fallbackOutput, categoryName: "Fallback");
using (fallbackLogger.BeginScope("Startup"))
{
    fallbackLogger.LogError(new EventId(7, "Failed"), "Logging initialization failed");
}
if (!fallbackOutput.ToString().Contains("[Error] Fallback[7:Failed] => Startup Logging initialization failed"))
{
    throw new InvalidOperationException("The packaged TextWriterLogger produced unexpected default output.");
}

var journal = new StandaloneLogJournal();
if (journal.HasErrors || journal.HasBacklog)
{
    throw new InvalidOperationException("A new journal unexpectedly reports buffered errors.");
}
using (journal.BeginScope("Startup"))
{
    journal.LogInformation("Loaded {Count} settings", 12);
    journal.LogInformation("Ready");
}
if (!journal.HasBacklog || journal.HasErrors)
{
    throw new InvalidOperationException("The journal did not report its informational backlog correctly.");
}
journal.ReplayTo(destination.CreateLogger("Standalone"));
if (journal.HasErrors || journal.HasBacklog)
{
    throw new InvalidOperationException("A replayed journal unexpectedly reports buffered errors.");
}
journal.LogInformation("Running");

using var journals = new LogJournalFactory();
if (journals.HasErrors || journals.HasBacklog)
{
    throw new InvalidOperationException("A new journal factory unexpectedly reports buffered errors.");
}
ILogger first = journals.CreateLogger("First");
ILogger second = journals.CreateLogger("Second");
using (first.BeginScope("Shared"))
{
    first.LogInformation("One");
    second.LogInformation("Two");
}
journals.ReplayTo(destination);
second.LogInformation("Three");

string[] expected = ["Standalone|Startup|Loaded 12 settings", "Standalone|Startup|Ready",
    "Standalone||Running", "First|Shared|One", "Second|Shared|Two", "Second||Three"];
if (!destination.Messages.SequenceEqual(expected))
{
    throw new InvalidOperationException("Packaged journal replay produced unexpected messages or scopes.");
}

using var factoryOutput = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
using var fallbackFactory = new TextWriterLoggerFactory(factoryOutput, entry =>
    $"{entry.CategoryName}|{entry.Message}");
journals.ReplayTo(fallbackFactory);
first.LogInformation("Four");
if (factoryOutput.ToString() != $"First|Four{Environment.NewLine}")
{
    throw new InvalidOperationException("The packaged TextWriterLoggerFactory produced unexpected output.");
}

Console.WriteLine("NuGet package contents, writer logging, replay, and destination replacement verified.");

internal sealed class RecordingFactory : ILoggerFactory
{
    private readonly IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }

    private sealed class RecordingLogger(RecordingFactory owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            owner._scopes.ForEachScope(static (scope, values) => values.Add(scope?.ToString() ?? ""), scopes);
            owner.Messages.Add($"{category}|{string.Join("/", scopes)}|{formatter(state, exception)}");
        }
    }
}
