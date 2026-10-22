/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    public partial class MovedDownloadCleanupProcessor
    {
        private enum ImportProofKind
        {
            None,
            CompletedProcessingJob,
            LastImportedAt,
            ImportedHistory,
            LegacyDownloadHistory,
            LegacyMovedState
        }

        private sealed record ImportProof(
            ImportProofKind Kind,
            string CorrelationId,
            string? ProcessingJobId,
            DateTime? ProvenAt,
            bool? SourceRetained = null)
        {
            public bool AllowsDestructiveCleanup =>
                Kind is not ImportProofKind.LegacyMovedState
                && SourceRetained is false;
        }

        private async Task RecordConfigurationIssueAsync(
            Download download,
            IDownloadRepository downloadRepository,
            string issueCode,
            string diagnostic)
        {
            var wasNewIssue = !string.Equals(
                download.GetMetadataString(CleanupDiagnosticKey),
                issueCode,
                StringComparison.Ordinal);
            download.SetMetadata(CleanupDiagnosticKey, issueCode);
            download.SetMetadata(CleanupLastErrorKey, diagnostic);
            ScheduleNextRetry(download, DateTime.UtcNow);
            await downloadRepository.UpdateAsync(download);

            if (wasNewIssue)
            {
                logger.LogWarning(
                    "Deferred cleanup for download {DownloadId} is blocked: {Diagnostic}",
                    download.Id,
                    diagnostic);
            }
        }

        private async Task RecordCleanupFailureAsync(
            Download download,
            IDownloadRepository downloadRepository,
            IHistoryRepository historyRepository,
            ImportProof proof,
            string removalPolicy,
            bool deleteFiles,
            string diagnostic,
            string eventKey,
            HistoryOutcome outcome,
            string message,
            CancellationToken cancellationToken)
        {
            var shouldRecordHistory = !string.Equals(
                download.GetMetadataString(CleanupLastFailureEventKey),
                eventKey,
                StringComparison.Ordinal);
            download.SetMetadata(CleanupDiagnosticKey, "client-removal-failed");
            download.SetMetadata(CleanupLastErrorKey, diagnostic);
            download.SetMetadata(CleanupLastFailureEventKey, eventKey);
            ScheduleNextRetry(download, DateTime.UtcNow);
            await downloadRepository.UpdateAsync(download);

            if (!shouldRecordHistory)
            {
                return;
            }

            if (outcome == HistoryOutcome.Failed)
            {
                logger.LogWarning(
                    "Deferred cleanup for download {DownloadId} remains blocked: {Diagnostic}",
                    download.Id,
                    diagnostic);
            }
            else
            {
                logger.LogInformation(
                    "Deferred cleanup retry scheduled for download {DownloadId}: {Diagnostic}",
                    download.Id,
                    diagnostic);
            }

            var details = BuildCleanupDetails(proof, removalPolicy, deleteFiles);
            details["Diagnostic"] = diagnostic;
            details["AttemptCount"] = ReadCleanupAttemptCount(download);
            details["NextAttemptAt"] = download.GetMetadataString(CleanupNextAttemptAtKey) ?? string.Empty;
            details["OperationalRecordRemoved"] = false;
            await AddCleanupHistoryAsync(
                historyRepository,
                download,
                HistoryEvents.CleanupFailed,
                outcome,
                proof.CorrelationId,
                message,
                details,
                cancellationToken);
        }

        private static bool IsCleanupRetryDue(Download download, DateTime now)
        {
            var nextAttempt = download.GetMetadataString(CleanupNextAttemptAtKey);
            return !DateTimeOffset.TryParse(
                    nextAttempt,
                    out var parsedNextAttempt)
                || parsedNextAttempt.UtcDateTime <= now;
        }

        private static void ScheduleNextRetry(Download download, DateTime now)
        {
            var attemptCount = ReadCleanupAttemptCount(download) + 1;
            download.SetMetadata(CleanupAttemptCountKey, attemptCount);
            var exponent = Math.Min(attemptCount - 1, 10);
            var delay = TimeSpan.FromTicks(
                Math.Min(
                    CleanupRetryBaseDelay.Ticks * (1L << exponent),
                    CleanupRetryMaximumDelay.Ticks));
            download.SetMetadata(CleanupNextAttemptAtKey, now.Add(delay).ToString("O"));
        }

        private static int ReadCleanupAttemptCount(Download download)
        {
            return int.TryParse(download.GetMetadataString(CleanupAttemptCountKey), out var attempts)
                ? Math.Max(0, attempts)
                : 0;
        }

        private static string DescribeRemovalFailure(Exception exception)
        {
            return exception switch
            {
                UnauthorizedAccessException =>
                    "The download client rejected authentication. Verify the saved credentials and client permissions.",
                HttpRequestException =>
                    "The download client could not be reached. Verify its host, port, network access, and service health.",
                InvalidDataException =>
                    "The download client returned an invalid response. Check client compatibility and its logs.",
                InvalidOperationException =>
                    "The download client configuration or protocol is invalid. Verify the client type, URL, and daemon connection.",
                _ =>
                    "The download client removal request failed. Check client availability, credentials, and application logs."
            };
        }

        private static bool IsConfigurationFailure(string? diagnostic) =>
            diagnostic?.StartsWith("The download client rejected authentication", StringComparison.Ordinal) == true
            || diagnostic?.StartsWith("The download client configuration or protocol is invalid", StringComparison.Ordinal) == true;

        private static async Task<ImportProof> ResolveImportProofAsync(
            Download download,
            IDownloadProcessingJobRepository processingJobRepository,
            IHistoryRepository historyRepository,
            IDownloadHistoryRepository downloadHistoryRepository,
            CancellationToken cancellationToken)
        {
            var persistedSourceRetained = bool.TryParse(
                download.GetMetadataString(Download.SourceRetainedMetadataKey),
                out var parsedPersistedSourceRetained)
                    ? parsedPersistedSourceRetained
                    : (bool?)null;
            var completedJob = (await processingJobRepository.GetByDownloadIdAsync(download.Id))
                .Where(job => job.Status == ProcessingJobStatus.Completed)
                .OrderByDescending(job => job.CompletedAt ?? job.CreatedAt)
                .FirstOrDefault();
            if (completedJob != null)
            {
                return new ImportProof(
                    ImportProofKind.CompletedProcessingJob,
                    completedJob.GetOrCreateCorrelationId(),
                    completedJob.Id,
                    completedJob.CompletedAt,
                    completedJob.TryGetJobDataString(
                            Download.SourceRetainedMetadataKey,
                            out var retainedValue)
                        && bool.TryParse(retainedValue, out var sourceRetained)
                            ? sourceRetained
                            : persistedSourceRetained);
            }

            var importedHistory = await historyRepository.GetSucceededImportedByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (importedHistory != null)
            {
                return new ImportProof(
                    ImportProofKind.ImportedHistory,
                    importedHistory.CorrelationId ?? download.Id.ToUpperInvariant(),
                    null,
                    importedHistory.Timestamp,
                    persistedSourceRetained ?? ReadSourceRetained(importedHistory.Data));
            }

            if (download.LastImportedAt.HasValue)
            {
                return new ImportProof(
                    ImportProofKind.LastImportedAt,
                    download.Id.ToUpperInvariant(),
                    null,
                    download.LastImportedAt.Value,
                    persistedSourceRetained);
            }

            var legacyDownloadHistory = await downloadHistoryRepository.GetImportedByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (legacyDownloadHistory != null)
            {
                return new ImportProof(
                    ImportProofKind.LegacyDownloadHistory,
                    download.Id.ToUpperInvariant(),
                    null,
                    legacyDownloadHistory.ImportedAt ?? legacyDownloadHistory.EventDate);
            }

            var oldestHistoryAt = await historyRepository.GetOldestTimestampByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (oldestHistoryAt.HasValue && DateTime.UtcNow - oldestHistoryAt.Value > LegacyMovedProofGracePeriod)
            {
                // Older builds used Moved as the only durable import marker. This is enough
                // to clean stale client/DB state, but not enough to delete external files.
                return new ImportProof(
                    ImportProofKind.LegacyMovedState,
                    download.Id.ToUpperInvariant(),
                    null,
                    oldestHistoryAt.Value);
            }

            return new ImportProof(
                ImportProofKind.None,
                download.Id.ToUpperInvariant(),
                null,
                null);
        }

        private static Dictionary<string, object> BuildCleanupDetails(
            ImportProof proof,
            string removalPolicy,
            bool deleteFiles)
        {
            var details = new Dictionary<string, object>
            {
                ["ImportProof"] = proof.Kind.ToString(),
                ["RemovalPolicy"] = removalPolicy,
                ["DeleteFiles"] = deleteFiles,
                ["SourceRetentionKnown"] = proof.SourceRetained.HasValue
            };

            if (proof.SourceRetained.HasValue)
            {
                details[Download.SourceRetainedMetadataKey] = proof.SourceRetained.Value;
            }

            if (!string.IsNullOrWhiteSpace(proof.ProcessingJobId))
            {
                details["ProcessingJobId"] = proof.ProcessingJobId;
            }

            if (proof.ProvenAt.HasValue)
            {
                details["ImportProofAt"] = proof.ProvenAt.Value;
            }

            return details;
        }

        private static bool? ReadSourceRetained(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return null;

            try
            {
                using var document = JsonDocument.Parse(data);
                if (!document.RootElement.TryGetProperty(
                        Download.SourceRetainedMetadataKey,
                        out var value))
                {
                    return null;
                }

                return value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                    _ => null
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static Task AddCleanupHistoryAsync(
            IHistoryRepository historyRepository,
            Download download,
            string eventType,
            HistoryOutcome outcome,
            string correlationId,
            string message,
            Dictionary<string, object> details,
            CancellationToken ct) =>
            historyRepository.AddAsync(new History
            {
                AudiobookId = download.AudiobookId,
                AudiobookTitle = download.Title,
                SourceTitle = download.Title,
                DownloadId = download.Id.ToUpperInvariant(),
                DownloadClientId = download.DownloadClientId,
                EventType = eventType,
                Outcome = outcome,
                Source = "DownloadCleanup",
                Message = message,
                Error = outcome == HistoryOutcome.Failed ? message : null,
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId,
                Data = JsonSerializer.Serialize(details)
            }, ct);
    }
}
