using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    public class DownloadProcessingJobRepositoryTests : BaseTests
    {
        [Fact]
        public async Task RetryCount_ShouldPersist_AfterDbContextRecreation()
        {
            using var connection = new SqliteConnection("Filename=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var context = new ListenArrDbContext(options))
            {
                context.Database.EnsureCreated();
                var job = new DownloadProcessingJobBuilder()
                    .WithPending(at: DateTime.UtcNow)
                    .Build();

                context.DownloadProcessingJobs.Add(job);
                await context.SaveChangesAsync();

                job.RetryCount = 4;
                await context.SaveChangesAsync();
            }

            using (var context = new ListenArrDbContext(options))
            {
                var job = await context.DownloadProcessingJobs.FirstOrDefaultAsync();

                Assert.NotNull(job);
                Assert.Equal(4, job.RetryCount);
            }
        }

        [Fact]
        public async Task TryClaimNextPendingAsync_ClaimsJobOnlyOnceAcrossConcurrentWorkers()
        {
            var databasePath = Path.Combine(
                FileService.GetTempPath(),
                "processing-job-claim.db");
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var firstFactory = new TestDbContextFactory(options);
            var secondFactory = new TestDbContextFactory(options);
            await using (var context = await firstFactory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
                context.DownloadProcessingJobs.Add(new DownloadProcessingJobBuilder()
                    .WithPending(at: DateTime.UtcNow)
                    .Build());
                await context.SaveChangesAsync();
            }

            var firstRepository = new EfDownloadProcessingJobRepository(firstFactory);
            var secondRepository = new EfDownloadProcessingJobRepository(secondFactory);

            var claims = await Task.WhenAll(
                firstRepository.TryClaimNextPendingAsync(DateTime.UtcNow),
                secondRepository.TryClaimNextPendingAsync(DateTime.UtcNow));

            var claimed = Assert.Single(claims, job => job != null);
            Assert.Equal(ProcessingJobStatus.Processing, claimed!.Status);
            await using var verifyContext = await firstFactory.CreateDbContextAsync();
            var persisted = await verifyContext.DownloadProcessingJobs.SingleAsync();
            Assert.Equal(ProcessingJobStatus.Processing, persisted.Status);
        }

        private sealed class TestDbContextFactory(
            DbContextOptions<ListenArrDbContext> options)
            : IDbContextFactory<ListenArrDbContext>
        {
            public ListenArrDbContext CreateDbContext() => new(options);

            public Task<ListenArrDbContext> CreateDbContextAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }
    }
}
