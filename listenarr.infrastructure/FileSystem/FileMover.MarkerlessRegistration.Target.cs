using System.ComponentModel;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<FileMutationJournal> PublishMarkerlessRegistrationTargetAsync(
        FileAction action,
        FileMoveGateLease gate,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        var stageName = $".staging-{journal.OperationId:N}.partial";
        using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
            gate.SourceName,
            requireDeleteAccess: false);
        var sourceSharesDestinationVolume = sourceEntry != null
            && !ForceCrossVolumeForTest
            && sourceEntry.IsOnSameVolume(gate.DestinationParent);
        var requiresGenerationPreservingLink =
            action == FileAction.HardlinkCopy
            || (action == FileAction.Move
                && !OperatingSystem.IsWindows()
                && sourceSharesDestinationVolume);

        if (journal.State == FileMutationJournalState.TargetStaged)
        {
            using var publishedTarget = gate.DestinationParent.TryOpenExistingFile(
                gate.DestinationName,
                requireDeleteAccess: false);
            if (publishedTarget != null)
            {
                var existingPublishedIdentity = publishedTarget.GetObjectIdentity();
                var exactTargetIdentityMatches =
                    !string.IsNullOrWhiteSpace(
                        journal.TargetPhysicalObjectIdentity)
                    && publishedTarget.MatchesObjectIdentity(
                        journal.TargetPhysicalObjectIdentity);
                var renamedTargetIdentityMatches =
                    !exactTargetIdentityMatches
                    && !string.IsNullOrWhiteSpace(
                        journal.TargetPhysicalObjectIdentity)
                    && publishedTarget.MatchesWindowsFileId(
                        journal.TargetPhysicalObjectIdentity);
                if ((exactTargetIdentityMatches
                        || renamedTargetIdentityMatches)
                    && await MatchesMarkerlessTargetContentAsync(
                        publishedTarget,
                        journal,
                        cancellationToken))
                {
                    using var interruptedStage = gate.DestinationParent.TryOpenExistingFile(
                        stageName,
                        requireDeleteAccess: true);
                    if (interruptedStage != null)
                    {
                        if (!interruptedStage.IdentifiesSameEntry(publishedTarget)
                            || !interruptedStage.VisiblePathMatches())
                        {
                            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                                journal,
                                "The interrupted registration stage does not identify the already-published target.",
                                cancellationToken);
                            return await _fileMutationJournalStore!.GetAsync(
                                journal.OperationId,
                                cancellationToken)
                                ?? throw new InvalidOperationException(
                                    "The markerless registration journal disappeared.");
                        }

                        interruptedStage.Delete(immediateWindows: true);
                    }

                    if (renamedTargetIdentityMatches)
                    {
                        journal = await _fileMutationJournalStore!.AdvanceAsync(
                            journal.OperationId,
                            FileMutationJournalState.TargetStaged,
                            existingPublishedIdentity,
                            audiobookId: null,
                            error: null,
                            cancellationToken);
                    }

                    return await _fileMutationJournalStore!.AdvanceAsync(
                        journal.OperationId,
                        FileMutationJournalState.TargetIdentityPersisted,
                        existingPublishedIdentity,
                        audiobookId: null,
                        error: null,
                        cancellationToken);
                }

                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "A different registration destination appeared during staged publication.",
                    cancellationToken);
                return await _fileMutationJournalStore!.GetAsync(
                    journal.OperationId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The markerless registration journal disappeared.");
            }

            if (sourceEntry == null
                || !await MatchesMarkerlessSourceProofAsync(
                    sourceEntry,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The registration source changed before staged publication could resume.",
                    cancellationToken);
                return await _fileMutationJournalStore!.GetAsync(
                    journal.OperationId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The markerless registration journal disappeared.");
            }

            using var resumableStage = gate.DestinationParent.TryOpenExistingFile(
                stageName,
                requireDeleteAccess: true);
            if (resumableStage == null
                || (!string.IsNullOrWhiteSpace(
                        journal.TargetPhysicalObjectIdentity)
                    && !resumableStage.MatchesObjectIdentity(
                        journal.TargetPhysicalObjectIdentity)))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The staged registration target is missing or changed physical generation.",
                    cancellationToken);
                return await _fileMutationJournalStore!.GetAsync(
                    journal.OperationId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The markerless registration journal disappeared.");
            }

            await CopyMarkerlessFileAsync(
                sourceEntry,
                resumableStage,
                cancellationToken);
            sourceEntry.PreserveMarkerlessMetadataTo(resumableStage);
            if (AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync != null)
            {
                await AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync();
            }
            if (!await MatchesMarkerlessTargetContentAsync(
                    resumableStage,
                    journal,
                    cancellationToken))
            {
                throw new IOException(
                    "The staged registration destination failed content verification.");
            }

            journal = await _fileMutationJournalStore!.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.TargetStaged,
                resumableStage.GetObjectIdentity(),
                audiobookId: null,
                error: null,
                cancellationToken);
            resumableStage.MoveWithinParent(gate.DestinationName);
            if (AfterMarkerlessRegistrationTargetPublishedBeforeStateForTestAsync != null)
            {
                await AfterMarkerlessRegistrationTargetPublishedBeforeStateForTestAsync();
            }
            var publishedIdentity = resumableStage.GetObjectIdentity();
            journal = await _fileMutationJournalStore!.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.TargetStaged,
                publishedIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
            return await _fileMutationJournalStore!.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.TargetIdentityPersisted,
                publishedIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        using var existingTarget = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        if (existingTarget != null)
        {
            if (requiresGenerationPreservingLink
                && sourceEntry != null
                && VisiblePathMatchesOrThrowUnavailable(
                    sourceEntry,
                    "The hardlink registration source is temporarily unavailable while interrupted publication is being verified.")
                && VisiblePathMatchesOrThrowUnavailable(
                    existingTarget,
                    "The hardlink registration destination is temporarily unavailable while interrupted publication is being verified.")
                && sourceEntry.IdentifiesSameEntry(existingTarget)
                && await MatchesMarkerlessSourceProofAsync(
                    sourceEntry,
                    journal,
                    cancellationToken)
                && await MatchesMarkerlessTargetContentAsync(
                    existingTarget,
                    journal,
                    cancellationToken))
            {
                return await _fileMutationJournalStore!.AdvanceAsync(
                    journal.OperationId,
                    FileMutationJournalState.TargetIdentityPersisted,
                    existingTarget.GetObjectIdentity(),
                    audiobookId: null,
                    error: null,
                    cancellationToken);
            }

            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "A registration destination appeared before its physical identity was persisted.",
                cancellationToken);
            return await _fileMutationJournalStore!.GetAsync(
                journal.OperationId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared.");
        }

        if (sourceEntry == null
            || !await MatchesMarkerlessSourceProofAsync(
                sourceEntry,
                journal,
                cancellationToken))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registration source changed before destination publication.",
                cancellationToken);
            return await _fileMutationJournalStore!.GetAsync(
                journal.OperationId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared.");
        }

        string targetIdentity;
        PinnedDirectoryCreation.PinnedFileEntry? publishedHardlink = null;
        if (requiresGenerationPreservingLink
            && sourceSharesDestinationVolume)
        {
            try
            {
                if (BeforePinnedHardlinkCreationForTestAsync != null)
                {
                    await BeforePinnedHardlinkCreationForTestAsync();
                }
                publishedHardlink = sourceEntry.CreateHardLinkTo(
                    gate.DestinationParent,
                    gate.DestinationName);
                targetIdentity = publishedHardlink.GetObjectIdentity();
                if (AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync != null)
                {
                    await AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync();
                }
                journal = await _fileMutationJournalStore!.AdvanceAsync(
                    journal.OperationId,
                    FileMutationJournalState.TargetIdentityPersisted,
                    targetIdentity,
                    audiobookId: null,
                    error: null,
                    cancellationToken);
                if (AfterMarkerlessRegistrationTargetStateForTestAsync != null)
                {
                    await AfterMarkerlessRegistrationTargetStateForTestAsync();
                }
                return journal;
            }
            catch (Exception exception) when (exception is
                IOException or Win32Exception or PlatformNotSupportedException)
            {
                if (action == FileAction.Move && !OperatingSystem.IsWindows())
                {
                    _logger.LogWarning(
                        exception,
                        "Markerless Unix move publication could not preserve the exact source generation with a hardlink: {Source} -> {Destination}",
                        LogRedaction.SanitizeFilePath(gate.SourcePath),
                        LogRedaction.SanitizeFilePath(gate.DestinationPath));
                    throw new IOException(
                        "The move source generation could not be published safely on this filesystem.",
                        exception);
                }

                _logger.LogInformation(
                    exception,
                    "Markerless hardlink publication was unavailable; falling back to a direct final-name copy: {Source} -> {Destination}",
                    LogRedaction.SanitizeFilePath(gate.SourcePath),
                    LogRedaction.SanitizeFilePath(gate.DestinationPath));
            }
            finally
            {
                publishedHardlink?.Dispose();
            }
        }

        journal = await EnsureMarkerlessSourceHashAsync(
            sourceEntry,
            journal,
            cancellationToken);
        using (var abandonedStage = gate.DestinationParent.TryOpenExistingFile(
                   stageName,
                   requireDeleteAccess: true))
        {
            abandonedStage?.Delete();
        }

        using var stagedTarget = gate.DestinationParent.CreateNewFile(stageName);
        if (AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync != null)
        {
            await AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync();
        }
        journal = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetStaged,
            targetPhysicalObjectIdentity: null,
            audiobookId: null,
            error: null,
            cancellationToken);
        if (AfterMarkerlessRegistrationTargetStateForTestAsync != null)
        {
            await AfterMarkerlessRegistrationTargetStateForTestAsync();
        }

        await CopyMarkerlessFileAsync(
            sourceEntry,
            stagedTarget,
            cancellationToken);
        sourceEntry.PreserveMarkerlessMetadataTo(stagedTarget);
        if (AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync != null)
        {
            await AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync();
        }
        if (!await MatchesMarkerlessTargetContentAsync(
                stagedTarget,
                journal,
                cancellationToken))
        {
            throw new IOException(
                "The staged registration destination failed content verification.");
        }

        targetIdentity = stagedTarget.GetObjectIdentity();
        journal = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetStaged,
            targetIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
        stagedTarget.MoveWithinParent(gate.DestinationName);
        if (AfterMarkerlessRegistrationTargetPublishedBeforeStateForTestAsync != null)
        {
            await AfterMarkerlessRegistrationTargetPublishedBeforeStateForTestAsync();
        }
        targetIdentity = stagedTarget.GetObjectIdentity();
        journal = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetStaged,
            targetIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
        return await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            targetIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
    }

    private async Task<FileMutationJournal> VerifyMarkerlessRegistrationTargetAsync(
        FileMoveGateLease gate,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        using var targetEntry = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        var targetMatchesJournal = targetEntry != null
            && TargetMatchesMarkerlessJournal(targetEntry, journal);
        if (targetEntry == null || !targetMatchesJournal)
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                targetEntry == null
                    ? "The registration destination is missing before content verification."
                    : "The registration destination changed before content verification.",
                cancellationToken);
            return await _fileMutationJournalStore!.GetAsync(
                journal.OperationId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared.");
        }

        if (!await MatchesMarkerlessTargetContentAsync(
                targetEntry,
                journal,
                cancellationToken))
        {
            using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
                gate.SourceName,
                requireDeleteAccess: false);
            if (sourceEntry == null
                || !await MatchesMarkerlessSourceProofAsync(
                    sourceEntry,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The registration source is unavailable before destination content was verified.",
                    cancellationToken);
                return await _fileMutationJournalStore!.GetAsync(
                    journal.OperationId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The markerless registration journal disappeared.");
            }

            await CopyMarkerlessFileAsync(
                sourceEntry,
                targetEntry,
                cancellationToken);
            sourceEntry.PreserveMarkerlessMetadataTo(targetEntry);
            if (AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync != null)
            {
                await AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync();
            }
            if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
                || !await MatchesMarkerlessTargetContentAsync(
                    targetEntry,
                    journal,
                    cancellationToken))
            {
                throw new IOException(
                    "The markerless registration destination failed content verification.");
            }
        }

        return await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetVerified,
            journal.TargetPhysicalObjectIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
    }

    private static async Task<bool> MarkerlessRegistrationTargetMatchesAsync(
        FileMoveGateLease gate,
        FileMutationJournal journal,
        CancellationToken cancellationToken)
    {
        using var targetEntry = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        return targetEntry != null
            && TargetMatchesMarkerlessJournal(targetEntry, journal)
            && await MatchesMarkerlessTargetContentAsync(
                targetEntry,
                journal,
                cancellationToken);
    }
}
