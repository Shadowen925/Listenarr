using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Downloads.Processing;

public partial class DownloadProcessingJobProcessor
{
    private static async Task HandleFailedImportResultsAsync(
        IServiceProvider services,
        DownloadProcessingJob job,
        IDownloadProcessingJobService jobService,
        IHistoryRepository historyRepository,
        Download download,
        Audiobook audiobook,
        string correlationId,
        IReadOnlyCollection<ImportResult> failedResults,
        CancellationToken cancellationToken)
    {
        var recoveryAttemptCount = GetRecoveryScanAttemptCount(job);
        const int maximumRecoveryScanAttempts = 2;
        if (recoveryAttemptCount < maximumRecoveryScanAttempts)
        {
            var recoveryService = services
                .GetRequiredService<PostImportScanRecoveryService>();
            var recovery = await recoveryService.TryEnqueueAsync(
                audiobook,
                download,
                failedResults,
                correlationId,
                cancellationToken);
            if (recovery.WasEnqueued)
            {
                job.JobData["PostImportRecovery.ScanAttemptCount"] =
                    recoveryAttemptCount + 1;
                job.JobData["PostImportRecovery.State"] = "Pending";
                job.JobData["PostImportRecovery.ScanJobId"] =
                    recovery.ScanJobId!.Value.ToString("D");
                job.AddLogEntry(
                    $"Queued targeted scan {recovery.ScanJobId} to recover the published audiobook files.");
                await jobService.UpdateJobAsync(job);
                await ScheduleRetryAsync(
                    job,
                    jobService,
                    historyRepository,
                    download,
                    audiobook,
                    correlationId,
                    "Published files passed recovery proof checks; waiting for the targeted library scan before retrying import.",
                    cancellationToken);
                return;
            }

            job.JobData["PostImportRecovery.State"] = "NotEligible";
            job.JobData["PostImportRecovery.Reason"] =
                recovery.Reason ?? "Recovery requirements were not met.";
            job.AddLogEntry(
                $"Automatic post-import scan recovery was not started: {recovery.Reason}");
        }
        else
        {
            job.JobData["PostImportRecovery.State"] = "AttemptsExhausted";
            job.JobData["PostImportRecovery.Reason"] =
                $"Automatic recovery stopped after {maximumRecoveryScanAttempts} targeted scan attempts.";
            job.AddLogEntry(
                $"Automatic post-import scan recovery stopped after {maximumRecoveryScanAttempts} targeted scan attempts.");
        }

        var reason = job.TryGetJobDataString(
            "PostImportRecovery.Reason",
            out var recoveryReason)
                ? $"Unable to import at least one file for the job. {recoveryReason}"
                : "Unable to import at least one file for the job (see the log entries)";
        await FailImportAsync(
            job,
            jobService,
            historyRepository,
            download,
            audiobook,
            correlationId,
            reason,
            cancellationToken,
            failedResults);
    }

    private static int GetRecoveryScanAttemptCount(DownloadProcessingJob job) =>
        job.TryGetJobDataString(
            "PostImportRecovery.ScanAttemptCount",
            out var attemptText)
        && int.TryParse(attemptText, out var attemptCount)
        && attemptCount >= 0
            ? attemptCount
            : 0;
}
