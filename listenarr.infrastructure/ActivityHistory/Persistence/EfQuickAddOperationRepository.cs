using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.ActivityHistory.Persistence;

public sealed class EfQuickAddOperationRepository(ListenArrDbContext db)
    : IQuickAddOperationRepository
{
    public Task<QuickAddOperation?> GetByIdAsync(
        string operationId,
        CancellationToken cancellationToken = default) =>
        db.QuickAddOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.Id == operationId, cancellationToken);

    public Task<QuickAddOperation?> GetByRequestKeyAsync(
        string requestKey,
        CancellationToken cancellationToken = default) =>
        db.QuickAddOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation => operation.RequestKey == requestKey
                    && operation.State != QuickAddOperationState.Imported,
                cancellationToken);

    public Task<QuickAddOperation?> GetByAudiobookIdAsync(
        int audiobookId,
        CancellationToken cancellationToken = default) =>
        db.QuickAddOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.AudiobookId == audiobookId, cancellationToken);

    public async Task<IReadOnlyList<QuickAddOperation>> GetUnresolvedAsync(
        CancellationToken cancellationToken = default) =>
        await db.QuickAddOperations
            .AsNoTracking()
            .Where(operation => operation.State != QuickAddOperationState.Imported)
            .OrderByDescending(operation => operation.UpdatedAt)
            .ToListAsync(cancellationToken);

    public async Task<bool> AdvanceAsync(
        int audiobookId,
        QuickAddOperationState state,
        string? selectedRelease = null,
        string? downloadClientId = null,
        string? downloadId = null,
        string? processingJobId = null,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        var targetOrder = QuickAddOperation.StateOrder(state);
        if (!db.Database.IsRelational())
        {
            var operation = await db.QuickAddOperations
                .SingleOrDefaultAsync(candidate => candidate.AudiobookId == audiobookId, cancellationToken);
            if (operation == null
                || operation.State is QuickAddOperationState.Imported
                || (operation.State == QuickAddOperationState.NoMatchingReleases
                    && state != QuickAddOperationState.Imported)
                || operation.State == QuickAddOperationState.FailedNeedsAttention
                || QuickAddOperation.StateOrder(operation.State) > targetOrder
                || (state == QuickAddOperationState.FailedNeedsAttention
                    && processingJobId == null
                    && QuickAddOperation.StateOrder(operation.State)
                        >= QuickAddOperation.StateOrder(QuickAddOperationState.ImportPending)))
            {
                return false;
            }

            operation.State = state;
            operation.UpdatedAt = DateTime.UtcNow;
            operation.SelectedRelease = selectedRelease ?? operation.SelectedRelease;
            operation.DownloadClientId = downloadClientId ?? operation.DownloadClientId;
            operation.DownloadId = downloadId ?? operation.DownloadId;
            operation.ProcessingJobId = processingJobId ?? operation.ProcessingJobId;
            operation.FailureReason = failureReason ?? operation.FailureReason;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var activeStates = Enum.GetValues<QuickAddOperationState>()
            .Where(candidate =>
                candidate != QuickAddOperationState.Imported
                && (candidate != QuickAddOperationState.NoMatchingReleases
                    || state == QuickAddOperationState.Imported)
                && candidate != QuickAddOperationState.FailedNeedsAttention
                && QuickAddOperation.StateOrder(candidate) <= targetOrder)
            .ToArray();
        if (state == QuickAddOperationState.FailedNeedsAttention
            && processingJobId == null)
        {
            activeStates = activeStates
                .Where(candidate =>
                    QuickAddOperation.StateOrder(candidate)
                        < QuickAddOperation.StateOrder(QuickAddOperationState.ImportPending))
                .ToArray();
        }

        var updatedAt = DateTime.UtcNow;
        var affected = await db.QuickAddOperations
            .Where(operation =>
                operation.AudiobookId == audiobookId
                && activeStates.Contains(operation.State))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.State, state)
                .SetProperty(operation => operation.UpdatedAt, updatedAt)
                .SetProperty(operation => operation.SelectedRelease, operation =>
                    selectedRelease ?? operation.SelectedRelease)
                .SetProperty(operation => operation.DownloadClientId, operation =>
                    downloadClientId ?? operation.DownloadClientId)
                .SetProperty(operation => operation.DownloadId, operation =>
                    downloadId ?? operation.DownloadId)
                .SetProperty(operation => operation.ProcessingJobId, operation =>
                    processingJobId ?? operation.ProcessingJobId)
                .SetProperty(operation => operation.FailureReason, operation =>
                    failureReason ?? operation.FailureReason), cancellationToken);
        return affected > 0;
    }

    public async Task<bool> RetryAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await db.QuickAddOperations
            .SingleOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken);
        if (operation == null
            || operation.State is not (
                QuickAddOperationState.NoMatchingReleases
                or QuickAddOperationState.FailedNeedsAttention))
        {
            return false;
        }

        operation.Attempt++;
        operation.FailureReason = null;
        operation.SelectedRelease = null;
        operation.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(operation.ProcessingJobId))
        {
            var job = await db.DownloadProcessingJobs
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == operation.ProcessingJobId,
                    cancellationToken);
            var download = operation.DownloadId == null
                ? null
                : await db.Downloads.SingleOrDefaultAsync(
                    candidate => candidate.Id == operation.DownloadId,
                    cancellationToken);
            if (job?.Status == ProcessingJobStatus.Failed && download != null)
            {
                job.RetryCount = 0;
                job.ErrorMessage = null;
                job.CompletedAt = null;
                job.NextRetryAt = null;
                job.UnStuck("Manually retried from Activity.");
                job.ActiveDeduplicationKey = job.DownloadId.Trim().ToUpperInvariant();
                download.Unblock();
                operation.State = QuickAddOperationState.ImportPending;
                operation.ProcessingJobId = job.Id;
            }
            else
            {
                operation.State = QuickAddOperationState.SearchPending;
                operation.DownloadId = null;
                operation.DownloadClientId = null;
                operation.ProcessingJobId = null;
                var audiobook = await db.Audiobooks
                    .SingleOrDefaultAsync(candidate => candidate.Id == operation.AudiobookId, cancellationToken);
                if (audiobook != null)
                {
                    audiobook.LastSearchTime = null;
                }
            }
        }
        else
        {
            operation.State = QuickAddOperationState.SearchPending;
            operation.DownloadId = null;
            operation.DownloadClientId = null;
            var audiobook = await db.Audiobooks
                .SingleOrDefaultAsync(candidate => candidate.Id == operation.AudiobookId, cancellationToken);
            if (audiobook != null)
            {
                audiobook.LastSearchTime = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task LinkDiagnosticAsync(
        string operationId,
        int diagnosticId,
        CancellationToken cancellationToken = default)
    {
        var updatedAt = DateTime.UtcNow;
        await db.QuickAddOperations
            .Where(operation =>
                operation.Id == operationId
                && operation.State != QuickAddOperationState.Imported)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.DiagnosticId, diagnosticId)
                .SetProperty(operation => operation.UpdatedAt, updatedAt), cancellationToken);
    }
}
