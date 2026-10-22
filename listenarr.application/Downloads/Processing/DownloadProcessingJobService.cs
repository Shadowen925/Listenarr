/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Processing
{
    /// <summary>
    /// Service for managing download post-processing queue with retry capabilities
    /// </summary>
    public class DownloadProcessingJobService(
        IDownloadProcessingJobRepository jobRepository,
        IQuickAddOperationRepository quickAddOperations,
        ILogger<DownloadProcessingJobService> logger,
        TimeProvider timeProvider) : IDownloadProcessingJobService
    {
        private static readonly ProcessingJobStatus[] TerminalCleanupStatuses =
            [ProcessingJobStatus.Completed, ProcessingJobStatus.Failed];

        public Task<string> EnqueueAsync(Download download) =>
            EnqueueCoreAsync(download, allowImportPending: false);

        private async Task<string> EnqueueCoreAsync(
            Download download,
            bool allowImportPending)
        {
            ArgumentNullException.ThrowIfNull(download);

            var existingActive = await jobRepository.GetActiveByDownloadIdAsync(download.Id);
            if (existingActive != null)
            {
                logger.LogInformation("Duplicate enqueue prevented - returning existing active job {JobId} for download {DownloadId}", existingActive.Id, download.Id);
                return existingActive.Id;
            }

            var previousJobs = await jobRepository.GetByDownloadIdAsync(download.Id);
            var previousJob = previousJobs
                .OrderByDescending(job => job.CreatedAt)
                .FirstOrDefault();
            if (previousJob != null)
            {
                logger.LogInformation(
                    "Duplicate completion ignored for download {DownloadId}; persisted processing job {JobId} is {Status}",
                    download.Id,
                    previousJob.Id,
                    previousJob.Status);
                return previousJob.Id;
            }

            if (download.Status != DownloadStatus.Completed
                && !(allowImportPending && download.Status == DownloadStatus.ImportPending))
            {
                throw new InvalidOperationException($"Trying to enqueue an import job for download {download.Id} with status {download.Status}: Status should be Completed");
            }

            var job = new DownloadProcessingJob
            {
                DownloadId = download.Id,
                QuickAddOperationId = download.QuickAddOperationId,
                JobType = ProcessingJobType.MoveOrCopyFile,
                SourcePath = download.DownloadPath,
                DownloadClientId = download.DownloadClientId,
                Priority = 5,
                Status = ProcessingJobStatus.Pending,
                JobData = new Dictionary<string, object>
                {
                    ["CorrelationId"] = download.Id.ToUpperInvariant()
                }
            };

            try
            {
                job = await jobRepository.AddAsync(job);
            }
            catch (UniqueConstraintViolationException)
            {
                existingActive = await jobRepository.GetActiveByDownloadIdAsync(download.Id);
                if (existingActive != null)
                {
                    logger.LogInformation(
                        "Concurrent duplicate enqueue prevented - returning existing active job {JobId} for download {DownloadId}",
                        existingActive.Id,
                        download.Id);
                    return existingActive.Id;
                }

                throw;
            }
            logger.LogInformation("Queued download {DownloadId} for post-processing: {JobId}", download.Id, job.Id);
            if (download.AudiobookId.HasValue)
            {
                await quickAddOperations.AdvanceAsync(
                    download.AudiobookId.Value,
                    QuickAddOperationState.ImportPending,
                    downloadId: download.Id,
                    downloadClientId: download.DownloadClientId,
                    processingJobId: job.Id);
            }
            return job.Id;
        }

        public async Task<string> RetryAsync(Download download)
        {
            ArgumentNullException.ThrowIfNull(download);

            var activeJob = await jobRepository.GetActiveByDownloadIdAsync(download.Id);
            if (activeJob != null)
            {
                return activeJob.Id;
            }

            var jobs = await jobRepository.GetByDownloadIdAsync(download.Id);
            var latestJob = jobs
                .OrderByDescending(job => job.CreatedAt)
                .FirstOrDefault();
            if (latestJob == null)
            {
                return await EnqueueCoreAsync(download, allowImportPending: true);
            }
            if (latestJob.Status != ProcessingJobStatus.Failed)
            {
                return latestJob.Id;
            }

            latestJob.Status = ProcessingJobStatus.Pending;
            latestJob.StartedAt = null;
            latestJob.CompletedAt = null;
            latestJob.NextRetryAt = null;
            latestJob.ErrorMessage = null;
            latestJob.JobData.Remove("NeedsAttention");
            latestJob.AddLogEntry("Explicitly requeued after user-requested import retry");
            await jobRepository.UpdateAsync(latestJob);

            if (!string.IsNullOrWhiteSpace(latestJob.QuickAddOperationId))
            {
                var operation = await quickAddOperations.GetByIdAsync(
                    latestJob.QuickAddOperationId);
                if (operation != null)
                {
                    await quickAddOperations.AdvanceAsync(
                        operation.AudiobookId,
                        QuickAddOperationState.ImportPending,
                        downloadId: download.Id,
                        downloadClientId: download.DownloadClientId,
                        processingJobId: latestJob.Id);
                }
            }

            return latestJob.Id;
        }

        public async Task<DownloadProcessingJob?> GetNextJobAsync()
        {
            return await jobRepository.TryClaimNextPendingAsync(
                timeProvider.GetUtcNow().UtcDateTime);
        }

        public async Task<List<DownloadProcessingJob>> GetRetryJobsAsync()
            => await jobRepository.GetDueRetryJobsAsync();

        public async Task UpdateJobAsync(DownloadProcessingJob job)
        {
            await jobRepository.UpdateAsync(job);
            if (string.IsNullOrWhiteSpace(job.QuickAddOperationId))
            {
                return;
            }

            var operation = await quickAddOperations.GetByIdAsync(job.QuickAddOperationId);
            if (operation == null)
            {
                return;
            }

            var state = job.Status switch
            {
                ProcessingJobStatus.Processing => QuickAddOperationState.Importing,
                ProcessingJobStatus.Failed => QuickAddOperationState.FailedNeedsAttention,
                _ => (QuickAddOperationState?)null
            };
            if (state.HasValue)
            {
                await quickAddOperations.AdvanceAsync(
                    operation.AudiobookId,
                    state.Value,
                    processingJobId: job.Id,
                    failureReason: job.ErrorMessage);
            }
        }

        public async Task<DownloadProcessingJob?> GetJobAsync(string jobId)
            => await jobRepository.GetByIdAsync(jobId);

        public async Task<List<DownloadProcessingJob>> GetJobsForDownloadAsync(string downloadId)
            => await jobRepository.GetByDownloadIdAsync(downloadId);

        public async Task<QueueStats> GetStatsAsync()
            => await jobRepository.GetStatsAsync();

        public async Task CleanupOldJobsAsync(int retentionDays = 7, CancellationToken cancellationToken = default)
        {
            // Retention policy belongs in the application layer so repositories remain thin
            // persistence adapters and cannot silently widen cleanup to active jobs later.
            var cutoffUtc = timeProvider
                .GetUtcNow()
                .UtcDateTime
                .AddDays(-retentionDays);

            var removed = await jobRepository.DeleteCompletedBeforeAsync(
                TerminalCleanupStatuses,
                cutoffUtc,
                cancellationToken);

            if (removed > 0)
            {
                logger.LogInformation(
                    "Cleaned up {Count} old processing jobs older than {Days} days",
                    removed,
                    retentionDays);
            }
        }

        public async Task<List<DownloadProcessingJob>> GetRecentActivityAsync(int count = 50)
            => await jobRepository.GetRecentAsync(count);

        /// <summary>
        /// Reset jobs that were stuck in Processing status from a previous session (e.g., after crash or restart).
        /// </summary>
        public async Task ResetStuckJobsAsync(CancellationToken cancellationToken = default)
        {
            var stuckJobs = await jobRepository.GetStuckProcessingJobsAsync(cancellationToken);
            if (stuckJobs.Count <= 0)
            {
                return;
            }

            logger.LogInformation("Found {Count} stuck jobs in Processing status, resetting to Pending", stuckJobs.Count);
            foreach (var job in stuckJobs)
            {
                await jobRepository.UpdateAsync(job.UnStuck("Reset from stuck Processing state after service restart"));
            }
        }
    }
}
