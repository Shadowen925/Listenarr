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

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    public partial class DownloadMonitorProcessor
    {
        private async Task TriggerCallbacks(
            DownloadClientConfiguration client,
            Download current,
            Download previous,
            CancellationToken cancellationToken = default)
        {
            if (IsIgnoredByUser(current))
            {
                logger.LogDebug(
                    "Skipping completion callbacks for explicitly ignored download {DownloadId}",
                    LogRedaction.SanitizeText(current.Id));
                return;
            }

            switch (previous.Status, current.Status)
            {
                case (DownloadStatus.Completed, DownloadStatus.Completed)
                    when !string.IsNullOrWhiteSpace(current.GetExternalId()):
                    await QueueRecoveredImportAsync(current);
                    return;
                case var (old, next) when old == next:
                    return;
                case (_, DownloadStatus.Failed):
                    await OnDownloadFailed(current, client, current.ErrorMessage ?? "Download failed in client", cancellationToken);
                    break;
                case (_, DownloadStatus.Completed):
                    await OnDownloadCompleted(current);
                    break;
                default:
                    break;
            }

            await OnDownloadUpdated(current, cancellationToken);
        }

        private async Task OnDownloadCompleted(Download download)
        {
            if (IsIgnoredByUser(download))
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
            try
            {
                await downloadProcessingJobService.EnqueueAsync(download);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(
                    exception,
                    "Unable to queue completed download {DownloadId} for importing; the next successful client poll will retry",
                    LogRedaction.SanitizeText(download.Id));
                return;
            }

            try
            {
                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
                await historyRepository.AddAsync(new History
                {
                    AudiobookId = download.AudiobookId,
                    AudiobookTitle = download.Title,
                    SourceTitle = download.Title,
                    DownloadId = download.Id.ToUpperInvariant(),
                    DownloadClientId = download.DownloadClientId,
                    EventType = HistoryEvents.DownloadCompleted,
                    Outcome = HistoryOutcome.Succeeded,
                    Source = "DownloadMonitor",
                    Message = "Download client reported completion",
                    Timestamp = DateTime.UtcNow,
                    CorrelationId = download.Id.ToUpperInvariant(),
                    Data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        download.DownloadPath,
                        download.CompletedAt
                    })
                });
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Completed download {DownloadId} was queued for import, but completion history could not be recorded",
                    LogRedaction.SanitizeText(download.Id));
            }
        }

        private async Task QueueRecoveredImportAsync(Download download)
        {
            if (IsIgnoredByUser(download))
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
            await downloadProcessingJobService.EnqueueAsync(download);
        }

        private static bool IsIgnoredByUser(Download download) =>
            bool.TryParse(download.GetMetadataString("Activity.Ignored"), out var ignored)
            && ignored;

        private async Task OnDownloadUpdated(Download download, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Broadcasting download update for {download.Id}");
            await downloadPushService.HandlePushAsync(download, cancellationToken);
        }

        private async Task OnDownloadsUpdated(List<Download> downloads, CancellationToken cancellationToken)
        {
            logger.LogInformation("Broadcasting downloads update");
            await downloadPushService.HandlePushAsync(downloads, cancellationToken);
        }
    }
}
