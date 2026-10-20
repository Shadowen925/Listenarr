using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Indexers;

[Trait("Name", nameof(IndexerTestWorkflowGenericTests))]
[Trait("Category", "IndexerTestWorkflow")]
public sealed class IndexerTestWorkflowGenericTests : BaseTests
{
    [Fact]
    public async Task TestGenericIndexerAsync_CustomImplementationFailsWithProtocolGuidance()
    {
        var workflow = new IndexerTestWorkflow(
            Mock.Of<IIndexerRepository>(),
            new HttpClient(),
            NullLogger<IndexerTestWorkflow>.Instance);
        var indexer = new Indexer
        {
            Name = "Prowlarr",
            Url = "http://localhost:9696/1/api",
            Implementation = "Custom",
            Type = "Usenet",
            ApiKey = "test-key"
        };

        var result = await workflow.TestGenericIndexerAsync(indexer, persist: false);

        Assert.False(result.Succeeded);
        Assert.Contains("Newznab for Usenet protocol", result.Message, StringComparison.Ordinal);
        Assert.Contains("Torznab for torrent protocol", result.Message, StringComparison.Ordinal);
    }
}
