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
using Listenarr.Application.Downloads.Stalls;
using Listenarr.Application.Search.Matching;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    public partial class AutomaticSearchProcessor
    {
        private async Task<int> ProcessAudiobookAsync(
            Audiobook audiobook,
            ISearchService searchService,
            IQualityProfileService qualityProfileService,
            IDownloadService downloadService,
            IAudiobookRepository audiobookRepository,
            IDownloadRepository downloadRepository,
            IAudiobookFileRepository fileRepository,
            IReleaseRejectionRepository releaseRejections,
            IConfigurationService configurationService,
            IReadOnlyList<string> indexersQueried,
            AutomaticSearchTrigger searchTrigger,
            int retryIntervalHours,
            CancellationToken stoppingToken)
        {
            if (audiobook.QualityProfile == null)
            {
                _logger.LogWarning("Audiobook '{Title}' has no quality profile assigned", audiobook.Title);
                return 0;
            }

            // Check if there's already an active download for this audiobook
            var allDownloads = await downloadRepository.GetByAudiobookIdAsync(audiobook.Id, stoppingToken);
            var activeDownload = allDownloads.FirstOrDefault(d =>
                d.Status == DownloadStatus.Queued ||
                d.Status == DownloadStatus.Downloading ||
                d.Status == DownloadStatus.Paused ||
                d.Status == DownloadStatus.Processing ||
                d.Status == DownloadStatus.Ready ||
                d.Status == DownloadStatus.ImportPending);

            if (activeDownload != null)
            {
                _logger.LogInformation("Audiobook '{Title}' already has an active download (ID: {DownloadId}, Status: {Status}), skipping automatic search",
                    audiobook.Title, activeDownload.Id, activeDownload.Status);
                return 0;
            }

            // Check existing quality and decide whether to search
            var (cutoffMet, bestExistingQuality) = await _qualityEvaluator.GetExistingQualityAsync(audiobook, downloadRepository, fileRepository, stoppingToken);
            _logger.LogInformation("Audiobook '{Title}': cutoff met={CutoffMet}, best existing quality={BestQuality}",
                audiobook.Title, cutoffMet, bestExistingQuality ?? "none");

            // Skip automatic search if quality cutoff is already met
            if (cutoffMet)
            {
                _logger.LogInformation("Quality cutoff already met for audiobook '{Title}', skipping automatic search", audiobook.Title);
                return 0;
            }

            // Build search query
            var searchQueries = _resultClassifier.BuildSearchQueries(audiobook);
            var searchQuery = searchQueries.FirstOrDefault() ?? string.Empty;
            _logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", audiobook.Title, searchQuery);

            var submissionAttempts = new List<AutomaticSearchSubmissionAttempt>();
            var rejectionReasons = new List<string>();
            var rawSearchResults = new List<SearchResult>();
            var searchResults = new List<SearchResult>();
            var attemptedQueries = new List<string>();
            var successfulQueries = 0;
            var totalIndexerResultCount = 0;
            var identityRejectedCount = 0;
            var identityMatchCount = 0;
            var qualityRejectedCount = 0;
            var indexerOutcomes = new List<AutomaticSearchIndexerOutcome>();
            Exception? lastSearchException = null;
            try
            {
                var collected = await CollectSearchResultsAsync(searchService, searchQueries, audiobook.Id);
                rawSearchResults = collected.Results;
                attemptedQueries = collected.QueriesAttempted;
                successfulQueries = collected.SuccessfulQueryCount;
                totalIndexerResultCount = collected.RawResultCount;
                indexerOutcomes = collected.IndexerOutcomes;
                lastSearchException = collected.LastFailure;
                if (successfulQueries == 0 && lastSearchException != null)
                {
                    throw new InvalidOperationException(
                        "All automatic search query variants failed.");
                }

                if (lastSearchException != null)
                {
                    rejectionReasons.Add("SomeSearchQueriesFailed");
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                audiobook.LastSearchTime = DateTime.UtcNow;
                await audiobookRepository.UpdateAsync(audiobook);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                audiobook.LastSearchTime = DateTime.UtcNow;
                await audiobookRepository.UpdateAsync(audiobook);
                await PublishDiagnosticAsync(
                    audiobook,
                    string.Join(" | ", attemptedQueries),
                    "IndexerFailure",
                    resultCount: 0,
                    eligibleCount: 0,
                    rejectedCount: 0,
                    searchTrigger,
                    indexersQueried,
                    nextRetryAt: DateTime.UtcNow.AddHours(retryIntervalHours),
                    queriesAttempted: attemptedQueries,
                    indexerOutcomes: indexerOutcomes);
                throw;
            }
            var searchedQuery = string.Join(" | ", attemptedQueries);
            searchResults = rawSearchResults
                .Where(result => AudiobookSearchResultMatcher.MatchesIdentity(audiobook, result))
                .ToList();
            identityMatchCount = searchResults.Count;
            identityRejectedCount = rawSearchResults.Count - searchResults.Count;
            if (identityRejectedCount > 0)
            {
                _logger.LogWarning(
                    "Discarded {Count} distinct automatic search result(s) with conflicting audiobook identity for '{Title}'",
                    identityRejectedCount,
                    audiobook.Title);
            }

            _logger.LogInformation(
                "Found {Count} matching search results for audiobook '{Title}' using query '{Query}'",
                searchResults.Count,
                audiobook.Title,
                searchedQuery);

            // Broadcast detailed debug info about the raw search results to help diagnose automatic search failures
            try
            {
                // Build a concise summary of up to 10 raw results
                var rawSummaries = rawSearchResults.Take(10).Select(r => new
                {
                    title = r.Title,
                    asin = r.Asin,
                    source = r.Source,
                    sizeMB = r.Size > 0 ? (r.Size / 1024 / 1024) : -1,
                    seeders = r.Seeders,
                    format = r.Format,
                    downloadType = r.DownloadType
                }).ToList();

                using var scope = _serviceScopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                // Send structured payload with type and audiobookId so the UI can ignore automatic messages by default
                await hub.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Automatic search query: {searchedQuery}", details = new { rawCount = totalIndexerResultCount, distinctRawCount = rawSearchResults.Count, identityRejectedCount, rawSamples = rawSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast raw search results summary for audiobook {Id}", audiobook.Id);
            }

            if (!searchResults.Any())
            {
                if (identityRejectedCount > 0)
                {
                    rejectionReasons.Add("IdentityMismatch");
                }

                if (lastSearchException != null)
                {
                    _logger.LogWarning(
                        lastSearchException,
                        "Automatic search found no results and at least one query failed for audiobook {AudiobookId}",
                        audiobook.Id);
                }
                _logger.LogInformation("No search results found for audiobook '{Title}'", audiobook.Title);
                await PublishDiagnosticAsync(
                    audiobook,
                    searchedQuery,
                    identityRejectedCount > 0
                        ? "NoSuitableReleases"
                        : lastSearchException == null ? "NoReleases" : "PartialIndexerFailure",
                    resultCount: totalIndexerResultCount,
                    eligibleCount: 0,
                    rejectedCount: identityRejectedCount,
                    searchTrigger,
                    indexersQueried,
                    rejectionReasons: rejectionReasons,
                    nextRetryAt: DateTime.UtcNow.AddHours(retryIntervalHours),
                    queriesAttempted: attemptedQueries,
                    indexerOutcomes: indexerOutcomes,
                    distinctCandidateCount: rawSearchResults.Count,
                    identityMatchCount: identityMatchCount,
                    qualityRejectedCount: qualityRejectedCount);
                return 0;
            }

            // Score results against quality profile
            var scoredResults = await qualityProfileService.ScoreSearchResults(searchResults, audiobook.QualityProfile);
            qualityRejectedCount = scoredResults.Count(result => result.IsRejected);
            var rejectedReleases = new ReleaseRejectionSet();
            foreach (var rejection in await releaseRejections.ListAsync(audiobook.Id, 1000, stoppingToken))
            {
                rejectedReleases.AddKey(rejection.ReleaseKey);
            }

            foreach (var failedDownload in allDownloads.Where(download => download.Status == DownloadStatus.Failed))
            {
                rejectedReleases.AddDownload(failedDownload);
            }
            var releaseRejectionCount = scoredResults.Count(result =>
                rejectedReleases.Matches(result.SearchResult));
            scoredResults = [.. scoredResults.Where(result => !rejectedReleases.Matches(result.SearchResult))];
            rejectionReasons.AddRange(scoredResults
                .Where(result => result.IsRejected)
                .SelectMany(result => result.RejectionReasons)
                .Where(reason => !string.IsNullOrWhiteSpace(reason)));
            if (releaseRejectionCount > 0)
            {
                rejectionReasons.Add("PreviouslyRejected");
            }

            // Log all scored results for debugging
            _logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, audiobook.Title);

            // Broadcast scored result summaries (score + rejection reasons) to aid debugging
            try
            {
                var scoredSummaries = scoredResults.Select(s => new
                {
                    title = s.SearchResult.Title,
                    asin = s.SearchResult.Asin,
                    totalScore = s.TotalScore,
                    isRejected = s.IsRejected,
                    rejectionReasons = s.RejectionReasons,
                    source = s.SearchResult.Source,
                    sizeMB = s.SearchResult.Size > 0 ? (s.SearchResult.Size / 1024 / 1024) : -1,
                    seeders = s.SearchResult.Seeders,
                    format = s.SearchResult.Format
                }).ToList();

                using var scope2 = _serviceScopeFactory.CreateScope();
                var hub2 = scope2.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                await hub2.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Scored results for '{audiobook.Title}'", details = new { query = searchedQuery, rawResultCount = totalIndexerResultCount, identityRejectedCount, resultCount = searchResults.Count, qualityRejectedCount = scoredResults.Count(s => s.IsRejected), duplicateExcludedCount = releaseRejectionCount, eligibleCount = scoredResults.Count(s => !s.IsRejected), scoredCount = scoredResults.Count, scoredSamples = scoredSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast scored search results for audiobook {Id}", audiobook.Id);
            }
            foreach (var scoredResult in scoredResults.OrderByDescending(s => s.TotalScore))
            {
                var status = scoredResult.IsRejected ? "REJECTED" : (scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE");
                _logger.LogInformation("  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                    status, scoredResult.TotalScore, scoredResult.SearchResult.Title, scoredResult.SearchResult.Source,
                    scoredResult.SearchResult.Size / 1024 / 1024, scoredResult.SearchResult.Seeders, scoredResult.SearchResult.Quality);

                if (scoredResult.IsRejected && scoredResult.RejectionReasons.Any())
                {
                    _logger.LogInformation("    Rejection reasons: {Reasons}", string.Join(", ", scoredResult.RejectionReasons));
                }
            }

            var eligibleResults = scoredResults
                .Where(result => !result.IsRejected)
                .OrderByDescending(result => result.TotalScore)
                .Where(result => string.IsNullOrWhiteSpace(bestExistingQuality)
                    || _qualityEvaluator.IsQualityBetter(
                        result.SearchResult.Quality,
                        bestExistingQuality,
                        audiobook.QualityProfile))
                .ToList();

            if (eligibleResults.Count == 0)
            {
                _logger.LogInformation(
                    "No eligible release improves existing quality for audiobook '{Title}'",
                    audiobook.Title);
                rejectionReasons.Add("NoReleaseImprovesExistingQuality");
                await PublishDiagnosticAsync(
                    audiobook,
                    searchedQuery,
                    "NoSuitableReleases",
                    totalIndexerResultCount,
                    eligibleCount: 0,
                    rejectedCount: scoredResults.Count(s => s.IsRejected) + releaseRejectionCount,
                    searchTrigger,
                    indexersQueried,
                    rejectionReasons: rejectionReasons.Distinct(StringComparer.Ordinal).Take(100).ToArray(),
                    nextRetryAt: DateTime.UtcNow.AddHours(retryIntervalHours),
                    queriesAttempted: attemptedQueries,
                    indexerOutcomes: indexerOutcomes,
                    distinctCandidateCount: rawSearchResults.Count,
                    identityMatchCount: identityMatchCount,
                    qualityRejectedCount: qualityRejectedCount);
                return 0;
            }

            foreach (var scoredResult in eligibleResults)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var result = scoredResult.SearchResult;
                result.Score = scoredResult.TotalScore;
                var isTorrent = _resultClassifier.IsTorrentResult(result);
                string? clientType = null;
                try
                {
                    var clientId = await _downloadClientSelector.GetAppropriateDownloadClientAsync(result, isTorrent);
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        submissionAttempts.Add(new AutomaticSearchSubmissionAttempt(
                            result.Title,
                            null,
                            "Failed",
                            "NoCompatibleClient"));
                        continue;
                    }

                    var clientConfiguration = await configurationService.GetDownloadClientConfigurationAsync(clientId);
                    clientType = clientConfiguration?.Type;
                    var downloadId = await downloadService.StartDownloadAsync(result, clientId, audiobook.Id);
                    if (string.IsNullOrWhiteSpace(downloadId))
                    {
                        submissionAttempts.Add(new AutomaticSearchSubmissionAttempt(
                            result.Title,
                            clientType,
                            "Skipped",
                            "DuplicatePrevented"));
                        await PublishDiagnosticAsync(
                            audiobook,
                            searchedQuery,
                            "DuplicatePrevented",
                            totalIndexerResultCount,
                            eligibleCount: eligibleResults.Count,
                            rejectedCount: scoredResults.Count(s => s.IsRejected) + releaseRejectionCount,
                            searchTrigger,
                            indexersQueried,
                            selectedRelease: result.Title,
                            rejectionReasons: rejectionReasons.Distinct(StringComparer.Ordinal).Take(100).ToArray(),
                            submissionAttempts: submissionAttempts,
                            downloadClient: clientType,
                            nextRetryAt: DateTime.UtcNow.AddHours(retryIntervalHours),
                            queriesAttempted: attemptedQueries,
                            indexerOutcomes: indexerOutcomes,
                            distinctCandidateCount: rawSearchResults.Count,
                            identityMatchCount: identityMatchCount,
                            qualityRejectedCount: qualityRejectedCount);
                        return 0;
                    }

                    submissionAttempts.Add(new AutomaticSearchSubmissionAttempt(
                        result.Title,
                        clientType,
                        "Submitted",
                        null));
                    _logger.LogInformation(
                        "Queued download for audiobook '{Title}': {ResultTitle} (Score: {Score})",
                        audiobook.Title,
                        result.Title,
                        scoredResult.TotalScore);
                    await PublishDiagnosticAsync(
                        audiobook,
                        searchedQuery,
                        "DownloadQueued",
                        totalIndexerResultCount,
                        eligibleCount: eligibleResults.Count,
                        rejectedCount: scoredResults.Count(s => s.IsRejected) + releaseRejectionCount,
                        searchTrigger,
                        indexersQueried,
                        selectedRelease: result.Title,
                        rejectionReasons: rejectionReasons.Distinct(StringComparer.Ordinal).Take(100).ToArray(),
                        submissionAttempts: submissionAttempts,
                        downloadClient: clientType,
                        queriesAttempted: attemptedQueries,
                        indexerOutcomes: indexerOutcomes,
                        distinctCandidateCount: rawSearchResults.Count,
                        identityMatchCount: identityMatchCount,
                        qualityRejectedCount: qualityRejectedCount);
                    return 1;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    var failureKind = ex is DownloadClientSubmissionException submissionException
                        ? submissionException.FailureKind
                        : DownloadSubmissionFailureKind.ClientUnavailable;
                    var failureReason = failureKind == DownloadSubmissionFailureKind.ReleaseSpecific
                        ? "ReleaseSourceRejected"
                        : "ClientUnavailable";
                    submissionAttempts.Add(new AutomaticSearchSubmissionAttempt(
                        result.Title,
                        clientType,
                        "Failed",
                        failureReason));
                    _logger.LogWarning(
                        ex,
                        "Automatic submission attempt failed for audiobook {AudiobookId} and release {ReleaseTitle} through client type {ClientType}",
                        audiobook.Id,
                        result.Title,
                        clientType ?? "unknown");

                    if (failureKind == DownloadSubmissionFailureKind.ReleaseSpecific)
                    {
                        var releaseKey = !string.IsNullOrWhiteSpace(result.Id)
                            ? ReleaseRejection.IdKey(result.Id)
                            : ReleaseRejection.TitleKey(result.Title);
                        if (!string.IsNullOrWhiteSpace(releaseKey))
                        {
                            await releaseRejections.AddAsync(new ReleaseRejection
                            {
                                AudiobookId = audiobook.Id,
                                ReleaseKey = releaseKey,
                                ReleaseTitle = result.Title,
                                Reason = "Automatic submission rejected the release source.",
                                Source = "AutomaticSearch"
                            }, stoppingToken);
                        }
                    }
                }
            }

            _logger.LogWarning(
                "All {AttemptCount} eligible release submission attempts failed for audiobook '{Title}'",
                submissionAttempts.Count,
                audiobook.Title);
            await PublishDiagnosticAsync(
                audiobook,
                searchedQuery,
                "AllSubmissionsFailed",
                totalIndexerResultCount,
                eligibleCount: eligibleResults.Count,
                rejectedCount: scoredResults.Count(s => s.IsRejected) + releaseRejectionCount,
                searchTrigger,
                indexersQueried,
                selectedRelease: submissionAttempts.LastOrDefault()?.ReleaseTitle,
                rejectionReasons: rejectionReasons.Distinct(StringComparer.Ordinal).Take(100).ToArray(),
                submissionAttempts: submissionAttempts,
                downloadClient: submissionAttempts.LastOrDefault()?.ClientType,
                nextRetryAt: DateTime.UtcNow.AddHours(retryIntervalHours),
                queriesAttempted: attemptedQueries,
                indexerOutcomes: indexerOutcomes,
                distinctCandidateCount: rawSearchResults.Count,
                identityMatchCount: identityMatchCount,
                qualityRejectedCount: qualityRejectedCount);
            return 0;
        }
    }
}
