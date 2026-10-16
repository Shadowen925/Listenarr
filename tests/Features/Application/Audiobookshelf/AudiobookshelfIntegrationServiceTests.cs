using Listenarr.Application.Audiobookshelf;
using Listenarr.Application.Audiobookshelf.Contracts;
using Listenarr.Application.Audiobookshelf.Models;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobookshelf;

[Trait("Name", nameof(AudiobookshelfIntegrationServiceTests))]
[Trait("Category", "Application")]
public sealed class AudiobookshelfIntegrationServiceTests : BaseTests
{
    [Fact]
    public async Task ImportAsync_ImportsCorrectedMetadataWithoutIdentifiersOrFilesystemMutation()
    {
        var fixture = new ImportFixture();
        Audiobook? addedAudiobook = null;
        fixture.AudiobookRepository
            .Setup(repository => repository.AddAsync(It.IsAny<Audiobook>()))
            .Callback<Audiobook>(audiobook =>
            {
                audiobook.Id = 41;
                addedAudiobook = audiobook;
            })
            .ReturnsAsync((Audiobook audiobook) => audiobook);
        fixture.ScanService
            .Setup(service => service.RegisterExistingFileAsync(
                41,
                It.IsAny<string>(),
                fixture.MappedAudioPath,
                "audiobookshelf-import",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var preview = await fixture.Service.PreviewImportAsync(fixture.Library.Id);
        var previewItem = Assert.Single(preview.Items);
        Assert.Equal(AudiobookshelfImportMatchStatus.Matched, previewItem.Status);
        Assert.True(previewItem.MissingIdentifiers);

        var result = await fixture.Service.ImportAsync(new AudiobookshelfImportRequest(
            fixture.Library.Id,
            [fixture.Item.Id]));

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.NotNull(addedAudiobook);
        Assert.Equal("Corrected ABS title", addedAudiobook.Title);
        Assert.Equal(["Corrected ABS author"], addedAudiobook.Authors);
        Assert.Equal("ABS corrected description", addedAudiobook.Description);
        Assert.Equal("Series", addedAudiobook.Series);
        Assert.Equal("2", addedAudiobook.SeriesNumber);
        Assert.Equal(
            "Series",
            Assert.Single(addedAudiobook.SeriesMemberships!).SeriesName);
        Assert.Equal("OL123W", addedAudiobook.OpenLibraryId);
        Assert.True(addedAudiobook.AudiobookshelfMetadataAuthoritative);
        Assert.Equal(fixture.MappedBookPath, addedAudiobook.BasePath);
        fixture.ScanService.Verify(service => service.RegisterExistingFileAsync(
            41,
            fixture.MappedBookPath,
            fixture.MappedAudioPath,
            "audiobookshelf-import",
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.FileSystem.Verify(fileSystem => fileSystem.DeleteFile(It.IsAny<string>()), Times.Never);
        fixture.FileSystem.Verify(fileSystem => fileSystem.DeleteDirectory(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        fixture.FileSystem.Verify(fileSystem => fileSystem.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ImportAsync_PreservesMultipleAudiobookshelfSeriesMembershipsAndPositions()
    {
        var fixture = new ImportFixture();
        var source = fixture.Item with
        {
            Metadata = fixture.Item.Metadata with
            {
                SeriesMemberships =
                [
                    new AudiobookshelfSeriesMembership("Series", "2"),
                    new AudiobookshelfSeriesMembership("Shared Universe", "4")
                ]
            }
        };
        fixture.Client.Setup(client => client.GetLibraryItemsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                fixture.Library.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([source]);
        fixture.AudiobookRepository
            .Setup(repository => repository.AddAsync(It.IsAny<Audiobook>()))
            .Callback<Audiobook>(audiobook => audiobook.Id = 41)
            .ReturnsAsync((Audiobook audiobook) => audiobook);
        fixture.ScanService
            .Setup(service => service.RegisterExistingFileAsync(
                41,
                It.IsAny<string>(),
                fixture.MappedAudioPath,
                "audiobookshelf-import",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await fixture.Service.ImportAsync(new AudiobookshelfImportRequest(
            fixture.Library.Id,
            [fixture.Item.Id]));

        var imported = Assert.Single(
            fixture.AudiobookRepository.Invocations
                .Where(invocation => invocation.Method.Name == nameof(IAudiobookRepository.AddAsync))
                .Select(invocation => (Audiobook)invocation.Arguments[0]));
        Assert.Equal(
            [("Series", "2"), ("Shared Universe", "4")],
            imported.SeriesMemberships!
                .OrderBy(membership => membership.SortOrder)
                .Select(membership => (membership.SeriesName, membership.SeriesNumber))
                .ToArray());
    }

    [Fact]
    public async Task SynchronizeSeriesAsync_UpdatesOnlySeriesMembershipsForSelectedImportedBooks()
    {
        var fixture = new ImportFixture();
        var source = fixture.Item with
        {
            Metadata = fixture.Item.Metadata with
            {
                SeriesMemberships =
                [
                    new AudiobookshelfSeriesMembership("Series", "2"),
                    new AudiobookshelfSeriesMembership("Shared Universe", "4")
                ]
            }
        };
        fixture.Client.Setup(client => client.GetLibraryItemsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                fixture.Library.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([source]);
        var audiobook = new Audiobook
        {
            Id = 41,
            Title = "Imported title",
            Subtitle = "Keep this subtitle",
            Authors = ["Existing author"],
            Asin = "B012345678",
            BasePath = fixture.MappedBookPath,
            FilePath = fixture.MappedAudioPath,
            AudiobookshelfLibraryId = fixture.Library.Id,
            AudiobookshelfItemId = fixture.Item.Id,
            Series = "Series",
            SeriesNumber = "1",
            SeriesMemberships =
            [
                new AudiobookSeriesMembership
                {
                    SeriesName = "Series",
                    SeriesNumber = "1",
                    SeriesAsin = "B098765432",
                    IsPrimary = true
                }
            ]
        };
        fixture.AudiobookRepository
            .Setup(repository => repository.GetAudiobookshelfBooksAsync(
                fixture.Library.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([audiobook]);
        fixture.AudiobookRepository
            .Setup(repository => repository.UpdateAsync(audiobook))
            .ReturnsAsync(true);

        var preview = await fixture.Service.PreviewSeriesSyncAsync(fixture.Library.Id);
        var previewItem = Assert.Single(preview.Items);
        Assert.Equal(AudiobookshelfSeriesSyncStatus.NeedsUpdate, previewItem.Status);
        Assert.Equal(2, previewItem.AudiobookshelfSeries.Count);

        var result = await fixture.Service.SynchronizeSeriesAsync(
            new AudiobookshelfSeriesSyncRequest(fixture.Library.Id, [fixture.Item.Id]));

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(
            [("Series", "2", "B098765432"), ("Shared Universe", "4", null)],
            audiobook.SeriesMemberships!
                .OrderBy(membership => membership.SortOrder)
                .Select(membership => (
                    membership.SeriesName,
                    membership.SeriesNumber,
                    membership.SeriesAsin))
                .ToArray());
        Assert.Equal("Series", audiobook.Series);
        Assert.Equal("2", audiobook.SeriesNumber);
        Assert.Equal("Imported title", audiobook.Title);
        Assert.Equal("Keep this subtitle", audiobook.Subtitle);
        Assert.Equal(["Existing author"], audiobook.Authors);
        Assert.Equal("B012345678", audiobook.Asin);
        Assert.Equal(fixture.MappedBookPath, audiobook.BasePath);
        Assert.Equal(fixture.MappedAudioPath, audiobook.FilePath);
        fixture.AudiobookRepository.Verify(
            repository => repository.UpdateAsync(audiobook),
            Times.Once);
    }

    [Fact]
    public async Task PreviewImportAsync_MarksAlreadyImportedItemAsDuplicate()
    {
        var fixture = new ImportFixture();
        fixture.AudiobookRepository
            .Setup(repository => repository.GetLibraryAsync())
            .ReturnsAsync(
            [
                new Audiobook
                {
                    AudiobookshelfLibraryId = fixture.Library.Id,
                    AudiobookshelfItemId = fixture.Item.Id
                }
            ]);

        var preview = await fixture.Service.PreviewImportAsync(fixture.Library.Id);

        Assert.Equal(
            AudiobookshelfImportMatchStatus.Duplicate,
            Assert.Single(preview.Items).Status);
        var result = await fixture.Service.ImportAsync(new AudiobookshelfImportRequest(
            fixture.Library.Id,
            [fixture.Item.Id]));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
        fixture.AudiobookRepository.Verify(
            repository => repository.AddAsync(It.IsAny<Audiobook>()),
            Times.Never);
    }

    [Fact]
    public async Task PreviewImportAsync_WorksWithEmptyDatabaseAndReportsDiagnosticFields()
    {
        var fixture = new ImportFixture();

        var preview = await fixture.Service.PreviewImportAsync(fixture.Library.Id);

        var item = Assert.Single(preview.Items);
        Assert.Equal(AudiobookshelfImportMatchStatus.Matched, item.Status);
        Assert.Equal("Series", item.Series);
        Assert.Equal("2", item.SeriesPosition);
        Assert.Equal(1, item.FileCount);
        Assert.Contains("m4b", item.FileFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fixture.MappedBookPath, item.ListenarrPath);
    }

    [Fact]
    public async Task PreviewImportAsync_CategorizesUnmatchedItemWhenFilesAreMissing()
    {
        var fixture = new ImportFixture();
        fixture.FileSystem.Setup(fileSystem => fileSystem.FileExists(It.IsAny<string>())).Returns(false);

        var preview = await fixture.Service.PreviewImportAsync(fixture.Library.Id);

        var item = Assert.Single(preview.Items);
        Assert.Equal(AudiobookshelfImportMatchStatus.Unmatched, item.Status);
        Assert.Equal(AudiobookshelfImportFailureCategories.AudioFilesMissing, item.FailureCategory);
        Assert.False(string.IsNullOrWhiteSpace(item.Reason));
        Assert.Equal(fixture.MappedBookPath, item.ListenarrPath);
    }

    [Fact]
    public async Task ImportAsync_ReturnsAuditSummaryAndRetryDoesNotCreateDuplicates()
    {
        var fixture = new ImportFixture();
        var library = new List<Audiobook>();
        fixture.AudiobookRepository
            .Setup(repository => repository.GetLibraryAsync())
            .ReturnsAsync(() => library.ToList());
        fixture.AudiobookRepository
            .Setup(repository => repository.AddAsync(It.IsAny<Audiobook>()))
            .Callback<Audiobook>(audiobook =>
            {
                audiobook.Id = 41;
                library.Add(audiobook);
            })
            .ReturnsAsync((Audiobook audiobook) => audiobook);
        fixture.ScanService
            .Setup(service => service.RegisterExistingFileAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var request = new AudiobookshelfImportRequest(fixture.Library.Id, [fixture.Item.Id]);

        var first = await fixture.Service.ImportAsync(request);
        var retry = await fixture.Service.ImportAsync(request);

        Assert.Equal(1, first.ImportedCount);
        Assert.Equal(0, first.FailedCount);
        Assert.Equal(1, first.SelectedCount);
        Assert.Equal(fixture.Library.Id, first.LibraryId);
        Assert.NotEqual(default, first.ImportedAt);
        Assert.Equal(AudiobookshelfImportItemOutcome.Imported, Assert.Single(first.Items!).Outcome);
        Assert.Equal(0, retry.ImportedCount);
        Assert.Equal(1, retry.SkippedCount);
        Assert.Equal(AudiobookshelfImportItemOutcome.Skipped, Assert.Single(retry.Items!).Outcome);
        fixture.AudiobookRepository.Verify(
            repository => repository.AddAsync(It.IsAny<Audiobook>()),
            Times.Once);
    }

    [Fact]
    public async Task PreviewImportAsync_MatchesRepresentativeLibraryWithoutPriorScan()
    {
        var fixture = new ImportFixture();
        var metadata = fixture.Item.Metadata with { Asin = null, Isbn = [] };
        AudiobookshelfLibraryItem Make(string id, string path, params string[] files) =>
            new(id, path, files, metadata);
        var items = new[]
        {
            Make("a", "/audiobooks/Author - Series/Author - Series - Book 01 - Title",
                "/audiobooks/Author - Series/Author - Series - Book 01 - Title/Author - Series - Book 01 - Title.m4b"),
            Make("b", "/audiobooks/Author/Multi",
                "/audiobooks/Author/Multi/Part 01.mp3", "/audiobooks/Author/Multi/Part 02.mp3"),
            Make("c", "/audiobooks/Zoë Ünïcode/Book: [Odd] #1 & 'More'",
                "/audiobooks/Zoë Ünïcode/Book: [Odd] #1 & 'More'/track (1).m4b")
        };
        fixture.Client.Setup(client => client.GetLibraryItemsAsync(
                It.IsAny<string>(), It.IsAny<string>(), fixture.Library.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

        var preview = await fixture.Service.PreviewImportAsync(fixture.Library.Id);

        Assert.All(preview.Items, item => Assert.Equal(AudiobookshelfImportMatchStatus.Matched, item.Status));
        Assert.Equal(3, preview.Items.Count);
        Assert.Equal(2, preview.Items.Single(item => item.ItemId == "b").FileCount);
        Assert.All(preview.Items, item => Assert.DoesNotContain("/audiobooks", item.ListenarrPath));
    }

    [Fact]
    public async Task PreviewImportAsync_ItemWithoutAudioFilePaths_IsUnmatchedWithCategory()
    {
        var fixture = new ImportFixture();
        fixture.Client.Setup(client => client.GetLibraryItemsAsync(
                It.IsAny<string>(), It.IsAny<string>(), fixture.Library.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([fixture.Item with { AudioFilePaths = [] }]);

        var preview = await fixture.Service.PreviewImportAsync(fixture.Library.Id);

        Assert.Equal(
            AudiobookshelfImportFailureCategories.NoMappableAudioFiles,
            Assert.Single(preview.Items).FailureCategory);
    }

    private sealed class ImportFixture
    {
        private const string ServerUrl = "http://audiobookshelf:13378";
        private const string ApiKey = "unit-test-token";
        private const string LibraryId = "lib_a";
        private const string ItemId = "li_book";
        private readonly string _listenarrRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "listenarr-audiobookshelf-test"));

        public ImportFixture()
        {
            MappedBookPath = Path.Combine(_listenarrRoot, "Author", "Book");
            MappedAudioPath = Path.Combine(MappedBookPath, "part01.m4b");
            Library = new AudiobookshelfLibrary(
                LibraryId,
                "Audiobooks",
                "book",
                ["/audiobooks"]);
            Item = new AudiobookshelfLibraryItem(
                ItemId,
                "/audiobooks/Author/Book",
                ["/audiobooks/Author/Book/part01.m4b"],
                new AudiobookshelfMetadata(
                    "Corrected ABS title",
                    null,
                    ["Corrected ABS author"],
                    ["Narrator"],
                    "Series",
                    "2",
                    "ABS corrected description",
                    null,
                    null,
                    [],
                    "OL123W",
                    null,
                    null,
                    null,
                    []));

            Client.Setup(client => client.GetLibrariesAsync(
                    ServerUrl,
                    ApiKey,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([Library]);
            Client.Setup(client => client.GetLibraryItemsAsync(
                    ServerUrl,
                    ApiKey,
                    LibraryId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([Item]);
            ConfigurationService.Setup(configuration => configuration.GetAudiobookshelfImportSettingsAsync(true))
                .ReturnsAsync(new AudiobookshelfImportConnectionSettings
                {
                    Url = ServerUrl,
                    ApiKey = ApiKey,
                    HasSavedApiKey = true
                });
            ConfigurationService.Setup(configuration => configuration.GetAudiobookshelfImportSettingsAsync(false))
                .ReturnsAsync(new AudiobookshelfImportConnectionSettings
                {
                    Url = ServerUrl,
                    HasSavedApiKey = true
                });
            RootFolderRepository.Setup(repository => repository.GetAllAsync())
                .ReturnsAsync(
                [
                    new RootFolder
                    {
                        Id = 7,
                        Name = "Audiobooks",
                        Path = _listenarrRoot,
                        AudiobookshelfLibraryId = LibraryId
                    }
                ]);
            AudiobookRepository.Setup(repository => repository.GetLibraryAsync())
                .ReturnsAsync([]);
            FileSystem.Setup(fileSystem => fileSystem.FileExists(It.IsAny<string>()))
                .Returns(true);

            Service = new AudiobookshelfIntegrationService(
                Client.Object,
                ConfigurationService.Object,
                RootFolderRepository.Object,
                AudiobookRepository.Object,
                Mock.Of<IAudiobookDeletionCommitService>(),
                ScanService.Object,
                FileSystem.Object,
                NullLogger<AudiobookshelfIntegrationService>.Instance);
        }

        public string MappedBookPath { get; }
        public string MappedAudioPath { get; }
        public AudiobookshelfLibrary Library { get; }
        public AudiobookshelfLibraryItem Item { get; }
        public AudiobookshelfIntegrationService Service { get; }
        public Mock<IAudiobookshelfClient> Client { get; } = new();
        public Mock<IConfigurationService> ConfigurationService { get; } = new();
        public Mock<IRootFolderRepository> RootFolderRepository { get; } = new();
        public Mock<IAudiobookRepository> AudiobookRepository { get; } = new();
        public Mock<IAudiobookScanService> ScanService { get; } = new();
        public Mock<IFileSystem> FileSystem { get; } = new();
    }
}
