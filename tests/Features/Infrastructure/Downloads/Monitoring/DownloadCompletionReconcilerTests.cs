using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Monitoring
{
    [Trait("Name", "DownloadCompletionReconcilerTests")]
    [Trait("Category", "DownloadCompletionReconciler")]
    public class DownloadCompletionReconcilerTests : BaseTests
    {
        [Theory]
        [InlineData(".m4b")]
        [InlineData(".m4a")]
        public async Task ReconcileAsync_ImportsStableAsinNamedAudioFileAfterMissedCompletion(string extension)
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var file = Path.Combine(root, $"B000R34YKC{extension}");
                await File.WriteAllBytesAsync(file, new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 10,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
                var reconciler = CreateReconciler(root, audiobook, timeProvider, new AudioMetadata
                {
                    Asin = "B000R34YKC",
                    Title = "Dune",
                    Artist = "Frank Herbert"
                });
                var client = new DownloadClientConfiguration
                {
                    Id = "deluge",
                    DownloadPath = "/downloads/deluge/completed"
                };
                var download = new Download
                {
                    Id = "tracked-dune",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Title = audiobook.Title!,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = client.Id,
                    ExpectedFileSize = 32
                };
                var settings = new ApplicationSettings
                {
                    CompletedDownloadMinimumStabilitySeconds = 60,
                    AutomaticImportReconciledDownloads = true
                };

                var firstPass = await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None);
                Assert.Empty(firstPass);
                Assert.Equal(DownloadStatus.Downloading, download.Status);

                timeProvider.Advance(TimeSpan.FromSeconds(60));
                var secondPass = await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None);

                Assert.Contains(download.Id, secondPass);
                Assert.Equal(DownloadStatus.Completed, download.Status);
                Assert.Equal(Path.GetDirectoryName(file), download.DownloadPath);
                Assert.Equal(
                    file,
                    System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                        download.GetMetadataString(Download.RecoveredSourceFilesMetadataKey)!)!.Single());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_DoesNotPromotePausedOrUndersizedFiles()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var file = Path.Combine(root, "B000R34YKC.m4b");
                await File.WriteAllBytesAsync(file, new byte[16]);
                var audiobook = new Audiobook
                {
                    Id = 11,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
                var reconciler = CreateReconciler(root, audiobook, timeProvider, new AudioMetadata
                {
                    Asin = audiobook.Asin,
                    Title = audiobook.Title!,
                    Artist = "Frank Herbert"
                });
                var client = new DownloadClientConfiguration
                {
                    Id = "deluge",
                    DownloadPath = "/downloads/deluge/completed"
                };
                var settings = new ApplicationSettings();
                var paused = new Download
                {
                    Id = "paused",
                    AudiobookId = audiobook.Id,
                    Status = DownloadStatus.Paused,
                    DownloadClientId = client.Id,
                    ExpectedFileSize = 16
                };
                var incomplete = new Download
                {
                    Id = "incomplete",
                    AudiobookId = audiobook.Id,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = client.Id,
                    ExpectedFileSize = 32
                };

                var recovered = await reconciler.ReconcileAsync(
                    client,
                    [paused, incomplete],
                    settings,
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal(DownloadStatus.Paused, paused.Status);
                Assert.Equal(DownloadStatus.Downloading, incomplete.Status);
                Assert.Equal("NeedsAttention", incomplete.GetMetadataString("Recovery.State"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_RecoversStableMultiFileMp3AfterReconcilerRestart()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var firstFile = Path.Combine(root, "01.mp3");
                var secondFile = Path.Combine(root, "02.mp3");
                await File.WriteAllBytesAsync(firstFile, new byte[32]);
                await File.WriteAllBytesAsync(secondFile, new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 12,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
                var client = new DownloadClientConfiguration
                {
                    Id = "deluge",
                    DownloadPath = "/downloads/deluge/completed"
                };
                var download = new Download
                {
                    Id = "tracked-dune-mp3",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Title = audiobook.Title!,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = client.Id,
                    ExpectedFileSize = 64
                };
                var settings = new ApplicationSettings
                {
                    CompletedDownloadMinimumStabilitySeconds = 60,
                    AutomaticImportReconciledDownloads = true
                };
                var metadata = new AudioMetadata
                {
                    Asin = audiobook.Asin,
                    Title = audiobook.Title!,
                    Artist = "Frank Herbert"
                };
                var firstReconciler = CreateReconciler(root, audiobook, timeProvider, metadata);

                Assert.Empty(await firstReconciler.ReconcileAsync(
                    client,
                    [download],
                    settings,
                    CancellationToken.None));

                timeProvider.Advance(TimeSpan.FromSeconds(60));
                var restartedReconciler = CreateReconciler(root, audiobook, timeProvider, metadata);
                var recovered = await restartedReconciler.ReconcileAsync(
                    client,
                    [download],
                    settings,
                    CancellationToken.None);

                Assert.Contains(download.Id, recovered);
                Assert.Equal(DownloadStatus.Completed, download.Status);
                var recoveredFiles = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    download.GetMetadataString(Download.RecoveredSourceFilesMetadataKey)!);
                Assert.NotNull(recoveredFiles);
                Assert.Equal(new[] { firstFile, secondFile }, recoveredFiles);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static DownloadCompletionReconciler CreateReconciler(
            string localRoot,
            Audiobook audiobook,
            TimeProvider timeProvider,
            AudioMetadata metadata)
        {
            var mappingService = new Mock<IRemotePathMappingService>();
            mappingService
                .Setup(service => service.TranslatePathAsync(
                    It.IsAny<DownloadClientConfiguration>(),
                    It.IsAny<string>()))
                .ReturnsAsync(localRoot);

            var metadataService = new Mock<IMetadataService>();
            metadataService
                .Setup(service => service.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(metadata);

            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository
                .Setup(repository => repository.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);

            return new DownloadCompletionReconciler(
                mappingService.Object,
                new LocalFileSystem(),
                metadataService.Object,
                audiobookRepository.Object,
                timeProvider,
                NullLogger<DownloadCompletionReconciler>.Instance);
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), $"listenarr-reconcile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
        {
            private DateTimeOffset _now = now;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan duration) => _now += duration;
        }
    }
}
