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
            try
            {
                ArgumentNullException.ThrowIfNull(client);
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    return (false, "SABnzbd API key not configured in client settings");
                }

                var url = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "version",
                    ["output"] = "json"
                });
                var http = httpFactory.CreateClient(clientType);
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return (false, SabnzbdRequestBuilder.DescribeHttpFailure(
                        resp.StatusCode,
                        "connection test",
                        client.Name ?? client.Id));
                }

                return (true, "SABnzbd: connected");
            }
            catch (HttpRequestException httpEx)
            {
                logger.LogDebug(httpEx, "SABnzbd TestConnection network error");
                return (false, $"SABnzbd: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                logger.LogDebug(tce, "SABnzbd TestConnection timed out");
                return (false, "SABnzbd: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "SABnzbd TestConnection failed");
                return (false, "SABnzbd: connection failed");
            }
        }
    }
}
