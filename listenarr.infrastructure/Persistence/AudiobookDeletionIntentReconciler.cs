using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed class AudiobookDeletionIntentReconciler(
    IAudiobookDeletionIntentStore intentStore,
    IAudiobookRepository audiobookRepository,
    IAudiobookDeletionCommitService deletionCommitService,
    IAudiobookFilesystemDeleteService filesystemDeleteService,
    ILogger<AudiobookDeletionIntentReconciler> logger,
    IConfiguration? configuration = null) : IAudiobookDeletionIntentReconciler
{
    /// <summary>
    /// Operator opt-in: comma/semicolon separated deletion-intent IDs that may be
    /// closed (without any filesystem mutation) when their audiobook row is gone.
    /// </summary>
    public const string AcknowledgedOrphanedIntentsConfigurationKey =
        "Listenarr:Recovery:AcknowledgedOrphanedDeletionIntents";

    private const string OrphanedReason =
        "The audiobook row disappeared before its durable filesystem cleanup completed.";

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var acknowledged = GetAcknowledgedIntentIds();
        var intents = await intentStore.GetActiveAsync(cancellationToken);
        foreach (var intent in intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (intent.State is AudiobookDeletionIntentState.Planned
                or AudiobookDeletionIntentState.NeedsAttention
                && acknowledged.Contains(intent.Id)
                && await audiobookRepository.GetByIdSnapshotAsync(
                    intent.AudiobookId,
                    cancellationToken) == null
                && await intentStore.CompleteOrphanedWithoutFilesystemCleanupAsync(
                    intent.Id,
                    $"Operator-acknowledged orphaned deletion intent closed without filesystem cleanup; audiobook {intent.AudiobookId} row was absent.",
                    CancellationToken.None))
            {
                logger.LogWarning(
                    "Closed operator-acknowledged orphaned deletion intent {IntentId} for missing audiobook {AudiobookId}; no filesystem changes were made",
                    intent.Id,
                    intent.AudiobookId);
                continue;
            }

            if (intent.State == AudiobookDeletionIntentState.NeedsAttention)
            {
                throw new InvalidOperationException(
                    $"Audiobook deletion intent {intent.Id} (audiobook {intent.AudiobookId}) requires operator attention: {intent.Error}");
            }

            if (intent.State == AudiobookDeletionIntentState.Planned)
            {
                var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
                    intent.AudiobookId,
                    cancellationToken);
                if (audiobook == null)
                {
                    var reason =
                        $"{OrphanedReason} Intent {intent.Id} (audiobook {intent.AudiobookId}) is retained; no files were changed. To close it, set {AcknowledgedOrphanedIntentsConfigurationKey} to the intent ID.";
                    await intentStore.MarkNeedsAttentionAsync(
                        intent.Id,
                        reason,
                        CancellationToken.None);
                    throw new InvalidOperationException(reason);
                }

                AudiobookFilesystemDeleteResult result;
                try
                {
                    result = await filesystemDeleteService.DeleteAsync(
                        audiobook,
                        intent.DeleteFolder,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    IsTransientRecoveryFilesystemException(exception))
                {
                    await intentStore.RecordErrorAsync(
                        intent.Id,
                        "Filesystem cleanup is temporarily unavailable during durable audiobook deletion recovery.",
                        CancellationToken.None);
                    logger.LogWarning(
                        exception,
                        "Audiobook deletion intent {IntentId} remains pending because filesystem cleanup is temporarily unavailable",
                        intent.Id);
                    continue;
                }
                catch (Exception exception) when (exception is not (
                    OutOfMemoryException or StackOverflowException))
                {
                    await intentStore.RecordErrorAsync(
                        intent.Id,
                        "Filesystem cleanup failed during durable audiobook deletion recovery.",
                        CancellationToken.None);
                    throw new InvalidOperationException(
                        "Durable audiobook deletion recovery could not complete filesystem cleanup safely.",
                        exception);
                }

                foreach (var warning in result.Warnings)
                {
                    logger.LogWarning(
                        "Recovered audiobook deletion {IntentId} completed with warning: {Warning}",
                        intent.Id,
                        warning);
                }
                if (!result.TrackedFileCleanupComplete)
                {
                    await intentStore.RecordErrorAsync(
                        intent.Id,
                        "One or more tracked audiobook file generations remain pending after filesystem cleanup recovery.",
                        CancellationToken.None);
                    logger.LogWarning(
                        "Audiobook deletion intent {IntentId} remains pending because tracked-file cleanup is not yet complete",
                        intent.Id);
                    continue;
                }
                await intentStore.MarkFilesystemCleanupCompletedAsync(
                    intent.Id,
                    CancellationToken.None);
            }

            var commit = await deletionCommitService.DeleteAsync(
                intent.AudiobookId,
                includeFiles: false,
                CancellationToken.None);
            if (commit.Outcome == AudiobookDeletionCommitOutcome.Failed)
            {
                throw new InvalidOperationException(
                    "Durable audiobook deletion recovery could not commit the database deletion.");
            }

            await intentStore.MarkCompletedAsync(
                intent.Id,
                CancellationToken.None);
            logger.LogInformation(
                "Recovered durable audiobook deletion {IntentId} for audiobook {AudiobookId}",
                intent.Id,
                intent.AudiobookId);
        }
    }

    private HashSet<Guid> GetAcknowledgedIntentIds()
    {
        var ids = new HashSet<Guid>();
        var raw = configuration?[AcknowledgedOrphanedIntentsConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ids;
        }

        foreach (var token in raw.Split(
            [',', ';', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(token, out var id) && id != Guid.Empty)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool IsTransientRecoveryFilesystemException(Exception exception)
    {
        if (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        if (exception is System.ComponentModel.Win32Exception native)
        {
            return native.NativeErrorCode is 5 or 13 or 16 or 30 or 32 or 33;
        }

        return exception is InvalidOperationException { InnerException: not null }
            && IsTransientRecoveryFilesystemException(exception.InnerException);
    }
}
