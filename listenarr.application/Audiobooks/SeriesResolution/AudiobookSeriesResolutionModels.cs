namespace Listenarr.Application.Audiobooks.SeriesResolution;

public interface IAudiobookSeriesResolutionService
{
    Task<IReadOnlyList<SeriesResolutionPreview>> PreviewAsync(
        string? seriesId,
        string region,
        CancellationToken cancellationToken = default);

    Task<SeriesResolutionMergeResult> MergeAsync(
        string seriesId,
        string candidateAsin,
        string region,
        CancellationToken cancellationToken = default);
}

/// <summary>Preview of Audible catalog matches for one imported audiobook series.</summary>
public sealed record SeriesResolutionPreview(
    string SeriesId,
    string ImportedSeriesName,
    int ImportedBookCount,
    IReadOnlyList<string> BooksWithoutAsin,
    IReadOnlyList<string> UnresolvedBooks,
    IReadOnlyList<string> Conflicts,
    bool IsAmbiguous,
    IReadOnlyList<SeriesResolutionCandidate> Candidates);

/// <summary>A catalog-series candidate and the evidence supporting its match.</summary>
public sealed record SeriesResolutionCandidate(
    string Asin,
    string Name,
    string Confidence,
    int Score,
    int ExistingLibraryBookCount,
    int ProposedMergedBookCount,
    int OverlappingBookCount,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Conflicts);

/// <summary>Summary of membership changes after a confirmed series merge.</summary>
public sealed record SeriesResolutionMergeResult(
    string SeriesId,
    string CanonicalSeriesAsin,
    string CanonicalSeriesName,
    int TransferredMembershipCount,
    int DeduplicatedMembershipCount,
    int MergedBookCount,
    string? CatalogWarning);
