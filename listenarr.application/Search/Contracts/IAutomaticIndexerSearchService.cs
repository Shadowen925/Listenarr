namespace Listenarr.Application.Search.Contracts;

public sealed record AutomaticIndexerQueryOutcome(
    string Query,
    string IndexerName,
    string Status,
    int ResultsReturned,
    string? FailureReason);

public sealed record AutomaticIndexerSearchResponse(
    IReadOnlyList<SearchResult> Results,
    IReadOnlyList<AutomaticIndexerQueryOutcome> IndexerOutcomes);

public sealed record IndexerSearchBatch(
    IReadOnlyList<IndexerSearchResult> Results,
    IReadOnlyList<AutomaticIndexerQueryOutcome> IndexerOutcomes);

public interface IAutomaticIndexerSearchService
{
    Task<AutomaticIndexerSearchResponse> SearchAutomaticQueryAsync(string query);
}

public sealed class UnsupportedIndexerProviderException(string implementation)
    : InvalidOperationException($"No search provider is registered for indexer type {implementation}.")
{
    public string Implementation { get; } = implementation;
}
