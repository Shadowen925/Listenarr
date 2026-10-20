using Microsoft.AspNetCore.SignalR;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.HostedServices;

[Trait("Name", nameof(AutomaticSearchProcessorFallbackTests))]
[Trait("Category", "Infrastructure")]
public sealed class AutomaticSearchProcessorFallbackTests : BaseTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(_ => { });

        public Audiobook Audiobook { get; } = new()
        {
            Id = 17,
            Title = "Test Book",
            Asin = "B000TEST01",
            Monitored = true,
            QualityProfile = new QualityProfile { Id = 1, Name = "Default" }
        };
        public List<SearchResult> Results { get; }
        public List<string> SubmissionOrder { get; } = [];
        public List<AutomaticSearchDiagnostic> Diagnostics { get; } = [];
        public Mock<IReleaseRejectionRepository> Rejections { get; } = new();
        public Mock<IDownloadService> Downloads { get; } = new();
        public Mock<ISearchService> Search { get; } = new();
        public List<string> SearchQueries { get; } = [];
        public AutomaticSearchProcessor Processor { get; }

        public Fixture(params (string Id, int Score)[] results)
        {
            Results = results.Select(result => new SearchResult
            {
                Id = result.Id,
                Title = $"Release {result.Id}",
                Asin = Audiobook.Asin,
                DownloadType = "Usenet",
                NzbUrl = $"https://indexer.example/{result.Id}.nzb",
                Quality = "MP3 320kbps"
            }).ToList();

            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository.Setup(repository => repository.GetByIdAsync(Audiobook.Id))
                .ReturnsAsync(Audiobook);
            audiobookRepository.Setup(repository => repository.UpdateAsync(It.IsAny<Audiobook>()))
                .ReturnsAsync(true);

            var downloadRepository = new Mock<IDownloadRepository>();
            downloadRepository.Setup(repository =>
                    repository.GetByAudiobookIdAsync(Audiobook.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var audiobookFileRepository = new Mock<IAudiobookFileRepository>();
            audiobookFileRepository.Setup(repository =>
                    repository.GetByAudiobookIdAsync(Audiobook.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            Search.Setup(service => service.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    true))
                .Callback<string, string?, List<string>?, SearchSortBy, SearchSortDirection, bool>(
                    (query, _, _, _, _, _) => SearchQueries.Add(query))
                .ReturnsAsync(Results);

            var qualityProfileService = new Mock<IQualityProfileService>();
            qualityProfileService.Setup(service =>
                    service.ScoreSearchResults(Results, Audiobook.QualityProfile!))
                .ReturnsAsync(results.Select((result, index) => new QualityScore
                {
                    SearchResult = Results[index],
                    TotalScore = result.Score
                }).ToList());

            var settings = new ApplicationSettings { AutomaticSearchIntervalHours = 12 };
            var client = new DownloadClientConfiguration
            {
                Id = "client-1",
                Name = "SABnzbd",
                Type = "sabnzbd",
                IsEnabled = true
            };
            var configurationService = new Mock<IConfigurationService>();
            configurationService.Setup(service => service.GetApplicationSettingsAsync())
                .ReturnsAsync(settings);
            configurationService.Setup(service => service.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync([client]);
            configurationService.Setup(service => service.GetDownloadClientConfigurationAsync(client.Id))
                .ReturnsAsync(client);

            var indexerRepository = new Mock<IIndexerRepository>();
            indexerRepository.Setup(repository =>
                    repository.GetEnabledAsync(true, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new Indexer { Name = "Test indexer" }]);

            Rejections.Setup(repository => repository.ListAsync(
                    Audiobook.Id,
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            Rejections.Setup(repository => repository.AddAsync(
                    It.IsAny<ReleaseRejection>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            Downloads.Setup(service => service.StartDownloadAsync(
                    It.IsAny<SearchResult>(),
                    client.Id,
                    Audiobook.Id))
                .Returns((SearchResult result, string _, int? _) =>
                {
                    SubmissionOrder.Add(result.Id);
                    return Task.FromResult($"download-{result.Id}");
                });

            var diagnosticRepository = new Mock<IAutomaticSearchDiagnosticRepository>();
            diagnosticRepository.Setup(repository => repository.AddAsync(
                    It.IsAny<AutomaticSearchDiagnostic>(),
                    It.IsAny<CancellationToken>()))
                .Callback<AutomaticSearchDiagnostic, CancellationToken>((diagnostic, _) =>
                    Diagnostics.Add(diagnostic))
                .Returns(Task.CompletedTask);

            var clientProxy = new Mock<IClientProxy>();
            clientProxy.Setup(proxy => proxy.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(clients => clients.All).Returns(clientProxy.Object);
            var hubContext = new Mock<IHubContext<DownloadHub>>();
            hubContext.Setup(context => context.Clients).Returns(hubClients.Object);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(audiobookRepository.Object);
            services.AddSingleton(downloadRepository.Object);
            services.AddSingleton(audiobookFileRepository.Object);
            services.AddSingleton(Search.Object);
            services.AddSingleton(qualityProfileService.Object);
            services.AddSingleton(Downloads.Object);
            services.AddSingleton(Rejections.Object);
            services.AddSingleton(configurationService.Object);
            services.AddSingleton(indexerRepository.Object);
            services.AddSingleton(diagnosticRepository.Object);
            services.AddSingleton(hubContext.Object);
            _provider = services.BuildServiceProvider();
            Processor = new AutomaticSearchProcessor(
                _loggerFactory.CreateLogger<AutomaticSearchProcessor>(),
                _provider.GetRequiredService<IServiceScopeFactory>());
        }

        public void Dispose()
        {
            _provider.Dispose();
            _loggerFactory.Dispose();
        }
    }

    [Fact]
    public async Task SearchAudiobookAsync_FallsBackToNextRankedReleaseAfterClientUnavailable()
    {
        using var fixture = new Fixture(("best", 100), ("next", 80));
        fixture.Downloads.Setup(service => service.StartDownloadAsync(
                It.Is<SearchResult>(result => result.Id == "best"),
                "client-1",
                fixture.Audiobook.Id))
            .Callback<SearchResult, string, int?>((result, _, _) =>
                fixture.SubmissionOrder.Add(result.Id))
            .ThrowsAsync(new DownloadClientSubmissionException(
                "Client unavailable",
                failureKind: DownloadSubmissionFailureKind.ClientUnavailable));

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Equal(["best", "next"], fixture.SubmissionOrder);
        Assert.DoesNotContain(fixture.Diagnostics.Single().SubmissionAttempts, attempt =>
            attempt.FailureReason == "ReleaseSourceRejected");
        Assert.Equal("DownloadQueued", fixture.Diagnostics.Single().FinalOutcome);
        fixture.Rejections.Verify(repository => repository.AddAsync(
            It.IsAny<ReleaseRejection>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAudiobookAsync_RecordsAllFailuresAndLeavesPeriodicRetryAvailable()
    {
        using var fixture = new Fixture(("best", 100), ("next", 80));
        fixture.Downloads.Setup(service => service.StartDownloadAsync(
                It.IsAny<SearchResult>(),
                "client-1",
                fixture.Audiobook.Id))
            .Callback<SearchResult, string, int?>((result, _, _) =>
                fixture.SubmissionOrder.Add(result.Id))
            .ThrowsAsync(new DownloadClientSubmissionException(
                "Client unavailable",
                failureKind: DownloadSubmissionFailureKind.ClientUnavailable));

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        var diagnostic = Assert.Single(fixture.Diagnostics);
        Assert.Equal(["best", "next"], fixture.SubmissionOrder);
        Assert.Equal("AllSubmissionsFailed", diagnostic.FinalOutcome);
        Assert.NotNull(diagnostic.NextRetryAt);
        Assert.Empty(diagnostic.RejectionReasons);
    }

    [Fact]
    public async Task SearchAudiobookAsync_StopsWhenSubmissionIsDuplicatePrevented()
    {
        using var fixture = new Fixture(("best", 100), ("next", 80));
        fixture.Downloads.Setup(service => service.StartDownloadAsync(
                It.IsAny<SearchResult>(),
                "client-1",
                fixture.Audiobook.Id))
            .Returns((SearchResult result, string _, int? _) =>
            {
                fixture.SubmissionOrder.Add(result.Id);
                return Task.FromResult(string.Empty);
            });

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Equal(["best"], fixture.SubmissionOrder);
        Assert.Equal("DuplicatePrevented", Assert.Single(fixture.Diagnostics).FinalOutcome);
    }

    [Fact]
    public async Task SearchAudiobookAsync_RejectsReleaseSpecificFailureThenSubmitsAlternate()
    {
        using var fixture = new Fixture(("bad-source", 100), ("good-source", 80));
        fixture.Downloads.Setup(service => service.StartDownloadAsync(
                It.Is<SearchResult>(result => result.Id == "bad-source"),
                "client-1",
                fixture.Audiobook.Id))
            .Callback<SearchResult, string, int?>((result, _, _) =>
                fixture.SubmissionOrder.Add(result.Id))
            .ThrowsAsync(new DownloadClientSubmissionException("Invalid release source"));

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Equal(["bad-source", "good-source"], fixture.SubmissionOrder);
        fixture.Rejections.Verify(repository => repository.AddAsync(
            It.Is<ReleaseRejection>(rejection =>
                rejection.AudiobookId == fixture.Audiobook.Id &&
                rejection.ReleaseKey == ReleaseRejection.IdKey("bad-source")),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("DownloadQueued", Assert.Single(fixture.Diagnostics).FinalOutcome);
    }

    [Fact]
    public async Task SearchAudiobookAsync_QueriesTitleAndSeriesPositionVariants()
    {
        using var fixture = new Fixture(("release", 100));
        fixture.Audiobook.Authors = ["Troy Ashford"];
        fixture.Audiobook.Series = "Mystic Seekers";
        fixture.Audiobook.SeriesNumber = "1";

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Contains("Test Book Troy Ashford Mystic Seekers", fixture.SearchQueries);
        Assert.Contains("Mystic Seekers 1 Troy Ashford", fixture.SearchQueries);
        Assert.Contains(fixture.Audiobook.Asin, fixture.SearchQueries);
        Assert.Equal(fixture.SearchQueries.Count, fixture.SearchQueries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task SearchAudiobookAsync_ContinuesAfterOneQueryVariantFails()
    {
        using var fixture = new Fixture();
        fixture.Audiobook.Authors = ["Troy Ashford"];
        fixture.Audiobook.Series = "Mystic Seekers";
        fixture.Audiobook.SeriesNumber = "1";
        fixture.Search.Setup(service => service.SearchAsync(
                "Test Book Troy Ashford Mystic Seekers",
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<SearchSortBy>(),
                It.IsAny<SearchSortDirection>(),
                true))
            .ThrowsAsync(new HttpRequestException("Indexer query failed"));

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Contains("Mystic Seekers 1 Troy Ashford", fixture.SearchQueries);
        Assert.Equal("PartialIndexerFailure", Assert.Single(fixture.Diagnostics).FinalOutcome);
    }
}
