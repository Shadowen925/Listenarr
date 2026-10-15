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
