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
                    DownloadPath = string.Empty
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
                Assert.True(File.Exists(file));
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
        public async Task ReconcileAsync_RecoversStableAsinFileWhenTorrentIsMissingAndExpectedSizeIsUnknown()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var file = Path.Combine(root, "B000R34YKC.m4b");
                await File.WriteAllBytesAsync(file, new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 13,
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
                    DownloadPath = string.Empty
                };
                var download = new Download
                {
                    Id = "tracked-dune-missing-torrent",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Title = audiobook.Title!,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = client.Id,
                    TotalSize = 100,
                    DownloadedSize = 99
                };
                download.SetMetadata("ClientState", "downloading");
                var settings = new ApplicationSettings
                {
                    CompletedDownloadMinimumStabilitySeconds = 60,
                    AutomaticImportReconciledDownloads = true
                };

                Assert.Empty(await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None));
                Assert.Equal("RecoveryPending", download.GetMetadataString("Recovery.State"));

                timeProvider.Advance(TimeSpan.FromSeconds(60));
                var recovered = await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None);

                Assert.Contains(download.Id, recovered);
                Assert.Equal(DownloadStatus.Completed, download.Status);
                Assert.Equal("tracked-dune-missing-torrent", download.Id);
                Assert.Equal("ImportPending", download.GetMetadataString("Recovery.State"));
                Assert.Equal("MissingFromClient", download.GetMetadataString("Recovery.ClientPresence"));
                Assert.Equal(file, System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    download.GetMetadataString(Download.RecoveredSourceFilesMetadataKey)!)!.Single());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_RecoversStableMatchingFileWhenClientDisappearsWithStaleDownloadingProgress()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var file = Path.Combine(root, "B000R34YKC.m4b");
                await File.WriteAllBytesAsync(file, new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 15,
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
                var client = new DownloadClientConfiguration { Id = "deluge" };
                var download = new Download
                {
                    Id = "tracked-dune-stale-progress",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Title = audiobook.Title!,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = client.Id,
                    TotalSize = 100,
                    DownloadedSize = 99,
                    ExpectedFileSize = 32
                };
                download.SetMetadata("ClientState", "downloading");
                var settings = new ApplicationSettings
                {
                    CompletedDownloadMinimumStabilitySeconds = 60,
                    AutomaticImportReconciledDownloads = true
                };

                Assert.Empty(await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None));
                Assert.Equal(DownloadStatus.Downloading, download.Status);

                timeProvider.Advance(TimeSpan.FromSeconds(60));
                var recovered = await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None);

                Assert.Contains(download.Id, recovered);
                Assert.Equal(DownloadStatus.Completed, download.Status);
                Assert.Equal(file, System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    download.GetMetadataString(Download.RecoveredSourceFilesMetadataKey)!)!.Single());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_RecoversGreatGatsbyWhenDelugeDisappearsWithStaleDownloadingProgress()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var file = Path.Combine(root, "The Great Gatsby - F. Scott Fitzgerald.m4b");
                await File.WriteAllBytesAsync(file, new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 16,
                    Title = "The Great Gatsby",
                    Authors = ["F. Scott Fitzgerald"]
                };
                var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
                var reconciler = CreateReconciler(root, audiobook, timeProvider, new AudioMetadata
                {
                    Title = "The Great Gatsby",
                    Artist = "F. Scott Fitzgerald"
                });
                var client = new DownloadClientConfiguration { Id = "deluge" };
                var download = new Download
                {
                    Id = "tracked-great-gatsby-stale-progress",
                    AudiobookId = audiobook.Id,
                    Title = audiobook.Title!,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = client.Id,
                    TotalSize = 100,
                    DownloadedSize = 99,
                    ExpectedFileSize = 32
                };
                download.SetMetadata("ClientState", "downloading");
                var settings = new ApplicationSettings
                {
                    CompletedDownloadMinimumStabilitySeconds = 60,
                    AutomaticImportReconciledDownloads = true
                };

                Assert.Empty(await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None));
                Assert.Equal(DownloadStatus.Downloading, download.Status);

                timeProvider.Advance(TimeSpan.FromSeconds(60));
                var recovered = await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None);

                Assert.Contains(download.Id, recovered);
                Assert.Equal(DownloadStatus.Completed, download.Status);
                Assert.Equal("ImportPending", download.GetMetadataString("Recovery.State"));
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
        public async Task ReconcileAsync_LeavesDownloadRecoveryPendingWhenClientHasNoPathMappings()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var audiobook = new Audiobook
                {
                    Id = 14,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
                var reconciler = CreateReconciler(
                    root,
                    audiobook,
                    timeProvider,
                    new AudioMetadata { Asin = audiobook.Asin, Title = audiobook.Title!, Artist = "Frank Herbert" },
                    []);
                var download = new Download
                {
                    Id = "no-mapping",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "deluge"
                };

                var recovered = await reconciler.ReconcileAsync(
                    new DownloadClientConfiguration { Id = "deluge" },
                    [download],
                    new ApplicationSettings(),
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal("RecoveryPending", download.GetMetadataString("Recovery.State"));
                Assert.Contains("No client-specific", download.GetMetadataString("Recovery.Reason"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_DoesNotScanWhenTranslatedPathDoesNotMatchAuthorizedMappingRoot()
        {
            var root = CreateTemporaryDirectory();
            var translated = Path.Combine(root, "unmapped");
            Directory.CreateDirectory(translated);
            try
            {
                var file = Path.Combine(translated, "B000R34YKC.m4b");
                await File.WriteAllBytesAsync(file, new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 15,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
                var reconciler = CreateReconciler(
                    root,
                    audiobook,
                    timeProvider,
                    new AudioMetadata { Asin = audiobook.Asin, Title = audiobook.Title!, Artist = "Frank Herbert" },
                    translatedLocalRoot: translated);
                var download = new Download
                {
                    Id = "invalid-mapping",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "deluge",
                    ExpectedFileSize = 32
                };

                var recovered = await reconciler.ReconcileAsync(
                    new DownloadClientConfiguration { Id = "deluge" },
                    [download],
                    new ApplicationSettings(),
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal("RecoveryPending", download.GetMetadataString("Recovery.State"));
                Assert.Contains("authorized by this mapping", download.GetMetadataString("Recovery.Reason"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData("deluge", "/data/deluge/completed/")]
        [InlineData("sabnzbd", "/data/sabnzbd/completed/")]
        public async Task ReconcileAsync_UsesOnlyVerifiedDownloadSpecificDirectoryBeforeBroadDiscovery(
            string clientId,
            string remoteRoot)
        {
            var root = CreateTemporaryDirectory();
            var targetDirectory = Path.Combine(root, "Dune");
            var unrelatedDirectory = Path.Combine(root, "Unrelated");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(unrelatedDirectory);
            try
            {
                var targetFile = Path.Combine(targetDirectory, "B000R34YKC.m4b");
                var unrelatedFile = Path.Combine(unrelatedDirectory, "B000R34YKC.m4b");
                await File.WriteAllBytesAsync(targetFile, new byte[32]);
                await File.WriteAllBytesAsync(unrelatedFile, new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 20,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
                var reconciler = CreateReconciler(
                    root,
                    audiobook,
                    timeProvider,
                    new AudioMetadata
                    {
                        Asin = audiobook.Asin,
                        Title = audiobook.Title!,
                        Artist = "Frank Herbert"
                    },
                    clientId: clientId,
                    remoteRoot: remoteRoot);
                var client = new DownloadClientConfiguration { Id = clientId };
                var download = new Download
                {
                    Id = $"tracked-{clientId}-dune",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Title = audiobook.Title!,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = clientId,
                    ExpectedFileSize = 32
                };
                download.SetMetadata("ClientContentPath", $"{remoteRoot}Dune/");
                var settings = new ApplicationSettings
                {
                    CompletedDownloadMinimumStabilitySeconds = 60,
                    AutomaticImportReconciledDownloads = true
                };

                Assert.Empty(await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None));
                timeProvider.Advance(TimeSpan.FromSeconds(60));
                var recovered = await reconciler.ReconcileAsync(client, [download], settings, CancellationToken.None);

                Assert.Contains(download.Id, recovered);
                Assert.Equal(targetDirectory, download.DownloadPath);
                Assert.Equal(
                    new[] { targetFile },
                    System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                        download.GetMetadataString(Download.RecoveredSourceFilesMetadataKey)!)!);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_LeavesBroadMatchesPendingWhenMultipleFoldersMatch()
        {
            var root = CreateTemporaryDirectory();
            var firstDirectory = Path.Combine(root, "First");
            var secondDirectory = Path.Combine(root, "Second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(firstDirectory, "01.m4b"), new byte[32]);
                await File.WriteAllBytesAsync(Path.Combine(secondDirectory, "02.m4b"), new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 21,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var reconciler = CreateReconciler(
                    root,
                    audiobook,
                    new MutableTimeProvider(DateTimeOffset.UtcNow),
                    new AudioMetadata
                    {
                        Asin = audiobook.Asin,
                        Title = audiobook.Title!,
                        Artist = "Frank Herbert"
                    });
                var download = new Download
                {
                    Id = "ambiguous-folders",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "deluge",
                    ExpectedFileSize = 32,
                    DownloadPath = firstDirectory
                };
                download.SetMetadata("ClientContentPath", firstDirectory);
                download.FinalPath = Path.Combine(secondDirectory, "02.m4b");

                var recovered = await reconciler.ReconcileAsync(
                    new DownloadClientConfiguration { Id = "deluge" },
                    [download],
                    new ApplicationSettings(),
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal(DownloadStatus.Downloading, download.Status);
                Assert.Contains("Multiple authorized directories", download.GetMetadataString("Recovery.Reason"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_IgnoresDownloadSpecificPathOutsideAuthorizedMappingRoot()
        {
            var parent = CreateTemporaryDirectory();
            var authorizedRoot = Path.Combine(parent, "authorized");
            var unauthorizedDirectory = Path.Combine(parent, "outside");
            Directory.CreateDirectory(authorizedRoot);
            Directory.CreateDirectory(unauthorizedDirectory);
            try
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(unauthorizedDirectory, "B000R34YKC.m4b"),
                    new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 23,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var reconciler = CreateReconciler(
                    authorizedRoot,
                    audiobook,
                    new MutableTimeProvider(DateTimeOffset.UtcNow),
                    new AudioMetadata
                    {
                        Asin = audiobook.Asin,
                        Title = audiobook.Title!,
                        Artist = "Frank Herbert"
                    });
                var download = new Download
                {
                    Id = "outside-authorized-root",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "deluge",
                    ExpectedFileSize = 32
                };
                download.SetMetadata("ClientContentPath", unauthorizedDirectory);

                var recovered = await reconciler.ReconcileAsync(
                    new DownloadClientConfiguration { Id = "deluge" },
                    [download],
                    new ApplicationSettings(),
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal(DownloadStatus.Downloading, download.Status);
                Assert.Equal("RecoveryPending", download.GetMetadataString("Recovery.State"));
            }
            finally
            {
                Directory.Delete(parent, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_DoesNotBroadenWhenPersistedPathMappingIsUnavailable()
        {
            var root = CreateTemporaryDirectory();
            var targetDirectory = Path.Combine(root, "Dune");
            Directory.CreateDirectory(targetDirectory);
            try
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(targetDirectory, "B000R34YKC.m4b"),
                    new byte[32]);
                var audiobook = new Audiobook
                {
                    Id = 24,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var reconciler = CreateReconciler(
                    root,
                    audiobook,
                    new MutableTimeProvider(DateTimeOffset.UtcNow),
                    new AudioMetadata
                    {
                        Asin = audiobook.Asin,
                        Title = audiobook.Title!,
                        Artist = "Frank Herbert"
                    },
                    candidateMappingUnavailable: true);
                var download = new Download
                {
                    Id = "unavailable-specific-mapping",
                    AudiobookId = audiobook.Id,
                    Asin = audiobook.Asin,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "deluge",
                    ExpectedFileSize = 32
                };
                download.SetMetadata("ClientContentPath", "/data/deluge/completed/Dune/");

                var recovered = await reconciler.ReconcileAsync(
                    new DownloadClientConfiguration { Id = "deluge" },
                    [download],
                    new ApplicationSettings(),
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal(DownloadStatus.Downloading, download.Status);
                Assert.Contains("broader discovery was not attempted", download.GetMetadataString("Recovery.Reason"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ReconcileAsync_LeavesRecoveryPendingWhenAuthorizedRootIsMissing()
        {
            var root = CreateTemporaryDirectory();
            var missingRoot = Path.Combine(root, "missing");
            try
            {
                var audiobook = new Audiobook
                {
                    Id = 22,
                    Title = "Dune",
                    Authors = ["Frank Herbert"],
                    Asin = "B000R34YKC"
                };
                var mapping = new RemotePathMapping
                {
                    DownloadClientId = "deluge",
                    RemotePath = "/data/deluge/completed/",
                    LocalPath = missingRoot
                };
                var reconciler = CreateReconciler(
                    root,
                    audiobook,
                    new MutableTimeProvider(DateTimeOffset.UtcNow),
                    new AudioMetadata { Asin = audiobook.Asin, Title = audiobook.Title!, Artist = "Frank Herbert" },
                    mappings: [mapping],
                    translatedLocalRoot: missingRoot);
                var download = new Download
                {
                    Id = "missing-authorized-root",
                    AudiobookId = audiobook.Id,
                    Status = DownloadStatus.Downloading,
                    DownloadClientId = "deluge"
                };

                var recovered = await reconciler.ReconcileAsync(
                    new DownloadClientConfiguration { Id = "deluge" },
                    [download],
                    new ApplicationSettings(),
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal("RecoveryPending", download.GetMetadataString("Recovery.State"));
                Assert.Contains("missing or is not mounted", download.GetMetadataString("Recovery.Reason"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DirectoryScanner_ReportsFileLimitAndReturnsDeterministicCandidates()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var paths = new[]
                {
                    Path.Combine(root, "03.m4b"),
                    Path.Combine(root, "01.m4b"),
                    Path.Combine(root, "02.m4b")
                };
                foreach (var path in paths)
                {
                    File.WriteAllBytes(path, new byte[1]);
                }

                var scanner = CreateDirectoryScanner(maximumScannedFiles: 2);

                var result = scanner.EnumerateAudioFiles([root], CancellationToken.None);

                Assert.False(result.IsComplete);
                Assert.Equal(
                    new[] { paths[1], paths[2] }.OrderBy(path => path, StringComparer.Ordinal),
                    result.Files);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DirectoryScanner_ReportsDepthLimitWithoutTraversingDeeperDirectories()
        {
            var root = CreateTemporaryDirectory();
            var deeperDirectory = Path.Combine(root, "child");
            Directory.CreateDirectory(deeperDirectory);
            try
            {
                var deeperFile = Path.Combine(deeperDirectory, "B000R34YKC.m4b");
                File.WriteAllBytes(deeperFile, new byte[1]);
                var scanner = CreateDirectoryScanner(maximumDirectoryDepth: 0);

                var result = scanner.EnumerateAudioFiles([root], CancellationToken.None);

                Assert.False(result.IsComplete);
                Assert.Empty(result.Files);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DirectoryScanner_ReportsDirectoryEntryLimitForNonAudioFiles()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "01.txt"), "one");
                File.WriteAllText(Path.Combine(root, "02.txt"), "two");
                File.WriteAllText(Path.Combine(root, "03.txt"), "three");
                var scanner = CreateDirectoryScanner(maximumScannedEntries: 2);

                var result = scanner.EnumerateAudioFiles([root], CancellationToken.None);

                Assert.False(result.IsComplete);
                Assert.Empty(result.Files);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DirectoryScanner_HonorsCancellationBeforeStartingTraversal()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var scanner = CreateDirectoryScanner();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                Assert.Throws<OperationCanceledException>(
                    () => scanner.EnumerateAudioFiles([root], cancellation.Token));
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
                    TotalSize = 100,
                    DownloadedSize = 99,
                    ExpectedFileSize = 32
                };
                incomplete.SetMetadata("ClientState", "downloading");

                var recovered = await reconciler.ReconcileAsync(
                    client,
                    [paused, incomplete],
                    settings,
                    CancellationToken.None);

                Assert.Empty(recovered);
                Assert.Equal(DownloadStatus.Paused, paused.Status);
                Assert.Equal(DownloadStatus.Downloading, incomplete.Status);
                Assert.Equal("RecoveryPending", incomplete.GetMetadataString("Recovery.State"));
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
            AudioMetadata metadata,
            IReadOnlyCollection<RemotePathMapping>? mappings = null,
            string? translatedLocalRoot = null,
            string clientId = "deluge",
            string? remoteRoot = null,
            bool candidateMappingUnavailable = false)
        {
            remoteRoot ??= clientId == "sabnzbd"
                ? "/data/sabnzbd/completed/"
                : "/data/deluge/completed/";
            var mappingService = new Mock<IRemotePathMappingService>();
            mappingService
                .Setup(service => service.GetPathMappingByClientAsync(It.IsAny<DownloadClientConfiguration>()))
                .ReturnsAsync(mappings?.ToList() ??
                [
                    new RemotePathMapping
                    {
                        DownloadClientId = clientId,
                        RemotePath = remoteRoot,
                        LocalPath = localRoot
                    }
                ]);
            mappingService
                .Setup(service => service.TranslatePathAsync(
                    It.IsAny<DownloadClientConfiguration>(),
                    It.IsAny<string>()))
                .ReturnsAsync((DownloadClientConfiguration _, string path) =>
                {
                    if (candidateMappingUnavailable
                        && path != remoteRoot
                        && path.StartsWith(remoteRoot, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The specific mapping is unavailable.");
                    }

                    if (translatedLocalRoot != null && path == remoteRoot)
                    {
                        return translatedLocalRoot;
                    }

                    if (path.StartsWith(remoteRoot, StringComparison.Ordinal))
                    {
                        var relativePath = path[remoteRoot.Length..]
                            .Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar);
                        return Path.GetFullPath(Path.Combine(localRoot, relativePath));
                    }

                    return path;
                });

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

        private static DownloadCompletionDirectoryScanner CreateDirectoryScanner(
            int maximumScannedFiles = 1000,
            int maximumDirectoryDepth = 5,
            int maximumScannedEntries = 10000)
        {
            return new DownloadCompletionDirectoryScanner(
                Mock.Of<IRemotePathMappingService>(),
                new LocalFileSystem(),
                NullLogger<DownloadCompletionReconciler>.Instance,
                maximumScannedFiles,
                maximumDirectoryDepth,
                maximumScannedEntries);
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
