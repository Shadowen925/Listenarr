using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Processing;

[Trait("Name", "PostImportScanRecoveryServiceTests")]
[Trait("Category", "DownloadProcessingJob")]
public sealed class PostImportScanRecoveryServiceTests : BaseTests
{
    [Fact]
    public async Task TryEnqueueAsync_QueuesTargetedScanForMatchingPublishedOutputAndJournal()
    {
        var sourcePath = await FileService.GetFileAsync(
            FileService.GetTempDirectory("recovery-source"),
            "B012345678.m4b");
        var libraryPath = FileService.GetTempDirectory("recovery-library");
        var destinationPath = Path.Combine(libraryPath, "B012345678.m4b");
        File.Copy(sourcePath, destinationPath);
        var audiobookToAdd = new AudiobookBuilder()
            .WithTitle("Recovery Book")
            .WithBasePath(libraryPath)
            .Build();
        audiobookToAdd.Asin = "B012345678";
        var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);
        await AddNeedsAttentionJournalAsync(sourcePath, destinationPath);

        var service = CreateRecoveryService();
        var result = await service.TryEnqueueAsync(
            audiobook,
            new Download { Id = "recovery-download" },
            [ImportResult.ImportFailure(FileAction.Move, sourcePath, destinationPath)],
            "recovery-correlation",
            CancellationToken.None);

        Assert.True(result.WasEnqueued, result.Reason);
        Assert.True(result.ScanJobId.HasValue);
        var scanQueue = _provider.GetRequiredService<IScanQueueService>();
        Assert.True(scanQueue.TryGetJob(result.ScanJobId.Value, out var scanJob));
        Assert.Equal(audiobook.Id, scanJob!.AudiobookId);
        Assert.Equal(
            "post-import-recovery:recovery-correlation",
            scanJob.CorrelationId);
        Assert.Equal("recovery-download", scanJob.DownloadId);

        var duplicate = await service.TryEnqueueAsync(
            audiobook,
            new Download { Id = "recovery-download" },
            [ImportResult.ImportFailure(FileAction.Move, sourcePath, destinationPath)],
            "recovery-correlation",
            CancellationToken.None);
        Assert.Equal(result.ScanJobId, duplicate.ScanJobId);
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task TryEnqueueAsync_RejectsPublishedOutputWithDifferentContent()
    {
        var sourcePath = await FileService.GetFileAsync(
            FileService.GetTempDirectory("recovery-source-mismatch"),
            "book.m4b");
        var libraryPath = FileService.GetTempDirectory("recovery-library-mismatch");
        var destinationPath = Path.Combine(libraryPath, "book.m4b");
        await File.WriteAllTextAsync(destinationPath, "different content");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder().WithBasePath(libraryPath).Build());
        await AddNeedsAttentionJournalAsync(sourcePath, destinationPath);

        var service = CreateRecoveryService();
        var result = await service.TryEnqueueAsync(
            audiobook,
            new Download { Id = "recovery-download-mismatch" },
            [ImportResult.ImportFailure(FileAction.Move, sourcePath, destinationPath)],
            "recovery-correlation-mismatch",
            CancellationToken.None);

        Assert.False(result.WasEnqueued);
        Assert.Contains("content proof", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryEnqueueAsync_RejectsJournalWithoutCurrentTargetIdentityMismatch()
    {
        var sourcePath = await FileService.GetFileAsync(
            FileService.GetTempDirectory("recovery-source-identity"),
            "B012345678.m4b");
        var libraryPath = FileService.GetTempDirectory("recovery-library-identity");
        var destinationPath = Path.Combine(libraryPath, "B012345678.m4b");
        File.Copy(sourcePath, destinationPath);
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder().WithBasePath(libraryPath).Build());
        await AddNeedsAttentionJournalAsync(
            sourcePath,
            destinationPath,
            targetIdentityMatches: true);

        var result = await CreateRecoveryService()
            .TryEnqueueAsync(
                audiobook,
                new Download { Id = "recovery-download-identity" },
                [ImportResult.ImportFailure(FileAction.Move, sourcePath, destinationPath)],
                "recovery-correlation-identity",
                CancellationToken.None);

        Assert.False(result.WasEnqueued);
        Assert.Contains("target-identity mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryEnqueueAsync_RejectsDestinationOutsideAuthorizedScanRoots()
    {
        var sourcePath = await FileService.GetFileAsync(
            FileService.GetTempDirectory("recovery-source-unauthorized"),
            "B012345678.m4b");
        var libraryPath = FileService.GetTempDirectory("recovery-library-unauthorized");
        var destinationPath = Path.Combine(libraryPath, "B012345678.m4b");
        File.Copy(sourcePath, destinationPath);
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder().WithBasePath(libraryPath).Build());
        await AddNeedsAttentionJournalAsync(sourcePath, destinationPath);

        var result = await CreateRecoveryService(authorized: false).TryEnqueueAsync(
            audiobook,
            new Download { Id = "recovery-download-unauthorized" },
            [ImportResult.ImportFailure(FileAction.Move, sourcePath, destinationPath)],
            "recovery-correlation-unauthorized",
            CancellationToken.None);

        Assert.False(result.WasEnqueued);
        Assert.Contains("configured root", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private PostImportScanRecoveryService CreateRecoveryService(bool authorized = true)
    {
        var authorization = new Mock<IScanPathAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) =>
            {
                if (!authorized)
                {
                    return ScanPathAuthorizationResult.Rejected(
                        ScanPathAuthorizationFailure.OutsideConfiguredRoots,
                        "The scan path is not within a configured root folder.");
                }

                var boundary = Path.GetDirectoryName(path)
                    ?? throw new InvalidOperationException("The test path has no parent directory.");
                var semantics = FileSystemPathSemantics.CurrentHostDefault;
                return ScanPathAuthorizationResult.Authorized(
                    path,
                    PathIdentitySnapshot.FromResolution(
                        semantics,
                        FileSystemCaseSensitivityMode.Auto,
                        boundary,
                        path),
                    ScanPathPhysicalIdentity.PinnedPathOnly());
            });

        return new PostImportScanRecoveryService(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            _provider.GetRequiredService<IFilePublicationSourceCapability>(),
            authorization.Object,
            _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>(),
            _provider.GetRequiredService<IAudiobookFileService>(),
            _provider.GetRequiredService<IScanQueueService>());
    }

    private async Task AddNeedsAttentionJournalAsync(
        string sourcePath,
        string destinationPath,
        bool targetIdentityMatches = false)
    {
        var capability = _provider.GetRequiredService<IFilePublicationSourceCapability>();
        var source = await capability.CheckAsync(sourcePath);
        var destination = await capability.CheckAsync(destinationPath);
        Assert.True(source.IsSupported, source.Reason);
        Assert.True(destination.IsSupported, destination.Reason);
        Assert.NotNull(source.SourceProof);
        Assert.NotNull(destination.SourceProof);

        var sourceProof = source.SourceProof.Value;
        var contextFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
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
            TargetPhysicalObjectIdentity = targetIdentityMatches
                ? destination.SourceProof.Value.PhysicalObjectIdentity
                : "previous-target-identity",
            State = FileMutationJournalState.NeedsAttention,
            Error = "The registration destination changed before content verification."
        });
        await db.SaveChangesAsync();
    }
}
