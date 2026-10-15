using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobookshelf;

[Trait("Name", nameof(AudiobookshelfNamingTests))]
[Trait("Category", "Application")]
public sealed class AudiobookshelfNamingTests : BaseTests
{
    [Fact]
    public async Task GenerateFilePathAsync_UsesPaddedSeriesBookFolderAndFilename()
    {
        var outputPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "listenarr-naming"));
        var settings = new ApplicationSettings { OutputPath = outputPath };
        var service = CreateService(settings);

        var path = await service.GenerateFilePathAsync(
            new AudioMetadata
            {
                Artist = "Author",
                Series = "Saga",
                SeriesPosition = 2,
                Title = "First Book"
            },
            outputPath,
            ".m4b");

        Assert.Equal(
            Path.Combine(
                outputPath,
                "Author - Saga",
                "Author - Saga - Book 02 - First Book",
                "Author - Saga - Book 02 - First Book.m4b"),
            path);
    }

    [Fact]
    public async Task GenerateFilePathAsync_HandlesStandaloneAndMultiPartAudiobooks()
    {
        var outputPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "listenarr-naming"));
        var settings = new ApplicationSettings { OutputPath = outputPath };
        var service = CreateService(settings);

        var standalonePath = await service.GenerateFilePathAsync(
            new AudioMetadata { Artist = "Author", Title = "Standalone" },
            outputPath,
            ".mp3");
        var multiPartPath = await service.GenerateFilePathAsync(
            new AudioMetadata
            {
                Artist = "Author",
                Series = "Saga",
                SeriesPosition = 2,
                Title = "First Book",
                DiscNumber = 1,
                TrackNumber = 3
            },
            outputPath,
            ".mp3");

        Assert.Equal(
            Path.Combine(
                outputPath,
                "Author",
                "Author - Standalone",
                "Author - Standalone.mp3"),
            standalonePath);
        Assert.Equal(
            Path.Combine(
                outputPath,
                "Author - Saga",
                "Author - Saga - Book 02 - First Book",
                "Author - Saga - Book 02 - First Book - Part 01-03.mp3"),
            multiPartPath);
    }

    [Fact]
    public async Task GenerateFilePathAsync_PreservesFractionalSeriesPosition()
    {
        var outputPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "listenarr-naming"));
        var service = CreateService(new ApplicationSettings { OutputPath = outputPath });

        var path = await service.GenerateFilePathAsync(
            new AudioMetadata
            {
                Artist = "Author",
                Series = "Saga",
                SeriesPosition = 4.5m,
                Title = "Fifth Book"
            },
            outputPath,
            ".m4b");

        Assert.Contains("Book 04.5", path, StringComparison.Ordinal);
    }

    private static FileNamingService CreateService(ApplicationSettings settings)
    {
        var configurationService = new Mock<IConfigurationService>();
        configurationService
            .Setup(configuration => configuration.GetApplicationSettingsAsync())
            .ReturnsAsync(settings);
        return new FileNamingService(
            configurationService.Object,
            NullLogger<FileNamingService>.Instance);
    }
}
