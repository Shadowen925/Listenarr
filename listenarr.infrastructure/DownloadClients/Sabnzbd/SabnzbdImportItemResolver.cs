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
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdImportItemResolver(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger logger,
        string clientType)
    {
        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            CancellationToken ct = default)
        {
            var result = item.Clone();

            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                var localPath = result.OutputPath;
                if (SabnzbdImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.OutputPath = localPath;
                    return result;
                }
            }

            // SABnzbd's active queue path can be absent or stale. The completed history
            // record's storage field is the authoritative import location once available.
            var storage = await ResolveHistoryStorageAsync(client, item.DownloadId, ct);
            if (!string.IsNullOrWhiteSpace(storage))
            {
                result.OutputPath = storage;
                logger.LogDebug(
                    "Resolved SABnzbd content path for {NzoId}: {ContentPath}",
                    item.DownloadId,
                    storage);
            }

            return result;
        }

        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var result = queueItem.Clone();

            if (!string.IsNullOrWhiteSpace(result.ContentPath))
            {
                var localPath = result.ContentPath;
                if (SabnzbdImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.ContentPath = localPath;
                    return result;
                }
            }

            // Prefer SABnzbd history storage over a guessed or missing queue ContentPath.
            // This keeps active queue telemetry separate from import path resolution.
            var storage = await ResolveHistoryStorageAsync(client, queueItem.Id, ct);
            if (!string.IsNullOrWhiteSpace(storage))
            {
                result.ContentPath = storage;
                logger.LogDebug($"Resolved SABnzbd content path for {queueItem.Id}: {result.ContentPath}");
            }

            return result;
        }

        private async Task<string?> ResolveHistoryStorageAsync(
            DownloadClientConfiguration client,
            string nzoId,
            CancellationToken ct)
        {
            IEnumerable<string?> sensitiveValues = [];
            try
            {
                var requestContext = requestBuilder.CreateContext(client);
                sensitiveValues = requestBuilder.BuildSensitiveValues(requestContext);
                if (!requestContext.HasApiKey)
                {
                    throw new DownloadClientAdapterPollingException(
                        $"SABnzbd API key is not configured for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.");
                }

                var historyUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "history",
                    ["output"] = "json"
                });
                var http = httpFactory.CreateClient(clientType);
                using var historyResp = await http.GetAsync(historyUrl, ct);

                if (!historyResp.IsSuccessStatusCode)
                {
                    throw new DownloadClientAdapterPollingException(
                        SabnzbdRequestBuilder.DescribeHttpFailure(
                            historyResp.StatusCode,
                            "history API",
                            client.Name ?? client.Id));
                }

                var historyText = await historyResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(historyText))
                {
                    throw new DownloadClientAdapterPollingException("SABnzbd returned an empty history response.");
                }

                using var doc = JsonDocument.Parse(historyText);
                if (!doc.RootElement.TryGetProperty("history", out var history) ||
                    !history.TryGetProperty("slots", out var slots) ||
                    slots.ValueKind != JsonValueKind.Array)
                {
                    throw new DownloadClientAdapterPollingException("SABnzbd returned an invalid history response.");
                }

                foreach (var slot in slots.EnumerateArray())
                {
                    var slotNzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                    if (!string.Equals(slotNzoId, nzoId, StringComparison.OrdinalIgnoreCase)) continue;

                    var storage = SabnzbdImportPathResolver.GetStoragePath(slot);
                    if (string.IsNullOrWhiteSpace(storage))
                    {
                        logger.LogWarning("No storage path found for SABnzbd download {NzoId}", nzoId);
                        return null;
                    }

                    return storage;
                }

                logger.LogWarning("Download {NzoId} not found in SABnzbd history", nzoId);
                return null;
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
                    "Error resolving import item for SABnzbd download {NzoId}: {Failure}",
                    LogRedaction.SanitizeText(nzoId),
                    LogRedaction.RedactText(ex.ToString(), sensitiveValues));
                throw new DownloadClientAdapterPollingException(
                    "SABnzbd history could not be queried while resolving the import path.");
            }
        }
    }
}
