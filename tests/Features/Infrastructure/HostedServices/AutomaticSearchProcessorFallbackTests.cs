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
        public Mock<IDownloadRepository> DownloadRepository { get; } = new();
        public Mock<IQuickAddOperationRepository> QuickAddOperations { get; } = new();
        public Mock<ISearchService> Search { get; } = new();
        public Mock<IAudiobookFileRepository> AudiobookFiles { get; } = new();
        public Mock<IAudiobookScanService> AudiobookScanService { get; } = new();
        public Mock<IAutomaticIndexerSearchService>? AutomaticIndexerSearch { get; private set; }
        public Mock<IQualityProfileService> QualityProfileService { get; } = new();
        public List<string> SearchQueries { get; } = [];
        public AutomaticSearchProcessor Processor { get; }

        public Fixture(params (string Id, int Score)[] results)
            : this(results, useAutomaticIndexerSearch: false)
        {
        }

        public Fixture((string Id, int Score)[] results, bool useAutomaticIndexerSearch)
        {
            if (useAutomaticIndexerSearch)
            {
                AutomaticIndexerSearch = Search.As<IAutomaticIndexerSearchService>();
            }
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

            var downloadRepository = DownloadRepository;
            downloadRepository.Setup(repository =>
                    repository.GetByAudiobookIdAsync(Audiobook.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            AudiobookFiles.Setup(repository =>
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

            QualityProfileService.Setup(service =>
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

            QuickAddOperations.Setup(repository => repository.GetByAudiobookIdAsync(
                    Audiobook.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((QuickAddOperation?)null);

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
            services.AddSingleton(AudiobookFiles.Object);
            services.AddSingleton(AudiobookScanService.Object);
            services.AddSingleton(Search.Object);
            services.AddSingleton(QualityProfileService.Object);
            services.AddSingleton(Downloads.Object);
            services.AddSingleton(Rejections.Object);
            services.AddSingleton(configurationService.Object);
            services.AddSingleton(indexerRepository.Object);
            services.AddSingleton(diagnosticRepository.Object);
            services.AddSingleton(QuickAddOperations.Object);
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
    public async Task SearchAudiobookAsync_PriorNoMatchWithoutRegisteredFile_RemainsSearchEligible()
    {
        using var fixture = new Fixture();
        var operation = new QuickAddOperation
        {
            Id = "prior-no-match",
            AudiobookId = fixture.Audiobook.Id,
            State = QuickAddOperationState.NoMatchingReleases
        };
        fixture.QuickAddOperations
            .Setup(repository => repository.GetByAudiobookIdAsync(
                fixture.Audiobook.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(operation);
        fixture.QuickAddOperations
            .Setup(repository => repository.RetryAsync(operation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        fixture.QuickAddOperations
            .Setup(repository => repository.AdvanceAsync(
                fixture.Audiobook.Id,
                It.IsAny<QuickAddOperationState>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        fixture.Search.Verify(service => service.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<List<string>?>(),
            It.IsAny<SearchSortBy>(),
            It.IsAny<SearchSortDirection>(),
            true), Times.Exactly(2));
        fixture.QuickAddOperations.Verify(
            repository => repository.RetryAsync(operation.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAudiobookAsync_RegisteredFileWithVerifiedGeneration_ResolvesPriorNoMatch()
    {
        using var fixture = new Fixture();
        var basePath = FileService.GetTempDirectory("verified-audiobook");
        fixture.Audiobook.BasePath = basePath;
        fixture.Audiobook.QualityProfile = new QualityProfile
        {
            Id = 1,
            Name = "MP3",
            CutoffQuality = "MP3",
            Qualities =
            [
                new QualityDefinition { Quality = "MP3", Priority = 1 }
            ]
        };
        var filePath = await FileService.GetFileAsync(basePath, "book.mp3", "audio bytes");
        var semantics = await _provider.GetRequiredService<IFileSystemSemanticsResolver>()
            .ResolveAsync(filePath, FileSystemCaseSensitivityMode.Auto);
        Assert.Equal(PathIdentityState.Valid, semantics.State);
        var file = new AudiobookFile
        {
            Id = 63,
            AudiobookId = fixture.Audiobook.Id,
            Size = new FileInfo(filePath).Length,
            Format = "mp3",
            Codec = "mp3",
            Bitrate = 128_000
        };
        file.ApplyPathIdentity(
            filePath,
            AudiobookFilePathIdentity.CreateValid(
                filePath,
                semantics.Semantics,
                FileSystemCaseSensitivityMode.Auto,
                semantics.BoundaryPath));
        file.ApplyPhysicalObjectIdentity("verified-physical-object", DateTime.UtcNow);
        fixture.AudiobookFiles
            .Setup(repository => repository.GetByAudiobookIdAsync(
                fixture.Audiobook.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([file]);
        fixture.AudiobookFiles
            .Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([file]);
        fixture.AudiobookScanService
            .Setup(service => service.RegisterExistingFileAsync(
                fixture.Audiobook.Id,
                basePath,
                filePath,
                "automatic-search-availability",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var operation = new QuickAddOperation
        {
            Id = "prior-no-match",
            AudiobookId = fixture.Audiobook.Id,
            State = QuickAddOperationState.NoMatchingReleases
        };
        fixture.QuickAddOperations
            .Setup(repository => repository.GetByAudiobookIdAsync(
                fixture.Audiobook.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(operation);
        fixture.QuickAddOperations
            .Setup(repository => repository.AdvanceAsync(
                fixture.Audiobook.Id,
                It.IsAny<QuickAddOperationState>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        fixture.AudiobookScanService.Verify(service => service.RegisterExistingFileAsync(
            fixture.Audiobook.Id,
            basePath,
            filePath,
            "automatic-search-availability",
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.QuickAddOperations.Verify(repository => repository.AdvanceAsync(
            fixture.Audiobook.Id,
            QuickAddOperationState.Imported,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Search.Verify(service => service.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<List<string>?>(),
            It.IsAny<SearchSortBy>(),
            It.IsAny<SearchSortDirection>(),
            true), Times.Never);
    }

    [Fact]
    public async Task SearchAudiobookAsync_ReplacementRetriesFailedQuickAddOperation()
    {
        using var fixture = new Fixture(("replacement", 90));
        var operation = new QuickAddOperation
        {
            Id = "operation-1",
            AudiobookId = fixture.Audiobook.Id,
            AudiobookTitle = fixture.Audiobook.Title ?? "Unknown Title",
            State = QuickAddOperationState.FailedNeedsAttention,
            DownloadId = "failed-download"
        };
        fixture.QuickAddOperations
            .Setup(repository => repository.GetByAudiobookIdAsync(
                fixture.Audiobook.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(operation);
        fixture.QuickAddOperations
            .Setup(repository => repository.RetryAsync(operation.Id, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                operation.State = QuickAddOperationState.SearchPending;
                operation.DownloadId = null;
            })
            .ReturnsAsync(true);

        await fixture.Processor.SearchAudiobookAsync(
            fixture.Audiobook.Id,
            CancellationToken.None,
            AutomaticSearchTrigger.Replacement);

        Assert.Equal(QuickAddOperationState.SearchPending, operation.State);
        Assert.Equal(["replacement"], fixture.SubmissionOrder);
        fixture.QuickAddOperations.Verify(
            repository => repository.RetryAsync(operation.Id, It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Theory]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Moved)]
    [InlineData(DownloadStatus.ImportBlocked)]
    public async Task SearchAudiobookAsync_DoesNotRegrabReleaseAlreadyDownloaded(DownloadStatus priorStatus)
    {
        using var fixture = new Fixture(("already-grabbed", 100), ("new-release", 80));
        fixture.DownloadRepository
            .Setup(repository => repository.GetByAudiobookIdAsync(
                fixture.Audiobook.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Download
                {
                    Title = "Release already-grabbed",
                    AudiobookId = fixture.Audiobook.Id,
                    Status = priorStatus,
                    Metadata = new Dictionary<string, object> { ["ReleaseId"] = "already-grabbed" }
                }
            ]);

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.DoesNotContain("already-grabbed", fixture.SubmissionOrder);
        Assert.Contains("new-release", fixture.SubmissionOrder);
    }

    [Fact]
    public async Task SearchAudiobookAsync_QueriesTitleAndSeriesPositionVariants()
    {
        using var fixture = new Fixture(("release", 100));
        fixture.Audiobook.Authors = ["Troy Ashford"];
        fixture.Audiobook.Series = "Mystic Seekers";
        fixture.Audiobook.SeriesNumber = "1";

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Contains("Test Book Troy Ashford", fixture.SearchQueries);
        Assert.Contains("Test Book", fixture.SearchQueries);
        Assert.Contains("Mystic Seekers 1 Troy Ashford", fixture.SearchQueries);
        Assert.Contains(fixture.Audiobook.Asin, fixture.SearchQueries);
        Assert.Equal(fixture.SearchQueries.Count, fixture.SearchQueries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task SearchAudiobookAsync_AcceptsHeartOfRadianceSeriesFirstCoauthoredRelease()
    {
        using var fixture = new Fixture(("aetherforged-book-3", 100));
        fixture.Audiobook.Title = "Heart of Radiance: A LitRPG Progression Fantasy";
        fixture.Audiobook.Authors = ["Luke Chmilenko"];
        fixture.Audiobook.Series = "Aetherforged";
        fixture.Audiobook.SeriesNumber = "3";
        fixture.Audiobook.Asin = "B0H493MK1M";
        fixture.Results[0].Asin = null;
        fixture.Results[0].Title = "Aetherforged, Book 3 - Heart of Radiance - Luke Chmilenko, Harmon Cooper [M4B] [128 Kbps]";
        fixture.Results[0].Artist = "Harmon Cooper";

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Contains("Heart of Radiance", fixture.SearchQueries);
        Assert.Equal(["aetherforged-book-3"], fixture.SubmissionOrder);
        Assert.Equal("DownloadQueued", Assert.Single(fixture.Diagnostics).FinalOutcome);
    }

    [Fact]
    public async Task SearchAudiobookAsync_ContinuesWithTitleOnlyWhenLongTitleQueryReturnsNoResults()
    {
        using var fixture = new Fixture(("title-only-release", 100));
        fixture.Audiobook.Title = "Heart of Radiance: A LitRPG Progression Fantasy";
        fixture.Audiobook.Authors = ["Luke Chmilenko"];
        fixture.Results[0].Title = "Heart of Radiance - Luke Chmilenko";
        fixture.Search.Setup(service => service.SearchAsync(
                "Heart of Radiance: A LitRPG Progression Fantasy",
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<SearchSortBy>(),
                It.IsAny<SearchSortDirection>(),
                true))
            .ReturnsAsync([]);

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        Assert.Contains("Heart of Radiance", fixture.SearchQueries);
        Assert.Equal(["title-only-release"], fixture.SubmissionOrder);
    }

    [Fact]
    public async Task SearchAudiobookAsync_ReportsQualityRejectionAfterIdentityAcceptance()
    {
        using var fixture = new Fixture(("correct-low-quality", 0));
        fixture.QualityProfileService.Setup(service =>
                service.ScoreSearchResults(fixture.Results, fixture.Audiobook.QualityProfile!))
            .ReturnsAsync([
                new QualityScore
                {
                    SearchResult = fixture.Results[0],
                    TotalScore = 0,
                    RejectionReasons = ["Quality profile rejected release"]
                }
            ]);

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        var diagnostic = Assert.Single(fixture.Diagnostics);
        Assert.Equal("NoSuitableReleases", diagnostic.FinalOutcome);
        Assert.Contains("Quality profile rejected release", diagnostic.RejectionReasons);
        Assert.Empty(fixture.SubmissionOrder);
    }

    [Fact]
    public async Task SearchAudiobookAsync_PersistsQueryAndPerIndexerDiagnostics()
    {
        using var fixture = new Fixture([("release", 100)], useAutomaticIndexerSearch: true);
        var automaticSearch = fixture.AutomaticIndexerSearch!;
        automaticSearch.Setup(service => service.SearchAutomaticQueryAsync(It.IsAny<string>()))
            .ReturnsAsync((string query) => new AutomaticIndexerSearchResponse(
                fixture.Results,
                [
                    new AutomaticIndexerQueryOutcome(
                        query,
                        "Test indexer",
                        "Succeeded",
                        fixture.Results.Count,
                        null)
                ]));

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        var diagnostic = Assert.Single(fixture.Diagnostics);
        Assert.Contains("Test Book", diagnostic.QueriesAttempted);
        Assert.Contains("Test indexer", diagnostic.IndexersQueried);
        Assert.All(diagnostic.IndexerOutcomes, outcome => Assert.Equal("Succeeded", outcome.Outcome));
        Assert.Equal(1, diagnostic.DistinctCandidateCount);
        Assert.Equal(1, diagnostic.IdentityMatchCount);
        Assert.Equal(0, diagnostic.QualityRejectedCount);
    }

    [Fact]
    public async Task SearchAudiobookAsync_ThreshingDay_UsesTitleOnlyAfterCompoundTitleAuthorQueryReturnsNothing()
    {
        using var fixture = new Fixture(("audiobook-bay-release", 100));
        fixture.Audiobook.Title = "Threshing Day: Wing and Claw Collection";
        fixture.Audiobook.Authors = ["Rebecca Yarros"];
        fixture.Results[0].Title = "Threshing Day - Rebecca Yarros";
        fixture.Search.Setup(service => service.SearchAsync(
                It.Is<string>(query => query.Contains("Wing and Claw", StringComparison.Ordinal)
                    || query.Contains("Rebecca Yarros", StringComparison.Ordinal)),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<SearchSortBy>(),
                It.IsAny<SearchSortDirection>(),
                true))
            .ReturnsAsync([]);

        await fixture.Processor.SearchAudiobookAsync(fixture.Audiobook.Id, CancellationToken.None);

        fixture.Search.Verify(service => service.SearchAsync(
            It.Is<string>(query => query.Contains("Wing and Claw", StringComparison.Ordinal)
                || query.Contains("Rebecca Yarros", StringComparison.Ordinal)),
            It.IsAny<string?>(),
            It.IsAny<List<string>?>(),
            It.IsAny<SearchSortBy>(),
            It.IsAny<SearchSortDirection>(),
            true), Times.AtLeastOnce);
        Assert.Contains("Threshing Day", fixture.SearchQueries);
        Assert.Equal(["audiobook-bay-release"], fixture.SubmissionOrder);
        Assert.Equal("DownloadQueued", Assert.Single(fixture.Diagnostics).FinalOutcome);
        Assert.True(fixture.Diagnostics.Single().ResultsReturned > 0);
    }

    [Fact]
    public async Task SearchAudiobookAsync_ContinuesAfterOneQueryVariantFails()
    {
        using var fixture = new Fixture();
        fixture.Audiobook.Authors = ["Troy Ashford"];
        fixture.Audiobook.Series = "Mystic Seekers";
        fixture.Audiobook.SeriesNumber = "1";
        fixture.Search.Setup(service => service.SearchAsync(
                "Test Book Troy Ashford",
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
