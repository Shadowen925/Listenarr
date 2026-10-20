/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdRequestBuilder
    {
        public static string DescribeHttpFailure(HttpStatusCode statusCode, string operation, string clientName)
        {
            var safeClientName = LogRedaction.SanitizeText(clientName);
            var context = $"SABnzbd {operation} failed with HTTP {(int)statusCode} for client {safeClientName}.";

            return statusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    $"{context} Authentication was rejected; verify the API key and any configured HTTP authentication.",
                HttpStatusCode.Forbidden =>
                    $"{context} Access was forbidden; verify API-key access, SABnzbd host restrictions, and reverse-proxy access rules.",
                HttpStatusCode.NotFound =>
                    $"{context} Check the host, port, and configured URL path.",
                _ => context
            };
        }

        public SabnzbdRequestContext CreateContext(DownloadClientConfiguration client)
        {
            var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
            var apiKey = "";
            if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
            {
                apiKey = apiKeyObj?.ToString() ?? "";
            }

            return new SabnzbdRequestContext(baseUrl, apiKey);
        }

        public string BuildUrl(SabnzbdRequestContext context, IReadOnlyDictionary<string, string> queryParams)
        {
            var merged = new Dictionary<string, string>(queryParams, StringComparer.OrdinalIgnoreCase)
            {
                ["apikey"] = context.ApiKey
            };
            var queryString = string.Join("&", merged.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            return $"{context.BaseUrl}?{queryString}";
        }

        public List<string> BuildSensitiveValues(SabnzbdRequestContext context, string? indexerApiKey = null)
            => BuildSensitiveValues(context.ApiKey, indexerApiKey);

        public List<string> BuildSensitiveValues(DownloadClientConfiguration client, string? indexerApiKey = null)
        {
            ArgumentNullException.ThrowIfNull(client);
            var apiKey = client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObject)
                ? apiKeyObject?.ToString() ?? string.Empty
                : string.Empty;
            return BuildSensitiveValues(apiKey, indexerApiKey);
        }

        private static List<string> BuildSensitiveValues(string apiKey, string? indexerApiKey)
        {
            var sensitiveValues = LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { apiKey }).ToList();
            if (!string.IsNullOrEmpty(apiKey))
            {
                sensitiveValues.Add(Uri.EscapeDataString(apiKey));
            }

            if (!string.IsNullOrEmpty(indexerApiKey))
            {
                sensitiveValues.Add(indexerApiKey);
                sensitiveValues.Add(Uri.EscapeDataString(indexerApiKey));
            }

            return sensitiveValues;
        }
    }

    internal sealed record SabnzbdRequestContext(string BaseUrl, string ApiKey)
    {
        public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
    }
}
