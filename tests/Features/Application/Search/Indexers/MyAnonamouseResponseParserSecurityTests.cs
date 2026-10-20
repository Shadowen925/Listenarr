using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Indexers;

[Trait("Name", nameof(MyAnonamouseResponseParserSecurityTests))]
[Trait("Category", "Application")]
public sealed class MyAnonamouseResponseParserSecurityTests : BaseTests
{
    [Fact]
    public void Parse_DownloadUrlWithMamId_DoesNotWriteCredentialToLogs()
    {
        const string mamId = "private-mam-session-token";
        const string json = """
            [
              {
                "guid": "https://www.myanonamouse.net/t/123",
                "dl": "abc123",
                "title": "Test"
              }
            ]
            """;
        var logger = new CapturingLogger();
        var indexer = new Indexer
        {
            Name = "MyAnonamouse",
            Url = "https://www.myanonamouse.net",
            Type = "Torrent",
            Implementation = "MyAnonamouse",
            AdditionalSettings = """{"mam_id":"private-mam-session-token"}"""
        };

        var results = MyAnonamouseResponseParser.Parse(json, indexer, logger);

        Assert.Single(results);
        Assert.Contains(mamId, results[0].TorrentUrl);
        var logOutput = string.Join(Environment.NewLine, logger.Messages);
        Assert.DoesNotContain(mamId, logOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("mam_id=", logOutput, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
