namespace Listenarr.Application.Downloads.Contracts.Repositories
{
    public interface IReleaseRejectionRepository
    {
        /// <summary>Adds the rejection unless the same key already exists for the audiobook. Returns true when added.</summary>
        Task<bool> AddAsync(ReleaseRejection rejection, CancellationToken ct = default);

        Task<List<ReleaseRejection>> ListAsync(int? audiobookId, int limit = 500, CancellationToken ct = default);

        Task<bool> RemoveAsync(int id, CancellationToken ct = default);

        Task<int> ClearAsync(int audiobookId, CancellationToken ct = default);
    }

    public interface IDownloadEventRepository
    {
        /// <summary>Appends the event and prunes the table to the retention limits.</summary>
        Task AddAsync(DownloadEvent downloadEvent, CancellationToken ct = default);

        Task<List<DownloadEvent>> GetRecentAsync(int limit, string? downloadId = null, CancellationToken ct = default);
    }
}
