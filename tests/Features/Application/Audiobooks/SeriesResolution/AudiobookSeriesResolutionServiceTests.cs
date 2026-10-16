using Listenarr.Application.Audiobooks.Common;
using Listenarr.Application.Audiobooks.SeriesResolution;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.SeriesResolution;

[Trait("Name", "AudiobookSeriesResolutionServiceTests")]
[Trait("Category", "Application")]
public sealed class AudiobookSeriesResolutionServiceTests : BaseTests
{
    private const string ImportedSeriesName = "Dungeon Crawler Carl";
    private const string AudibleSeriesAsin = "SERIES123";

    [Fact]
    public async Task PreviewAsync_ResolvesBookAsinsToAudibleSeriesAndPositions()
    {
        var audiobook = CreateAudiobook(1, "BOOK1", "1");
        var membership = CreateMembership(11, 1, ImportedSeriesName, "1", "abs-series-1");
        var service = CreateService(
            [audiobook],
            new Dictionary<int, List<AudiobookSeriesMembership>> { [1] = [membership] },
            new Dictionary<string, AudibleBookResponse>
            {
                ["BOOK1"] = CreateAudibleBook("BOOK1", ("SERIES123", ImportedSeriesName, "1"))
            });

        var previews = await service.PreviewAsync(null, "us");

        var preview = Assert.Single(previews);
        var candidate = Assert.Single(preview.Candidates);
        Assert.Equal(AudibleSeriesAsin, candidate.Asin);
        Assert.Contains(candidate.Evidence, item => item.Contains("position", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, preview.ImportedBookCount);
        Assert.Empty(preview.BooksWithoutAsin);
    }

    [Fact]
    public async Task PreviewAsync_ReportsBooksWithoutAsinsWithoutCreatingCandidates()
    {
        var audiobook = CreateAudiobook(1, null, null);
        var membership = CreateMembership(11, 1, ImportedSeriesName, null, "abs-series-1");
        var service = CreateService(
            [audiobook],
            new Dictionary<int, List<AudiobookSeriesMembership>> { [1] = [membership] },
            new Dictionary<string, AudibleBookResponse>());

        var preview = Assert.Single(await service.PreviewAsync(null, "us"));

        Assert.Single(preview.BooksWithoutAsin);
        Assert.Empty(preview.Candidates);
    }

    [Fact]
    public async Task PreviewAsync_MarksMultiplePlausibleCatalogSeriesAmbiguous()
    {
        var audiobook = CreateAudiobook(1, "BOOK1", "1");
        var membership = CreateMembership(11, 1, ImportedSeriesName, "1", "abs-series-1");
        var service = CreateService(
            [audiobook],
            new Dictionary<int, List<AudiobookSeriesMembership>> { [1] = [membership] },
            new Dictionary<string, AudibleBookResponse>
            {
                ["BOOK1"] = CreateAudibleBook(
                    "BOOK1",
                    ("SERIES123", ImportedSeriesName, "1"),
                    ("SERIES456", ImportedSeriesName, "1"))
            });

        var preview = Assert.Single(await service.PreviewAsync(null, "us"));

        Assert.True(preview.IsAmbiguous);
        Assert.Equal(2, preview.Candidates.Count);
    }

    [Fact]
    public async Task PreviewAsync_ReportsConflictingExistingPosition()
    {
        var audiobook = CreateAudiobook(1, "BOOK1", "2");
        var imported = CreateMembership(11, 1, ImportedSeriesName, "2", "abs-series-1");
        var canonical = CreateMembership(12, 1, ImportedSeriesName, "1", null, AudibleSeriesAsin);
        var service = CreateService(
            [audiobook],
            new Dictionary<int, List<AudiobookSeriesMembership>> { [1] = [imported, canonical] },
            new Dictionary<string, AudibleBookResponse>
            {
                ["BOOK1"] = CreateAudibleBook("BOOK1", ("SERIES123", ImportedSeriesName, "2"))
            });

        var preview = Assert.Single(await service.PreviewAsync(null, "us"));

        Assert.NotEmpty(Assert.Single(preview.Candidates).Conflicts);
    }

    [Fact]
    public async Task MergeAsync_RefreshesSelectedCanonicalCatalogAndOnlyMergesMemberships()
    {
        var audiobook = CreateAudiobook(1, "BOOK1", "1");
        var membership = CreateMembership(11, 1, ImportedSeriesName, "1", "abs-series-1");
        var repository = CreateRepository(
            [audiobook],
            new Dictionary<int, List<AudiobookSeriesMembership>> { [1] = [membership] });
        repository
            .Setup(item => item.MergeSeriesMembershipsAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 11 })),
                AudibleSeriesAsin,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMembershipMergeOutcome(1, 0, 1));
        var catalog = new Mock<ISeriesCatalogService>();
        catalog
            .Setup(item => item.GetCatalogByAsinAsync(
                AudibleSeriesAsin,
                ImportedSeriesName,
                "us",
                250,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesCatalogFetchResult
            {
                Series = new SeriesLookupItem { Asin = AudibleSeriesAsin, Name = ImportedSeriesName }
            });
        var audible = CreateAudibleService(
            new Dictionary<string, AudibleBookResponse>
            {
                ["BOOK1"] = CreateAudibleBook("BOOK1", ("SERIES123", ImportedSeriesName, "1"))
            });
        var service = new AudiobookSeriesResolutionService(
            audible.Object,
            repository.Object,
            catalog.Object,
            NullLogger<AudiobookSeriesResolutionService>.Instance);

        var result = await service.MergeAsync(
            AudiobookSeriesIdentity.GetId(
                audiobook,
                ImportedSeriesName,
                audiobookshelfSeriesId: "abs-series-1"),
            AudibleSeriesAsin,
            "us");

        Assert.Equal(AudibleSeriesAsin, result.CanonicalSeriesAsin);
        Assert.Equal(1, result.TransferredMembershipCount);
        repository.Verify(item => item.MergeSeriesMembershipsAsync(
            It.IsAny<IReadOnlyCollection<int>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
        catalog.Verify(item => item.GetCatalogByAsinAsync(
            AudibleSeriesAsin,
            ImportedSeriesName,
            "us",
            250,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.UpdateAsync(It.IsAny<Audiobook>()), Times.Never);
    }

    [Fact]
    public async Task MergeAsync_WhenSourceWasAlreadyMerged_ReturnsNoOpResult()
    {
        var audiobook = CreateAudiobook(1, "BOOK1", "1");
        var mergedMembership = CreateMembership(
            11,
            1,
            ImportedSeriesName,
            "1",
            "abs-series-1",
            AudibleSeriesAsin);
        var service = CreateService(
            [audiobook],
            new Dictionary<int, List<AudiobookSeriesMembership>> { [1] = [mergedMembership] },
            new Dictionary<string, AudibleBookResponse>());
        var sourceId = AudiobookSeriesIdentity.GetId(
            audiobook,
            ImportedSeriesName,
            audiobookshelfSeriesId: "abs-series-1");

        var result = await service.MergeAsync(sourceId, AudibleSeriesAsin, "us");

        Assert.Equal(AudibleSeriesAsin, result.CanonicalSeriesAsin);
        Assert.Equal(0, result.TransferredMembershipCount);
        Assert.Equal(1, result.MergedBookCount);
    }

    private static AudiobookSeriesResolutionService CreateService(
        List<Audiobook> audiobooks,
        Dictionary<int, List<AudiobookSeriesMembership>> memberships,
        Dictionary<string, AudibleBookResponse> metadata)
    {
        var repository = CreateRepository(audiobooks, memberships);
        return new AudiobookSeriesResolutionService(
            CreateAudibleService(metadata).Object,
            repository.Object,
            Mock.Of<ISeriesCatalogService>(),
            NullLogger<AudiobookSeriesResolutionService>.Instance);
    }

    private static Mock<IAudiobookRepository> CreateRepository(
        List<Audiobook> audiobooks,
        Dictionary<int, List<AudiobookSeriesMembership>> memberships)
    {
        var repository = new Mock<IAudiobookRepository>();
        repository.Setup(item => item.GetAllAsync()).ReturnsAsync(audiobooks);
        repository
            .Setup(item => item.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(memberships);
        return repository;
    }

    private static Mock<AudibleService> CreateAudibleService(Dictionary<string, AudibleBookResponse> metadata)
    {
        var httpClient = new HttpClient();
        var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
        audible
            .Setup(item => item.GetBooksMetadataByAsinsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string>()))
            .ReturnsAsync((IEnumerable<string> asins, string _) => asins
                .Where(asin => metadata.ContainsKey(asin))
                .Select(asin => metadata[asin])
                .ToList());
        return audible;
    }

    private static Audiobook CreateAudiobook(int id, string? asin, string? position) =>
        new()
        {
            Id = id,
            Asin = asin,
            Title = $"Book {id}",
            Authors = ["Dungeon Crawler Carl Author"],
            SeriesNumber = position,
            Monitored = true
        };

    private static AudiobookSeriesMembership CreateMembership(
        int id,
        int audiobookId,
        string name,
        string? position,
        string? audiobookshelfId,
        string? asin = null) =>
        new()
        {
            Id = id,
            AudiobookId = audiobookId,
            SeriesName = name,
            SeriesNumber = position,
            SeriesAsin = asin,
            AudiobookshelfSeriesId = audiobookshelfId,
            IsPrimary = true,
            SortOrder = 0
        };

    private static AudibleBookResponse CreateAudibleBook(
        string asin,
        params (string Asin, string Name, string Position)[] series) =>
        new()
        {
            Asin = asin,
            Authors = [new AudibleAuthor { Name = "Dungeon Crawler Carl Author" }],
            Series = series.Select(item => new AudibleSeries
            {
                Asin = item.Asin,
                Name = item.Name,
                Position = item.Position
            }).ToList()
        };
}
