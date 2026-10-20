using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed class EfAutomaticSearchDiagnosticRepository(ListenArrDbContext db)
    : IAutomaticSearchDiagnosticRepository
{
    public const int MaximumRecords = 5000;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(90);

    public async Task AddAsync(
        AutomaticSearchDiagnostic diagnostic,
        CancellationToken cancellationToken = default)
    {
        diagnostic.QueriesAttempted = diagnostic.QueriesAttempted.Take(100).ToList();
        diagnostic.IndexersQueried = diagnostic.IndexersQueried.Take(100).ToList();
        diagnostic.IndexerOutcomes = diagnostic.IndexerOutcomes.Take(500).ToList();
        diagnostic.RejectionReasons = diagnostic.RejectionReasons.Take(100).ToList();
        diagnostic.SubmissionAttempts = diagnostic.SubmissionAttempts.Take(100).ToList();
        db.AutomaticSearchDiagnostics.Add(diagnostic);
        await db.SaveChangesAsync(cancellationToken);

        var cutoff = DateTime.UtcNow - MaximumAge;
        await db.AutomaticSearchDiagnostics
            .Where(record => record.SearchTimestamp < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var count = await db.AutomaticSearchDiagnostics.CountAsync(cancellationToken);
        if (count > MaximumRecords)
        {
            var firstIdToKeep = await db.AutomaticSearchDiagnostics
                .OrderByDescending(record => record.Id)
                .Skip(MaximumRecords - 1)
                .Select(record => record.Id)
                .FirstAsync(cancellationToken);
            await db.AutomaticSearchDiagnostics
                .Where(record => record.Id < firstIdToKeep)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AutomaticSearchDiagnostic>> GetRecentAsync(
        int audiobookId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await db.AutomaticSearchDiagnostics
            .AsNoTracking()
            .Where(record => record.AudiobookId == audiobookId)
            .OrderByDescending(record => record.SearchTimestamp)
            .ThenByDescending(record => record.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
}
