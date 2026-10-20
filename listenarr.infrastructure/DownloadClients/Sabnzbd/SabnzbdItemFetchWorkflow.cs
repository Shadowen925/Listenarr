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

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdItemFetchWorkflow(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger<SabnzbdAdapter> logger,
        string clientType)
    {
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            ArgumentNullException.ThrowIfNull(client);

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
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

                var requestUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "queue",
                    ["output"] = "json"
                });
                var http = httpFactory.CreateClient(clientType);
                using var response = await http.GetAsync(requestUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new DownloadClientAdapterPollingException(
                        SabnzbdRequestBuilder.DescribeHttpFailure(
                            response.StatusCode,
                            "queue API",
                            client.Name ?? client.Id));
                }

                var jsonContent = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    throw new DownloadClientAdapterPollingException("SABnzbd returned an empty queue response.");
                }

                using var doc = JsonDocument.Parse(jsonContent);
                if (!doc.RootElement.TryGetProperty("queue", out var queue)
                    || !queue.TryGetProperty("slots", out var slots)
                    || slots.ValueKind != JsonValueKind.Array)
                {
                    throw new DownloadClientAdapterPollingException("SABnzbd returned an invalid queue response.");
                }

                var queueSpeed = 0.0;
                if (queue.TryGetProperty("speed", out var speedProp))
                {
                    var speedStr = speedProp.GetString() ?? "0";
                    queueSpeed = SabnzbdResponseMapper.ParseSpeed(speedStr);
                }

                foreach (var slot in slots.EnumerateArray())
                {
                    try
                    {
                        var downloadClientItem = SabnzbdResponseMapper.MapQueueSlotToDownloadClientItem(client, slot, configuredCategory ?? string.Empty, queueSpeed);
                        if (downloadClientItem != null)
                        {
                            items.Add(downloadClientItem);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogError(
                            "Error parsing SABnzbd queue item ({Failure})",
                            LogRedaction.RedactText(ex.ToString(), sensitiveValues));
                    }
                }
                logger.LogInformation("Retrieved {Count} items from SABnzbd queue", items.Count);
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(
                    "Error getting SABnzbd items ({Failure})",
                    LogRedaction.RedactText(ex.ToString(), sensitiveValues));
                throw new DownloadClientAdapterPollingException(
                    "SABnzbd queue item retrieval failed due to a network or response error.");
            }

            return items;
        }
    }
}
