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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Listenarr.Application.Audiobookshelf.Contracts;

namespace Listenarr.Infrastructure.Downloads.Processing
{
    /// <summary>
    /// Process the download processing jobs queued
    /// </summary>
    public partial class DownloadProcessingJobProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadProcessingJobProcessor> logger,
        IAppMetricsService metrics,
        IScanQueueService scanQueueService,
        ILibraryFilesystemReadiness filesystemReadiness) : BackgroundService, IDownloadImportProcessor
    {
        private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(10); // Check every 10 seconds

        public async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            await filesystemReadiness.WaitUntilReadyAsync(cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            var job = await downloadProcessingJobService.GetNextJobAsync();
            if (job == null)
            {
                return;
            }

            using var logScope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["JobId"] = job.Id,
                ["DownloadId"] = job.DownloadId,
                ["CorrelationId"] = job.GetOrCreateCorrelationId()
            });
            try
            {
                await ProcessJobAsync(job, cancellationToken);
            }
            catch (DownloadProcessingException exception)
            {
                logger.LogError($"Job {job.Id} failed: {exception.Message}");
                await downloadProcessingJobService.UpdateJobAsync(job.MarkAsFailed(exception.Message));
            }

            if (job.Status == ProcessingJobStatus.Failed)
            {
                // Unable to process import job and retries exceeded
                var download = await downloadRepository.GetByIdAsync(job.DownloadId);
                if (download == null)
                {
                    logger.LogError($"Download {job.DownloadId} disappeared after job {job.Id} failed");
                    return;
                }

                download.Blocked(
                    "Unable to import the download",
                    $"See the log of job {job.Id} for more information");
                if (!string.IsNullOrWhiteSpace(
                    download.GetMetadataString(Download.RecoveredSourceFilesMetadataKey)))
                {
                    download.SetMetadata("Recovery.State", "ImportFailed");
                    download.SetMetadata(
                        "Recovery.Reason",
                        "Import failed after the configured processing retries were exhausted.");
                }

                await downloadService.UpdateAsync(download);
            }
        }

        /// <summary>
        /// Make sure the job is consistent, recover all related files and 
        /// send them for processing to IDownloadImportService
        /// </summary>
        /// <param name="job"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="DownloadProcessingException"></exception>
        public async Task ProcessJobAsync(DownloadProcessingJob job, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Processing job {job.Id} for download {job.DownloadId}: {job.JobType}");

            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
            var download = await downloadRepository.GetByIdAsync(job.DownloadId);
            if (download == null)
            {
                throw new DownloadProcessingException($"The download {job.DownloadId} does not exist anymore");
            }

            if (download.Status == DownloadStatus.Moved)
            {
                job.AddLogEntry($"Download {download.Id} is already imported; completing stale import job without work");
                await downloadProcessingJobService.UpdateJobAsync(job.MarkAsCompleted());
                return;
            }

            if (!download.AwaitsImportation())
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} is not ready for importation. Current status: {download.Status}");
            }

            if (string.IsNullOrEmpty(download.DownloadPath))
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} has no path set");
            }

            if (download.AudiobookId == null)
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} has no audiobook related to it");
            }

            if (download.DownloadClientId == null)
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} has no download client");
            }

            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audiobook = await audiobookRepository.GetByIdAsync((int)download.AudiobookId);
            if (audiobook == null)
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id}'s audiobook {download.AudiobookId} cannot be retrieved");
            }

            var isDirectDownload = string.Equals(download.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase);
            DownloadClientConfiguration? client = null;
            if (!isDirectDownload)
            {
                var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                client = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
                if (client == null)
                {
                    throw new DownloadProcessingException($"Inconsistency: Download {download.Id}'s client {download.DownloadClientId} cannot be retrieved");
                }
            }

            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
            var correlationId = job.GetOrCreateCorrelationId();

            await downloadService.UpdateAsync(download.Importing());
            await downloadProcessingJobService.UpdateJobAsync(job.MarkAsProcessing());
            await RecordHistoryAsync(
                historyRepository,
                download,
                audiobook,
                HistoryEvents.ImportStarted,
                HistoryOutcome.Requested,
                correlationId,
                $"Import attempt {job.RetryCount + 1} started",
                new Dictionary<string, object> { ["JobId"] = job.Id, ["Attempt"] = job.RetryCount + 1 },
                cancellationToken);

            if (!job.HasCheckpoint("FilesImported"))
            {
                if (isDirectDownload &&
                    (string.IsNullOrEmpty(download.DownloadPath) || (!File.Exists(download.DownloadPath) && !Directory.Exists(download.DownloadPath))))
                {
                    metrics.Increment("processing.source_missing");
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, $"Direct-download source path not found at processing time: {download.DownloadPath}", cancellationToken);
                    return;
                }

                QueueItem queueItem;
                List<string> files;
                List<string> resolvedSourceFiles;
                try
                {
                    // External client DownloadPath may be stale or missing. Client-specific
                    // import resolvers own recovery from queue/history before the processor
                    // decides the source is unavailable.
                    var resolvedImportItem = await ResolveImportItemAsync(
                        scope.ServiceProvider,
                        download,
                        isDirectDownload,
                        cancellationToken);
                    queueItem = resolvedImportItem.Item;
                    resolvedSourceFiles = resolvedImportItem.SourceFiles;
                    job.AddLogEntry($"Resolved {resolvedSourceFiles.Count} file(s) for import");
                    files = [.. resolvedSourceFiles.Where(File.Exists)];
                    job.AddLogEntry($"{files.Count} file(s) remaining after checking which ones are effectively on disk");
                }
                catch (DownloadProcessingException exception)
                {
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, exception.Message, cancellationToken);
                    return;
                }

                if (files.Count == 0 || files.Count != resolvedSourceFiles.Count)
                {
                    var reason = files.Count == 0
                        ? "No importable files found"
                        : "Files reported by the download client and files on disk do not match";
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, reason, cancellationToken);
                    return;
                }

                List<ImportResult> results;
                try
                {
                    var downloadImportService = scope.ServiceProvider.GetRequiredService<IDownloadImportService>();
                    var importOptions = isDirectDownload && string.Equals(
                        download.GetMetadataString(DirectDownloadMetadataKeys.RequiresArchiveExtraction),
                        bool.TrueString,
                        StringComparison.OrdinalIgnoreCase)
                        ? new DownloadImportOptions(ForceArchiveExtraction: true)
                        : null;
                    results = await downloadImportService.ImportDownloadFilesAsync(
                        audiobook,
                        files,
                        cancellationToken,
                        importOptions);
                }
                catch (InvalidOperationException exception)
                {
                    await FailImportAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, exception.Message, cancellationToken);
                    return;
                }

                foreach (var result in results)
                {
                    if (!string.IsNullOrEmpty(result.Message)) job.AddLogEntry(result.Message);
                }

                var failedResults = results.Where(result => !result.Success).ToList();
                if (failedResults.Count > 0)
                {
                    await HandleFailedImportResultsAsync(
                        scope.ServiceProvider,
                        job,
                        downloadProcessingJobService,
                        historyRepository,
                        download,
                        audiobook,
                        correlationId,
                        failedResults,
                        cancellationToken);
                    return;
                }

                var outputProof = await CaptureImportedAudioOutputsAsync(
                    scope.ServiceProvider,
                    audiobook,
                    results,
                    cancellationToken);
                if (outputProof.Error != null)
                {
                    await FailImportAsync(
                        job,
                        downloadProcessingJobService,
                        historyRepository,
                        download,
                        audiobook,
                        correlationId,
                        outputProof.Error,
                        cancellationToken);
                    return;
                }

                if (job.TryGetJobDataString(
                        "PostImportRecovery.State",
                        out var recoveryState)
                    && string.Equals(recoveryState, "Pending", StringComparison.Ordinal))
                {
                    job.JobData["PostImportRecovery.State"] = "Recovered";
                    job.AddLogEntry(
                        "The targeted library scan registered the published files; import will continue through normal verification and finalization.");
                }

                foreach (var result in results)
                {
                    var outcome = string.IsNullOrWhiteSpace(result.SourcePath) || string.IsNullOrWhiteSpace(result.FinalPath)
                        ? HistoryOutcome.Skipped
                        : HistoryOutcome.Succeeded;
                    var eventType = outcome == HistoryOutcome.Skipped
                        ? HistoryEvents.FileSkipped
                        : result.Action == Listenarr.Domain.Audiobooks.Enumerations.FileAction.Move
                            ? HistoryEvents.FileMoved
                            : HistoryEvents.FileCopied;
                    await historyRepository.AddAsync(new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title,
                        SourceTitle = Path.GetFileName(result.FinalPath ?? result.SourcePath ?? download.Title),
                        DownloadId = download.Id.ToUpperInvariant(),
                        DownloadClientId = download.DownloadClientId,
                        EventType = eventType,
                        Outcome = outcome,
                        Source = "DownloadImport",
                        Message = result.Message ?? $"{result.Action} completed",
                        Timestamp = DateTime.UtcNow,
                        CorrelationId = correlationId,
                        Data = JsonSerializer.Serialize(new
                        {
                            JobId = job.Id,
                            result.Action,
                            result.RequestedAction,
                            result.EffectiveAction,
                            result.SourceDisposition,
                            result.WarningCode,
                            result.SourcePath,
                            result.FinalPath,
                            result.WasRegisteredToAudiobook
                        })
                    }, cancellationToken);
                }

                job.JobData[Download.SourceRetainedMetadataKey] = results.Any(result =>
                    result.SourceDisposition
                        == ImportSourceDisposition.Retained);
                job.JobData[ImportFinalizationService.ImportedAudioOutputsJobDataKey] =
                    JsonSerializer.Serialize(outputProof.Outputs);
                job.SetCheckpoint("FilesImported", results.Count);
                await downloadProcessingJobService.UpdateJobAsync(job);
            }

            if (!job.HasCheckpoint("ClientMarkedImported"))
            {
                if (isDirectDownload)
                {
                    // There is no external client history to mark for DDLs. The
                    // local staged file is already under Listenarr's ownership.
                    job.SetCheckpoint("ClientMarkedImported");
                    await downloadProcessingJobService.UpdateJobAsync(job);
                }
                else
                {
                    var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
                    if (!await downloadClientGateway.MarkItemAsImportedAsync(client!, download, cancellationToken))
                    {
                        await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                            correlationId, $"Unable to mark the item imported in client {client!.Id}", cancellationToken);
                        return;
                    }

                    job.SetCheckpoint("ClientMarkedImported");
                    await downloadProcessingJobService.UpdateJobAsync(job);
                }
            }

            if (!job.HasCheckpoint("ScanEnqueued"))
            {
                Guid scanJobId;
                try
                {
                    scanJobId = await scanQueueService.EnqueueScanAsync(
                        audiobook,
                        correlationId: correlationId,
                        downloadId: download.Id);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, $"Unable to enqueue the post-import library scan: {exception.Message}", cancellationToken);
                    return;
                }
                job.SetCheckpoint("ScanEnqueued", scanJobId.ToString());
                job.AddLogEntry($"Enqueued scan job {scanJobId} for audiobook {audiobook.Id}");
                await downloadProcessingJobService.UpdateJobAsync(job);
                await RecordHistoryAsync(
                    historyRepository,
                    download,
                    audiobook,
                    HistoryEvents.ScanQueued,
                    HistoryOutcome.Succeeded,
                    correlationId,
                    $"Library scan {scanJobId} queued",
                    new Dictionary<string, object> { ["JobId"] = job.Id, ["ScanJobId"] = scanJobId },
                    cancellationToken);
            }

            if (!job.HasCheckpoint("AudiobookshelfScanRequested"))
            {
                try
                {
                    var audiobookshelf = scope.ServiceProvider
                        .GetRequiredService<IAudiobookshelfIntegrationService>();
                    var requested = await audiobookshelf.TriggerScanForAudiobookAsync(
                        audiobook.Id,
                        cancellationToken);
                    job.SetCheckpoint(
                        "AudiobookshelfScanRequested",
                        requested ? "requested" : "not-mapped");
                }
                catch (HttpRequestException exception)
                {
                    logger.LogWarning(
                        exception,
                        "Audiobookshelf scan could not be requested for audiobook {AudiobookId}",
                        audiobook.Id);
                    job.SetCheckpoint("AudiobookshelfScanRequested", "request-failed");
                }
                catch (InvalidOperationException exception)
                {
                    logger.LogWarning(
                        exception,
                        "Audiobookshelf scan is not configured for audiobook {AudiobookId}",
                        audiobook.Id);
                    job.SetCheckpoint("AudiobookshelfScanRequested", "unavailable");
                }

                await downloadProcessingJobService.UpdateJobAsync(job);
            }

            var finalizationService = scope.ServiceProvider.GetRequiredService<IImportFinalizationService>();
            if (!string.IsNullOrWhiteSpace(job.QuickAddOperationId))
            {
                var quickAddOperations = scope.ServiceProvider
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
            try
            {
                await finalizationService.FinalizeAsync(
                    job.Id,
                    download.Id,
                    audiobook.Id,
                    audiobook.Title ?? download.Title,
                    client?.Id ?? download.DownloadClientId,
                    correlationId,
                    sourceRetained,
                    new Dictionary<string, object>
                    {
                        ["JobId"] = job.Id,
                        ["ScanJobId"] = job.TryGetJobDataString("ScanEnqueuedDetail", out var scanId) ? scanId : string.Empty
                    },
                    cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                    correlationId, $"Unable to commit import finalization: {exception.Message}", cancellationToken);
            }
        }

    }
}
