using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search;

public partial class AutomaticSearchProcessor
{
    private async Task<(QuickAddOperation? Operation, IReadOnlyList<Download> Downloads, bool ShouldSkip)>
        GetSearchContextAsync(
            Audiobook audiobook,
            IDownloadRepository downloadRepository,
            IQuickAddOperationRepository quickAddOperations,
            AutomaticSearchTrigger searchTrigger,
            CancellationToken cancellationToken)
    {
        var operation = await quickAddOperations.GetByAudiobookIdAsync(
            audiobook.Id,
            cancellationToken);
        if (operation?.State == QuickAddOperationState.FailedNeedsAttention
            && searchTrigger == AutomaticSearchTrigger.Replacement)
        {
            if (!await quickAddOperations.RetryAsync(operation.Id, cancellationToken))
            {
                return (operation, [], true);
            }
        }

        if (operation?.State is QuickAddOperationState.NoMatchingReleases
            or QuickAddOperationState.Imported
            or QuickAddOperationState.Queued
            or QuickAddOperationState.Downloading
            or QuickAddOperationState.DownloadCompleted
            or QuickAddOperationState.ImportPending
            or QuickAddOperationState.Importing
            or QuickAddOperationState.Verifying)
        {
            return (operation, [], true);
        }

        var downloads = await downloadRepository.GetByAudiobookIdAsync(
            audiobook.Id,
            cancellationToken);
        var activeDownload = downloads.FirstOrDefault(download =>
            download.Status is DownloadStatus.Queued
                or DownloadStatus.Downloading
                or DownloadStatus.Paused
                or DownloadStatus.Processing
                or DownloadStatus.Ready
                or DownloadStatus.ImportPending);
        if (activeDownload == null)
        {
            return (operation, downloads, false);
        }

        if (operation != null)
        {
            if (activeDownload.QuickAddOperationId != operation.Id)
            {
                activeDownload.QuickAddOperationId = operation.Id;
                await downloadRepository.UpdateAsync(activeDownload);
            }

            var activeState = activeDownload.Status switch
            {
                DownloadStatus.Downloading => QuickAddOperationState.Downloading,
                DownloadStatus.Completed => QuickAddOperationState.DownloadCompleted,
                DownloadStatus.ImportPending => QuickAddOperationState.ImportPending,
                DownloadStatus.Processing => QuickAddOperationState.Importing,
                _ => QuickAddOperationState.Queued
            };
            await quickAddOperations.AdvanceAsync(
                audiobook.Id,
                activeState,
                downloadClientId: activeDownload.DownloadClientId,
                downloadId: activeDownload.Id,
                cancellationToken: cancellationToken);
        }

        _logger.LogInformation(
            "Audiobook '{Title}' already has an active download (ID: {DownloadId}, Status: {Status}), skipping automatic search",
            audiobook.Title,
            activeDownload.Id,
            activeDownload.Status);
        return (operation, downloads, true);
    }

    private async Task BroadcastRawSearchProgressAsync(
        Audiobook audiobook,
        string searchedQuery,
        int totalIndexerResultCount,
        IReadOnlyList<SearchResult> rawSearchResults,
        int identityRejectedCount)
    {
        try
        {
            var rawSummaries = rawSearchResults.Take(10).Select(result => new
            {
                title = result.Title,
                asin = result.Asin,
                source = result.Source,
                sizeMB = result.Size > 0 ? result.Size / 1024 / 1024 : -1,
                seeders = result.Seeders,
                format = result.Format,
                downloadType = result.DownloadType
            }).ToList();

            using var scope = _serviceScopeFactory.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
            await hub.Clients.All.SendCoreAsync(
                "SearchProgress",
                new object[]
                {
                    new
                    {
                        message = $"Automatic search query: {searchedQuery}",
                        details = new
                        {
                            rawCount = totalIndexerResultCount,
                            distinctRawCount = rawSearchResults.Count,
                            identityRejectedCount,
                            rawSamples = rawSummaries
                        },
                        type = "automatic",
                        audiobookId = audiobook.Id
                    }
                });
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogDebug(
                exception,
                "Failed to broadcast raw search results summary for audiobook {Id}",
                audiobook.Id);
        }
    }

    private async Task BroadcastScoredSearchProgressAsync(
        Audiobook audiobook,
        string searchedQuery,
        int totalIndexerResultCount,
        int identityRejectedCount,
        int searchResultCount,
        int releaseRejectionCount,
        IReadOnlyList<QualityScore> scoredResults)
    {
        try
        {
            var scoredSummaries = scoredResults.Select(result => new
            {
                title = result.SearchResult.Title,
                asin = result.SearchResult.Asin,
                totalScore = result.TotalScore,
                isRejected = result.IsRejected,
                rejectionReasons = result.RejectionReasons,
                source = result.SearchResult.Source,
                sizeMB = result.SearchResult.Size > 0 ? result.SearchResult.Size / 1024 / 1024 : -1,
                seeders = result.SearchResult.Seeders,
                format = result.SearchResult.Format
            }).ToList();

            using var scope = _serviceScopeFactory.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
            await hub.Clients.All.SendCoreAsync(
                "SearchProgress",
                new object[]
                {
                    new
                    {
                        message = $"Scored results for '{audiobook.Title}'",
                        details = new
                        {
                            query = searchedQuery,
                            rawResultCount = totalIndexerResultCount,
                            identityRejectedCount,
                            resultCount = searchResultCount,
                            qualityRejectedCount = scoredResults.Count(result => result.IsRejected),
                            duplicateExcludedCount = releaseRejectionCount,
                            eligibleCount = scoredResults.Count(result => !result.IsRejected),
                            scoredCount = scoredResults.Count,
                            scoredSamples = scoredSummaries
                        },
                        type = "automatic",
                        audiobookId = audiobook.Id
                    }
                });
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogDebug(
                exception,
                "Failed to broadcast scored search results for audiobook {Id}",
                audiobook.Id);
        }

        foreach (var scoredResult in scoredResults.OrderByDescending(result => result.TotalScore))
        {
            var status = scoredResult.IsRejected
                ? "REJECTED"
                : scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE";
            _logger.LogInformation(
                "  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                status,
                scoredResult.TotalScore,
                scoredResult.SearchResult.Title,
                scoredResult.SearchResult.Source,
                scoredResult.SearchResult.Size / 1024 / 1024,
                scoredResult.SearchResult.Seeders,
                scoredResult.SearchResult.Quality);

            if (scoredResult.IsRejected && scoredResult.RejectionReasons.Any())
            {
                _logger.LogInformation(
                    "    Rejection reasons: {Reasons}",
                    string.Join(", ", scoredResult.RejectionReasons));
            }
        }
    }
}
