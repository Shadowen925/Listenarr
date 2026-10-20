namespace Listenarr.Domain.Search;

public enum AutomaticSearchTrigger
{
    Manual,
    Immediate,
    Periodic,
    Replacement
}

public sealed class AutomaticSearchDiagnostic
{
    public int Id { get; set; }
    public int AudiobookId { get; set; }
    public DateTime SearchTimestamp { get; set; } = DateTime.UtcNow;
    public string SearchTrigger { get; set; } = "Periodic";
    public List<string> QueriesAttempted { get; set; } = [];
    public List<string> IndexersQueried { get; set; } = [];
    public List<AutomaticSearchIndexerOutcome> IndexerOutcomes { get; set; } = [];
    public int ResultsReturned { get; set; }
    public int DistinctCandidateCount { get; set; }
    public int IdentityMatchCount { get; set; }
    public int QualityRejectedCount { get; set; }
    public List<string> RejectionReasons { get; set; } = [];
    public List<AutomaticSearchSubmissionAttempt> SubmissionAttempts { get; set; } = [];
    public string? SelectedRelease { get; set; }
    public string? DownloadClient { get; set; }
    public string FinalOutcome { get; set; } = string.Empty;
    public DateTime? NextRetryAt { get; set; }
}

public sealed record AutomaticSearchIndexerOutcome(
    string Query,
    string IndexerName,
    string Outcome,
    int ResultsReturned,
    string? FailureReason);

public sealed record AutomaticSearchSubmissionAttempt(
    string ReleaseTitle,
    string? ClientType,
    string Outcome,
    string? FailureReason);

public sealed record AutomaticSearchDiagnosticHistoryItem(
    int Id,
    int AudiobookId,
    DateTime SearchTimestamp,
    string SearchTrigger,
    IReadOnlyList<string> QueriesAttempted,
    IReadOnlyList<string> IndexersQueried,
    int ResultsReturned,
    int DistinctCandidateCount,
    int IdentityMatchCount,
    int QualityRejectedCount,
    IReadOnlyList<AutomaticSearchIndexerOutcome> IndexerOutcomes,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<AutomaticSearchSubmissionAttempt> SubmissionAttempts,
    string? SelectedRelease,
    string? DownloadClient,
    string FinalOutcome,
    DateTime? NextRetryAt);
