using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public partial class AudiobookRepository
    {
        public async Task<SeriesMembershipMergeOutcome> MergeSeriesMembershipsAsync(
            IReadOnlyCollection<int> membershipIds,
            string canonicalSeriesAsin,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(membershipIds);
            var normalizedAsin = NormalizeAsin(canonicalSeriesAsin);
            if (normalizedAsin.Length == 0)
            {
                throw new ArgumentException("A canonical series ASIN is required.", nameof(canonicalSeriesAsin));
            }

            var ids = membershipIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length == 0)
            {
                throw new ArgumentException("At least one series membership is required.", nameof(membershipIds));
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var sourceMemberships = await _db.AudiobookSeriesMemberships
                    .Where(membership => ids.Contains(membership.Id))
                    .ToListAsync(ct);
                if (sourceMemberships.Count != ids.Length)
                {
                    throw new InvalidOperationException(
                        "One or more series memberships changed after the preview. Refresh the preview and try again.");
                }

                if (sourceMemberships.Any(membership =>
                        !string.IsNullOrWhiteSpace(membership.SeriesAsin)
                        && NormalizeAsin(membership.SeriesAsin) != normalizedAsin))
                {
                    throw new InvalidOperationException(
                        "A selected membership is already assigned to a different Audible series.");
                }

                var audiobookIds = sourceMemberships
                    .Select(membership => membership.AudiobookId)
                    .Distinct()
                    .ToArray();
                var canonicalMemberships = await _db.AudiobookSeriesMemberships
                    .Where(membership =>
                        audiobookIds.Contains(membership.AudiobookId)
                        && membership.SeriesAsin != null
                        && membership.SeriesAsin.ToUpper() == normalizedAsin)
                    .ToListAsync(ct);

                var transferredCount = 0;
                var deduplicatedCount = 0;
                var affectedBooks = 0;
                foreach (var audiobookId in audiobookIds)
                {
                    var sourceRows = sourceMemberships
                        .Where(membership => membership.AudiobookId == audiobookId)
                        .ToList();
                    var canonicalRows = canonicalMemberships
                        .Where(membership => membership.AudiobookId == audiobookId)
                        .ToList();
                    var combinedRows = sourceRows
                        .Concat(canonicalRows)
                        .DistinctBy(membership => membership.Id)
                        .ToList();
                    if (combinedRows.Count == 0)
                    {
                        continue;
                    }

                    var positions = combinedRows
                        .Select(membership => membership.SeriesNumber?.Trim())
                        .Where(position => !string.IsNullOrWhiteSpace(position))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (positions.Count > 1)
                    {
                        throw new InvalidOperationException(
                            $"Audiobook {audiobookId} has conflicting positions for the same Audible series.");
                    }

                    var audiobookshelfIds = combinedRows
                        .Select(membership => membership.AudiobookshelfSeriesId?.Trim())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (audiobookshelfIds.Count > 1)
                    {
                        throw new InvalidOperationException(
                            $"Audiobook {audiobookId} has conflicting Audiobookshelf source series IDs.");
                    }

                    var survivor = canonicalRows.FirstOrDefault() ?? sourceRows[0];
                    survivor.SeriesAsin = normalizedAsin;
                    survivor.SeriesName ??= sourceRows
                        .Select(membership => membership.SeriesName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
                    survivor.SeriesNumber ??= positions.FirstOrDefault();
                    survivor.AudiobookshelfSeriesId ??= audiobookshelfIds.FirstOrDefault();
                    survivor.IsPrimary = combinedRows.Any(membership => membership.IsPrimary);
                    survivor.SortOrder = combinedRows.Min(membership => membership.SortOrder);

                    transferredCount += sourceRows.Count(membership =>
                        string.IsNullOrWhiteSpace(membership.SeriesAsin));
                    var duplicates = combinedRows.Where(membership => membership.Id != survivor.Id).ToList();
                    deduplicatedCount += duplicates.Count;
                    _db.AudiobookSeriesMemberships.RemoveRange(duplicates);
                    affectedBooks++;
                }

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new SeriesMembershipMergeOutcome(
                    transferredCount,
                    deduplicatedCount,
                    affectedBooks);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
    }
}
