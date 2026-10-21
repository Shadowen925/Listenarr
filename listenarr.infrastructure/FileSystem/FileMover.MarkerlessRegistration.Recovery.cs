using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> TryRecoverRegisteredDestinationAsync(
        FileMoveGateLease gate,
        FileMutationJournal journal,
        string? registeredDestinationRecoveryIdentity,
        int? registeredDestinationRecoveryAudiobookId,
        CancellationToken cancellationToken)
    {
        if (journal.Error
                != FileMutationJournalRecoveryPolicy.RegisteredTargetIdentityMismatch
            || string.IsNullOrWhiteSpace(
                registeredDestinationRecoveryIdentity)
            || registeredDestinationRecoveryAudiobookId is not > 0
            || (journal.AudiobookId.HasValue
                && journal.AudiobookId
                    != registeredDestinationRecoveryAudiobookId))
        {
            return false;
        }

        using var target = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        if (target == null
            || !target.VisiblePathMatches()
            || !target.MatchesObjectIdentity(
                registeredDestinationRecoveryIdentity)
            || !await MatchesMarkerlessTargetContentAsync(
                target,
                journal,
                cancellationToken))
        {
            _logger.LogWarning(
                "Registered-target recovery was rejected for operation {OperationIdPrefix} during {JournalState}; registered identity fingerprint {RegisteredIdentityFingerprint}, expected target identity fingerprint {ExpectedIdentityFingerprint}",
                journal.OperationId.ToString("N")[..8],
                journal.State,
                FileMoverIdentityDiagnostics.Fingerprint(
                    registeredDestinationRecoveryIdentity),
                FileMoverIdentityDiagnostics.Fingerprint(
                    journal.TargetPhysicalObjectIdentity));
            return false;
        }

        var observedIdentity = target.GetObjectIdentity();
        await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            observedIdentity,
            registeredDestinationRecoveryAudiobookId,
            error: null,
            cancellationToken);
        _logger.LogInformation(
            "Recovered published target for operation {OperationIdPrefix} using registered audiobook ownership and the original content proof",
            journal.OperationId.ToString("N")[..8]);
        return true;
    }
}

internal static class FileMoverIdentityDiagnostics
{
    internal static void LogTargetIdentityMismatch(
        ILogger logger,
        FileMutationJournal journal,
        PinnedDirectoryCreation.PinnedFileEntry target)
    {
        var observedFingerprints = string.Join(
            ",",
            target.GetObjectIdentityCandidates().Select(Fingerprint));
        logger.LogWarning(
            "Markerless registration target identity mismatch during content verification for operation {OperationIdPrefix}, action {Action}, journal stage {JournalState}; expected identity fingerprint {ExpectedIdentityFingerprint}, observed identity fingerprints {ObservedIdentityFingerprints}, expected content length {ExpectedLength}, source hash proof available {HasSourceHashProof}",
            journal.OperationId.ToString("N")[..8],
            journal.Action,
            journal.State,
            Fingerprint(journal.TargetPhysicalObjectIdentity),
            observedFingerprints,
            journal.SourceLength,
            !string.IsNullOrWhiteSpace(journal.SourceSha256));
    }

    internal static string Fingerprint(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return "unavailable";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 6));
    }
}
