using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfReleaseRejectionRepository(ListenArrDbContext db) : IReleaseRejectionRepository
    {
        public async Task<bool> AddAsync(ReleaseRejection rejection, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rejection.ReleaseKey))
            {
                return false;
            }

            var exists = await db.ReleaseRejections.AnyAsync(
                r => r.AudiobookId == rejection.AudiobookId && r.ReleaseKey == rejection.ReleaseKey, ct);
            if (exists)
            {
                return false;
            }

            db.ReleaseRejections.Add(rejection);
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException)
            {
                db.Entry(rejection).State = EntityState.Detached;
                if (await db.ReleaseRejections.AnyAsync(
                        r => r.AudiobookId == rejection.AudiobookId && r.ReleaseKey == rejection.ReleaseKey, ct))
                {
                    return false;
                }

                throw;
            }
        }

        public Task<List<ReleaseRejection>> ListAsync(int? audiobookId, int limit = 500, CancellationToken ct = default)
        {
            var query = db.ReleaseRejections.AsNoTracking();
            if (audiobookId is int id)
            {
                query = query.Where(r => r.AudiobookId == id);
            }

            return query.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit, 1, 2000)).ToListAsync(ct);
        }

        public async Task<bool> RemoveAsync(int id, CancellationToken ct = default)
            => await db.ReleaseRejections.Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0;

        public Task<int> ClearAsync(int audiobookId, CancellationToken ct = default)
            => db.ReleaseRejections.Where(r => r.AudiobookId == audiobookId).ExecuteDeleteAsync(ct);
    }

    public class EfDownloadEventRepository(ListenArrDbContext db) : IDownloadEventRepository
    {
        public const int MaxEvents = 5000;
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

        public async Task AddAsync(DownloadEvent downloadEvent, CancellationToken ct = default)
        {
            db.DownloadEvents.Add(downloadEvent);
            await db.SaveChangesAsync(ct);
            await PruneAsync(ct);
        }

        public Task<List<DownloadEvent>> GetRecentAsync(int limit, string? downloadId = null, CancellationToken ct = default)
        {
            var query = db.DownloadEvents.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(downloadId))
            {
                query = query.Where(e => e.DownloadId == downloadId);
            }

            return query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
                .Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);
        }

        private async Task PruneAsync(CancellationToken ct)
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            await db.DownloadEvents.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct);

            var count = await db.DownloadEvents.CountAsync(ct);
            if (count > MaxEvents)
            {
                var keepFrom = await db.DownloadEvents.OrderByDescending(e => e.Id).Skip(MaxEvents - 1).Select(e => e.Id).FirstAsync(ct);
                await db.DownloadEvents.Where(e => e.Id < keepFrom).ExecuteDeleteAsync(ct);
            }
        }
    }
}
