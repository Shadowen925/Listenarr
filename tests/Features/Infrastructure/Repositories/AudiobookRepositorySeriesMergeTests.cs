using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Common;
using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Name", "AudiobookRepositorySeriesMergeTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookRepositorySeriesMergeTests : BaseTests
{
    private const string CanonicalAsin = "SERIES123";

    [Fact]
    public async Task MergeSeriesMembershipsAsync_TransfersAndDeduplicatesWithoutChangingBookOrOtherMemberships()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Imported title",
            Asin = "BOOK123",
            AudiobookshelfItemId = "abs-book-1",
            AudiobookshelfMetadataAuthoritative = true,
            Monitored = false,
            FilePath = "C:\\audiobooks\\book.m4b",
            SeriesMemberships =
            [
                new AudiobookSeriesMembership
                {
                    SeriesName = "Dungeon Crawler Carl",
                    SeriesNumber = "8",
                    AudiobookshelfSeriesId = "abs-series-1",
                    IsPrimary = true
                },
                new AudiobookSeriesMembership
                {
                    SeriesName = "Canonical name",
                    SeriesNumber = "8",
                    SeriesAsin = CanonicalAsin
                },
                new AudiobookSeriesMembership
                {
                    SeriesName = "Separate legitimate series",
                    SeriesNumber = "2",
                    SeriesAsin = "OTHER456"
                }
            ]
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        var sourceMembershipId = audiobook.SeriesMemberships![0].Id;

        var result = await new AudiobookRepository(db).MergeSeriesMembershipsAsync(
            [sourceMembershipId],
            CanonicalAsin);

        Assert.Equal(new SeriesMembershipMergeOutcome(1, 1, 1), result);
        db.ChangeTracker.Clear();
        var savedBook = await db.Audiobooks
            .Include(book => book.SeriesMemberships)
            .SingleAsync();
        Assert.Equal("Imported title", savedBook.Title);
        Assert.Equal("BOOK123", savedBook.Asin);
        Assert.Equal("abs-book-1", savedBook.AudiobookshelfItemId);
        Assert.True(savedBook.AudiobookshelfMetadataAuthoritative);
        Assert.False(savedBook.Monitored);
        Assert.Equal("C:\\audiobooks\\book.m4b", savedBook.FilePath);
        Assert.Equal(2, savedBook.SeriesMemberships!.Count);

        var canonicalMembership = Assert.Single(savedBook.SeriesMemberships, membership =>
            membership.SeriesAsin == CanonicalAsin);
        Assert.Equal("8", canonicalMembership.SeriesNumber);
        Assert.Equal("abs-series-1", canonicalMembership.AudiobookshelfSeriesId);
        Assert.Contains(savedBook.SeriesMemberships, membership => membership.SeriesAsin == "OTHER456");
    }

    [Fact]
    public async Task MergeSeriesMembershipsAsync_RollsBackWhenPositionsConflict()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Conflict book",
            SeriesMemberships =
            [
                new AudiobookSeriesMembership
                {
                    SeriesName = "Imported series",
                    SeriesNumber = "2",
                    AudiobookshelfSeriesId = "abs-series-1"
                },
                new AudiobookSeriesMembership
                {
                    SeriesName = "Canonical series",
                    SeriesNumber = "1",
                    SeriesAsin = CanonicalAsin
                }
            ]
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        var sourceMembershipId = audiobook.SeriesMemberships![0].Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AudiobookRepository(db).MergeSeriesMembershipsAsync(
                [sourceMembershipId],
                CanonicalAsin));

        db.ChangeTracker.Clear();
        var memberships = await db.AudiobookSeriesMemberships.OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, memberships.Count);
        Assert.Null(memberships[0].SeriesAsin);
        Assert.Equal("2", memberships[0].SeriesNumber);
        Assert.Equal(CanonicalAsin, memberships[1].SeriesAsin);
        Assert.Equal("1", memberships[1].SeriesNumber);
    }
}
