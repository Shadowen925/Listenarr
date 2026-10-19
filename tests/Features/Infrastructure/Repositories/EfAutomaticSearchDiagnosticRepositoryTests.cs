using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Name", nameof(EfAutomaticSearchDiagnosticRepositoryTests))]
[Trait("Category", "Infrastructure")]
public sealed class EfAutomaticSearchDiagnosticRepositoryTests : BaseTests
{
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public ListenArrDbContext Db { get; }

        public TestDb()
        {
            _connection.Open();
            Db = new ListenArrDbContext(
                new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseSqlite(_connection)
                    .Options);
            Db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    [Fact]
    public async Task AddAsync_PersistsSafeSearchHistoryAndBoundsCollections()
    {
        using var context = new TestDb();
        var repository = new EfAutomaticSearchDiagnosticRepository(context.Db);
        var diagnostic = new AutomaticSearchDiagnostic
        {
            AudiobookId = 7,
            SearchTrigger = AutomaticSearchTrigger.Replacement.ToString(),
            IndexersQueried = Enumerable.Range(0, 105).Select(index => $"Indexer {index}").ToList(),
            ResultsReturned = 3,
            RejectionReasons = Enumerable.Range(0, 105).Select(index => $"Reason {index}").ToList(),
            SubmissionAttempts = Enumerable.Range(0, 105)
                .Select(index => new AutomaticSearchSubmissionAttempt(
                    $"Release {index}",
                    "sabnzbd",
                    "Failed",
                    "ClientUnavailable"))
                .ToList(),
            SelectedRelease = "Release 0",
            DownloadClient = "sabnzbd",
            FinalOutcome = "AllSubmissionsFailed",
            NextRetryAt = DateTime.UtcNow.AddHours(1)
        };

        await repository.AddAsync(diagnostic);
        var history = await repository.GetRecentAsync(7, 25);

        var item = Assert.Single(history);
        Assert.Equal("Replacement", item.SearchTrigger);
        Assert.Equal(3, item.ResultsReturned);
        Assert.Equal(100, item.IndexersQueried.Count);
        Assert.Equal(100, item.RejectionReasons.Count);
        Assert.Equal(100, item.SubmissionAttempts.Count);
        Assert.Equal("Release 0", item.SelectedRelease);
        Assert.Equal("AllSubmissionsFailed", item.FinalOutcome);
    }

    [Fact]
    public async Task AddAsync_PrunesHistoryOlderThanRetentionWindow()
    {
        using var context = new TestDb();
        context.Db.AutomaticSearchDiagnostics.Add(new AutomaticSearchDiagnostic
        {
            AudiobookId = 12,
            SearchTimestamp = DateTime.UtcNow - EfAutomaticSearchDiagnosticRepository.MaximumAge - TimeSpan.FromMinutes(1),
            FinalOutcome = "NoResults"
        });
        await context.Db.SaveChangesAsync();

        var repository = new EfAutomaticSearchDiagnosticRepository(context.Db);
        await repository.AddAsync(new AutomaticSearchDiagnostic
        {
            AudiobookId = 12,
            FinalOutcome = "NoResults"
        });

        var history = await repository.GetRecentAsync(12, 25);
        var item = Assert.Single(history);
        Assert.True(item.SearchTimestamp > DateTime.UtcNow - EfAutomaticSearchDiagnosticRepository.MaximumAge);
    }
}
