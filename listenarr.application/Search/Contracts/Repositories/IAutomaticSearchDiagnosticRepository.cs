namespace Listenarr.Application.Search.Contracts.Repositories;

public interface IAutomaticSearchDiagnosticRepository
{
    Task AddAsync(AutomaticSearchDiagnostic diagnostic, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomaticSearchDiagnostic>> GetRecentAsync(
        int audiobookId,
        int limit,
        CancellationToken cancellationToken = default);
}
