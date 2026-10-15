namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookDeletionIntentStore
{
    Task<AudiobookDeletionIntent> GetOrCreateAsync(
        int audiobookId,
        bool deleteFolder,
        CancellationToken cancellationToken = default);

    Task MarkFilesystemCleanupCompletedAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);

    Task RecordErrorAsync(
        Guid intentId,
        string error,
        CancellationToken cancellationToken = default);

    Task MarkNeedsAttentionAsync(
        Guid intentId,
        string error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a Planned or NeedsAttention intent whose audiobook row no longer
    /// exists, without performing any filesystem mutation. Returns false and
    /// changes nothing when the audiobook row still exists or the intent is not
    /// in a state that can be closed this way.
    /// </summary>
    Task<bool> CompleteOrphanedWithoutFilesystemCleanupAsync(
        Guid intentId,
        string note,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookDeletionIntent>> GetActiveAsync(
        CancellationToken cancellationToken = default);
}
