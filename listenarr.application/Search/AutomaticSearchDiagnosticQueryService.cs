namespace Listenarr.Application.Search;

public interface IAutomaticSearchDiagnosticQueryService
{
    Task<IReadOnlyList<AutomaticSearchDiagnosticHistoryItem>?> GetRecentAsync(
        int audiobookId,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class AutomaticSearchDiagnosticQueryService(
    IAudiobookRepository audiobookRepository,
    IAutomaticSearchDiagnosticRepository diagnosticRepository) : IAutomaticSearchDiagnosticQueryService
{
    public async Task<IReadOnlyList<AutomaticSearchDiagnosticHistoryItem>?> GetRecentAsync(
        int audiobookId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            return null;
        }

        if (await audiobookRepository.GetByIdAsync(audiobookId) is null)
        {
            return null;
        }

        var diagnostics = await diagnosticRepository.GetRecentAsync(
            audiobookId,
            Math.Clamp(limit, 1, 100),
            cancellationToken);

        return diagnostics
            .Select(item => new AutomaticSearchDiagnosticHistoryItem(
                item.Id,
                item.AudiobookId,
                item.SearchTimestamp,
                item.SearchTrigger,
                item.QueriesAttempted,
                item.IndexersQueried,
                item.ResultsReturned,
                item.DistinctCandidateCount,
                item.IdentityMatchCount,
                item.QualityRejectedCount,
                item.IndexerOutcomes,
                item.RejectionReasons,
                item.SubmissionAttempts,
                item.SelectedRelease,
                item.DownloadClient,
                item.FinalOutcome,
                item.NextRetryAt))
            .ToArray();
    }
}
