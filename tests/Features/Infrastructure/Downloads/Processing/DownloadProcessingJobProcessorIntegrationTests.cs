using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Listenarr.Infrastructure.Downloads.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Processing
{
    [Trait("Name", "DownloadProcessingJobProcessorIntegrationTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobProcessorIntegrationTests : BaseTests
    {
        private DownloadProcessingJobProcessor downloadProcessingJobProcessor = null!;
        private IDownloadProcessingJobService downloadProcessingJobService = null!;
        private DownloadClientGatewayMock downloadClientGateway = new();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGateway);
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());
            await InitData();
        }

        [Fact]
        public async Task ProcessQueueAsync_PublishedFileFailure_QueuesOneProofGatedRecoveryAndPersistsAttempt()
        {
            var sourceDir = FileService.GetTempDirectory("recovery-processor-source");
            var libraryPath = FileService.GetTempDirectory("recovery-processor-library");
            var bookDirectory = Path.Combine(
                libraryPath,
                "Recovery Author",
                "Recovery Book B012345678");
            Directory.CreateDirectory(bookDirectory);
            var sourcePath = await FileService.GetFileAsync(sourceDir, "B012345678.m4b");
            var destinationPath = Path.Combine(bookDirectory, "01.m4b");
            File.Copy(sourcePath, destinationPath);
            downloadClientGateway.SourceFiles = [sourcePath];

            var metadata = new Mock<IMetadataService>(MockBehavior.Strict);
            metadata.Setup(service => service.ExtractFileMetadataAsync(
                    It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Asin = "B012345678",
                    Title = "Recovery Book",
                    Artist = "Recovery Author",
                    Duration = TimeSpan.FromMinutes(20),
                    Format = "m4b"
                });
            var importService = new Mock<IDownloadImportService>(MockBehavior.Strict);
            importService.Setup(service => service.ImportDownloadFilesAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<DownloadImportOptions?>()))
                .ReturnsAsync((Audiobook _, List<string> _, CancellationToken _, DownloadImportOptions? _) =>
                    [ImportResult.ImportFailure(FileAction.Move, sourcePath, destinationPath)]);

            _services.Replace(ServiceDescriptor.Singleton(importService.Object));
            _services.Replace(ServiceDescriptor.Singleton<IMetadataService>(metadata.Object));
            Init();
            await InitData();
            await AddAuthorizedRootAsync(libraryPath);
            var authorizedLibrary = await _provider
                .GetRequiredService<IScanPathAuthorizationService>()
                .AuthorizeAsync(libraryPath);
            Assert.True(authorizedLibrary.IsAuthorized, authorizedLibrary.Error);

            var audiobookToAdd = new AudiobookBuilder()
                .WithTitle("Recovery Book")
                .WithAuthor("Recovery Author")
                .WithBasePath(libraryPath)
                .Build();
            audiobookToAdd.Asin = "B012345678";
            var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithAudiobook(audiobook)
                .Build());

            var sourceCapability = await _provider
                .GetRequiredService<IFilePublicationSourceCapability>()
                .CheckAsync(sourcePath);
            Assert.True(sourceCapability.IsSupported, sourceCapability.Reason);
            var sourceProof = sourceCapability.SourceProof!.Value;
            var contextFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = Guid.NewGuid(),
                    Action = FileAction.Move,
                    SourcePath = Path.GetFullPath(sourcePath),
                    DestinationPath = Path.GetFullPath(destinationPath),
                    SourceParentDirectoryObjectIdentity = "source-parent",
                    DestinationParentDirectoryObjectIdentity = "destination-parent",
                    SourcePhysicalObjectIdentity = sourceProof.PhysicalObjectIdentity,
                    SourceLength = sourceProof.Length,
                    SourceSha256 = sourceProof.Sha256,
                    TargetPhysicalObjectIdentity = "previous-target-identity",
                    State = FileMutationJournalState.NeedsAttention,
                    Error = "The registration destination changed before content verification."
                });
                await db.SaveChangesAsync();
            }

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);
            Assert.True(
                job.Status == ProcessingJobStatus.Pending,
                $"Recovery job ended {job.Status}: {job.ErrorMessage}; "
                    + $"{job.JobData.GetValueOrDefault("PostImportRecovery.Reason")}");
            Assert.Equal(1, job.RetryCount);
            Assert.Equal(
                "1",
                job.JobData["PostImportRecovery.ScanAttemptCount"].ToString());
            Assert.Equal(
                "Pending",
                job.JobData["PostImportRecovery.State"].ToString());

            var scanQueue = _provider.GetRequiredService<IScanQueueService>();
            Assert.True(scanQueue.TryGetJob(
                Guid.Parse(job.JobData["PostImportRecovery.ScanJobId"].ToString()!),
                out var scanJob));
            Assert.Equal(audiobook.Id, scanJob!.AudiobookId);
            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(scanJob, CancellationToken.None);
            var registeredFiles = await _audiobookFileRepository
                .GetByAudiobookIdAsync(audiobook.Id);
            Assert.True(
                registeredFiles.Count == 1,
                $"Recovery scan status {scanJob.Status}: {scanJob.Error}");
            Assert.Equal(destinationPath, registeredFiles[0].Path);
            Assert.True(File.Exists(sourcePath));
            importService.Verify(
                service => service.ImportDownloadFilesAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<DownloadImportOptions?>()),
                Times.Once);

        }

        [Fact]
        public async Task ProcessJob_PublishedFileRecoverySurvivesRestartAndFinalizesWithoutRepublishing()
        {
            await UsePersistentSqlitePersistenceAsync();

            var sourceDir = FileService.GetTempDirectory("recovery-restart-source");
            var libraryRoot = FileService.GetTempDirectory("recovery-restart-library");
            var audiobookPath = Path.Combine(
                libraryRoot,
                "Recovery Author",
                "Recovery Book B012345678");
            Directory.CreateDirectory(audiobookPath);
            var sourcePath = await FileService.GetFileAsync(sourceDir, "B012345678.m4b");
            var destinationPath = Path.Combine(audiobookPath, "Recovery Book.m4b");
            File.Copy(sourcePath, destinationPath);
            var sourceHashBeforeRecovery = await ComputeSha256Async(sourcePath);
            var destinationHashBeforeRecovery = await ComputeSha256Async(destinationPath);
            downloadClientGateway.SourceFiles = [sourcePath];

            var failureGate = new RecoveryImportFailureGate(destinationPath);
            _services.AddSingleton(failureGate);
            _services.AddScoped<DownloadImportService>();
            _services.Replace(ServiceDescriptor.Scoped<IDownloadImportService>(provider =>
                new FailFirstImportService(
                    provider.GetRequiredService<DownloadImportService>(),
                    failureGate)));
            Init();
            await InitData();

            await AddAuthorizedRootAsync(libraryRoot);
            var audiobookToAdd = new AudiobookBuilder()
                .WithTitle("Recovery Book")
                .WithAuthor("Recovery Author")
                .WithBasePath(audiobookPath)
                .Build();
            audiobookToAdd.Asin = "B012345678";
            var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithAudiobook(audiobook)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(libraryRoot)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var sourceCapability = await _provider
                .GetRequiredService<IFilePublicationSourceCapability>()
                .CheckAsync(sourcePath);
            var destinationCapability = await _provider
                .GetRequiredService<IFilePublicationSourceCapability>()
                .CheckAsync(destinationPath);
            Assert.True(sourceCapability.IsSupported, sourceCapability.Reason);
            Assert.True(destinationCapability.IsSupported, destinationCapability.Reason);
            var sourceProof = sourceCapability.SourceProof!.Value;
            var destinationProof = destinationCapability.SourceProof!.Value;
            var semanticsResolver = _provider.GetRequiredService<IFileSystemSemanticsResolver>();
            var sourceSemantics = await semanticsResolver.ResolveAsync(sourceDir);
            var destinationSemantics = await semanticsResolver.ResolveAsync(audiobookPath);
            Assert.Equal(PathIdentityState.Valid, sourceSemantics.State);
            Assert.Equal(PathIdentityState.Valid, destinationSemantics.State);
            var operationId = FileMoveOperationIdentity.CreateForPaths(
                "download-import",
                audiobook.Id,
                FileAction.Copy,
                sourcePath,
                sourceSemantics.Semantics,
                sourceProof,
                destinationPath,
                destinationSemantics.Semantics);
            using var sourceParent = Listenarr.Infrastructure.FileSystem.PinnedDirectoryCreation
                .OpenPinnedHierarchyNoFollow(sourceDir, createMissing: false);
            using var destinationParent = Listenarr.Infrastructure.FileSystem.PinnedDirectoryCreation
                .OpenPinnedHierarchyNoFollow(audiobookPath, createMissing: false);

            await using (var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync())
            {
                db.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = operationId,
                    Action = FileAction.Copy,
                    SourcePath = Path.GetFullPath(sourcePath),
                    DestinationPath = Path.GetFullPath(destinationPath),
                    SourceParentDirectoryObjectIdentity =
                        sourceParent.GetDirectoryObjectIdentity(),
                    DestinationParentDirectoryObjectIdentity =
                        destinationParent.GetDirectoryObjectIdentity(),
                    SourcePhysicalObjectIdentity = sourceProof.PhysicalObjectIdentity,
                    SourceLength = sourceProof.Length,
                    SourceSha256 = sourceProof.Sha256,
                    TargetPhysicalObjectIdentity = "previous-target-identity",
                    State = FileMutationJournalState.NeedsAttention,
                    AudiobookId = audiobook.Id,
                    Error = "The registration destination changed before content verification."
                });
                await db.SaveChangesAsync();
            }

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            var pendingJob = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(pendingJob);
            var journalsAfterInitialAttempt = await GetJournalsAsync();
            Assert.True(
                pendingJob.Status == ProcessingJobStatus.Pending,
                $"Recovery job ended {pendingJob.Status}: {pendingJob.ErrorMessage}; "
                    + string.Join(" | ", pendingJob.ProcessingLog)
                    + "; journals: "
                    + string.Join(" | ", journalsAfterInitialAttempt.Select(journal =>
                        $"{journal.OperationId} {journal.Action} {journal.State} {journal.Error} "
                            + $"{journal.SourcePath} -> {journal.DestinationPath} "
                            + $"{journal.SourcePhysicalObjectIdentity} {journal.SourceSha256}")));
            Assert.Equal(1, pendingJob.RetryCount);
            Assert.Equal(
                "Pending",
                pendingJob.JobData["PostImportRecovery.State"].ToString());
            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));

            var scanQueue = _provider.GetRequiredService<IScanQueueService>();
            var recoveryScanId = Guid.Parse(
                pendingJob.JobData["PostImportRecovery.ScanJobId"].ToString()!);
            Assert.True(scanQueue.TryGetJob(recoveryScanId, out var recoveryScan));
            await _provider.GetRequiredService<IScanJobProcessor>()
                .ProcessJobAsync(recoveryScan!, CancellationToken.None);

            var registeredFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            var registeredFile = Assert.Single(registeredFiles);
            Assert.Equal(destinationPath, registeredFile.Path);
            var registeredIdentityBeforeRetry = registeredFile.PhysicalObjectIdentity;
            Assert.False(string.IsNullOrWhiteSpace(registeredIdentityBeforeRetry));
            Assert.Equal(
                FileMutationJournalState.NeedsAttention,
                await GetJournalStateAsync(operationId));
            Assert.Equal(sourceHashBeforeRecovery, await ComputeSha256Async(sourcePath));
            Assert.Equal(destinationHashBeforeRecovery, await ComputeSha256Async(destinationPath));

            await RestartProviderAsync();
            await InitData();

            var persistedJob = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(persistedJob);
            Assert.Equal(ProcessingJobStatus.Pending, persistedJob.Status);
            Assert.Equal(
                recoveryScanId.ToString("D"),
                persistedJob.JobData["PostImportRecovery.ScanJobId"].ToString());
            Assert.False(
                _provider.GetRequiredService<IScanQueueService>()
                    .TryGetJob(recoveryScanId, out _),
                "The test must exercise a restarted in-memory scan queue.");

            var retryJob = persistedJob;
            retryJob.NextRetryAt = null;
            await downloadProcessingJobService.UpdateJobAsync(retryJob);
            await downloadProcessingJobProcessor.ProcessJobAsync(
                retryJob,
                CancellationToken.None);

            var completedJob = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(completedJob);
            Assert.True(
                completedJob.Status == ProcessingJobStatus.Completed,
                $"Retry job ended {completedJob.Status}: {completedJob.ErrorMessage}; "
                    + string.Join(" | ", completedJob.ProcessingLog)
                    + "; journals: "
                    + string.Join(" | ", (await GetJournalsAsync()).Select(journal =>
                        $"{journal.OperationId} {journal.State} {journal.Error} "
                            + $"{journal.TargetPhysicalObjectIdentity} {journal.AudiobookFileId}")));
            Assert.True(completedJob.HasCheckpoint("FilesImported"));
            Assert.True(completedJob.HasCheckpoint("ClientMarkedImported"));
            Assert.True(completedJob.HasCheckpoint("ImportCommitted"));
            Assert.Equal(
                "Recovered",
                completedJob.JobData["PostImportRecovery.State"].ToString());
            Assert.Contains(
                ImportFinalizationService.ImportedAudioOutputsJobDataKey,
                completedJob.JobData.Keys);
            using (var outputProof = System.Text.Json.JsonDocument.Parse(
                completedJob.JobData[
                    ImportFinalizationService.ImportedAudioOutputsJobDataKey].ToString()!))
            {
                var output = Assert.Single(
                    outputProof.RootElement.EnumerateArray().ToArray());
                Assert.Equal(destinationPath, output.GetProperty("Path").GetString());
                Assert.Equal(
                    new FileInfo(destinationPath).Length,
                    output.GetProperty("Length").GetInt64());
            }

            var finalDownload = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(finalDownload);
            Assert.Equal(DownloadStatus.Moved, finalDownload.Status);
            Assert.Single(await _downloadRepository.GetByAudiobookIdAsync(audiobook.Id));
            var finalRegisteredFiles = await _audiobookFileRepository
                .GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(finalRegisteredFiles);
            Assert.Equal(destinationPath, finalRegisteredFiles[0].Path);
            Assert.Equal(
                registeredIdentityBeforeRetry,
                finalRegisteredFiles[0].PhysicalObjectIdentity);
            var finalDestinationProof = await _provider
                .GetRequiredService<IFilePublicationSourceCapability>()
                .CheckAsync(destinationPath);
            Assert.True(finalDestinationProof.IsSupported, finalDestinationProof.Reason);
            Assert.Equal(
                finalRegisteredFiles[0].PhysicalObjectIdentity,
                finalDestinationProof.SourceProof!.Value.PhysicalObjectIdentity);
            Assert.Equal(FileMutationJournalState.Completed, await GetJournalStateAsync(operationId));
            Assert.Equal(sourceHashBeforeRecovery, await ComputeSha256Async(sourcePath));
            Assert.Equal(destinationHashBeforeRecovery, await ComputeSha256Async(destinationPath));
            Assert.Single(Directory.GetFiles(audiobookPath));
            Assert.Equal(1, downloadClientGateway.GetCallCount(
                nameof(IDownloadClientGateway.MarkItemAsImportedAsync)));
            Assert.Equal(0, downloadClientGateway.GetCallCount(
                nameof(IDownloadClientGateway.RemoveAsync)));

            var histories = await _historyRepository.GetByCorrelationIdAsync(
                completedJob.GetOrCreateCorrelationId());
            Assert.Contains(histories, history =>
                history.EventType == HistoryEvents.Imported
                && history.Outcome == HistoryOutcome.Succeeded);
            var activeItems = await _provider
                .GetRequiredService<IDownloadQueueService>()
                .GetQueueAsync();
            Assert.DoesNotContain(activeItems, item => item.Id == download.Id);

            var processingJobs = await _downloadProcessingJobRepository
                .GetByDownloadIdAsync(download.Id);
            Assert.Single(processingJobs);
            Assert.Equal(jobId, processingJobs[0].Id);

            await downloadProcessingJobProcessor.ProcessJobAsync(
                completedJob,
                CancellationToken.None);
            Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
            Assert.Single(await _downloadProcessingJobRepository.GetByDownloadIdAsync(download.Id));
            Assert.Equal(1, downloadClientGateway.GetCallCount(
                nameof(IDownloadClientGateway.MarkItemAsImportedAsync)));
        }

        private async Task InitData()
        {
            downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
        }

        private async Task UsePersistentSqlitePersistenceAsync()
        {
            var databasePath = Path.Combine(
                FileService.GetTempPath(),
                "post-import-recovery-restart.db");
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;

            _services.RemoveAll<IDbContextFactory<ListenArrDbContext>>();
            _services.RemoveAll<DbContextOptions<ListenArrDbContext>>();
            _services.RemoveAll<ListenArrDbContext>();
            _services.AddSingleton(options);
            _services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(
                new PersistentListenArrDbContextFactory(options));
            _services.AddScoped(provider => new ListenArrDbContext(
                provider.GetRequiredService<DbContextOptions<ListenArrDbContext>>()));

            Init();
            await using var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        private async Task RestartProviderAsync()
        {
            await _provider.DisposeAsync();
            _provider = _services.BuildServiceProvider();
            _applicationSettingsRepository = _provider.GetRequiredService<IApplicationSettingsRepository>();
            _downloadClientConfigurationRepository = _provider.GetRequiredService<IDownloadClientConfigurationRepository>();
            _downloadRepository = _provider.GetRequiredService<IDownloadRepository>();
            _remotePathMappingRepository = _provider.GetRequiredService<IRemotePathMappingRepository>();
            _historyRepository = _provider.GetRequiredService<IHistoryRepository>();
            _audiobookRepository = _provider.GetRequiredService<IAudiobookRepository>();
            _audiobookFileRepository = _provider.GetRequiredService<IAudiobookFileRepository>();
            _downloadProcessingJobRepository = _provider.GetRequiredService<IDownloadProcessingJobRepository>();
            _indexerRepository = _provider.GetRequiredService<IIndexerRepository>();
            _downloadHistoryRepository = _provider.GetRequiredService<IDownloadHistoryRepository>();
            _qualityProfileRepository = _provider.GetRequiredService<IQualityProfileRepository>();
            _moveJobRepository = _provider.GetRequiredService<IMoveJobRepository>();
            _rootFolderRepository = _provider.GetRequiredService<IRootFolderRepository>();
            _applicationPathService = _provider.GetRequiredService<IApplicationPathService>();
        }

        private async Task<FileMutationJournalState> GetJournalStateAsync(Guid operationId)
        {
            await using var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            return await db.FileMutationJournals
                .Where(journal => journal.OperationId == operationId)
                .Select(journal => journal.State)
                .SingleAsync();
        }

        private async Task<List<FileMutationJournal>> GetJournalsAsync()
        {
            await using var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            return await db.FileMutationJournals.AsNoTracking().ToListAsync();
        }

        private static async Task<string> ComputeSha256Async(string path)
        {
            await using var stream = File.OpenRead(path);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
            return Convert.ToHexString(hash);
        }

        private sealed class PersistentListenArrDbContextFactory(
            DbContextOptions<ListenArrDbContext> options)
            : IDbContextFactory<ListenArrDbContext>
        {
            public ListenArrDbContext CreateDbContext() => new(options);

            public Task<ListenArrDbContext> CreateDbContextAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }

        private sealed class RecoveryImportFailureGate(string destinationPath)
        {
            private int _hasFailed;

            public string DestinationPath { get; } = destinationPath;

            public bool ShouldFailFirstAttempt() =>
                Interlocked.Exchange(ref _hasFailed, 1) == 0;
        }

        private sealed class FailFirstImportService(
            IDownloadImportService inner,
            RecoveryImportFailureGate failureGate) : IDownloadImportService
        {
            public Task<List<ImportResult>> ImportDownloadFilesAsync(
                Audiobook audiobook,
                List<string> files,
                CancellationToken cancellationToken = default,
                DownloadImportOptions? options = null)
            {
                if (failureGate.ShouldFailFirstAttempt())
                {
                    return Task.FromResult(new List<ImportResult>
                    {
                        ImportResult.ImportFailure(
                            FileAction.Copy,
                            files.Single(),
                            failureGate.DestinationPath)
                    });
                }

                return inner.ImportDownloadFilesAsync(
                    audiobook,
                    files,
                    cancellationToken,
                    options);
            }
        }

        [Fact]
        public async Task ProcessJob_HappyPath()
        {
            var sourceDir = FileService.GetTempDirectory("dl-dir");
            var destRoot = FileService.GetTempDirectory("dl-dest");

            var audioPath = await FileService.GetFileAsync(sourceDir, "book.m4b");
            var coverPath = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var txtPath = await FileService.GetFileAsync(sourceDir, "book.txt");

            downloadClientGateway.SourceFiles = [audioPath, coverPath, txtPath];

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(audiobook)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var expectedAudioDest = Path.Join(destRoot, "book.m4b");
            var expectedCoverDest = Path.Join(destRoot, "cover.jpg");
            var expectedTxtDest = Path.Join(destRoot, "book.txt");

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);
            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);

            Assert.False(File.Exists(expectedAudioDest));
            Assert.False(File.Exists(expectedCoverDest));
            Assert.False(File.Exists(expectedTxtDest));
            Assert.Equal(sourceDir, download.DownloadPath);

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);

            Assert.True(File.Exists(expectedAudioDest));
            Assert.True(File.Exists(expectedCoverDest));
            Assert.True(File.Exists(expectedTxtDest));
            Assert.Equal(sourceDir, download.DownloadPath);
        }

        [Fact]
        public async Task ProcessJob_RecoveredSourceRegistersAudiobookBeforeClientCleanup()
        {
            var sourceDir = FileService.GetTempDirectory("recovered-dl-dir");
            var destRoot = FileService.GetTempDirectory("recovered-dl-dest");
            var audioPath = await FileService.GetFileAsync(sourceDir, "B000R34YKC.m4b");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithAudiobook(audiobook)
                .Build());
            download.SetMetadata(
                Download.RecoveredSourceFilesMetadataKey,
                System.Text.Json.JsonSerializer.Serialize(new[] { audioPath }));
            await _downloadRepository.UpdateAsync(download);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destRoot)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var registeredBeforeClientCleanup = false;
            downloadClientGateway.BeforeMarkImportedAsync = async _ =>
            {
                var importedFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
                Assert.NotEmpty(importedFiles);
                Assert.All(importedFiles, file => Assert.True(File.Exists(file.Path)));
                registeredBeforeClientCleanup = true;
            };

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            var processedJob = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.True(
                registeredBeforeClientCleanup,
                $"Job status: {processedJob?.Status}; error: {processedJob?.ErrorMessage}; logs: {string.Join(" | ", processedJob?.ProcessingLog ?? [])}");
            Assert.Equal(1, downloadClientGateway.GetCallCount(nameof(IDownloadClientGateway.MarkItemAsImportedAsync)));
            Assert.NotEmpty(jobId);
        }

        [Fact]
        public async Task ProcessQueueAsync_DirectorySource_UsesClientReportedFilesToExcludeUnrelatedFiles()
        {
            var sourceDir = FileService.GetTempDirectory($"dl-dir");
            var destRoot = FileService.GetTempDirectory($"dl-dest");

            var audioPath = await FileService.GetFileAsync(sourceDir, "book.m4b");
            var coverPath = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var txtPath = await FileService.GetFileAsync(sourceDir, "book.txt");
            var unrelatedPath = await FileService.GetFileAsync(sourceDir, "unrelated.txt");

            var downloadClientGatewayMock = new DownloadClientGatewayMock
            {
                SourceFiles = [audioPath, coverPath, txtPath]
            };
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGatewayMock);
            _services.Replace(new ServiceDescriptor(typeof(IDownloadItemService), typeof(DownloadItemService), ServiceLifetime.Singleton));
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());
            await InitData();

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destRoot)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("client-1")
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithAudiobook(audiobook)
                .Build());

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);
            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            Assert.True(File.Exists(Path.Join(destRoot, "book.m4b")));
            Assert.True(File.Exists(Path.Join(destRoot, "cover.jpg")));
            Assert.True(File.Exists(Path.Join(destRoot, "book.txt")));
            Assert.False(File.Exists(Path.Join(destRoot, "unrelated.txt")));
        }
    }
}
