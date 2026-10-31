using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search;

public partial class AutomaticSearchProcessor
{
    private sealed record AutomaticSearchQueryCollection(
        List<SearchResult> Results,
        List<string> QueriesAttempted,
        List<AutomaticSearchIndexerOutcome> IndexerOutcomes,
        int SuccessfulQueryCount,
        int RawResultCount,
        Exception? LastFailure);

    private async Task<AutomaticSearchQueryCollection> CollectSearchResultsAsync(
        ISearchService searchService,
        IReadOnlyList<string> searchQueries,
        int audiobookId,
        Audiobook? audiobook = null)
    {
        var results = new List<SearchResult>();
        var queriesAttempted = new List<string>();
        var indexerOutcomes = new List<AutomaticSearchIndexerOutcome>();
        var successfulQueryCount = 0;
        var rawResultCount = 0;
        Exception? lastFailure = null;

        foreach (var query in searchQueries)
        {
            queriesAttempted.Add(query);
            List<SearchResult> queryResults;
            try
            {
                if (searchService is IAutomaticIndexerSearchService automaticSearch)
                {
                    var response = await automaticSearch.SearchAutomaticQueryAsync(query);
                    queryResults = response.Results.ToList();
                    indexerOutcomes.AddRange(response.IndexerOutcomes.Select(outcome =>
                        new AutomaticSearchIndexerOutcome(
                            outcome.Query,
                            outcome.IndexerName,
                            outcome.Status,
                            outcome.ResultsReturned,
                            outcome.FailureReason)));

                    if (response.IndexerOutcomes.Count == 0
                        || response.IndexerOutcomes.Any(outcome => outcome.Status != "Failed"))
                    {
                        successfulQueryCount++;
                    }

                    rawResultCount += response.IndexerOutcomes.Count > 0
                        ? response.IndexerOutcomes.Sum(outcome => outcome.ResultsReturned)
                        : queryResults.Count;
                    if (response.IndexerOutcomes.Count > 0
                        && response.IndexerOutcomes.All(outcome => outcome.Status == "Failed"))
                    {
                        lastFailure = new InvalidOperationException(
                            "All configured indexers failed for this query.");
                        continue;
                    }
                }
                else
                {
                    queryResults = await searchService.SearchAsync(query, isAutomaticSearch: true);
                    successfulQueryCount++;
                    rawResultCount += queryResults.Count;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException
                && exception is not StackOverflowException)
            {
                lastFailure = exception;
                _logger.LogWarning(
                    "Automatic search query failed for audiobook {AudiobookId} ({FailureType}); trying remaining query variants",
                    audiobookId,
                    exception.GetType().Name);
                continue;
            }

            foreach (var result in queryResults)
            {
                if (!results.Any(existing => HaveSameRelease(existing, result)))
                {
                    results.Add(result);
                }
            }

            // Queries run broad-to-narrow; once one yields releases matching this book, the rest only
            // add indexer load (some indexers take ~7s per query).
            if (audiobook != null
                && queryResults.Any(result => Listenarr.Application.Search.Matching.AudiobookSearchResultMatcher.MatchesIdentity(audiobook, result)))
            {
                break;
            }
        }

        return new AutomaticSearchQueryCollection(
            results,
            queriesAttempted,
            indexerOutcomes,
            successfulQueryCount,
            rawResultCount,
            lastFailure);
    }
}
