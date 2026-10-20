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
    internal sealed class SabnzbdAddWorkflow(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger<SabnzbdAdapter> logger,
        string clientType)
    {
        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (submission is not PreparedUsenetSubmission usenet)
                throw new DownloadClientSubmissionException("SABnzbd requires a prepared Usenet submission.");

            IEnumerable<string?> sensitiveValues = [];
            try
            {
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    throw new DownloadClientSubmissionException(
                        "SABnzbd API key is not configured.",
                        failureKind: DownloadSubmissionFailureKind.ClientUnavailable);
                }

                logger.LogInformation("Sending prepared NZB to SABnzbd: {Title} from {Source}", LogRedaction.SanitizeText(usenet.Title), LogRedaction.SanitizeText(usenet.Source));

                sensitiveValues = requestBuilder.BuildSensitiveValues(requestContext);
                var queryParams = SabnzbdAddRequestPlanner.BuildFileQueryParams(client, usenet.Title);
                var requestUrl = requestBuilder.BuildUrl(requestContext, queryParams);

                logger.LogDebug("SABnzbd request URL: {Url}", LogRedaction.RedactText(requestUrl, sensitiveValues));

                var http = httpFactory.CreateClient(clientType);
                using var multipart = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(usenet.NzbBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-nzb");
                multipart.Add(fileContent, "name", usenet.FileName);
                var response = await http.PostAsync(requestUrl, multipart, ct);
                var responseContent = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    var redacted = LogRedaction.RedactText(responseContent, sensitiveValues);
                    logger.LogError("SABnzbd returned error status {Status}: {Content}", response.StatusCode, redacted);
                    var failureKind = response.StatusCode == System.Net.HttpStatusCode.BadRequest
                        ? DownloadSubmissionFailureKind.ReleaseSpecific
                        : DownloadSubmissionFailureKind.ClientUnavailable;
                    throw new DownloadClientSubmissionException(
                        SabnzbdRequestBuilder.DescribeHttpFailure(
                            response.StatusCode,
                            "NZB submission",
                            client.Name ?? client.Id),
                        failureKind: failureKind);
                }

                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    logger.LogWarning("SABnzbd returned empty response body when adding NZB: {Url}", LogRedaction.RedactText(requestUrl, sensitiveValues));
                    throw new DownloadClientSubmissionException(
                        "SABnzbd returned an empty submission response.",
                        failureKind: DownloadSubmissionFailureKind.ClientUnavailable);
                }

                var jsonDoc = JsonDocument.Parse(responseContent);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errorMsg = errorElement.GetString();
                    if (!string.IsNullOrEmpty(errorMsg))
                    {
                        var normalizedError = errorMsg.ToLowerInvariant();
                        var failureKind = normalizedError.Contains("api key", StringComparison.Ordinal)
                            || normalizedError.Contains("unauthorized", StringComparison.Ordinal)
                            || normalizedError.Contains("forbidden", StringComparison.Ordinal)
                            ? DownloadSubmissionFailureKind.ClientUnavailable
                            : DownloadSubmissionFailureKind.ReleaseSpecific;
                        throw new DownloadClientSubmissionException(
                            failureKind == DownloadSubmissionFailureKind.ClientUnavailable
                                ? "SABnzbd rejected the request due to an authentication or access configuration error."
                                : "SABnzbd rejected the release.",
                            failureKind: failureKind);
                    }
                }

                if (root.TryGetProperty("nzo_ids", out var nzoIds) && nzoIds.ValueKind == JsonValueKind.Array)
                {
                    var firstId = nzoIds.EnumerateArray().FirstOrDefault();
                    var downloadId = firstId.GetString() ?? Guid.NewGuid().ToString();
                    logger.LogInformation("Successfully added NZB to SABnzbd with ID: {DownloadId}", LogRedaction.SanitizeText(downloadId));
                    return new DownloadClientSubmissionResult(downloadId);
                }

                throw new DownloadClientSubmissionException(
                    "SABnzbd did not return a verified queue identifier.",
                    failureKind: DownloadSubmissionFailureKind.ClientUnavailable);
            }
            catch (DownloadClientSubmissionException ex)
            {
                logger.LogError(
                    "Failed to send NZB to SABnzbd: {Failure}",
                    LogRedaction.RedactText(ex.ToString(), sensitiveValues));
                throw;
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(
                    "Failed to send NZB to SABnzbd: {Failure}",
                    LogRedaction.RedactText(ex.ToString(), sensitiveValues));
                throw new DownloadClientSubmissionException(
                    "SABnzbd could not be reached while submitting the NZB.",
                    failureKind: DownloadSubmissionFailureKind.ClientUnavailable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(
                    "Failed to send NZB to SABnzbd: {Failure}",
                    LogRedaction.RedactText(ex.ToString(), sensitiveValues));
                throw new DownloadClientSubmissionException(
                    "SABnzbd returned an invalid or unexpected submission response.",
                    failureKind: DownloadSubmissionFailureKind.ClientUnavailable);
            }
        }
    }
}
