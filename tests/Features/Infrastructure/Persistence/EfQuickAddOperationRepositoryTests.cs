using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "EfQuickAddOperationRepositoryTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfQuickAddOperationRepositoryTests : BaseTests
{
    [Fact]
    public async Task AdvanceAsync_IsDurableAndRejectsStaleStateRegression()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new ListenArrDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.QuickAddOperations.Add(NewOperation(1, "durable"));
            await db.SaveChangesAsync();

            var repository = new EfQuickAddOperationRepository(db);
            Assert.True(await repository.AdvanceAsync(
                1,
                QuickAddOperationState.Queued,
                downloadId: "download-1"));
            Assert.False(await repository.AdvanceAsync(
                1,
                QuickAddOperationState.Searching));
            Assert.True(await repository.AdvanceAsync(
                1,
                QuickAddOperationState.ImportPending));
            Assert.False(await repository.AdvanceAsync(
                1,
                QuickAddOperationState.DownloadCompleted));
        }

        await using var reload = new ListenArrDbContext(options);
        var persisted = await reload.QuickAddOperations.SingleAsync();
        Assert.Equal(QuickAddOperationState.ImportPending, persisted.State);
        Assert.Equal("download-1", persisted.DownloadId);
    }

    [Fact]
    public async Task AdvanceAsync_FailedStateUsesRelationallyTranslatablePredicate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var searchOperation = NewOperation(1, "failure-state");
        var importOperation = NewOperation(2, "import-failure-state");
        importOperation.State = QuickAddOperationState.ImportPending;
        db.QuickAddOperations.AddRange(searchOperation, importOperation);
        await db.SaveChangesAsync();

        var repository = new EfQuickAddOperationRepository(db);

        Assert.True(await repository.AdvanceAsync(
            1,
            QuickAddOperationState.FailedNeedsAttention,
            failureReason: "Search failed"));
        Assert.False(await repository.AdvanceAsync(
            1,
            QuickAddOperationState.Searching));
        Assert.False(await repository.AdvanceAsync(
            2,
            QuickAddOperationState.FailedNeedsAttention,
            failureReason: "Import failed"));
        Assert.True(await repository.AdvanceAsync(
            2,
            QuickAddOperationState.FailedNeedsAttention,
            processingJobId: "job-2",
            failureReason: "Import failed"));
    }

    [Fact]
    public async Task GetUnresolvedAsync_ReturnsOldOperationsBeyondActivityHistoryLimits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.QuickAddOperations.AddRange(
            Enumerable.Range(1, 250)
                .Select(id => NewOperation(id, $"operation-{id}", updatedAt: DateTime.UnixEpoch)));
        await db.SaveChangesAsync();

        var operations = await new EfQuickAddOperationRepository(db).GetUnresolvedAsync();

        Assert.Equal(250, operations.Count);
        Assert.All(operations, operation => Assert.Equal(DateTime.UnixEpoch, operation.UpdatedAt));
    }

    [Fact]
    public async Task RetryAsync_ResetsSearchFailureAndMakesSearchEligible()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Retry Search",
            LastSearchTime = DateTime.UtcNow
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        var operation = NewOperation(audiobook.Id, "retry-search");
        operation.State = QuickAddOperationState.NoMatchingReleases;
        db.QuickAddOperations.Add(operation);
        await db.SaveChangesAsync();

        Assert.True(await new EfQuickAddOperationRepository(db).RetryAsync(operation.Id));

        var persisted = await db.QuickAddOperations.SingleAsync();
        var persistedAudiobook = await db.Audiobooks.SingleAsync();
        Assert.Equal(QuickAddOperationState.SearchPending, persisted.State);
        Assert.Equal(1, persisted.Attempt);
        Assert.Null(persisted.FailureReason);
        Assert.Null(persistedAudiobook.LastSearchTime);
    }

    [Fact]
    public async Task RetryAsync_RequeuesFailedImportWithoutStartingAnotherSearch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook { Title = "Retry Import" };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        var operation = NewOperation(audiobook.Id, "retry-import");
        operation.State = QuickAddOperationState.FailedNeedsAttention;
        operation.DownloadId = "download-1";
        operation.ProcessingJobId = "job-1";
        db.QuickAddOperations.Add(operation);
        db.Downloads.Add(new Download
        {
            Id = "download-1",
            AudiobookId = audiobook.Id,
            QuickAddOperationId = operation.Id,
            DownloadClientId = "deluge",
            Status = DownloadStatus.ImportBlocked
        });
        db.DownloadProcessingJobs.Add(new DownloadProcessingJob
        {
            Id = "job-1",
            DownloadId = "download-1",
            QuickAddOperationId = operation.Id,
            Status = ProcessingJobStatus.Failed,
            CompletedAt = DateTime.UtcNow,
            ErrorMessage = "Previous import failed"
        });
        await db.SaveChangesAsync();

        Assert.True(await new EfQuickAddOperationRepository(db).RetryAsync(operation.Id));

        var persistedOperation = await db.QuickAddOperations.SingleAsync();
        var persistedDownload = await db.Downloads.SingleAsync();
        var persistedJob = await db.DownloadProcessingJobs.SingleAsync();
        Assert.Equal(QuickAddOperationState.ImportPending, persistedOperation.State);
        Assert.Equal(ProcessingJobStatus.Pending, persistedJob.Status);
        Assert.Null(persistedJob.ErrorMessage);
        Assert.Null(persistedJob.CompletedAt);
        Assert.Equal(DownloadStatus.ImportPending, persistedDownload.Status);
    }

    private static QuickAddOperation NewOperation(
        int audiobookId,
        string requestKey,
        DateTime? updatedAt = null) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            RequestKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(requestKey))),
            AudiobookId = audiobookId,
            AudiobookTitle = requestKey,
            State = QuickAddOperationState.SearchPending,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = updatedAt ?? DateTime.UtcNow
        };
}
