using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Indexers;

[Trait("Name", nameof(IndexerSearchWorkflowAutomationTests))]
[Trait("Category", "Application")]
public sealed class IndexerSearchWorkflowAutomationTests : BaseTests
{
    [Fact]
    public async Task SearchIndexersAsync_AutomaticSearchWithoutConfiguredIndexers_ReturnsNoSyntheticReleases()
    {
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetEnabledAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var workflow = CreateWorkflow(repository.Object, []);

        var results = await workflow.SearchIndexersAsync("Book", isAutomaticSearch: true);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchIndexersAsync_AutomaticSearchWhenEveryIndexerFails_ThrowsForRetryDiagnostics()
    {
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetEnabledAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Indexer { Name = "Offline", Implementation = "Torznab", IsEnabled = true }]);

        var provider = new Mock<IIndexerSearchProvider>();
        provider.SetupGet(value => value.IndexerType).Returns("Torznab");
        provider.Setup(value => value.SearchAsync(
                It.IsAny<Indexer>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<SearchRequest?>()))
            .ThrowsAsync(new HttpRequestException("Indexer unavailable"));
        var workflow = CreateWorkflow(repository.Object, [provider.Object]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.SearchIndexersAsync("Book", isAutomaticSearch: true));

        Assert.Contains("All 1 enabled indexer", exception.Message, StringComparison.Ordinal);
    }

    private static IndexerSearchWorkflow CreateWorkflow(
        IIndexerRepository repository,
        IEnumerable<IIndexerSearchProvider> providers) =>
        new(
            new HttpClient(),
            Mock.Of<IConfigurationService>(),
            repository,
            providers,
            new IndexerAdditionalSettingsParser(NullLogger<IndexerAdditionalSettingsParser>.Instance),
            NullLogger<IndexerSearchWorkflow>.Instance);
}
