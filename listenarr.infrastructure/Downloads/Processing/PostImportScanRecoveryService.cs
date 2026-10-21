using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Downloads.Processing;

public sealed class PostImportScanRecoveryService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFilePublicationSourceCapability publicationSourceCapability,
    IScanPathAuthorizationService scanPathAuthorizationService,
    IAudiobookFilePathIdentityResolver pathIdentityResolver,
    IAudiobookFileService audiobookFileService,
    IScanQueueService scanQueueService)
{
    private const string RecoveryCorrelationPrefix = "post-import-recovery:";

    public async Task<PostImportScanRecoveryResult> TryEnqueueAsync(
        Audiobook audiobook,
        Download download,
        IReadOnlyCollection<ImportResult> failedResults,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audiobook.BasePath))
        {
            return PostImportScanRecoveryResult.NotEligible(
                "Automatic scan recovery was skipped because the audiobook has no library path.");
        }

        var failedAudioResults = failedResults
            .Where(result => FileUtils.IsAudioFile(result.SourcePath ?? string.Empty)
                || FileUtils.IsAudioFile(result.FinalPath ?? string.Empty))
            .ToList();
        if (failedAudioResults.Count == 0 || failedAudioResults.Count != failedResults.Count)
        {
            return PostImportScanRecoveryResult.NotEligible(
                "Automatic scan recovery only applies when every failed import result is an audio publication.");
        }

        foreach (var result in failedAudioResults)
        {
            var validation = await ValidatePublishedOutputAsync(
                audiobook,
                result,
                cancellationToken);
            if (validation != null)
            {
                return PostImportScanRecoveryResult.NotEligible(validation);
            }
        }

        try
        {
            var scanJobId = await scanQueueService.EnqueueScanAsync(
                audiobook,
                correlationId: $"{RecoveryCorrelationPrefix}{correlationId}",
                downloadId: download.Id);
            return PostImportScanRecoveryResult.Enqueued(scanJobId);
        }
        catch (Exception exception) when (
            exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return PostImportScanRecoveryResult.NotEligible(
                $"The targeted audiobook scan could not be queued: {exception.Message}");
        }
    }

    private async Task<string?> ValidatePublishedOutputAsync(
        Audiobook audiobook,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.SourcePath)
            || string.IsNullOrWhiteSpace(result.FinalPath)
            || !File.Exists(result.SourcePath)
            || !File.Exists(result.FinalPath))
        {
            return "Automatic scan recovery requires both the original source and published destination files to exist.";
        }

        string sourcePath;
        string destinationPath;
        try
        {
            sourcePath = Path.GetFullPath(result.SourcePath);
            destinationPath = Path.GetFullPath(result.FinalPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Automatic scan recovery rejected an invalid source or destination path: {exception.Message}";
        }

        var destinationIdentity = await pathIdentityResolver.ResolveAsync(
            audiobook,
            destinationPath,
            cancellationToken);
        if (destinationIdentity.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(destinationIdentity.BoundaryPath))
        {
            return destinationIdentity.Reason
                ?? "Automatic scan recovery could not verify that the destination is inside an authorized library root.";
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return "Automatic scan recovery could not identify the destination directory.";
        }

        var authorization = await scanPathAuthorizationService.AuthorizeAsync(
            destinationDirectory,
            cancellationToken);
        if (!authorization.IsAuthorized)
        {
            return authorization.Error
                ?? "Automatic scan recovery rejected a destination outside configured scan roots.";
        }

        var semantics = new FileSystemPathSemantics(
            destinationIdentity.Syntax,
            destinationIdentity.CaseSensitivity);
        var audiobookRoot = FileSystemPathIdentity.Canonicalize(
            audiobook.BasePath!,
            semantics.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                destinationIdentity.CanonicalPath,
                audiobookRoot,
                semantics))
        {
            return "Automatic scan recovery rejected a destination outside the intended audiobook folder.";
        }

        var ownership = await audiobookFileService.CheckAudiobookFileOwnershipAsync(
            audiobook,
            destinationPath,
            audiobook.BasePath,
            cancellationToken);
        if (ownership.Outcome is not (
            AudiobookFileOwnershipCheckOutcome.Available
            or AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook))
        {
            return ownership.Outcome == AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook
                ? "Automatic scan recovery rejected a destination already owned by another audiobook."
                : ownership.Reason
                    ?? "Automatic scan recovery could not establish safe ownership of the destination.";
        }

        var sourceCapability = await publicationSourceCapability.CheckAsync(
            sourcePath,
            cancellationToken);
        if (!sourceCapability.IsSupported
            || sourceCapability.SourceProof is not { } sourceProof
            || !sourceProof.HasDurablePhysicalObjectIdentity)
        {
            return sourceCapability.Reason
                ?? "Automatic scan recovery requires a durable source identity and content proof.";
        }

        var destinationCapability = await publicationSourceCapability.CheckAsync(
            destinationPath,
            cancellationToken);
        if (!destinationCapability.IsSupported
            || destinationCapability.SourceProof is not { } destinationProof)
        {
            return destinationCapability.Reason
                ?? "Automatic scan recovery could not establish destination identity and content proof.";
        }

        if (sourceProof.Length <= 0
            || sourceProof.Length != destinationProof.Length
            || !string.Equals(sourceProof.Sha256, destinationProof.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return "Automatic scan recovery rejected the destination because its content proof does not match the source.";
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var journals = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.State == FileMutationJournalState.NeedsAttention
                && journal.Error == FileMutationJournalRecoveryPolicy.RegisteredTargetIdentityMismatch
                && journal.Action == result.Action
                && journal.SourceLength == sourceProof.Length
                && journal.SourceSha256 == sourceProof.Sha256
                && journal.SourcePhysicalObjectIdentity == sourceProof.PhysicalObjectIdentity
                && (journal.AudiobookId == null || journal.AudiobookId == audiobook.Id))
            .ToListAsync(cancellationToken);

        var matchingJournal = journals.SingleOrDefault(journal =>
            PathsEqual(journal.SourcePath, sourcePath, semantics)
            && PathsEqual(journal.DestinationPath, destinationPath, semantics));
        if (matchingJournal == null)
        {
            return "Automatic scan recovery requires the matching publication journal to be in the registered-target identity mismatch state.";
        }

        if (string.IsNullOrWhiteSpace(matchingJournal.TargetPhysicalObjectIdentity)
            || string.Equals(
                matchingJournal.TargetPhysicalObjectIdentity,
                destinationProof.PhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return "Automatic scan recovery rejected a publication journal without a verified target-identity mismatch.";
        }

        return null;
    }

    private static bool PathsEqual(
        string persistedPath,
        string candidatePath,
        FileSystemPathSemantics semantics)
    {
        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                persistedPath,
                candidatePath,
                semantics);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed record PostImportScanRecoveryResult(
    bool WasEnqueued,
    Guid? ScanJobId,
    string? Reason)
{
    public static PostImportScanRecoveryResult Enqueued(Guid scanJobId) =>
        new(true, scanJobId, null);

    public static PostImportScanRecoveryResult NotEligible(string reason) =>
        new(false, null, reason);
}
