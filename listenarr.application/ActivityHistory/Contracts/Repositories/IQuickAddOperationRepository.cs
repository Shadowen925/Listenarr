namespace Listenarr.Application.ActivityHistory.Contracts.Repositories;

public interface IQuickAddOperationRepository
{
    Task<QuickAddOperation?> GetByIdAsync(string operationId, CancellationToken cancellationToken = default);
    Task<QuickAddOperation?> GetByRequestKeyAsync(string requestKey, CancellationToken cancellationToken = default);
    Task<QuickAddOperation?> GetByAudiobookIdAsync(int audiobookId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuickAddOperation>> GetUnresolvedAsync(CancellationToken cancellationToken = default);
    Task<bool> AdvanceAsync(
        int audiobookId,
        QuickAddOperationState state,
        string? selectedRelease = null,
        string? downloadClientId = null,
        string? downloadId = null,
        string? processingJobId = null,
        string? failureReason = null,
        CancellationToken cancellationToken = default);
    Task<bool> RetryAsync(string operationId, CancellationToken cancellationToken = default);
    Task LinkDiagnosticAsync(string operationId, int diagnosticId, CancellationToken cancellationToken = default);
}
