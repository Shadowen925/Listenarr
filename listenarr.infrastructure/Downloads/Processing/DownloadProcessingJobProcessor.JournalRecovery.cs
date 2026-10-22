using Listenarr.Application.Common.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Downloads.Processing;

public partial class DownloadProcessingJobProcessor
{
    private async Task<bool> ReconcileRegistrationPublicationsBeforeFinalizationAsync(
        IServiceProvider services,
        DownloadProcessingJob job,
        IDownloadProcessingJobService jobService,
        IHistoryRepository historyRepository,
        Download download,
        Audiobook audiobook,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var recoveryService = services
            .GetRequiredService<IFileRegistrationRecoveryService>();
        try
        {
            await recoveryService.ReconcileAudiobookWithReceiptsAsync(
                audiobook.Id,
                Array.Empty<string>(),
                cancellationToken);
            return true;
        }
        catch (ApplicationConflictException exception) when (
            exception.Code == "registration_recovery_pending")
        {
            await ScheduleRetryAsync(
                job,
                jobService,
                historyRepository,
                download,
                audiobook,
                correlationId,
                exception.SafeDetail,
                cancellationToken);
            return false;
        }
        catch (ApplicationConflictException exception) when (
            exception.Code == "registration_recovery_repair_required")
        {
            await FailImportNeedsAttentionAsync(
                job,
                jobService,
                historyRepository,
                download,
                audiobook,
                correlationId,
                exception.SafeDetail,
                cancellationToken);
            return false;
        }
    }

    private static async Task FinalizeImportAsync(
        IServiceProvider services,
        DownloadProcessingJob job,
        IDownloadProcessingJobService jobService,
        IHistoryRepository historyRepository,
        Download download,
        Audiobook audiobook,
        string downloadClientId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(job.QuickAddOperationId))
        {
            var quickAddOperations = services
                .GetRequiredService<IQuickAddOperationRepository>();
            await quickAddOperations.AdvanceAsync(
                audiobook.Id,
                QuickAddOperationState.Verifying,
                downloadId: download.Id,
                downloadClientId: download.DownloadClientId,
                processingJobId: job.Id,
                cancellationToken: cancellationToken);
        }

        bool? sourceRetained = job.TryGetJobDataString(
                Download.SourceRetainedMetadataKey,
                out var sourceRetainedValue)
            && bool.TryParse(sourceRetainedValue, out var parsedSourceRetained)
                ? parsedSourceRetained
                : null;
        var finalizationService = services
            .GetRequiredService<IImportFinalizationService>();
        try
        {
            await finalizationService.FinalizeAsync(
                job.Id,
                download.Id,
                audiobook.Id,
                audiobook.Title ?? download.Title,
                downloadClientId,
                correlationId,
                sourceRetained,
                new Dictionary<string, object>
                {
                    ["JobId"] = job.Id,
                    ["ScanJobId"] = job.TryGetJobDataString(
                        "ScanEnqueuedDetail",
                        out var scanId)
                        ? scanId
                        : string.Empty
                },
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await ScheduleRetryAsync(
                job,
                jobService,
                historyRepository,
                download,
                audiobook,
                correlationId,
                $"Unable to commit import finalization: {exception.Message}",
                cancellationToken);
        }
        catch (ApplicationConflictException exception) when (
            exception.Code == "import_mutation_journal_unresolved")
        {
            await FailImportNeedsAttentionAsync(
                job,
                jobService,
                historyRepository,
                download,
                audiobook,
                correlationId,
                $"Unable to commit import finalization: {exception.SafeDetail}",
                cancellationToken);
        }
    }

    private static async Task FailImportNeedsAttentionAsync(
        DownloadProcessingJob job,
        IDownloadProcessingJobService jobService,
        IHistoryRepository historyRepository,
        Download download,
        Audiobook audiobook,
        string correlationId,
        string reason,
        CancellationToken cancellationToken)
    {
        job.SetCheckpoint("NeedsAttention");
        job.JobData["NeedsAttentionReason"] = reason;
        await FailImportAsync(
            job,
            jobService,
            historyRepository,
            download,
            audiobook,
            correlationId,
            reason,
            cancellationToken);
    }
}
