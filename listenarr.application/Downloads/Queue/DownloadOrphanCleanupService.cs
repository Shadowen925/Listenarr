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

namespace Listenarr.Application.Downloads.Queue
{
    public sealed class DownloadOrphanCleanupService(
        IDownloadRepository downloadRepository,
        IAppMetricsService metrics,
        ILogger<DownloadOrphanCleanupService> logger)
    {
        private static readonly TimeSpan OrphanedDownloadGracePeriod = TimeSpan.FromMinutes(5);

        public async Task RemoveOrphansAsync(
            DownloadClientConfiguration client,
            ClientQueueFetchResult clientQueueResult,
            List<QueueItem> mappedQueueItems,
            List<Download> clientDownloads)
        {
            if (clientQueueResult.UsedCachedSnapshot)
            {
                logger.LogInformation(
                    "Skipping orphan cleanup for client {ClientName}: using cached queue snapshot",
                    client.Name);
                return;
            }

            if (clientQueueResult.IsUnavailable)
            {
                logger.LogWarning(
                    "Skipping orphan cleanup for client {ClientName}: no live queue snapshot was available",
                    client.Name);
                return;
            }

            var clientQueue = clientQueueResult.QueueItems;
            if (clientQueue.Count == 0 && clientDownloads.Any())
            {
                logger.LogWarning(
                    "Skipping orphan cleanup for client {ClientName}: client returned 0 queue items but {Count} downloads are tracked. Client may be temporarily unreachable.",
                    client.Name,
                    clientDownloads.Count);
                return;
            }

            var liveClientItemIds = BuildLiveClientItemIds(clientQueue, mappedQueueItems);
            var now = DateTime.UtcNow;
            var cleanupCandidates = clientDownloads
                .Select(download => new OrphanCleanupCandidate(
                    download,
                    DownloadQueueMetadataMatcher.GetKnownClientItemIds(download.Metadata)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()))
                .Select(candidate => new OrphanCleanupDecision(
                    candidate.Download,
                    candidate.ExternalIds,
                    GetCleanupReason(
                        candidate.Download,
                        candidate.ExternalIds,
                        liveClientItemIds,
                        now)))
                .Where(decision => decision.Reason.HasValue)
                .ToList();

            if (!cleanupCandidates.Any())
            {
                return;
            }

            var newlyDetectedCount = 0;
            foreach (var candidate in cleanupCandidates)
            {
                var download = candidate.Download;

                var reason = candidate.Reason == OrphanCleanupReason.MissingExternalId
                    ? "The tracked download has no client item identifier; its configured completed directory will be checked for recovery."
                    : "The client no longer reports this tracked download; its configured completed directory will be checked for recovery.";
                if (download.GetMetadataString("Recovery.State") != "NeedsAttention"
                    || download.GetMetadataString("Recovery.Reason") != reason)
                {
                    download.SetMetadata("Recovery.State", "NeedsAttention");
                    download.SetMetadata("Recovery.Reason", reason);
                    await downloadRepository.UpdateAsync(download);
                    newlyDetectedCount++;

                    logger.LogWarning(
                        "Retaining tracked download {DownloadId} for client {ClientName}: {Reason}",
                        LogRedaction.SanitizeText(download.Id),
                        LogRedaction.SanitizeText(client.Name),
                        reason);
                }
            }

            if (newlyDetectedCount > 0)
            {
                DownloadQueueDiagnostics.TryIncrementMetric(metrics, "download.orphan.detected", newlyDetectedCount);
            }
        }

        private static HashSet<string> BuildLiveClientItemIds(
            List<QueueItem> clientQueue,
            List<QueueItem> mappedQueueItems)
        {
            var liveClientItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapped in mappedQueueItems.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
            {
                liveClientItemIds.Add(mapped.Id!);
            }

            foreach (var itemId in clientQueue.Where(item => !string.IsNullOrWhiteSpace(item.Id)).Select(item => item.Id!))
            {
                liveClientItemIds.Add(itemId);
            }

            return liveClientItemIds;
        }

        private OrphanCleanupReason? GetCleanupReason(
            Download download,
            List<string> externalIds,
            HashSet<string> liveClientItemIds,
            DateTime now)
        {
            if (download.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused))
            {
                return null;
            }

            var age = now - download.StartedAt;
            if (age < OrphanedDownloadGracePeriod)
            {
                logger.LogDebug(
                    "Skipping orphan cleanup for recent download {DownloadId} '{Title}' (age: {Age:F1} min)",
                    download.Id,
                    download.Title,
                    age.TotalMinutes);
                return null;
            }

            if (externalIds.Count == 0)
            {
                // Direct-download records are owned internally by Listenarr. They do
                // not have a download-client queue item to reconcile against, so the
                // absence of ClientDownloadId/TorrentHash is valid only for DDL.
                if (string.Equals(download.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return OrphanCleanupReason.MissingExternalId;
            }

            if (liveClientItemIds.Contains(download.Id) || externalIds.Any(liveClientItemIds.Contains))
            {
                return null;
            }

            return OrphanCleanupReason.MissingFromClientSnapshot;
        }

        private enum OrphanCleanupReason
        {
            MissingExternalId,
            MissingFromClientSnapshot
        }

        private sealed record OrphanCleanupCandidate(
            Download Download,
            List<string> ExternalIds);

        private sealed record OrphanCleanupDecision(
            Download Download,
            List<string> ExternalIds,
            OrphanCleanupReason? Reason);
    }
}
