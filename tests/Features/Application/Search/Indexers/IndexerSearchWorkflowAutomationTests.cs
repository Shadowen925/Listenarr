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

    [Fact]
    public async Task SearchIndexersWithDiagnosticsAsync_ReportsUnsupportedImplementationAsFailure()
    {
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetEnabledAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Indexer { Name = "Prowlarr Custom", Implementation = "Custom", IsEnabled = true }]);
        var workflow = CreateWorkflow(repository.Object, []);

        var response = await workflow.SearchIndexersWithDiagnosticsAsync("Book", isAutomaticSearch: true);

        Assert.Empty(response.Results);
        var outcome = Assert.Single(response.IndexerOutcomes);
        Assert.Equal("Failed", outcome.Status);
        Assert.Equal("UnsupportedIndexerImplementation", outcome.FailureReason);
    }

    [Fact]
    public async Task SearchIndexersWithDiagnosticsAsync_PreservesPartialIndexerFailure()
    {
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetEnabledAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Indexer { Name = "Available", Implementation = "Torznab", IsEnabled = true },
                new Indexer { Name = "Offline", Implementation = "Newznab", IsEnabled = true }
            ]);

        var provider = new Mock<IIndexerSearchProvider>();
        provider.SetupGet(value => value.IndexerType).Returns("Torznab");
        provider.Setup(value => value.SearchAsync(
                It.Is<Indexer>(indexer => indexer.Name == "Available"),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<SearchRequest?>()))
            .ReturnsAsync([]);
        provider.Setup(value => value.SearchAsync(
                It.Is<Indexer>(indexer => indexer.Name == "Offline"),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<SearchRequest?>()))
            .ThrowsAsync(new HttpRequestException("Indexer unavailable"));
        var workflow = CreateWorkflow(repository.Object, [provider.Object]);

        var response = await workflow.SearchIndexersWithDiagnosticsAsync("Book", isAutomaticSearch: true);

        Assert.Equal(2, response.IndexerOutcomes.Count);
        Assert.Contains(response.IndexerOutcomes, outcome =>
            outcome.IndexerName == "Available" && outcome.Status == "Succeeded");
        Assert.Contains(response.IndexerOutcomes, outcome =>
            outcome.IndexerName == "Offline" && outcome.Status == "Failed");
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
