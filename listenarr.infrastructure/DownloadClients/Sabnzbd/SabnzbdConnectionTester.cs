/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdConnectionTester(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger<SabnzbdAdapter> logger,
        string clientType)
    {
        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            var requestContext = requestBuilder.CreateContext(client);
            if (!requestContext.HasApiKey)
            {
                return (false, "API key: Missing; version, queue, and history endpoints were not tested.");
            }

            var version = await CheckEndpointAsync(
                requestContext,
                "version",
                "version",
                client,
                ct);
            var queue = await CheckEndpointAsync(
                requestContext,
                "queue",
                "queue",
                client,
                ct);
            var history = await CheckEndpointAsync(
                requestContext,
                "history",
                "history",
                client,
                ct);

            var success = version.Success && queue.Success && history.Success;
            return (success, string.Join("; ", version.Message, queue.Message, history.Message));
        }

        private async Task<(bool Success, string Message)> CheckEndpointAsync(
            SabnzbdRequestContext requestContext,
            string mode,
            string operation,
            DownloadClientConfiguration client,
            CancellationToken ct)
        {
            var url = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
            {
                ["mode"] = mode,
                ["output"] = "json"
            });

            try
            {
                var http = httpFactory.CreateClient(clientType);
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, FormatStatus(operation, response.StatusCode, client));
                }

                await using var content = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(content, cancellationToken: ct);
                if (!HasExpectedPayload(document.RootElement, mode))
                {
                    return (false, $"{Capitalize(operation)} API: Invalid response (HTTP {(int)response.StatusCode}).");
                }

                return (true, $"{Capitalize(operation)} API: Success (HTTP {(int)response.StatusCode}).");
            }
            catch (JsonException)
            {
                return (false, $"{Capitalize(operation)} API: Invalid JSON.");
            }
            catch (HttpRequestException exception)
            {
                logger.LogDebug(
                    "SABnzbd {Operation} endpoint network error: {Failure}",
                    operation,
                    LogRedaction.RedactText(
                        exception.ToString(),
                        requestBuilder.BuildSensitiveValues(requestContext)));
                return (false, exception.StatusCode is HttpStatusCode statusCode
                    ? FormatStatus(operation, statusCode, client)
                    : $"{Capitalize(operation)} API: Network error.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, $"{Capitalize(operation)} API: Request timed out.");
            }
        }

        private static bool HasExpectedPayload(JsonElement root, string mode)
        {
            if (mode == "version")
            {
                return root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(version.GetString());
            }

            var containerName = mode;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(containerName, out var container)
                && container.ValueKind == JsonValueKind.Object
                && container.TryGetProperty("slots", out var slots)
                && slots.ValueKind == JsonValueKind.Array;
        }

        private static string FormatStatus(
            string operation,
            HttpStatusCode statusCode,
            DownloadClientConfiguration client)
        {
            var summary = SabnzbdRequestBuilder.DescribeHttpFailure(
                statusCode,
                $"{operation} API",
                client.Name ?? client.Id);
            return $"{Capitalize(operation)} API: {summary}";
        }

        private static string Capitalize(string value) =>
            char.ToUpperInvariant(value[0]) + value[1..];
    }
}
