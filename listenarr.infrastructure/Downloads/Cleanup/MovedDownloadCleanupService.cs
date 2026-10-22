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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    public partial class MovedDownloadCleanupProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<MovedDownloadCleanupProcessor> logger) : IMovedDownloadCleanupProcessor
    {
        private static readonly TimeSpan ClientRemovalGracePeriod = TimeSpan.FromHours(2);
        private static readonly TimeSpan FailedCleanupWarningGracePeriod = TimeSpan.FromHours(24);
        private static readonly TimeSpan LegacyMovedProofGracePeriod = TimeSpan.FromDays(7);
        private static readonly TimeSpan CleanupRetryBaseDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan CleanupRetryMaximumDelay = TimeSpan.FromHours(1);
        private const string CleanupAttemptCountKey = "MovedCleanupAttemptCount";
        private const string CleanupNextAttemptAtKey = "MovedCleanupNextAttemptAt";
        private const string CleanupLastErrorKey = "MovedCleanupLastError";
        private const string CleanupLastFailureEventKey = "MovedCleanupLastFailureEvent";
        private const string CleanupDiagnosticKey = "MovedCleanupDiagnostic";
        private const string CleanupRequestedAtKey = "MovedCleanupRequestedAt";

        /// <summary>
        /// Processes deferred removals for downloads that have been imported (Status == Moved)
        /// but couldn't be removed from the client because the torrent hadn't reached its seed limit.
        /// Checks metadata CanBeRemoved flag which is updated by DownloadMonitorService on each poll.
        /// </summary>
        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var processingJobRepository = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobRepository>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
            var downloadHistoryRepository = scope.ServiceProvider.GetRequiredService<IDownloadHistoryRepository>();

            var movedDownloads = (await downloadRepository.GetActiveAsync())
                .Where(d => d.Status == DownloadStatus.Moved)
                .ToList();

            if (movedDownloads.Count == 0) return;

            // Pre-load enabled clients once so torrent cross-client cleanup does not reload
            // configuration for every moved download in a cleanup cycle.
            List<DownloadClientConfiguration> allEnabledClients;
            try
            {
                var allClients = await configurationService.GetDownloadClientConfigurationsAsync();
                allEnabledClients = allClients.Where(c => c.IsEnabled).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to load client configurations for deferred removal");
                return;
            }

            foreach (var download in movedDownloads)
            {
                try
                {
                    if (download.DownloadClientId == Listenarr.Application.Downloads.Submission.DirectDownloadMetadataKeys.ClientId)
                    {
                        continue;
                    }

                    if (!IsCleanupRetryDue(download, DateTime.UtcNow))
                    {
                        continue;
                    }

                    var clientConfig = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
                    if (clientConfig == null)
                    {
                        await RecordConfigurationIssueAsync(
                            download,
                            downloadRepository,
                            "client-missing",
                            $"Download client '{download.DownloadClientId}' is not configured. Restore or reassign the client before deferred cleanup can continue.");
                        continue;
                    }

                    if (!clientConfig.IsEnabled)
                    {
                        logger.LogDebug("Deferred removal: Skipping download {DownloadId} — client {ClientName} is disabled",
                            download.Id, clientConfig.Name);
                        continue;
                    }

                    var removalPolicy = clientConfig.RemoveCompletedDownloads;
                    if (string.IsNullOrEmpty(removalPolicy) || removalPolicy == "none")
                    {
                        // Policy none is an intentional retention choice, so do not resolve import
                        // proof, infer removability, or write cleanup history for these records.
                        logger.LogDebug("Deferred removal: Retaining imported download {DownloadId}; client action is none", download.Id);
                        continue;
                    }

                    if (removalPolicy is not ("remove" or "remove_and_delete"))
                    {
                        await RecordConfigurationIssueAsync(
                            download,
                            downloadRepository,
                            "invalid-removal-policy",
                            $"Download client '{clientConfig.Name}' has unsupported cleanup policy '{removalPolicy}'. Choose 'none', 'remove', or 'remove_and_delete'.");
                        continue;
                    }

                    var proof = await ResolveImportProofAsync(
                        download,
                        processingJobRepository,
                        historyRepository,
                        downloadHistoryRepository,
                        cancellationToken);
                    if (proof.Kind == ImportProofKind.None)
                    {
                        await RecordConfigurationIssueAsync(
                            download,
                            downloadRepository,
                            "import-proof-missing",
                            "The download is marked Moved but has no durable import proof. Preserve the client item and investigate the import history before cleanup.");
                        continue;
                    }

                    var hasCanBeRemoved = false;
                    var canBeRemoved = false;
                    if (download.Metadata != null && download.Metadata.TryGetValue("CanBeRemoved", out var canRemoveObj))
                    {
                        hasCanBeRemoved = true;
                        canBeRemoved = canRemoveObj is bool b
                            ? b
                            : canRemoveObj is JsonElement je
                                ? je.GetBoolean()
                                : bool.TryParse(canRemoveObj?.ToString(), out var parsed) && parsed;
                    }

                    // Only infer removability when the client never reported CanBeRemoved. A stored
                    // false value means the client explicitly says cleanup is not ready yet.
                    var timeSinceImportProof = proof.ProvenAt.HasValue
                        ? DateTime.UtcNow - proof.ProvenAt.Value
                        : (TimeSpan?)null;
                    if (!canBeRemoved && !hasCanBeRemoved &&
                        timeSinceImportProof.HasValue &&
                        timeSinceImportProof.Value > ClientRemovalGracePeriod)
                    {
                        logger.LogDebug(
                            "Deferred removal: Download {DownloadId} CanBeRemoved not set after {Hours:F1}h — " +
                            "treating as removable (possible client ID mismatch)", download.Id, timeSinceImportProof.Value.TotalHours);
                        canBeRemoved = true;
                    }

                    if (!canBeRemoved)
                    {
                        logger.LogDebug("Deferred removal: Download {DownloadId} still not removable", download.Id);
                        continue;
                    }

                    var deleteFiles = removalPolicy == "remove_and_delete";
                    if (deleteFiles && !proof.AllowsDestructiveCleanup)
                    {
                        var reason = proof.Kind == ImportProofKind.LegacyMovedState
                            ? "only legacy Moved-state import proof is available"
                            : proof.SourceRetained is true
                                ? "the import retained its source"
                                : "the source-retention disposition is unavailable";
                        logger.LogWarning(
                            "Deferred removal: Download {DownloadId} cannot be deleted because {Reason}; " +
                            "remove_and_delete was downgraded to remove",
                            download.Id,
                            reason);
                        deleteFiles = false;
                    }

                    if (proof.Kind == ImportProofKind.LegacyMovedState)
                    {
                        logger.LogInformation(
                            "Deferred removal: Attempting non-destructive legacy cleanup for download {DownloadId}",
                            download.Id);
                    }

                    string? torrentHash = null;
                    torrentHash = download.GetMetadataString("TorrentHash");
                    var clientId = download.GetExternalId();
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        await RecordConfigurationIssueAsync(
                            download,
                            downloadRepository,
                            "external-id-missing",
                            $"No external item ID is stored for download '{download.Id}'. The local Listenarr ID is not a valid client removal ID; restore the client ID or remove the stale record manually.");
                        continue;
                    }

                    var removed = false;
                    if (download.GetMetadataString(CleanupRequestedAtKey) == null)
                    {
                        download.SetMetadata(CleanupRequestedAtKey, DateTime.UtcNow.ToString("O"));
                        await downloadRepository.UpdateAsync(download);
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupRequested,
                            HistoryOutcome.Requested,
                            proof.CorrelationId,
                            $"Client cleanup requested ({removalPolicy})",
                            BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                            cancellationToken);
                    }

                    string? removalError = null;
                    try
                    {
                        removed = await downloadClientGateway.RemoveAsync(clientConfig, clientId, deleteFiles, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        removalError = DescribeRemovalFailure(ex);
                        logger.LogDebug(ex, "Deferred removal from {ClientName} failed for {DownloadId}",
                            clientConfig.Name, download.Id);
                    }

                    // If the primary client did not remove a torrent, try other enabled torrent
                    // clients by hash. This keeps cleanup resilient to older records assigned to
                    // the wrong DownloadClientId.
                    if (!removed && !string.IsNullOrEmpty(torrentHash))
                    {
                        var torrentClientTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { "qbittorrent", "transmission", "deluge" };
                        var otherTorrentClients = allEnabledClients
                            .Where(c => torrentClientTypes.Contains(c.Type ?? "") &&
                                        c.Id != download.DownloadClientId)
                            .ToList();

                        foreach (var altClient in otherTorrentClients)
                        {
                            try
                            {
                                var altDeleteFiles = proof.AllowsDestructiveCleanup &&
                                    altClient.RemoveCompletedDownloads == "remove_and_delete";
                                removed = await downloadClientGateway.RemoveAsync(altClient, torrentHash, altDeleteFiles, cancellationToken);
                                if (removed)
                                {
                                    logger.LogInformation(
                                        "Deferred removal: Cross-client removal succeeded — removed {DownloadId} from {ClientName} " +
                                        "(was assigned to {OriginalClientId})",
                                        download.Id, altClient.Name, download.DownloadClientId);
                                    break;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                removalError ??= DescribeRemovalFailure(ex);
                                logger.LogDebug(ex, "Deferred removal: Cross-client removal from {ClientName} failed for {DownloadId}",
                                    altClient.Name, download.Id);
                            }
                        }
                    }

                    if (removed)
                    {
                        logger.LogInformation("Deferred removal: Successfully removed {DownloadId} (deleteFiles={DeleteFiles})",
                            download.Id, deleteFiles);
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupSucceeded,
                            HistoryOutcome.Succeeded,
                            proof.CorrelationId,
                            "Client cleanup completed",
                            BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                            cancellationToken);
                        await downloadRepository.RemoveAsync(download.Id);
                    }
                    else if (IsConfigurationFailure(removalError))
                    {
                        await RecordCleanupFailureAsync(
                            download,
                            downloadRepository,
                            historyRepository,
                            proof,
                            removalPolicy,
                            deleteFiles,
                            removalError!,
                            "configuration-error",
                            HistoryOutcome.Failed,
                            "Client cleanup is blocked by a configuration or authentication error. Correct the saved client settings; retries continue.",
                            cancellationToken);
                    }
                    else if (timeSinceImportProof.HasValue && timeSinceImportProof.Value > FailedCleanupWarningGracePeriod)
                    {
                        await RecordCleanupFailureAsync(
                            download,
                            downloadRepository,
                            historyRepository,
                            proof,
                            removalPolicy,
                            deleteFiles,
                            removalError ?? "The client did not confirm removal. Check client availability, credentials, and the configured item ID.",
                            "failed-after-grace",
                            HistoryOutcome.Failed,
                            "Deferred client cleanup is still failing after 24 hours; the operational record is retained and retries continue.",
                            cancellationToken);
                    }
                    else
                    {
                        await RecordCleanupFailureAsync(
                            download,
                            downloadRepository,
                            historyRepository,
                            proof,
                            removalPolicy,
                            deleteFiles,
                            removalError ?? "The client did not confirm removal. Check client availability, credentials, and the configured item ID.",
                            "retrying",
                            HistoryOutcome.Retrying,
                            "Client cleanup failed; a persisted retry has been scheduled.",
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Error processing deferred removal for {DownloadId}", download.Id);
                }
            }
        }

    }
}
