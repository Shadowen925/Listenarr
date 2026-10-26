using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Listenarr.Domain.Common;
using Listenarr.Application.Search.Matching;

namespace Listenarr.Infrastructure.HostedServices.Search;

public partial class AutomaticSearchProcessor
{
    private (List<SearchResult> Matches, int RejectedCount) FilterIdentityMatchedResults(
        Audiobook audiobook,
        IReadOnlyCollection<SearchResult> results)
    {
        var matches = results
            .Where(result => AudiobookSearchResultMatcher.MatchesIdentity(audiobook, result))
            .ToList();
        var rejectedCount = results.Count - matches.Count;
        if (rejectedCount > 0)
        {
            _logger.LogWarning(
                "Discarded {Count} distinct automatic search result(s) with conflicting audiobook identity for '{Title}'",
                rejectedCount,
                audiobook.Title);
        }

        return (matches, rejectedCount);
    }

    private async Task<bool> TryResolveVerifiedNoMatchAsync(
        Audiobook audiobook,
        QuickAddOperation? operation,
        IQuickAddOperationRepository quickAddOperations,
        IAudiobookFileRepository fileRepository,
        IAudiobookScanService audiobookScanService,
        CancellationToken cancellationToken)
    {
        if (operation?.State != QuickAddOperationState.NoMatchingReleases
            || !await HasVerifiedRegisteredFileAtCutoffAsync(
                audiobook,
                fileRepository,
                audiobookScanService,
                cancellationToken))
        {
            return false;
        }

        await quickAddOperations.AdvanceAsync(
            audiobook.Id,
            QuickAddOperationState.Imported,
            cancellationToken: cancellationToken);
        return true;
    }

    private bool ShouldSkipForExistingQuality(
        Audiobook audiobook,
        QuickAddOperation? operation,
        bool cutoffMet)
    {
        if (!cutoffMet)
        {
            return false;
        }

        _logger.LogInformation(
            "Quality cutoff already met for audiobook '{Title}', skipping automatic search",
            audiobook.Title);
        if (operation?.State != QuickAddOperationState.NoMatchingReleases)
        {
            return true;
        }

        _logger.LogInformation(
            "Prior no-match state for audiobook {AudiobookId} is not backed by a verified registered file; preserving search eligibility",
            audiobook.Id);
        return false;
    }

    private async Task<bool> TryReopenNoMatchAsync(
        QuickAddOperation? operation,
        IQuickAddOperationRepository quickAddOperations,
        CancellationToken cancellationToken)
    {
        if (operation?.State != QuickAddOperationState.NoMatchingReleases
            || await quickAddOperations.RetryAsync(operation.Id, cancellationToken))
        {
            return true;
        }

        _logger.LogWarning(
            "Could not reopen prior no-match search operation {OperationId}",
            operation.Id);
        return false;
    }

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

        if (operation?.State is QuickAddOperationState.Imported
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

    private async Task<bool> FailIfQualityProfileMissingAsync(
        Audiobook audiobook,
        QuickAddOperation? operation,
        IQuickAddOperationRepository quickAddOperations,
        CancellationToken cancellationToken)
    {
        if (audiobook.QualityProfile != null)
        {
            return false;
        }

        _logger.LogWarning(
            "Audiobook '{Title}' has no quality profile assigned",
            audiobook.Title);
        if (operation != null)
        {
            await quickAddOperations.AdvanceAsync(
                audiobook.Id,
                QuickAddOperationState.FailedNeedsAttention,
                failureReason: "No quality profile is assigned to this audiobook.",
                cancellationToken: cancellationToken);
        }

        return true;
    }

    private async Task<bool> VerifyBlankCutoffHasRegisteredFileAsync(
        Audiobook audiobook,
        QuickAddOperation? operation,
        bool cutoffMet,
        IAudiobookFileRepository fileRepository,
        IAudiobookScanService audiobookScanService,
        CancellationToken cancellationToken)
    {
        if (!cutoffMet
            || !string.IsNullOrWhiteSpace(audiobook.QualityProfile?.CutoffQuality)
            || operation?.State == QuickAddOperationState.NoMatchingReleases)
        {
            return cutoffMet;
        }

        return await HasVerifiedRegisteredFileAtCutoffAsync(
            audiobook,
            fileRepository,
            audiobookScanService,
            cancellationToken);
    }

    private async Task<bool> HasVerifiedRegisteredFileAtCutoffAsync(
        Audiobook audiobook,
        IAudiobookFileRepository fileRepository,
        IAudiobookScanService audiobookScanService,
        CancellationToken cancellationToken)
    {
        var profile = audiobook.QualityProfile;
        if (profile == null)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(profile.CutoffQuality)
            && profile.Qualities?.Any(quality =>
                quality.Quality == profile.CutoffQuality) != true)
        {
            return false;
        }

        var registeredFiles = await fileRepository.GetByAudiobookIdAsync(
            audiobook.Id,
            cancellationToken);
        var allFiles = await fileRepository.GetAllAsync(cancellationToken);
        foreach (var file in registeredFiles)
        {
            if (file.PathIdentityState != PathIdentityState.Valid
                || string.IsNullOrWhiteSpace(file.PathOwnershipKey)
                || string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)
                || allFiles.Count(candidate =>
                    string.Equals(
                        candidate.PathOwnershipKey,
                        file.PathOwnershipKey,
                        StringComparison.Ordinal)) != 1
                || !QualityMatcher.MeetsCutoff(
                    new AudioQualityInput
                    {
                        Codec = file.Codec,
                        Container = file.Container,
                        Format = file.Format,
                        BitrateBitsPerSecond = file.Bitrate,
                        Path = file.Path
                    },
                    profile))
            {
                continue;
            }

            var path = file.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!Path.IsPathRooted(path))
            {
                if (string.IsNullOrWhiteSpace(audiobook.BasePath))
                {
                    continue;
                }

                path = Path.Combine(audiobook.BasePath, path);
            }

            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 0
                    && !string.IsNullOrWhiteSpace(audiobook.BasePath)
                    && await audiobookScanService.RegisterExistingFileAsync(
                        audiobook.Id,
                        audiobook.BasePath,
                        path,
                        "automatic-search-availability",
                        cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogDebug(
                    exception,
                    "Registered file for audiobook {AudiobookId} could not be verified as readable",
                    audiobook.Id);
            }
        }

        return false;
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
