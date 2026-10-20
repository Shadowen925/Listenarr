/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdHistoryFetchWorkflow(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger<SabnzbdAdapter> logger,
        string clientType)
    {
        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var result = new List<(string Id, string Name)>();
            ArgumentNullException.ThrowIfNull(client);
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
                    ["output"] = "json",
                    ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
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
                if (!doc.RootElement.TryGetProperty("history", out var history)
                    || !history.TryGetProperty("slots", out var slots)
                    || slots.ValueKind != JsonValueKind.Array)
                {
                    throw new DownloadClientAdapterPollingException("SABnzbd returned an invalid history response.");
                }

                foreach (var slot in slots.EnumerateArray())
                {
                    var nzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                    var name = slot.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                    result.Add((nzoId, name));
                }
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(
                    "SABnzbd history endpoint request failed for {ClientName}: {Failure}",
                    LogRedaction.SanitizeText(client.Name ?? client.Id),
                    LogRedaction.RedactText(exception.ToString(), sensitiveValues));
                throw new DownloadClientAdapterPollingException(
                    exception.StatusCode is System.Net.HttpStatusCode statusCode
                        ? SabnzbdRequestBuilder.DescribeHttpFailure(
                            statusCode,
                            "history API",
                            client.Name ?? client.Id)
                        : "SABnzbd history endpoint could not be reached.");
            }
            catch (JsonException exception)
            {
                logger.LogWarning(
                    "SABnzbd history endpoint returned invalid JSON for {ClientName}: {Failure}",
                    LogRedaction.SanitizeText(client.Name ?? client.Id),
                    LogRedaction.RedactText(exception.ToString(), sensitiveValues));
                throw new DownloadClientAdapterPollingException("SABnzbd returned invalid history JSON.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
                    "Failed to fetch SABnzbd history for {ClientName}: {Failure}",
                    LogRedaction.SanitizeText(client.Name ?? client.Id),
                    LogRedaction.RedactText(ex.ToString(), sensitiveValues));
                throw new DownloadClientAdapterPollingException("Failed to fetch SABnzbd history.");
            }

            return result;
        }
    }
}
